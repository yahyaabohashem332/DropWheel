namespace Petal.App.Wheels;

/// <summary>
/// The ready-made wheels: quick actions, media, and wheels for well-known apps with their own keyboard
/// shortcuts. Each is a starting point the user can change, switch off, or put back as it was.
/// </summary>
public static class WheelTemplates
{
    /// <summary>Raised to add wheels made after the user's settings were: see <see cref="Upgrade"/>.</summary>
    public const int Version = 3;

    private static WheelEntry A(string action) => new() { Kind = EntryKind.Action, Action = action };

    private static WheelEntry K(string label, string name, string icon, string keys) =>
        new() { Kind = EntryKind.Keystroke, Label = label, Name = name, Icon = icon, Keys = keys };

    private static WheelEntry Group(string label, string icon, params WheelEntry[] children) =>
        new() { Kind = EntryKind.Group, Label = label.ToUpperInvariant(), Name = label, Icon = icon, Children = [.. children] };

    public static UserWheel Quick() => new()
    {
        Kind = WheelKind.Quick,
        Name = "Quick",
        Template = "quick",
        Items =
        [
            A("system.darkmode"), A("system.mute"), A("system.mic"), A("system.awake"), A("screen.area"), A("petal.clipboard"),
            A("system.hidden"), A("system.extensions"), A("system.desktopicons"), A("system.lock"), A("system.recyclebin"),
            Group("More", "layers", A("system.sleep"), A("system.eject"), A("system.clearclipboard"), A("system.showdesktop"),
                A("system.taskview"), A("system.notifications"), A("system.bluetooth"), A("screen.full")),
        ],
    };

    public static UserWheel Media() => new()
    {
        Kind = WheelKind.Media,
        Name = "Media",
        Template = "media",
        Items =
        [
            A("media.playpause"), A("media.next"), A("media.shuffle"), A("media.volumeup"),
            A("system.mute"), A("media.volumedown"), A("media.repeat"), A("media.previous"),
        ],
    };

    /// <summary>Screenshots, recording, text from the screen, pins, colours and the ruler.</summary>
    public static UserWheel Screen() => new()
    {
        Kind = WheelKind.Custom,
        Name = "Screen",
        Template = "screen",
        Items =
        [
            A("screen.area"), A("screen.record"), A("screen.text"), A("screen.scroll"),
            A("screen.full"), A("screen.pin"), A("screen.private"), A("screen.colour"), A("screen.ruler"),
        ],
    };

    /// <summary>For teaching, meetings and recordings: drawing, boards, zoom, spotlight, clicks and a timer.</summary>
    public static UserWheel Present() => new()
    {
        Kind = WheelKind.Custom,
        Name = "Draw & Present",
        Template = "present",
        Items =
        [
            A("draw.screen"), A("draw.zoom"), A("draw.spotlight"), A("draw.clicks"),
            A("draw.whiteboard"), A("draw.lens"), A("draw.timer"), A("draw.blackboard"),
        ],
    };

    /// <summary>What to do with the text selected in any app.</summary>
    /// <summary>Arabic script is in use here: the interface, or a keyboard. The petals only it needs wait for that; every petal stays in the list to add.</summary>
    private static bool ArabicInUse()
    {
        if (Localization.L.IsRightToLeft)
            return true;
        try
        {
            foreach (var language in System.Windows.Input.InputLanguageManager.Current.AvailableInputLanguages ?? Array.Empty<object>())
                if (language is System.Globalization.CultureInfo { TwoLetterISOLanguageName: "ar" or "fa" or "ur" })
                    return true;
        }
        catch (InvalidOperationException)
        {
        }
        return false;
    }

