using Petal.Core.Formats;
using Petal.Core.Jobs;
using F = Petal.Core.Formats.Formats;

namespace Petal.Engines.Imaging;

/// <summary>
/// Raster and SVG conversions to any raster format or PDF. Decoding and most encoders are
/// ImageMagick; HEIC is encoded with FFmpeg's libx265 and a built-in HEIF muxer, and PDFs are
/// written directly (JPEGs are embedded without re-compression).
/// </summary>
public sealed class ImageConverter : IConverter
{
    public string Name => "ImageMagick";

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
            progress?.Report(0.45);

            var options = new ImageWriteOptions
            {
                Quality = Math.Clamp(job.Settings.ImageQuality, 1, 100),
                PdfJpegSource = actual == F.Jpg ? job.InputPath : null,
                PdfPreferJpeg = ImageFormats.IsLossy(actual),
            };
            await ImageWriter.WriteAsync(image, job.OutputPath, job.To, options, cancellationToken);
            progress?.Report(1);
            return JobResult.Single(job.OutputPath);
        }, cancellationToken);
}
