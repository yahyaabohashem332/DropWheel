using System.Collections.Concurrent;
using ImageMagick;
using Microsoft.ML.OnnxRuntime;
using Petal.Core.Models;

namespace Petal.Engines.Imaging;

/// <summary>
/// One ONNX model loaded for inference: on the graphics card through DirectML when Windows has a
/// DirectX 12 device, otherwise on the processor. Sessions are kept, since loading takes seconds.
/// </summary>
internal sealed class OnnxModel
{
    private static readonly ConcurrentDictionary<string, Lazy<OnnxModel>> Cache = new(StringComparer.OrdinalIgnoreCase);
    private InferenceSession _session;
    private readonly string _path;

    private OnnxModel(string path, bool allowGpu)
    {
        _path = path;
        (_session, OnGpu) = Create(path, gpu: allowGpu);
    }

    /// <summary>True when inference runs on the graphics card.</summary>
    public bool OnGpu { get; private set; }

    public string InputName => _session.InputMetadata.Keys.First();
    public string OutputName => _session.OutputMetadata.Keys.First();

    /// <param name="allowGpu">
    /// False for large models: on 6 GB cards BiRefNet runs DirectML out of memory and can reset the
    /// graphics driver, which flickers the whole screen. The processor is slower but always works.
    /// </param>
    public static OnnxModel Get(string path, bool allowGpu) =>
        Cache.GetOrAdd(path, p => new Lazy<OnnxModel>(() => new OnnxModel(p, allowGpu))).Value;

    private static (InferenceSession, bool) Create(string path, bool gpu)
    {
        if (gpu)
        {
            try
            {
                var options = new SessionOptions
                {
                    // DirectML wants these off.
                    EnableMemoryPattern = false,
                    ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
                    GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                };
                options.AppendExecutionProvider_DML(0);
                return (new InferenceSession(path, options), true);
            }
            catch (Exception ex) when (ex is OnnxRuntimeException or DllNotFoundException or EntryPointNotFoundException)
            {
                // No DirectX 12 device: the processor it is.
            }
        }
        var cpu = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            IntraOpNumThreads = Environment.ProcessorCount,
        };
        return (new InferenceSession(path, cpu), false);
    }

    /// <summary>Runs the model on one float tensor and returns the first output.</summary>
    public float[] Run(float[] input, long[] shape)
    {
        try
        {
            return RunOn(_session, input, shape);
        }
        catch (OnnxRuntimeException) when (OnGpu)
        {
            // Some drivers fail at run time; fall back to the processor for good.
            lock (this)
            {
                if (OnGpu)
                {
                    var old = _session;
                    (_session, OnGpu) = Create(_path, gpu: false);
                    old.Dispose();
                }
            }
            return RunOn(_session, input, shape);
        }
    }

    private float[] RunOn(InferenceSession session, float[] input, long[] shape)
    {
        using var tensor = OrtValue.CreateTensorValueFromMemory(input, shape);
        using var run = new RunOptions();
        using var outputs = session.Run(run, [InputName], [tensor], [OutputName]);
        return outputs[0].GetTensorDataAsSpan<float>().ToArray();
    }
}

/// <summary>What goes behind the subject once the background is gone.</summary>
public sealed record RemoveBackgroundOptions
{
    /// <summary>A solid colour behind the subject; null keeps it transparent.</summary>
    public MagickColor? Fill { get; init; }

    /// <summary>Trim the empty space around the subject.</summary>
    public bool CropToSubject { get; init; }
}

/// <summary>Remove Background: BiRefNet finds the subject, the rest becomes transparent. Runs locally.</summary>
public static class BackgroundRemover
{
    private const int Side = 1024;
    private static readonly float[] Mean = [0.485f, 0.456f, 0.406f];
    private static readonly float[] Std = [0.229f, 0.224f, 0.225f];

    /// <summary>True when the model is on this PC (it is downloaded once, when first needed).</summary>
    public static bool IsReady => ModelStore.IsAvailable(ModelCatalog.Background);

    /// <summary>Saves the picture with its background removed (PNG keeps transparency).</summary>
    public static Task RemoveAsync(string inputPath, string outputPath, RemoveBackgroundOptions? options = null, IProgress<double>? progress = null,
        CancellationToken cancellationToken = default) =>
        Task.Run(async () =>
        {
            options ??= new RemoveBackgroundOptions();
            var format = ImageFormats.Detect(inputPath);
            using var image = ImageLoader.Load(inputPath, format);
            progress?.Report(0.1);
            using var mask = Mask(image, cancellationToken);
            progress?.Report(0.8);
            Apply(image, mask, options);
            var target = ToolSupport.OutputFormat(outputPath, format);
            await ImageWriter.WriteAsync(image, outputPath, target, ToolSupport.WriteOptions(image, format, target), cancellationToken);
            progress?.Report(1);
        }, cancellationToken);

