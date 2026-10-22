using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Petal.App.Input;
using Petal.App.Localization;
using Petal.App.Visuals;
using static Petal.App.Interop.NativeMethods;

namespace Petal.App.Screen;

/// <summary>
/// Zooms into a still of the monitor under the pointer: the mouse wheel goes in and out around the
/// pointer, moving the pointer moves across the picture, a click (or D) starts drawing on what is
/// shown, Ctrl+C copies it, and Esc or a right-click goes back.
/// </summary>
internal sealed class ScreenZoom : MonitorWindow
{
    private const double MinZoom = 1, MaxZoom = 8;
    private static ScreenZoom? _open;
    private readonly BitmapSource _still;
    private readonly Image _image;
    private readonly ScaleTransform _scale = new();
    private readonly TranslateTransform _move = new();
    private readonly Border _hint;
    private readonly Action<string, ScreenPoint>? _openFiles;
    private double _zoom = 1, _target = 2;
    private Point _pointer;

    public static bool IsOpen => _open is not null;

    public static void Toggle(Action<string, ScreenPoint>? openFiles)
    {
        if (_open is not null)
        {
            _open.Close();
            return;
        }
        var monitor = ScreenNative.UnderPointer();
        if (ScreenUi.Still(monitor.Bounds) is not { } still)
            return;
        _open = new ScreenZoom(monitor.Bounds, still, openFiles);
        _open.Closed += (_, _) => _open = null;
        _open.Show();
    }

    private ScreenZoom(RECT bounds, BitmapSource still, Action<string, ScreenPoint>? openFiles) : base(bounds)
    {
        _still = still;
        _openFiles = openFiles;
        Title = L.T("Zoom");
        Cursor = Cursors.Cross;
        Background = Brushes.Black;
        _image = new Image { Source = still, Stretch = Stretch.Fill, RenderTransform = new TransformGroup { Children = { _scale, _move } } };
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality);
        _hint = new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14, 7, 14, 8),
            Background = new SolidColorBrush(Color.FromArgb(0xE0, 0x1E, 0x1E, 0x25)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 40),
            IsHitTestVisible = false,
            Child = new TextBlock
            {
                Text = L.T("Mouse wheel zooms · Click or D to draw · Ctrl+C copies · Esc goes back"),
                Foreground = Brushes.White,
                FontFamily = UiFonts.Text,
                FontSize = 13,
                FlowDirection = L.Direction,
            },
        };
        var root = new Grid { ClipToBounds = true };
        root.Children.Add(_image);
        root.Children.Add(_hint);
        Content = root;
        Loaded += (_, _) =>
        {
            GetCursorPos(out var p);
            _pointer = ToLocal(new ScreenPoint(p.X, p.Y));
            Activate();
            Focus();
            CompositionTarget.Rendering += OnFrame;
            var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(500)) { BeginTime = TimeSpan.FromSeconds(3) };
            _hint.BeginAnimation(OpacityProperty, fade);
        };
        Closed += (_, _) => CompositionTarget.Rendering -= OnFrame;
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        // Ease towards the zoom asked for; the point under the pointer stays where it is.
        _zoom += (_target - _zoom) * 0.22;
        if (Math.Abs(_target - _zoom) < 0.001)
            _zoom = _target;
        _scale.ScaleX = _scale.ScaleY = _zoom;
        _move.X = -_pointer.X * (_zoom - 1);
        _move.Y = -_pointer.Y * (_zoom - 1);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        _pointer = e.GetPosition(this);
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        ZoomBy(e.Delta > 0 ? 1.25 : 0.8);
    }

    private void ZoomBy(double factor) => _target = Math.Clamp(_target * factor, MinZoom, MaxZoom);

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        Draw();
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        Close();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        switch (e.Key)
        {
            case Key.Escape: Close(); break;
            case Key.D: Draw(); break;
            case Key.C when ctrl: Copy(); break;
            case Key.OemPlus or Key.Add or Key.Up: ZoomBy(1.25); break;
            case Key.OemMinus or Key.Subtract or Key.Down: ZoomBy(0.8); break;
            case Key.D0 or Key.NumPad0: _target = 1; break;
            default: return;
        }
        e.Handled = true;
    }

    /// <summary>What is on the screen now, at the monitor's pixels.</summary>
    private BitmapSource View()
    {
        _hint.Visibility = Visibility.Collapsed;
        _zoom = _target;
        OnFrame(null, EventArgs.Empty);
        var root = (FrameworkElement)Content;
        root.UpdateLayout();
        return ScreenUi.Render(root, Bounds.Width, Bounds.Height, Scale);
    }

    private void Copy()
    {
        try
        {
            Clipboard.SetImage(View());
            Hud.HudCard.ShowAt(Hud.HudCard.CornerPoint(), "", "").Succeed(L.T("Copied"), L.T("Paste it in any app"), null);
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
        }
    }

    /// <summary>Drawing over the zoomed picture, as it is now.</summary>
    private void Draw()
    {
        var view = View();
        var openFiles = _openFiles;
        Close();
        DrawOverlay.Toggle(DrawBoard.Still, openFiles, view);
    }
}

