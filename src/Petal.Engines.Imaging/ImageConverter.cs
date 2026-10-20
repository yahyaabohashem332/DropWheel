using ImageMagick;
using Petal.Core.Formats;
using Petal.Core.Jobs;
using F = Petal.Core.Formats.Formats;

namespace Petal.Engines.Imaging;

/// <summary>
/// Raster and SVG conversions to any raster format or PDF. Decoding and most encoders are
/// ImageMagick; HEIC is encoded with FFmpeg's libx265 and a built-in HEIF muxer, and PDFs are
/// written directly (JPEGs are embedded without re-compression).
/// </summary>
public sealed class ImageConverter : IConverter, IResaver
{
    public string Name => "ImageMagick";

    public bool CanResave(FileFormat format) =>
        format.Family == FormatFamily.Image && format.CanWrite && (format != F.Heic || HeifWriter.IsAvailable);

    public bool CanConvert(FileFormat from, FileFormat to) =>
        from.Family is FormatFamily.Image or FormatFamily.Vector &&
        (to.Family == FormatFamily.Image || to == F.Pdf) &&
        to.CanWrite &&
        (to != F.Heic || HeifWriter.IsAvailable);

    public Task<JobResult> ConvertAsync(ConversionJob job, IProgress<double>? progress, CancellationToken cancellationToken) =>
        Task.Run(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(0.02);

            // Trust the bytes over the extension: a ".jpg" that is really a PNG still converts.
            var actual = ImageFormats.Detect(job.InputPath) ?? job.From;
            var load = new LoadOptions
            {
                SvgDensity = Math.Max(96, job.Settings.RenderDpi / 2.0),
                SvgMinLongEdge = job.To == F.Ico ? 256 : 0,
            };
            using var image = ImageLoader.Load(job.InputPath, actual, load);
            cancellationToken.ThrowIfCancellationRequested();
            bool shrunk = Shrink(image, job.Settings.MaxLongEdge);
            progress?.Report(0.45);

            var options = new ImageWriteOptions
            {
                Quality = Math.Clamp(job.Settings.ImageQuality, 1, 100),
                PdfJpegSource = actual == F.Jpg && !shrunk ? job.InputPath : null,
                PdfPreferJpeg = ImageFormats.IsLossy(actual),
            };
            await ImageWriter.WriteAsync(image, job.OutputPath, job.To, options, cancellationToken);
            progress?.Report(1);
            return JobResult.Single(job.OutputPath);
        }, cancellationToken);

    /// <summary>Scales the image down so its longest side is at most <paramref name="maxLongEdge"/>; 0 does nothing.</summary>
    internal static bool Shrink(MagickImage image, int maxLongEdge)
    {
        if (maxLongEdge <= 0 || Math.Max(image.Width, image.Height) <= maxLongEdge)
            return false;
        var (w, h) = ImageTransformer.TargetSize((int)image.Width, (int)image.Height,
            new ImageTransform { Mode = ResizeMode.Fit, Width = maxLongEdge, Height = maxLongEdge });
        image.FilterType = FilterType.Lanczos;
        image.Resize(new MagickGeometry((uint)w, (uint)h) { IgnoreAspectRatio = true });
        image.ResetPage();
        MetadataHygiene.AfterGeometryChange(image);
        return true;
    }
}
