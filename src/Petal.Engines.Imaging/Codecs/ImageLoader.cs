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

    /// <summary>Resolution Illustrator and EPS drawings are rendered at.</summary>
    public int DrawingDpi { get; init; } = 300;

    /// <summary>Without Ghostscript, an EPS may give its small built-in preview instead of failing (for searching, not converting).</summary>
    public bool AllowEpsPreview { get; init; }
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
        else if (format == F.Psd || format == F.Psb)
            image = PhotoshopTools.LoadComposite(path, format);
        else if (format == F.Ai || format == F.Eps)
            image = LoadDrawing(path, format, options);
        else if (format == F.Svg)
            image = new MagickImage(path, CreateReadSettings(path, format, options)); // relative hrefs need the path
        else if (format == F.Heic)
            image = WindowsHeif.Read(path, options.DecodeSizeHint); // HEVC, decoded by Windows
        else if (format == F.Xcf)
            image = XcfFile.Load(path); // GIMP: its layers, flattened
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
        else if (format == F.Gif || format == F.Webp || format == F.Tiff || format == F.Avif || format == F.Jxl || format == F.Dds)
        {
            // Animated WEBP/GIF/JXL, multi-page TIFF and a texture's smaller copies (DDS mipmaps): only the first.
            settings.FrameIndex = 0;
            settings.FrameCount = 1;
        }

        if (format == F.Jpg && options.DecodeSizeHint is int hint and > 0)
            settings.Defines = new JpegReadDefines { Size = new MagickGeometry((uint)hint, (uint)hint) };

        if (format == F.Raw)
        {
            // As the camera saw it: its white balance, in sRGB. For previews, the JPEG the camera embedded.
            settings.Defines = new DngReadDefines
            {
                UseCameraWhiteBalance = true,
                OutputColor = DngOutputColor.SRGB,
                ReadThumbnail = options.DecodeSizeHint is > 0 and <= 2048 ? true : null,
            };
        }
        return settings;
    }

    /// <summary>
    /// Illustrator files with PDF content are drawn by the PDF engine; PostScript (EPS, old
    /// Illustrator) by Ghostscript when it is installed, else from the preview the file carries.
    /// </summary>
    private static MagickImage LoadDrawing(string path, FileFormat format, LoadOptions options)
    {
        Span<byte> head = stackalloc byte[8];
        using (var fs = OpenRead(path))
            head = head[..fs.ReadAtLeast(head, head.Length, throwOnEndOfStream: false)];
        if (head.StartsWith("%PDF-"u8))
        {
            var render = PageRasterizer.Render ?? throw new InvalidOperationException("Illustrator files need the PDF engine.");
            return new MagickImage(render(path, 0, options.DrawingDpi, true), new MagickReadSettings { Format = MagickFormat.Png });
        }
        if (Petal.Core.Util.Ghostscript.IsAvailable)
        {
            var png = Path.Combine(Path.GetTempPath(), "Petal", $"eps-{Guid.NewGuid():N}.png");
            Directory.CreateDirectory(Path.GetDirectoryName(png)!);
            try
            {
                Petal.Core.Util.Ghostscript.RenderPngAsync(path, png, options.DrawingDpi).GetAwaiter().GetResult();
                return new MagickImage(File.ReadAllBytes(png), new MagickReadSettings { Format = MagickFormat.Png });
            }
            finally
            {
                try { File.Delete(png); } catch (IOException) { }
            }
        }
        if (options.AllowEpsPreview && EpsFile.LoadPreview(path) is { } preview)
            return preview;
        throw new InvalidOperationException(Petal.Core.Util.Ghostscript.Needed);
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
