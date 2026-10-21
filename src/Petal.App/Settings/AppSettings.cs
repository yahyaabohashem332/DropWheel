using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Petal.App.Input;
using Petal.App.Visuals;
using Petal.Core.Jobs;

namespace Petal.App.Settings;

public enum OutputLocation
{
    BesideOriginal,
    Folder,
}

public sealed class AppSettings
{
    public ModifierKey ConvertKey { get; set; } = ModifierKey.Shift;
    public ModifierKey ToolsKey { get; set; } = ModifierKey.Control;
    public ModifierKey ActionsKey { get; set; } = ModifierKey.Alt;

    public bool Sounds { get; set; } = true;
    public bool ShowKeyHints { get; set; } = true;
    public string QuickShortcut { get; set; } = Input.Shortcut.Default.ToString();
    public ThemePreference Theme { get; set; } = ThemePreference.System;
    public Localization.UiLanguage Language { get; set; } = Localization.UiLanguage.System;
    public bool UseWindowsAccent { get; set; }
    public bool LaunchAtStartup { get; set; } = true;
    public bool Paused { get; set; }
    public bool SelectResultInExplorer { get; set; } = true;
    public bool ExplorerContextMenu { get; set; } = true;
    public OutputLocation Output { get; set; } = OutputLocation.BesideOriginal;
    public string? OutputFolder { get; set; }
    public bool OnboardingDone { get; set; }
    public bool KeepHistory { get; set; } = true;

    /// <summary>The quick shortcut with nothing selected opens the wheel for the clipboard's contents.</summary>
    public bool ClipboardShortcut { get; set; } = true;

    /// <summary>Search by Meaning: Petal indexes the folders below while the PC is idle.</summary>
    public bool SearchEnabled { get; set; }

    /// <summary>The folders searched; empty means Pictures, Documents, Desktop, Downloads and Videos.</summary>
    public List<string> SearchFolders { get; set; } = [];

    /// <summary>Where Scan with Phone saves its PDFs; null is Documents\Scans.</summary>
    public string? ScanFolder { get; set; }

    /// <summary>An update feed (update.json over https, or a file path). Empty: no updates are looked for.</summary>
    public string UpdateFeed { get; set; } = "";
    public bool CheckForUpdates { get; set; } = true;
    public DateTime? LastUpdateCheck { get; set; }
    public string? SkippedVersion { get; set; }
    public ConversionSettings Conversion { get; set; } = new();
    public WatermarkSettings Watermark { get; set; } = new();

    /// <summary>Starred conversions shown first on the conversion wheel, in this order.</summary>
    public List<Favourite> Favourites { get; set; } = [];

    /// <summary>Chains of steps, run as one petal on the tools wheel.</summary>
    public List<Petal.Recipes.Recipe> Recipes { get; set; } = [];

    /// <summary>Fingerprints of custom petals the user allowed to run without asking.</summary>
    public HashSet<string> ApprovedPetals { get; set; } = [];

    /// <summary>Watched folders whose new files are converted automatically.</summary>
    public List<WatchRule> Automations { get; set; } = [];

    public string? OutputDirectoryFor(string input) =>
        Output == OutputLocation.Folder && !string.IsNullOrWhiteSpace(OutputFolder) && Directory.Exists(OutputFolder)
            ? OutputFolder
            : null;
}

/// <summary>The last watermark used, so the next one starts from it.</summary>
public sealed class WatermarkSettings
{
    public bool UsePicture { get; set; }
    public string Text { get; set; } = "";
    public string? PicturePath { get; set; }
    public string Color { get; set; } = "#FFFFFF";
    public double Size { get; set; } = 0.3;
    public double Opacity { get; set; } = 0.6;
    public string Place { get; set; } = "BottomRight";
    public bool Tiled { get; set; }
}

public static class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Directory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Petal");

    public static string FilePath => Path.Combine(Directory, "settings.json");

    public static event Action<AppSettings>? Saved;

    public static AppSettings Current { get; private set; } = new();

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                Current = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Options) ?? new AppSettings();
        }
        catch
        {
            Current = new AppSettings();
        }
        return Current;
    }

    public static void Save()
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(Current, Options));
            File.Move(temp, FilePath, overwrite: true);
        }
        catch
        {
            // Settings are a convenience; never crash over them.
        }
        Saved?.Invoke(Current);
    }
}
