using System.Windows.Media;
using Microsoft.Win32;

namespace Petal.App.Visuals;

public enum ThemePreference
{
    System,
    Light,
    Dark,
}

/// <summary>The accent colour: Petal's violet, the one Windows uses, or one of a few others.</summary>
public enum AccentChoice
{
    Violet,
    Windows,
    Ocean,
    Mint,
    Rose,
    Amber,
    Graphite,
    Custom,
}

/// <summary>Colours for the current light/dark appearance. Brushes are frozen and shared.</summary>
public sealed record Palette
{
    public required bool IsDark { get; init; }
    public required Color Accent { get; init; }
    public required Color AccentStrong { get; init; }
    public required Color AccentSoft { get; init; }
    public required Color Text { get; init; }
    public required Color TextSecondary { get; init; }
    public required Color TextOnAccent { get; init; }
    public required Color GlassTint { get; init; }
    public required Color GlassEdge { get; init; }
    public required Color PetalTop { get; init; }
    public required Color PetalBottom { get; init; }
    public required Color PetalStroke { get; init; }
    public required Color HubFill { get; init; }
    public required Color PillFill { get; init; }
    public required Color Shadow { get; init; }
    public required Color WindowBackground { get; init; }
    public required Color Surface { get; init; }
    public required Color SurfaceStrong { get; init; }
    public required Color Divider { get; init; }
    public required Color Control { get; init; }
    public required Color ControlHover { get; init; }
    public required Color ControlPressed { get; init; }
    public required Color PreviewBackground { get; init; }
    public required Color Danger { get; init; }
    public required Color Success { get; init; }

    public static SolidColorBrush Brush(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}

public static class Theme
{
    /// <summary>Petal's signature violet.</summary>
    public static readonly Color BrandAccent = Color.FromRgb(0x7B, 0x5C, 0xFF);

    public static event Action? Changed;

    public static ThemePreference Preference { get; private set; } = ThemePreference.System;
    public static AccentChoice Accent { get; private set; } = AccentChoice.Violet;
    private static Color _custom = BrandAccent;
    public static bool UseWindowsAccent => Accent == AccentChoice.Windows;

    public static Palette Current { get; private set; } = Build(IsSystemDark(), BrandAccent);

    public static void Configure(ThemePreference preference, bool useWindowsAccent) =>
        Configure(preference, useWindowsAccent ? AccentChoice.Windows : AccentChoice.Violet);

    /// <param name="custom">The colour for <see cref="AccentChoice.Custom"/>, as #RRGGBB.</param>
    public static void Configure(ThemePreference preference, AccentChoice accent, string? custom = null)
    {
        Preference = preference;
        Accent = accent;
        if (Tools.Parts.ColourMath.TryParseHex(custom, out var chosen))
            _custom = chosen;
        Refresh();
    }

    /// <summary>The colour an accent choice stands for (Windows' own accent read now).</summary>
    public static Color ColourOf(AccentChoice accent) => accent switch
    {
        AccentChoice.Windows => WindowsAccent() ?? BrandAccent,
        AccentChoice.Ocean => Color.FromRgb(0x2F, 0x7B, 0xEB),
        AccentChoice.Mint => Color.FromRgb(0x1C, 0xA0, 0x7A),
        AccentChoice.Rose => Color.FromRgb(0xE0, 0x45, 0x7B),
        AccentChoice.Amber => Color.FromRgb(0xE0, 0x88, 0x24),
        AccentChoice.Graphite => Color.FromRgb(0x5A, 0x62, 0x70),
        AccentChoice.Custom => _custom,
        _ => BrandAccent,
    };

    /// <summary>The same colours with greys in place of the accent: the monochrome wheel.</summary>
    public static Palette Mono(Palette p)
    {
        var grey = p.IsDark ? Color.FromRgb(0xE4, 0xE4, 0xE8) : Color.FromRgb(0x2E, 0x2F, 0x35);
        return p with
        {
            Accent = grey,
            AccentStrong = grey,
            AccentSoft = Color.FromArgb(0x40, grey.R, grey.G, grey.B),
            TextOnAccent = p.IsDark ? Color.FromRgb(0x16, 0x16, 0x1A) : Colors.White,
        };
    }

    public static void Refresh()
    {
        var dark = Preference switch
        {
            ThemePreference.Light => false,
            ThemePreference.Dark => true,
            _ => IsSystemDark(),
        };
        var accent = ColourOf(Accent);
        Current = Build(dark, accent);
        Changed?.Invoke();
    }

    public static bool IsSystemDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch
        {
            return false;
        }
    }

