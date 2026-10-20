using System.Runtime.CompilerServices;

namespace Petal.Engines.Imaging.Editing;

/// <summary>
/// Source pixels plus the blurred "base" planes local adjustments need. Built once for the
/// preview and once (at full resolution) for export; every radius is a fraction of the long
/// edge, so both apply the same look.
/// </summary>
internal sealed class PhotoFrame
{
    /// <summary>Long edge the preview is designed for; detail radii and grain size scale from it.</summary>
    public const float ReferenceLongEdge = 1600f;

    private readonly Lock _gate = new();
    private float[]? _baseLarge, _baseSmall, _dark, _dx2, _dy2;
    private float _airlight = -1;

    public PhotoFrame(int width, int height, byte[] bgra)
    {
        Width = width;
        Height = height;
        Pixels = bgra;
    }

    public int Width { get; }

    public int Height { get; }

    public byte[] Pixels { get; }

    public float LongEdge => Math.Max(Width, Height);

    /// <summary>Blurred source luma (≈1.2 % of the long edge): the base layer for clarity and highlights/shadows.</summary>
    public float[] BaseLarge
    {
        get { lock (_gate) return _baseLarge ??= BlurredLuma(BoxBlur.RadiusForSigma(0.012 * LongEdge)); }
    }

    /// <summary>Lightly blurred luma (≈1 px at preview size): the detail layer for sharpening and noise reduction.</summary>
    public float[] BaseSmall
    {
        get { lock (_gate) return _baseSmall ??= BlurredLuma(BoxBlur.RadiusForSigma(Math.Max(0.8, LongEdge / ReferenceLongEdge))); }
    }

    /// <summary>Blurred dark channel (min of R, G, B) used to estimate haze.</summary>
    public float[] Dark
    {
        get
        {
            lock (_gate)
            {
                if (_dark is null)
                {
                    var plane = new float[Width * Height];
                    var px = Pixels;
                    Parallel.For(0, Height, y =>
                    {
                        int i = y * Width, p = i * 4;
                        for (int x = 0; x < Width; x++, i++, p += 4)
                            plane[i] = Math.Min(px[p], Math.Min(px[p + 1], px[p + 2])) * (1f / 255f);
                    });
                    BoxBlur.Apply(plane, Width, Height, BoxBlur.RadiusForSigma(0.015 * LongEdge));
                    _dark = plane;
                }
                return _dark;
            }
        }
    }

    /// <summary>Haze colour: the dark channel's 99.9th percentile, kept bright.</summary>
    public float Airlight
    {
        get
        {
            var dark = Dark;
            lock (_gate)
            {
                if (_airlight < 0)
                {
                    var bins = new int[1024];
                    foreach (var v in dark)
                        bins[Math.Clamp((int)(v * 1023), 0, 1023)]++;
                    long target = (long)(dark.Length * 0.999), seen = 0;
                    int k = 0;
                    for (; k < 1023 && seen + bins[k] < target; k++)
                        seen += bins[k];
                    _airlight = Math.Clamp(k / 1023f + 0.05f, 0.7f, 1f);
                }
                return _airlight;
            }
        }
    }

    /// <summary>Squared horizontal / vertical distance from the centre, normalised so a corner is 1.</summary>
    public (float[] Dx2, float[] Dy2) VignetteAxes
    {
        get
        {
            lock (_gate)
            {
                if (_dx2 is null || _dy2 is null)
                {
                    _dx2 = Axis(Width);
                    _dy2 = Axis(Height);
                }
                return (_dx2, _dy2);
            }

            static float[] Axis(int n)
            {
                var a = new float[n];
                float half = n / 2f;
                for (int i = 0; i < n; i++)
                {
                    float d = (i + 0.5f - half) / half;
                    a[i] = d * d * 0.5f;
                }
                return a;
            }
        }
    }

    /// <summary>Computes the planes a render will need up front (in parallel), so the render itself is pure arithmetic.</summary>
    public void Prepare(RenderPlan plan)
    {
        var work = new List<Action>();
        if (plan.UsesBaseLarge) work.Add(() => _ = BaseLarge);
        if (plan.UsesBaseSmall) work.Add(() => _ = BaseSmall);
        if (plan.Dehaze) work.Add(() => _ = Airlight);
        if (plan.Vignette) work.Add(() => _ = VignetteAxes);
        Parallel.Invoke(work.ToArray());
    }

