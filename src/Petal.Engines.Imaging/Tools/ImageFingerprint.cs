using System.Numerics;
using ImageMagick;
using Petal.Core.Formats;

namespace Petal.Engines.Imaging;

/// <param name="Hash">64-bit difference hash of the upright picture.</param>
public sealed record ImagePrint(ulong Hash, int Width, int Height)
{
    public double Aspect => Height == 0 ? 0 : (double)Width / Height;
}

/// <summary>
/// A short fingerprint of what a picture looks like (a difference hash): the same photo resized,
/// re-saved as another format or lightly edited keeps nearly the same 64 bits.
/// </summary>
public static class ImageFingerprint
{
    /// <summary>The picture's fingerprint, or null when it can't be read.</summary>
    public static ImagePrint? Compute(string path)
    {
        try
        {
            var format = ImageFormats.Detect(path);
            MagickImage image;
            if (format == Formats.Jpg)
            {
                // JPEGs decode at an eighth of their size or less: plenty for 9 × 8 pixels.
                var settings = new MagickReadSettings();
                settings.SetDefine(MagickFormat.Jpeg, "size", "128x128");
                image = new MagickImage(path, settings);
                var info = new MagickImageInfo(path);
                using (image)
                {
                    image.AutoOrient();
                    // The reduced decode keeps the proportions: turned upright, the long side shows which way.
                    bool turned = image.Width > image.Height != info.Width > info.Height;
                    return Of(image, (int)(turned ? info.Height : info.Width), (int)(turned ? info.Width : info.Height));
                }
            }
            using (image = ImageLoader.Load(path, format))
            {
                image.AutoOrient();
                return Of(image, (int)image.Width, (int)image.Height);
            }
        }
        catch (Exception ex) when (ex is MagickException or IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException)
        {
            return null;
        }
    }

    private static ImagePrint Of(MagickImage image, int width, int height)
    {
        ImageWriter.FlattenOnWhite(image);
        image.ColorType = ColorType.Grayscale;
        image.FilterType = FilterType.Box;
        image.Resize(new MagickGeometry(9, 8) { IgnoreAspectRatio = true });
        var gray = image.GetPixels().ToByteArray(PixelMapping.RGB) ?? [];
        ulong hash = 0;
        int bit = 0;
        for (int y = 0; y < 8; y++)
        {
            for (int x = 0; x < 8; x++)
            {
                int left = gray[(y * 9 + x) * 3], right = gray[(y * 9 + x + 1) * 3];
                if (left > right)
                    hash |= 1UL << bit;
                bit++;
            }
        }
        return new ImagePrint(hash, width, height);
    }

    /// <summary>How many of the 64 bits differ: 0 is the same picture, up to about 8 a near copy.</summary>
    public static int Distance(ulong a, ulong b) => BitOperations.PopCount(a ^ b);
}
