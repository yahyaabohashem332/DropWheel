using System.Runtime.InteropServices.WindowsRuntime;
using SkiaSharp;

namespace Petal.Engines.Documents.Util;

internal readonly record struct NormalizedImage(byte[] Bytes, string MediaType, int Width, int Height);

/// <summary>Image sniffing, decoding and encoding shared by every reader and writer.</summary>
internal static class ImageTools
{
    public static bool IsPng(ReadOnlySpan<byte> b) => b.Length > 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47;

    public static bool IsJpeg(ReadOnlySpan<byte> b) => b.Length > 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF;

    private static bool IsTiff(ReadOnlySpan<byte> b) =>
        b.Length > 4 && ((b[0] == 0x49 && b[1] == 0x49 && b[2] == 0x2A) || (b[0] == 0x4D && b[1] == 0x4D && b[3] == 0x2A));

    /// <summary>Keeps PNG and JPEG as they are (only reading their size); converts anything else Skia or WIC can decode to PNG.</summary>
    public static NormalizedImage? Normalize(byte[] bytes)
    {
        if (bytes.Length < 8)
            return null;
        try
        {
            if (IsPng(bytes) || IsJpeg(bytes))
            {
                using var codec = SKCodec.Create(new SKMemoryStream(bytes));
                if (codec is not null)
                {
                    // JPEGs in CMYK or with odd orientation still embed fine everywhere we write them.
                    return new NormalizedImage(bytes, IsPng(bytes) ? "image/png" : "image/jpeg", codec.Info.Width, codec.Info.Height);
                }
            }

            using (var bitmap = SKBitmap.Decode(bytes))
            {
                if (bitmap is not null)
                    return new NormalizedImage(EncodePng(bitmap), "image/png", bitmap.Width, bitmap.Height);
            }

            if (IsTiff(bytes))
                return DecodeWithWic(bytes);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Undecodable pictures are dropped by the callers.
        }
        return null;
    }

    public static byte[] EncodePng(SKBitmap bitmap)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    public static byte[] EncodeJpeg(SKBitmap bitmap, int quality)
    {
        var source = bitmap;
        SKBitmap? flattened = null;
        if (bitmap.AlphaType != SKAlphaType.Opaque)
        {
            // JPEG has no alpha: composite on white rather than letting it turn black.
            flattened = new SKBitmap(new SKImageInfo(bitmap.Width, bitmap.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
            using var canvas = new SKCanvas(flattened);
            using var sourceImage = SKImage.FromBitmap(bitmap);
            canvas.Clear(SKColors.White);
            canvas.DrawImage(sourceImage, 0, 0, new SKSamplingOptions(SKFilterMode.Linear));
            source = flattened;
        }
        try
        {
            using var image = SKImage.FromBitmap(source);
            using var data = image.Encode(SKEncodedImageFormat.Jpeg, Math.Clamp(quality, 1, 100));
            return data.ToArray();
        }
        finally
        {
            flattened?.Dispose();
        }
    }

    public static void Save(SKBitmap bitmap, string path, bool jpeg, int quality) =>
        File.WriteAllBytes(path, jpeg ? EncodeJpeg(bitmap, quality) : EncodePng(bitmap));

    public static string MediaTypeFor(string extension) => extension.TrimStart('.').ToLowerInvariant() switch
    {
        "jpg" or "jpeg" or "jpe" or "jfif" => "image/jpeg",
        "png" => "image/png",
        "gif" => "image/gif",
        "svg" => "image/svg+xml",
        "webp" => "image/webp",
        "bmp" => "image/bmp",
        "tif" or "tiff" => "image/tiff",
        _ => "application/octet-stream",
    };

    private static NormalizedImage? DecodeWithWic(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        var decoder = Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream.AsRandomAccessStream()).AsTask().GetAwaiter().GetResult();
        using var soft = decoder.GetSoftwareBitmapAsync(
            Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
            Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied).AsTask().GetAwaiter().GetResult();
        var pixels = new byte[soft.PixelWidth * soft.PixelHeight * 4];
        soft.CopyToBuffer(pixels.AsBuffer());
        using var bitmap = new SKBitmap(new SKImageInfo(soft.PixelWidth, soft.PixelHeight, SKColorType.Bgra8888, SKAlphaType.Premul));
        System.Runtime.InteropServices.Marshal.Copy(pixels, 0, bitmap.GetPixels(), pixels.Length);
        return new NormalizedImage(EncodePng(bitmap), "image/png", bitmap.Width, bitmap.Height);
    }
}
