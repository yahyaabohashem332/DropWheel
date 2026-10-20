using ImageMagick;

namespace Petal.Engines.Imaging;

/// <summary>
/// Lays a transparent PNG over a picture, for watermarks. The caller draws the overlay at the
/// image's own size, so text can be laid out with the app's typography (including Arabic).
/// </summary>
public static class ImageOverlay
{
    /// <param name="overlay">Given the upright width and height, returns a PNG of exactly that size.</param>
    public static Task CompositeAsync(string inputPath, string outputPath, Func<int, int, byte[]> overlay, IProgress<double>? progress = null,
        CancellationToken cancellationToken = default) =>
        Task.Run(async () =>
        {
            var format = ImageFormats.Detect(inputPath);
            using var image = ImageLoader.Load(inputPath, format);
            progress?.Report(0.35);
            using var layer = new MagickImage(overlay((int)image.Width, (int)image.Height));
            cancellationToken.ThrowIfCancellationRequested();
            if (layer.Width != image.Width || layer.Height != image.Height)
                layer.Resize(new MagickGeometry(image.Width, image.Height) { IgnoreAspectRatio = true });
            image.Composite(layer, CompositeOperator.Over, Channels.All);
            progress?.Report(0.6);
            var target = ToolSupport.OutputFormat(outputPath, format);
            await ImageWriter.WriteAsync(image, outputPath, target, ToolSupport.WriteOptions(image, format, target), cancellationToken);
            progress?.Report(1);
        }, cancellationToken);
}
