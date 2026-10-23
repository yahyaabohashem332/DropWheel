using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Petal.App.Localization;
using Petal.App.Visuals;
using static Petal.App.Interop.NativeMethods;

namespace Petal.App.Screen;

/// <summary>
/// A live region: part of another window (a call, a stream, a build, a timer) shown on top of everything,
/// moving as it moves there. Windows draws it (a DWM thumbnail), so it costs nothing and is always current;
/// it is not a copy and nothing is recorded. It closes by itself when its window closes.
/// </summary>
internal sealed partial class LiveRegion : Window
{
    /// <summary>More than this many live regions would only clutter the screen.</summary>
    public const int Most = 6;

    private static readonly List<LiveRegion> Open = [];
    private const double Header = 26;
    private readonly IntPtr _source;
    private readonly RECT _part;
    private readonly double _aspect;
    private readonly DispatcherTimer _watch = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly TextBlock _title = new() { Foreground = Brushes.White, FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(8, 0, 4, 0) };
    private IntPtr _thumbnail;
    private IntPtr _handle;
    private byte _opacity = 255;

    public static int Count => Open.Count;

    /// <summary>Asks for the part of a window to keep live, then shows it.</summary>
    public static void Start()
    {
        if (Open.Count >= Most)
        {
            Hud.HudCard.ShowAt(Hud.HudCard.CornerPoint(), "", "").Fail(L.F("Up to {0} live regions", Most), L.T("Close one to add another."));
            return;
        }
        Wheels.Screenshots.ChooseArea("Drag over the part of a window to keep live on top · Esc cancels", false, (_, area) =>
        {
            var centre = new POINT(area.Left + area.Width / 2, area.Top + area.Height / 2);
            var source = GetAncestor(WindowFromPoint(centre), GA_ROOT);
            GetWindowThreadProcessId(source, out var process);
            var kind = ClassNameOf(source);
            if (source == IntPtr.Zero || process == Environment.ProcessId || kind is "Progman" or "WorkerW" or "Shell_TrayWnd")
            {
                Hud.HudCard.ShowAt(Hud.HudCard.CornerPoint(), "", "").Fail(L.T("Choose part of an app's window"), L.T("The desktop and the taskbar can't be kept live."));
                return;
            }
            var live = new LiveRegion(source, area);
            live.Show();
        });
    }

    /// <summary>Closes every live region.</summary>
    public static void CloseAll()
    {
        foreach (var live in Open.ToList())
            live.Close();
    }

