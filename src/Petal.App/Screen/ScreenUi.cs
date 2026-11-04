using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using Petal.App.Input;
using Petal.App.Localization;
using Petal.App.Visuals;
using static Petal.App.Interop.NativeMethods;

namespace Petal.App.Screen;

/// <summary>
/// A borderless window laid exactly over one monitor, in its pixels. It can let clicks through to what is
/// under it, never take the keyboard, or stay out of screenshots and recordings.
/// </summary>
internal class MonitorWindow : Window
{
    private readonly bool _noActivate, _hideFromCapture;
    private bool _clickThrough;

    public MonitorWindow(RECT bounds, bool clickThrough = false, bool noActivate = false, bool hideFromCapture = false)
    {
        Bounds = bounds;
        _clickThrough = clickThrough;
        _noActivate = noActivate;
        _hideFromCapture = hideFromCapture;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = !noActivate;
        // Placed off screen until it is laid over its monitor in pixels.
        Left = -32000;
        Top = -32000;
        Width = 10;
        Height = 10;
        L.Apply(this, mirror: false);
        SourceInitialized += (_, _) =>
        {
            Handle = new WindowInteropHelper(this).Handle;
            ApplyStyles();
            if (_hideFromCapture)
                ScreenNative.SetWindowDisplayAffinity(Handle, ScreenNative.WDA_EXCLUDEFROMCAPTURE);
            SetWindowPos(Handle, HWND_TOPMOST, Bounds.Left, Bounds.Top, Bounds.Width, Bounds.Height, SWP_NOACTIVATE | SWP_SHOWWINDOW);
        };
    }

    public RECT Bounds { get; }

    public IntPtr Handle { get; private set; }

    /// <summary>Device pixels per unit of layout here.</summary>
    public double Scale => VisualTreeHelper.GetDpi(this).DpiScaleX;

    public bool ClickThrough
    {
        get => _clickThrough;
        set
        {
            _clickThrough = value;
            if (Handle != IntPtr.Zero)
                ApplyStyles();
        }
    }

    private void ApplyStyles()
    {
        long ex = (long)GetWindowLongPtr(Handle, GWL_EXSTYLE) | WS_EX_TOOLWINDOW;
        ex = _clickThrough ? ex | WS_EX_TRANSPARENT | WS_EX_LAYERED : ex & ~(long)WS_EX_TRANSPARENT;
        if (_noActivate)
            ex |= WS_EX_NOACTIVATE;
        SetWindowLongPtr(Handle, GWL_EXSTYLE, new IntPtr(ex));
    }

    /// <summary>A point on the screen, in this window's layout units.</summary>
    public Point ToLocal(ScreenPoint p) => new((p.X - Bounds.Left) / Scale, (p.Y - Bounds.Top) / Scale);

    public bool Contains(ScreenPoint p) => p.X >= Bounds.Left && p.X < Bounds.Right && p.Y >= Bounds.Top && p.Y < Bounds.Bottom;
}

/// <summary>
/// Keeps a window with the button that ends something in front of everything until it is closed on purpose. Another program
/// can take "always on top" away from it, full-screen viewers push it down, Show Desktop hides it, and a change of screen can
/// leave it off every monitor; nothing tells this process, so it looks twice a second and puts things right.
/// </summary>
internal sealed class KeepOnTop
{
    private readonly Window _bar;
    private readonly Func<IEnumerable<Window>> _under;
    private readonly Action? _lost;

    private KeepOnTop(Window bar, Func<IEnumerable<Window>>? under, Action? lost)
    {
        _bar = bar;
        _under = under ?? (() => []);
        _lost = lost;
    }

    /// <param name="bar">The window that must stay in reach, always raised to the very front.</param>
    /// <param name="under">Windows of the same tool that go under it. Raised only when they have lost their place, so they don't cover other programs' menus.</param>
    /// <param name="lost">Puts the bar back when it is on no monitor any more.</param>
    public static KeepOnTop Start(Window bar, Func<IEnumerable<Window>>? under = null, Action? lost = null)
    {
        var keep = new KeepOnTop(bar, under, lost);
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        timer.Tick += (_, _) => keep.Check();
        bar.Closed += (_, _) => timer.Stop();
        timer.Start();
        return keep;
    }

