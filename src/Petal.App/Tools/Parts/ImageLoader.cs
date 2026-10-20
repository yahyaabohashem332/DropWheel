using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ImageMagick;

namespace Petal.App.Tools.Parts;

/// <summary>
/// Loads any image Petal supports as a WPF bitmap: WPF's own decoders first (fast, colour-managed),
/// then ImageMagick for HEIC, AVIF, WEBP, SVG and anything Windows lacks a codec for.
/// EXIF orientation is applied so pixels match what people see.
/// </summary>
public static class ImageLoader
{
    public static Task<BitmapSource> LoadAsync(string path, int maxSide = 0) => Task.Run(() => Load(path, maxSide));

    public static BitmapSource Load(string path, int maxSide = 0)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is ".jpg" or ".jpeg" or ".jfif" or ".png" or ".bmp" or ".gif" or ".tif" or ".tiff" or ".ico")
        {
            try
            {
                return LoadWithWpf(path, maxSide);
            }
            catch
            {
                // Fall through to ImageMagick.
            }
        }
        return LoadWithMagick(path, maxSide);
    }

    private static BitmapSource LoadWithWpf(string path, int maxSide)
    {
        using var stream = File.OpenRead(path);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        BitmapSource frame = decoder.Frames[0];
        frame = Orient(frame, ReadOrientation(decoder.Frames[0]));
        if (maxSide > 0 && Math.Max(frame.PixelWidth, frame.PixelHeight) > maxSide)
        {
            double s = (double)maxSide / Math.Max(frame.PixelWidth, frame.PixelHeight);
            frame = new TransformedBitmap(frame, new ScaleTransform(s, s));
        }
        var normal = Normalize(frame);
        normal.Freeze();
        return normal;
    }

    private static BitmapSource LoadWithMagick(string path, int maxSide)
    {
        var settings = new MagickReadSettings();
        if (Path.GetExtension(path).Equals(".svg", StringComparison.OrdinalIgnoreCase))
        {
            settings.Density = new Density(144);
            settings.BackgroundColor = MagickColors.Transparent;
        }
        using var image = new MagickImage(path, settings);
        image.AutoOrient();
        if (maxSide > 0 && Math.Max(image.Width, image.Height) > maxSide)
            image.Resize(new MagickGeometry((uint)maxSide, (uint)maxSide));
        int w = (int)image.Width, h = (int)image.Height;
        using var pixels = image.GetPixelsUnsafe();
        var bytes = pixels.ToByteArray(PixelMapping.BGRA) ?? throw new InvalidDataException("Could not decode the image.");
        var bitmap = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, bytes, w * 4);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>96-DPI BGRA so layout units equal pixels everywhere.</summary>
    public static BitmapSource Normalize(BitmapSource source)
    {
        var converted = source.Format == PixelFormats.Bgra32 ? source : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        int stride = converted.PixelWidth * 4;
        var buffer = new byte[stride * converted.PixelHeight];
        converted.CopyPixels(buffer, stride, 0);
        return BitmapSource.Create(converted.PixelWidth, converted.PixelHeight, 96, 96, PixelFormats.Bgra32, null, buffer, stride);
    }

    private static int ReadOrientation(BitmapFrame frame)
    {
        try
        {
            if (frame.Metadata is BitmapMetadata meta && meta.GetQuery("System.Photo.Orientation") is ushort o)
                return o;
        }
        catch
        {
            // No readable metadata.
        }
        return 1;
    }

    private static BitmapSource Orient(BitmapSource source, int orientation) => orientation switch
    {
        3 => new TransformedBitmap(source, new RotateTransform(180)),
        6 => new TransformedBitmap(source, new RotateTransform(90)),
        8 => new TransformedBitmap(source, new RotateTransform(270)),
        2 => new TransformedBitmap(source, new ScaleTransform(-1, 1)),
        4 => new TransformedBitmap(source, new ScaleTransform(1, -1)),
        5 => new TransformedBitmap(new TransformedBitmap(source, new RotateTransform(90)), new ScaleTransform(-1, 1)),
        7 => new TransformedBitmap(new TransformedBitmap(source, new RotateTransform(270)), new ScaleTransform(-1, 1)),
        _ => source,
    };

    /// <summary>Renders a visual at an exact pixel size.</summary>
    public static BitmapSource Render(Visual visual, int width, int height)
    {
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    public static void SavePng(BitmapSource image, string path)
    {
        var temp = path + ".petal-tmp";
        using (var stream = File.Create(temp))
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            encoder.Save(stream);
        }
        File.Move(temp, path);
    }

    /// <summary>Saves a rendered bitmap in the format of <paramref name="path"/> (JPG, WEBP, HEIC… via ImageMagick).</summary>
    public static void Save(BitmapSource image, string path, int quality = 92)
    {
        if (Path.GetExtension(path).Equals(".png", StringComparison.OrdinalIgnoreCase))
        {
            SavePng(image, path);
            return;
        }
        var bgra = Normalize(image);
        int w = bgra.PixelWidth, h = bgra.PixelHeight;
        var pixels = new byte[w * h * 4];
        bgra.CopyPixels(pixels, w * 4, 0);
        using var magick = new MagickImage(pixels, new PixelReadSettings((uint)w, (uint)h, StorageType.Char, PixelMapping.BGRA));
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is ".jpg" or ".jpeg" or ".bmp")
        {
            magick.BackgroundColor = MagickColors.White;
            magick.Alpha(AlphaAction.Remove);
        }
        magick.Quality = (uint)quality;
        var temp = path + ".petal-tmp";
        magick.Write(temp, FormatFor(ext));
        File.Move(temp, path);
    }

    private static MagickFormat FormatFor(string ext) => ext switch
    {
        ".jpg" or ".jpeg" or ".jfif" => MagickFormat.Jpeg,
        ".webp" => MagickFormat.WebP,
        ".heic" or ".heif" => MagickFormat.Heic,
        ".avif" => MagickFormat.Avif,
        ".tif" or ".tiff" => MagickFormat.Tiff,
        ".bmp" => MagickFormat.Bmp,
        ".ico" => MagickFormat.Ico,
        _ => MagickFormat.Png,
    };
}
