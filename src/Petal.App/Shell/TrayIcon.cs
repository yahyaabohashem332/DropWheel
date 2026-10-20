using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace Petal.App.Shell;

/// <summary>Notification-area icon with a WPF context menu, using Shell_NotifyIcon directly.</summary>
public sealed class TrayIcon : IDisposable
{
    private const int WM_APP_TRAY = 0x8000 + 41;
    private const int NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIM_SETVERSION = 4;
    private const int NIF_MESSAGE = 0x1, NIF_ICON = 0x2, NIF_TIP = 0x4, NIF_INFO = 0x10, NIF_SHOWTIP = 0x80;
    private const int NOTIFYICON_VERSION_4 = 4;
    private const int WM_LBUTTONUP = 0x0202, WM_RBUTTONUP = 0x0205, WM_CONTEXTMENU = 0x007B, NIN_SELECT = 0x0400;
    private static readonly int WM_TASKBARCREATED = RegisterWindowMessage("TaskbarCreated");

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public int dwState;
        public int dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public int uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public int dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(int message, ref NOTIFYICONDATA data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegisterWindowMessage(string message);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadImage(IntPtr hinst, string name, uint type, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    private readonly HwndSource _window;
    private readonly IntPtr _icon;
    private NOTIFYICONDATA _data;

    public TrayIcon(string iconPath, string tooltip)
    {
        _window = new HwndSource(new HwndSourceParameters("PetalTray") { Width = 0, Height = 0, WindowStyle = 0 });
        _window.AddHook(WndProc);
        int size = GetSystemMetrics(49); // SM_CXSMICON
        _icon = LoadImage(IntPtr.Zero, iconPath, 1, size, size, 0x10); // IMAGE_ICON, LR_LOADFROMFILE
        _data = new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _window.Handle,
            uID = 1,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP,
            uCallbackMessage = WM_APP_TRAY,
            hIcon = _icon,
            szTip = tooltip,
            szInfo = "",
            szInfoTitle = "",
        };
        Add();
    }

    public ContextMenu? Menu { get; set; }

    public event Action? Clicked;

    private void Add()
    {
        Shell_NotifyIcon(NIM_ADD, ref _data);
        _data.uVersion = NOTIFYICON_VERSION_4;
        Shell_NotifyIcon(NIM_SETVERSION, ref _data);
    }

    public void SetTooltip(string text)
    {
        _data.szTip = text.Length > 127 ? text[..127] : text;
        _data.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP;
        Shell_NotifyIcon(NIM_MODIFY, ref _data);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_APP_TRAY)
        {
            int ev = (int)(lParam.ToInt64() & 0xFFFF);
            if (ev is WM_LBUTTONUP or NIN_SELECT)
                Clicked?.Invoke();
            else if (ev is WM_RBUTTONUP or WM_CONTEXTMENU)
                ShowMenu();
            handled = true;
        }
        else if (msg == WM_TASKBARCREATED)
        {
            // Explorer restarted: put the icon back.
            Add();
        }
        return IntPtr.Zero;
    }

    private void ShowMenu()
    {
        if (Menu is null)
            return;
        // The menu must belong to a foreground window or it will not close when clicking away.
        SetForegroundWindow(_window.Handle);
        Menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        Menu.IsOpen = true;
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    public void Dispose()
    {
        Shell_NotifyIcon(NIM_DELETE, ref _data);
        if (_icon != IntPtr.Zero)
            DestroyIcon(_icon);
        _window.Dispose();
    }
}
