using System.IO;
using System.Windows.Threading;
using Petal.App.Actions;
using Petal.App.Input;
using Petal.App.Jobs;
using Petal.App.Onboarding;
using Petal.App.Overlay;
using Petal.App.Settings;
using Petal.App.Stats;
using Petal.App.Tools;
using Petal.App.Util;
using Petal.App.Wheels;
using Petal.Core.Actions;
using Petal.Core.Formats;
using Petal.Core.Jobs;
using Petal.Core.Tools;
using Petal.App.Localization;

namespace Petal.App;

/// <summary>Wires the wheel to conversions, tools, file actions and the wheels without files.</summary>
public sealed class PetalController : IDisposable, IWheelHost
{
    private readonly WheelWindow _wheel;
    private readonly GestureController _gestures;
    private readonly JobRunner _jobs;
    private readonly ToolLauncher _tools;
    private readonly FileActionRunner _actions;
    private readonly FolderWatcher _folders;
    private readonly PetalPreviews _previews;
    private string _automations = "";
    private (IReadOnlyList<string> Files, FolderContents Contents, DateTime At)? _look;

    public PetalController(ConverterRegistry converters, Dispatcher ui)
    {
        Sound = new FeedbackSound { Enabled = SettingsStore.Current.Sounds, Style = SettingsStore.Current.SoundStyle };
        _wheel = new WheelWindow(Sound)
        {
            ContentProvider = BuildContent,
            HubTextProvider = files => Look(files) is { } look ? FolderDrop.Hub(look).Title : Sizes.Describe(files),
            HubSubtitleProvider = files => Look(files) is { } look ? FolderDrop.Hub(look).Subtitle : null,
            KeyLabel = KeyLabel,
            ModeForKey = ModeForKey,
            OptionsProvider = () => OptionsFrom(SettingsStore.Current),
            Remembered = (page, content) => content.Items.ToList().FindIndex(i => i.Suggested) is >= 0 and var usual ? usual
                : SettingsStore.Current.RememberLastPetal ? WheelMemory.IndexIn(page, content) : null,
        };
        _wheel.ItemDropped += OnItemDropped;
        _wheel.ChainDropped += OnChainDropped;
        _wheel.ChainView = Chains.View;
        _wheel.EntryChosen += OnEntryChosen;
        _wheel.UnavailableChosen += (item, at) => Card(at, L.T(item.Name), L.T(item.Unavailable ?? ""), false);
        _wheel.Chose += (page, item) =>
        {
            if (SettingsStore.Current.RememberLastPetal)
                WheelMemory.Remember(page, item);
            if (page.StartsWith("files:", StringComparison.Ordinal) && WheelMemory.KindOf(_wheel.CurrentFiles) is { } kind && !Practice.IsOn)
                WheelMemory.CountChoice(kind, page, item.Key);
            if (!Practice.IsOn)
                Statistics.RecordPetal(item.Key, 0, 0, 0, item.Name);
        };
        _wheel.ShowHints = SettingsStore.Current.ShowKeyHints;
        SettingsStore.Saved += s => _wheel.ShowHints = s.ShowKeyHints;
        _jobs = new JobRunner(converters);
        _previews = new PetalPreviews(_jobs);
        _wheel.PreviewProvider = (item, files, ct) => _previews.MakeAsync(item, files, ct);
        _wheel.ChainCheck = (chain, next, files) => Chains.Check(chain, next, files, _jobs.Converters);
        _tools = new ToolLauncher(_jobs);
        _actions = new FileActionRunner(_jobs);
        _gestures = new GestureController(_wheel, ui)
        {
            MouseTriggerPressed = OnMouseTrigger,
        };
        _folders = new FolderWatcher(_jobs, ui);
        SettingsStore.Saved += s =>
        {
            Sound.Enabled = s.Sounds;
            Sound.Style = s.SoundStyle;
        };
        SettingsStore.Saved += s => WatchFolders(s);
    }

