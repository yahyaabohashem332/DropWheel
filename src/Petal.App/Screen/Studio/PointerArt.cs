using System.IO;
using System.Runtime.InteropServices;
using Petal.Core.Recording;
using SkiaSharp;

namespace Petal.App.Screen.Studio;

/// <summary>A pointer's picture, drawn large so it stays sharp when the video zooms in, and the point of it that clicks.</summary>
internal sealed record PointerPicture(SKImage Image, double HotX, double HotY, double Nominal)
{
    /// <summary>How many of its pixels make one pixel of the pointer as Windows draws it at 100 %.</summary>
    public double PixelsPerPoint => Image.Height / Nominal;
}

/// <summary>
/// The pointers a cinematic recording draws: Windows' own shapes redrawn crisply (the arrow, the text cursor,
/// the resize arrows, the cross and the move arrows), the hand and the "unavailable" sign from Windows' own
/// large pictures, and any other shape as it was taken from the screen.
/// </summary>
internal sealed class PointerArt : IDisposable
{
    /// <summary>The pictures are drawn this many times larger than the pointer at 100 %.</summary>
    private const double Detail = 4;

    private readonly Dictionary<PointerKind, PointerPicture> _kinds = [];
    private readonly Dictionary<int, PointerPicture> _shapes = [];
    private readonly StudioProject? _project;

    public PointerArt(StudioProject? project = null) => _project = project;

    public PointerPicture For(PointerKind kind, int shape)
    {
        if (kind == PointerKind.Other && shape >= 0)
        {
            if (_shapes.TryGetValue(shape, out var taken))
                return taken;
            var made = Taken(shape);
            if (made is not null)
                return _shapes[shape] = made;
            kind = PointerKind.Arrow;
        }
        if (!_kinds.TryGetValue(kind, out var picture))
            _kinds[kind] = picture = Make(kind);
        return picture;
    }

