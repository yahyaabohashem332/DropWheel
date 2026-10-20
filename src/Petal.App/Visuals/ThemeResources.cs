using System.Windows;
using System.Windows.Media;

namespace Petal.App.Visuals;

/// <summary>
/// Publishes the current <see cref="Palette"/> as application resources ("Petal.Text", "Petal.Accent"…)
/// so XAML can use {DynamicResource} and every window follows light/dark and accent changes live.
/// </summary>
public static class ThemeResources
{
    public static void Install(Application app)
    {
        Apply(app, Theme.Current);
        Theme.Changed += () => app.Dispatcher.BeginInvoke(() => Apply(app, Theme.Current));
    }

    private static void Apply(Application app, Palette p)
    {
        var r = app.Resources;
        void Set(string key, Color c) => r[key] = Palette.Brush(c);
        void SetColor(string key, Color c) => r[key] = c;

        Set("Petal.Text", p.Text);
        Set("Petal.TextSecondary", p.TextSecondary);
        Set("Petal.TextDisabled", Color.FromArgb(0x70, p.Text.R, p.Text.G, p.Text.B));
        Set("Petal.Accent", p.Accent);
        Set("Petal.AccentHover", p.AccentStrong);
        Set("Petal.AccentPressed", Mix(p.Accent, p.IsDark ? Colors.Black : Colors.Black, 0.2));
        Set("Petal.AccentSoft", p.AccentSoft);
        Set("Petal.OnAccent", p.TextOnAccent);
        Set("Petal.Window", p.WindowBackground);
        Set("Petal.Surface", p.Surface);
        Set("Petal.SurfaceStrong", p.SurfaceStrong);
        Set("Petal.Divider", p.Divider);
        Set("Petal.Control", p.Control);
        Set("Petal.ControlHover", p.ControlHover);
        Set("Petal.ControlPressed", p.ControlPressed);
        Set("Petal.ControlBorder", p.IsDark ? Color.FromArgb(0x24, 255, 255, 255) : Color.FromArgb(0x1E, 0, 0, 0));
        Set("Petal.Track", p.IsDark ? Color.FromArgb(0x36, 255, 255, 255) : Color.FromArgb(0x1C, 0, 0, 0));
        Set("Petal.Thumb", p.IsDark ? Color.FromRgb(0xF2, 0xF2, 0xF4) : Colors.White);
        Set("Petal.Preview", p.PreviewBackground);
        Set("Petal.Danger", p.Danger);
        Set("Petal.Success", p.Success);
        Set("Petal.Field", p.IsDark ? Color.FromArgb(0x1E, 255, 255, 255) : Colors.White);
        Set("Petal.Popup", p.IsDark ? Color.FromRgb(0x2C, 0x2C, 0x32) : Colors.White);
        Set("Petal.Hover", p.IsDark ? Color.FromArgb(0x16, 255, 255, 255) : Color.FromArgb(0x0C, 0, 0, 0));
        Set("Petal.Selection", Color.FromArgb(0x38, p.Accent.R, p.Accent.G, p.Accent.B));
        SetColor("Petal.AccentColor", p.Accent);
        SetColor("Petal.ShadowColor", p.IsDark ? Colors.Black : Color.FromRgb(0x20, 0x20, 0x40));
        r["Petal.ShadowOpacity"] = p.IsDark ? 0.5 : 0.16;
    }

    private static Color Mix(Color a, Color b, double t) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));
}
