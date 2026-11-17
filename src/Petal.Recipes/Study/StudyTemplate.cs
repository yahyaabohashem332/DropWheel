using System.Text.Json;
using System.Text.Json.Serialization;

namespace Petal.Recipes.Study;

/// <summary>
/// A request kept to use again: its name, its words, and the settings it goes with (null where it leaves them as they
/// are). Shown beside the suggestions; sent to others as a <c>.petalstudy</c> file.
/// </summary>
public sealed record StudyTemplate(string Name, string Request, StudyActivities? Activities = null, StudyAmount? Amount = null);

/// <summary>Templates in a file to send to colleagues, and read back with every field checked.</summary>
public static class StudyTemplates
{
    /// <summary>The file's extension.</summary>
    public const string Extension = ".petalstudy";

    /// <summary>The most templates kept, and so the most a file may bring.</summary>
    public const int Most = 20;

    /// <summary>The longest name a template may have.</summary>
    public const int LongestName = 40;

    /// <summary>What the file says it is, so another JSON file isn't taken for one.</summary>
    private const string Kind = "dropwheel-study-templates";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private sealed record FileForm(string Kind, int Version, IReadOnlyList<StudyTemplate> Templates);

    /// <summary>The templates as the file's text.</summary>
    public static string Write(IEnumerable<StudyTemplate> templates) =>
        JsonSerializer.Serialize(new FileForm(Kind, 1, templates.Select(Clean).Where(t => t is not null).Take(Most).ToList()!), Json);

    /// <summary>
    /// The templates a file holds, each checked: a name and a request (cut to their longest), settings that exist. Null
    /// when the text isn't a templates file.
    /// </summary>
    public static IReadOnlyList<StudyTemplate>? Read(string text)
    {
        FileForm? form;
        try
        {
            form = JsonSerializer.Deserialize<FileForm>(text, Json);
        }
        catch (JsonException)
        {
            return null;
        }
        if (form is null || form.Kind != Kind || form.Templates is null)
            return null;
        return form.Templates.Select(Clean).Where(t => t is not null).Take(Most).ToList()!;
    }

    /// <summary>
    /// Templates brought in beside those kept: one with the same request as one kept is left out, one with a name already
    /// used is numbered, and no more than <see cref="Most"/> are kept in all. Also says how many came in.
    /// </summary>
    public static (List<StudyTemplate> All, int Added) Merge(IReadOnlyList<StudyTemplate> kept, IReadOnlyList<StudyTemplate> brought)
    {
        var all = kept.ToList();
        int added = 0;
        foreach (var t in brought)
        {
            if (all.Count >= Most)
                break;
            if (all.Any(k => Same(k.Request, t.Request)))
                continue;
            var name = t.Name;
            for (int n = 2; all.Any(k => string.Equals(k.Name, name, StringComparison.CurrentCultureIgnoreCase)); n++)
                name = Cut(t.Name, LongestName - 4) + " (" + n + ")";
            all.Add(t with { Name = name });
            added++;
        }
        return (all, added);
    }

    /// <summary>A name to start from: the request's first words.</summary>
    public static string NameFor(string request)
    {
        var flat = string.Join(' ', request.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (flat.Length <= LongestName)
            return flat;
        var cut = flat[..LongestName];
        int space = cut.LastIndexOf(' ');
        return (space > LongestName / 2 ? cut[..space] : cut).TrimEnd(',', '.', ':', '،', ';') + "…";
    }

    /// <summary>Two requests that say the same, whatever their spaces.</summary>
    public static bool Same(string a, string b) =>
        string.Join(' ', a.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)) == string.Join(' ', b.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static StudyTemplate? Clean(StudyTemplate? t)
    {
        if (t is null || string.IsNullOrWhiteSpace(t.Request))
            return null;
        var request = Cut(t.Request.Trim(), StudyRequest.Longest);
        var name = string.IsNullOrWhiteSpace(t.Name) ? NameFor(request) : Cut(string.Join(' ', t.Name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)), LongestName);
        return new StudyTemplate(name, request,
            t.Activities is { } a && Enum.IsDefined(a) ? a : null,
            t.Amount is { } m && Enum.IsDefined(m) ? m : null);
    }

    private static string Cut(string text, int most) => text.Length <= most ? text : text[..most];
}