    private PointerPicture? Taken(int shape)
    {
        if (_project is null || shape >= _project.Shapes.Count)
            return null;
        var s = _project.Shapes[shape];
        try
        {
            var image = SKImage.FromEncodedData(_project.PathOf(s.File));
            return image is null ? null : new PointerPicture(image, s.HotX, s.HotY, s.Height / Math.Max(1, _project.PixelScale));
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static PointerPicture Make(PointerKind kind)
    {
        if (kind is PointerKind.Hand or PointerKind.No && FromWindows(kind) is { } windows)
            return windows;
        int side = (int)(32 * Detail);
        var info = new SKImageInfo(side, side, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        canvas.Scale((float)Detail);
        // Shapes are drawn in the pointer's own points (32 a side at 100 %), the click point given in the same.
        (double hx, double hy) = kind switch
        {
            PointerKind.Text => DrawText(canvas),
            PointerKind.SizeWE => DrawDouble(canvas, 0),
            PointerKind.SizeNS => DrawDouble(canvas, 90),
            PointerKind.SizeNWSE => DrawDouble(canvas, 45),
            PointerKind.SizeNESW => DrawDouble(canvas, -45),
            PointerKind.Move => DrawMove(canvas),
            PointerKind.Cross => DrawCross(canvas),
            PointerKind.Wait => DrawWait(canvas, alone: true),
            PointerKind.Busy => DrawBusy(canvas),
            PointerKind.No => DrawNo(canvas),
            _ => DrawArrow(canvas, 0, 0),
        };
        return new PointerPicture(surface.Snapshot(), hx * Detail, hy * Detail, 32);
    }

    private static SKPaint Fill(SKColor colour) => new() { IsAntialias = true, Color = colour };

    private static SKPaint Edge(SKColor colour, float width) => new() { IsAntialias = true, Color = colour, Style = SKPaintStyle.Stroke, StrokeWidth = width, StrokeJoin = SKStrokeJoin.Round, StrokeCap = SKStrokeCap.Round };

    private static readonly SKColor Ink = new(0x16, 0x16, 0x1A);

    private static void Shadow(SKCanvas canvas, SKPath path)
    {
        using var shadow = new SKPaint { IsAntialias = true, Color = new SKColor(0, 0, 0, 80), MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 0.9f) };
        canvas.Save();
        canvas.Translate(0.6f, 1.1f);
        canvas.DrawPath(path, shadow);
        canvas.Restore();
    }

    /// <summary>The arrow, its tip at the click point.</summary>
    private static (double, double) DrawArrow(SKCanvas canvas, float x, float y)
    {
        using var path = SKPath.ParseSvgPathData("M1 1 L1 18.5 L5.3 14.4 L8.1 20.8 L10.9 19.6 L8.1 13.3 L13.9 13.3 Z");
        path.Offset(x, y);
        Shadow(canvas, path);
        using var fill = Fill(SKColors.White);
        using var edge = Edge(Ink, 1.2f);
        canvas.DrawPath(path, fill);
        canvas.DrawPath(path, edge);
        return (1 + x, 1 + y);
    }

    /// <summary>The text cursor: a dark I-beam with a light edge, so it shows on any page.</summary>
    private static (double, double) DrawText(SKCanvas canvas)
    {
        using var path = SKPath.ParseSvgPathData("M12.5 6.5 L14.5 6.5 L16 7.5 L17.5 6.5 L19.5 6.5 M16 7.5 L16 24.5 M12.5 25.5 L14.5 25.5 L16 24.5 L17.5 25.5 L19.5 25.5");
        using var halo = Edge(SKColors.White, 3.4f);
        using var line = Edge(Ink, 1.5f);
        canvas.DrawPath(path, halo);
        canvas.DrawPath(path, line);
        return (16, 16);
    }

    /// <summary>A two-headed arrow through the middle, turned by <paramref name="degrees"/> from left–right.</summary>
    private static (double, double) DrawDouble(SKCanvas canvas, float degrees)
    {
        using var path = SKPath.ParseSvgPathData("M4 16 L9.5 10.5 L9.5 14 L22.5 14 L22.5 10.5 L28 16 L22.5 21.5 L22.5 18 L9.5 18 L9.5 21.5 Z");
        canvas.Save();
        canvas.RotateDegrees(degrees, 16, 16);
        Shadow(canvas, path);
        using var fill = Fill(SKColors.White);
        using var edge = Edge(Ink, 1.2f);
        canvas.DrawPath(path, fill);
        canvas.DrawPath(path, edge);
        canvas.Restore();
        return (16, 16);
    }

    private static (double, double) DrawMove(SKCanvas canvas)
    {
        using var path = SKPath.ParseSvgPathData("M16 3 L20.5 7.5 L17.6 7.5 L17.6 14.4 L24.5 14.4 L24.5 11.5 L29 16 L24.5 20.5 L24.5 17.6 L17.6 17.6 L17.6 24.5 L20.5 24.5 L16 29 L11.5 24.5 L14.4 24.5 L14.4 17.6 L7.5 17.6 L7.5 20.5 L3 16 L7.5 11.5 L7.5 14.4 L14.4 14.4 L14.4 7.5 L11.5 7.5 Z");
        Shadow(canvas, path);
        using var fill = Fill(SKColors.White);
        using var edge = Edge(Ink, 1.2f);
        canvas.DrawPath(path, fill);
        canvas.DrawPath(path, edge);
        return (16, 16);
    }

    private static (double, double) DrawCross(SKCanvas canvas)
    {
        using var path = SKPath.ParseSvgPathData("M16 5 L16 27 M5 16 L27 16");
        using var halo = Edge(SKColors.White, 3.2f);
        using var line = Edge(Ink, 1.4f);
        canvas.DrawPath(path, halo);
        canvas.DrawPath(path, line);
        return (16, 16);
    }

    /// <summary>Windows' blue ring of waiting.</summary>
    private static (double, double) DrawWait(SKCanvas canvas, bool alone, float cx = 16, float cy = 16, float r = 9)
    {
        using var track = Edge(new SKColor(0xFF, 0xFF, 0xFF, 0xE0), alone ? 4.2f : 3.4f);
        using var arc = Edge(new SKColor(0x1E, 0x8B, 0xF0), alone ? 3f : 2.4f);
        canvas.DrawCircle(cx, cy, r, track);
        canvas.DrawArc(SKRect.Create(cx - r, cy - r, r * 2, r * 2), -90, 250, false, arc);
        return (cx, cy);
    }

    private static (double, double) DrawBusy(SKCanvas canvas)
    {
        DrawArrow(canvas, 0, 0);
        DrawWait(canvas, alone: false, 20, 21, 5.5f);
        return (1, 1);
    }

    private static (double, double) DrawNo(SKCanvas canvas)
    {
        using var ring = Edge(new SKColor(0xE5, 0x39, 0x35), 2.8f);
        using var halo = Edge(SKColors.White, 4.8f);
        canvas.DrawCircle(16, 16, 9, halo);
        canvas.DrawLine(9.6f, 22.4f, 22.4f, 9.6f, halo);
        canvas.DrawCircle(16, 16, 9, ring);
        canvas.DrawLine(9.6f, 22.4f, 22.4f, 9.6f, ring);
        return (16, 16);
    }

    // ---------- Windows' own pictures ----------

    private const int OcrHand = 32649, OcrNo = 32648, ImageCursor = 2;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr LoadImage(IntPtr instance, IntPtr name, int type, int width, int height, int flags);

    [DllImport("user32.dll", EntryPoint = "LoadImageW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadImageFile(IntPtr instance, string name, int type, int width, int height, int flags);

    [DllImport("user32.dll")]
    private static extern bool DestroyCursor(IntPtr cursor);

    private const int LoadFromFile = 0x10, Shared = 0x8000;

    /// <summary>
    /// The hand or "unavailable" pointer of the scheme Windows uses, loaded large from its file (pointer files hold
    /// several sizes); Windows' built-in one when the scheme names no file.
    /// </summary>
    private static PointerPicture? FromWindows(PointerKind kind)
    {
        int side = (int)(32 * Detail);
        string fallback = kind == PointerKind.Hand ? "aero_link.cur" : "aero_unavail.cur";
        string? file = null;
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Control Panel\Cursors");
            if (key?.GetValue(kind == PointerKind.Hand ? "Hand" : "No") is string set && set.Length > 0)
                file = Environment.ExpandEnvironmentVariables(set);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
        }
        if (file is null || !File.Exists(file))
            file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Cursors", fallback);
        var handle = File.Exists(file) ? LoadImageFile(IntPtr.Zero, file, ImageCursor, side, side, LoadFromFile) : IntPtr.Zero;
        bool shared = false;
        if (handle == IntPtr.Zero)
        {
            handle = LoadImage(IntPtr.Zero, kind == PointerKind.Hand ? OcrHand : OcrNo, ImageCursor, 0, 0, Shared);
            shared = true;
        }
        if (handle == IntPtr.Zero)
            return null;
        try
        {
            return Capture(handle, side) is { } taken ? new PointerPicture(taken.Image, taken.HotX, taken.HotY, 32) : null;
        }
        finally
        {
            if (!shared)
                DestroyCursor(handle);
        }
    }

    /// <summary>A pointer's picture at its own size, as it is on the screen.</summary>
    public static (SKImage Image, double HotX, double HotY)? CaptureNative(IntPtr cursor)
    {
        if (!ScreenNative.GetIconInfo(cursor, out var icon))
            return null;
        int side;
        try
        {
            side = Math.Clamp(NativeSize(icon), 16, 256);
        }
        finally
        {
            if (icon.hbmMask != IntPtr.Zero) DeleteObject(icon.hbmMask);
            if (icon.hbmColor != IntPtr.Zero) DeleteObject(icon.hbmColor);
        }
        return Capture(cursor, side);
    }

    /// <summary>
    /// A pointer's picture with its see-through parts: drawn once on black and once on white, the difference
    /// between the two tells how solid each pixel is.
    /// </summary>
    public static (SKImage Image, double HotX, double HotY)? Capture(IntPtr cursor, int side)
    {
        if (!ScreenNative.GetIconInfo(cursor, out var icon))
            return null;
        try
        {
            var black = Draw(cursor, side, 0x000000);
            var white = Draw(cursor, side, 0xFFFFFF);
            if (black is null || white is null)
                return null;
            var bitmap = new SKBitmap(new SKImageInfo(side, side, SKColorType.Bgra8888, SKAlphaType.Unpremul));
            var pixels = new byte[side * side * 4];
            bool any = false;
            for (int i = 0; i < pixels.Length; i += 4)
            {
                int alpha = 255 - Math.Clamp(((white[i] - black[i]) + (white[i + 1] - black[i + 1]) + (white[i + 2] - black[i + 2])) / 3, 0, 255);
                pixels[i + 3] = (byte)alpha;
                if (alpha > 0)
                {
                    any = true;
                    for (int c = 0; c < 3; c++)
                        pixels[i + c] = (byte)Math.Clamp(black[i + c] * 255 / alpha, 0, 255);
                }
            }
            if (!any)
                return null;
            Marshal.Copy(pixels, 0, bitmap.GetPixels(), pixels.Length);
            // The hot spot is given for the pointer's own size; the picture may be drawn larger.
            double scale = side / (double)Math.Max(1, NativeSize(icon));
            return (SKImage.FromBitmap(bitmap), icon.xHotspot * scale, icon.yHotspot * scale);
        }
        finally
        {
            if (icon.hbmMask != IntPtr.Zero) DeleteObject(icon.hbmMask);
            if (icon.hbmColor != IntPtr.Zero) DeleteObject(icon.hbmColor);
        }
    }

    /// <summary>The pointer's own height in pixels (a black-and-white pointer's mask holds two pictures, one above the other).</summary>
    private static int NativeSize(ScreenNative.ICONINFO icon)
    {
        var bitmap = icon.hbmColor != IntPtr.Zero ? icon.hbmColor : icon.hbmMask;
        var b = new BITMAP();
        if (GetObject(bitmap, Marshal.SizeOf<BITMAP>(), ref b) == 0)
            return 32;
        return icon.hbmColor != IntPtr.Zero ? b.bmHeight : b.bmHeight / 2;
    }

    private static byte[]? Draw(IntPtr cursor, int side, int colour)
    {
        var screen = GetDC(IntPtr.Zero);
        var memory = CreateCompatibleDC(screen);
        var header = new BITMAPINFOHEADER { biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(), biWidth = side, biHeight = -side, biPlanes = 1, biBitCount = 32 };
        var dib = CreateDIBSection(screen, ref header, 0, out var bits, IntPtr.Zero, 0);
        var old = SelectObject(memory, dib);
        try
        {
            var brush = CreateSolidBrush(colour);
            var rect = new RECT { Left = 0, Top = 0, Right = side, Bottom = side };
            FillRect(memory, ref rect, brush);
            DeleteObject(brush);
            if (!ScreenNative.DrawIconEx(memory, 0, 0, cursor, side, side, 0, IntPtr.Zero, ScreenNative.DI_NORMAL))
                return null;
            var pixels = new byte[side * side * 4];
            Marshal.Copy(bits, pixels, 0, pixels.Length);
            return pixels;
        }
        finally
        {
            SelectObject(memory, old);
            DeleteObject(dib);
            DeleteDC(memory);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int bmType, bmWidth, bmHeight, bmWidthBytes;
        public ushort bmPlanes, bmBitsPixel;
        public IntPtr bmBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth, biHeight;
        public ushort biPlanes, biBitCount;
        public uint biCompression, biSizeImage;
        public int biXPelsPerMeter, biYPelsPerMeter;
        public uint biClrUsed, biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("gdi32.dll")] private static extern int GetObject(IntPtr h, int size, ref BITMAP bitmap);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFOHEADER header, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateSolidBrush(int colour);
    [DllImport("user32.dll")] private static extern int FillRect(IntPtr dc, ref RECT rect, IntPtr brush);

    public void Dispose()
    {
        foreach (var p in _kinds.Values.Concat(_shapes.Values))
            p.Image.Dispose();
        _kinds.Clear();
        _shapes.Clear();
    }
}
