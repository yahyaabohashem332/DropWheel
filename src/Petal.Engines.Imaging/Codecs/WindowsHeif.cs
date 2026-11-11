using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using ImageMagick;
using Petal.Engines.Imaging.Heif;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Petal.Engines.Imaging;

/// <summary>
/// HEIC read by Windows' own decoder (WIC, with the HEIF Image Extensions and HEVC Video Extensions from the
/// Microsoft Store): the HEVC a HEIC photo is coded with is decoded by Windows, under Microsoft's licence, as
/// DropWheel carries no HEVC decoder. The EXIF and XMP come from the file itself.
/// </summary>
internal static class WindowsHeif
{
    internal const string MissingText = "Windows can't read this HEIC photo. The HEIF Image Extensions and HEVC Video Extensions from the Microsoft Store add it.";

    /// <summary>The photo upright and in sRGB, with its EXIF (orientation 1) and XMP.</summary>
    public static MagickImage Read(string path, int? sizeHint = null) =>
        Task.Run(() => ReadAsync(path, sizeHint)).GetAwaiter().GetResult();

    private static async Task<MagickImage> ReadAsync(string path, int? sizeHint)
    {
        var bytes = await File.ReadAllBytesAsync(path).ConfigureAwait(false);
        var metadata = HeifMetadata.Read(bytes);
        using var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(bytes.AsBuffer());
        stream.Seek(0);
        SoftwareBitmap bitmap;
        try
        {
            var decoder = await BitmapDecoder.CreateAsync(BitmapDecoder.HeifDecoderId, stream);
            var transform = new BitmapTransform();
            if (sizeHint is int hint and > 0 && Math.Max(decoder.OrientedPixelWidth, decoder.OrientedPixelHeight) > hint * 2)
            {
                // A preview: Windows scales while decoding. The scale is set on the stored picture, before turning.
                var scale = (double)hint / Math.Max(decoder.PixelWidth, decoder.PixelHeight);
                transform.ScaledWidth = (uint)Math.Max(1, Math.Round(decoder.PixelWidth * scale));
                transform.ScaledHeight = (uint)Math.Max(1, Math.Round(decoder.PixelHeight * scale));
                transform.InterpolationMode = BitmapInterpolationMode.Fant;
            }
            // HEIF turns a picture by its own properties (irot, imir), which Windows applies; an EXIF orientation
            // beside them only repeats them. With an ICC profile the pixels stay as they are, the profile with them;
            // otherwise Windows gives sRGB.
            bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight, transform,
                ExifOrientationMode.IgnoreExifOrientation, metadata.Icc is null ? ColorManagementMode.ColorManageToSRgb : ColorManagementMode.DoNotColorManage);
        }
        catch (Exception e) when (e is COMException or ArgumentException or InvalidCastException or NotSupportedException)
        {
            throw new InvalidOperationException(MissingText, e);
        }

        using (bitmap)
        {
            int width = bitmap.PixelWidth, height = bitmap.PixelHeight;
            var pixels = new byte[width * height * 4];
            bitmap.CopyToBuffer(pixels.AsBuffer());
            var opaque = true;
            for (var i = 3; i < pixels.Length && opaque; i += 4)
                opaque = pixels[i] == 255;
            var image = new MagickImage();
            if (opaque)
            {
                var bgr = new byte[width * height * 3];
                for (int i = 0, j = 0; i < pixels.Length; i += 4, j += 3)
                {
                    bgr[j] = pixels[i];
                    bgr[j + 1] = pixels[i + 1];
                    bgr[j + 2] = pixels[i + 2];
                }
                image.ReadPixels(bgr, new PixelReadSettings((uint)width, (uint)height, StorageType.Char, PixelMapping.BGR));
            }
            else
            {
                image.ReadPixels(pixels, new PixelReadSettings((uint)width, (uint)height, StorageType.Char, PixelMapping.BGRA));
            }
            image.ColorSpace = ColorSpace.sRGB;
            image.Format = MagickFormat.Heic;
            if (metadata.Icc is { } icc)
                image.SetProfile(new ColorProfile(icc));
            if (metadata.Exif is { } exif)
            {
                var profile = new ExifProfile(exif);
                // Windows has turned the pixels already.
                if (profile.GetValue(ExifTag.Orientation) is not null)
                    profile.SetValue(ExifTag.Orientation, (ushort)1);
                image.SetProfile(profile);
            }
            if (metadata.Xmp is { } xmp)
                image.SetProfile(new XmpProfile(xmp));
            image.Orientation = Orientation.TopLeft;
            return image;
        }
    }
}
