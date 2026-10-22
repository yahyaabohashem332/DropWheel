using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Petal.App.Input;
using Petal.App.Localization;
using static Petal.App.Interop.NativeMethods;

namespace Petal.App.Screen;

/// <summary>
/// Darkens every screen except a soft circle around the pointer, for showing people where to look.
/// Clicks pass through to the apps; Ctrl and the mouse wheel make the circle bigger or smaller; Esc or the
/// petal again turns it off.
/// </summary>
internal static class Spotlight
{
    private static readonly List<(MonitorWindow Window, Rectangle Shade, RadialGradientBrush Light)> Windows = [];
    private static IDisposable? _wheel, _escape;
    private static double _radius = 150;

    public static bool IsOn => Windows.Count > 0;

    public static void Toggle()
    {
        if (IsOn)
            Off();
        else
            On();
    }

    private static void On()
    {
        foreach (var monitor in ScreenNative.Monitors())
        {
            var light = new RadialGradientBrush
            {
                MappingMode = BrushMappingMode.Absolute,
                RadiusX = _radius,
                RadiusY = _radius,
                Center = new Point(-10000, -10000),
                GradientOrigin = new Point(-10000, -10000),
                GradientStops =
                {
                    new GradientStop(Colors.Transparent, 0),
                    new GradientStop(Colors.Transparent, 0.78),
                    new GradientStop(Color.FromArgb(0xC8, 0x05, 0x05, 0x0A), 1),
                },
            };
            var shade = new Rectangle { Fill = light, Opacity = 0 };
            var window = new MonitorWindow(monitor.Bounds, clickThrough: true, noActivate: true) { Content = shade, Title = L.T("Spotlight") };
            window.Show();
            shade.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(260)));
            Windows.Add((window, shade, light));
        }
        CompositionTarget.Rendering += OnFrame;
        _wheel = ScreenHooks.CtrlWheel(step => _radius = Math.Clamp(_radius * (step > 0 ? 1.15 : 0.87), 50, 700));
        _escape = ScreenHooks.Escape(Off);
    }

    private static void Off()
    {
        CompositionTarget.Rendering -= OnFrame;
        _wheel?.Dispose();
        _escape?.Dispose();
        _wheel = _escape = null;
        foreach (var (window, shade, _) in Windows)
        {
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(180));
            fade.Completed += (_, _) => window.Close();
            shade.BeginAnimation(UIElement.OpacityProperty, fade);
        }
        Windows.Clear();
    }

    private static void OnFrame(object? sender, EventArgs e)
    {
        GetCursorPos(out var p);
        var point = new ScreenPoint(p.X, p.Y);
        foreach (var (window, _, light) in Windows)
        {
            // Only the monitor with the pointer has a light; the others stay dark.
            var local = window.Contains(point) ? window.ToLocal(point) : new Point(-10000, -10000);
            light.Center = light.GradientOrigin = local;
            light.RadiusX = light.RadiusY = _radius;
        }
    }
}