    private LiveRegion(IntPtr source, RECT area)
    {
        _source = source;
        GetWindowRect(source, out var window);
        // The part, relative to the source window's own corner, as DWM wants it.
        _part = new RECT { Left = area.Left - window.Left, Top = area.Top - window.Top, Right = area.Right - window.Left, Bottom = area.Bottom - window.Top };
        _aspect = Math.Max(1, area.Width) / (double)Math.Max(1, area.Height);
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.CanResize;
        ShowInTaskbar = false;
        Topmost = true;
        Background = Brushes.Black;
        Title = L.T("Live Region");
        L.Apply(this);
        var monitor = ScreenNative.OfWindow(source);
        double s = monitor.Scale;
        Width = Math.Max(160, area.Width / s);
        Height = Math.Max(90, area.Height / s) + Header;
        // Beside where it was, a little in, so it doesn't hide its own source.
        Left = Math.Min(monitor.Work.Right / s - Width - 16, (area.Left + 32) / s);
        Top = Math.Min(monitor.Work.Bottom / s - Height - 16, (area.Top + 32) / s);
        _title.Text = TitleOf(source);

        var bar = new DockPanel { Height = Header, Background = new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x22)), VerticalAlignment = VerticalAlignment.Top };
        var close = new StripButton("x", "Close", Close, null, 22);
        var fade = new StripButton("blend", "See-through", CycleOpacity, null, 22);
        DockPanel.SetDock(close, Dock.Right);
        DockPanel.SetDock(fade, Dock.Right);
        bar.Children.Add(close);
        bar.Children.Add(fade);
        var dot = new System.Windows.Shapes.Ellipse { Width = 7, Height = 7, Fill = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44)), Margin = new Thickness(9, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(dot, Dock.Left);
        bar.Children.Add(dot);
        bar.Children.Add(_title);
        ScreenUi.DragFromBackground(this, bar);
        Content = new Grid { Children = { bar } };

        SourceInitialized += (_, _) =>
        {
            _handle = new WindowInteropHelper(this).Handle;
            var ex = (long)GetWindowLongPtr(_handle, GWL_EXSTYLE) | WS_EX_TOOLWINDOW;
            SetWindowLongPtr(_handle, GWL_EXSTYLE, new IntPtr(ex));
            HwndSource.FromHwnd(_handle)?.AddHook(KeepShape);
            Register();
        };
        SizeChanged += (_, _) => Update();
        _watch.Tick += (_, _) =>
        {
            // The source closed: so does its live copy.
            if (!IsWindow(_source))
                Close();
        };
        _watch.Start();
        Closed += (_, _) =>
        {
            _watch.Stop();
            if (_thumbnail != IntPtr.Zero)
                DwmUnregisterThumbnail(_thumbnail);
            Open.Remove(this);
        };
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
                Close();
        };
        Open.Add(this);
    }

    private static string TitleOf(IntPtr window)
    {
        var text = new System.Text.StringBuilder(256);
        GetWindowText(window, text, text.Capacity);
        return text.Length > 0 ? text.ToString() : L.T("Live Region");
    }

    private void Register()
    {
        if (DwmRegisterThumbnail(_handle, _source, out _thumbnail) != 0)
        {
            _thumbnail = IntPtr.Zero;
            Close();
            return;
        }
        Update();
    }

    /// <summary>The part of the source, drawn below the header across the rest of the window.</summary>
    private void Update()
    {
        if (_thumbnail == IntPtr.Zero || _handle == IntPtr.Zero)
            return;
        GetClientRect(_handle, out var client);
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleY;
        var properties = new DWM_THUMBNAIL_PROPERTIES
        {
            dwFlags = DWM_TNP_RECTDESTINATION | DWM_TNP_RECTSOURCE | DWM_TNP_VISIBLE | DWM_TNP_OPACITY,
            rcDestination = new RECT { Left = 0, Top = (int)Math.Round(Header * scale), Right = client.Right, Bottom = client.Bottom },
            rcSource = _part,
            opacity = _opacity,
            fVisible = true,
        };
        DwmUpdateThumbnailProperties(_thumbnail, ref properties);
    }

    private void CycleOpacity()
    {
        _opacity = _opacity switch { 255 => 200, 200 => 140, _ => 255 };
        Update();
    }

    /// <summary>Resizing keeps the part's shape (plus the header), so nothing is stretched.</summary>
    private IntPtr KeepShape(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_SIZING = 0x0214;
        if (msg != WM_SIZING)
            return IntPtr.Zero;
        var r = Marshal.PtrToStructure<RECT>(lParam);
        double header = Header * VisualTreeHelper.GetDpi(this).DpiScaleY;
        int edge = wParam.ToInt32();
        const int Top = 3, Bottom = 6;
        if (edge is Top or Bottom)
        {
            // Dragging the top or the bottom: the height leads and the width follows.
            int tall = r.Bottom - r.Top;
            r.Right = r.Left + (int)Math.Round(Math.Max(1, tall - header) * _aspect);
        }
        else
        {
            int width = r.Right - r.Left;
            int height = (int)Math.Round(width / _aspect + header);
            // Top corners move the top; the others the bottom.
            if (edge is 4 or 5)
                r.Top = r.Bottom - height;
            else
                r.Bottom = r.Top + height;
        }
        Marshal.StructureToPtr(r, lParam, false);
        handled = true;
        return new IntPtr(1);
    }

    private const uint GA_ROOT = 2;
    private const int DWM_TNP_RECTDESTINATION = 0x1, DWM_TNP_RECTSOURCE = 0x2, DWM_TNP_OPACITY = 0x4, DWM_TNP_VISIBLE = 0x8;

    [StructLayout(LayoutKind.Sequential)]
    private struct DWM_THUMBNAIL_PROPERTIES
    {
        public int dwFlags;
        public RECT rcDestination;
        public RECT rcSource;
        public byte opacity;
        [MarshalAs(UnmanagedType.Bool)] public bool fVisible;
        [MarshalAs(UnmanagedType.Bool)] public bool fSourceClientAreaOnly;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmRegisterThumbnail(IntPtr destination, IntPtr source, out IntPtr thumbnail);

    [DllImport("dwmapi.dll")]
    private static extern int DwmUnregisterThumbnail(IntPtr thumbnail);

    [DllImport("dwmapi.dll")]
    private static extern int DwmUpdateThumbnailProperties(IntPtr thumbnail, ref DWM_THUMBNAIL_PROPERTIES properties);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out int process);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hwnd, System.Text.StringBuilder text, int count);
}
