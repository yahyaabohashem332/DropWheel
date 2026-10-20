using ImageMagick;
using Petal.Core.Formats;
using Petal.Core.Jobs;
using F = Petal.Core.Formats.Formats;

namespace Petal.Engines.Imaging;

/// <summary>Raster and SVG conversions, plus image → PDF, through ImageMagick.</summary>
public sealed class ImageConverter : IConverter
{
    public string Name => "ImageMagick";

    public bool CanConvert(FileFormat from, FileFormat to) =>
        (from.Family is FormatFamily.Image or FormatFamily.Vector) &&
        (to.Family == FormatFamily.Image || to == F.Pdf);

    public Task<JobResult> ConvertAsync(ConversionJob job, IProgress<double>? progress, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(0.05);
            var settings = new MagickReadSettings();
            if (job.From == F.Svg)
            {
                settings.Density = new Density(Math.Max(96, job.Settings.RenderDpi / 2.0));
                settings.BackgroundColor = MagickColors.Transparent;
            }

            using var image = new MagickImage(job.InputPath, settings);
            progress?.Report(0.4);
            image.AutoOrient();
            Prepare(image, job.To, job.Settings);
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(0.6);

            Write(image, job.OutputPath, job.To, job.Settings);
            progress?.Report(1);
            return JobResult.Single(job.OutputPath);
        }, cancellationToken);

    internal static void Prepare(MagickImage image, FileFormat target, ConversionSettings settings)
    {
        // Formats without transparency get a white background instead of black.
        if (target == F.Jpg || target == F.Bmp || target == F.Pdf)
        {
            if (image.HasAlpha)
            {
                image.BackgroundColor = MagickColors.White;
                image.Alpha(AlphaAction.Remove);
            }
        }
        image.Quality = (uint)Math.Clamp(settings.ImageQuality, 1, 100);
    }

    internal static void Write(MagickImage image, string path, FileFormat target, ConversionSettings settings)
    {
        var format = target.Id switch
        {
            "jpg" => MagickFormat.Jpeg,
            "png" => MagickFormat.Png,
            "webp" => MagickFormat.WebP,
            "heic" => MagickFormat.Heic,
            "tiff" => MagickFormat.Tiff,
            "avif" => MagickFormat.Avif,
            "bmp" => MagickFormat.Bmp,
            "ico" => MagickFormat.Ico,
            "pdf" => MagickFormat.Pdf,
            _ => throw new NotSupportedException($"Cannot write {target.Label} images"),
        };

        if (format == MagickFormat.Ico)
        {
            // Windows icons: square, every standard size up to 256 px.
            int side = (int)Math.Max(image.Width, image.Height);
            image.BackgroundColor = MagickColors.Transparent;
            image.Extent((uint)side, (uint)side, Gravity.Center);
            if (side > 256)
                image.Resize(256, 256);
            image.Settings.SetDefine(MagickFormat.Ico, "auto-resize", "256,128,64,48,32,24,16");
        }
        if (format == MagickFormat.Tiff)
            image.Settings.Compression = CompressionMethod.LZW;
        if (format == MagickFormat.Png)
            image.Settings.SetDefine(MagickFormat.Png, "compression-level", "9");
        if (format == MagickFormat.Pdf)
            image.Density = new Density(Math.Max(72, image.Density.X > 1 ? image.Density.X : 72));

        var temp = path + ".petal-tmp";
        image.Write(temp, format);
        File.Move(temp, path, overwrite: false);
    }
}
