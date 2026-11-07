using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using Petal.App.Localization;
using Petal.App.Util;
using Petal.Core.Models;

namespace Petal.App.Settings;

public enum HealthState
{
    Good,
    Note,
    Problem,
}

/// <summary>One thing Petal checked about itself: how it is, in a line, more when it helps, and a fix when there is one.</summary>
public sealed record HealthCheck(string Area, HealthState State, string Summary, string? Detail = null, string? FixLabel = null, Action? Fix = null);

/// <summary>
/// Petal looks itself over: the keys and mouse it listens to, its shortcuts, the programs and models it
/// uses, the disk, its own files, File Explorer, recent errors and its connections. Read only; nothing is
/// changed unless a fix is chosen. The report leaves out what is personal (snippets, workspaces, notes).
/// </summary>
public static class Health
{
    public static async Task<IReadOnlyList<HealthCheck>> RunAsync(CancellationToken cancellationToken = default)
    {
        var checks = new List<HealthCheck>
        {
            Version(),
            StartUp(),
            Input(),
            Shortcuts(),
        };
        checks.Add(await Task.Run(() => Helpers(cancellationToken), cancellationToken));
        checks.Add(await Task.Run(Models, cancellationToken));
        checks.Add(await Task.Run(Graphics, cancellationToken));
        checks.Add(await Task.Run(Disk, cancellationToken));
        checks.Add(await Task.Run(OwnFiles, cancellationToken));
        checks.Add(Explorer());
        checks.Add(await Task.Run(Errors, cancellationToken));
        checks.Add(Connections());
        return checks;
    }

    private static string PetalVersion => Shell.UpdateService.Current.ToString(3);

    private static HealthCheck Version()
    {
        var where = Shell.Packaged.IsPackaged ? L.T("from the Microsoft Store") : L.T("from DropWheel's own setup");
        return new HealthCheck("DropWheel", HealthState.Good, L.F("DropWheel {0}, {1}", PetalVersion, where),
            $"Windows {Environment.OSVersion.Version} · .NET {Environment.Version} · {SettingsStore.Directory}");
    }

    /// <summary>Whether every part of DropWheel started, the icon by the clock included, and why one didn't.</summary>
    private static HealthCheck StartUp()
    {
        var failed = StartupParts.Failed;
        var parts = failed.Select(f => f.Part).ToList();
        if ((System.Windows.Application.Current as App)?.TrayShown == false)
            parts.Add("The icon by the clock");
        if (parts.Count == 0)
            return new HealthCheck("Start-up", HealthState.Good, L.T("Every part of DropWheel started"));
        return new HealthCheck("Start-up", HealthState.Problem, L.F("Didn't start: {0}", StartupParts.Names(parts)),
            failed.Count == 0 ? null : string.Join("\n", failed.Select(f => $"{L.T(f.Part)}: {f.Reason}")),
            L.T("Open the Activity Log"), () => Open(Log.FilePath));
    }

    private static HealthCheck Input()
    {
        if (SettingsStore.Current.Paused)
            return new HealthCheck("Keys and mouse", HealthState.Note, L.T("DropWheel is paused: dragging with a key doesn't open the wheel"),
                FixLabel: L.T("Turn DropWheel On"), Fix: () =>
                {
                    SettingsStore.Current.Paused = false;
                    SettingsStore.Save();
                });
        bool running = Petal.App.Input.GlobalInputHook.Current?.IsRunning == true;
        return running
            ? new HealthCheck("Keys and mouse", HealthState.Good, L.T("DropWheel sees drags and key taps"))
            : new HealthCheck("Keys and mouse", HealthState.Problem, L.T("DropWheel can't see drags and key taps"),
                L.T("Another program may be keeping them from DropWheel, or Windows stopped it after a long freeze. Closing and opening DropWheel again usually brings them back."));
    }