    /// <summary>Lets the window close only when <paramref name="finished"/> says so, or when the app is ending: Alt+F4 won't lose the button.</summary>
    public static void CloseOnlyWhen(Window bar, Func<bool> finished) =>
        bar.Closing += (_, e) => e.Cancel = !finished() && !bar.Dispatcher.HasShutdownStarted;

    internal void Check()
    {
        foreach (var window in _under())
            if (!IsUp(window))
                Raise(window);
        Raise(_bar);
        var handle = new WindowInteropHelper(_bar).Handle;
        if (handle != IntPtr.Zero && ScreenNative.MonitorFromWindow(handle, 0) == IntPtr.Zero)
            _lost?.Invoke();
    }

    private static bool IsUp(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        return handle == IntPtr.Zero || (ScreenNative.IsWindowVisible(handle) && !ScreenNative.IsIconic(handle)
            && ((long)GetWindowLongPtr(handle, GWL_EXSTYLE) & WS_EX_TOPMOST) != 0);
    }

    private static void Raise(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
            return;
        if (!window.IsVisible)
            window.Show();
        if (ScreenNative.IsIconic(handle))
            ScreenNative.ShowWindow(handle, ScreenNative.SW_SHOWNOACTIVATE);
        if (window.WindowState != WindowState.Normal)
            window.WindowState = WindowState.Normal;
        SetWindowPos(handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }
}

/// <summary>The look shared by the screen tools: floating dark-glass strips of round buttons.</summary>
internal static class ScreenUi
{
    /// <summary>
    /// Lets the window be moved by pressing on the empty parts of <paramref name="area"/>. A press on
    /// anything that can be clicked or typed in (a button, a chip with the hand pointer, a text box, a
    /// slider) is left to it: moving the window there would swallow the click.
    /// </summary>
    public static void DragFromBackground(Window window, UIElement area)
    {
        area.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState != MouseButtonState.Pressed || e.ClickCount > 1 || IsClickable(e.OriginalSource as DependencyObject, area))
                return;
            try
            {
                window.DragMove();
            }
            catch (InvalidOperationException)
            {
                // The button was already up: there is nothing to move.
            }
        };
    }

    /// <summary>Whether a press on <paramref name="hit"/> belongs to something clickable between it and <paramref name="area"/>.</summary>
    internal static bool IsClickable(DependencyObject? hit, UIElement area)
    {
        for (var d = hit; d is not null && !ReferenceEquals(d, area); d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
        {
            if (d is System.Windows.Controls.Primitives.ButtonBase or System.Windows.Controls.Primitives.TextBoxBase or PasswordBox or StripButton or Slider
                or ComboBox or System.Windows.Controls.Primitives.ScrollBar or System.Windows.Controls.Primitives.Thumb or ListBoxItem)
                return true;
            if (d is FrameworkElement { Cursor: { } cursor } && (cursor == Cursors.Hand || cursor == Cursors.IBeam))
                return true;
        }
        return false;
    }

    public static readonly Color Ink = Color.FromRgb(0x1C, 0x1C, 0x22);

    /// <summary>A rounded strip that floats over the screen.</summary>
    public static Border Strip(UIElement content, double radius = 16) => new Border
    {
        CornerRadius = new CornerRadius(radius),
        Background = new SolidColorBrush(Color.FromArgb(0xF0, 0x1E, 0x1E, 0x25)),
        BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
        BorderThickness = new Thickness(1),
        Padding = new Thickness(6),
        Child = content,
        Effect = new DropShadowEffect { BlurRadius = 24, ShadowDepth = 6, Direction = 270, Opacity = 0.45, Color = Colors.Black },
    }.WithForeground();

    private static Border WithForeground(this Border b)
    {
        TextElement.SetForeground(b, Brushes.White);
        TextElement.SetFontFamily(b, UiFonts.Text);
        return b;
    }

    /// <summary>A thin upright line between groups of buttons.</summary>
    public static FrameworkElement Divider() => new Border
    {
        Width = 1,
        Margin = new Thickness(5, 6, 5, 6),
        Background = new SolidColorBrush(Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF)),
    };

    /// <summary>The still of a monitor, in its pixels.</summary>
    public static BitmapSource? Still(RECT bounds) => Wheels.Screenshots.Capture(bounds.Left, bounds.Top, bounds.Width, bounds.Height);

    /// <summary>Renders an element at a monitor's pixel size.</summary>
    public static BitmapSource Render(FrameworkElement element, int pixelWidth, int pixelHeight, double scale)
    {
        var bitmap = new RenderTargetBitmap(pixelWidth, pixelHeight, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(element);
        bitmap.Freeze();
        return bitmap;
    }
}