    /// <summary>The cut-out at preview size, for a window to show before saving.</summary>
    public static Task<byte[]> PreviewAsync(string inputPath, int maxSide, RemoveBackgroundOptions? options = null, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            using var image = ImageLoader.Load(inputPath);
            if (Math.Max(image.Width, image.Height) > maxSide)
                image.Resize(new MagickGeometry((uint)maxSide, (uint)maxSide));
            using var mask = Mask(image, cancellationToken);
            Apply(image, mask, options ?? new RemoveBackgroundOptions());
            using var stream = new MemoryStream();
            image.Write(stream, MagickFormat.Png32);
            return stream.ToArray();
        }, cancellationToken);

    /// <summary>
    /// Where the subject stands across a picture: the middle of its mask, 0 at the left edge and 1 at the
    /// right; null when there is hardly any subject (an empty scene). For following someone in a video.
    /// </summary>
    public static Task<double?> SubjectCentreAsync(byte[] picture, CancellationToken cancellationToken = default) =>
        Task.Run<double?>(() =>
        {
            using var image = new MagickImage(picture);
            if (Math.Max(image.Width, image.Height) > 512)
                image.Resize(new MagickGeometry(512, 512));
            using var mask = Mask(image, cancellationToken);
            var values = mask.GetPixels().ToByteArray("R") ?? [];
            int w = (int)mask.Width, h = (int)mask.Height;
            double sum = 0, weighted = 0;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int v = values[y * w + x];
                    if (v < 128)
                        continue;
                    sum += v;
                    weighted += v * (x + 0.5) / w;
                }
            return sum < w * h * 255 * 0.02 ? null : weighted / sum;
        }, cancellationToken);

    /// <summary>The subject's mask at the image's size: white where it is, black where it isn't.</summary>
    internal static MagickImage Mask(MagickImage image, CancellationToken cancellationToken)
    {
        var path = ModelStore.Find(ModelCatalog.Background) ?? throw new FileNotFoundException("The background removal model isn't downloaded yet.");
        var model = OnnxModel.Get(path, allowGpu: false);

        using var small = (MagickImage)image.Clone();
        small.Alpha(AlphaAction.Remove);
        small.FilterType = FilterType.Triangle;
        small.Resize(new MagickGeometry(Side, Side) { IgnoreAspectRatio = true });
        var rgb = small.GetPixels().ToByteArray(PixelMapping.RGB) ?? throw new InvalidOperationException("Could not read pixels");
        var input = new float[3 * Side * Side];
        for (int i = 0, plane = Side * Side; i < plane; i++)
            for (int c = 0; c < 3; c++)
                input[c * plane + i] = (rgb[i * 3 + c] / 255f - Mean[c]) / Std[c];
        cancellationToken.ThrowIfCancellationRequested();

        var logits = model.Run(input, [1, 3, Side, Side]);
        cancellationToken.ThrowIfCancellationRequested();
        var alpha = new byte[Side * Side];
        for (int i = 0; i < alpha.Length; i++)
            alpha[i] = (byte)Math.Clamp(255f / (1f + MathF.Exp(-logits[i])) + 0.5f, 0, 255);

        var mask = new MagickImage(alpha, new PixelReadSettings(Side, Side, StorageType.Char, "R"));
        mask.ColorType = ColorType.Grayscale;
        mask.FilterType = FilterType.Lanczos;
        mask.Resize(new MagickGeometry(image.Width, image.Height) { IgnoreAspectRatio = true });
        return mask;
    }

    private static void Apply(MagickImage image, MagickImage mask, RemoveBackgroundOptions options)
    {
        if (image.HasAlpha)
        {
            // Keep what was already transparent: the new alpha is the darker of the two.
            using var existing = (MagickImage)image.Separate(Channels.Alpha).First();
            mask.Composite(existing, CompositeOperator.Darken);
        }
        image.Alpha(AlphaAction.Set);
        image.Composite(mask, CompositeOperator.CopyAlpha);
        if (options.CropToSubject)
        {
            image.BackgroundColor = MagickColors.Transparent;
            image.ColorFuzz = new Percentage(2);
            image.Trim();
            image.ResetPage();
        }
        if (options.Fill is { } fill)
        {
            using var canvas = new MagickImage(fill, image.Width, image.Height);
            canvas.Composite(image, CompositeOperator.Over);
            image.CopyPixels(canvas);
            image.Alpha(AlphaAction.Off);
        }
        MetadataHygiene.AfterGeometryChange(image);
    }
}

