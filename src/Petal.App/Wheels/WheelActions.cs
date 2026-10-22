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
/// <param name="Section">Where the picker lists it: "Windows", "Screen", "Draw", "Text", "Arrange", "Clipboard", "Notes", "Media" or "Petal".</param>
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

    /// <summary>A screen tool's way to open the conversion wheel on what it made.</summary>
    private static Action<string, ScreenPoint> Open(WheelActionContext c) => (path, at) => c.Host.OpenFiles([path], at);

    private static WheelAction Text(string id, string label, string name, string icon, string description) =>
        new(id, label, name, icon, description, "Text") { Run = c => Words.TextActions.RunAsync(id, c) };

    private static WheelAction Arrange(string id, string label, string name, string icon, string description, Func<IntPtr, bool> act) =>
        new(id, label, name, icon, description, "Arrange")
        {
            Unavailable = () => Screen.WindowArranger.CanArrange(Screen.WindowArranger.Front) ? null : "No window to arrange",
            Run = c =>
            {
                if (!act(c.Target))
                    c.Host.Card(c.At, Localization.L.T("No window to arrange"), Localization.L.T("Open the wheel over an app's window."), false);
                return Task.CompletedTask;
            },
        };

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
        new("system.extensions", "FILE TYPES", "File Extensions", "fileType", "Show or hide file name extensions such as .jpg in File Explorer.", "Windows")
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

        new("screen.record", "RECORD", "Record the Screen", "record", "Record an area or the whole screen to MP4, with the PC's sound and the microphone if wanted.", "Screen")
        {
            LiveLabel = () => Screen.ScreenRecorder.IsRecording ? "STOP" : "RECORD",
            Note = () => Screen.ScreenRecorder.IsRecording ? "Recording" : null,
            Run = async c => { await Task.Delay(180); Screen.ScreenRecorder.Toggle(Open(c)); },
        },
        new("screen.scroll", "SCROLL SHOT", "Scrolling Screenshot", "scrollCapture", "A whole long page in one picture: Petal scrolls it and joins what it sees.", "Screen")
        {
            Run = async c => { await Task.Delay(180); Screen.ScrollCapture.Toggle(Open(c)); },
        },
        new("screen.text", "COPY TEXT", "Text from the Screen", "scanText", "Copy the words in any part of the screen, even in pictures and videos. QR codes are read too.", "Screen")
        {
            Run = async c => { await Task.Delay(180); Screen.ScreenTextWindow.Start(c.At); },
        },
        new("screen.pin", "PIN", "Pin a Screenshot", "pinPicture", "Keep a piece of the screen on top of every window, to compare or copy from.", "Screen")
        {
            Run = async c => { await Task.Delay(180); Screen.PinnedShot.Start(Open(c)); },
        },
        new("screen.private", "PRIVATE SHOT", "Private Screenshot", "eyeOff", "A screenshot with e-mail addresses, phone numbers, card numbers and passwords already covered, to share safely.", "Screen")
        {
            Run = async c =>
            {
                await Task.Delay(180);
                Screenshots.ChooseArea((picture, middle) =>
                {
                    var path = Screenshots.Save(picture);
                    var controller = App.Current.Controller;
                    new Tools.Windows.RedactPhotoWindow(Petal.Core.Tools.ToolRegistry.Get(Petal.Core.Tools.ToolId.RedactPhoto), path, controller.Jobs, middle).ShowNear(middle);
                });
            },
        },
        new("screen.colour", "COLOUR", "Pick a Colour", "dropper", "The colour of any pixel on the screen, in HEX, RGB and HSL, with its contrast.", "Screen")
        {
            Run = async _ => { await Task.Delay(180); Screen.ColourPicker.Start(); },
        },
        new("screen.ruler", "RULER", "Screen Ruler", "ruler", "Measure spaces and sizes on the screen, in pixels.", "Screen")
        {
            Run = async _ => { await Task.Delay(180); Screen.ScreenRuler.Start(); },
        },

        // ---------- drawing and showing ----------
        new("draw.screen", "DRAW", "Draw on Screen", "pen", "Pen, highlighter, arrows, boxes and text over anything on the screen. Esc puts it away.", "Draw")
        {
            State = () => Screen.DrawOverlay.IsOpen,
            Run = async c => { await Task.Delay(150); Screen.DrawOverlay.Toggle(Screen.DrawBoard.Live, Open(c)); },
        },
        new("draw.whiteboard", "WHITE BOARD", "Whiteboard", "presentation", "A clean white board over the screen, to draw and explain on.", "Draw")
        {
            Run = async c => { await Task.Delay(150); Screen.DrawOverlay.Toggle(Screen.DrawBoard.White, Open(c)); },
        },
        new("draw.blackboard", "BLACK BOARD", "Blackboard", "presentation", "A dark board over the screen, to draw on in white and colour.", "Draw")
        {
            Run = async c => { await Task.Delay(150); Screen.DrawOverlay.Toggle(Screen.DrawBoard.Black, Open(c)); },
        },
        new("draw.zoom", "ZOOM", "Zoom In", "zoomIn", "Zoom into the screen with the mouse wheel, and draw on what is zoomed.", "Draw")
        {
            Run = async c => { await Task.Delay(180); Screen.ScreenZoom.Toggle(Open(c)); },
        },
        new("draw.lens", "MAGNIFIER", "Magnifier", "scanSearch", "A magnifying glass that follows the pointer. Ctrl and the mouse wheel change how much.", "Draw")
        {
            State = () => Screen.Lens.IsOpen,
            Run = Do(Screen.Lens.Toggle),
        },
        new("draw.spotlight", "SPOTLIGHT", "Spotlight", "spotlight", "Darken the screen except around the pointer, to show where to look.", "Draw")
        {
            State = () => Screen.Spotlight.IsOn,
            Run = Do(Screen.Spotlight.Toggle),
        },
        new("draw.clicks", "SHOW CLICKS", "Show Clicks and Keys", "clickShow", "Rings where you click and the shortcuts you press, for recordings and presentations.", "Draw")
        {
            State = () => Screen.InputVisualizer.IsOn,
            Run = Do(Screen.InputVisualizer.Toggle),
        },
        new("draw.timer", "TIMER", "Timer", "timer", "A countdown or a stopwatch on top of everything, big enough for a room.", "Draw")
        {
            State = () => Screen.TimerWindow.IsOpen,
            Note = () => Screen.TimerWindow.Showing,
            Run = Do(Screen.TimerWindow.Toggle),
        },

        // ---------- text in any app ----------
        Text("text.fixlayout", "FIX LAYOUT", "Fix Keyboard Layout", "keyboard", "Text typed with the wrong keyboard language put right: ؤشف becomes cat, and hgsghl becomes السلام."),
        Text("text.translate", "TRANSLATE", "Translate", "languages", "The selected text in Arabic or English, translated on this PC."),
        Text("text.upper", "UPPER", "UPPERCASE", "caseUpper", "The selected text in capital letters."),
        Text("text.lower", "LOWER", "lowercase", "caseLower", "The selected text in small letters."),
        Text("text.title", "TITLE", "Title Case", "heading", "Every word starting with a capital."),
        Text("text.sentence", "SENTENCE", "Sentence case", "pilcrow", "Only the first word of each sentence starts with a capital."),
        Text("text.diacritics", "TASHKEEL", "Remove Diacritics", "eraser", "Takes the harakat and tatweel off Arabic text."),
        Text("text.digits", "DIGITS", "Swap Digits", "hash", "Arabic digits ١٢٣ into 123, or the other way."),
        Text("text.tidy", "TIDY", "Tidy Spaces", "tidyText", "Double spaces, spaces at line ends and extra empty lines taken out."),
        Text("text.count", "COUNT", "Count Words", "sigma", "Words, characters, lines and sentences in the selection."),
        Text("text.calculate", "CALCULATE", "Calculate", "calculator", "Works out a selected sum or unit conversion (12% of 340, 5 km in mi) and copies the answer."),
        Text("text.summarize", "SUMMARY", "Summarize", "summary", "A short summary of the selected text, made by the AI model on this PC."),
        Text("text.rewrite", "REWRITE", "Rewrite", "wand", "Clearer, shorter, more formal, friendlier, or with mistakes fixed, by the AI model on this PC."),
        new("text.dictate", "DICTATE", "Dictate", "mic", "Speak, and Petal types what you say where the cursor is, in Arabic, English and more.", "Text")
        {
            State = () => Words.Dictation.IsListening,
            Unavailable = () => Words.Listener.HasMicrophone ? null : "No microphone found",
            Run = async c => { await Task.Delay(150); Words.Dictation.Toggle(c.Target); },
        },

        // ---------- arranging the window in front ----------
        Arrange("window.left", "LEFT", "Left Half", "panelLeft", "The window fills the left half of its screen.", w => Screen.WindowArranger.Place(w, Screen.WindowPlace.LeftHalf)),
        Arrange("window.right", "RIGHT", "Right Half", "panelRight", "The window fills the right half of its screen.", w => Screen.WindowArranger.Place(w, Screen.WindowPlace.RightHalf)),
        Arrange("window.top", "TOP", "Top Half", "panelTop", "The window fills the top half of its screen.", w => Screen.WindowArranger.Place(w, Screen.WindowPlace.TopHalf)),
        Arrange("window.bottom", "BOTTOM", "Bottom Half", "panelBottom", "The window fills the bottom half of its screen.", w => Screen.WindowArranger.Place(w, Screen.WindowPlace.BottomHalf)),
        Arrange("window.topleft", "TOP LEFT", "Top-Left Quarter", "layoutGrid", "The window fills the top-left quarter.", w => Screen.WindowArranger.Place(w, Screen.WindowPlace.TopLeft)),
        Arrange("window.topright", "TOP RIGHT", "Top-Right Quarter", "layoutGrid", "The window fills the top-right quarter.", w => Screen.WindowArranger.Place(w, Screen.WindowPlace.TopRight)),
        Arrange("window.bottomleft", "BOTTOM LEFT", "Bottom-Left Quarter", "layoutGrid", "The window fills the bottom-left quarter.", w => Screen.WindowArranger.Place(w, Screen.WindowPlace.BottomLeft)),
        Arrange("window.bottomright", "BOTTOM RIGHT", "Bottom-Right Quarter", "layoutGrid", "The window fills the bottom-right quarter.", w => Screen.WindowArranger.Place(w, Screen.WindowPlace.BottomRight)),
        Arrange("window.leftthird", "LEFT THIRD", "Left Third", "split", "The window fills the left third, for wide screens.", w => Screen.WindowArranger.Place(w, Screen.WindowPlace.LeftThird)),
        Arrange("window.middlethird", "MIDDLE THIRD", "Middle Third", "split", "The window fills the middle third.", w => Screen.WindowArranger.Place(w, Screen.WindowPlace.MiddleThird)),
        Arrange("window.rightthird", "RIGHT THIRD", "Right Third", "split", "The window fills the right third.", w => Screen.WindowArranger.Place(w, Screen.WindowPlace.RightThird)),
        Arrange("window.centre", "CENTRE", "Centre", "target", "The window in the middle of its screen.", w => Screen.WindowArranger.Place(w, Screen.WindowPlace.Centre)),
        Arrange("window.maximize", "MAXIMIZE", "Maximize", "maximize", "The window fills its screen, or goes back to its size.", Screen.WindowArranger.ToggleMaximised) with
        {
            State = () => Screen.WindowArranger.CanArrange(Screen.WindowArranger.Front) ? Screen.ScreenNative.IsZoomed(Screen.WindowArranger.Front) : null,
        },
        Arrange("window.nextscreen", "NEXT SCREEN", "Next Screen", "monitorNext", "The window moves to the next screen, in the same place.", Screen.WindowArranger.NextScreen) with
        {
            Unavailable = () => !Screen.WindowArranger.CanArrange(Screen.WindowArranger.Front) ? "No window to arrange" : Screen.ScreenNative.Monitors().Count < 2 ? "Only one screen" : null,
        },
        Arrange("window.ontop", "ON TOP", "Always on Top", "pin", "Keep the window above all others, or let it go back.", Screen.WindowArranger.ToggleOnTop) with
        {
            State = () => Screen.WindowArranger.IsOnTop(Screen.WindowArranger.Front),
        },
        new("window.seethrough", "SEE THROUGH", "See-Through", "blend", "Make the window see-through, a step at a time, then solid again.", "Arrange")
        {
            Unavailable = () => Screen.WindowArranger.CanArrange(Screen.WindowArranger.Front) ? null : "No window to arrange",
            Note = () => Screen.WindowArranger.Opacity(Screen.WindowArranger.Front) is { } a && a < 250 ? Localization.L.F("{0}% solid", (int)Math.Round(a / 2.55)) : null,
            Run = c =>
            {
                if (Screen.WindowArranger.CycleOpacity(c.Target) is { } percent)
                    c.Host.Card(c.At, Localization.L.F("{0}% solid", percent), "", true);
                return Task.CompletedTask;
            },
        },
        Arrange("window.minimize", "MINIMIZE", "Minimize", "minimize", "The window goes down to the taskbar.", Screen.WindowArranger.Minimise),
        Arrange("window.close", "CLOSE", "Close Window", "x", "Closes the window, as its own X would (it can still ask to save).", Screen.WindowArranger.Close),

        // ---------- the clipboard and notes ----------
        new("clipboard.history", "HISTORY", "Clipboard History", "clipboardList", "Everything you copied lately, to search, pin and paste again.", "Clipboard")
        {
            Run = c => { Clips.ClipboardWindow.ShowSingle(c.Target, c.At); return Task.CompletedTask; },
        },
        new("clipboard.forget", "FORGET", "Clear Clipboard History", "clipboardX", "Forget what was copied (pinned clips stay).", "Clipboard")
        {
            Run = c =>
            {
                Clips.ClipboardHistory.Clear(keepPinned: true);
                c.Host.Card(c.At, Localization.L.T("Clipboard history cleared"), Localization.L.T("Pinned clips stay"), true);
                return Task.CompletedTask;
            },
        },
        new("notes.new", "NOTE", "New Note", "note", "A sticky note on the desktop, in any language and colour.", "Notes") { Run = Do(Notes.StickyNotes.New) },
        new("notes.toggle", "NOTES", "Show or Hide Notes", "notebook", "Bring every sticky note out, or tuck them all away.", "Notes")
        {
            State = () => Notes.StickyNotes.Showing,
            Run = Do(Notes.StickyNotes.ToggleAll),
        },

        // ---------- work ----------
        new("petal.palette", "COMMANDS", "Command Palette", "search", "Type what you want: any action, a petal for the selected files, an app, a file, a sum.", "Work")
        {
            Run = c => Commands.CommandPalette.ToggleAsync(c.Target),
        },
        new("work.focus", "FOCUS", "Focus", "target", "A focus session: distracting apps wait until it ends, and the minutes are counted.", "Work")
        {
            State = () => Screen.FocusMode.IsOn,
            Note = () => Screen.FocusMode.IsOn ? Petal.Core.Text.Durations.Format(Screen.FocusMode.Left) : null,
            Run = Do(Screen.FocusMode.Toggle),
        },
        new("work.meeting", "MEETING", "Meeting Notes", "notebook", "Records the meeting, then writes the transcript, a summary, the decisions and the action items, on this PC.", "Work")
        {
            State = () => Words.MeetingRecorder.IsRecording,
            Run = Do(Words.MeetingRecorder.Toggle),
        },
        new("work.captions", "CC", "Live Captions", "captions", "Captions for any video, call or lesson, translated into Arabic as they come, on this PC.", "Work")
        {
            State = () => Words.LiveCaptions.IsOn,
            Run = Do(Words.LiveCaptions.Toggle),
        },
        new("workspace.save", "SAVE LAYOUT", "Save Workspace", "plus", "Keep the apps open now and where their windows are, to open them all together later.", "Work")
        {
            Run = Do(Screen.SaveWorkspaceWindow.ShowSingle),
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
