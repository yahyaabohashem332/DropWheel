using System.Windows;
using Petal.App.Input;

namespace Petal.App.Wheels;

/// <summary>What a petal on a wheel without files can ask of Petal itself.</summary>
public interface IWheelHost
{
    /// <summary>Opens the wheel for files (a screenshot, the clipboard's contents) at a point.</summary>
    void OpenFiles(IReadOnlyList<string> files, ScreenPoint at);

    /// <summary>One of Petal's own windows or switches: "settings", "history", "search", "scan", "qr", "stats", "pause", "clipboard".</summary>
    void Command(string id, ScreenPoint at);

    /// <summary>A short card at a point: done, or what went wrong.</summary>
    void Card(ScreenPoint at, string title, string subtitle, bool ok);
}

/// <summary>Where a petal was chosen and what was in front then.</summary>
public sealed record WheelActionContext(ScreenPoint At, IntPtr Target, IWheelHost Host);

/// <summary>A built-in action for the wheels without files, with its state when it is a switch.</summary>
/// <param name="Section">Where the picker lists it: "Windows", "Screen", "Media" or "Petal".</param>
public sealed record WheelAction(string Id, string Label, string Name, string Icon, string Description, string Section)
{
    /// <summary>On or off for a switch, read now; null for a plain action or when it can't be told.</summary>
    public Func<bool?>? State { get; init; }

    /// <summary>Why it can't run on this PC now ("No microphone found"), or null.</summary>
    public Func<string?>? Unavailable { get; init; }

    /// <summary>The centre's second line instead of on or off ("Playing").</summary>
    public Func<string?>? Note { get; init; }

    /// <summary>The label for now, when it changes (PLAY or PAUSE).</summary>
    public Func<string>? LiveLabel { get; init; }

    public Func<WheelActionContext, Task> Run { get; init; } = _ => Task.CompletedTask;
}

/// <summary>Every built-in action, by id.</summary>
public static class WheelActions
{
    // Bits Windows only tells asynchronously, refreshed in the background before the wheel opens.
    private static bool? _bluetooth;
    private static bool _bluetoothChecked;
    private static NowPlaying? _playing;

    /// <summary>Reads what changes behind Petal's back (Bluetooth, the song playing) before the wheels open, briefly.</summary>
    public static async Task RefreshAsync()
    {
        var bluetooth = Task.Run(async () =>
        {
            var radio = await SystemToggles.BluetoothAsync().ConfigureAwait(false);
            _bluetooth = radio is null ? null : radio.State == Windows.Devices.Radios.RadioState.On;
            _bluetoothChecked = true;
        });
        var media = Task.Run(async () => _playing = await MediaControl.CurrentAsync().ConfigureAwait(false));
        await Task.WhenAny(Task.WhenAll(bluetooth, media), Task.Delay(350));
    }

    /// <summary>The song or video playing when the wheels last opened.</summary>
    public static NowPlaying? Playing => _playing;

    private static Func<WheelActionContext, Task> Do(Action action) => _ => { action(); return Task.CompletedTask; };