/// <summary>AI enlargement with Real-ESRGAN (4×), tile by tile so any size fits in memory.</summary>
public static class AiUpscaler
{
    public const int Factor = 4;

    /// <summary>Above this the 4× result would be too large to hold comfortably; plain resizing is used.</summary>
    public const long MaxSourcePixels = 4_200_000;

    private const int Tile = 128;
    private const int Overlap = 12;

    public static bool IsReady => ModelStore.IsAvailable(ModelCatalog.Upscaler);

    /// <summary>True when the model is here and the picture isn't too large for it.</summary>
    public static bool CanEnlarge(int width, int height) => IsReady && (long)width * height <= MaxSourcePixels;

    /// <summary>A new image four times the size, sharpened by the model. Transparency is enlarged smoothly.</summary>
    internal static MagickImage Upscale(MagickImage image, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var path = ModelStore.Find(ModelCatalog.Upscaler) ?? throw new FileNotFoundException("The enlargement model is missing. Reinstall DropWheel to restore it.");
        var model = OnnxModel.Get(path, allowGpu: true);
        int w = (int)image.Width, h = (int)image.Height, ow = w * Factor, oh = h * Factor;

        using var flat = (MagickImage)image.Clone();
        flat.Alpha(AlphaAction.Off);
        var rgb = flat.GetPixels().ToByteArray(PixelMapping.RGB) ?? throw new InvalidOperationException("Could not read pixels");
        var output = new byte[(long)ow * oh * 3];
        int step = Tile - 2 * Overlap;
        int cols = (w + step - 1) / step, rows = (h + step - 1) / step, done = 0;
        var input = new float[3 * Tile * Tile];
        for (int ty = 0; ty < rows; ty++)
        {
            for (int tx = 0; tx < cols; tx++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int x0 = tx * step - Overlap, y0 = ty * step - Overlap;
                // Edges are mirrored so tiles at the border see natural surroundings.
                for (int y = 0; y < Tile; y++)
                {
                    int sy = Mirror(y0 + y, h);
                    for (int x = 0; x < Tile; x++)
                    {
                        int sx = Mirror(x0 + x, w);
                        long s = ((long)sy * w + sx) * 3;
                        int d = y * Tile + x;
                        input[d] = rgb[s] / 255f;
                        input[Tile * Tile + d] = rgb[s + 1] / 255f;
                        input[2 * Tile * Tile + d] = rgb[s + 2] / 255f;
                    }
                }
                var result = model.Run(input, [1, 3, Tile, Tile]);
                int outTile = Tile * Factor, plane = outTile * outTile;
                // Keep only the tile's centre; the overlap is there to hide seams.
                int coreX = tx * step * Factor, coreY = ty * step * Factor;
                int coreW = Math.Min(step * Factor, ow - coreX), coreH = Math.Min(step * Factor, oh - coreY);
                for (int y = 0; y < coreH; y++)
                {
                    int ry = y + Overlap * Factor;
                    long row = ((long)(coreY + y) * ow + coreX) * 3;
                    for (int x = 0; x < coreW; x++)
                    {
                        int r = ry * outTile + x + Overlap * Factor;
                        long o = row + x * 3L;
                        output[o] = ToByte(result[r]);
                        output[o + 1] = ToByte(result[plane + r]);
                        output[o + 2] = ToByte(result[2 * plane + r]);
                    }
                }
                progress?.Report((double)++done / (rows * cols));
            }
        }

        var enlarged = new MagickImage(output, new PixelReadSettings((uint)ow, (uint)oh, StorageType.Char, PixelMapping.RGB));
        if (image.HasAlpha)
        {
            using var alpha = (MagickImage)image.Separate(Channels.Alpha).First();
            alpha.FilterType = FilterType.Lanczos;
            alpha.Resize(new MagickGeometry((uint)ow, (uint)oh) { IgnoreAspectRatio = true });
            enlarged.Alpha(AlphaAction.Set);
            enlarged.Composite(alpha, CompositeOperator.CopyAlpha);
        }
        return enlarged;
    }

    private static int Mirror(int v, int size)
    {
        if (size == 1) return 0;
        while (v < 0 || v >= size)
            v = v < 0 ? -v : 2 * size - 2 - v;
        return v;
    }

    private static byte ToByte(float v) => (byte)Math.Clamp(v * 255f + 0.5f, 0, 255);
}
