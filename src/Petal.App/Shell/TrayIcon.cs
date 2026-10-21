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

    private IntPtr _badged;

    /// <summary>
    /// A small dot in the accent colour on the icon, for an update waiting to be installed; off puts the
    /// plain icon back.
    /// </summary>
    public void SetBadge(bool on, System.Windows.Media.Color colour)
    {
        if (_badged != IntPtr.Zero)
        {
            DestroyIcon(_badged);
            _badged = IntPtr.Zero;
        }
        if (on)
            _badged = Badged(_icon, GetSystemMetrics(49), colour);
        _data.hIcon = _badged != IntPtr.Zero ? _badged : _icon;
        _data.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP;
        Shell_NotifyIcon(NIM_MODIFY, ref _data);
    }

    /// <summary>The icon with a dot at its top corner, drawn with WPF and made into an icon handle.</summary>
    private static IntPtr Badged(IntPtr icon, int size, System.Windows.Media.Color colour)
    {
        var plain = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(icon, Int32Rect.Empty, System.Windows.Media.Imaging.BitmapSizeOptions.FromWidthAndHeight(size, size));
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawImage(plain, new Rect(0, 0, size, size));
            double r = size * 0.23;
            var centre = new Point(size - r - 0.5, r + 0.5);
            dc.DrawEllipse(Brushes.White, null, centre, r + 1, r + 1);
            dc.DrawEllipse(new SolidColorBrush(colour), null, centre, r, r);
        }
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var pixels = new byte[size * size * 4];
        bitmap.CopyPixels(pixels, size * 4, 0);
        // Icons take straight alpha, not WPF's premultiplied colours.
        for (int i = 0; i < pixels.Length; i += 4)
        {
            int a = pixels[i + 3];
            if (a is > 0 and < 255)
                for (int c = 0; c < 3; c++)
                    pixels[i + c] = (byte)Math.Min(255, pixels[i + c] * 255 / a);
        }
        var header = new Interop.NativeMethods.BITMAPINFOHEADER
        {
            biSize = (uint)Marshal.SizeOf<Interop.NativeMethods.BITMAPINFOHEADER>(),
            biWidth = size,
            biHeight = -size,
            biPlanes = 1,
            biBitCount = 32,
        };
        var colours = Interop.NativeMethods.CreateDIBSection(IntPtr.Zero, ref header, 0, out var bits, IntPtr.Zero, 0);
        Marshal.Copy(pixels, 0, bits, pixels.Length);
        var mask = CreateBitmap(size, size, 1, 1, new byte[(size + 15) / 16 * 2 * size]);
        var info = new ICONINFO { fIcon = true, hbmMask = mask, hbmColor = colours };
        var result = CreateIconIndirect(ref info);
        Interop.NativeMethods.DeleteObject(colours);
        Interop.NativeMethods.DeleteObject(mask);
        return result;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        [MarshalAs(UnmanagedType.Bool)] public bool fIcon;
        public int xHotspot, yHotspot;
        public IntPtr hbmMask, hbmColor;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr CreateIconIndirect(ref ICONINFO info);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateBitmap(int width, int height, uint planes, uint bitsPerPixel, byte[] bits);

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
        if (_badged != IntPtr.Zero)
            DestroyIcon(_badged);
        if (_icon != IntPtr.Zero)
            DestroyIcon(_icon);
        _window.Dispose();
    }
}
