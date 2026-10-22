using System.Globalization;
using ImageMagick;
using Petal.Core.Formats;

namespace Petal.Engines.Imaging;

/// <summary>How sharp a photo is (higher is sharper), when it was taken, and its look in 64 bits.</summary>
public sealed record ShotInfo(double Sharpness, DateTime? Taken, ulong Hash, int Width, int Height)
{
    public double Aspect => Height == 0 ? 0 : (double)Width / Height;
}

/// <summary>
/// What tells a good shot from the others of a burst: its sharpness (how strongly neighbouring pixels
/// differ, measured on a small grey copy, so focus and shake show while the size doesn't matter), and
/// how well it is exposed (blown-out or crushed areas count against it).
/// </summary>
public static class ImageQuality
{
    public static ShotInfo? Measure(string path)
    {
        try
        {
            var format = ImageFormats.Detect(path);
            var settings = new MagickReadSettings();
            if (format == Formats.Jpg)
                settings.SetDefine(MagickFormat.Jpeg, "size", "640x640");
            using var image = format == Formats.Jpg ? new MagickImage(path, settings) : ImageLoader.Load(path, format);
            var info = new MagickImageInfo(path);
            DateTime? taken = null;
            if (image.GetExifProfile()?.GetValue(ExifTag.DateTimeOriginal)?.Value is { } text
                && DateTime.TryParseExact(text, "yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var when))
                taken = when;
            image.AutoOrient();
            bool turned = image.Width > image.Height != info.Width > info.Height;
            int width = (int)(turned ? info.Height : info.Width), height = (int)(turned ? info.Width : info.Height);
            var hash = ImageFingerprint.Compute(path)?.Hash ?? 0;
            ImageWriter.FlattenOnWhite(image);
            image.ColorType = ColorType.Grayscale;
            image.Resize(new MagickGeometry(512, 512) { Greater = true });
            int w = (int)image.Width, h = (int)image.Height;
            var pixels = image.GetPixels().ToByteArray(PixelMapping.RGB) ?? [];
            return new ShotInfo(Sharpness(pixels, w, h), taken, hash, width, height);
        }
        catch (Exception ex) when (ex is MagickException or IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>The variance of the Laplacian, lowered for blown or crushed pixels. RGB bytes of a grey picture.</summary>
    public static double Sharpness(byte[] rgb, int width, int height)
    {
        if (width < 3 || height < 3)
            return 0;
        double sum = 0, sumSq = 0;
        int n = 0, clipped = 0;
        for (int y = 1; y < height - 1; y++)
        {
            for (int x = 1; x < width - 1; x++)
            {
                int c = rgb[(y * width + x) * 3];
                if (c <= 3 || c >= 252)
                    clipped++;
                int lap = 4 * c - rgb[(y * width + x - 1) * 3] - rgb[(y * width + x + 1) * 3] - rgb[((y - 1) * width + x) * 3] - rgb[((y + 1) * width + x) * 3];
                sum += lap;
                sumSq += (double)lap * lap;
                n++;
            }
        }
        double mean = sum / n;
        double variance = sumSq / n - mean * mean;
        double exposure = 1 - Math.Min(0.6, (double)clipped / n);
        return variance * exposure;
    }
}
