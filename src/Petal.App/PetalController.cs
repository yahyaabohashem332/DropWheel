using System.IO;
using System.Windows.Threading;
using Petal.App.Actions;
using Petal.App.Input;
using Petal.App.Jobs;
using Petal.App.Overlay;
using Petal.App.Settings;
using Petal.App.Tools;
using Petal.App.Util;
using Petal.Core.Actions;
using Petal.Core.Formats;
using Petal.Core.Jobs;
using Petal.Core.Tools;
using Petal.App.Localization;

namespace Petal.App;

/// <summary>Wires the wheel to conversions, tools and file actions.</summary>
public sealed class PetalController : IDisposable
{
    private readonly WheelWindow _wheel;
    private readonly GestureController _gestures;
    private readonly JobRunner _jobs;
    private readonly ToolLauncher _tools;
    private readonly FileActionRunner _actions;
    private readonly FolderWatcher _folders;
    private string _automations = "";

    public PetalController(ConverterRegistry converters, Dispatcher ui)
    {
        Sound = new FeedbackSound { Enabled = SettingsStore.Current.Sounds };
        _wheel = new WheelWindow(Sound)
        {
            ContentProvider = BuildContent,
            HubTextProvider = Sizes.Describe,
            KeyLabel = KeyLabel,
            ModeForKey = ModeForKey,
        };
        _wheel.ItemDropped += OnItemDropped;
        _wheel.ShowHints = SettingsStore.Current.ShowKeyHints;
        SettingsStore.Saved += s => _wheel.ShowHints = s.ShowKeyHints;
        _jobs = new JobRunner(converters);
        _tools = new ToolLauncher(_jobs);
        _actions = new FileActionRunner(_jobs);
        _gestures = new GestureController(_wheel, ui);
        _folders = new FolderWatcher(_jobs, ui);
        SettingsStore.Saved += s => Sound.Enabled = s.Sounds;
        SettingsStore.Saved += s => WatchFolders(s);
    }

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

    internal WheelWindow CreateWheelForSnapshots() => _wheel;

    public bool IsWheelOpen => _wheel.IsOpen;

    public bool IsDragging => _gestures.IsDragging;

    public void CloseWheel() => _wheel.Cancel();

    /// <summary>Opens the keyboard-driven wheel at once, for files chosen with the shortcut.</summary>
    public void OpenWheelNow(IReadOnlyList<string> files, ScreenPoint at) =>
        _wheel.OpenInteractive(files, at, files.Any(Directory.Exists) ? WheelMode.Actions : WheelMode.Conversions);

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
            bool anyFolder = batch.Any(Directory.Exists);
            _wheel.OpenInteractive(batch, _pendingAt, anyFolder ? WheelMode.Actions : WheelMode.Conversions);
        }, Dispatcher.CurrentDispatcher);
        _pendingTimer.Stop();
        _pendingTimer.Start();
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

    private WheelContent BuildContent(WheelMode mode, IReadOnlyList<string> files)
    {
        var convert = KeyLabel(WheelMode.Conversions);
        var actionsKey = KeyLabel(WheelMode.Actions);
        switch (mode)
        {
            case WheelMode.Actions:
                return new WheelContent(FileActionRegistry.All.Select(WheelItem.From).ToList(), "");

            case WheelMode.Conversions:
            {
                var formats = FormatsOf(files);
                if (formats is null)
                    return new WheelContent([], L.F("Folders can't be converted. Tap {0} for file actions.", actionsKey));
                var targets = _jobs.Converters.AvailableTargets(formats);
                var favourites = formats.Count == 0 ? [] : SettingsStore.Current.Favourites.Where(f => f.AppliesTo(formats, _jobs.Converters));
                return new WheelContent(favourites.Select(WheelItem.From).Concat(targets.Select(WheelItem.From)).ToList(),
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
                return new WheelContent(recipes.Select(WheelItem.From).Concat(petals.Select(WheelItem.From)).Concat(tools.Select(WheelItem.From)).ToList(),
                    files.Count > 1 ? L.F("No tools for this selection. Tap {0} to convert or {1} for file actions.", convert, actionsKey) : L.F("No tools for this format. Tap {0} to convert or {1} for file actions.", convert, actionsKey));
            }
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
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, $"Running {item.Name}");
        }
    }

    public void Dispose()
    {
        _gestures.Dispose();
        _folders.Dispose();
        Sound.Dispose();
        _wheel.Close();
    }
}
