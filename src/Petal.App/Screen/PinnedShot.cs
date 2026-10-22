using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Petal.App.Input;
using Petal.App.Localization;
using static Petal.App.Interop.NativeMethods;

namespace Petal.App.Screen;

/// <summary>
/// A piece of the screen kept on top of every window: to copy numbers from, compare with, or keep an eye
/// on. It appears exactly where the area was; drag it anywhere, the mouse wheel makes it bigger or
/// smaller, Ctrl and the wheel see through it, a right-click has copy, save and the conversion wheel, and
/// Esc or a double-click puts it away.
/// </summary>
internal sealed class PinnedShot : Window
{
    private static readonly List<PinnedShot> Open = [];
    private readonly BitmapSource _picture;
    private readonly RECT _area;
    private readonly Action<string, ScreenPoint>? _openFiles;
    private readonly Border _frame;
    private readonly TextBlock _level = new() { Foreground = Brushes.White, FontSize = 11, FontWeight = FontWeights.SemiBold };
    private readonly Border _levelPill;
    private double _zoom = 1;
    private IntPtr _handle;

    public static int Count => Open.Count;

    public static void Start(Action<string, ScreenPoint>? openFiles) =>
        Wheels.Screenshots.ChooseArea("Drag over the area to pin on top · Esc cancels", false, (picture, area) => new PinnedShot(picture, area, openFiles).Show());

    /// <summary>Takes every pinned picture off the screen.</summary>
    public static void CloseAll()
    {
        foreach (var pin in Open.ToList())
            pin.Close();
    }

    private PinnedShot(BitmapSource picture, RECT area, Action<string, ScreenPoint>? openFiles)
    {
        _picture = picture;
        _area = area;
        _openFiles = openFiles;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        Title = L.T("Pinned Screenshot");
        L.Apply(this, mirror: false);
        Left = -32000;
        var image = new Image { Source = picture, Stretch = Stretch.Fill };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        _levelPill = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(7, 1, 7, 2),
            Margin = new Thickness(6),
            Background = new SolidColorBrush(Color.FromArgb(0xD0, 0x1E, 0x1E, 0x25)),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Opacity = 0,
            IsHitTestVisible = false,
            Child = _level,
        };
        var grid = new Grid();
        grid.Children.Add(image);
        grid.Children.Add(_levelPill);
        _frame = new Border
        {
            BorderThickness = new Thickness(1.5),
            BorderBrush = new SolidColorBrush(Visuals.Theme.Current.Accent),
            Margin = new Thickness(12),
            Child = grid,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 14, ShadowDepth = 3, Opacity = 0.45 },
            Cursor = Cursors.SizeAll,
        };
        Content = _frame;
        ContextMenu = Menu();
        SourceInitialized += (_, _) =>
        {
            _handle = new WindowInteropHelper(this).Handle;
            Place(1);
        };
        Loaded += (_, _) => Open.Add(this);
        Closed += (_, _) => Open.Remove(this);
        // A short pulse says "this one is a copy, floating".
        Loaded += (_, _) => _frame.BeginAnimation(OpacityProperty, new DoubleAnimation(0.4, 1, TimeSpan.FromMilliseconds(260)));
    }

    /// <summary>Sized to the area's pixels times the zoom, around the same middle (the margin for the shadow included).</summary>
    private void Place(double zoom)
    {
        GetWindowRect(_handle, out var now);
        var scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        int margin = (int)Math.Round(12 * scale);
        int w = (int)Math.Round(_area.Width * zoom) + margin * 2, h = (int)Math.Round(_area.Height * zoom) + margin * 2;
        int cx = now.Left < -20000 ? _area.Left + _area.Width / 2 : now.Left + now.Width / 2;
        int cy = now.Left < -20000 ? _area.Top + _area.Height / 2 : now.Top + now.Height / 2;
        SetWindowPos(_handle, HWND_TOPMOST, cx - w / 2, cy - h / 2, w, h, SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

    private ContextMenu Menu()
    {
        var menu = new ContextMenu();
        MenuItem Item(string text, Action act)
        {
            var item = new MenuItem { Header = L.T(text) };
            item.Click += (_, _) => act();
            menu.Items.Add(item);
            return item;
        }
        Item("Copy", () =>
        {
            try { Clipboard.SetImage(_picture); } catch (System.Runtime.InteropServices.ExternalException) { }
        });
        Item("Save to Screenshots", () =>
        {
            var path = Wheels.Screenshots.Save(_picture);
            Hud.HudCard.ShowAt(Hud.HudCard.CornerPoint(), "", "").Succeed(L.T("Screenshot saved"), System.IO.Path.GetFileName(path), null);
        });
        Item("Open the Wheel for It", () =>
        {
            var path = Wheels.Screenshots.Save(_picture);
            GetWindowRect(_handle, out var r);
            _openFiles?.Invoke(path, new ScreenPoint(r.Left + r.Width / 2, r.Top + r.Height / 2));
        });
        menu.Items.Add(new Separator());
        Item("Actual Size", () => SetZoom(1));
        Item("See Through", () => Opacity = Opacity > 0.9 ? 0.5 : 1);
        menu.Items.Add(new Separator());
        Item("Close", Close);
        Item("Close All Pinned", CloseAll);
        return menu;
    }

    private void SetZoom(double zoom)
    {
        _zoom = Math.Clamp(zoom, 0.2, 4);
        Place(_zoom);
        _level.Text = $"{Math.Round(_zoom * 100)}%";
        Flash();
    }

    private void Flash() => _levelPill.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(400)) { BeginTime = TimeSpan.FromMilliseconds(700) });

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (e.ClickCount == 2)
        {
            Close();
            return;
        }
        DragMove();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            Opacity = Math.Clamp(Opacity + (e.Delta > 0 ? 0.1 : -0.1), 0.2, 1);
            _level.Text = $"{Math.Round(Opacity * 100)}%";
            Flash();
        }
        else
        {
            SetZoom(_zoom * (e.Delta > 0 ? 1.1 : 1 / 1.1));
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape)
            Close();
        else if (e.Key == Key.C && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            try { Clipboard.SetImage(_picture); } catch (System.Runtime.InteropServices.ExternalException) { }
        }
    }
}
