using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media;
using Petal.App.Input;
using Petal.App.Localization;
using Petal.App.Overlay;
using Petal.App.Settings;

namespace Petal.App.Wheels;

/// <summary>Turns the user's wheels into what the wheel shows, with switches read as they are now.</summary>
public static class Rings
{
    private static readonly ConcurrentDictionary<string, ImageSource?> Icons = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The wheels to show over <paramref name="front"/>: its app's wheel first when there is one, then the
    /// quick, media and the user's own wheels in their order. Starts on the app's wheel, else on the one
    /// used last.
    /// </summary>
    public static (List<RingPage> Pages, int Start) For(AppSettings settings, IntPtr front, string? onlyWheel = null)
    {
        // The window petals act on, and read their switches from.
        Screen.WindowArranger.Front = front;
        return ForProgram(settings, RunningApps.ProgramOf(front), onlyWheel);
    }

    /// <summary>As <see cref="For"/>, for the program in front by name ("chrome"), or none.</summary>
    public static (List<RingPage> Pages, int Start) ForProgram(AppSettings settings, string? program, string? onlyWheel = null)
    {
        var pages = new List<RingPage>();
        var app = program is null ? null : settings.Wheels.FirstOrDefault(w =>
            w.Enabled && w.Kind == WheelKind.App && w.Apps.Any(a => a.Equals(program, StringComparison.OrdinalIgnoreCase)));
        if (app is not null)
            pages.Add(Page(app));
        foreach (var wheel in settings.Wheels.Where(w => w.Enabled && w.Kind != WheelKind.App))
            pages.Add(Page(wheel));
        int start = 0;
        if (onlyWheel is not null)
        {
            var wheel = settings.Wheels.FirstOrDefault(w => w.Id == onlyWheel);
            if (wheel is null) return (pages, 0);
            int at = pages.FindIndex(p => p.Key == onlyWheel);
            if (at < 0)
            {
                pages.Insert(0, Page(wheel));
                at = 0;
            }
            start = at;
        }
        else if (app is null && settings.RememberLastPetal && WheelMemory.LastRing is { } last)
        {
            start = Math.Max(0, pages.FindIndex(p => p.Key == last));
        }
        return (pages, start);
    }

    public static RingPage Page(UserWheel wheel) => new(wheel.Id, wheel.Name,
        () => new WheelContent(wheel.Items.SelectMany(e => e.Kind == EntryKind.Clips ? ClipPetals(e) : [Item(e)]).ToList(),
            wheel.Items.Any(e => e.Kind == EntryKind.Clips) ? L.T("Nothing copied yet. What you copy appears here.") : L.T("This wheel is empty. Add petals to it in Settings › Wheels.")),
        () => Hub(wheel));

    private static RingHub Hub(UserWheel wheel)
    {
        if (wheel.Kind == WheelKind.Media)
        {
            var playing = WheelActions.Playing;
            if (playing is null)
                return new RingHub(L.T("Media"), L.T("Nothing is playing"));
            var title = playing.Title.Length > 0 ? playing.Title : playing.App;
            return new RingHub(title, playing.Artist.Length > 0 ? playing.Artist : playing.App, playing.Cover, playing.Playing);
        }
        return new RingHub(L.T(wheel.Name), wheel.Kind == WheelKind.App ? L.T("Shortcuts") : "");
    }

