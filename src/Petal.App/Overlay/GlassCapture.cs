using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using static Petal.App.Interop.NativeMethods;

namespace Petal.App.Overlay;

/// <summary>
/// Frosted glass without compositor tricks: grab the pixels behind the wheel just before
/// it appears, then blur and lift their saturation on the CPU. Works on Windows 10 and 11.
/// </summary>
public static class GlassCapture
{
    /// <summary>Captures a screen rectangle (physical pixels) and returns a blurred image at half resolution.</summary>
    public static BitmapSource? CaptureBlurred(int left, int top, int width, int height, double scale)
    {
        if (width <= 0 || height <= 0)
            return null;

        var pixels = CaptureScreen(left, top, width, height);
        if (pixels is null)
            return null;

        // Work at half resolution: cheaper, and the upscale adds extra softness.
        int w = Math.Max(1, width / 2), h = Math.Max(1, height / 2);
        var small = Downsample(pixels, width, height, w, h);

        int radius = (int)Math.Round(9 * scale);
        for (int pass = 0; pass < 3; pass++)
        {
            BoxBlurHorizontal(small, w, h, radius);
            BoxBlurVertical(small, w, h, radius);
        }
        Saturate(small, 1.45f);

        var bitmap = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, small, w * 4);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>Applies the same frosting to an existing image (used for off-screen snapshots).</summary>
    public static BitmapSource Blur(BitmapSource source, double scale = 1)
    {
        var bgra = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        int width = bgra.PixelWidth, height = bgra.PixelHeight;
        var pixels = new byte[width * height * 4];
        bgra.CopyPixels(pixels, width * 4, 0);
        int w = Math.Max(1, width / 2), h = Math.Max(1, height / 2);
        var small = Downsample(pixels, width, height, w, h);
        int radius = (int)Math.Round(9 * scale);
        for (int pass = 0; pass < 3; pass++)
        {
            BoxBlurHorizontal(small, w, h, radius);
            BoxBlurVertical(small, w, h, radius);
        }
        Saturate(small, 1.45f);
        var bitmap = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, small, w * 4);
        bitmap.Freeze();
        return bitmap;
    }

    internal static byte[]? CaptureScreen(int left, int top, int width, int height)
    {
        IntPtr screen = GetDC(IntPtr.Zero);
        IntPtr memory = CreateCompatibleDC(screen);
        var header = new BITMAPINFOHEADER
        {
            biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = width,
            biHeight = -height, // top-down
            biPlanes = 1,
            biBitCount = 32,
        };
        IntPtr dib = CreateDIBSection(screen, ref header, 0, out var bits, IntPtr.Zero, 0);
        try
        {
            if (dib == IntPtr.Zero)
                return null;
            var old = SelectObject(memory, dib);
            BitBlt(memory, 0, 0, width, height, screen, left, top, SRCCOPY | CAPTUREBLT);
            SelectObject(memory, old);
            var buffer = new byte[width * height * 4];
            Marshal.Copy(bits, buffer, 0, buffer.Length);
            for (int i = 3; i < buffer.Length; i += 4)
                buffer[i] = 255;
            return buffer;
        }
        finally
        {
            if (dib != IntPtr.Zero)
                DeleteObject(dib);
            DeleteDC(memory);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    private static byte[] Downsample(byte[] src, int sw, int sh, int dw, int dh)
    {
        var dst = new byte[dw * dh * 4];
        Parallel.For(0, dh, y =>
        {
            int sy = Math.Min(sh - 1, y * 2);
            int sy2 = Math.Min(sh - 1, sy + 1);
            for (int x = 0; x < dw; x++)
            {
                int sx = Math.Min(sw - 1, x * 2), sx2 = Math.Min(sw - 1, sx + 1);
                int a = (sy * sw + sx) * 4, b = (sy * sw + sx2) * 4, c = (sy2 * sw + sx) * 4, d = (sy2 * sw + sx2) * 4;
                int o = (y * dw + x) * 4;
                for (int k = 0; k < 3; k++)
                    dst[o + k] = (byte)((src[a + k] + src[b + k] + src[c + k] + src[d + k] + 2) >> 2);
                dst[o + 3] = 255;
            }
        });
        return dst;
    }

    private static void BoxBlurHorizontal(byte[] px, int w, int h, int r)
    {
        if (r < 1) return;
        Parallel.For(0, h, y =>
        {
            var row = new byte[w * 4];
            Buffer.BlockCopy(px, y * w * 4, row, 0, w * 4);
            int window = r * 2 + 1;
            int sb = 0, sg = 0, sr = 0;
            for (int i = -r; i <= r; i++)
            {
                int x = Math.Clamp(i, 0, w - 1) * 4;
                sb += row[x]; sg += row[x + 1]; sr += row[x + 2];
            }
            for (int x = 0; x < w; x++)
            {
                int o = (y * w + x) * 4;
                px[o] = (byte)(sb / window); px[o + 1] = (byte)(sg / window); px[o + 2] = (byte)(sr / window);
                int add = Math.Min(w - 1, x + r + 1) * 4, sub = Math.Max(0, x - r) * 4;
                sb += row[add] - row[sub]; sg += row[add + 1] - row[sub + 1]; sr += row[add + 2] - row[sub + 2];
            }
        });
    }

    private static void BoxBlurVertical(byte[] px, int w, int h, int r)
    {
        if (r < 1) return;
        Parallel.For(0, w, x =>
        {
            var col = new byte[h * 4];
            for (int y = 0; y < h; y++)
                Buffer.BlockCopy(px, (y * w + x) * 4, col, y * 4, 4);
            int window = r * 2 + 1;
            int sb = 0, sg = 0, sr = 0;
            for (int i = -r; i <= r; i++)
            {
                int y = Math.Clamp(i, 0, h - 1) * 4;
                sb += col[y]; sg += col[y + 1]; sr += col[y + 2];
            }
            for (int y = 0; y < h; y++)
            {
                int o = (y * w + x) * 4;
                px[o] = (byte)(sb / window); px[o + 1] = (byte)(sg / window); px[o + 2] = (byte)(sr / window);
                int add = Math.Min(h - 1, y + r + 1) * 4, sub = Math.Max(0, y - r) * 4;
                sb += col[add] - col[sub]; sg += col[add + 1] - col[sub + 1]; sr += col[add + 2] - col[sub + 2];
            }
        });
    }

    private static void Saturate(byte[] px, float amount)
    {
        Parallel.For(0, px.Length / 4, i =>
        {
            int o = i * 4;
            float b = px[o], g = px[o + 1], r = px[o + 2];
            float l = 0.2126f * r + 0.7152f * g + 0.0722f * b;
            px[o] = (byte)Math.Clamp(l + (b - l) * amount, 0, 255);
            px[o + 1] = (byte)Math.Clamp(l + (g - l) * amount, 0, 255);
            px[o + 2] = (byte)Math.Clamp(l + (r - l) * amount, 0, 255);
        });
    }
}