    public static UserWheel Text() => new()
    {
        Kind = WheelKind.Custom,
        Name = "Text",
        Template = "text",
        Items =
        [
            .. ArabicInUse() ? [A("text.fixlayout")] : Array.Empty<WheelEntry>(), A("text.translate"), A("text.dictate"),
            Group("Case", "caseText", A("text.upper"), A("text.lower"), A("text.title"), A("text.sentence")),
            A("text.calculate"), A("text.count"),
            ArabicInUse() ? Group("Tidy", "tidyText", A("text.tidy"), A("text.diacritics"), A("text.digits")) : A("text.tidy"),
            A("text.summarize"), A("text.rewrite"),
        ],
    };

    /// <summary>The window in front, arranged: each petal points where the window goes.</summary>
    public static UserWheel Windows() => new()
    {
        Kind = WheelKind.Custom,
        Name = "Arrange",
        Template = "windows",
        Items =
        [
            A("window.maximize"), A("window.topright"), A("window.right"), A("window.bottomright"),
            Group("More", "layers", A("window.centre"), A("window.nextscreen"), A("window.top"), A("window.bottom"),
                A("window.ontop"), A("window.seethrough"), A("window.minimize"), A("window.close")),
            A("window.bottomleft"), A("window.left"), A("window.topleft"),
        ],
    };

    /// <summary>For getting things done: the palette, focus, meetings, captions, dictation and workspaces.</summary>
    public static UserWheel Work() => new()
    {
        Kind = WheelKind.Custom,
        Name = "Work",
        Template = "work",
        Items =
        [
            A("petal.palette"), A("work.focus"), A("work.meeting"), A("work.captions"),
            A("text.dictate"), new WheelEntry { Kind = EntryKind.Workspaces }, A("draw.timer"), A("notes.new"),
        ],
    };

    /// <summary>What was copied lately, to paste again, with the history and the clipboard's wheel.</summary>
    public static UserWheel Clipboard() => new()
    {
        Kind = WheelKind.Custom,
        Name = "Clipboard",
        Template = "clipboard",
        Items =
        [
            new WheelEntry { Kind = EntryKind.Clips },
            A("clipboard.history"), A("petal.clipboard"), A("notes.new"),
        ],
    };

    private static UserWheel App(string template, string name, string[] apps, params WheelEntry[] items) => new()
    {
        Kind = WheelKind.App,
        Name = name,
        Template = template,
        Apps = [.. apps],
        Items = [.. items],
    };

