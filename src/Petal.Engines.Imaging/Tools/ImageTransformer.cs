using ImageMagick;

namespace Petal.Engines.Imaging;

public enum ResizeMode
{
    /// <summary>Keep the size (rotate or flip only).</summary>
    None,
    /// <summary>Scale both sides by <see cref="ImageTransform.Percent"/>.</summary>
    Percent,
    /// <summary>Exactly <see cref="ImageTransform.Width"/> wide; the height follows.</summary>
    Width,
    /// <summary>Exactly <see cref="ImageTransform.Height"/> high; the width follows.</summary>
    Height,
    /// <summary>As large as fits inside <see cref="ImageTransform.Width"/> × <see cref="ImageTransform.Height"/>.</summary>
    Fit,
}

/// <summary>
/// Resize, rotate and flip in one pass. Rotation and flips are applied first, so sizes refer to the
/// picture as it will look. Proportions are always kept.
/// </summary>
public sealed record ImageTransform
{
    public ResizeMode Mode { get; init; }
    public double Percent { get; init; } = 100;
    public int Width { get; init; }
    public int Height { get; init; }

    /// <summary>Allow making images larger than they are (off by default: upscaling only blurs).</summary>
    public bool Enlarge { get; init; }

    /// <summary>Clockwise, in quarter turns: 0, 90, 180 or 270.</summary>
    public int Rotation { get; init; }

    public bool FlipHorizontal { get; init; }
    public bool FlipVertical { get; init; }

    /// <summary>
    /// When enlarging, let the AI model add the detail (Real-ESRGAN, up to 4×) instead of only
    /// smoothing pixels. Pictures too large for it are enlarged the ordinary way.
    /// </summary>
    public bool AiEnlarge { get; init; }
}

/// <summary>Resize &amp; Rotate tool.</summary>
public static class ImageTransformer
{
    /// <summary>The output size for an upright <paramref name="width"/> × <paramref name="height"/> image.</summary>
    public static (int Width, int Height) TargetSize(int width, int height, ImageTransform t)
    {
        if (QuarterTurns(t.Rotation) % 2 == 1)
            (width, height) = (height, width);
        double scale = t.Mode switch
        {
            ResizeMode.Percent => t.Percent / 100.0,
            ResizeMode.Width when t.Width > 0 => (double)t.Width / width,
            ResizeMode.Height when t.Height > 0 => (double)t.Height / height,
            ResizeMode.Fit when t.Width > 0 && t.Height > 0 => Math.Min((double)t.Width / width, (double)t.Height / height),
            _ => 1,
        };
        if (!t.Enlarge)
            scale = Math.Min(scale, 1);
        if (scale <= 0)
            scale = 1;
        return (Math.Max(1, (int)Math.Round(width * scale)), Math.Max(1, (int)Math.Round(height * scale)));
    }

    public static Task TransformAsync(string inputPath, string outputPath, ImageTransform transform, IProgress<double>? progress = null,
        CancellationToken cancellationToken = default) =>
        Task.Run(async () =>
        {
            var format = ImageFormats.Detect(inputPath);
            using var source = ImageLoader.Load(inputPath, format);
            progress?.Report(0.15);
            var image = Apply(source, transform, InlineProgress.Slice(progress, 0.15, 0.85), cancellationToken);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(0.85);
                var target = ToolSupport.OutputFormat(outputPath, format);
                await ImageWriter.WriteAsync(image, outputPath, target, ToolSupport.WriteOptions(source, format, target), cancellationToken);
            }
            finally
            {
                if (!ReferenceEquals(image, source))
                    image.Dispose();
            }
            progress?.Report(1);
        }, cancellationToken);

    public static Task<IReadOnlyList<ImageBatchResult>> TransformBatchAsync(IReadOnlyList<ImageJobItem> items, ImageTransform transform,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default) =>
        ToolSupport.RunBatchAsync(items, i => (i.InputPath, i.OutputPath),
            (i, p, ct) => TransformAsync(i.InputPath, i.OutputPath, transform, p, ct), progress, cancellationToken);

    /// <summary>Applies the transform; returns <paramref name="image"/> itself, or a new image when AI enlarged it.</summary>
    internal static MagickImage Apply(MagickImage image, ImageTransform t, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        bool changed = false;
        int turns = QuarterTurns(t.Rotation);
        if (turns != 0)
        {
            image.Rotate(turns * 90);
            changed = true;
        }
        if (t.FlipHorizontal)
        {
            image.Flop();
            changed = true;
        }
        if (t.FlipVertical)
        {
            image.Flip();
            changed = true;
        }
        var (w, h) = TargetSize((int)image.Width, (int)image.Height, t with { Rotation = 0 });
        var result = image;
        if (t.AiEnlarge && (w > image.Width || h > image.Height) && AiUpscaler.CanEnlarge((int)image.Width, (int)image.Height))
        {
            // 4× with the model, then exactly to the asked size (down, or further up).
            result = AiUpscaler.Upscale(image, progress, cancellationToken);
            changed = true;
        }
        if (w != result.Width || h != result.Height)
        {
            result.FilterType = FilterType.Lanczos;
            result.Resize(new MagickGeometry((uint)w, (uint)h) { IgnoreAspectRatio = true });
            changed = true;
        }
        if (changed)
        {
            result.ResetPage();
            MetadataHygiene.AfterGeometryChange(result);
        }
        return result;
    }

    private static int QuarterTurns(int degrees) => ((degrees / 90) % 4 + 4) % 4;
}
