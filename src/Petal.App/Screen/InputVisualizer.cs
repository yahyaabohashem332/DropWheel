using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Petal.App.Input;
using Petal.App.Localization;
using Petal.App.Visuals;
using Petal.App.Wheels;

namespace Petal.App.Screen;

/// <summary>
/// Shows clicks and keys on the screen, for recordings and presentations: a ring where the mouse was
/// clicked (amber for the left button, blue for the right, green for the middle) and the shortcuts
/// pressed, in pills at the bottom of the screen. "Every key" shows typing too; the default shows only
/// presses with Ctrl, Alt or Win, so passwords never appear. Clicks pass through; the petal turns it off.
/// </summary>
internal static class InputVisualizer
{
    private static readonly List<(MonitorWindow Window, Canvas Canvas, StackPanel Keys)> Windows = [];
    private static readonly HashSet<int> Held = [];
    private static Action<int, ScreenPoint>? _clicks;
    private static Action<int, bool>? _keys;
    private static Border? _typing;
    private static DateTime _typedAt;

    public static bool IsOn => Windows.Count > 0;

    /// <summary>Typing shows too, not only shortcuts.</summary>
    public static bool EveryKey => Settings.SettingsStore.Current.ShowEveryKey;

    public static void Toggle()
    {
        if (IsOn)
            Off();
        else
            On();
    }