    /// <summary>The wheel's looks and behaviour from the settings.</summary>
    public static WheelOptions OptionsFrom(AppSettings s) =>
        new(s.CancelZone, s.GrowPetal, s.WheelMotion, s.ShowPetalNames, s.ShowNumberKeys, s.ScrollSwitchesWheels, s.WheelStyle);

    /// <summary>(Re)starts the watched folders when their rules changed.</summary>
    private void WatchFolders(AppSettings settings)
    {
        var now = System.Text.Json.JsonSerializer.Serialize(settings.Automations);
        if (now == _automations)
            return;
        _automations = now;
        _folders.Apply(settings.Automations);
    }

    public FeedbackSound Sound { get; }

    public JobRunner Jobs => _jobs;

    public ToolLauncher Tools => _tools;

    internal WheelWindow CreateWheelForSnapshots() => _wheel;

    public bool IsWheelOpen => _wheel.IsOpen;

    public bool IsDragging => _gestures.IsDragging;

    public void CloseWheel() => _wheel.Cancel();

    /// <summary>Opens the keyboard-driven wheel at once, for files chosen with the shortcut.</summary>
    public void OpenWheelNow(IReadOnlyList<string> files, ScreenPoint at)
    {
        var mode = files.Any(Directory.Exists) ? WheelMode.Actions : WheelMode.Conversions;
        // Back on the wheel used last for files, when it has something for these.
        if (SettingsStore.Current.RememberLastPetal && WheelMemory.LastFileMode is { } last && !files.Any(Directory.Exists) && !BuildContent(last, files).IsEmpty)
            mode = last;
        _wheel.OpenInteractive(files, at, mode);
    }

    /// <summary>
    /// The wheels without files over the window in front: its app's wheel first when there is one, then
    /// quick actions, media and the user's own. With <paramref name="onlyWheel"/>, opens onto that one.
    /// </summary>
    public async Task OpenRingsAsync(IntPtr front, ScreenPoint at, string? onlyWheel = null)
    {
        await WheelActions.RefreshAsync();
        var (pages, start) = Rings.For(SettingsStore.Current, front, onlyWheel);
        if (pages.Count == 0)
        {
            Card(at, L.T("No wheels are on"), L.T("Turn one on in Settings › Wheels."), false);
            return;
        }
        _wheel.OpenRings(pages, start, at);
    }

    private readonly List<string> _pendingFiles = [];
    private DispatcherTimer? _pendingTimer;
    private ScreenPoint _pendingAt;

