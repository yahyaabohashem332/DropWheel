using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Petal.App.Settings;

namespace Petal.App.Wheels;

/// <summary>
/// Wheels passed between people: saved as a small .petalwheel file, or copied as a line of text to paste
/// in a chat. What comes in gets new ids, is checked for petals Petal doesn't know, and its scripts still
/// ask before their first run.
/// </summary>
public static class WheelSharing
{
    public const string Extension = ".petalwheel";
    private const string CodePrefix = "PETAL-WHEEL:";

    private sealed record Envelope(int PetalWheel, UserWheel Wheel);

    /// <summary>The wheel as the file's text.</summary>
    public static string ToJson(UserWheel wheel) => JsonSerializer.Serialize(new Envelope(1, Plain(wheel)), SettingsStore.Options);

    /// <summary>The wheel as one line of text: "PETAL-WHEEL:" and the compressed file.</summary>
    public static string ToCode(UserWheel wheel)
    {
        var json = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new Envelope(1, Plain(wheel)), new JsonSerializerOptions(SettingsStore.Options) { WriteIndented = false }));
        using var packed = new MemoryStream();
        using (var gzip = new GZipStream(packed, CompressionLevel.SmallestSize))
            gzip.Write(json);
        return CodePrefix + Convert.ToBase64String(packed.ToArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>A wheel from a file's text or a code, ready to add; null with a reason when it isn't a wheel.</summary>
    public static (UserWheel? Wheel, string? Problem) Read(string text)
    {
        text = text.Trim();
        try
        {
            if (text.StartsWith(CodePrefix, StringComparison.OrdinalIgnoreCase))
            {
                var b64 = text[CodePrefix.Length..].Trim().Replace('-', '+').Replace('_', '/');
                b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
                using var gzip = new GZipStream(new MemoryStream(Convert.FromBase64String(b64)), CompressionMode.Decompress);
                using var reader = new StreamReader(gzip, Encoding.UTF8);
                var inner = reader.ReadToEnd();
                if (inner.Length > 512 * 1024)
                    return (null, "This wheel is too large.");
                text = inner;
            }
            if (text.Length > 512 * 1024)
                return (null, "This wheel is too large.");
            var envelope = JsonSerializer.Deserialize<Envelope>(text, SettingsStore.Options);
            if (envelope is not { PetalWheel: 1, Wheel: { } wheel } || string.IsNullOrWhiteSpace(wheel.Name) || wheel.Items is null)
                return (null, "This isn't a Petal wheel.");
            return (Fresh(wheel), null);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidDataException or NotSupportedException)
        {
            return (null, "This isn't a Petal wheel.");
        }
    }

    /// <summary>A copy with nothing personal: new ids, no shortcut, no link to a ready-made template.</summary>
    private static UserWheel Plain(UserWheel wheel) => new()
    {
        Kind = wheel.Kind is WheelKind.Quick or WheelKind.Media ? WheelKind.Custom : wheel.Kind,
        Name = wheel.Name,
        Apps = [.. wheel.Apps],
        Items = wheel.Items.Select(e => e.Clone()).ToList(),
    };

    /// <summary>A wheel that came in: new ids, at most twelve petals and eight in a group, nothing Petal can't show.</summary>
    internal static UserWheel Fresh(UserWheel wheel) => new()
    {
        Kind = wheel.Kind is WheelKind.App && wheel.Apps.Count > 0 ? WheelKind.App : WheelKind.Custom,
        Name = wheel.Name.Length > 40 ? wheel.Name[..40] : wheel.Name,
        Apps = wheel.Apps.Where(a => a.Length is > 0 and < 64).Take(12).ToList(),
        Enabled = true,
        Items = wheel.Items.Take(Settings.WheelEditorWindow.MostPetals).Select(Clean).ToList(),
    };

    private static WheelEntry Clean(WheelEntry e)
    {
        var copy = e.Clone();
        if (copy.Icon is { } icon && !Visuals.Icons.Keys.Contains(icon))
            copy.Icon = null;
        copy.Children = copy.Children?.Take(8).Select(Clean).ToList();
        return copy;
    }

    /// <summary>What a wheel holds, for the window that asks before adding it: petals, keys, scripts.</summary>
    public static (int Petals, int Scripts, int Unknown) Summary(UserWheel wheel)
    {
        var all = wheel.Items.SelectMany(e => e.Children is { } c ? c.Prepend(e) : [e]).ToList();
        return (all.Count(e => e.Kind != EntryKind.Group), all.Count(e => e.Kind == EntryKind.Script), all.Count(e => e.Kind == EntryKind.Action && WheelActions.Get(e.Action) is null));
    }
}

/// <summary>Ready-made wheels to add from the gallery: for apps without one, and for kinds of work.</summary>
public static class WheelGallery
{
    public sealed record Entry(string Name, string Description, string Icon, Func<UserWheel> Make);

    private static WheelEntry A(string action) => new() { Kind = EntryKind.Action, Action = action };

    private static WheelEntry K(string label, string name, string icon, string keys) =>
        new() { Kind = EntryKind.Keystroke, Label = label, Name = name, Icon = icon, Keys = keys };

    private static UserWheel App(string name, string[] apps, params WheelEntry[] items) => new() { Kind = WheelKind.App, Name = name, Apps = [.. apps], Items = [.. items] };

    private static UserWheel Own(string name, params WheelEntry[] items) => new() { Kind = WheelKind.Custom, Name = name, Items = [.. items] };

