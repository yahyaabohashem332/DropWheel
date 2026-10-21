using ImageMagick;
using Petal.Core.Formats;
using F = Petal.Core.Formats.Formats;

namespace Petal.Engines.Imaging;

/// <summary>EXIF orientation values: how the stored pixels must be transformed for display.</summary>
public enum ImageOrientation
{
    Normal = 1,
    MirrorHorizontal = 2,
    Rotate180 = 3,
    MirrorVertical = 4,
    MirrorHorizontalRotate270 = 5,
    Rotate90 = 6,
    MirrorHorizontalRotate90 = 7,
    Rotate270 = 8,
}

/// <summary>Basic facts about an image file, read without decoding the pixels.</summary>
/// <param name="Width">Stored width in pixels (before orientation is applied).</param>
/// <param name="Height">Stored height in pixels (before orientation is applied).</param>
/// <param name="FormatName">The decoder's name for the format, e.g. "JPEG", useful when <paramref name="Format"/> is null.</param>
/// <param name="FrameCount">Pages, frames or icon sizes in the file.</param>
public sealed record ImageInfo(
    string Path,
    FileFormat? Format,
    string FormatName,
    int Width,
    int Height,
    long FileSize,
    double DpiX,
    double DpiY,
    bool HasAlpha,
    ImageOrientation Orientation,
    int FrameCount,
    string ColorSpace,
    int BitDepth,
    string? ColorProfile)
{
    /// <summary>The orientation swaps width and height.</summary>
    public bool IsQuarterTurn => Orientation >= ImageOrientation.MirrorHorizontalRotate270;

    /// <summary>Width as displayed, after orientation (the coordinate space all tools use).</summary>
    public int DisplayWidth => IsQuarterTurn ? Height : Width;

    public int DisplayHeight => IsQuarterTurn ? Width : Height;
}

public static class ImageInspector
{
    /// <summary>A picture's real format, from its first bytes (and, for camera RAW and Illustrator files, its extension).</summary>
    public static FileFormat? DetectFormat(string path) => ImageFormats.Detect(path);

    /// <summary>Reads dimensions, format, size, resolution, alpha and orientation of an image.</summary>
    public static ImageInfo Read(string path)
    {
        var format = ImageFormats.Detect(path);
        long size = new FileInfo(path).Length;

        if (format == F.Svg)
        {
            var svg = new MagickImageInfo(path, new MagickReadSettings { Format = MagickFormat.Svg, Density = new Density(96) });
            return new ImageInfo(path, format, "SVG", (int)svg.Width, (int)svg.Height, size, 96, 96, true,
                ImageOrientation.Normal, 1, "sRGB", 8, null);
        }

        // Streams carry no file name, so name the decoder (ICO has no magic number ImageMagick recognises).
        var settings = format is not null ? new MagickReadSettings { Format = ImageFormats.ToMagick(format) } : new MagickReadSettings();
        using var frames = new MagickImageCollection();
        using (var stream = ImageLoader.OpenRead(path))
            frames.Ping(stream, settings);
        if (frames.Count == 0)
            throw new InvalidDataException("Not an image: " + path);
        // Icons: describe the largest size; everything else: the first page or frame.
        var image = format == F.Ico ? frames.OrderByDescending(f => (long)f.Width * f.Height).First() : frames[0];

        var density = image.Density.Units == DensityUnit.PixelsPerCentimeter ? image.Density.ChangeUnits(DensityUnit.PixelsPerInch) : image.Density;
        var orientation = image.Orientation is Orientation.Undefined ? ImageOrientation.Normal : (ImageOrientation)(int)image.Orientation;
        var icc = image.GetColorProfile();
        return new ImageInfo(
            path,
            format ?? ImageFormats.FromMagick(image.Format),
            image.Format.ToString().ToUpperInvariant(),
            (int)image.Width,
            (int)image.Height,
            size,
            density.X,
            density.Y,
            image.HasAlpha,
            orientation,
            frames.Count,
            image.ColorSpace.ToString(),
            (int)image.Depth,
            icc is null ? null : icc.Description ?? "Embedded profile");
    }

    public static Task<ImageInfo> ReadAsync(string path, CancellationToken cancellationToken = default) =>
        Task.Run(() => Read(path), cancellationToken);
}