/// <summary>
/// A round magnifying glass that follows the pointer over everything, without taking clicks: Ctrl and
/// the mouse wheel change how much it magnifies; Esc or the petal again puts it away. It stays out of
/// screenshots, so it never magnifies itself.
/// </summary>
internal sealed class Lens : Window
{
    private const double Size = 240;
    private static Lens? _open;
    private readonly Image _image = new() { Stretch = Stretch.Fill };
    private readonly TextBlock _level;
    private readonly Border _levelPill;
    private IntPtr _handle;
    private double _zoom = 2.5;
    private IDisposable? _wheel, _escape;
    private POINT _last = new() { X = int.MinValue };

    public static bool IsOpen => _open is not null;

    public static void Toggle()
    {
        if (_open is not null)
        {
            _open.Close();
            return;
        }
        _open = new Lens();
        _open.Closed += (_, _) => _open = null;
        _open.Show();
    }

    private Lens()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Width = Size + 24;
        Height = Size + 24;
        Left = -32000;
        Title = L.T("Magnifier");
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.NearestNeighbor);
        var glass = new Grid { Width = Size, Height = Size, Clip = new EllipseGeometry(new Point(Size / 2, Size / 2), Size / 2, Size / 2) };
        glass.Children.Add(_image);
        // A small cross where the pointer is.
        glass.Children.Add(new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse($"M {Size / 2 - 7},{Size / 2} H {Size / 2 + 7} M {Size / 2},{Size / 2 - 7} V {Size / 2 + 7}"),
            Stroke = new SolidColorBrush(Color.FromArgb(0xC0, 0xFF, 0x3B, 0x30)),
            StrokeThickness = 1.5,
        });
        var ring = new Border
        {
            Width = Size + 6,
            Height = Size + 6,
            CornerRadius = new CornerRadius(Size / 2 + 3),
            BorderThickness = new Thickness(3),
            BorderBrush = new LinearGradientBrush(Color.FromArgb(0xF0, 0xFF, 0xFF, 0xFF), Color.FromArgb(0xA0, 0x90, 0x90, 0xA0), 60),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 16, ShadowDepth = 3, Opacity = 0.45 },
            Child = glass,
        };
        _level = new TextBlock { Foreground = Brushes.White, FontFamily = UiFonts.Text, FontSize = 11, FontWeight = FontWeights.SemiBold };
        _levelPill = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(7, 1, 7, 2),
            Background = new SolidColorBrush(Color.FromArgb(0xD0, 0x1E, 0x1E, 0x25)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 14),
            Child = _level,
        };
        var root = new Grid { IsHitTestVisible = false };
        root.Children.Add(ring);
        root.Children.Add(_levelPill);
        Content = root;
        ShowLevel();
        SourceInitialized += (_, _) =>
        {
            _handle = new WindowInteropHelper(this).Handle;
            long ex = (long)GetWindowLongPtr(_handle, GWL_EXSTYLE) | WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
            SetWindowLongPtr(_handle, GWL_EXSTYLE, new IntPtr(ex));
            ScreenNative.SetWindowDisplayAffinity(_handle, ScreenNative.WDA_EXCLUDEFROMCAPTURE);
        };
        Loaded += (_, _) =>
        {
            CompositionTarget.Rendering += OnFrame;
            _wheel = ScreenHooks.CtrlWheel(step =>
            {
                _zoom = Math.Clamp(_zoom * (step > 0 ? 1.25 : 0.8), 1.25, 12);
                ShowLevel();
                _last.X = int.MinValue;
            });
            _escape = ScreenHooks.Escape(Close);
        };
        Closed += (_, _) =>
        {
            CompositionTarget.Rendering -= OnFrame;
            _wheel?.Dispose();
            _escape?.Dispose();
        };
    }

    private void ShowLevel() => _level.Text = $"{_zoom:0.#}×";

    private void OnFrame(object? sender, EventArgs e)
    {
        if (_handle == IntPtr.Zero || !GetCursorPos(out var p))
            return;
        // Move with the pointer; take a fresh picture every frame, since what is under it may be moving.
        GetWindowRect(_handle, out var r);
        if (p.X != _last.X || p.Y != _last.Y)
        {
            SetWindowPos(_handle, HWND_TOPMOST, p.X - r.Width / 2, p.Y - r.Height / 2, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE);
            _last = p;
        }
        double scale = r.Width / Math.Max(1, ActualWidth);
        int side = Math.Max(8, (int)Math.Round(Size * scale / _zoom));
        var pixels = Overlay.GlassCapture.CaptureScreen(p.X - side / 2, p.Y - side / 2, side, side);
        if (pixels is null)
            return;
        for (int i = 3; i < pixels.Length; i += 4)
            pixels[i] = 255;
        _image.Source = BitmapSource.Create(side, side, 96, 96, PixelFormats.Bgra32, null, pixels, side * 4);
    }
}