    private static void On()
    {
        if (GlobalInputHook.Current is not { } hook)
            return;
        foreach (var monitor in ScreenNative.Monitors())
        {
            var canvas = new Canvas();
            var keys = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 70) };
            var root = new Grid();
            root.Children.Add(canvas);
            root.Children.Add(keys);
            var window = new MonitorWindow(monitor.Bounds, clickThrough: true, noActivate: true) { Content = root, Title = L.T("Show Clicks and Keys") };
            window.Show();
            Windows.Add((window, canvas, keys));
        }
        var dispatcher = Application.Current.Dispatcher;
        _clicks = (button, at) => dispatcher.BeginInvoke(() => Ripple(button, at));
        _keys = (vk, down) => dispatcher.BeginInvoke(() => Key(vk, down));
        hook.ButtonPressed += _clicks;
        hook.KeyChanged += _keys;
    }

    private static void Off()
    {
        if (GlobalInputHook.Current is { } hook)
        {
            hook.ButtonPressed -= _clicks;
            hook.KeyChanged -= _keys;
        }
        foreach (var (window, _, _) in Windows)
            window.Close();
        Windows.Clear();
        Held.Clear();
        _typing = null;
    }

    private static void Ripple(int button, ScreenPoint at)
    {
        foreach (var (window, canvas, _) in Windows)
        {
            if (!window.Contains(at))
                continue;
            var p = window.ToLocal(at);
            var colour = button switch
            {
                2 => Color.FromRgb(0x3B, 0x82, 0xF6),
                3 => Color.FromRgb(0x22, 0xC5, 0x5E),
                _ => Color.FromRgb(0xF5, 0x9E, 0x0B),
            };
            var ring = new Ellipse { Width = 1, Height = 1, StrokeThickness = 3, Stroke = new SolidColorBrush(colour), Fill = new SolidColorBrush(Color.FromArgb(0x40, colour.R, colour.G, colour.B)) };
            var grow = new ScaleTransform(1, 1);
            ring.RenderTransform = grow;
            ring.RenderTransformOrigin = new Point(0.5, 0.5);
            ring.Width = ring.Height = 52;
            Canvas.SetLeft(ring, p.X - 26);
            Canvas.SetTop(ring, p.Y - 26);
            canvas.Children.Add(ring);
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var time = TimeSpan.FromMilliseconds(520);
            grow.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.25, 1, time) { EasingFunction = ease });
            grow.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.25, 1, time) { EasingFunction = ease });
            var fade = new DoubleAnimation(1, 0, time) { BeginTime = TimeSpan.FromMilliseconds(120) };
            fade.Completed += (_, _) => canvas.Children.Remove(ring);
            ring.BeginAnimation(UIElement.OpacityProperty, fade);
        }
    }

    private static bool IsModifier(int vk) => vk is 0x10 or 0x11 or 0x12 or 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5 or 0x5B or 0x5C;

    private static void Key(int vk, bool down)
    {
        if (!down)
        {
            Held.Remove(vk);
            return;
        }
        bool repeat = !Held.Add(vk);
        if (IsModifier(vk) || repeat && !EveryKey)
            return;
        bool ctrl = Held.Overlaps([0x11, 0xA2, 0xA3]), alt = Held.Overlaps([0x12, 0xA4, 0xA5]);
        bool shift = Held.Overlaps([0x10, 0xA0, 0xA1]), win = Held.Overlaps([0x5B, 0x5C]);
        var key = KeyInterop.KeyFromVirtualKey(vk);
        bool shortcut = ctrl || alt || win;
        // Function keys, Esc and the like read as shortcuts on their own.
        bool special = key is >= System.Windows.Input.Key.F1 and <= System.Windows.Input.Key.F24 or System.Windows.Input.Key.Escape or System.Windows.Input.Key.PrintScreen;
        if (!shortcut && !special)
        {
            if (EveryKey)
                Typed(key, shift);
            return;
        }
        var parts = new List<string>();
        if (ctrl) parts.Add("Ctrl");
        if (alt) parts.Add("Alt");
        if (shift) parts.Add("Shift");
        if (win) parts.Add("Win");
        parts.Add(KeySender.NameOf(key));
        _typing = null;
        Pill(string.Join(" + ", parts), wide: true);
    }

    /// <summary>Typing gathers into one pill while it goes on.</summary>
    private static void Typed(Key key, bool shift)
    {
        string text = key switch
        {
            >= System.Windows.Input.Key.A and <= System.Windows.Input.Key.Z => ((char)((shift ? 'A' : 'a') + (key - System.Windows.Input.Key.A))).ToString(),
            >= System.Windows.Input.Key.D0 and <= System.Windows.Input.Key.D9 when !shift => ((char)('0' + (key - System.Windows.Input.Key.D0))).ToString(),
            >= System.Windows.Input.Key.NumPad0 and <= System.Windows.Input.Key.NumPad9 => ((char)('0' + (key - System.Windows.Input.Key.NumPad0))).ToString(),
            System.Windows.Input.Key.Space => "␣",
            System.Windows.Input.Key.Return => "⏎",
            System.Windows.Input.Key.Back => "⌫",
            System.Windows.Input.Key.Tab => "⇥",
            System.Windows.Input.Key.Left => "←",
            System.Windows.Input.Key.Right => "→",
            System.Windows.Input.Key.Up => "↑",
            System.Windows.Input.Key.Down => "↓",
            _ => KeySender.NameOf(key),
        };
        if (_typing is { Child: TextBlock block } pill && DateTime.Now - _typedAt < TimeSpan.FromSeconds(1.4) && block.Text.Length < 40)
        {
            block.Text += text;
            Linger(pill);
        }
        else
        {
            _typing = Pill(text, wide: false);
        }
        _typedAt = DateTime.Now;
    }

    private static Border? Pill(string text, bool wide)
    {
        GetCursorPos(out var p);
        var at = new ScreenPoint(p.X, p.Y);
        var (_, _, keys) = Windows.FirstOrDefault(w => w.Window.Contains(at));
        keys ??= Windows.FirstOrDefault().Keys;
        if (keys is null)
            return null;
        var pill = new Border
        {
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(wide ? 20 : 16, 9, wide ? 20 : 16, 11),
            Margin = new Thickness(0, 6, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
            Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x1C, 0x1C, 0x22)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, ShadowDepth = 4, Opacity = 0.4 },
            Child = new TextBlock
            {
                Text = text,
                Foreground = Brushes.White,
                FontFamily = UiFonts.Display,
                FontWeight = FontWeights.SemiBold,
                FontSize = 26,
                FlowDirection = FlowDirection.LeftToRight,
            },
        };
        keys.Children.Add(pill);
        while (keys.Children.Count > 3)
            keys.Children.RemoveAt(0);
        pill.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120)));
        Linger(pill);
        return pill;
    }

    /// <summary>A pill stays a moment after its last key, then fades.</summary>
    private static void Linger(Border pill)
    {
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(400)) { BeginTime = TimeSpan.FromSeconds(1.6) };
        fade.Completed += (_, _) =>
        {
            if (pill.Opacity < 0.05 && pill.Parent is Panel panel)
                panel.Children.Remove(pill);
        };
        pill.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    private static bool GetCursorPos(out Interop.NativeMethods.POINT p) => Interop.NativeMethods.GetCursorPos(out p);
}