    public static readonly IReadOnlyList<Entry> All =
    [
        new("Student", "Timer, focus, dictation, translation, summaries, text from the screen, a whiteboard and notes.", "book",
            () => Own("Student", A("draw.timer"), A("work.focus"), A("text.dictate"), A("text.translate"), A("text.summarize"), A("screen.text"), A("draw.whiteboard"), A("notes.new"))),
        new("Presenter", "Spotlight, zoom, drawing, clicks on screen, a timer, live captions, recording and the magnifier.", "presentation",
            () => Own("Presenter", A("draw.spotlight"), A("draw.zoom"), A("draw.screen"), A("draw.clicks"), A("draw.timer"), A("work.captions"), A("screen.record"), A("draw.lens"))),
        new("Writer", "Fix the keyboard layout, translate, rewrite, summarise, count, tidy, dictate and remove diacritics.", "pencil",
            () => Own("Writer", A("text.fixlayout"), A("text.translate"), A("text.rewrite"), A("text.summarize"), A("text.count"), A("text.tidy"), A("text.dictate"), A("text.diacritics"))),
        new("Developer", "The palette, screenshots, the colour picker, the ruler, the clipboard history, sums and windows side by side.", "code",
            () => Own("Developer", A("petal.palette"), A("screen.area"), A("screen.colour"), A("screen.ruler"), A("clipboard.history"), A("text.calculate"), A("window.left"), A("window.right"))),
        new("Figma", "Frame, rectangle, text, pen, component, group, auto layout, export and zoom to fit.", "frame",
            () => App("Figma", ["figma"], K("FRAME", "Frame", "frame", "F"), K("RECTANGLE", "Rectangle", "square", "R"), K("TEXT", "Text", "type", "T"), K("PEN", "Pen", "pen", "P"),
                K("COMPONENT", "Create Component", "puzzle", "Ctrl+Alt+K"), K("GROUP", "Group", "layers", "Ctrl+G"), K("AUTO LAYOUT", "Auto Layout", "layoutGrid", "Shift+A"),
                K("EXPORT", "Export", "download", "Ctrl+Shift+E"), K("FIT", "Zoom to Fit", "zoomOut", "Shift+1"), K("DUPLICATE", "Duplicate", "copy", "Ctrl+D"))),
        new("Illustrator", "Selection, direct selection, pen, type, group, outline view, fit the artboard and export for screens.", "pen",
            () => App("Illustrator", ["illustrator"], K("SELECT", "Selection", "pointer", "V"), K("DIRECT", "Direct Selection", "pointer", "A"), K("PEN", "Pen", "pen", "P"), K("TYPE", "Type", "type", "T"),
                K("GROUP", "Group", "layers", "Ctrl+G"), K("UNGROUP", "Ungroup", "layers", "Ctrl+Shift+G"), K("OUTLINE", "Outline View", "eye", "Ctrl+Y"),
                K("FIT", "Fit Artboard", "zoomOut", "Ctrl+0"), K("TO FRONT", "Bring to Front", "chevronUp", "Ctrl+Shift+]"), K("EXPORT", "Export for Screens", "download", "Ctrl+Alt+E"))),
        new("Premiere Pro", "Selection, razor, add edit, ripple delete, marker, play, zoom to the sequence and export.", "film",
            () => App("Premiere Pro", ["adobe premiere pro"], K("SELECT", "Selection", "pointer", "V"), K("RAZOR", "Razor", "scissors", "C"), K("CUT", "Add Edit", "split", "Ctrl+K"),
                K("RIPPLE", "Ripple Delete", "trash", "Shift+Delete"), K("MARKER", "Add Marker", "pin", "M"), K("PLAY", "Play", "play", "Space"),
                K("FIT", "Zoom to Sequence", "zoomOut", "\\"), K("EXPORT", "Export Media", "download", "Ctrl+M"))),
        new("Blender", "Grab, rotate, scale, edit mode, add, loop cut, render and save.", "puzzle",
            () => App("Blender", ["blender"], K("GRAB", "Grab", "pointer", "G"), K("ROTATE", "Rotate", "rotateCw", "R"), K("SCALE", "Scale", "resize", "S"), K("EDIT", "Edit Mode", "pencil", "Tab"),
                K("ADD", "Add", "plus", "Shift+A"), K("LOOP CUT", "Loop Cut", "split", "Ctrl+R"), K("RENDER", "Render", "camera", "F12"), K("SAVE", "Save", "download", "Ctrl+S"))),
        new("Notion", "New page, search, the sidebar, dark mode, back and forward.", "fileText",
            () => App("Notion", ["notion"], K("NEW PAGE", "New Page", "filePlus", "Ctrl+N"), K("SEARCH", "Search", "search", "Ctrl+P"), K("SIDEBAR", "Sidebar", "panelLeft", "Ctrl+\\"),
                K("DARK", "Dark Mode", "moon", "Ctrl+Shift+L"), K("BACK", "Back", "chevronLeft", "Ctrl+["), K("FORWARD", "Forward", "chevronRight", "Ctrl+]"))),
        new("Outlook", "New mail, reply, reply all, forward, mail and calendar, a new appointment, search and send/receive.", "bell",
            () => App("Outlook", ["outlook", "olk"], K("NEW", "New Mail", "plus", "Ctrl+N"), K("REPLY", "Reply", "undo", "Ctrl+R"), K("REPLY ALL", "Reply All", "users", "Ctrl+Shift+R"),
                K("FWD", "Forward the Mail", "arrowRight", "Ctrl+F"), K("MAIL", "Mail", "bell", "Ctrl+1"), K("CALENDAR", "Calendar", "history", "Ctrl+2"),
                K("MEETING", "New Appointment", "plus", "Ctrl+Shift+A"), K("SEARCH", "Search", "search", "Ctrl+E"), K("SYNC", "Send/Receive", "refresh", "F9"))),
    ];
}