    public void PrepareAll() => Parallel.Invoke(() => _ = BaseLarge, () => _ = BaseSmall, () => _ = Airlight, () => _ = VignetteAxes);

    private float[] BlurredLuma(int radius)
    {
        var plane = new float[Width * Height];
        var px = Pixels;
        Parallel.For(0, Height, y =>
        {
            int i = y * Width, p = i * 4;
            for (int x = 0; x < Width; x++, i++, p += 4)
                plane[i] = Luma(px[p + 2], px[p + 1], px[p]);
        });
        BoxBlur.Apply(plane, Width, Height, radius);
        return plane;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Luma(int r, int g, int b) => (0.2126f * r + 0.7152f * g + 0.0722f * b) * (1f / 255f);
}

/// <summary>Per-render constants and lookup tables derived from <see cref="PhotoAdjustments"/>.</summary>
internal sealed class RenderPlan
{
    public const int ToneSize = 4096, EncodeSize = 4096, BaseSize = 1024, CurveSize = 1024;

    // Stage 1: per-channel decode × white balance × exposure (indexed by the source byte).
    public readonly float[] LutR = new float[256], LutG = new float[256], LutB = new float[256];

    public bool HighlightsShadows;
    public readonly float[] HsGain = new float[BaseSize];

    public bool Tone;
    public readonly float[] ToneGain = new float[ToneSize];

    /// <summary>Linear → sRGB-encoded, indexed by √linear (fine steps in the shadows); identical for every render.</summary>
    public static readonly float[] EncodeLut = BuildEncodeLut();

    private static readonly double[] DecodeTable = Enumerable.Range(0, 256).Select(i => Decode(i / 255.0)).ToArray();

    public bool Dehaze;
    public float DehazeOmega, HazeAdd, Airlight;

    /// <summary>1 / transmission for each dark-channel value (avoids a division per pixel).</summary>
    public readonly float[] DehazeInverse = new float[CurveSize];

    public bool Detail;
    public float Clarity, Sharpen, NoiseReduction;
    public readonly float[] NoiseCurve = new float[CurveSize];

    public bool Color;
    public float M00, M01, M02, M10, M11, M12, M20, M21, M22;
    public bool Matrix;
    public float Vibrance;

    public bool Vignette;
    public float VignetteAmount;
    public readonly float[] VignetteCurve = new float[CurveSize];

    public bool Grain;
    public float GrainAmount, GrainInvCell;

    public bool UsesBaseLarge => HighlightsShadows || (Detail && Clarity != 0);

    public bool UsesBaseSmall => Detail && (Sharpen != 0 || NoiseReduction != 0);

    public bool IsIdentity => !HighlightsShadows && !Tone && !Dehaze && !Detail && !Color && !Vignette && !Grain && _neutralStage1;

    private bool _neutralStage1;

