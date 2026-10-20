using ImageMagick;
using ImageMagick.Formats;
using Petal.Core.Formats;
using F = Petal.Core.Formats.Formats;

namespace Petal.Engines.Imaging;

internal sealed record LoadOptions
{
    public static LoadOptions Default { get; } = new();

    /// <summary>Rotate/flip the pixels per EXIF orientation and reset the tag to 1.</summary>
    public bool AutoOrient { get; init; } = true;

    /// <summary>Convert CMYK/Lab and other non-RGB colour spaces to sRGB.</summary>
    public bool ConvertToSrgb { get; init; } = true;

    /// <summary>Resolution SVGs are rasterised at (96 is the SVG's own pixel size).</summary>
    public double SvgDensity { get; init; } = 150;

    /// <summary>Rasterise SVGs at least this large on the long edge (e.g. 256 for icons).</summary>
    public int SvgMinLongEdge { get; init; }

    /// <summary>When set, decoders may return a smaller image (JPEG DCT scaling) no smaller than this box.</summary>
    public int? DecodeSizeHint { get; init; }
}

/// <summary>Decodes any supported image into a single, upright, RGB <see cref="MagickImage"/>.</summary>
internal static class ImageLoader
{
    private const int SvgMaxLongEdge = 8192;

    public static MagickImage Load(string path, LoadOptions? options = null) =>
        Load(path, ImageFormats.Detect(path), options);

    public static MagickImage Load(string path, FileFormat? format, LoadOptions? options = null)
    {
        options ??= LoadOptions.Default;
        if (!File.Exists(path))
            throw new FileNotFoundException("Image not found", path);

        MagickImage image;
        if (format == F.Ico)
            image = LoadLargestFrame(path);
        else if (format == F.Svg)
            image = new MagickImage(path, CreateReadSettings(path, format, options)); // relative hrefs need the path
        else
        {
            // Read from a stream: given a path, ImageMagick lets the extension pick the decoder,
            // so a PNG saved as ".jpg" would fail.
            using var stream = OpenRead(path);
            image = new MagickImage(stream, CreateReadSettings(path, format, options));
            // ImageMagick keeps a TIFF's EXIF only as loose properties; rebuild a real profile.
            if (format == F.Tiff && image.GetExifProfile() is null && Metadata.TiffExif.Extract(stream) is { } exif)
                image.SetProfile(new ExifProfile(exif));
        }

        try
        {
            Normalize(image, options);
            return image;
        }
        catch
        {
            image.Dispose();
            throw;
        }
    }

    internal static MagickReadSettings CreateReadSettings(string path, FileFormat? format, LoadOptions options)
    {
        var settings = new MagickReadSettings();
        if (format is not null && format != F.Svg)
            settings.Format = ImageFormats.ToMagick(format);

        if (format == F.Svg)
        {
            settings.Format = MagickFormat.Svg;
            settings.BackgroundColor = MagickColors.Transparent;
            settings.Density = new Density(SvgDensityFor(path, options));
        }
        else if (format == F.Gif || format == F.Webp || format == F.Tiff || format == F.Avif)
        {
            // Animated WEBP/GIF and multi-page TIFF: decode only the first frame.
            settings.FrameIndex = 0;
            settings.FrameCount = 1;
        }

        if (format == F.Jpg && options.DecodeSizeHint is int hint and > 0)
            settings.Defines = new JpegReadDefines { Size = new MagickGeometry((uint)hint, (uint)hint) };
        return settings;
    }

    /// <summary>Density that renders the SVG at the requested scale, never beyond 8192 px.</summary>
    private static double SvgDensityFor(string path, LoadOptions options)
    {
        double density = options.SvgDensity;
        try
        {
            var info = new MagickImageInfo(path, new MagickReadSettings { Format = MagickFormat.Svg, Density = new Density(96) });
            double longEdge = Math.Max(info.Width, info.Height);
            if (longEdge > 0)
            {
                if (options.SvgMinLongEdge > 0)
                    density = Math.Max(density, 96.0 * options.SvgMinLongEdge / longEdge);
                density = Math.Min(density, 96.0 * SvgMaxLongEdge / longEdge);
            }
        }
        catch (MagickException)
        {
            // Unknown intrinsic size: keep the requested density.
        }
        return Math.Clamp(density, 1, 2400);
    }

    internal static FileStream OpenRead(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);

    /// <summary>Icons hold several sizes; the biggest (then deepest) one is the master.</summary>
    private static MagickImage LoadLargestFrame(string path)
    {
        using var stream = OpenRead(path);
        using var frames = new MagickImageCollection(stream, new MagickReadSettings { Format = MagickFormat.Ico });
        var best = frames
            .OrderByDescending(f => (long)f.Width * f.Height)
            .ThenByDescending(f => f.Depth)
            .First();
        return (MagickImage)best.Clone();
    }

    private static void Normalize(MagickImage image, LoadOptions options)
    {
        image.ResetPage();

        if (options.ConvertToSrgb)
            ToSrgb(image);

        if (options.AutoOrient)
            Orient(image);
    }

    internal static void ToSrgb(MagickImage image)
    {
        switch (image.ColorSpace)
        {
            case ColorSpace.sRGB or ColorSpace.RGB or ColorSpace.Gray or ColorSpace.LinearGray or ColorSpace.Undefined:
                return;
            case ColorSpace.CMYK:
                var profile = image.GetColorProfile();
                if (profile is not null && profile.ColorSpace == ColorSpace.CMYK)
                    image.TransformColorSpace(ColorProfiles.SRGB);
                else
                    image.TransformColorSpace(ColorProfiles.USWebCoatedSWOP, ColorProfiles.SRGB);
                image.ColorSpace = ColorSpace.sRGB;
                return;
            default:
                image.ColorSpace = ColorSpace.sRGB;
                return;
        }
    }

    /// <summary>Applies EXIF orientation to the pixels and drops the now-stale EXIF thumbnail.</summary>
    internal static void Orient(MagickImage image)
    {
        var orientation = image.Orientation;
        if (orientation is Orientation.Undefined or Orientation.TopLeft)
        {
            image.Orientation = Orientation.TopLeft;
            return;
        }
        image.AutoOrient();
        image.Orientation = Orientation.TopLeft;
        MetadataHygiene.AfterGeometryChange(image);
    }
}
