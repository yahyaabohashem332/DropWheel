using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Petal.App.Wheels;

namespace Petal.Tests;

/// <summary>
/// What a screenshot puts on the clipboard (checked without touching the real one): the picture as a
/// bitmap, for any app, and as PNG, for browsers and Office, both whole.
/// </summary>
public sealed class ScreenshotClipboardTests
{
    [Fact]
    public void A_screenshot_goes_on_the_clipboard_as_a_bitmap_and_as_PNG()
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var pixels = new byte[40 * 30 * 4];
                for (int i = 0; i < pixels.Length; i += 4)
                    (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]) = (0x30, 0x80, 0xE0, 0xFF);
                var picture = BitmapSource.Create(40, 30, 96, 96, PixelFormats.Bgra32, null, pixels, 40 * 4);
                picture.Freeze();

                var data = Screenshots.ClipboardData(picture);
                var image = Assert.IsAssignableFrom<BitmapSource>(data.GetData(DataFormats.Bitmap));
                Assert.Equal((40, 30), (image.PixelWidth, image.PixelHeight));

                var png = Assert.IsAssignableFrom<Stream>(data.GetData("PNG"));
                Assert.Equal(0, png.Position);
                var decoded = new PngBitmapDecoder(png, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
                Assert.Equal((40, 30), (decoded.PixelWidth, decoded.PixelHeight));
                var back = new byte[4];
                new FormatConvertedBitmap(decoded, PixelFormats.Bgra32, null, 0).CopyPixels(new Int32Rect(10, 10, 1, 1), back, 4, 0);
                Assert.Equal(new byte[] { 0x30, 0x80, 0xE0, 0xFF }, back);
            }
            catch (Exception e) { failure = ExceptionDispatchInfo.Capture(e); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        failure?.Throw();
    }
}