    public static RenderPlan Create(PhotoAdjustments adjustments, float longEdge, float airlight)
    {
        var a = adjustments.Clamped();
        var plan = new RenderPlan();

        // White balance: warm/cool trades red against blue, tint trades green against magenta;
        // gains are normalised so overall brightness stays put.
        double t = a.Temperature / 100, m = a.Tint / 100;
        double rGain = Math.Pow(2, 0.35 * t), bGain = Math.Pow(2, -0.35 * t), gGain = Math.Pow(2, -0.25 * m);
        double norm = 1 / (0.2126 * rGain + 0.7152 * gGain + 0.0722 * bGain);
        double exposure = Math.Pow(2, a.Exposure);
        for (int i = 0; i < 256; i++)
        {
            double lin = DecodeTable[i];
            plan.LutR[i] = (float)(lin * rGain * norm * exposure);
            plan.LutG[i] = (float)(lin * gGain * norm * exposure);
            plan.LutB[i] = (float)(lin * bGain * norm * exposure);
        }
        plan._neutralStage1 = a.Temperature == 0 && a.Tint == 0 && a.Exposure == 0;

        // Highlights / shadows act on the blurred base, so local contrast survives.
        double sh = a.Shadows / 100, hi = a.Highlights / 100;
        plan.HighlightsShadows = sh != 0 || hi != 0;
        if (plan.HighlightsShadows)
        {
            for (int i = 0; i < BaseSize; i++)
            {
                double baseLinear = Math.Max(Decode((double)i / (BaseSize - 1)) * exposure, 1e-6);
                double b = Encode(baseLinear);
                double bc = Math.Clamp(b, 0, 1);
                double shadowsBump = 6.75 * bc * (1 - bc) * (1 - bc);  // peaks at 1/3
                double highlightsBump = 6.75 * bc * bc * (1 - bc);     // peaks at 2/3
                double f = b + 0.45 * (sh * shadowsBump + hi * highlightsBump) + (b > 1 ? hi * 0.6 * (b - 1) : 0);
                plan.HsGain[i] = (float)Math.Clamp(Decode(Math.Max(f, 0)) / baseLinear, 0, 16);
            }
        }

        double whites = a.Whites / 100, blacks = a.Blacks / 100, contrast = a.Contrast / 100, brightness = a.Brightness / 100;
        double gammaExponent = Math.Pow(2, -a.Gamma / 100);
        plan.Tone = whites != 0 || blacks != 0 || contrast != 0 || brightness != 0 || a.Gamma != 0;
        if (plan.Tone)
        {
            for (int i = 0; i < ToneSize; i++)
            {
                double u = (double)i / (ToneSize - 1);
                double y = Math.Max(u * u * 4, 1e-7); // sqrt-spaced over linear 0…4
                double e = Encode(y);
                double mapped = e > 1 ? ToneCurve(1) + (e - 1) : ToneCurve(e);
                plan.ToneGain[i] = (float)Math.Clamp(Decode(Math.Max(mapped, 0)) / y, 0, 64);
            }

            double ToneCurve(double v)
            {
                v += whites * 0.25 * v * v * v;
                v += blacks * 0.2 * (1 - v) * (1 - v) * (1 - v);
                v = Math.Clamp(v, 0, 1);
                v += 0.7 * contrast * (v - 0.5) * (1 - Math.Abs(2 * v - 1));
                v += 0.8 * brightness * v * (1 - v);
                v = Math.Clamp(v, 0, 1);
                return Math.Pow(v, gammaExponent);
            }
        }

        double dehaze = a.Dehaze / 100;
        plan.Dehaze = dehaze != 0;
        plan.Airlight = airlight;
        plan.DehazeOmega = (float)(Math.Max(dehaze, 0) * 0.75);
        plan.HazeAdd = (float)(Math.Max(-dehaze, 0) * 0.45);
        if (plan.DehazeOmega > 0)
            for (int i = 0; i < CurveSize; i++)
                plan.DehazeInverse[i] = 1f / MathF.Max(1f - plan.DehazeOmega * ((float)i / (CurveSize - 1)) / airlight, 0.33f);

        plan.Clarity = (float)(a.Clarity / 100);
        plan.Sharpen = (float)(a.Sharpness / 100 * 1.5);
        plan.NoiseReduction = (float)(a.NoiseReduction / 100);
        plan.Detail = plan.Clarity != 0 || plan.Sharpen != 0 || plan.NoiseReduction != 0;
        if (plan.NoiseReduction != 0)
        {
            // Small-amplitude detail is treated as noise and removed; edges (large differences) stay.
            const double sigma = 0.035;
            for (int i = 0; i < CurveSize; i++)
            {
                double d = (double)i / (CurveSize - 1) - 0.5;
                plan.NoiseCurve[i] = (float)(d * Math.Exp(-(d / sigma) * (d / sigma)));
            }
        }

        double saturation = 1 + a.Saturation / 100;
        double hue = a.Hue * Math.PI / 180;
        plan.Vibrance = (float)(a.Vibrance / 100);
        plan.Matrix = a.Saturation != 0 || a.Hue != 0;
        plan.Color = plan.Matrix || plan.Vibrance != 0;
        if (plan.Matrix)
        {
            // Hue rotation about the grey axis and saturation, as in the CSS filter matrices (luma-preserving).
            double c = Math.Cos(hue), s = Math.Sin(hue);
            var h = new double[,]
            {
                { 0.213 + c * 0.787 - s * 0.213, 0.715 - c * 0.715 - s * 0.715, 0.072 - c * 0.072 + s * 0.928 },
                { 0.213 - c * 0.213 + s * 0.143, 0.715 + c * 0.285 + s * 0.140, 0.072 - c * 0.072 - s * 0.283 },
                { 0.213 - c * 0.213 - s * 0.787, 0.715 - c * 0.715 + s * 0.715, 0.072 + c * 0.928 + s * 0.072 },
            };
            var sat = new double[,]
            {
                { 0.213 + 0.787 * saturation, 0.715 - 0.715 * saturation, 0.072 - 0.072 * saturation },
                { 0.213 - 0.213 * saturation, 0.715 + 0.285 * saturation, 0.072 - 0.072 * saturation },
                { 0.213 - 0.213 * saturation, 0.715 - 0.715 * saturation, 0.072 + 0.928 * saturation },
            };
            var mtx = new double[3, 3];
            for (int r = 0; r < 3; r++)
                for (int col = 0; col < 3; col++)
                    mtx[r, col] = h[r, 0] * sat[0, col] + h[r, 1] * sat[1, col] + h[r, 2] * sat[2, col];
            (plan.M00, plan.M01, plan.M02) = ((float)mtx[0, 0], (float)mtx[0, 1], (float)mtx[0, 2]);
            (plan.M10, plan.M11, plan.M12) = ((float)mtx[1, 0], (float)mtx[1, 1], (float)mtx[1, 2]);
            (plan.M20, plan.M21, plan.M22) = ((float)mtx[2, 0], (float)mtx[2, 1], (float)mtx[2, 2]);
        }

        double vignette = a.Vignette / 100;
        plan.Vignette = vignette != 0;
        plan.VignetteAmount = (float)vignette;
        if (plan.Vignette)
        {
            for (int i = 0; i < CurveSize; i++)
            {
                double r2 = (double)i / (CurveSize - 1);
                double fall = SmoothStep(0.12, 1.0, r2);
                plan.VignetteCurve[i] = (float)(1 - Math.Abs(vignette) * 0.85 * fall * fall);
            }
        }

        plan.GrainAmount = (float)(a.Grain / 100 * 0.12);
        plan.Grain = plan.GrainAmount != 0;
        // Grain cells are one preview pixel; at full resolution they span several pixels so the look matches.
        plan.GrainInvCell = 1f / Math.Max(1f, longEdge / PhotoFrame.ReferenceLongEdge);
        return plan;
    }

