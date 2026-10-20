using ImageMagick;

namespace Petal.Engines.Imaging;

/// <summary>How a batch of images is cropped (always centred).</summary>
public abstract record CropSpec
{
    private CropSpec()
    {
    }

    /// <summary>The largest centred rectangle with this aspect ratio, e.g. 16:9.</summary>
    public sealed record AspectRatio(double Width, double Height) : CropSpec;

    /// <summary>
    /// An exact output size in pixels. With <paramref name="ScaleToFill"/> the image is scaled so it
    /// covers the size, then centre-cropped (output is always exactly W×H); otherwise a centred
    /// W×H window is cut out without scaling (smaller images keep their size on that axis).
    /// </summary>
    public sealed record ExactSize(int Width, int Height, bool ScaleToFill = true) : CropSpec;
}

/// <summary>One file of a batch: where to read and where to write.</summary>
public sealed record ImageJobItem(string InputPath, string OutputPath);

/// <summary>Crop Image tool. All rectangles are in upright (EXIF-orientation-applied) pixels.</summary>
public static class ImageCropper
{
    /// <summary>Crops to <paramref name="rect"/> and saves in the output path's format (default: the source's).</summary>
    public static Task CropAsync(string inputPath, string outputPath, PixelRect rect, IProgress<double>? progress = null,
        CancellationToken cancellationToken = default) =>
        Task.Run(async () =>
        {
            var format = ImageFormats.Detect(inputPath);
            using var image = ImageLoader.Load(inputPath, format);
            progress?.Report(0.4);
            var r = rect.ClampTo((int)image.Width, (int)image.Height);
            if (r.IsEmpty)
                throw new ArgumentException("The crop rectangle is outside the image.", nameof(rect));
            Crop(image, r);
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(0.6);
            var target = ToolSupport.OutputFormat(outputPath, format);
            await ImageWriter.WriteAsync(image, outputPath, target, ToolSupport.WriteOptions(image, format, target), cancellationToken);
            progress?.Report(1);
        }, cancellationToken);

    /// <summary>Crops one image according to <paramref name="spec"/>.</summary>
    public static Task CropAsync(string inputPath, string outputPath, CropSpec spec, IProgress<double>? progress = null,
        CancellationToken cancellationToken = default) =>
        Task.Run(async () =>
        {
            var format = ImageFormats.Detect(inputPath);
            using var image = ImageLoader.Load(inputPath, format);
            progress?.Report(0.4);
            Apply(image, spec);
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(0.6);
            var target = ToolSupport.OutputFormat(outputPath, format);
            await ImageWriter.WriteAsync(image, outputPath, target, ToolSupport.WriteOptions(image, format, target), cancellationToken);
            progress?.Report(1);
        }, cancellationToken);

    /// <summary>Crops every item with the same spec; failures are reported per item.</summary>
    public static Task<IReadOnlyList<ImageBatchResult>> CropBatchAsync(IReadOnlyList<ImageJobItem> items, CropSpec spec,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default) =>
        ToolSupport.RunBatchAsync(items, i => (i.InputPath, i.OutputPath),
            (i, p, ct) => CropAsync(i.InputPath, i.OutputPath, spec, p, ct), progress, cancellationToken);

    /// <summary>The largest rectangle of the given aspect ratio centred in a <paramref name="width"/> × <paramref name="height"/> image.</summary>
    public static PixelRect CenteredRect(int width, int height, double aspectWidth, double aspectHeight)
    {
        if (aspectWidth <= 0 || aspectHeight <= 0)
            return new PixelRect(0, 0, width, height);
        double target = aspectWidth / aspectHeight;
        int w = width, h = height;
        if ((double)width / height > target)
            w = Math.Max(1, (int)Math.Round(height * target));
        else
            h = Math.Max(1, (int)Math.Round(width / target));
        return new PixelRect((width - w) / 2, (height - h) / 2, w, h);
    }

    internal static void Apply(MagickImage image, CropSpec spec)
    {
        int width = (int)image.Width, height = (int)image.Height;
        switch (spec)
        {
            case CropSpec.AspectRatio a:
                Crop(image, CenteredRect(width, height, a.Width, a.Height));
                break;

            case CropSpec.ExactSize { ScaleToFill: true } e:
                if (e.Width < 1 || e.Height < 1)
                    throw new ArgumentException("Size must be positive", nameof(spec));
                Crop(image, CenteredRect(width, height, e.Width, e.Height));
                if (image.Width != e.Width || image.Height != e.Height)
                {
                    image.Resize(new MagickGeometry((uint)e.Width, (uint)e.Height) { IgnoreAspectRatio = true });
                    MetadataHygiene.AfterGeometryChange(image);
                }
                break;

            case CropSpec.ExactSize e:
                int w = Math.Min(e.Width, width), h = Math.Min(e.Height, height);
                Crop(image, new PixelRect((width - w) / 2, (height - h) / 2, w, h));
                break;
        }
    }

    internal static void Crop(MagickImage image, PixelRect rect)
    {
        if (rect.X == 0 && rect.Y == 0 && rect.Width == image.Width && rect.Height == image.Height)
            return;
        image.Crop(new MagickGeometry(rect.X, rect.Y, (uint)rect.Width, (uint)rect.Height));
        image.ResetPage();
        MetadataHygiene.AfterGeometryChange(image);
    }
}
