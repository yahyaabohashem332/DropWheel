using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ComTypes = System.Runtime.InteropServices.ComTypes;

namespace Petal.DragHarness;

/// <summary>
/// Options: --files a;b --key shift|ctrl|alt [--key2 ...] --petal N --count N --shots dir [--mode drop|cancel|outside]
/// </summary>
public sealed record HarnessScript(string[] Files, string Key, string? Key2, int Petal, int Count, string ShotsDir, string Mode, int HoverAll, int WaitAfterTap)
{
    public static HarnessScript Parse(string[] args)
    {
        string Get(string name, string fallback)
        {
            int i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
        }
        return new HarnessScript(
            Get("--files", "").Split(';', StringSplitOptions.RemoveEmptyEntries),
            Get("--key", "shift"),
            args.Contains("--key2") ? Get("--key2", "") : null,
            int.Parse(Get("--petal", "0")),
            int.Parse(Get("--count", "8")),
            Get("--shots", Path.GetTempPath()),
            Get("--mode", "drop"),
            int.Parse(Get("--hover-all", "0")),
            int.Parse(Get("--wait-after-tap", "260")));
    }
}

/// <summary>
/// Drag source that behaves like File Explorer: a shell data object (PIDLs + CF_HDROP) started
/// with SHDoDragDrop, so the shell draws its usual drag image. Then it drives the mouse and
/// keyboard with SendInput like a person would and saves screenshots along the way.
/// </summary>
public sealed class HarnessWindow : Window
{
    private readonly HarnessScript _script;
    private readonly Border _tile;
    private bool _dragging;
    private Point _down;

    public HarnessWindow(HarnessScript script)
    {
        _script = script;
        Title = "Petal drag harness";
        Width = 260;
        Height = 200;
        Left = 160;
        Top = 520;
        Topmost = true;
        WindowStyle = WindowStyle.ToolWindow;
        _tile = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0xE8, 0xF0, 0xFF)),
            CornerRadius = new CornerRadius(8),
            Margin = new Thickness(16),
            Child = new TextBlock
            {
                Text = string.Join("\n", script.Files.Select(Path.GetFileName)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 14,
            },
        };
        Content = _tile;
        _tile.PreviewMouseLeftButtonDown += (_, e) =>
        {
            _down = e.GetPosition(this);
            _dragging = false;
        };
        _tile.PreviewMouseMove += (_, e) =>
        {
            if (_dragging || e.LeftButton != MouseButtonState.Pressed)
                return;
            var d = e.GetPosition(this) - _down;
            if (Math.Abs(d.X) < 4 && Math.Abs(d.Y) < 4)
                return;
            _dragging = true;
            ShellDrag.Start(new WindowInteropHelper(this).Handle, _script.Files);
            Log("drag finished");
        };
        Loaded += (_, _) =>
        {
            var p = _tile.PointToScreen(new Point(_tile.ActualWidth / 2, _tile.ActualHeight / 2));
            var thread = new Thread(() =>
            {
                try { Run((int)p.X, (int)p.Y); }
                catch (Exception ex) { Log(ex.ToString()); }
                Dispatcher.BeginInvoke(Close);
            }) { IsBackground = true };
            thread.Start();
        };
    }

    private static void Log(string message) =>
        File.AppendAllText(Path.Combine(Path.GetTempPath(), "petal-harness.log"), $"{DateTime.Now:HH:mm:ss.fff} {message}\n");

    private void Run(int x, int y)
    {
        Thread.Sleep(600);
        Directory.CreateDirectory(_script.ShotsDir);

        Input.MoveTo(x, y);
        Thread.Sleep(120);
        Input.LeftDown();
        Thread.Sleep(80);
        // Start dragging (the shell's modal drag loop begins on the UI thread).
        for (int i = 1; i <= 6; i++)
        {
            Input.MoveTo(x + i * 6, y - i * 4);
            Thread.Sleep(25);
        }
        int ax = x + 36, ay = y - 24;
        Thread.Sleep(150);
        Input.Tap(_script.Key);
        Log($"tapped {_script.Key} at {ax},{ay}");
        Thread.Sleep(_script.WaitAfterTap);
        Screen.Save(ax, ay, 560, Path.Combine(_script.ShotsDir, "1-open.png"));

        if (_script.Key2 is { Length: > 0 } key2)
        {
            Input.Tap(key2);
            Thread.Sleep(200);
            Screen.Save(ax, ay, 560, Path.Combine(_script.ShotsDir, "2-switched.png"));
        }

        if (_script.HoverAll > 0)
        {
            for (int i = 0; i < _script.Count; i++)
            {
                var (tx, ty) = PetalPoint(ax, ay, i, _script.Count);
                Glide(ax, ay, tx, ty);
                Thread.Sleep(140);
                if (i == _script.HoverAll - 1)
                    Screen.Save(ax, ay, 560, Path.Combine(_script.ShotsDir, $"3-hover-{i}.png"));
            }
        }

        var (px, py) = _script.Mode == "outside" ? (ax + 420, ay + 260) : _script.Mode == "cancel" ? (ax, ay) : PetalPoint(ax, ay, _script.Petal, _script.Count);
        for (int i = 1; i <= 10; i++)
        {
            Input.MoveTo(ax + (px - ax) * i / 10, ay + (py - ay) * i / 10);
            Thread.Sleep(22);
        }
        Thread.Sleep(220);
        Screen.Save(ax, ay, 560, Path.Combine(_script.ShotsDir, "4-hover.png"));
        Input.LeftUp();
        Log("released");
        Thread.Sleep(260);
        Screen.Save(ax, ay, 560, Path.Combine(_script.ShotsDir, "5-after-drop.png"));
        Thread.Sleep(1800);
        Screen.Save(ax, ay, 700, Path.Combine(_script.ShotsDir, "6-result.png"));
    }

    private static void Glide(int fromX, int fromY, int toX, int toY) => Input.MoveTo(toX, toY);

    private static (int X, int Y) PetalPoint(int ax, int ay, int index, int count)
    {
        double angle = Math.PI / 2 - 2 * Math.PI * index / count;
        double r = 84;
        return (ax + (int)(Math.Cos(angle) * r), ay - (int)(Math.Sin(angle) * r));
    }
}