    /// <summary>Wheels for apps, shown first while one of them is in front.</summary>
    public static IReadOnlyList<UserWheel> Apps() =>
    [
        App("browser", "Browser", ["chrome", "msedge", "firefox", "brave", "opera", "vivaldi"],
            K("NEW TAB", "New Tab", "plus", "Ctrl+T"),
            K("CLOSE TAB", "Close Tab", "x", "Ctrl+W"),
            K("REOPEN TAB", "Reopen Closed Tab", "undo", "Ctrl+Shift+T"),
            K("RELOAD", "Reload", "refresh", "F5"),
            K("ADDRESS", "Address Bar", "link", "Ctrl+L"),
            K("FIND", "Find on Page", "search", "Ctrl+F"),
            K("BOOKMARK", "Bookmark", "star", "Ctrl+D"),
            K("HISTORY", "History", "history", "Ctrl+H"),
            K("FILES", "Downloads", "download", "Ctrl+J"),
            K("NEXT TAB", "Next Tab", "chevronRight", "Ctrl+Tab"),
            K("PREV TAB", "Previous Tab", "chevronLeft", "Ctrl+Shift+Tab")),
        App("explorer", "File Explorer", ["explorer"],
            K("NEW FOLDER", "New Folder", "folder", "Ctrl+Shift+N"),
            K("NEW WINDOW", "New Window", "appWindow", "Ctrl+N"),
            K("ADDRESS", "Address Bar", "link", "Alt+D"),
            K("SEARCH", "Search", "search", "Ctrl+E"),
            K("UP", "Up a Folder", "chevronUp", "Alt+Up"),
            K("BACK", "Back", "chevronLeft", "Alt+Left"),
            K("RENAME", "Rename", "rename", "F2"),
            K("PROPERTIES", "Properties", "info", "Alt+Enter"),
            K("PREVIEW", "Preview Pane", "image", "Alt+P"),
            A("system.hidden"), A("system.extensions")),
        App("code", "VS Code", ["code", "cursor", "windsurf"],
            K("PALETTE", "Command Palette", "keyboard", "Ctrl+Shift+P"),
            K("OPEN FILE", "Quick Open", "fileSearch", "Ctrl+P"),
            K("FIND ALL", "Find in Files", "search", "Ctrl+Shift+F"),
            K("TERMINAL", "Terminal", "terminal", "Ctrl+`"),
            K("SIDEBAR", "Sidebar", "panelLeft", "Ctrl+B"),
            K("DEFINITION", "Go to Definition", "arrowRight", "F12"),
            K("RENAME", "Rename Symbol", "rename", "F2"),
            K("FORMAT", "Format Document", "wand", "Shift+Alt+F"),
            K("COMMENT", "Comment Line", "hash", "Ctrl+/"),
            K("SPLIT", "Split Editor", "split", "Ctrl+\\"),
            K("ZEN", "Zen Mode", "square", "Ctrl+K Z")),
        App("word", "Word", ["winword"],
            K("BOLD", "Bold", "type", "Ctrl+B"),
            K("ITALIC", "Italic", "type", "Ctrl+I"),
            K("UNDERLINE", "Underline", "type", "Ctrl+U"),
            K("HEADING 1", "Heading 1", "hash", "Ctrl+Alt+1"),
            K("HEADING 2", "Heading 2", "hash", "Ctrl+Alt+2"),
            K("NORMAL", "Normal Text", "fileText", "Ctrl+Shift+N"),
            K("CENTRE", "Centre", "listPlus", "Ctrl+E"),
            K("FIND", "Find", "search", "Ctrl+F"),
            K("REPLACE", "Replace", "refresh", "Ctrl+H"),
            K("PAGE BREAK", "Page Break", "minus", "Ctrl+Enter"),
            K("SAVE AS", "Save As", "download", "F12")),
        App("excel", "Excel", ["excel"],
            K("SUM", "AutoSum", "plus", "Alt+="),
            K("TODAY", "Today's Date", "history", "Ctrl+;"),
            K("FILTER", "Filter", "sliders", "Ctrl+Shift+L"),
            K("TABLE", "Format as Table", "sheet", "Ctrl+T"),
            K("FORMAT", "Format Cells", "settings", "Ctrl+1"),
            K("FILL DOWN", "Fill Down", "chevronDown", "Ctrl+D"),
            K("FLASH FILL", "Flash Fill", "sparkles", "Ctrl+E"),
            K("NEW SHEET", "New Sheet", "filePlus", "Shift+F11"),
            K("EDIT", "Edit Cell", "pencil", "F2"),
            K("FIND", "Find", "search", "Ctrl+F"),
            K("BOLD", "Bold", "type", "Ctrl+B")),
        App("powerpoint", "PowerPoint", ["powerpnt"],
            K("NEW SLIDE", "New Slide", "plus", "Ctrl+M"),
            K("DUPLICATE", "Duplicate Slide", "copy", "Ctrl+D"),
            K("SHOW", "Start Show", "play", "F5"),
            K("FROM HERE", "Show from This Slide", "presentation", "Shift+F5"),
            K("GROUP", "Group", "layers", "Ctrl+G"),
            K("UNGROUP", "Ungroup", "layers", "Ctrl+Shift+G"),
            K("TO FRONT", "Bring to Front", "chevronUp", "Ctrl+Shift+]"),
            K("TO BACK", "Send to Back", "chevronDown", "Ctrl+Shift+["),
            K("CENTRE", "Centre Text", "listPlus", "Ctrl+E"),
            K("FIND", "Find", "search", "Ctrl+F"),
            K("SAVE AS", "Save As", "download", "F12")),
        App("teams", "Teams", ["ms-teams", "teams"],
            K("MUTE", "Mute", "micOff", "Ctrl+Shift+M"),
            K("CAMERA", "Camera", "camera", "Ctrl+Shift+O"),
            K("HAND", "Raise Hand", "pointer", "Ctrl+Shift+K"),
            K("SHARE", "Share Screen", "monitor", "Ctrl+Shift+E"),
            K("LEAVE", "Leave Call", "power", "Ctrl+Shift+H"),
            K("ANSWER", "Answer with Video", "video", "Ctrl+Shift+A"),
            K("DECLINE", "Decline Call", "x", "Ctrl+Shift+D"),
            K("SEARCH", "Search", "search", "Ctrl+E"),
            K("CHAT", "Chat", "speech", "Ctrl+2"),
            K("CALENDAR", "Calendar", "history", "Ctrl+4")),
        App("zoom", "Zoom", ["zoom"],
            K("MUTE", "Mute", "micOff", "Alt+A"),
            K("VIDEO", "Video", "camera", "Alt+V"),
            K("SHARE", "Share Screen", "monitor", "Alt+S"),
            K("HAND", "Raise Hand", "pointer", "Alt+Y"),
            K("CHAT", "Chat", "speech", "Alt+H"),
            K("PEOPLE", "Participants", "users", "Alt+U"),
            K("RECORD", "Record", "circle", "Alt+R"),
            K("FULL SCREEN", "Full Screen", "frame", "Alt+F"),
            K("GALLERY", "Gallery View", "grid", "Alt+F2"),
            K("LEAVE", "Leave Meeting", "power", "Alt+Q")),
        App("photoshop", "Photoshop", ["photoshop"],
            K("BRUSH", "Brush", "brush", "B"),
            K("MOVE", "Move", "pointer", "V"),
            K("SCALE", "Free Transform", "frame", "Ctrl+T"),
            K("NEW LAYER", "New Layer", "layers", "Ctrl+Shift+N"),
            K("MERGE", "Merge Down", "chevronDown", "Ctrl+E"),
            K("DESELECT", "Deselect", "x", "Ctrl+D"),
            K("INVERT", "Invert", "sunMoon", "Ctrl+I"),
            K("STEP BACK", "Step Backward", "undo", "Ctrl+Alt+Z"),
            K("FIT", "Fit on Screen", "zoomOut", "Ctrl+0"),
            K("EXPORT", "Export As", "download", "Ctrl+Alt+Shift+W"),
            K("WEB", "Save for Web", "image", "Ctrl+Alt+Shift+S")),
        App("terminal", "Terminal", ["windowsterminal"],
            K("NEW TAB", "New Tab", "plus", "Ctrl+Shift+T"),
            K("CLOSE TAB", "Close Tab", "x", "Ctrl+Shift+W"),
            K("SPLIT", "Split Pane", "split", "Alt+Shift+D"),
            K("PALETTE", "Command Palette", "keyboard", "Ctrl+Shift+P"),
            K("FIND", "Find", "search", "Ctrl+Shift+F"),
            K("SETTINGS", "Settings", "settings", "Ctrl+,"),
            K("BIGGER", "Zoom In", "zoomIn", "Ctrl+="),
            K("SMALLER", "Zoom Out", "zoomOut", "Ctrl+-"),
            K("NEXT TAB", "Next Tab", "chevronRight", "Ctrl+Tab")),
        App("slack", "Slack", ["slack"],
            K("JUMP", "Quick Switcher", "search", "Ctrl+K"),
            K("SEARCH", "Search", "fileSearch", "Ctrl+G"),
            K("UNREADS", "Unreads", "bell", "Ctrl+Shift+A"),
            K("THREADS", "Threads", "speech", "Ctrl+Shift+T"),
            K("DMS", "Direct Messages", "users", "Ctrl+Shift+K"),
            K("MENTIONS", "Mentions", "ask", "Ctrl+Shift+M"),
            K("STATUS", "Set Status", "heart", "Ctrl+Shift+Y"),
            K("UPLOAD", "Upload", "share", "Ctrl+U"),
            K("PREVIOUS", "Previous Channel", "chevronUp", "Alt+Up"),
            K("NEXT", "Next Channel", "chevronDown", "Alt+Down")),
        App("discord", "Discord", ["discord"],
            K("MUTE", "Mute", "micOff", "Ctrl+Shift+M"),
            K("DEAFEN", "Deafen", "volumeOff", "Ctrl+Shift+D"),
            K("JUMP", "Quick Switcher", "search", "Ctrl+K"),
            K("SEARCH", "Search", "fileSearch", "Ctrl+F"),
            K("UPLOAD", "Upload", "share", "Ctrl+Shift+U"),
            K("PINS", "Pins", "pin", "Ctrl+P"),
            K("MEMBERS", "Members", "users", "Ctrl+U"),
            K("EMOJI", "Emoji", "heart", "Ctrl+E"),
            K("ANSWER", "Answer Call", "smartphone", "Ctrl+Enter"),
            K("READ", "Mark as Read", "check", "Escape")),
    ];