    private static HealthCheck Shortcuts()
    {
        var s = SettingsStore.Current;
        var taken = new List<string>();
        void Check(string? shortcut, bool held)
        {
            if (!string.IsNullOrWhiteSpace(shortcut) && !held)
                taken.Add(shortcut);
        }
        Check(s.QuickShortcut, (System.Windows.Application.Current as App)?.Hotkey?.Current is not null);
        Check(s.PaletteShortcut, Commands.CommandPalette.ShortcutHeld);
        Check(s.DictationShortcut, Words.Dictation.ShortcutHeld);
        Check(s.VoiceShortcut, Words.VoiceCommand.ShortcutHeld);
        Check(s.CircleShortcut, Screen.CircleWindow.ShortcutHeld);
        return taken.Count == 0
            ? new HealthCheck("Shortcuts", HealthState.Good, L.T("Every shortcut works"), string.Join(" · ", new[] { s.QuickShortcut, s.PaletteShortcut, s.DictationShortcut, s.VoiceShortcut, s.CircleShortcut }.Where(k => !string.IsNullOrWhiteSpace(k))))
            : new HealthCheck("Shortcuts", HealthState.Problem, L.F("Another program has {0}", string.Join(", ", taken)),
                L.T("Choose another shortcut in Settings, or close the program that uses it."), L.T("Open Settings"), () => App.Current.ShowSettings());
    }

