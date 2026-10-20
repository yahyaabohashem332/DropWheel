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

namespace Petal.App;

/// <summary>Wires the wheel to conversions, tools and file actions.</summary>
public sealed class PetalController : IDisposable
{
    private readonly WheelWindow _wheel;
    private readonly GestureController _gestures;
    private readonly JobRunner _jobs;
    private readonly ToolLauncher _tools;
    private readonly FileActionRunner _actions;

    public PetalController(ConverterRegistry converters, Dispatcher ui)
    {
        Sound = new FeedbackSound { Enabled = SettingsStore.Current.Sounds };
        _wheel = new WheelWindow(Sound)
        {
            ContentProvider = BuildContent,
            HubTextProvider = Sizes.Describe,
            KeyLabel = KeyLabel,
        };
        _wheel.ItemDropped += OnItemDropped;
        _wheel.ShowHints = SettingsStore.Current.ShowKeyHints;
        SettingsStore.Saved += s => _wheel.ShowHints = s.ShowKeyHints;
        _jobs = new JobRunner(converters);
        _tools = new ToolLauncher(_jobs);
        _actions = new FileActionRunner();
        _gestures = new GestureController(_wheel, ui);
        SettingsStore.Saved += s => Sound.Enabled = s.Sounds;
    }

    public FeedbackSound Sound { get; }

    public JobRunner Jobs => _jobs;

    internal WheelWindow CreateWheelForSnapshots() => _wheel;

    public void Start()
    {
        _wheel.Prepare();
        _gestures.Start();
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
                    return new WheelContent([], $"Folders can't be converted. Tap {actionsKey} for file actions.");
                var targets = _jobs.Converters.AvailableTargets(formats);
                return new WheelContent(targets.Select(WheelItem.From).ToList(),
                    files.Count > 1
                        ? $"These files have no format in common. Tap {actionsKey} for file actions."
                        : $"No conversions for this format. Tap {actionsKey} for file actions.");
            }

            default:
            {
                var formats = FormatsOf(files);
                var tools = formats is null ? [] : ToolRegistry.ToolsFor(formats).Where(_tools.IsAvailable).ToList();
                return new WheelContent(tools.Select(WheelItem.From).ToList(),
                    $"No tools for {(files.Count > 1 ? "this selection" : "this format")}. Tap {convert} to convert or {actionsKey} for file actions.");
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
        Sound.Dispose();
        _wheel.Close();
    }
}
