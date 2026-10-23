using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using Petal.App.Localization;
using Petal.App.Overlay;
using Petal.App.Wheels;
using Petal.Core.Actions;
using Petal.Core.Tools;

namespace Petal.App.Guide;

/// <summary>What one tool or action is, how to use it and what it is good for, in the language Petal speaks.</summary>
public sealed record GuideEntry(
    string Key,
    string Category,
    string Summary,
    string Details,
    string How,
    IReadOnlyList<string> Examples,
    string WorksOn,
    IReadOnlyList<string> Keywords);

/// <summary>
/// The guide to every tool, file action and action of the wheels without files: embedded tables
/// (Guide\Entries\&lt;language&gt;.json), English first, each other language translated from it. Keys say what an
/// entry is about: "tool:Compress", "action:Zip" (the file actions), "do:screen.area" (the wheels without
/// files), and "kind:recipe"-style entries for petals that aren't one fixed tool. The tables are what the
/// Tools Guide shows and all the AI model is told, so answers come from them, not from what the model guesses.
/// </summary>
public static class GuideBook
{
    /// <summary>The guide's sections, in the order the Tools Guide lists them.</summary>
    public static readonly IReadOnlyList<string> Categories =
    [
        "pictures", "video", "audio", "pdf", "documents", "files", "folders", "screen", "draw", "text",
        "windows", "clipboard", "notes", "work", "media", "system", "petal", "general",
    ];

    private static readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, GuideEntry>> Tables = new();

    /// <summary>The section's name.</summary>
    public static string CategoryName(string category) => L.T(category switch
    {
        "pictures" => "Pictures",
        "video" => "Videos",
        "audio" => "Audio",
        "pdf" => "PDF",
        "documents" => "Documents",
        "files" => "Files",
        "folders" => "Folders",
        "screen" => "Screen",
        "draw" => "Draw & Present",
        "text" => "Text",
        "windows" => "Arrange Windows",
        "clipboard" => "Clipboard",
        "notes" => "Notes",
        "work" => "Work",
        "media" => "Media",
        "system" => "Windows Switches",
        "petal" => "Petal",
        _ => "Kinds of Petals",
    });

    /// <summary>Every key, in English's order (which every language follows).</summary>
    public static IReadOnlyList<string> Keys => Table("en").Keys.ToList();

    /// <summary>The entry in the language Petal speaks; English where that language lacks it.</summary>
    public static GuideEntry? Get(string key) =>
        Table(L.Code).TryGetValue(key, out var entry) || Table("en").TryGetValue(key, out entry) ? entry : null;

    /// <summary>The English entry: what the search also reads, whatever the language.</summary>
    public static GuideEntry? English(string key) => Table("en").TryGetValue(key, out var entry) ? entry : null;

    /// <summary>One language's table as embedded (empty when Petal has no guide in it).</summary>
    internal static IReadOnlyDictionary<string, GuideEntry> Table(string code) => Tables.GetOrAdd(code, Load);

