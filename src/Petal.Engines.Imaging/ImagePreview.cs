using ImageMagick;

namespace Petal.Engines.Imaging;

/// <summary>
/// Fast, display-ready decodes of any supported image (including HEIC, AVIF, SVG and ICO that
/// WPF cannot read), upright and converted to sRGB.
/// </summary>
public static class ImagePreview
{
    /// <summary>Decodes <paramref name="path"/> scaled to fit <paramref name="maxSize"/> × <paramref name="maxSize"/> (never enlarged, except SVG which renders at that size).</summary>
    public static Task<BgraBitmap> DecodeBgraAsync(string path, int maxSize, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            using var image = Decode(path, maxSize, cancellationToken);
            return ToBgra(image);
        }, cancellationToken);

    /// <summary>Same as <see cref="DecodeBgraAsync"/> but returns PNG bytes, e.g. for a <c>BitmapImage</c> stream source.</summary>
    public static Task<byte[]> DecodePngAsync(string path, int maxSize, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            using var image = Decode(path, maxSize, cancellationToken);
            image.Settings.SetDefine(MagickFormat.Png, "compression-level", "1");
            return image.ToByteArray(MagickFormat.Png);
        }, cancellationToken);

    internal static MagickImage Decode(string path, int maxSize, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxSize, 1);
        var format = ImageFormats.Detect(path);
        var options = new LoadOptions
        {
            DecodeSizeHint = maxSize,
            SvgDensity = 1, // the minimum; SvgMinLongEdge then sets the exact size
            SvgMinLongEdge = maxSize,
            // Previews may use an EPS's built-in picture, and draw Illustrator files at screen resolution.
            AllowEpsPreview = true,
            DrawingDpi = 96,
        };
        var image = ImageLoader.Load(path, format, options);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var icc = image.GetColorProfile();
            if (image.Width > maxSize || image.Height > maxSize)
            {
                // Thumbnail() is much faster than Resize() but drops profiles; keep the ICC for the conversion below.
                image.Thumbnail(new MagickGeometry((uint)maxSize, (uint)maxSize));
                if (icc is not null)
                    image.SetProfile(icc);
            }
            // WPF shows buffers as sRGB; convert wide-gamut (Display P3, Adobe RGB) photos so colours are right.
            if (icc is not null && icc.ColorSpace == ColorSpace.sRGB && !IsSrgb(icc))
                image.TransformColorSpace(icc, ColorProfiles.SRGB);
            return image;
        }
        catch
        {
            image.Dispose();
            throw;
        }
    }

    internal static BgraBitmap ToBgra(MagickImage image)
    {
        using var pixels = image.GetPixels();
        var bytes = pixels.ToByteArray(PixelMapping.BGRA) ?? throw new InvalidOperationException("Could not read pixels");
        return new BgraBitmap((int)image.Width, (int)image.Height, bytes);
    }

    private static bool IsSrgb(IColorProfile profile) =>
        profile.Description?.Contains("sRGB", StringComparison.OrdinalIgnoreCase) ?? false;
}
