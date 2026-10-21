using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Petal.App.Wheels;

/// <summary>
/// The windows open on the desktop, as the taskbar shows them: visible, top-level, not tool windows, not
/// hidden on another virtual desktop, and not Petal's own.
/// </summary>
public static partial class RunningApps
{
    /// <summary>At most this many: a wheel with more petals can't be read.</summary>
    public const int Most = 12;

    public static IReadOnlyList<RunningWindow> List()
    {
        var windows = new List<RunningWindow>();
        var shell = GetShellWindow();
        EnumWindows((hwnd, _) =>
        {
            if (windows.Count >= Most * 2 || hwnd == shell || !IsWindowVisible(hwnd))
                return true;
            if (GetWindow(hwnd, GW_OWNER) != IntPtr.Zero)
                return true;
            long ex = (long)Interop.NativeMethods.GetWindowLongPtr(hwnd, Interop.NativeMethods.GWL_EXSTYLE);
            if ((ex & Interop.NativeMethods.WS_EX_TOOLWINDOW) != 0 && (ex & WS_EX_APPWINDOW) == 0)
                return true;
            if (DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0)
                return true;
            var title = Title(hwnd);
            if (title.Length == 0)
                return true;
            KeySender.GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == Environment.ProcessId)
                return true;
            var path = ProcessPath(pid);
            var program = path is null ? "" : Path.GetFileNameWithoutExtension(path);
            if (program.Equals("ApplicationFrameHost", StringComparison.OrdinalIgnoreCase) && title.Length == 0)
                return true;
            windows.Add(new RunningWindow(hwnd, title, program, path));
            return true;
        }, IntPtr.Zero);
        return windows.Take(Most).ToList();
    }

    /// <summary>Switches to a window, restoring it when minimised.</summary>
    public static Task<bool> SwitchToAsync(IntPtr window) => KeySender.BringToFrontAsync(window);

    private static string Title(IntPtr hwnd)
    {
        int length = GetWindowTextLength(hwnd);
        if (length <= 0) return "";
        var buffer = new char[length + 1];
        int n = GetWindowText(hwnd, buffer, buffer.Length);
        return new string(buffer, 0, n).Trim();
    }

    /// <summary>The program behind a process, or null when Windows won't tell (an elevated app).</summary>
    public static string? ProcessPath(int pid)
    {
        var process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process == IntPtr.Zero)
            return null;
        try
        {
            var buffer = new char[1024];
            int size = buffer.Length;
            return QueryFullProcessImageName(process, 0, buffer, ref size) ? new string(buffer, 0, size) : null;
        }
        finally
        {
            CloseHandle(process);
        }
    }

    /// <summary>The program of a window, by file name without ".exe" ("chrome"), for app wheels.</summary>
    public static string? ProgramOf(IntPtr window)
    {
        if (window == IntPtr.Zero) return null;
        KeySender.GetWindowThreadProcessId(window, out var pid);
        var path = ProcessPath(pid);
        return path is null ? null : Path.GetFileNameWithoutExtension(path);
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    private const uint GW_OWNER = 4;
    private const long WS_EX_APPWINDOW = 0x00040000;
    private const int DWMWA_CLOAKED = 14;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetShellWindow();

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetWindow(IntPtr hWnd, uint cmd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindowVisible(IntPtr hWnd);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowTextLengthW")]
    private static partial int GetWindowTextLength(IntPtr hWnd);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowTextW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int GetWindowText(IntPtr hWnd, [Out] char[] text, int max);

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int pid);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryFullProcessImageName(IntPtr process, uint flags, [Out] char[] name, ref int size);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr handle);
}