    private static Color? WindowsAccent()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
            if (key?.GetValue("AccentColor") is int abgr)
            {
                var u = unchecked((uint)abgr);
                return Color.FromRgb((byte)(u & 0xFF), (byte)((u >> 8) & 0xFF), (byte)((u >> 16) & 0xFF));
            }
        }
        catch
        {
            // Fall back to the brand colour.
        }
        return null;
    }

    private static Color Mix(Color a, Color b, double t) => Color.FromArgb(
        (byte)(a.A + (b.A - a.A) * t),
        (byte)(a.R + (b.R - a.R) * t),
        (byte)(a.G + (b.G - a.G) * t),
        (byte)(a.B + (b.B - a.B) * t));

    private static Color A(byte alpha, Color c) => Color.FromArgb(alpha, c.R, c.G, c.B);

    public static Palette Build(bool dark, Color accent)
    {
        var white = Colors.White;
        var black = Colors.Black;
        return dark
            ? new Palette
            {
                IsDark = true,
                Accent = accent,
                AccentStrong = Mix(accent, white, 0.12),
                AccentSoft = A(0x40, accent),
                Text = Color.FromRgb(0xF4, 0xF4, 0xF6),
                TextSecondary = Color.FromRgb(0xA4, 0xA6, 0xAE),
                TextOnAccent = Tools.Parts.ColourMath.Readable(accent),
                GlassTint = Color.FromArgb(0x8C, 0x22, 0x23, 0x28),
                GlassEdge = Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF),
                PetalTop = Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF),
                PetalBottom = Color.FromArgb(0x1C, 0xFF, 0xFF, 0xFF),
                PetalStroke = Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF),
                HubFill = Color.FromArgb(0x78, 0x1A, 0x1A, 0x20),
                PillFill = Color.FromArgb(0xD8, 0x30, 0x31, 0x38),
                Shadow = Color.FromArgb(0x70, 0, 0, 0),
                WindowBackground = Color.FromRgb(0x20, 0x20, 0x24),
                Surface = Color.FromRgb(0x2A, 0x2A, 0x30),
                SurfaceStrong = Color.FromRgb(0x34, 0x34, 0x3B),
                Divider = Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF),
                Control = Color.FromRgb(0x3A, 0x3A, 0x42),
                ControlHover = Color.FromRgb(0x44, 0x44, 0x4D),
                ControlPressed = Color.FromRgb(0x33, 0x33, 0x3A),
                PreviewBackground = Color.FromRgb(0x0E, 0x0E, 0x11),
                Danger = Color.FromRgb(0xFF, 0x6B, 0x6B),
                Success = Color.FromRgb(0x4A, 0xD6, 0x8C),
            }
            : new Palette
            {
                IsDark = false,
                Accent = accent,
                AccentStrong = Mix(accent, black, 0.12),
                AccentSoft = A(0x2E, accent),
                Text = Color.FromRgb(0x16, 0x16, 0x1A),
                TextSecondary = Color.FromRgb(0x6E, 0x70, 0x78),
                TextOnAccent = Tools.Parts.ColourMath.Readable(accent),
                GlassTint = Color.FromArgb(0x8A, 0xF6, 0xF6, 0xF8),
                GlassEdge = Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF),
                PetalTop = Color.FromArgb(0xC4, 0xFF, 0xFF, 0xFF),
                PetalBottom = Color.FromArgb(0x84, 0xFF, 0xFF, 0xFF),
                PetalStroke = Color.FromArgb(0x26, 0x2A, 0x30, 0x40),
                HubFill = Color.FromArgb(0xA0, 0xFA, 0xFA, 0xFC),
                PillFill = Color.FromArgb(0xEE, 0xFF, 0xFF, 0xFF),
                Shadow = Color.FromArgb(0x3C, 0x1C, 0x22, 0x36),
                WindowBackground = Color.FromRgb(0xF7, 0xF7, 0xF8),
                Surface = Colors.White,
                SurfaceStrong = Color.FromRgb(0xEE, 0xEE, 0xF1),
                Divider = Color.FromArgb(0x1C, 0x10, 0x14, 0x24),
                Control = Color.FromRgb(0xE8, 0xE8, 0xEC),
                ControlHover = Color.FromRgb(0xDE, 0xDE, 0xE3),
                ControlPressed = Color.FromRgb(0xD2, 0xD2, 0xD8),
                PreviewBackground = Color.FromRgb(0x15, 0x15, 0x18),
                Danger = Color.FromRgb(0xE0, 0x3E, 0x3E),
                Success = Color.FromRgb(0x1F, 0xA8, 0x62),
            };
    }
}