internal static class ShellDrag
{
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(string name, IntPtr bc, out IntPtr pidl, uint sfgaoIn, out uint sfgaoOut);

    [DllImport("shell32.dll")]
    private static extern IntPtr ILFindLastID(IntPtr pidl);

    [DllImport("shell32.dll")]
    private static extern IntPtr ILClone(IntPtr pidl);

    [DllImport("shell32.dll")]
    private static extern bool ILRemoveLastID(IntPtr pidl);

    [DllImport("shell32.dll")]
    private static extern int SHCreateDataObject(IntPtr pidlFolder, uint cidl, IntPtr[] apidl, IntPtr pdtInner, ref Guid riid, out ComTypes.IDataObject ppv);

    [DllImport("shell32.dll")]
    private static extern int SHDoDragDrop(IntPtr hwnd, ComTypes.IDataObject pdtobj, IntPtr pdsrc, int dwEffect, out int pdwEffect);

    public static void Start(IntPtr hwnd, string[] files)
    {
        SHParseDisplayName(files[0], IntPtr.Zero, out var first, 0, out _);
        var folder = ILClone(first);
        ILRemoveLastID(folder);
        var children = files.Select(f =>
        {
            SHParseDisplayName(f, IntPtr.Zero, out var pidl, 0, out _);
            return ILFindLastID(pidl);
        }).ToArray();
        var iid = typeof(ComTypes.IDataObject).GUID;
        SHCreateDataObject(folder, (uint)children.Length, children, IntPtr.Zero, ref iid, out var data);
        // DROPEFFECT_COPY | MOVE | LINK, like Explorer.
        SHDoDragDrop(hwnd, data, IntPtr.Zero, 1 | 2 | 4, out var effect);
        File.AppendAllText(Path.Combine(Path.GetTempPath(), "petal-harness.log"), $"{DateTime.Now:HH:mm:ss.fff} effect={effect}\n");
    }
}

internal static class Input
{
    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr extra; }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr extra; }

    [StructLayout(LayoutKind.Explicit)]
    private struct UNION { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint type; public UNION u; }

    [DllImport("user32.dll")]
    private static extern uint SendInput(uint n, INPUT[] inputs, int size);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int i);

    private static void Send(INPUT input) => SendInput(1, [input], Marshal.SizeOf<INPUT>());

    public static void MoveTo(int x, int y)
    {
        int w = GetSystemMetrics(0), h = GetSystemMetrics(1);
        Send(new INPUT { type = 0, u = new UNION { mi = new MOUSEINPUT { dx = x * 65535 / (w - 1), dy = y * 65535 / (h - 1), dwFlags = 0x0001 | 0x8000 } } });
    }

    public static void LeftDown() => Send(new INPUT { type = 0, u = new UNION { mi = new MOUSEINPUT { dwFlags = 0x0002 } } });

    public static void LeftUp() => Send(new INPUT { type = 0, u = new UNION { mi = new MOUSEINPUT { dwFlags = 0x0004 } } });

    public static void Tap(string key)
    {
        ushort vk = key switch { "ctrl" => 0xA2, "alt" => 0xA4, "esc" => 0x1B, _ => 0xA0 };
        Send(new INPUT { type = 1, u = new UNION { ki = new KEYBDINPUT { wVk = vk } } });
        Thread.Sleep(60);
        Send(new INPUT { type = 1, u = new UNION { ki = new KEYBDINPUT { wVk = vk, dwFlags = 0x0002 } } });
    }
}

internal static class Screen
{
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int w, int h);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr o);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr d, int x, int y, int w, int h, IntPtr s, int sx, int sy, uint rop);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr o);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);

    public static void Save(int cx, int cy, int size, string path)
    {
        int x = Math.Max(0, cx - size / 2), y = Math.Max(0, cy - size / 2);
        IntPtr screen = GetDC(IntPtr.Zero), mem = CreateCompatibleDC(screen), bmp = CreateCompatibleBitmap(screen, size, size);
        var old = SelectObject(mem, bmp);
        BitBlt(mem, 0, 0, size, size, screen, x, y, 0x00CC0020 | 0x40000000);
        SelectObject(mem, old);
        var source = Imaging.CreateBitmapSourceFromHBitmap(bmp, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
        using (var stream = File.Create(path))
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(source));
            encoder.Save(stream);
        }
        DeleteObject(bmp);
        DeleteDC(mem);
        ReleaseDC(IntPtr.Zero, screen);
    }
}
