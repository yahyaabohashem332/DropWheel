using System.Runtime.InteropServices;
using static Petal.App.Interop.NativeMethods;

namespace Petal.App.Screen;

/// <summary>Where a window goes on its screen.</summary>
public enum WindowPlace
{
    LeftHalf,
    RightHalf,
    TopHalf,
    BottomHalf,
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight,
    LeftThird,
    MiddleThird,
    RightThird,
    Centre,
}

/// <summary>
/// Arranges the window that was in front when the wheel opened: halves, quarters, thirds, the middle,
/// the next screen, maximised, always on top, see-through, minimised or closed. Petal's own windows, the
/// desktop and the taskbar are left alone.
/// </summary>
internal static class WindowArranger
{
    /// <summary>The window in front when the wheels opened, for the switches' state.</summary>
    public static IntPtr Front { get; set; }

    private static readonly string[] Untouchable = ["Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Windows.UI.Core.CoreWindow"];

    /// <summary>True when <paramref name="window"/> is an app's window that may be moved.</summary>
    public static bool CanArrange(IntPtr window)
    {
        if (window == IntPtr.Zero || !ScreenNative.IsWindow(window))
            return false;
        ScreenNative.GetWindowThreadProcessId(window, out int pid);
        if (pid == Environment.ProcessId)
            return false;
        return !Untouchable.Contains(ScreenNative.ClassOf(window));
    }

    /// <summary>The window's frame as it looks (without the invisible resize borders), and those borders.</summary>
    private static (RECT Visible, RECT Borders) Frame(IntPtr window)
    {
        GetWindowRect(window, out var outer);
        if (ScreenNative.DwmGetWindowAttribute(window, ScreenNative.DWMWA_EXTENDED_FRAME_BOUNDS, out var visible, Marshal.SizeOf<RECT>()) != 0)
            visible = outer;
        var borders = new RECT { Left = visible.Left - outer.Left, Top = visible.Top - outer.Top, Right = outer.Right - visible.Right, Bottom = outer.Bottom - visible.Bottom };
        return (visible, borders);
    }

    /// <summary>Moves the window so what can be seen of it fills <paramref name="area"/> exactly.</summary>
    internal static void MoveTo(IntPtr window, RECT area)
    {
        if (ScreenNative.IsZoomed(window) || ScreenNative.IsIconic(window))
            ScreenNative.ShowWindow(window, ScreenNative.SW_RESTORE);
        var (_, b) = Frame(window);
        SetWindowPos(window, IntPtr.Zero, area.Left - b.Left, area.Top - b.Top, area.Width + b.Left + b.Right, area.Height + b.Top + b.Bottom,
            ScreenNative.SWP_NOZORDER_ | ScreenNative.SWP_NOACTIVATE_);
    }

    public static bool Place(IntPtr window, WindowPlace place)
    {
        if (!CanArrange(window))
            return false;
        var w = ScreenNative.OfWindow(window).Work;
        int midX = w.Left + w.Width / 2, midY = w.Top + w.Height / 2;
        int third = w.Width / 3;
        RECT area = place switch
        {
            WindowPlace.LeftHalf => R(w.Left, w.Top, midX, w.Bottom),
            WindowPlace.RightHalf => R(midX, w.Top, w.Right, w.Bottom),
            WindowPlace.TopHalf => R(w.Left, w.Top, w.Right, midY),
            WindowPlace.BottomHalf => R(w.Left, midY, w.Right, w.Bottom),
            WindowPlace.TopLeft => R(w.Left, w.Top, midX, midY),
            WindowPlace.TopRight => R(midX, w.Top, w.Right, midY),
            WindowPlace.BottomLeft => R(w.Left, midY, midX, w.Bottom),
            WindowPlace.BottomRight => R(midX, midY, w.Right, w.Bottom),
            WindowPlace.LeftThird => R(w.Left, w.Top, w.Left + third, w.Bottom),
            WindowPlace.MiddleThird => R(w.Left + third, w.Top, w.Right - third, w.Bottom),
            WindowPlace.RightThird => R(w.Right - third, w.Top, w.Right, w.Bottom),
            _ => Centred(window, w),
        };
        MoveTo(window, area);
        return true;
    }

    private static RECT R(int left, int top, int right, int bottom) => new() { Left = left, Top = top, Right = right, Bottom = bottom };

    /// <summary>The same size in the middle; a window too big for the screen shrinks to fit.</summary>
    private static RECT Centred(IntPtr window, RECT work)
    {
        if (ScreenNative.IsZoomed(window))
            ScreenNative.ShowWindow(window, ScreenNative.SW_RESTORE);
        var (visible, _) = Frame(window);
        int width = Math.Min(visible.Width, (int)(work.Width * 0.92)), height = Math.Min(visible.Height, (int)(work.Height * 0.92));
        if (width < 200 || height < 150)
            (width, height) = ((int)(work.Width * 0.6), (int)(work.Height * 0.7));
        int left = work.Left + (work.Width - width) / 2, top = work.Top + (work.Height - height) / 2;
        return R(left, top, left + width, top + height);
    }

