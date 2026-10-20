using ImageMagick;
using Petal.Core.Formats;

namespace Petal.Engines.Imaging;

/// <summary>Options for saving a composed bitmap (Annotate, Add Background, Collage).</summary>
public sealed record ImageSaveOptions
{
    /// <summary>JPEG / WEBP / HEIC / AVIF quality, 1–100.</summary>
    public int Quality { get; init; } = 92;

    /// <summary>Copy EXIF/XMP/IPTC (and the colour profile, see <see cref="CopyColorProfile"/>) from this image.</summary>
    public string? MetadataSourcePath { get; init; }

    /// <summary>
    /// Also copy the source's ICC profile. Leave on when the bitmap holds the source's pixel values
    /// unconverted; turn off when the UI rendered it to sRGB.
    /// </summary>
    public bool CopyColorProfile { get; init; } = true;
}

public static class ImageSaver
{
    /// <summary>
    /// Saves a BGRA32 buffer to <paramref name="outputPath"/> as <paramref name="format"/> (any raster
    /// format or PDF). Fully opaque buffers are saved without an alpha channel; formats without
    /// transparency get a white background. The file must not already exist.
    /// </summary>
    public static Task SaveAsync(BgraBitmap bitmap, string outputPath, FileFormat format, ImageSaveOptions? options = null,
        CancellationToken cancellationToken = default) =>
        Task.Run(async () =>
        {
            options ??= new ImageSaveOptions();
            if (!ImageFormats.CanWrite(format))
                throw new NotSupportedException($"Cannot write {format.Label} images");

            using var image = FromBgra(bitmap);
            if (options.MetadataSourcePath is { } source && File.Exists(source))
            {
                using var donor = new MagickImage();
                using (var stream = ImageLoader.OpenRead(source))
                    donor.Ping(stream);
                MetadataHygiene.CopyProfiles(donor, image, options.CopyColorProfile);
            }
            cancellationToken.ThrowIfCancellationRequested();
            await ImageWriter.WriteAsync(image, outputPath, format, new ImageWriteOptions { Quality = options.Quality }, cancellationToken);
        }, cancellationToken);

    internal static MagickImage FromBgra(BgraBitmap bitmap)
    {
        var image = new MagickImage();
        image.ReadPixels(bitmap.Pixels, new PixelReadSettings((uint)bitmap.Width, (uint)bitmap.Height, StorageType.Char, PixelMapping.BGRA));
        image.ColorSpace = ColorSpace.sRGB;
        if (image.IsOpaque)
            image.Alpha(AlphaAction.Off);
        return image;
    }
}