    /// <summary>
    /// Opens the wheel at a point for files chosen from Explorer's context menu. Explorer starts one
    /// process per selected file, so paths that arrive within a moment are gathered into one wheel.
    /// </summary>
    public void ShowWheelFor(IReadOnlyList<string> files, ScreenPoint at)
    {
        foreach (var f in files)
            if (!_pendingFiles.Contains(f, StringComparer.OrdinalIgnoreCase))
                _pendingFiles.Add(f);
        _pendingAt = at;
        _pendingTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(260), DispatcherPriority.Normal, (_, _) =>
        {
            _pendingTimer!.Stop();
            var batch = _pendingFiles.Where(p => File.Exists(p) || Directory.Exists(p)).ToList();
            _pendingFiles.Clear();
            if (batch.Count == 0) return;
            OpenWheelNow(batch, _pendingAt);
        }, Dispatcher.CurrentDispatcher);
        _pendingTimer.Stop();
        _pendingTimer.Start();
    }

    /// <summary>
    /// A side button of the mouse: the wheel for what File Explorer has selected under the pointer, else
    /// the wheels without files.
    /// </summary>
    private async void OnMouseTrigger(ScreenPoint at)
    {
        try
        {
            if (_wheel.IsOpen)
            {
                _wheel.Cancel();
                return;
            }
            var front = Interop.NativeMethods.GetForegroundWindow();
            var files = await Shell.ExplorerSelection.SelectionInAsync(front);
            if (files.Count > 0)
                OpenWheelNow(files, at);
            else
                await OpenRingsAsync(front, at);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Opening the wheel from the mouse");
        }
    }

    private static WheelMode? ModeForKey(System.Windows.Input.Key key)
    {
        var modifier = key switch
        {
            System.Windows.Input.Key.LeftShift or System.Windows.Input.Key.RightShift => ModifierKey.Shift,
            System.Windows.Input.Key.LeftCtrl or System.Windows.Input.Key.RightCtrl => ModifierKey.Control,
            System.Windows.Input.Key.LeftAlt or System.Windows.Input.Key.RightAlt => ModifierKey.Alt,
            _ => ModifierKey.None,
        };
        var s = SettingsStore.Current;
        if (modifier == s.ConvertKey) return WheelMode.Conversions;
        if (modifier == s.ToolsKey) return WheelMode.Tools;
        if (modifier == s.ActionsKey) return WheelMode.Actions;
        return null;
    }

    public void Start()
    {
        _wheel.Prepare();
        _gestures.Start();
        WatchFolders(SettingsStore.Current);
    }

    private static string KeyLabel(WheelMode mode)
    {
        var s = SettingsStore.Current;
        var key = mode switch
        {
            WheelMode.Conversions => s.ConvertKey,
            WheelMode.Tools => s.ToolsKey,
            _ => s.ActionsKey,
        };
        return key switch
        {
            ModifierKey.Shift => "Shift",
            ModifierKey.Control => "Ctrl",
            ModifierKey.Alt => "Alt",
            ModifierKey.Windows => "Win",
            _ => "—",
        };
    }

    /// <summary>
    /// A quick look inside the folders of a selection, kept for the moments the same selection is on the
    /// wheel (switching wheels asks again); null when it has no folders.
    /// </summary>
    private FolderContents? Look(IReadOnlyList<string> files)
    {
        if (_look is { } look && DateTime.UtcNow - look.At < TimeSpan.FromSeconds(15) && look.Files.SequenceEqual(files, StringComparer.OrdinalIgnoreCase))
            return look.Contents;
        if (!FolderContents.HasFolders(files))
            return null;
        var contents = FolderContents.Read(files, FolderDrop.QuickLook);
        _look = (files.ToList(), contents, DateTime.UtcNow);
        return contents;
    }

    /// <summary>What a file wheel would show for these files, for the command palette.</summary>
    internal WheelContent ContentFor(WheelMode mode, IReadOnlyList<string> files) => BuildContent(mode, files);

    /// <summary>Runs a file wheel's petal on files as a drop on it would, for the command palette.</summary>
    internal void RunOnFiles(WheelItem item, IReadOnlyList<string> files, ScreenPoint at) => OnItemDropped(item, files, at);

    /// <summary>
    /// A tool or file action started from the Tools Guide on files chosen there: run as a drop on its petal when the
    /// tools wheel would offer it for them; otherwise the reason it can't take them.
    /// </summary>
    internal string? TryOnFiles(WheelItem item, IReadOnlyList<string> files, ScreenPoint at)
    {
        if (item.Payload is ToolInfo tool)
        {
            static IEnumerable<WheelItem> All(IEnumerable<WheelItem> items) => items.SelectMany(i => i.Children is { } c ? All(c) : [i]);
            bool fits = Look(files) is { } folder
                ? All(FolderDrop.Tools(folder, _ => true, "", "").Items).Any(i => i.Payload is ToolInfo t && t.Id == tool.Id)
                : FormatsOf(files) is { Count: > 0 } formats && ToolRegistry.ToolsFor(formats).Any(t => t.Id == tool.Id);
            if (!fits)
                return L.F("{0} doesn't work on these files.", L.T(tool.Name));
        }
        OnItemDropped(Availability.Of(item), files, at);
        return null;
    }

    private WheelContent BuildContent(WheelMode mode, IReadOnlyList<string> files) => Usual(Coloured(Content(mode, files)), mode, files);

    /// <summary>The petal usually chosen for this kind of file gets its star and says so.</summary>
    private static WheelContent Usual(WheelContent content, WheelMode mode, IReadOnlyList<string> files)
    {
        if (WheelMemory.KindOf(files) is not { } kind || WheelMemory.UsualFor(kind, "files:" + mode) is not { } key)
            return content;
        int at = content.Items.ToList().FindIndex(i => i.Key == key && i.Unavailable is null);
        if (at < 0)
            return content;
        var items = content.Items.ToList();
        items[at] = items[at] with { Suggested = true, Note = L.F("Your usual for {0}", kind.ToUpperInvariant().Replace(",", ", ")) };
        return content with { Items = items };
    }

    private WheelContent Content(WheelMode mode, IReadOnlyList<string> files)
    {
        var convert = KeyLabel(WheelMode.Conversions);
        var actionsKey = KeyLabel(WheelMode.Actions);
        if (mode != WheelMode.Actions && Look(files) is { } folder)
        {
            return mode == WheelMode.Conversions
                ? FolderDrop.Conversions(folder, _jobs.Converters, SettingsStore.Current.Favourites, actionsKey)
                : Overflow(FolderDrop.Tools(folder, _tools.IsAvailable, convert, actionsKey));
        }
        switch (mode)
        {
            case WheelMode.Actions:
                return ActionsContent();

            case WheelMode.Conversions:
            {
                var formats = FormatsOf(files);
                if (formats is null)
                    return new WheelContent([], L.F("Folders can't be converted. Tap {0} for file actions.", actionsKey));
                var targets = _jobs.Converters.AvailableTargets(formats);
                var favourites = formats.Count == 0 ? [] : SettingsStore.Current.Favourites.Where(f => f.AppliesTo(formats, _jobs.Converters));
                return new WheelContent(favourites.Select(WheelItem.From).Concat(targets.Select(WheelItem.From)).Concat(Availability.MissingConversions(formats, _jobs.Converters)).ToList(),
                    files.Count > 1
                        ? L.F("These files have no format in common. Tap {0} for file actions.", actionsKey)
                        : L.F("No conversions for this format. Tap {0} for file actions.", actionsKey));
            }

            default:
            {
                var formats = FormatsOf(files);
                var tools = formats is null ? [] : ToolRegistry.ToolsFor(formats).Where(_tools.IsAvailable).ToList();
                // Recipes that fit the selection come first, like favourites on the conversion wheel.
                var recipes = formats is null or { Count: 0 } ? [] : SettingsStore.Current.Recipes.Where(r => r.OnWheel && r.AppliesTo(formats, _jobs.Converters));
                // The user's own petals, by extension, so they also work for files Petal doesn't know.
                var petals = Petal.Recipes.CustomPetal.LoadAll().Where(p => p.Takes(files));
                return Overflow(new WheelContent(recipes.Select(WheelItem.From).Concat(petals.Select(WheelItem.From)).Concat(tools.Select(t => Availability.Of(WheelItem.From(t)))).ToList(),
                    files.Count > 1 ? L.F("No tools for this selection. Tap {0} to convert or {1} for file actions.", convert, actionsKey) : L.F("No tools for this format. Tap {0} to convert or {1} for file actions.", convert, actionsKey)));
            }
        }
    }

    /// <summary>More petals than this and their labels grow too small to read.</summary>
    public const int MostToolPetals = 12;

    /// <summary>A tools wheel with too many petals keeps the first ones and gathers the rest under More, which opens around itself.</summary>
    internal static WheelContent Overflow(WheelContent content)
    {
        if (content.Items.Count <= MostToolPetals)
            return content;
        var kept = content.Items.Take(MostToolPetals - 1).ToList();
        kept.Add(WheelItem.Group("more-tools", "MORE", "More", "layers", content.Items.Skip(MostToolPetals - 1).ToList()));
        return content with { Items = kept };
    }

    /// <summary>
    /// The actions wheel: the actions chosen for it, the user's folders, and More with the rest.
    /// </summary>
    private static WheelContent ActionsContent()
    {
        var s = SettingsStore.Current;
        var on = ActionWheel.Parse(s.ActionsOnWheel);
        var items = on.Select(id => FileActionRegistry.All.First(a => a.Id == id)).Select(a => Availability.Of(WheelItem.From(a))).ToList();
        foreach (var destination in s.Destinations.Take(MaxDestinations))
            items.Add(Destination(destination));
        var rest = FileActionRegistry.All.Where(a => !on.Contains(a.Id)).Select(a => Availability.Of(WheelItem.From(a))).ToList();
        if (rest.Count > 0)
            items.Add(WheelItem.Group("more-actions", "MORE", "More", "layers", rest));
        return new WheelContent(items, "");
    }

    /// <summary>More folders than this crowd the actions wheel.</summary>
    public const int MaxDestinations = 4;

    private static WheelItem Destination(FolderDestination d)
    {
        var name = d.Name ?? Path.GetFileName(d.Folder.TrimEnd('\\'));
        if (name.Length == 0) name = d.Folder;
        return new WheelItem(name.ToUpperInvariant(), L.F(d.Move ? "Move to {0}" : "Copy to {0}", name), d.Move ? "folderInput" : "folders", d)
        {
            Note = d.Move ? "Moves the files here" : "Copies the files here",
            Tint = Rings.Colour(d.Tint),
            Unavailable = Directory.Exists(d.Folder) ? null : "The folder isn't there any more",
            Blocked = true,
        };
    }

    /// <summary>The colours the user gave petals, on every petal and inside groups.</summary>
    private static WheelContent Coloured(WheelContent content)
    {
        var colours = SettingsStore.Current.PetalColours;
        if (colours.Count == 0)
            return content;
        return content with { Items = content.Items.Select(Colour).ToList() };

        WheelItem Colour(WheelItem item)
        {
            var tinted = colours.TryGetValue(item.Key, out var hex) && Rings.Colour(hex) is { } c ? item with { Tint = c } : item;
            return tinted.Children is { } children ? tinted with { Children = children.Select(Colour).ToList() } : tinted;
        }
    }

    private static List<FileFormat>? FormatsOf(IReadOnlyList<string> files)
    {
        var formats = new List<FileFormat>(files.Count);
        foreach (var f in files)
        {
            if (Directory.Exists(f))
                return null;
            var format = Formats.FromPath(f);
            if (format is null)
                return [];
            formats.Add(format);
        }
        return formats;
    }

    private async void OnItemDropped(WheelItem item, IReadOnlyList<string> files, ScreenPoint at)
    {
        try
        {
            if (_wheel.PageKey.StartsWith("files:", StringComparison.Ordinal) && Enum.TryParse<WheelMode>(_wheel.PageKey["files:".Length..], out var mode))
                WheelMemory.LastFileMode = mode;
            if (Practice.IsOn)
            {
                Practice.Explain(item, files, at, _wheel.PageKey);
                return;
            }
            // A folder stands for the files inside it: read them all now, away from the interface.
            if (item.Payload is not FileActionInfo and not FolderDestination && FolderContents.HasFolders(files))
            {
                var contents = await Task.Run(() => FolderContents.Read(files));
                switch (item.Payload)
                {
                    case FileFormat folderTarget:
                        await FolderDrop.ConvertAsync(_jobs, contents, folderTarget, at);
                        break;
                    case Favourite folderFavourite:
                        await FolderDrop.ConvertAsync(_jobs, contents, folderFavourite, at);
                        break;
                    case ToolInfo folderTool:
                        await FolderDrop.RunToolAsync(_tools, folderTool, contents, at);
                        break;
                }
                return;
            }
            switch (item.Payload)
            {
                case FileFormat target:
                    await _jobs.ConvertAsync(files, target, at);
                    break;
                case Favourite favourite:
                    await _jobs.ConvertAsync(files, favourite, at);
                    break;
                case Petal.Recipes.Recipe recipe:
                    await _jobs.RunRecipeAsync(files, recipe, at);
                    break;
                case Petal.Recipes.CustomPetal petal:
                    Actions.CustomPetalRunner.Run(petal, files, _jobs, at);
                    break;
                case ToolInfo tool:
                    await _tools.LaunchAsync(tool, files, at);
                    break;
                case FileActionInfo action:
                    await _actions.RunAsync(action, files, at);
                    break;
                case FolderDestination destination:
                    await _actions.ToFolderAsync(files, destination, at);
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, $"Running {item.Name}");
        }
    }

    /// <summary>
    /// Steps chosen on the wheel and the petal dropped on: one recipe when that petal is a step too,
    /// otherwise the steps first and the petal on what they made.
    /// </summary>
    private async void OnChainDropped(IReadOnlyList<WheelItem> chain, WheelItem last, IReadOnlyList<string> files, ScreenPoint at)
    {
        try
        {
            if (Practice.IsOn)
            {
                Practice.Explain(last, files, at, _wheel.PageKey);
                return;
            }
            var name = string.Join(" → ", chain.Append(last).Select(Chains.Name));
            bool ends = Chains.StepsFor(last) is not null && Chains.Check(chain, last, files, _jobs.Converters) is null;
            var steps = Chains.Steps(ends ? chain.Append(last) : chain);
            var made = await _jobs.RunRecipeAsync(files, new Petal.Recipes.Recipe { Name = name, Steps = steps, OnWheel = false }, at);
            if (!ends && made.Count > 0)
                OnItemDropped(last, made, at);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Running steps");
        }
    }

    private async void OnEntryChosen(WheelItem item, ScreenPoint at, IntPtr target)
    {
        if (Practice.IsOn)
        {
            Practice.Explain(item, [], at, "ring");
            return;
        }
        await EntryRunner.RunAsync(item, at, target, this);
    }

    // ---------- what petals of the wheels without files ask of Petal ----------

    void IWheelHost.OpenFiles(IReadOnlyList<string> files, ScreenPoint at) => _wheel.OpenInteractive(files, at, WheelMode.Conversions);

    void IWheelHost.Command(string id, ScreenPoint at) => Command(id, at);

    /// <summary>Petal's own windows and switches, for the quick wheel and the tray.</summary>
    public void Command(string id, ScreenPoint at)
    {
        switch (id)
        {
            case "settings":
                App.Current.ShowSettings();
                break;
            case "history":
                Petal.App.Tools.Windows.HistoryWindow.ShowSingle();
                break;
            case "search":
                Search.SearchWindow.ShowSingle();
                break;
            case "scan":
                Actions.ScanWindow.ShowSingle();
                break;
            case "qr":
                Petal.App.Tools.Windows.QrWindow.ShowSingle();
                break;
            case "stats":
                StatsWindow.ShowSingle();
                break;
            case "pause":
                SettingsStore.Current.Paused = !SettingsStore.Current.Paused;
                SettingsStore.Save();
                Card(at, L.T(SettingsStore.Current.Paused ? "Petal is paused" : "Petal is on again"), "", true);
                break;
            case "clipboard":
                OpenClipboard(at);
                break;
        }
    }

    /// <summary>The wheel for what is on the clipboard: its files, its picture or its text; a link becomes a QR code.</summary>
    public void OpenClipboard(ScreenPoint at)
    {
        switch (Shell.ClipboardSource.Take())
        {
            case Shell.ClipboardContent.Files clip:
                _wheel.OpenInteractive(clip.Paths, at, clip.Paths.Any(Directory.Exists) ? WheelMode.Actions : WheelMode.Conversions);
                break;
            case Shell.ClipboardContent.Link link:
                Petal.App.Tools.Windows.QrWindow.ShowSingle(link.Url);
                break;
            default:
                Card(at, L.T("The clipboard is empty"), L.T("Copy files, a picture or text first."), false);
                break;
        }
    }

    public void Card(ScreenPoint at, string title, string subtitle, bool ok)
    {
        var card = Hud.HudCard.ShowAt(at, title, subtitle);
        if (ok)
            card.Succeed(title, subtitle, null);
        else
            card.Fail(title, subtitle);
    }

    public void Dispose()
    {
        _gestures.Dispose();
        _folders.Dispose();
        Sound.Dispose();
        _wheel.Close();
        SystemToggles.SetKeepAwake(false);
        Statistics.Save();
    }
}
