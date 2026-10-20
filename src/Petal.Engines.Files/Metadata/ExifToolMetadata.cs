using System.Globalization;
using System.Text;
using System.Text.Json;
using Petal.Core.Util;

namespace Petal.Engines.Files.Metadata;

/// <summary>One metadata field, e.g. group "ExifIFD", name "ISO", value "200".</summary>
public sealed record MetadataField(string Group, string Name, string Value, bool Editable, bool Removable)
{
    /// <summary>ExifTool's "Group:Tag" key used for writing.</summary>
    public string Key => $"{Group}:{Name}";
}

/// <summary>A change to apply when saving: a new value, or null to delete the field.</summary>
public sealed record MetadataEdit(string Group, string Name, string? Value);

/// <summary>
/// Reads, edits and strips metadata in images, video, audio and PDF through ExifTool.
/// Never touches the original: every write produces a new file.
/// </summary>
public static class ExifToolMetadata
{
    // Groups ExifTool can write for common files; everything else is shown read-only.
    private static readonly string[] WritableGroupPrefixes =
    [
        "IFD0", "IFD1", "ExifIFD", "GPS", "InteropIFD", "IPTC", "XMP", "Photoshop",
        "PDF", "QuickTime", "ItemList", "Keys", "UserData", "ID3", "Vorbis", "RIFF", "PNG", "Track", "Microsoft",
    ];

    // Fields that describe the file rather than live inside it.
    private static readonly HashSet<string> SystemGroups = new(StringComparer.OrdinalIgnoreCase)
    {
        "ExifTool", "System", "File", "Composite",
    };

    public static bool IsAvailable => ToolLocator.ExifTool is not null;

    public static async Task<IReadOnlyList<MetadataField>> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        var json = await RunAsync(["-j", "-G1", "-a", "-s", "-n", "-api", "LargeFileSupport=1", path], cancellationToken);
        var fields = new List<MetadataField>();
        using var doc = JsonDocument.Parse(json);
        foreach (var obj in doc.RootElement.EnumerateArray())
        {
            foreach (var prop in obj.EnumerateObject())
            {
                if (prop.Name == "SourceFile")
                    continue;
                var colon = prop.Name.IndexOf(':');
                var group = colon > 0 ? prop.Name[..colon] : "Other";
                var name = colon > 0 ? prop.Name[(colon + 1)..] : prop.Name;
                var value = ToText(prop.Value);
                bool system = SystemGroups.Contains(group);
                bool writable = !system && WritableGroupPrefixes.Any(p => group.StartsWith(p, StringComparison.OrdinalIgnoreCase));
                bool binary = value.StartsWith("(Binary data", StringComparison.Ordinal) || value.StartsWith("base64:", StringComparison.Ordinal);
                fields.Add(new MetadataField(group, Prettify(name), binary ? "(binary data)" : value, writable && !binary, writable));
            }
        }
        return fields;
    }

    /// <summary>Writes the edits into a copy at <paramref name="outputPath"/>.</summary>
    public static async Task SaveEditsAsync(string path, string outputPath, IReadOnlyList<MetadataEdit> edits, CancellationToken cancellationToken = default)
    {
        var args = new List<string> { "-m", "-o", outputPath };
        foreach (var edit in edits)
            args.Add(edit.Value is null ? $"-{edit.Group}:{Compact(edit.Name)}=" : $"-{edit.Group}:{Compact(edit.Name)}={edit.Value}");
        args.Add(path);
        await RunAsync(args, cancellationToken);
    }

    /// <summary>
    /// Removes all metadata, including hidden camera and processing data, into a copy. The colour
    /// profile and orientation are kept so the picture looks the same.
    /// </summary>
    public static async Task RemoveAllAsync(string path, string outputPath, CancellationToken cancellationToken = default)
    {
        await RunAsync(["-m", "-all=", "-tagsfromfile", "@", "-ICC_Profile", "-ColorSpaceTags", "-Orientation", "-o", outputPath, path], cancellationToken);
    }

    /// <summary>A short summary for the window header: camera, date taken, location.</summary>
    public static IReadOnlyList<(string Label, string Value)> Summary(IReadOnlyList<MetadataField> fields)
    {
        string? Find(params string[] names) => fields.FirstOrDefault(f => names.Contains(f.Name.Replace(" ", ""), StringComparer.OrdinalIgnoreCase))?.Value;
        var result = new List<(string, string)>();
        var make = Find("Make");
        var model = Find("Model");
        if (model is not null)
            result.Add(("Camera", make is not null && !model.StartsWith(make, StringComparison.OrdinalIgnoreCase) ? $"{make} {model}" : model));
        if (Find("DateTimeOriginal", "CreateDate", "CreationDate") is { } date)
            result.Add(("Taken", date));
        var lat = Find("GPSLatitude");
        var lon = Find("GPSLongitude");
        if (lat is not null && lon is not null)
            result.Add(("Location", $"{lat}, {lon}"));
        if (Find("Artist", "Author", "Creator") is { } author)
            result.Add(("Author", author));
        return result;
    }

    private static async Task<string> RunAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var exe = ToolLocator.ExifTool ?? throw new InvalidOperationException("ExifTool isn't installed.");
        // Pass arguments through a UTF-8 file so non-English file names survive.
        var argFile = Path.Combine(Path.GetTempPath(), $"petal-exiftool-{Guid.NewGuid():N}.args");
        await File.WriteAllLinesAsync(argFile, args, new UTF8Encoding(false), cancellationToken);
        try
        {
            var result = await ProcessRunner.RunAsync(exe, ["-charset", "filename=utf8", "-charset", "utf8", "-@", argFile], cancellationToken: cancellationToken);
            // Exit code 1 with "files weren't updated" is fine for no-op edits; real errors mention "Error:".
            if (!result.Succeeded && result.StandardError.Contains("Error", StringComparison.OrdinalIgnoreCase))
                throw new ExternalToolException("ExifTool", result);
            return result.StandardOutput;
        }
        finally
        {
            try { File.Delete(argFile); } catch { /* temp file */ }
        }
    }

    private static string ToText(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? "",
        JsonValueKind.Number => value.TryGetInt64(out var l) ? l.ToString(CultureInfo.InvariantCulture) : value.GetDouble().ToString("0.########", CultureInfo.InvariantCulture),
        JsonValueKind.True => "Yes",
        JsonValueKind.False => "No",
        JsonValueKind.Array => string.Join(", ", value.EnumerateArray().Select(ToText)),
        JsonValueKind.Object => value.GetRawText(),
        _ => "",
    };

    /// <summary>"ExifImageWidth" → "Exif Image Width" for display.</summary>
    private static string Prettify(string name)
    {
        var sb = new StringBuilder(name.Length + 8);
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            if (i > 0 && char.IsUpper(c) && (char.IsLower(name[i - 1]) || (i + 1 < name.Length && char.IsLower(name[i + 1]) && char.IsUpper(name[i - 1]))))
                sb.Append(' ');
            sb.Append(c);
        }
        return sb.ToString();
    }

    private static string Compact(string prettyName) => prettyName.Replace(" ", "");
}