    private static float[] BuildEncodeLut()
    {
        var lut = new float[EncodeSize];
        for (int i = 0; i < EncodeSize; i++)
        {
            double u = (double)i / (EncodeSize - 1);
            lut[i] = (float)Encode(u * u);
        }
        return lut;
    }

    public static double Decode(double c) => c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);

    public static double Encode(double l) => l <= 0.0031308 ? l * 12.92 : 1.055 * Math.Pow(l, 1 / 2.4) - 0.055;

    private static double SmoothStep(double e0, double e1, double x)
    {
        double t = Math.Clamp((x - e0) / (e1 - e0), 0, 1);
        return t * t * (3 - 2 * t);
    }
}

/// <summary>The per-pixel adjustment kernel, shared by preview and export.</summary>
internal static unsafe class PhotoRenderer
{
    private const int RowsPerTask = 16;

    /// <summary>Tests use this to compare the scalar kernel with the AVX2 one.</summary>
    internal static bool ForceScalar { get; set; }

    /// <summary>
    /// Renders <paramref name="frame"/> into <paramref name="destination"/> (BGRA, may be the
    /// frame's own buffer for in-place export).
    /// </summary>
    public static void Render(PhotoFrame frame, RenderPlan plan, byte* destination, int destinationStride)
    {
        int w = frame.Width, h = frame.Height;
        frame.Prepare(plan);
        var baseLarge = plan.UsesBaseLarge ? frame.BaseLarge : null;
        var baseSmall = plan.UsesBaseSmall ? frame.BaseSmall : null;
        var dark = plan.Dehaze ? frame.Dark : null;
        var (dx2, dy2) = plan.Vignette ? frame.VignetteAxes : (null!, null!);
        nint dst = (nint)destination;

        Parallel.For(0, (h + RowsPerTask - 1) / RowsPerTask, block =>
        {
            fixed (byte* src = frame.Pixels)
            fixed (float* lutR = plan.LutR, lutG = plan.LutG, lutB = plan.LutB, encode = RenderPlan.EncodeLut, hs = plan.HsGain, tone = plan.ToneGain)
            fixed (float* nrCurve = plan.NoiseCurve, vigCurve = plan.VignetteCurve, haze = plan.DehazeInverse)
            fixed (float* bl = baseLarge, bs = baseSmall, dk = dark, vx = dx2, vy = dy2)
            {
                int y1 = Math.Min(h, (block + 1) * RowsPerTask);
                bool simd = PhotoRendererAvx2.IsSupported && !ForceScalar;
                for (int y = block * RowsPerTask; y < y1; y++)
                {
                    byte* srcRow = src + (long)y * w * 4, dstRow = (byte*)dst + (long)y * destinationStride;
                    int done = simd
                        ? PhotoRendererAvx2.RenderRow(plan, y, w, srcRow, dstRow, lutR, lutG, lutB, encode, hs, tone, nrCurve, vigCurve, haze, bl, bs, dk, vx, vy)
                        : 0;
                    if (done < w)
                        RenderRow(plan, y, w, done, srcRow + done * 4, dstRow + done * 4,
                            lutR, lutG, lutB, encode, hs, tone, nrCurve, vigCurve, haze, bl, bs, dk, vx, vy);
                }
            }
        });
    }

