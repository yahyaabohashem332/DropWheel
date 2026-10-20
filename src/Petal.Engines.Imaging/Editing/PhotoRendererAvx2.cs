using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Petal.Engines.Imaging.Editing;

/// <summary>
/// AVX2 version of the pixel kernel: eight pixels per step, lookup tables read with gathers.
/// Every operation mirrors the scalar kernel in the same order (no FMA), so both paths give
/// bit-identical results and the scalar loop can finish a row's last few pixels.
/// </summary>
internal static unsafe class PhotoRendererAvx2
{
    public static bool IsSupported => Avx2.IsSupported;

    /// <summary>Processes whole groups of eight pixels and returns how many it did.</summary>
    public static int RenderRow(RenderPlan p, int y, int w, byte* s, byte* d,
        float* lutR, float* lutG, float* lutB, float* encode, float* hs, float* tone, float* nrCurve, float* vigCurve, float* haze,
        float* baseLarge, float* baseSmall, float* dark, float* dx2, float* dy2)
    {
        int count = w & ~7;
        if (count == 0)
            return 0;

        bool useHs = p.HighlightsShadows, useTone = p.Tone, useDehaze = p.Dehaze, useDetail = p.Detail;
        bool useColor = p.Color, useMatrix = p.Matrix, useVignette = p.Vignette, useGrain = p.Grain;
        bool hazeUp = p.DehazeOmega > 0, useSmall = baseSmall != null;
        float clarityS = p.Clarity, sharpenS = p.Sharpen, noiseS = p.NoiseReduction, vibranceS = p.Vibrance;
        bool darkVignette = p.VignetteAmount < 0, grainOnGrid = p.GrainInvCell == 1f;

        var zero = Vector256<float>.Zero;
        var one = Vector256.Create(1f);
        var half = Vector256.Create(0.5f);
        var two = Vector256.Create(2f);
        var kr = Vector256.Create(0.2126f);
        var kg = Vector256.Create(0.7152f);
        var kb = Vector256.Create(0.0722f);
        var cr = Vector256.Create(0.213f);
        var cg = Vector256.Create(0.715f);
        var cb = Vector256.Create(0.072f);
        var baseScale = Vector256.Create((float)(RenderPlan.BaseSize - 1));
        var curveScale = Vector256.Create((float)(RenderPlan.CurveSize - 1));
        var encodeScale = Vector256.Create((float)(RenderPlan.EncodeSize - 1));
        var toneScale = Vector256.Create((float)(RenderPlan.ToneSize - 1));
        var quarter = Vector256.Create(0.25f);
        var four = Vector256.Create(4f);
        var inv255 = Vector256.Create(1f / 255f);
        var f255 = Vector256.Create(255f);
        var airlight = Vector256.Create(p.Airlight);
        var hazeAdd = Vector256.Create(p.HazeAdd);
        var clarity = Vector256.Create(clarityS);
        var sharpen = Vector256.Create(sharpenS);
        var noise = Vector256.Create(noiseS);
        var minusHalf = Vector256.Create(-0.5f);
        var vibrance = Vector256.Create(vibranceS);
        var m00 = Vector256.Create(p.M00); var m01 = Vector256.Create(p.M01); var m02 = Vector256.Create(p.M02);
        var m10 = Vector256.Create(p.M10); var m11 = Vector256.Create(p.M11); var m12 = Vector256.Create(p.M12);
        var m20 = Vector256.Create(p.M20); var m21 = Vector256.Create(p.M21); var m22 = Vector256.Create(p.M22);
        var vigY = Vector256.Create(useVignette ? dy2[y] : 0f);
        var grainAmount = Vector256.Create(p.GrainAmount);
        var invCell = Vector256.Create(p.GrainInvCell);
        var grainY = Vector256.Create(y * p.GrainInvCell);
        var g04 = Vector256.Create(0.4f);
        var g06 = Vector256.Create(0.6f);
        var lanes = Vector256.Create(0, 1, 2, 3, 4, 5, 6, 7);
        var byteMask = Vector256.Create(0xFFu);
        var maxIndexBase = Vector256.Create(RenderPlan.BaseSize - 1);
        var maxIndexCurve = Vector256.Create(RenderPlan.CurveSize - 1);
        var maxIndex255 = Vector256.Create(255);

        long rowStart = (long)y * w;
        for (int x = 0; x < count; x += 8, s += 32, d += 32)
        {
            long i = rowStart + x;
            var px = Avx.LoadVector256((uint*)s);
            var ib = Avx2.And(px, byteMask).AsInt32();
            var ig = Avx2.And(Avx2.ShiftRightLogical(px, 8), byteMask).AsInt32();
            var ir = Avx2.And(Avx2.ShiftRightLogical(px, 16), byteMask).AsInt32();
            var alpha = Avx2.ShiftRightLogical(px, 24);

            var r = Avx2.GatherVector256(lutR, ir, 4);
            var g = Avx2.GatherVector256(lutG, ig, 4);
            var b = Avx2.GatherVector256(lutB, ib, 4);

            if (useHs)
            {
                var idx = Clamp(Avx.ConvertToVector256Int32WithTruncation(Avx.Add(Avx.Multiply(Avx.LoadVector256(baseLarge + i), baseScale), half)), maxIndexBase);
                var k = Avx2.GatherVector256(hs, idx, 4);
                r = Avx.Multiply(r, k); g = Avx.Multiply(g, k); b = Avx.Multiply(b, k);
            }

            if (useTone)
            {
                var lum = Luma(r, g, b, kr, kg, kb);
                var c = Avx.Min(Avx.Max(lum, zero), four);
                var idx = Avx.ConvertToVector256Int32WithTruncation(Avx.Add(Avx.Multiply(Avx.Sqrt(Avx.Multiply(c, quarter)), toneScale), half));
                var k = Avx2.GatherVector256(tone, idx, 4);
                r = Avx.Multiply(r, k); g = Avx.Multiply(g, k); b = Avx.Multiply(b, k);
            }

            var max = Avx.Max(r, Avx.Max(g, b));
            var over = Avx.CompareGreaterThan(max, one);
            if (Avx.MoveMask(over) != 0)
            {
                var lum = Luma(r, g, b, kr, kg, kb);
                var t = Avx.Divide(Avx.Subtract(one, lum), Avx.Subtract(max, lum));
                var white = Avx.CompareGreaterThanOrEqual(lum, one);
                var nr = Avx.BlendVariable(Avx.Add(lum, Avx.Multiply(Avx.Subtract(r, lum), t)), one, white);
                var ng = Avx.BlendVariable(Avx.Add(lum, Avx.Multiply(Avx.Subtract(g, lum), t)), one, white);
                var nb = Avx.BlendVariable(Avx.Add(lum, Avx.Multiply(Avx.Subtract(b, lum), t)), one, white);
                r = Avx.BlendVariable(r, nr, over);
                g = Avx.BlendVariable(g, ng, over);
                b = Avx.BlendVariable(b, nb, over);
            }

            var er = Avx2.GatherVector256(encode, EncodeIndex(r, zero, one, encodeScale, half), 4);
            var eg = Avx2.GatherVector256(encode, EncodeIndex(g, zero, one, encodeScale, half), 4);
            var eb = Avx2.GatherVector256(encode, EncodeIndex(b, zero, one, encodeScale, half), 4);

            if (useDehaze)
            {
                if (hazeUp)
                {
                    var idx = Clamp(Avx.ConvertToVector256Int32WithTruncation(Avx.Add(Avx.Multiply(Avx.LoadVector256(dark + i), curveScale), half)), maxIndexCurve);
                    var inv = Avx2.GatherVector256(haze, idx, 4);
                    er = Avx.Add(Avx.Multiply(Avx.Subtract(er, airlight), inv), airlight);
                    eg = Avx.Add(Avx.Multiply(Avx.Subtract(eg, airlight), inv), airlight);
                    eb = Avx.Add(Avx.Multiply(Avx.Subtract(eb, airlight), inv), airlight);
                }
                else
                {
                    er = Avx.Add(er, Avx.Multiply(Avx.Subtract(airlight, er), hazeAdd));
                    eg = Avx.Add(eg, Avx.Multiply(Avx.Subtract(airlight, eg), hazeAdd));
                    eb = Avx.Add(eb, Avx.Multiply(Avx.Subtract(airlight, eb), hazeAdd));
                }
            }

            if (useDetail)
            {
                var source = Avx.Multiply(Luma(Avx.ConvertToVector256Single(ir), Avx.ConvertToVector256Single(ig), Avx.ConvertToVector256Single(ib), kr, kg, kb), inv255);
                var delta = zero;
                if (clarityS != 0)
                {
                    var l = Luma(er, eg, eb, kr, kg, kb);
                    var m = Avx.Subtract(Avx.Multiply(two, l), one);
                    var mid = Avx.Subtract(one, Avx.Multiply(m, m));
                    delta = Avx.Multiply(Avx.Multiply(clarity, Avx.Subtract(source, Avx.LoadVector256(baseLarge + i))), Avx.Max(mid, zero));
                }
                if (useSmall)
                {
                    var fine = Avx.Subtract(source, Avx.LoadVector256(baseSmall + i));
                    delta = Avx.Add(delta, Avx.Multiply(sharpen, fine));
                    if (noiseS != 0)
                    {
                        var f = Avx.Min(Avx.Max(fine, minusHalf), half);
                        var idx = Clamp(Avx.ConvertToVector256Int32WithTruncation(Avx.Add(Avx.Multiply(Avx.Add(f, half), curveScale), half)), maxIndexCurve);
                        delta = Avx.Subtract(delta, Avx.Multiply(noise, Avx2.GatherVector256(nrCurve, idx, 4)));
                    }
                }
                er = Avx.Add(er, delta); eg = Avx.Add(eg, delta); eb = Avx.Add(eb, delta);
            }

            if (useColor)
            {
                if (useMatrix)
                {
                    var nr = Avx.Add(Avx.Add(Avx.Multiply(m00, er), Avx.Multiply(m01, eg)), Avx.Multiply(m02, eb));
                    var ng = Avx.Add(Avx.Add(Avx.Multiply(m10, er), Avx.Multiply(m11, eg)), Avx.Multiply(m12, eb));
                    var nb = Avx.Add(Avx.Add(Avx.Multiply(m20, er), Avx.Multiply(m21, eg)), Avx.Multiply(m22, eb));
                    er = nr; eg = ng; eb = nb;
                }
                if (vibranceS != 0)
                {
                    var l = Luma(er, eg, eb, cr, cg, cb);
                    var sat = Avx.Subtract(Avx.Max(er, Avx.Max(eg, eb)), Avx.Min(er, Avx.Min(eg, eb)));
                    sat = Avx.Min(Avx.Max(sat, zero), one);
                    var oneMinus = Avx.Subtract(one, sat);
                    var f = vibranceS > 0
                        ? Avx.Add(one, Avx.Multiply(Avx.Multiply(vibrance, oneMinus), oneMinus))
                        : Avx.Add(one, Avx.Multiply(vibrance, Avx.Subtract(one, Avx.Multiply(half, sat))));
                    er = Avx.Add(l, Avx.Multiply(Avx.Subtract(er, l), f));
                    eg = Avx.Add(l, Avx.Multiply(Avx.Subtract(eg, l), f));
                    eb = Avx.Add(l, Avx.Multiply(Avx.Subtract(eb, l), f));
                }
            }

            if (useVignette)
            {
                var r2 = Avx.Min(Avx.Add(Avx.LoadVector256(dx2 + x), vigY), one);
                var v = Avx2.GatherVector256(vigCurve, Avx.ConvertToVector256Int32WithTruncation(Avx.Multiply(r2, curveScale)), 4);
                if (darkVignette)
                {
                    er = Avx.Multiply(er, v); eg = Avx.Multiply(eg, v); eb = Avx.Multiply(eb, v);
                }
                else
                {
                    er = Avx.Subtract(one, Avx.Multiply(Avx.Subtract(one, er), v));
                    eg = Avx.Subtract(one, Avx.Multiply(Avx.Subtract(one, eg), v));
                    eb = Avx.Subtract(one, Avx.Multiply(Avx.Subtract(one, eb), v));
                }
            }

            if (useGrain)
            {
                var l = Luma(er, eg, eb, kr, kg, kb);
                var m = Avx.Subtract(Avx.Multiply(two, l), one);
                var mid = Avx.Max(Avx.Subtract(one, Avx.Multiply(m, m)), zero);
                var xs = Avx2.Add(Vector256.Create(x), lanes);
                Vector256<float> n;
                if (grainOnGrid)
                {
                    n = Hash(xs.AsUInt32(), Vector256.Create((uint)y));
                }
                else
                {
                    var fx = Avx.Multiply(Avx.ConvertToVector256Single(xs), invCell);
                    var ixs = Avx.ConvertToVector256Int32WithTruncation(fx);
                    var iys = Avx.ConvertToVector256Int32WithTruncation(grainY);
                    var tx = Avx.Subtract(fx, Avx.ConvertToVector256Single(ixs));
                    var ty = Avx.Subtract(grainY, Avx.ConvertToVector256Single(iys));
                    var ones = Vector256.Create(1);
                    var x0 = ixs.AsUInt32();
                    var x1 = Avx2.Add(ixs, ones).AsUInt32();
                    var y0 = iys.AsUInt32();
                    var y1 = Avx2.Add(iys, ones).AsUInt32();
                    var n00 = Hash(x0, y0);
                    var n10 = Hash(x1, y0);
                    var n01 = Hash(x0, y1);
                    var n11 = Hash(x1, y1);
                    var top = Avx.Add(n00, Avx.Multiply(Avx.Subtract(n10, n00), tx));
                    var bottom = Avx.Add(n01, Avx.Multiply(Avx.Subtract(n11, n01), tx));
                    n = Avx.Add(top, Avx.Multiply(Avx.Subtract(bottom, top), ty));
                }
                n = Avx.Multiply(Avx.Multiply(n, grainAmount), Avx.Add(g04, Avx.Multiply(g06, mid)));
                er = Avx.Add(er, n); eg = Avx.Add(eg, n); eb = Avx.Add(eb, n);
            }

            var ob = ToBytes(eb, f255, half, maxIndex255);
            var og = ToBytes(eg, f255, half, maxIndex255);
            var or = ToBytes(er, f255, half, maxIndex255);
            var packed = Avx2.Or(Avx2.Or(ob, Avx2.ShiftLeftLogical(og, 8)), Avx2.Or(Avx2.ShiftLeftLogical(or, 16), Avx2.ShiftLeftLogical(alpha, 24)));
            Avx.Store((uint*)d, packed);
        }
        return count;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<float> Luma(Vector256<float> r, Vector256<float> g, Vector256<float> b, Vector256<float> kr, Vector256<float> kg, Vector256<float> kb) =>
        Avx.Add(Avx.Add(Avx.Multiply(kr, r), Avx.Multiply(kg, g)), Avx.Multiply(kb, b));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<int> EncodeIndex(Vector256<float> linear, Vector256<float> zero, Vector256<float> one, Vector256<float> scale, Vector256<float> half)
    {
        var c = Avx.Min(Avx.Max(linear, zero), one);
        return Avx.ConvertToVector256Int32WithTruncation(Avx.Add(Avx.Multiply(Avx.Sqrt(c), scale), half));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<int> Clamp(Vector256<int> index, Vector256<int> max) =>
        Avx2.Min(Avx2.Max(index, Vector256<int>.Zero), max);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<uint> ToBytes(Vector256<float> v, Vector256<float> f255, Vector256<float> half, Vector256<int> max255)
    {
        var i = Avx.ConvertToVector256Int32WithTruncation(Avx.Add(Avx.Multiply(v, f255), half));
        return Avx2.Min(Avx2.Max(i, Vector256<int>.Zero), max255).AsUInt32();
    }

    /// <summary>Same hash as <c>PhotoRenderer.Hash</c>, eight lanes at once.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<float> Hash(Vector256<uint> x, Vector256<uint> y)
    {
        var h = Avx2.Xor(Avx2.Xor(Avx2.MultiplyLow(x, Vector256.Create(0x8DA6B343u)), Avx2.MultiplyLow(y, Vector256.Create(0xD8163841u))), Vector256.Create(0x9E3779B9u));
        h = Avx2.Xor(h, Avx2.ShiftRightLogical(h, 13));
        h = Avx2.MultiplyLow(h, Vector256.Create(0x5BD1E995u));
        h = Avx2.Xor(h, Avx2.ShiftRightLogical(h, 15));
        var sum = Avx2.Add(Avx2.And(h, Vector256.Create(0xFFFFu)), Avx2.ShiftRightLogical(h, 16)).AsInt32();
        return Avx.Subtract(Avx.Multiply(Avx.ConvertToVector256Single(sum), Vector256.Create(1f / 65535f)), Vector256.Create(1f));
    }
}
