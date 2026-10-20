using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Petal.App.Tools.Parts;
using Petal.Engines.Imaging;

namespace Petal.Tests;

public class StampRendererTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("petal-stamp-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static byte[] Pixels(byte[] png, out int width, out int height)
    {
        var frame = BitmapDecoder.Create(new MemoryStream(png), BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
        var bgra = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        width = bgra.PixelWidth;
        height = bgra.PixelHeight;
        var pixels = new byte[width * height * 4];
        bgra.CopyPixels(pixels, width * 4, 0);
        return pixels;
    }

    /// <summary>How many pixels in a region (fractions of the picture) are visibly drawn on.</summary>
    private static int Inked(byte[] pixels, int width, int height, double x0, double y0, double x1, double y1, Func<byte, byte, byte, byte, bool> drawn)
    {
        int n = 0;
        for (int y = (int)(height * y0); y < (int)(height * y1); y++)
            for (int x = (int)(width * x0); x < (int)(width * x1); x++)
            {
                int i = (y * width + x) * 4;
                if (drawn(pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3])) n++;
            }
        return n;
    }

    [Fact]
    public async Task Renders_from_a_background_thread_at_the_asked_size()
    {
        var spec = new StampSpec { Text = "© مرحبا 2026", Place = Place9.BottomRight, Opacity = 1 };
        // Engines call the overlay from the thread pool, which WPF drawing can't use directly.
        var png = await Task.Run(() => StampRenderer.RenderPng(800, 600, spec));
        var pixels = Pixels(png, out var w, out var h);
        Assert.Equal((800, 600), (w, h));
        static bool Opaque(byte b, byte g, byte r, byte a) => a > 128;
        Assert.True(Inked(pixels, w, h, 0.55, 0.75, 1, 1, Opaque) > 200, "text in the bottom-right corner");
        Assert.Equal(0, Inked(pixels, w, h, 0, 0, 0.5, 0.5, Opaque));
    }

    [Fact]
    public void Large_overlays_can_be_drawn_smaller()
    {
        var png = StampRenderer.RenderPng(8000, 4000, new StampSpec { Text = "Petal" }, maxSide: 2000);
        Pixels(png, out var w, out var h);
        Assert.Equal((2000, 1000), (w, h));
    }

    [Fact]
    public void Repeating_covers_the_whole_picture()
    {
        var png = StampRenderer.RenderPng(900, 600, new StampSpec { Text = "SAMPLE", Tiled = true, Size = 0.2, Opacity = 1 });
        var pixels = Pixels(png, out var w, out var h);
        static bool Opaque(byte b, byte g, byte r, byte a) => a > 128;
        foreach (var (x, y) in new[] { (0.0, 0.0), (0.5, 0.0), (0.0, 0.5), (0.5, 0.5) })
            Assert.True(Inked(pixels, w, h, x, y, x + 0.5, y + 0.5, Opaque) > 100, $"quarter at {x},{y}");
    }

    [Fact]
    public void A_picture_keeps_its_proportions()
    {
        var logo = new WriteableBitmap(200, 100, 96, 96, PixelFormats.Bgra32, null);
        logo.WritePixels(new Int32Rect(0, 0, 200, 100), Enumerable.Repeat(new byte[] { 0, 0, 255, 255 }, 200 * 100).SelectMany(p => p).ToArray(), 800, 0);
        logo.Freeze();
        var png = StampRenderer.RenderPng(1000, 1000, new StampSpec { Picture = logo, Size = 0.4, Place = Place9.Center, Opacity = 1 });
        var pixels = Pixels(png, out var w, out var h);
        static bool Red(byte b, byte g, byte r, byte a) => r > 200 && a > 200;
        // 40% of 1000 wide → 400 × 200 in the middle.
        Assert.True(Inked(pixels, w, h, 0.31, 0.41, 0.69, 0.59, Red) > 0.9 * 380 * 180);
        Assert.Equal(0, Inked(pixels, w, h, 0.31, 0.2, 0.69, 0.38, Red));
    }

    [Fact]
    public async Task Watermarks_a_photo_end_to_end()
    {
        var input = Path.Combine(_dir, "photo.png");
        var gray = new WriteableBitmap(640, 480, 96, 96, PixelFormats.Bgra32, null);
        gray.WritePixels(new Int32Rect(0, 0, 640, 480), Enumerable.Repeat(new byte[] { 90, 90, 90, 255 }, 640 * 480).SelectMany(p => p).ToArray(), 640 * 4, 0);
        using (var file = File.Create(input))
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(gray));
            encoder.Save(file);
        }
        var output = Path.Combine(_dir, "photo Watermarked.png");
        var spec = new StampSpec { Text = "© 2026", Color = Colors.White, Opacity = 1, Place = Place9.TopLeft };
        await ImageOverlay.CompositeAsync(input, output, (w, h) => StampRenderer.RenderPng(w, h, spec, maxSide: 4096));

        var pixels = Pixels(File.ReadAllBytes(output), out var pw, out var ph);
        static bool Light(byte b, byte g, byte r, byte a) => r > 200 && g > 200 && b > 200;
        Assert.True(Inked(pixels, pw, ph, 0, 0, 0.4, 0.25, Light) > 300, "white text top-left");
        Assert.Equal(0, Inked(pixels, pw, ph, 0.5, 0.5, 1, 1, Light));
    }
}