    /// <summary>A petal for one of the user's entries, as it stands now.</summary>
    public static WheelItem Item(WheelEntry e)
    {
        var tint = Colour(e.Tint);
        switch (e.Kind)
        {
            case EntryKind.Action:
            {
                var action = WheelActions.Get(e.Action);
                if (action is null)
                    return new WheelItem("?", e.Action ?? "", "alert", e) { Unavailable = "This action isn't in this version of Petal", Blocked = true };
                return new WheelItem(e.Label ?? action.LiveLabel?.Invoke() ?? action.Label, e.Name ?? action.Name, e.Icon ?? action.Icon, e)
                {
                    Tint = tint,
                    State = action.State?.Invoke(),
                    Unavailable = action.Unavailable?.Invoke(),
                    Blocked = true,
                    Note = action.Note?.Invoke(),
                };
            }
            case EntryKind.App:
            {
                var name = e.Name ?? AppName(e.Path);
                bool there = e.Path is { Length: > 0 } p && (p.StartsWith("shell:", StringComparison.OrdinalIgnoreCase) || File.Exists(p) || Directory.Exists(p));
                return new WheelItem(e.Label ?? name.ToUpperInvariant(), name, e.Icon, e)
                {
                    Picture = e.Icon is null && e.Path is { } path ? IconOf(path) : null,
                    Tint = tint,
                    Note = "Open",
                    Unavailable = there ? null : "The program isn't there any more",
                    Blocked = true,
                };
            }
            case EntryKind.Keystroke:
            {
                var chords = KeySender.Parse(e.Keys);
                var name = e.Name ?? e.Keys ?? "";
                return new WheelItem(e.Label ?? name.ToUpperInvariant(), name, e.Icon ?? "keyboard", e)
                {
                    Tint = tint,
                    Note = chords is null ? null : string.Join("  ", chords.Select(c => c.Display)),
                    Unavailable = chords is null ? "These keys can't be pressed" : null,
                    Blocked = true,
                };
            }
            case EntryKind.Script:
            {
                var name = e.Name ?? L.T("Script");
                return new WheelItem(e.Label ?? name.ToUpperInvariant(), name, e.Icon ?? "terminal", e)
                {
                    Tint = tint,
                    Note = e.Shell == ScriptShell.Cmd ? "Command Prompt" : "PowerShell",
                    Unavailable = string.IsNullOrWhiteSpace(e.Script) ? "The script is empty" : null,
                    Blocked = true,
                };
            }
            case EntryKind.Folder:
            {
                var name = e.Name ?? FolderName(e.Path);
                return new WheelItem(e.Label ?? name.ToUpperInvariant(), name, e.Icon ?? "folderOpen", e)
                {
                    Tint = tint,
                    Note = "Open",
                    Unavailable = e.Path is { } path && Directory.Exists(Environment.ExpandEnvironmentVariables(path)) ? null : "The folder isn't there any more",
                    Blocked = true,
                };
            }
            case EntryKind.Link:
            {
                var name = e.Name ?? e.Path ?? "";
                return new WheelItem(e.Label ?? name.ToUpperInvariant(), name, e.Icon ?? "globe", e) { Tint = tint, Note = "Open" };
            }
            case EntryKind.Clips:
            {
                var name = e.Name ?? "Recent Clips";
                var children = ClipPetals(e);
                return new WheelItem(e.Label ?? "CLIPS", name, e.Icon ?? "clipboardList", e)
                {
                    Tint = tint,
                    Children = children,
                    Unavailable = children.Count == 0 ? "Nothing copied yet" : null,
                    Blocked = true,
                };
            }
            case EntryKind.RunningApps:
            {
                var name = e.Name ?? "Open Windows";
                var windows = RunningApps.List();
                var children = windows.Select(w => new WheelItem(Short(w.Program), w.Title, null, w) { Picture = w.ExePath is null ? null : IconOf(w.ExePath) }).ToList();
                return new WheelItem(e.Label ?? "WINDOWS", name, e.Icon ?? "windows", e)
                {
                    Tint = tint,
                    Children = children,
                    Unavailable = children.Count == 0 ? "No other windows are open" : null,
                    Blocked = true,
                };
            }
            default:
            {
                var name = e.Name ?? "More";
                var children = (e.Children ?? []).Select(Item).ToList();
                return new WheelItem(e.Label ?? name.ToUpperInvariant(), name, e.Icon ?? "layers", e) { Tint = tint, Children = children };
            }
        }
    }

