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

    /// <summary>The accent colour; null for settings from before there was a choice (then <see cref="UseWindowsAccent"/> decides).</summary>
    public AccentChoice? Accent { get; set; }

    public AccentChoice AccentColour => Accent ?? (UseWindowsAccent ? AccentChoice.Windows : AccentChoice.Violet);

    /// <summary>The accent colour chosen freely, as #RRGGBB, for <see cref="AccentChoice.Custom"/>.</summary>
    public string? AccentCustom { get; set; }

    /// <summary>The colours used lately in the colour panel, newest first.</summary>
    public List<string> RecentColours { get; set; } = [];

    /// <summary>The size a floating window was last given, by the kind of file it shows: [width, height].</summary>
    public Dictionary<string, double[]> FloatSizes { get; set; } = [];

    /// <summary>What study files were last asked to be, in the person's own words, newest first.</summary>
    public List<string> StudyRequests { get; set; } = [];

    /// <summary>Requests kept by name to use again (the star beside the request), at most <see cref="Petal.Recipes.Study.StudyTemplates.Most"/>.</summary>
    public List<Petal.Recipes.Study.StudyTemplate> StudyTemplates { get; set; } = [];

    // ---------- the wheel ----------

    public Overlay.WheelStyle WheelStyle { get; set; } = Overlay.WheelStyle.Glass;
    public Overlay.WheelMotion WheelMotion { get; set; } = Overlay.WheelMotion.Bloom;
    public Overlay.SoundStyle SoundStyle { get; set; } = Overlay.SoundStyle.Soft;
    public bool ShowPetalNames { get; set; } = true;
    public bool GrowPetal { get; set; } = true;
    public bool ShowNumberKeys { get; set; } = true;
    public bool ScrollSwitchesWheels { get; set; } = true;
    public Overlay.CancelZone CancelZone { get; set; } = Overlay.CancelZone.Small;

    /// <summary>Open on the wheel used last, with the petal chosen last lit up for the keyboard.</summary>
    public bool RememberLastPetal { get; set; } = true;

    /// <summary>The centre says what a petal will make (its size, a cut-out) while the pointer rests on it.</summary>
    public bool PetalPreviews { get; set; } = true;

    /// <summary>Petals the user gave a colour, by their key ("tool:Compress", "format:webp"), as "#RRGGBB".</summary>
    public Dictionary<string, string> PetalColours { get; set; } = [];

    // ---------- wheels without files ----------

    public Petal.App.Wheels.NothingSelected NothingSelected { get; set; } = Petal.App.Wheels.NothingSelected.Wheels;

    /// <summary>Quick actions, media, app wheels and the user's own, in the order they come.</summary>
    public List<Petal.App.Wheels.UserWheel> Wheels { get; set; } = [];

    /// <summary>Which ready-made wheels have been added, so new ones arrive once and removed ones stay away.</summary>
    public int WheelsVersion { get; set; }

    public Petal.App.Wheels.MouseTrigger MouseButton { get; set; } = Petal.App.Wheels.MouseTrigger.None;

    // ---------- the actions wheel ----------

    /// <summary>The actions shown on the wheel itself, in order; the rest are under More. Null for the usual set.</summary>
    public List<string>? ActionsOnWheel { get; set; }

    /// <summary>Folders on the actions wheel that dropped files are copied or moved to.</summary>
    public List<Petal.App.Wheels.FolderDestination> Destinations { get; set; } = [];

    // ---------- statistics ----------

    /// <summary>Count what Petal does, on this PC only, for the statistics page.</summary>
    public bool KeepStatistics { get; set; } = true;
    public bool LaunchAtStartup { get; set; } = true;
    public bool Paused { get; set; }
    public bool SelectResultInExplorer { get; set; } = true;
    public bool ExplorerContextMenu { get; set; } = true;
    public OutputLocation Output { get; set; } = OutputLocation.BesideOriginal;
    public string? OutputFolder { get; set; }
    public bool OnboardingDone { get; set; }
    public bool KeepHistory { get; set; } = true;

    /// <summary>From before <see cref="NothingSelected"/>: the shortcut with nothing selected opened the clipboard's wheel. Kept so old files still read.</summary>
    public bool ClipboardShortcut { get; set; } = true;

    /// <summary>Search by Meaning: Petal indexes the folders below while the PC is idle.</summary>
    public bool SearchEnabled { get; set; }

    /// <summary>The folders searched; empty means Pictures, Documents, Desktop, Downloads and Videos.</summary>
    public List<string> SearchFolders { get; set; } = [];

    /// <summary>Where Scan with Phone saves its PDFs; null is Documents\Scans.</summary>
    public string? ScanFolder { get; set; }

    /// <summary>The folders chosen for what DropWheel saves, by <see cref="SavedKind"/>; what isn't here goes to its usual folder.</summary>
    public Dictionary<string, string> SavedFolders { get; set; } = [];

    /// <summary>
    /// An update feed (update.json over https, or a file path). Empty: DropWheel's own releases on GitHub
    /// (<see cref="Petal.Core.Util.Updates.DefaultFeed"/>); <see cref="CheckForUpdates"/> off stops the daily look.
    /// </summary>
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

    // ---------- the screen and text ----------

    /// <summary>Showing keys on the screen shows typing too, not only shortcuts.</summary>
    public bool ShowEveryKey { get; set; }

    /// <summary>Recordings take the microphone and the PC's own sound.</summary>
    public bool RecordMicrophone { get; set; }
    public bool RecordSystemSound { get; set; } = true;

    /// <summary>Recordings also show clicks and shortcuts.</summary>
    public bool RecordShowsClicks { get; set; }

    /// <summary>Recordings are polished: the camera zooms in on clicks, the pointer is smoothed, a background frames them.</summary>
    public bool RecordPolish { get; set; } = true;
    public string PolishBackground { get; set; } = "violet";

    /// <summary>How far the camera goes in on a click; 1 keeps it still.</summary>
    public double PolishZoom { get; set; } = 1.8;
    public bool PolishLargeCursor { get; set; } = true;
    public bool PolishClickRings { get; set; } = true;

    /// <summary>The cinematic studio's look and video size last used, so the next recording starts from them.</summary>
    public Petal.Core.Recording.StudioLook? StudioLook { get; set; }
    public Petal.Core.Recording.StudioOutput? StudioOutput { get; set; }

    /// <summary>Recordings show the webcam in a round bubble; where it sits (its middle, across and down the area) and how big (of the area's shorter side).</summary>
    public bool RecordCamera { get; set; }
    public double RecordCameraX { get; set; } = 0.86;
    public double RecordCameraY { get; set; } = 0.80;
    public double RecordCameraSize { get; set; } = 0.24;

    /// <summary>Dictation: the shortcut, the language spoken ("auto" to tell), and the accuracy chosen.</summary>
    public string DictationShortcut { get; set; } = "Ctrl+Win+J";
    public string DictationLanguage { get; set; } = "auto";
    public string DictationAccuracy { get; set; } = "Balanced";

    /// <summary>Clipboard history: kept while Petal runs, and remembered after it closes.</summary>
    public bool KeepClipboardHistory { get; set; } = true;
    public bool RememberClipboardHistory { get; set; }

    /// <summary>Where sticky notes appear when Petal starts: shown or tucked away.</summary>
    public bool NotesHidden { get; set; }

    /// <summary>The command palette's shortcut.</summary>
    public string PaletteShortcut { get; set; } = "Ctrl+Win+K";

    /// <summary>Circle Anything's shortcut: the screen stands still to circle something on it.</summary>
    public string CircleShortcut { get; set; } = "Ctrl+Win+C";

    /// <summary>Voice commands: say what you want and the command palette opens with it.</summary>
    public string VoiceShortcut { get; set; } = "Ctrl+Win+M";

    /// <summary>Live captions: from the PC's sound ("system") or the microphone ("mic"); shown as "both", "arabic", "original" or "english"; the language spoken.</summary>
    public string CaptionsSource { get; set; } = "system";
    public string CaptionsShow { get; set; } = "both";
    public string CaptionsLanguage { get; set; } = "auto";

    /// <summary>The country and paper last used for ID photos; empty until one is chosen.</summary>
    public string IdPhotoSpec { get; set; } = "";

    public string IdPhotoPaper { get; set; } = "";

    /// <summary>The time tracker goes on after a restart if it was on. It is off until asked for.</summary>
    public bool TimeTrackerOn { get; set; }

    /// <summary>Window titles are kept too; off, only programs' names are.</summary>
    public bool TimeTrackerTitles { get; set; }

    /// <summary>Programs whose time isn't counted.</summary>
    public List<string> TimeTrackerLeftOut { get; set; } = [];

    /// <summary>The language live captions are translated into; empty for the one the interface speaks.</summary>
    public string CaptionsInto { get; set; } = "";

    /// <summary>The language selected text is translated into; empty for the one the interface speaks.</summary>
    public string TranslateInto { get; set; } = "";

    /// <summary>What does the AI's jobs: "" for this PC, or a provider's name.</summary>
    public string AiWriting { get; set; } = "";

    public string AiTranslating { get; set; } = "";

    public string AiSeeing { get; set; } = "";

    /// <summary>The model chosen from each provider, by the provider's name.</summary>
    public Dictionary<string, string> AiModels { get; set; } = [];

    /// <summary>The folder downloaded AI models are kept in; empty for the usual one (%LOCALAPPDATA%\Petal\models).</summary>
    public string ModelsFolder { get; set; } = "";

    /// <summary>Focus sessions: the length chosen last, the apps that wait, and whether the sound is muted.</summary>
    public int FocusMinutes { get; set; } = 25;
    public List<string> FocusApps { get; set; } = [];
    public bool FocusMute { get; set; }

    /// <summary>Snippets: abbreviations that become longer text as they are typed.</summary>
    public bool SnippetsOn { get; set; } = true;

    /// <summary>Space shows the file selected in File Explorer or on the desktop.</summary>
    public bool QuickLook { get; set; } = true;

    /// <summary>File Explorer shows thumbnails and previews of design files, PDFs, 3D models, EPUBs, comic books and SVGs.</summary>
    public bool ExplorerThumbnails { get; set; } = true;

    /// <summary>Other programs of this user (Stream Deck, AutoHotkey, scripts, the command line) can run Petal's petals.</summary>
    public bool LocalApi { get; set; } = true;
    public List<Petal.Core.Text.Snippet> Snippets { get; set; } = Petal.Core.Text.Snippets.Starters();

    /// <summary>Other PCs with Petal may send files here (it asks each time); the id other PCs know this one by.</summary>
    public bool ReceiveFromPcs { get; set; }
    public string DropId { get; set; } = "";

    /// <summary>Workspaces: sets of apps arranged on the screens, opened together.</summary>
    public List<Petal.App.Screen.Workspace> Workspaces { get; set; } = [];

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
    internal static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    // What Petal wrote last, so its own writes aren't mistaken for changes made by hand.
    private static string _written = "";
    private static FileSystemWatcher? _watcher;
    private static System.Threading.Timer? _settle;

    /// <summary>
    /// The file changed outside Petal (edited by hand, or replaced by an import) and was read again: apply
    /// everything from it now. <see cref="Saved"/> is raised too.
    /// </summary>
    public static event Action<AppSettings>? Reloaded;

    /// <summary>
    /// Where settings and what Petal keeps beside them live: %APPDATA%\Petal. Tests and development runs point
    /// it at a folder of their own (PETAL_SETTINGS_DIR), so they never change the user's.
    /// </summary>
    public static string Directory { get; set; } =
        Environment.GetEnvironmentVariable("PETAL_SETTINGS_DIR") is { Length: > 0 } own ? own : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Petal");

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
            var json = JsonSerializer.Serialize(Current, Options);
            _written = json;
            File.WriteAllText(temp, json);
            File.Move(temp, FilePath, overwrite: true);
        }
        catch
        {
            // Settings are a convenience; never crash over them.
        }
        Saved?.Invoke(Current);
    }

    /// <summary>
    /// Watches settings.json: a change made by hand (in Notepad, or a synced copy) is read and applied a
    /// moment later. A file that doesn't read is left alone, and Petal keeps what it had.
    /// </summary>
    public static void Watch(System.Windows.Threading.Dispatcher ui)
    {
        if (_watcher is not null)
            return;
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            if (File.Exists(FilePath))
                _written = File.ReadAllText(FilePath);
            _watcher = new FileSystemWatcher(Directory, "settings.json") { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size };
            FileSystemEventHandler changed = (_, _) =>
            {
                _settle?.Dispose();
                _settle = new System.Threading.Timer(_ => ui.BeginInvoke(ReadAgain), null, 400, System.Threading.Timeout.Infinite);
            };
            _watcher.Changed += changed;
            _watcher.Created += changed;
            _watcher.Renamed += (s, e) => changed(s, e);
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            Log.Error(ex, "Watching the settings file");
        }
    }

    /// <summary>Reads settings.json again when it differs from what Petal wrote; true when it did.</summary>
    public static bool ReadAgain()
    {
        string json;
        try
        {
            json = File.ReadAllText(FilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        if (json == _written)
            return false;
        AppSettings? read;
        try
        {
            read = JsonSerializer.Deserialize<AppSettings>(json, Options);
        }
        catch (JsonException ex)
        {
            Log.Error(ex, "settings.json was changed but doesn't read; keeping the settings as they were");
            return false;
        }
        if (read is null)
            return false;
        _written = json;
        Replace(read);
        return true;
    }

    /// <summary>Puts other settings in place of these (an import, or a file edited by hand) and applies them.</summary>
    public static void Replace(AppSettings settings)
    {
        Current = settings;
        Reloaded?.Invoke(Current);
        Saved?.Invoke(Current);
    }
}