    /// <summary>To the next screen, in the same place relative to it (kept maximised when it was).</summary>
    public static bool NextScreen(IntPtr window)
    {
        if (!CanArrange(window))
            return false;
        var monitors = ScreenNative.Monitors().OrderBy(m => m.Bounds.Left).ThenBy(m => m.Bounds.Top).ToList();
        if (monitors.Count < 2)
            return false;
        var here = ScreenNative.OfWindow(window);
        int index = monitors.FindIndex(m => m.Handle == here.Handle);
        var next = monitors[(index + 1) % monitors.Count];
        bool maximised = ScreenNative.IsZoomed(window);
        if (maximised)
            ScreenNative.ShowWindow(window, ScreenNative.SW_RESTORE);
        var (visible, _) = Frame(window);
        var from = here.Work;
        var to = next.Work;
        double sx = (double)to.Width / from.Width, sy = (double)to.Height / from.Height;
        var area = R(
            to.Left + (int)((visible.Left - from.Left) * sx), to.Top + (int)((visible.Top - from.Top) * sy),
            to.Left + (int)((visible.Right - from.Left) * sx), to.Top + (int)((visible.Bottom - from.Top) * sy));
        MoveTo(window, area);
        if (maximised)
            ScreenNative.ShowWindow(window, ScreenNative.SW_MAXIMIZE);
        return true;
    }

    public static bool ToggleMaximised(IntPtr window)
    {
        if (!CanArrange(window))
            return false;
        ScreenNative.ShowWindow(window, ScreenNative.IsZoomed(window) ? ScreenNative.SW_RESTORE : ScreenNative.SW_MAXIMIZE);
        return true;
    }

    public static bool Minimise(IntPtr window)
    {
        if (!CanArrange(window))
            return false;
        ScreenNative.ShowWindow(window, ScreenNative.SW_MINIMIZE);
        return true;
    }

    /// <summary>Asks the window to close, as its own X button would (it can still ask to save).</summary>
    public static bool Close(IntPtr window)
    {
        if (!CanArrange(window))
            return false;
        ScreenNative.PostMessage(window, ScreenNative.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        return true;
    }

    public static bool? IsOnTop(IntPtr window) =>
        CanArrange(window) ? ((long)GetWindowLongPtr(window, GWL_EXSTYLE) & ScreenNative.WS_EX_TOPMOST_) != 0 : null;

    public static bool ToggleOnTop(IntPtr window)
    {
        if (IsOnTop(window) is not { } on)
            return false;
        SetWindowPos(window, on ? ScreenNative.HWND_NOTOPMOST : HWND_TOPMOST, 0, 0, 0, 0, ScreenNative.SWP_NOMOVE_ | ScreenNative.SWP_NOSIZE_ | ScreenNative.SWP_NOACTIVATE_);
        return true;
    }

    /// <summary>How see-through the window is, 255 for solid.</summary>
    public static byte? Opacity(IntPtr window)
    {
        if (!CanArrange(window))
            return null;
        long ex = (long)GetWindowLongPtr(window, GWL_EXSTYLE);
        if ((ex & WS_EX_LAYERED) == 0)
            return 255;
        return ScreenNative.GetLayeredWindowAttributes(window, out _, out var alpha, out var flags) && (flags & ScreenNative.LWA_ALPHA) != 0 ? alpha : (byte)255;
    }

    /// <summary>Solid, then 85%, 70% and 55% see-through, then solid again. Returns the new percentage.</summary>
    public static int? CycleOpacity(IntPtr window)
    {
        if (Opacity(window) is not { } now)
            return null;
        byte[] steps = [255, 217, 178, 140];
        int at = Array.FindIndex(steps, s => Math.Abs(s - now) < 10);
        byte next = steps[(at + 1) % steps.Length];
        long ex = (long)GetWindowLongPtr(window, GWL_EXSTYLE);
        if (next == 255)
        {
            ScreenNative.SetLayeredWindowAttributes(window, 0, 255, ScreenNative.LWA_ALPHA);
        }
        else
        {
            if ((ex & WS_EX_LAYERED) == 0)
                SetWindowLongPtr(window, GWL_EXSTYLE, new IntPtr(ex | WS_EX_LAYERED));
            ScreenNative.SetLayeredWindowAttributes(window, 0, next, ScreenNative.LWA_ALPHA);
        }
        return (int)Math.Round(next / 255.0 * 100);
    }
}