    /// <summary>The clips copied last (pinned first), each a petal that pastes it.</summary>
    public static List<WheelItem> ClipPetals(WheelEntry e) =>
        Clips.ClipboardHistory.All.Take(10).Select(clip =>
        {
            var preview = clip.Preview;
            var label = clip.Kind switch
            {
                Clips.ClipKind.Picture => L.T("PICTURE"),
                Clips.ClipKind.Files => clip.Files!.Count == 1 ? Short(Path.GetFileNameWithoutExtension(clip.Files[0])) : L.P(clip.Files.Count, "{0} FILE", "{0} FILES"),
                _ => preview.Length > 14 ? preview[..13].TrimEnd() + "…" : preview,
            };
            return new WheelItem(label, preview, clip.Kind switch { Clips.ClipKind.Picture => "image", Clips.ClipKind.Files => "folder", _ => clip.Pinned ? "pin" : "fileText" }, clip)
            {
                Picture = clip.Kind == Clips.ClipKind.Picture ? clip.Picture() : null,
                Note = clip.Pinned ? "Pinned · paste it" : "Paste it",
                Tint = Colour(e.Tint),
            };
        }).ToList();

    /// <summary>"#RRGGBB" as a colour, or null.</summary>
    public static Color? Colour(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        try
        {
            return (Color)ColorConverter.ConvertFromString(text);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static string Short(string program) =>
        program.Length == 0 ? "?" : program.Length > 12 ? program[..12].ToUpperInvariant() : program.ToUpperInvariant();

    public static string AppName(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return L.T("App");
        var name = Path.GetFileNameWithoutExtension(path.TrimEnd('\\'));
        return name.Length > 0 ? name : path;
    }

    private static string FolderName(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return L.T("Folder");
        var expanded = Environment.ExpandEnvironmentVariables(path).TrimEnd('\\');
        var name = Path.GetFileName(expanded);
        return name.Length > 0 ? name : expanded;
    }

    /// <summary>A program's, shortcut's or Start item's own icon, kept once read.</summary>
    public static ImageSource? IconOf(string path) => Icons.GetOrAdd(path, p => Shell.ShellIcons.Thumbnail(Environment.ExpandEnvironmentVariables(p), 48));
}

/// <summary>Runs a petal chosen on a wheel without files.</summary>
public static class EntryRunner
{
    public static async Task RunAsync(WheelItem item, ScreenPoint at, IntPtr target, IWheelHost host)
    {
        try
        {
            switch (item.Payload)
            {
                case RunningWindow window:
                    if (!await RunningApps.SwitchToAsync(window.Handle))
                        host.Card(at, L.T("That window has closed"), window.Title, false);
                    return;
                case Clips.ClipItem clip:
                    if (!await Clips.ClipboardHistory.PasteAsync(clip, target))
                        host.Card(at, L.T("On the clipboard"), L.T("Paste it with Ctrl+V."), true);
                    return;
                case WheelEntry entry:
                    await RunAsync(entry, item, at, target, host);
                    return;
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, $"Running {item.Name}");
            host.Card(at, L.T(item.Name), L.T("Something went wrong. The activity log has the details."), false);
        }
    }

    private static async Task RunAsync(WheelEntry e, WheelItem item, ScreenPoint at, IntPtr target, IWheelHost host)
    {
        switch (e.Kind)
        {
            case EntryKind.Action:
                if (WheelActions.Get(e.Action) is { } action)
                    await action.Run(new WheelActionContext(at, target, host));
                return;
            case EntryKind.App:
                await OpenAppAsync(e);
                return;
            case EntryKind.Keystroke:
                if (KeySender.Parse(e.Keys) is { } chords && !await KeySender.SendAsync(target, chords))
                    host.Card(at, L.T("The keys weren't pressed"), L.T("The app that was in front isn't there any more."), false);
                return;
            case EntryKind.Script:
                await ScriptRunner.RunAsync(e, target, host, at);
                return;
            case EntryKind.Folder when e.Path is { } folder:
                SystemToggles.Open(Environment.ExpandEnvironmentVariables(folder));
                return;
            case EntryKind.Link when e.Path is { } link:
                SystemToggles.Open(link);
                return;
        }
    }

    /// <summary>Brings the program to the front when it is already open, else starts it.</summary>
    private static async Task OpenAppAsync(WheelEntry e)
    {
        if (e.Path is not { Length: > 0 } path)
            return;
        var exe = Path.GetExtension(path).Equals(".lnk", StringComparison.OrdinalIgnoreCase) ? ShortcutTarget(path) : path;
        if (exe is not null && string.IsNullOrEmpty(e.Arguments) && exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            var open = RunningApps.List().FirstOrDefault(w => string.Equals(w.ExePath, exe, StringComparison.OrdinalIgnoreCase));
            if (open is not null && await RunningApps.SwitchToAsync(open.Handle))
                return;
        }
        var start = new ProcessStartInfo(Environment.ExpandEnvironmentVariables(path)) { UseShellExecute = true };
        if (!string.IsNullOrWhiteSpace(e.Arguments))
            start.Arguments = e.Arguments;
        Process.Start(start);
    }

    /// <summary>Where a .lnk shortcut points, or null.</summary>
    private static string? ShortcutTarget(string link)
    {
        try
        {
            var type = Type.GetTypeFromProgID("WScript.Shell");
            if (type is null) return null;
            var shell = Activator.CreateInstance(type)!;
            var shortcut = type.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, shell, [link])!;
            return shortcut.GetType().InvokeMember("TargetPath", System.Reflection.BindingFlags.GetProperty, null, shortcut, null) as string;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or System.Reflection.TargetInvocationException)
        {
            return null;
        }
    }
}

/// <summary>
/// Runs a script petal with PowerShell or the Command Prompt, hidden. What it is given to work on is in the
/// PETAL_INPUT variable (and the file named by PETAL_INPUT_FILE), or the selected files in PETAL_FILES, one per
/// line. A script that came from somewhere else (an imported settings file) is shown before its first run.
/// </summary>
public static class ScriptRunner
{
    public static string Fingerprint(WheelEntry e) =>
        "script:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{e.Shell}\n{e.Input}\n{e.Script}")));

    /// <summary>Marks a script as the user's own, written in Petal: it runs without asking.</summary>
    public static void Approve(WheelEntry e) => SettingsStore.Current.ApprovedPetals.Add(Fingerprint(e));

    public static async Task RunAsync(WheelEntry e, IntPtr target, IWheelHost host, ScreenPoint at)
    {
        if (string.IsNullOrWhiteSpace(e.Script))
            return;
        if (!SettingsStore.Current.ApprovedPetals.Contains(Fingerprint(e)))
        {
            var allowed = new TaskCompletionSource<bool>();
            ConfirmScriptWindow.Ask(e, at, allowed);
            if (!await allowed.Task)
                return;
        }
        var (text, files) = await InputAsync(e.Input, target);
        if (e.Input == ScriptInput.SelectedText && string.IsNullOrEmpty(text))
        {
            host.Card(at, L.T("No text is selected"), L.T("Select some text in the app first."), false);
            return;
        }
        if (e.Input == ScriptInput.SelectedFiles && files.Count == 0)
        {
            host.Card(at, L.T("No files are selected"), L.T("Select files in File Explorer first."), false);
            return;
        }
        var name = e.Name ?? L.T("Script");
        var card = Hud.HudCard.ShowAt(at, L.F("Running {0}", name), "");
        card.SetProgress(null);
        var (code, output, error) = await Task.Run(() => Run(e, text, files));
        Stats.Statistics.RecordPetal("entry:" + e.Id, 0, 0, 0);
        if (code == 0)
        {
            var first = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "";
            card.Succeed(L.F("{0}: done", name), e.ShowOutput ? first : "", e.ShowOutput && output.Trim().Length > 0 ? () => System.Windows.Clipboard.SetText(output.Trim()) : null);
        }
        else
        {
            var message = (error.Trim().Length > 0 ? error : output).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault()
                ?? L.F("It stopped with code {0}.", code);
            card.Fail(L.F("{0} failed", name), message);
        }
    }

    /// <summary>The text or files the script is given.</summary>
    private static async Task<(string Text, IReadOnlyList<string> Files)> InputAsync(ScriptInput input, IntPtr target)
    {
        switch (input)
        {
            case ScriptInput.Clipboard:
                try
                {
                    return (System.Windows.Clipboard.ContainsText() ? System.Windows.Clipboard.GetText() : "", []);
                }
                catch (System.Runtime.InteropServices.ExternalException)
                {
                    return ("", []);
                }
            case ScriptInput.SelectedText:
                // Give the app a moment to have the keyboard back, then ask it what is selected.
                await Task.Delay(120);
                return (await Task.Run(SelectedText), []);
            case ScriptInput.SelectedFiles:
                return ("", await Shell.ExplorerSelection.SelectionInAsync(target));
            default:
                return ("", []);
        }
    }

    /// <summary>The text selected in the focused control of the app in front, through UI Automation.</summary>
    internal static string SelectedText()
    {
        try
        {
            var focused = System.Windows.Automation.AutomationElement.FocusedElement;
            if (focused?.TryGetCurrentPattern(System.Windows.Automation.TextPattern.Pattern, out var pattern) == true
                && pattern is System.Windows.Automation.TextPattern text)
                return string.Concat(text.GetSelection().Select(r => r.GetText(-1)));
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Windows.Automation.ElementNotAvailableException or System.Runtime.InteropServices.COMException)
        {
            Log.Error(ex, "Reading the selected text");
        }
        return "";
    }

    /// <summary>Runs it and waits: the exit code and what it printed.</summary>
    internal static (int Code, string Output, string Error) Run(WheelEntry e, string text, IReadOnlyList<string> files)
    {
        var temp = Path.Combine(Path.GetTempPath(), "petal-script-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(temp);
        try
        {
            var inputFile = Path.Combine(temp, "input.txt");
            File.WriteAllText(inputFile, text, new UTF8Encoding(false));
            ProcessStartInfo start;
            if (e.Shell == ScriptShell.Cmd)
            {
                var script = Path.Combine(temp, "script.cmd");
                File.WriteAllText(script, "@chcp 65001 >nul\r\n" + e.Script!.Replace("\r\n", "\n").Replace("\n", "\r\n"), new UTF8Encoding(false));
                start = new ProcessStartInfo(Environment.ExpandEnvironmentVariables(@"%SystemRoot%\System32\cmd.exe")) { ArgumentList = { "/d", "/c", script } };
            }
            else
            {
                var body = "[Console]::OutputEncoding = [System.Text.Encoding]::UTF8\n" + e.Script;
                start = new ProcessStartInfo(Environment.ExpandEnvironmentVariables(@"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe"))
                {
                    ArgumentList = { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(body)) },
                };
            }
            start.UseShellExecute = false;
            start.CreateNoWindow = true;
            start.RedirectStandardOutput = true;
            start.RedirectStandardError = true;
            start.StandardOutputEncoding = Encoding.UTF8;
            start.StandardErrorEncoding = Encoding.UTF8;
            start.WorkingDirectory = files.Count > 0 && Path.GetDirectoryName(files[0]) is { } folder ? folder : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            // The environment holds up to 32K characters; longer text is only in the file.
            start.Environment["PETAL_INPUT"] = text.Length > 16000 ? text[..16000] : text;
            start.Environment["PETAL_INPUT_FILE"] = inputFile;
            start.Environment["PETAL_FILES"] = string.Join("\n", files);
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(TimeSpan.FromMinutes(10)))
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                return (-1, output.Result, L.T("It ran for ten minutes and was stopped."));
            }
            return (process.ExitCode, output.Result, error.Result);
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