    private static HealthCheck Helpers(CancellationToken ct)
    {
        var tools = Path.Combine(AppContext.BaseDirectory, "tools");
        var missing = new List<string>();
        // Where Petal itself finds it: beside it, or installed on the PC.
        var ffmpeg = Petal.Core.Util.ToolLocator.Ffmpeg ?? "";
        string ffmpegVersion = "";
        if (!File.Exists(ffmpeg))
        {
            missing.Add("FFmpeg");
        }
        else
        {
            try
            {
                using var process = Process.Start(new ProcessStartInfo(ffmpeg, "-hide_banner -version") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true })!;
                ffmpegVersion = process.StandardOutput.ReadLine() ?? "";
                if (!process.WaitForExit(8000))
                {
                    process.Kill();
                    missing.Add("FFmpeg");
                }
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                missing.Add("FFmpeg");
            }
        }
        if (!File.Exists(Path.Combine(tools, "exiftool", "exiftool.exe")) && !Directory.Exists(Path.Combine(tools, "exiftool")))
            missing.Add("ExifTool");
        ct.ThrowIfCancellationRequested();
        return missing.Count == 0
            ? new HealthCheck("Helper programs", HealthState.Good, L.T("FFmpeg and ExifTool are in place"), ffmpegVersion)
            : new HealthCheck("Helper programs", HealthState.Problem, L.F("Missing or not starting: {0}", string.Join(", ", missing)),
                L.T("Video, sound and photo details need them. Installing DropWheel again puts them back."));
    }

    private static HealthCheck Models()
    {
        var present = ModelCatalog.All.Where(ModelStore.IsAvailable).ToList();
        long size = present.Sum(m => m.Size);
        var detail = present.Count == 0 ? L.T("None yet: each downloads the first time a tool needs it.") : string.Join(", ", present.Select(m => m.Name));
        return new HealthCheck("AI models", HealthState.Good, L.F("{0} of {1} on this PC · {2}", present.Count, ModelCatalog.All.Count, Sizes.Format(size)), detail,
            L.T("Open the Models Folder"), () => Open(ModelStore.Folder));
    }

    /// <summary>The display adapters Windows has drivers for, by their names, from the registry (no WMI needed).</summary>
    internal static IReadOnlyList<string> GraphicsCards()
    {
        var names = new List<string>();
        using var adapters = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
        if (adapters is null)
            return names;
        foreach (var sub in adapters.GetSubKeyNames().Where(n => n.All(char.IsDigit)))
        {
            using var key = adapters.OpenSubKey(sub);
            if (key?.GetValue("DriverDesc") is string name && name.Length > 0 && !name.Contains("Basic Display", StringComparison.OrdinalIgnoreCase)
                && !name.Contains("Remote", StringComparison.OrdinalIgnoreCase) && !names.Contains(name))
                names.Add(name);
        }
        return names;
    }

    private static HealthCheck Graphics()
    {
        try
        {
            var names = GraphicsCards();
            return new HealthCheck("Graphics", HealthState.Good, names.Count > 0 ? string.Join(", ", names) : L.T("No graphics card was named"),
                L.T("The AI tools use the graphics card through DirectML when they can, and the processor otherwise."));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return new HealthCheck("Graphics", HealthState.Note, L.T("Windows didn't say which graphics card this PC has"));
        }
    }

    private static HealthCheck Disk()
    {
        var drives = new[] { Path.GetPathRoot(Path.GetTempPath()), Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)) }
            .Where(r => r is not null).Distinct(StringComparer.OrdinalIgnoreCase).Select(r => new DriveInfo(r!)).ToList();
        var low = drives.Where(d => d.IsReady && d.AvailableFreeSpace < 2L * 1024 * 1024 * 1024).ToList();
        var text = string.Join(" · ", drives.Where(d => d.IsReady).Select(d => L.F("{0} free on {1}", Sizes.Format(d.AvailableFreeSpace), d.Name.TrimEnd('\\'))));
        return low.Count == 0
            ? new HealthCheck("Disk space", HealthState.Good, text)
            : new HealthCheck("Disk space", HealthState.Problem, L.T("Little room left"), text + " — " + L.T("Videos and AI tools need a few gigabytes while they work."));
    }

    private static HealthCheck OwnFiles()
    {
        var settingsFile = Path.Combine(SettingsStore.Directory, "settings.json");
        try
        {
            if (File.Exists(settingsFile))
                _ = JsonNode.Parse(File.ReadAllText(settingsFile));
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException)
        {
            return new HealthCheck("Settings and history", HealthState.Problem, L.T("The settings file can't be read"),
                ex.Message, L.T("Open the Settings Folder"), () => Open(SettingsStore.Directory));
        }
        long log = File.Exists(Log.FilePath) ? new FileInfo(Log.FilePath).Length : 0;
        return new HealthCheck("Settings and history", HealthState.Good,
            L.F("Settings read · {0} in the history · activity log {1}", Jobs.History.Entries.Count, Sizes.Format(log)), SettingsStore.Directory,
            L.T("Open the Settings Folder"), () => Open(SettingsStore.Directory));
    }

    private static HealthCheck Explorer()
    {
        var s = SettingsStore.Current;
        var parts = new List<string>();
        if (!Shell.Packaged.IsPackaged)
        {
            using var menu = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Classes\*\shell\Petal");
            parts.Add(L.T(menu is null ? "Right-click entry: off" : "Right-click entry: on"));
            var covered = Shell.ExplorerThumbnails.Covered(Microsoft.Win32.Registry.CurrentUser);
            parts.Add(covered.Count == 0 ? L.T("Thumbnails: off") : L.F("Thumbnails for {0}", string.Join(" ", covered)));
        }
        parts.Add(L.T(s.QuickLook ? "Quick Look: on" : "Quick Look: off"));
        return new HealthCheck("File Explorer", HealthState.Good, string.Join(" · ", parts));
    }

    /// <summary>Errors written to the activity log in the last week, and the latest of them.</summary>
    internal static (int Count, string? Latest) RecentErrors(IEnumerable<string> lines, DateTime now)
    {
        int count = 0;
        string? latest = null;
        foreach (var line in lines)
        {
            if (!line.Contains(" ERROR ", StringComparison.Ordinal) || line.Length < 19)
                continue;
            if (!DateTime.TryParse(line[..19], System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeLocal, out var at) || now - at > TimeSpan.FromDays(7))
                continue;
            count++;
            latest = line;
        }
        return (count, latest);
    }

    private static HealthCheck Errors()
    {
        if (!File.Exists(Log.FilePath))
            return new HealthCheck("Errors lately", HealthState.Good, L.T("No errors were written down"));
        IEnumerable<string> lines;
        try
        {
            lines = Tail(Log.FilePath, 2 * 1024 * 1024).Split('\n');
        }
        catch (IOException)
        {
            return new HealthCheck("Errors lately", HealthState.Note, L.T("The activity log is busy"));
        }
        var (count, latest) = RecentErrors(lines, DateTime.Now);
        var open = (Label: L.T("Open the Activity Log"), Run: (Action)(() => Open(Log.FilePath)));
        return count == 0
            ? new HealthCheck("Errors lately", HealthState.Good, L.T("No errors in the last week"), FixLabel: open.Label, Fix: open.Run)
            : new HealthCheck("Errors lately", HealthState.Note, L.P(count, "{0} error in the last week", "{0} errors in the last week"),
                latest is { Length: > 300 } ? latest[..300] + "…" : latest, open.Label, open.Run);
    }

    private static HealthCheck Connections()
    {
        var s = SettingsStore.Current;
        var parts = new List<string>
        {
            L.T(s.LocalApi ? "Other programs can run petals" : "Other programs can't run petals"),
            L.T(Actions.PcDrop.Receiving ? "Receiving files from other PCs" : "Not receiving files from other PCs"),
        };
        return new HealthCheck("Connections", HealthState.Good, string.Join(" · ", parts), L.T("DropWheel goes online only to download a model you ask for, to use a cloud model you chose with your own key, to look for updates when an address is set, or to fetch today's exchange rates when you convert a currency."));
    }

    private static string Tail(string path, int bytes)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (stream.Length > bytes)
            stream.Seek(-bytes, SeekOrigin.End);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static void Open(string path)
    {
        try
        {
            if (File.Exists(path))
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            else
            {
                Directory.CreateDirectory(path);
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        {
            Log.Error(ex, "Opening from DropWheel Health");
        }
    }

    /// <summary>The checks as plain text, for the report.</summary>
    public static string Text(IReadOnlyList<HealthCheck> checks)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"DropWheel Health · {DateTime.Now:yyyy-MM-dd HH:mm}");
        sb.AppendLine();
        foreach (var c in checks)
        {
            sb.Append(c.State switch { HealthState.Good => "[ok]   ", HealthState.Note => "[note] ", _ => "[!]    " });
            sb.Append(c.Area).Append(": ").AppendLine(c.Summary);
            if (c.Detail is { Length: > 0 } detail)
                sb.Append("       ").AppendLine(detail);
        }
        return sb.ToString();
    }

    /// <summary>The settings for the report, without what is personal: snippets, workspaces, the PC's name, folders and notes.</summary>
    internal static string PlainSettings(string json)
    {
        if (JsonNode.Parse(json) is not JsonObject settings)
            return "{}";
        foreach (var personal in new[] { "Snippets", "Workspaces", "DropId", "Destinations", "OutputFolder", "Automations", "SearchFolders", "Favourites",
                     "ScanFolder", "SavedFolders", "Watermark", "UpdateFeed", "Recipes", "Wheels", "FocusApps", "RecentCustom", "PetalColours" })
        {
            var key = settings.Select(p => p.Key).FirstOrDefault(k => k.Equals(personal, StringComparison.OrdinalIgnoreCase));
            if (key is not null)
                settings[key] = "(left out)";
        }
        return settings.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>
    /// A ZIP on the desktop with the checks, the end of the activity log and the settings without what is
    /// personal, to send with a question. Nothing is sent by Petal.
    /// </summary>
    public static string SaveReport(IReadOnlyList<HealthCheck> checks)
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var path = Petal.Core.Jobs.OutputNaming.Unique(desktop, $"DropWheel Health {DateTime.Now:yyyy-MM-dd HH.mm}", "zip");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        void Add(string name, string text)
        {
            using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
            writer.Write(text);
        }
        Add("report.txt", Text(checks));
        if (File.Exists(Log.FilePath))
            Add("petal.log", Tail(Log.FilePath, 1024 * 1024));
        var settingsFile = Path.Combine(SettingsStore.Directory, "settings.json");
        if (File.Exists(settingsFile))
        {
            try
            {
                Add("settings.json", PlainSettings(File.ReadAllText(settingsFile)));
            }
            catch (System.Text.Json.JsonException)
            {
                Add("settings.json", "(unreadable)");
            }
        }
        return path;
    }
}
