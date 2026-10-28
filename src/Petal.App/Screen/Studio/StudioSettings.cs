using Petal.App.Settings;
using Petal.Core.Recording;

namespace Petal.App.Screen.Studio;

/// <summary>Where a new studio project starts: the look and video size last saved with, the zoom set in Settings.</summary>
internal static class StudioSettings
{
    public static StudioLook Look
    {
        get
        {
            var s = SettingsStore.Current;
            return s.StudioLook ?? new StudioLook { Background = s.PolishBackground, PointerSize = s.PolishLargeCursor ? 1.5 : 1.15, ClickRings = s.PolishClickRings };
        }
    }

    public static StudioOutput Output => SettingsStore.Current.StudioOutput ?? new StudioOutput();

    /// <summary>How far the view goes in by itself (1 keeps it still).</summary>
    public static double Zoom => Math.Clamp(SettingsStore.Current.PolishZoom, 1, 4);

    /// <summary>Keeps a look for the next recording; Settings' own switches follow it.</summary>
    public static void Remember(StudioLook look, StudioOutput output)
    {
        var s = SettingsStore.Current;
        s.StudioLook = look;
        s.StudioOutput = output;
        s.PolishBackground = look.Background;
        s.PolishClickRings = look.ClickRings;
        s.PolishLargeCursor = look.PointerSize >= 1.3;
        SettingsStore.Save();
    }

    /// <summary>Settings' switches changed: the kept look follows them.</summary>
    public static void FromSettings()
    {
        var s = SettingsStore.Current;
        if (s.StudioLook is { } look)
            s.StudioLook = look with
            {
                Background = s.PolishBackground,
                ClickRings = s.PolishClickRings,
                PointerSize = s.PolishLargeCursor == look.PointerSize >= 1.3 ? look.PointerSize : s.PolishLargeCursor ? 1.5 : 1.15,
            };
    }
}