    private static void RenderRow(RenderPlan p, int y, int w, int startX, byte* s, byte* d,
        float* lutR, float* lutG, float* lutB, float* encode, float* hs, float* tone, float* nrCurve, float* vigCurve, float* haze,
        float* baseLarge, float* baseSmall, float* dark, float* dx2, float* dy2)
    {
        // Copy everything the loop reads into locals: writes through d could alias the plan's
        // fields as far as the JIT knows, so it would otherwise reload them for every pixel.
        bool useHs = p.HighlightsShadows, useTone = p.Tone, useDehaze = p.Dehaze, useDetail = p.Detail;
        bool useColor = p.Color, useMatrix = p.Matrix, useVignette = p.Vignette, useGrain = p.Grain;
        bool hazeUp = p.DehazeOmega > 0, useSmall = baseSmall != null;
        float airlight = p.Airlight, hazeAdd = p.HazeAdd, clarity = p.Clarity, sharpen = p.Sharpen, noise = p.NoiseReduction;
        float vibrance = p.Vibrance, vignetteAmount = p.VignetteAmount, grainAmount = p.GrainAmount, invCell = p.GrainInvCell;
        float m00 = p.M00, m01 = p.M01, m02 = p.M02, m10 = p.M10, m11 = p.M11, m12 = p.M12, m20 = p.M20, m21 = p.M21, m22 = p.M22;
        const float BaseScale = RenderPlan.BaseSize - 1, CurveScale = RenderPlan.CurveSize - 1;

        long rowStart = (long)y * w;
        float vigY = useVignette ? dy2[y] : 0;
        float grainY = y * invCell;
        for (int x = startX; x < w; x++, s += 4, d += 4)
        {
            int sb = s[0], sg = s[1], sr = s[2];
            byte sa = s[3];
            float r = lutR[sr], g = lutG[sg], b = lutB[sb];
            long i = rowStart + x;

            if (useHs)
            {
                float k = hs[(int)(baseLarge[i] * BaseScale + 0.5f)];
                r *= k; g *= k; b *= k;
            }

            if (useTone)
            {
                float k = tone[ToneIndex(0.2126f * r + 0.7152f * g + 0.0722f * b)];
                r *= k; g *= k; b *= k;
            }

            // Keep hue when a channel overshoots white: pull it towards the pixel's luminance.
            float max = MathF.Max(r, MathF.Max(g, b));
            if (max > 1f)
            {
                float lum = 0.2126f * r + 0.7152f * g + 0.0722f * b;
                if (lum >= 1f)
                {
                    r = g = b = 1f;
                }
                else
                {
                    float t = (1f - lum) / (max - lum);
                    r = lum + (r - lum) * t;
                    g = lum + (g - lum) * t;
                    b = lum + (b - lum) * t;
                }
            }

            float er = encode[EncodeIndex(r)], eg = encode[EncodeIndex(g)], eb = encode[EncodeIndex(b)];

            if (useDehaze)
            {
                if (hazeUp)
                {
                    float inv = haze[(int)(dark[i] * CurveScale + 0.5f)];
                    er = (er - airlight) * inv + airlight;
                    eg = (eg - airlight) * inv + airlight;
                    eb = (eb - airlight) * inv + airlight;
                }
                else
                {
                    er += (airlight - er) * hazeAdd;
                    eg += (airlight - eg) * hazeAdd;
                    eb += (airlight - eb) * hazeAdd;
                }
            }

            if (useDetail)
            {
                float source = (0.2126f * sr + 0.7152f * sg + 0.0722f * sb) * (1f / 255f);
                float delta = 0;
                if (clarity != 0)
                {
                    float l = 0.2126f * er + 0.7152f * eg + 0.0722f * eb;
                    float mid = 1f - (2f * l - 1f) * (2f * l - 1f);
                    delta = clarity * (source - baseLarge[i]) * (mid > 0f ? mid : 0f);
                }
                if (useSmall)
                {
                    float fine = source - baseSmall[i];
                    delta += sharpen * fine;
                    if (noise != 0)
                    {
                        float f = fine < -0.5f ? -0.5f : fine > 0.5f ? 0.5f : fine;
                        delta -= noise * nrCurve[(int)((f + 0.5f) * CurveScale + 0.5f)];
                    }
                }
                er += delta; eg += delta; eb += delta;
            }

            if (useColor)
            {
                if (useMatrix)
                {
                    float nr = m00 * er + m01 * eg + m02 * eb;
                    float ng = m10 * er + m11 * eg + m12 * eb;
                    float nb = m20 * er + m21 * eg + m22 * eb;
                    er = nr; eg = ng; eb = nb;
                }
                if (vibrance != 0)
                {
                    float l = 0.213f * er + 0.715f * eg + 0.072f * eb;
                    float sat = MathF.Max(er, MathF.Max(eg, eb)) - MathF.Min(er, MathF.Min(eg, eb));
                    sat = sat < 0f ? 0f : sat > 1f ? 1f : sat;
                    // Boost muted colours most; reducing vibrance mutes everything a little less than saturation would.
                    float f = vibrance > 0 ? 1f + vibrance * (1f - sat) * (1f - sat) : 1f + vibrance * (1f - 0.5f * sat);
                    er = l + (er - l) * f;
                    eg = l + (eg - l) * f;
                    eb = l + (eb - l) * f;
                }
            }

            if (useVignette)
            {
                float r2 = dx2[x] + vigY;
                float v = vigCurve[(int)((r2 < 1f ? r2 : 1f) * CurveScale)];
                if (vignetteAmount < 0)
                {
                    er *= v; eg *= v; eb *= v;
                }
                else
                {
                    er = 1f - (1f - er) * v;
                    eg = 1f - (1f - eg) * v;
                    eb = 1f - (1f - eb) * v;
                }
            }

            if (useGrain)
            {
                float l = 0.2126f * er + 0.7152f * eg + 0.0722f * eb;
                float mid = 1f - (2f * l - 1f) * (2f * l - 1f);
                float n = Noise(x * invCell, grainY) * grainAmount * (0.4f + 0.6f * (mid > 0f ? mid : 0f));
                er += n; eg += n; eb += n;
            }

            d[0] = ToByte(eb);
            d[1] = ToByte(eg);
            d[2] = ToByte(er);
            d[3] = sa;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int EncodeIndex(float linear)
    {
        if (linear <= 0f) return 0;
        if (linear >= 1f) return RenderPlan.EncodeSize - 1;
        return (int)(MathF.Sqrt(linear) * (RenderPlan.EncodeSize - 1) + 0.5f);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ToneIndex(float linear)
    {
        if (linear <= 0f) return 0;
        if (linear >= 4f) return RenderPlan.ToneSize - 1;
        return (int)(MathF.Sqrt(linear * 0.25f) * (RenderPlan.ToneSize - 1) + 0.5f);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte ToByte(float v)
    {
        int i = (int)(v * 255f + 0.5f);
        return (byte)(i < 0 ? 0 : i > 255 ? 255 : i);
    }

    /// <summary>Smooth value noise in −1…1 on an integer lattice (bilinear between hashed corners).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Noise(float fx, float fy)
    {
        int ix = (int)fx, iy = (int)fy;
        float tx = fx - ix, ty = fy - iy;
        float n00 = Hash(ix, iy);
        if (tx == 0f && ty == 0f)
            return n00;
        float n10 = Hash(ix + 1, iy), n01 = Hash(ix, iy + 1), n11 = Hash(ix + 1, iy + 1);
        float top = n00 + (n10 - n00) * tx, bottom = n01 + (n11 - n01) * tx;
        return top + (bottom - top) * ty;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Hash(int x, int y)
    {
        uint h = unchecked((uint)x * 0x8DA6B343u ^ (uint)y * 0xD8163841u ^ 0x9E3779B9u);
        h ^= h >> 13;
        h *= 0x5BD1E995u;
        h ^= h >> 15;
        // Sum of two uniforms → triangular distribution, closer to film grain than flat noise.
        return ((h & 0xFFFF) + (h >> 16)) * (1f / 65535f) - 1f;
    }
}