/// <summary>A round button on a screen tool's strip: an icon, a hint, lit while it is the one in use.</summary>
internal sealed class StripButton : Border
{
    private readonly FrameworkElement _face;
    private bool _on;
    private Color _accent = Theme.Current.Accent;

    public StripButton(string? icon, string tip, Action click, UIElement? face = null, double size = 34)
    {
        Width = size;
        Height = size;
        CornerRadius = new CornerRadius(size / 2);
        Margin = new Thickness(1);
        Cursor = Cursors.Hand;
        ToolTip = L.T(tip);
        System.Windows.Automation.AutomationProperties.SetName(this, L.T(tip));
        Background = Brushes.Transparent;
        _face = (FrameworkElement?)face ?? Icons.Create(icon!, 18, Brushes.White, 1.9);
        _face.HorizontalAlignment = HorizontalAlignment.Center;
        _face.VerticalAlignment = VerticalAlignment.Center;
        Child = _face;
        MouseEnter += (_, _) => Paint(true);
        MouseLeave += (_, _) => Paint(false);
        MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            click();
        };
        MouseLeftButtonDown += (_, e) => e.Handled = true;
    }

    public bool IsOn
    {
        get => _on;
        set
        {
            _on = value;
            Paint(IsMouseOver);
        }
    }

    public Color Accent
    {
        set
        {
            _accent = value;
            Paint(IsMouseOver);
        }
    }

    private void Paint(bool hover)
    {
        Background = _on
            ? new SolidColorBrush(_accent)
            : hover ? new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)) : Brushes.Transparent;
        if (_face is System.Windows.Shapes.Path icon)
            icon.Stroke = _on && Luminance(_accent) > 170 ? new SolidColorBrush(ScreenUi.Ink) : Brushes.White;
    }

    private static double Luminance(Color c) => 0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B;
}

/// <summary>
/// The screen tools' share of the global hooks: the mouse wheel with Ctrl held (to size a lens or a
/// spotlight while another app has the keyboard) and Esc, both handed to the UI thread.
/// </summary>
internal static class ScreenHooks
{
    private static readonly List<Func<int, bool>> Wheels = [];
    private static readonly List<Action> Escapes = [];
    private static bool _installed;

    /// <summary>Ctrl and the mouse wheel: <paramref name="turn"/> gets +1 or -1 on the UI thread, and the app under the pointer doesn't scroll.</summary>
    public static IDisposable CtrlWheel(Action<int> turn)
    {
        Install();
        var dispatcher = Application.Current.Dispatcher;
        Func<int, bool> handler = delta =>
        {
            if ((GetAsyncKeyState(VK_CONTROL) & 0x8000) == 0)
                return false;
            dispatcher.BeginInvoke(() => turn(Math.Sign(delta)));
            return true;
        };
        lock (Wheels)
            Wheels.Add(handler);
        return new Undo(() => { lock (Wheels) Wheels.Remove(handler); });
    }

    /// <summary>Esc anywhere, on the UI thread.</summary>
    public static IDisposable Escape(Action escape)
    {
        Install();
        var dispatcher = Application.Current.Dispatcher;
        Action handler = () => dispatcher.BeginInvoke(escape);
        lock (Escapes)
            Escapes.Add(handler);
        return new Undo(() => { lock (Escapes) Escapes.Remove(handler); });
    }

    private static void Install()
    {
        if (_installed || GlobalInputHook.Current is not { } hook)
            return;
        _installed = true;
        hook.WheelFirst = delta =>
        {
            Func<int, bool>[] handlers;
            lock (Wheels)
                handlers = [.. Wheels];
            // The newest tool first; one is enough.
            for (int i = handlers.Length - 1; i >= 0; i--)
                if (handlers[i](delta))
                    return true;
            return false;
        };
        hook.EscapePressed += () =>
        {
            Action[] handlers;
            lock (Escapes)
                handlers = [.. Escapes];
            foreach (var h in handlers)
                h();
        };
    }

    private sealed class Undo(Action undo) : IDisposable
    {
        private Action? _undo = undo;

        public void Dispose()
        {
            _undo?.Invoke();
            _undo = null;
        }
    }
}