    public static readonly IReadOnlyList<WheelAction> All =
    [
        // ---------- Windows ----------
        new("system.darkmode", "DARK MODE", "Dark Mode", "sunMoon", "Switch Windows and apps between light and dark.", "Windows")
        {
            State = () => SystemToggles.IsDarkMode,
            Run = Do(() => SystemToggles.SetDarkMode(!SystemToggles.IsDarkMode)),
        },
        new("system.mute", "MUTE", "Mute Sound", "volumeOff", "Mute or unmute the speakers.", "Windows")
        {
            State = () => SystemToggles.IsMuted,
            Unavailable = () => SystemToggles.HasSpeakers ? null : "No speakers found",
            Run = Do(() => SystemToggles.SetMuted(!(SystemToggles.IsMuted ?? false))),
        },
        new("system.mic", "MIC", "Mute Microphone", "micOff", "Mute or unmute the microphone, in every app.", "Windows")
        {
            State = () => SystemToggles.IsMicMuted,
            Unavailable = () => SystemToggles.HasMicrophone ? null : "No microphone found",
            Run = Do(() => SystemToggles.SetMicMuted(!(SystemToggles.IsMicMuted ?? false))),
        },
        new("system.awake", "AWAKE", "Keep Awake", "coffee", "Keep the PC and the screen from sleeping until you turn it off.", "Windows")
        {
            State = () => SystemToggles.IsKeepingAwake,
            Run = Do(() => SystemToggles.SetKeepAwake(!SystemToggles.IsKeepingAwake)),
        },
        new("system.lock", "LOCK", "Lock", "lock", "Lock the PC.", "Windows") { Run = Do(SystemToggles.Lock) },
        new("system.sleep", "SLEEP", "Sleep", "bed", "Put the PC to sleep.", "Windows") { Run = Do(SystemToggles.Sleep) },
        new("system.hidden", "HIDDEN", "Hidden Files", "eye", "Show or hide hidden files in File Explorer.", "Windows")
        {
            State = () => SystemToggles.ShowsHiddenFiles,
            Run = Do(() => SystemToggles.SetShowHiddenFiles(!SystemToggles.ShowsHiddenFiles)),
        },
        new("system.extensions", "EXTENSIONS", "File Extensions", "fileType", "Show or hide file name extensions such as .jpg in File Explorer.", "Windows")
        {
            State = () => SystemToggles.ShowsExtensions,
            Run = Do(() => SystemToggles.SetShowExtensions(!SystemToggles.ShowsExtensions)),
        },
        new("system.desktopicons", "DESKTOP ICONS", "Desktop Icons", "monitor", "Show or hide the icons on the desktop.", "Windows")
        {
            State = () => SystemToggles.ShowsDesktopIcons,
            Run = Do(() => SystemToggles.SetShowDesktopIcons(!SystemToggles.ShowsDesktopIcons)),
        },
        new("system.recyclebin", "EMPTY BIN", "Empty Recycle Bin", "trash", "Empty the Recycle Bin on every drive. Windows asks first.", "Windows")
        {
            Run = c => { SystemToggles.EmptyRecycleBin(c.Target); return Task.CompletedTask; },
        },
        new("system.eject", "EJECT", "Eject Drives", "usb", "Safely remove every USB drive, memory card and disc.", "Windows")
        {
            Unavailable = () => SystemToggles.RemovableDrives().Any() ? null : "No USB drives or discs",
            Run = async c =>
            {
                var (ejected, busy) = await Task.Run(SystemToggles.EjectRemovableDrives);
                if (busy.Count > 0)
                    c.Host.Card(c.At, Localization.L.T("Some drives are in use"), string.Join(" ", busy), false);
                else
                    c.Host.Card(c.At, Localization.L.T("Safe to remove"), string.Join(" ", ejected), true);
            },
        },
        new("system.clearclipboard", "CLEAR", "Empty Clipboard", "clipboardX", "Forget what was copied.", "Windows")
        {
            Run = c =>
            {
                try { Clipboard.Clear(); } catch (System.Runtime.InteropServices.ExternalException) { }
                c.Host.Card(c.At, Localization.L.T("Clipboard emptied"), "", true);
                return Task.CompletedTask;
            },
        },
        new("system.showdesktop", "SHOW DESKTOP", "Show Desktop", "monitorOff", "Hide every window to see the desktop, or bring them back.", "Windows") { Run = Do(SystemToggles.ToggleDesktop) },
        new("system.taskview", "TASK VIEW", "Task View", "windows", "Every open window, and your desktops.", "Windows") { Run = Do(SystemToggles.TaskView) },
        new("system.notifications", "NOTIFY", "Notifications", "bell", "Open the notification centre.", "Windows") { Run = Do(() => SystemToggles.Open("ms-actioncenter:")) },
        new("system.bluetooth", "BLUETOOTH", "Bluetooth", "bluetooth", "Turn Bluetooth on or off.", "Windows")
        {
            State = () => _bluetooth,
            Unavailable = () => _bluetoothChecked && _bluetooth is null ? "No Bluetooth on this PC" : null,
            Run = async c =>
            {
                bool on = !(_bluetooth ?? false);
                if (await SystemToggles.SetBluetoothAsync(on))
                    _bluetooth = on;
                else
                    c.Host.Card(c.At, Localization.L.T("Bluetooth"), Localization.L.T("Windows didn't allow the change."), false);
            },
        },
        new("system.settings", "SETTINGS", "Windows Settings", "settings", "Open Windows Settings.", "Windows") { Run = Do(() => SystemToggles.Open("ms-settings:")) },

        // ---------- the screen ----------
        new("screen.area", "SNIP", "Screenshot an Area", "screenshot", "Drag over part of the screen; the conversion wheel opens on the picture.", "Screen")
        {
            Run = async c =>
            {
                // Let the wheel leave the screen first.
                await Task.Delay(180);
                Screenshots.ChooseArea((picture, middle) => c.Host.OpenFiles([Screenshots.Save(picture)], middle));
            },
        },
        new("screen.full", "SCREEN", "Screenshot the Screen", "monitor", "The whole screen under the pointer, straight onto the conversion wheel.", "Screen")
        {
            Run = async c =>
            {
                await Task.Delay(180);
                if (Screenshots.CaptureScreenUnderPointer() is { } shot)
                    c.Host.OpenFiles([Screenshots.Save(shot.Picture)], c.At);
            },
        },

        // ---------- media ----------
        new("media.playpause", "PLAY", "Play or Pause", "play", "Play or pause what is playing in any app.", "Media")
        {
            LiveLabel = () => _playing?.Playing == true ? "PAUSE" : "PLAY",
            Note = () => _playing is null ? "Nothing is playing" : _playing.Playing ? "Playing" : "Paused",
            Unavailable = () => _playing is null ? "Nothing is playing" : null,
            Run = async _ => await MediaControl.PlayPauseAsync(),
        },
        new("media.next", "NEXT", "Next", "skipForward", "The next track.", "Media")
        {
            Unavailable = () => _playing is null ? "Nothing is playing" : null,
            Run = async _ => await MediaControl.NextAsync(),
        },
        new("media.previous", "PREVIOUS", "Previous", "skipBack", "The previous track.", "Media")
        {
            Unavailable = () => _playing is null ? "Nothing is playing" : null,
            Run = async _ => await MediaControl.PreviousAsync(),
        },
        new("media.shuffle", "SHUFFLE", "Shuffle", "shuffle", "Shuffle on or off, in players that have it.", "Media")
        {
            State = () => _playing?.Shuffle,
            Unavailable = () => _playing is null ? "Nothing is playing" : _playing.Shuffle is null ? "This player can't shuffle" : null,
            Run = async _ => await MediaControl.ShuffleAsync(),
        },
        new("media.repeat", "REPEAT", "Repeat", "repeat", "Repeat all, one, or off, in players that have it.", "Media")
        {
            State = () => _playing?.Repeat,
            Unavailable = () => _playing is null ? "Nothing is playing" : _playing.Repeat is null ? "This player can't repeat" : null,
            Run = async _ => await MediaControl.RepeatAsync(),
        },
        new("media.volumeup", "LOUDER", "Volume Up", "volume", "Turn the volume up a step.", "Media")
        {
            Unavailable = () => SystemToggles.HasSpeakers ? null : "No speakers found",
            Run = c => { if (SystemToggles.StepVolume(1) is { } v) c.Host.Card(c.At, Localization.L.F("Volume {0}%", v), "", true); return Task.CompletedTask; },
        },
        new("media.volumedown", "QUIETER", "Volume Down", "volumeLow", "Turn the volume down a step.", "Media")
        {
            Unavailable = () => SystemToggles.HasSpeakers ? null : "No speakers found",
            Run = c => { if (SystemToggles.StepVolume(-1) is { } v) c.Host.Card(c.At, Localization.L.F("Volume {0}%", v), "", true); return Task.CompletedTask; },
        },

        // ---------- Petal ----------
        new("petal.clipboard", "CLIPBOARD", "Clipboard's Wheel", "clipboardPaste", "The wheel for what you copied: files, a screenshot or text. A copied link becomes a QR code.", "Petal")
        {
            Unavailable = () => Shell.ClipboardSource.HasSomething() ? null : "The clipboard is empty",
            Run = c => { c.Host.Command("clipboard", c.At); return Task.CompletedTask; },
        },
        new("petal.history", "HISTORY", "Petal History", "history", "What Petal made lately, to find or undo.", "Petal") { Run = c => { c.Host.Command("history", c.At); return Task.CompletedTask; } },
        new("petal.search", "SEARCH", "Search by Meaning", "search", "Find files by what they are about.", "Petal") { Run = c => { c.Host.Command("search", c.At); return Task.CompletedTask; } },
        new("petal.scan", "SCAN", "Scan with Phone", "scanLine", "Scan pages with a phone into a searchable PDF.", "Petal") { Run = c => { c.Host.Command("scan", c.At); return Task.CompletedTask; } },
        new("petal.qr", "QR", "Create QR Code", "qr", "A QR code for text, a link or Wi-Fi.", "Petal") { Run = c => { c.Host.Command("qr", c.At); return Task.CompletedTask; } },
        new("petal.stats", "STATS", "Your Statistics", "chart", "What Petal has done for you.", "Petal") { Run = c => { c.Host.Command("stats", c.At); return Task.CompletedTask; } },
        new("petal.settings", "PETAL", "Petal Settings", "sparkles", "Open Petal's settings.", "Petal") { Run = c => { c.Host.Command("settings", c.At); return Task.CompletedTask; } },
        new("petal.pause", "PAUSE", "Pause Petal", "pause", "Ignore key taps while dragging until you turn it back on.", "Petal")
        {
            State = () => Settings.SettingsStore.Current.Paused,
            Run = c => { c.Host.Command("pause", c.At); return Task.CompletedTask; },
        },
    ];

    private static readonly Dictionary<string, WheelAction> ById = All.ToDictionary(a => a.Id);

    public static WheelAction? Get(string? id) => id is null ? null : ById.GetValueOrDefault(id);
}