    private static IReadOnlyDictionary<string, GuideEntry> Load(string code)
    {
        using var stream = typeof(GuideBook).Assembly.GetManifestResourceStream($"Petal.App.Guide.Entries.{code}.json");
        if (stream is null)
            return new Dictionary<string, GuideEntry>();
        try
        {
            using var document = JsonDocument.Parse(stream);
            var table = new Dictionary<string, GuideEntry>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                var e = property.Value;
                string Text(string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
                IReadOnlyList<string> List(string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
                    ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList()
                    : [];
                table[property.Name] = new GuideEntry(property.Name, Text("category"), Text("summary"), Text("details"), Text("how"), List("examples"), Text("worksOn"), List("keywords"));
            }
            return table;
        }
        catch (JsonException ex)
        {
            Log.Error(ex, $"Reading the {code} guide");
            return new Dictionary<string, GuideEntry>();
        }
    }

    /// <summary>The guide's key for a petal on a wheel, or null when the guide has nothing on it.</summary>
    public static string? KeyFor(WheelItem item)
    {
        string? key = item.Payload switch
        {
            ToolInfo t => "tool:" + t.Id,
            FileActionInfo a => "action:" + a.Id,
            WheelEntry { Kind: EntryKind.Action } e => "do:" + e.Action,
            WheelEntry e => e.Kind switch
            {
                EntryKind.App => "kind:app",
                EntryKind.Keystroke => "kind:keys",
                EntryKind.Script => "kind:script",
                EntryKind.Folder => "kind:folder",
                EntryKind.Group => "kind:group",
                EntryKind.RunningApps => "kind:windows",
                EntryKind.Link => "kind:link",
                EntryKind.Clips => "kind:clips",
                EntryKind.Workspace or EntryKind.Workspaces => "kind:workspaces",
                _ => null,
            },
            Petal.Core.Formats.FileFormat => "kind:format",
            Petal.Core.Jobs.Favourite => "kind:favourite",
            Petal.Recipes.Recipe => "kind:recipe",
            Petal.Recipes.CustomPetal => "kind:petal",
            WheelGroup => "kind:group",
            RunningWindow => "kind:windows",
            Clips.ClipItem => "kind:clips",
            Screen.Workspace => "kind:workspaces",
            FolderDestination => "kind:destination",
            _ => null,
        };
        return key is not null && Get(key) is not null ? key : null;
    }

    /// <summary>The name of what an entry is about, in the language Petal speaks.</summary>
    public static string Name(string key)
    {
        var (kind, id) = Split(key);
        switch (kind)
        {
            case "tool" when Enum.TryParse<ToolId>(id, out var tool):
                return L.T(ToolRegistry.Get(tool).Name);
            case "action" when Enum.TryParse<FileActionId>(id, out var action) && FileActionRegistry.All.FirstOrDefault(a => a.Id == action) is { } info:
                return L.T(info.Name);
            case "do" when WheelActions.Get(id) is { } wheelAction:
                return L.T(wheelAction.Name);
            case "kind":
                return L.T(id switch
                {
                    "format" => "Convert to a Format",
                    "favourite" => "Favourites",
                    "recipe" => "Recipes",
                    "petal" => "Your Petals",
                    "app" => "A Program",
                    "keys" => "Keys",
                    "script" => "Script",
                    "folder" => "Folder",
                    "link" => "Link",
                    "group" => "Group",
                    "windows" => "Open Windows",
                    "clips" => "Recent Clips",
                    "workspaces" => "Workspaces",
                    "destination" => "A Folder for Dropped Files",
                    _ => id,
                });
            default:
                return id;
        }
    }

    /// <summary>The name in English (for a tool or action; empty for the kinds of petals), whatever language Petal speaks.</summary>
    public static string EnglishName(string key)
    {
        var (kind, id) = Split(key);
        return kind switch
        {
            "tool" when Enum.TryParse<ToolId>(id, out var tool) => ToolRegistry.Get(tool).Name,
            "action" when Enum.TryParse<FileActionId>(id, out var a) => FileActionRegistry.All.FirstOrDefault(x => x.Id == a)?.Name ?? "",
            "do" => WheelActions.Get(id)?.Name ?? "",
            _ => "",
        };
    }

    /// <summary>The icon of what an entry is about.</summary>
    public static string Icon(string key)
    {
        var (kind, id) = Split(key);
        return kind switch
        {
            "tool" when Enum.TryParse<ToolId>(id, out var tool) => ToolRegistry.Get(tool).Icon,
            "action" when Enum.TryParse<FileActionId>(id, out var action) && FileActionRegistry.All.FirstOrDefault(a => a.Id == action) is { } info => info.Icon,
            "do" when WheelActions.Get(id) is { } wheelAction => wheelAction.Icon,
            "kind" => id switch
            {
                "format" => "refresh",
                "favourite" => "star",
                "recipe" => "workflow",
                "petal" => "puzzle",
                "app" => "appWindow",
                "keys" => "keyboard",
                "script" => "terminal",
                "folder" or "destination" => "folder",
                "link" => "link",
                "group" => "layers",
                "windows" => "grid",
                "clips" => "clipboard",
                "workspaces" => "layoutGrid",
                _ => "info",
            },
            _ => "info",
        };
    }

    /// <summary>Whether Petal can start it from the guide: a tool or file action (on files chosen there) or an action.</summary>
    public static bool CanRun(string key)
    {
        var (kind, id) = Split(key);
        return kind switch
        {
            "tool" => Enum.TryParse<ToolId>(id, out _),
            "action" => Enum.TryParse<FileActionId>(id, out _),
            "do" => WheelActions.Get(id) is not null,
            _ => false,
        };
    }

    /// <summary>Whether it works on files, chosen first when started from the guide.</summary>
    public static bool NeedsFiles(string key) => key.StartsWith("tool:", StringComparison.Ordinal) || key.StartsWith("action:", StringComparison.Ordinal);

    internal static (string Kind, string Id) Split(string key)
    {
        int colon = key.IndexOf(':');
        return colon < 0 ? ("", key) : (key[..colon], key[(colon + 1)..]);
    }
}