    /// <summary>Every ready-made wheel, in the order they are first added.</summary>
    public static IReadOnlyList<UserWheel> All() =>
        [Quick(), Media(), Screen(), Present(), Text(), Windows(), Clipboard(), Work(), .. Apps()];

    /// <summary>A new custom wheel to fill in.</summary>
    public static UserWheel Custom(string name) => new()
    {
        Kind = WheelKind.Custom,
        Name = name,
        Items = [A("petal.settings")],
    };

    /// <summary>The ready-made wheel <paramref name="template"/> as new, for "put back as it was".</summary>
    public static UserWheel? Fresh(string? template) => template switch
    {
        "quick" => Quick(),
        "media" => Media(),
        "screen" => Screen(),
        "present" => Present(),
        "text" => Text(),
        "windows" => Windows(),
        "clipboard" => Clipboard(),
        "work" => Work(),
        null => null,
        _ => Apps().FirstOrDefault(a => a.Template == template),
    };

    /// <summary>
    /// Adds the ready-made wheels the user hasn't been given yet: all of them on first run, later ones
    /// as they arrive. Wheels the user removed are not brought back.
    /// </summary>
    public static bool Upgrade(Settings.AppSettings settings)
    {
        if (settings.WheelsVersion >= Version)
            return false;
        if (settings.WheelsVersion < 1)
        {
            settings.Wheels.Add(Quick());
            settings.Wheels.Add(Media());
            settings.Wheels.AddRange(Apps());
        }
        if (settings.WheelsVersion < 2)
        {
            // After the quick and media wheels, before the app wheels and the user's own.
            int at = settings.Wheels.FindLastIndex(w => w.Template is "quick" or "media") + 1;
            settings.Wheels.InsertRange(at, [Screen(), Present(), Text(), Windows(), Clipboard()]);
        }
        if (settings.WheelsVersion < 3)
        {
            // After the wheels of 1.6, or after quick and media.
            int at = settings.Wheels.FindLastIndex(w => w.Template is "quick" or "media" or "screen" or "present" or "text" or "windows" or "clipboard") + 1;
            settings.Wheels.Insert(at, Work());
            // The Screen wheel of 1.6 gets the private screenshot.
            if (settings.Wheels.FirstOrDefault(w => w.Template == "screen") is { } screen && screen.Items.Count < 12
                && !screen.Items.Any(e => e.Action == "screen.private"))
                screen.Items.Insert(Math.Min(6, screen.Items.Count), A("screen.private"));
        }
        settings.WheelsVersion = Version;
        return true;
    }
}
