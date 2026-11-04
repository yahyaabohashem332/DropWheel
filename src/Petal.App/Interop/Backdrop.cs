using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace Petal.App.Interop;

/// <summary>
/// Translucent window backgrounds: Acrylic through DWM on Windows 11, the accent-policy blur on
/// Windows 10 with the window painting its own tint over it. Respects the "Transparency effects"
/// setting; falls back to a solid colour.
/// </summary>
/// <remarks>
/// Windows 10's acrylic accent isn't used: it loses a WPF window's drawing whenever the window changes
/// size, so a tool window sizing itself to its content opened as an empty box until the pointer passed
/// over its controls, and it lags while the window is dragged. The plain blur has neither problem.
/// </remarks>
internal static class Backdrop
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>Windows won't animate this window as it opens or closes.</summary>
    public static void NoAnimation(System.Windows.Window window)
    {
        int on = 1;
        // DWMWA_TRANSITIONS_FORCEDISABLED
        DwmSetWindowAttribute(new System.Windows.Interop.WindowInteropHelper(window).Handle, 3, ref on, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS margins);

    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

    [StructLayout(LayoutKind.Sequential)]
    private struct MARGINS
    {
        public int Left, Right, Top, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public int AccentState;
        public int AccentFlags;
        public uint GradientColor;
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttributeData
    {
        public int Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
    private const int DWMSBT_TRANSIENTWINDOW = 3;
    private const int DWMWCP_ROUND = 2;
    private const int WCA_ACCENT_POLICY = 19;
    private const int ACCENT_ENABLE_BLURBEHIND = 3;

    public static bool IsWindows11 => Environment.OSVersion.Version.Build >= 22000;

    public static bool TransparencyEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                return key?.GetValue("EnableTransparency") is not int v || v != 0;
            }
            catch
            {
                return true;
            }
        }
    }

    /// <summary>Applies the backdrop. Returns true when the window is translucent (content should use a tint, not an opaque fill).</summary>
    public static bool Apply(Window window, bool dark)
    {
        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        int darkValue = dark ? 1 : 0;
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkValue, sizeof(int));
        if (IsWindows11)
        {
            int round = DWMWCP_ROUND;
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
        }
        if (!TransparencyEnabled)
            return false;

        try
        {
            if (HwndSource.FromHwnd(hwnd) is { CompositionTarget: { } target })
                target.BackgroundColor = Colors.Transparent;

            if (IsWindows11)
            {
                var margins = new MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
                DwmExtendFrameIntoClientArea(hwnd, ref margins);
                int type = DWMSBT_TRANSIENTWINDOW;
                return DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref type, sizeof(int)) == 0;
            }

            // Windows 10: a plain blur; the window paints the tint.
            var policy = new AccentPolicy { AccentState = ACCENT_ENABLE_BLURBEHIND };
            int size = Marshal.SizeOf(policy);
            var ptr = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(policy, ptr, false);
                var data = new WindowCompositionAttributeData { Attribute = WCA_ACCENT_POLICY, Data = ptr, SizeOfData = size };
                return SetWindowCompositionAttribute(hwnd, ref data) != 0;
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
        }
        catch
        {
            return false;
        }
    }
}
