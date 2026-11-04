using System.Runtime.InteropServices;
using static Petal.App.Interop.NativeMethods;

namespace Petal.App.Screen;

/// <summary>The Windows calls the screen tools need beyond the shared ones.</summary>
internal static partial class ScreenNative
{
    public const uint WDA_NONE = 0x0, WDA_EXCLUDEFROMCAPTURE = 0x11;
    public const int SW_RESTORE = 9, SW_MINIMIZE = 6, SW_MAXIMIZE = 3, SW_SHOWNOACTIVATE = 4;
    public const uint WM_CLOSE = 0x0010, WM_INPUTLANGCHANGEREQUEST = 0x0050;
    public const uint MONITOR_DEFAULTTONEAREST_ = 2;
    public const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    public const uint LWA_ALPHA = 0x2;
    public const int WS_EX_TOPMOST_ = 0x00000008;
    public const uint SWP_NOMOVE_ = 0x0002, SWP_NOSIZE_ = 0x0001, SWP_NOZORDER_ = 0x0004, SWP_NOACTIVATE_ = 0x0010, SWP_FRAMECHANGED = 0x0020;
    public static readonly IntPtr HWND_NOTOPMOST = new(-2);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowDisplayAffinity(IntPtr hWnd, uint affinity);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetCursorPos(int x, int y);

    [LibraryImport("user32.dll")]
    public static partial IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsZoomed(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsIconic(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindowVisible(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindow(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ShowWindow(IntPtr hWnd, int command);

    [LibraryImport("user32.dll", EntryPoint = "PostMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte alpha, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetLayeredWindowAttributes(IntPtr hwnd, out uint crKey, out byte alpha, out uint flags);

    [LibraryImport("dwmapi.dll")]
    public static partial int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out RECT value, int size);

    [LibraryImport("user32.dll")]
    public static partial uint GetWindowThreadProcessId(IntPtr hWnd, out int processId);

    [LibraryImport("user32.dll")]
    public static partial int GetKeyboardLayoutList(int count, [Out] IntPtr[]? list);

    [LibraryImport("user32.dll")]
    public static partial IntPtr GetKeyboardLayout(uint threadId);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AddClipboardFormatListener(IntPtr hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool RemoveClipboardFormatListener(IntPtr hwnd);

    [LibraryImport("user32.dll")]
    public static partial uint GetClipboardSequenceNumber();

    [LibraryImport("user32.dll", EntryPoint = "GetClassNameW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int GetClassName(IntPtr hWnd, [Out] char[] name, int max);

    [StructLayout(LayoutKind.Sequential)]
    public struct CURSORINFO
    {
        public int cbSize;
        public int flags;
        public IntPtr hCursor;
        public POINT ptScreenPos;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ICONINFO
    {
        public int fIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    public const int CURSOR_SHOWING = 1, DI_NORMAL = 3;

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetCursorInfo(ref CURSORINFO info);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetIconInfo(IntPtr icon, out ICONINFO info);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DrawIconEx(IntPtr hdc, int x, int y, IntPtr icon, int cx, int cy, int step, IntPtr brush, int flags);

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, IntPtr rect, IntPtr data);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

    /// <summary>A monitor: all of it, the part not under the taskbar, and its scale (1.5 at 144 dpi).</summary>
    public sealed record Monitor(RECT Bounds, RECT Work, double Scale, IntPtr Handle);

    /// <summary>Every monitor, the main one first.</summary>
    public static IReadOnlyList<Monitor> Monitors()
    {
        var list = new List<Monitor>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (handle, _, _, _) =>
        {
            list.Add(Describe(handle));
            return true;
        }, IntPtr.Zero);
        return list.OrderBy(m => m.Bounds.Left == 0 && m.Bounds.Top == 0 ? 0 : 1).ToList();
    }

    public static Monitor Describe(IntPtr handle)
    {
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(handle, ref info);
        double scale = GetDpiForMonitor(handle, 0, out var dpi, out _) == 0 ? dpi / 96.0 : 1;
        return new Monitor(info.rcMonitor, info.rcWork, scale, handle);
    }

    public static Monitor UnderPointer()
    {
        GetCursorPos(out var p);
        return Describe(MonitorFromPoint(p, MONITOR_DEFAULTTONEAREST_));
    }

    public static Monitor OfWindow(IntPtr window) => Describe(MonitorFromWindow(window, MONITOR_DEFAULTTONEAREST_));

    public static string ClassOf(IntPtr window)
    {
        var name = new char[128];
        int n = GetClassName(window, name, name.Length);
        return n > 0 ? new string(name, 0, n) : "";
    }
}
