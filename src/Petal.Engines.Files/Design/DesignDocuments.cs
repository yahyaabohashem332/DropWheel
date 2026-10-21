using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Petal.Engines.Files.Design;

/// <summary>What a design or video project holds, as shown by File Info and Collect Files.</summary>
/// <param name="Kind">"InDesign document", "Premiere Pro project"…</param>
/// <param name="Facts">Label and value pairs: pages, page size, artboards, sequences…</param>
/// <param name="Links">Files the project uses, as recorded where it was saved.</param>
public sealed record DesignDetails(
    string Kind,
    string? Application,
    IReadOnlyList<(string Label, string Value)> Facts,
    IReadOnlyList<DesignFont> Fonts,
    IReadOnlyList<string> Links,
    IReadOnlyList<string> Swatches,
    byte[]? Preview);

/// <summary>
/// Reads InDesign (INDD, IDML), XD, Premiere Pro and After Effects projects, and the XMP of
/// Illustrator and EPS files, without the applications.
/// </summary>
public static partial class DesignDocuments
{
    public static bool CanRead(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".indd" or ".indt" or ".idml" or ".xd" or ".prproj" or ".aep" or ".ai" or ".eps" or ".epsf";

    public static DesignDetails Read(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".indd" or ".indt" => FromXmp(path, "InDesign document"),
        ".idml" => Idml(path),
        ".xd" => Xd(path),
        ".prproj" => Premiere(path),
        ".aep" => AfterEffects(path),
        ".ai" => FromXmp(path, "Illustrator file"),
        ".eps" or ".epsf" => FromXmp(path, "EPS file"),
        _ => throw new NotSupportedException("Petal can't read this kind of file."),
    };

    /// <summary>The preview the application stored: InDesign's and Illustrator's XMP thumbnail, XD's preview picture.</summary>
    public static byte[]? Preview(string path) => Read(path).Preview;

    // ─────────────── InDesign, Illustrator, EPS: XMP ───────────────

    private static DesignDetails FromXmp(string path, string kind)
    {
        var xmp = XmpPacket.Read(path);
        var facts = new List<(string, string)>();
        if (xmp is null)
            return new DesignDetails(kind, null, facts, [], [], [], null);
        if (xmp.Pages is int pages)
            facts.Add((kind.StartsWith("InDesign", StringComparison.Ordinal) ? "Pages" : "Artboards", pages.ToString(CultureInfo.InvariantCulture)));
        if (xmp.PageSize is { } size && size.Width > 0)
            facts.Add(("Page size", FormatSize(size.Width, size.Height, size.Unit)));
        if (xmp.Plates.Count > 0)
            facts.Add(("Colour plates", string.Join(", ", xmp.Plates)));
        return new DesignDetails(kind, xmp.CreatorTool, facts, xmp.Fonts, xmp.LinkedFiles, xmp.Swatches, xmp.Thumbnail);
    }

    /// <summary>"210 × 297 mm" from a size and its XMP unit.</summary>
    public static string FormatSize(double width, double height, string unit)
    {
        var (w, h, label) = unit.ToLowerInvariant() switch
        {
            "points" => (width * 25.4 / 72, height * 25.4 / 72, "mm"),
            "inches" => (width * 25.4, height * 25.4, "mm"),
            "millimeters" => (width, height, "mm"),
            "centimeters" => (width * 10, height * 10, "mm"),
            "pixels" => (width, height, "px"),
            _ => (width, height, unit.ToLowerInvariant()),
        };
        string n(double v) => Math.Round(v, label == "px" ? 0 : 1).ToString("0.#", CultureInfo.InvariantCulture);
        return $"{n(w)} × {n(h)} {label}".TrimEnd();
    }

    // ─────────────── IDML ───────────────

    private static DesignDetails Idml(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var facts = new List<(string, string)>();
        string? application = null;
        if (zip.GetEntry("designmap.xml") is { } designmap)
        {
            var text = ReadEntry(designmap);
            var m = AidProduct().Match(text);
            if (m.Success)
                application = "Adobe InDesign " + m.Groups[1].Value;
        }
        int pages = 0;
        var links = new List<string>();
        foreach (var entry in zip.Entries.Where(e => e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)))
        {
            var text = ReadEntry(entry);
            if (entry.FullName.StartsWith("Spreads/", StringComparison.OrdinalIgnoreCase))
                pages += PageElement().Count(text);
            foreach (Match link in LinkUri().Matches(text))
                links.Add(XmpPacket.ToPath(WebUtilityDecode(link.Groups[1].Value)));
        }
        facts.Add(("Pages", pages.ToString(CultureInfo.InvariantCulture)));
        if (zip.GetEntry("Resources/Preferences.xml") is { } prefs)
        {
            var m = PageSizeAttributes().Match(ReadEntry(prefs));
            if (m.Success)
                facts.Add(("Page size", FormatSize(double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture), "points")));
        }
        int stories = zip.Entries.Count(e => e.FullName.StartsWith("Stories/", StringComparison.OrdinalIgnoreCase));
        facts.Add(("Stories", stories.ToString(CultureInfo.InvariantCulture)));
        var fonts = new List<DesignFont>();
        if (zip.GetEntry("Resources/Fonts.xml") is { } fontsXml)
        {
            var doc = XDocument.Parse(ReadEntry(fontsXml));
            foreach (var font in doc.Descendants().Where(e => e.Name.LocalName == "Font"))
            {
                var family = font.Attribute("FontFamily")?.Value;
                var style = font.Attribute("FontStyleName")?.Value;
                var name = font.Attribute("Name")?.Value ?? $"{family} {style}".Trim();
                fonts.Add(new DesignFont(name, family, style, font.Attribute("PostScriptName")?.Value, null, font.Attribute("FontType")?.Value));
            }
        }
        var swatches = new List<string>();
        if (zip.GetEntry("Resources/Graphic.xml") is { } graphic)
        {
            var doc = XDocument.Parse(ReadEntry(graphic));
            swatches.AddRange(doc.Descendants().Where(e => e.Name.LocalName == "Color" && e.Attribute("Name") is not null)
                .Select(e => e.Attribute("Name")!.Value).Where(n => n.Length > 0 && !n.StartsWith('$')).Distinct());
        }
        return new DesignDetails("InDesign Markup (IDML)", application, facts, fonts, links.Distinct(StringComparer.OrdinalIgnoreCase).ToList(), swatches, null);
    }

    private static string WebUtilityDecode(string text) => System.Net.WebUtility.HtmlDecode(text);

    private static string ReadEntry(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    // ─────────────── XD ───────────────

    private static DesignDetails Xd(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var facts = new List<(string, string)>();
        var artboards = new List<string>();
        if (zip.GetEntry("manifest") is { } manifest)
        {
            try
            {
                using var json = JsonDocument.Parse(ReadEntry(manifest));
                CollectArtboards(json.RootElement, artboards);
            }
            catch (JsonException)
            {
                // A manifest Petal doesn't understand: no artboard list.
            }
        }
        if (artboards.Count > 0)
        {
            facts.Add(("Artboards", artboards.Count.ToString(CultureInfo.InvariantCulture)));
            facts.Add(("Artboard names", string.Join(", ", artboards.Take(12)) + (artboards.Count > 12 ? "…" : "")));
        }
        // The cover picture XD saves for previews: the largest PNG at the top level or among the renditions.
        var preview = zip.Entries
            .Where(e => e.FullName.EndsWith(".png", StringComparison.OrdinalIgnoreCase) && (!e.FullName.Contains('/') || e.FullName.StartsWith("renditions/", StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(e => e.FullName.Equals("preview.png", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(e => e.Length)
            .FirstOrDefault();
        byte[]? image = null;
        if (preview is not null)
        {
            using var stream = preview.Open();
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            image = ms.ToArray();
        }
        string? application = null;
        if (zip.GetEntry("META-INF/metadata.xml") is { } metadata && XmpPacket.Parse(Encoding.UTF8.GetBytes(ReadEntry(metadata))) is { } xmp)
            application = xmp.CreatorTool;
        return new DesignDetails("XD document", application ?? "Adobe XD", facts, [], [], [], image);
    }

    private static void CollectArtboards(JsonElement element, List<string> names)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.String && p.GetString()!.StartsWith("artboard-", StringComparison.Ordinal)
                && element.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String)
                names.Add(n.GetString()!);
            foreach (var property in element.EnumerateObject())
                CollectArtboards(property.Value, names);
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                CollectArtboards(item, names);
        }
    }

    // ─────────────── Premiere Pro ───────────────

    /// <summary>A Premiere project's XML (it is gzip-compressed, though old ones may be plain).</summary>
    public static string PremiereXml(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length > 2 && bytes[0] == 0x1F && bytes[1] == 0x8B)
        {
            using var gz = new GZipStream(new MemoryStream(bytes), CompressionMode.Decompress);
            using var reader = new StreamReader(gz, Encoding.UTF8);
            return reader.ReadToEnd();
        }
        return Encoding.UTF8.GetString(bytes);
    }

    /// <summary>The media a Premiere project uses: ActualMediaFilePath where present, else FilePath, of each Media item.</summary>
    internal static List<string> PremiereMedia(string xml)
    {
        var files = new List<string>();
        foreach (Match media in MediaElement().Matches(xml))
        {
            var body = media.Groups[1].Value;
            var actual = ActualPath().Match(body);
            var file = actual.Success ? actual : FilePathElement().Match(body);
            if (!file.Success)
                continue;
            var path = System.Net.WebUtility.HtmlDecode(file.Groups[1].Value).Trim();
            if (path.Length > 3 && (path[1] == ':' || path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith('/')))
                files.Add(path);
        }
        return files.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static DesignDetails Premiere(string path)
    {
        var xml = PremiereXml(path);
        var facts = new List<(string, string)>();
        var sequences = SequenceName().Matches(xml).Select(m => System.Net.WebUtility.HtmlDecode(m.Groups[1].Value)).Distinct().ToList();
        var media = PremiereMedia(xml);
        facts.Add(("Sequences", sequences.Count.ToString(CultureInfo.InvariantCulture)));
        if (sequences.Count > 0)
            facts.Add(("Sequence names", string.Join(", ", sequences.Take(12)) + (sequences.Count > 12 ? "…" : "")));
        facts.Add(("Media files", media.Count.ToString(CultureInfo.InvariantCulture)));
        var version = PremiereVersion().Match(xml);
        return new DesignDetails("Premiere Pro project", version.Success ? $"Adobe Premiere Pro (project version {version.Groups[1].Value})" : "Adobe Premiere Pro", facts, [], media, [], null);
    }

    // ─────────────── After Effects ───────────────

    /// <summary>The footage files an After Effects project uses, from its "alas" chunks (JSON with the full path).</summary>
    internal static List<string> AfterEffectsFootage(RiffChunk root) =>
        root.Descendants().Where(c => c.Id == "alas").Select(c => AliasPath(c.Data)).OfType<string>()
            .Where(p => p.Length > 3).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    internal static string? AliasPath(byte[] data)
    {
        try
        {
            using var json = JsonDocument.Parse(Encoding.UTF8.GetString(data).TrimEnd('\0'));
            return json.RootElement.TryGetProperty("fullpath", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static DesignDetails AfterEffects(string path)
    {
        var root = Rifx.Parse(File.ReadAllBytes(path)) ?? throw new InvalidDataException("This isn't an After Effects project.");
        if (root.ListType != "Egg!")
            throw new InvalidDataException("This isn't an After Effects project.");
        var footage = AfterEffectsFootage(root);
        var facts = new List<(string, string)> { ("Footage files", footage.Count.ToString(CultureInfo.InvariantCulture)) };
        return new DesignDetails("After Effects project", "Adobe After Effects", facts, [], footage, [], null);
    }

    [GeneratedRegex(@"<\?aid [^?]*product=""([^""(]+)")]
    private static partial Regex AidProduct();

    [GeneratedRegex(@"<Page\s")]
    private static partial Regex PageElement();

    [GeneratedRegex(@"LinkResourceURI=""([^""]+)""")]
    private static partial Regex LinkUri();

    [GeneratedRegex(@"PageWidth=""([\d.]+)""\s+PageHeight=""([\d.]+)""")]
    private static partial Regex PageSizeAttributes();

    [GeneratedRegex(@"<Media\b[^>]*>(.*?)</Media>", RegexOptions.Singleline)]
    private static partial Regex MediaElement();

    [GeneratedRegex(@"<ActualMediaFilePath>([^<]+)</ActualMediaFilePath>")]
    private static partial Regex ActualPath();

    [GeneratedRegex(@"<FilePath>([^<]+)</FilePath>")]
    private static partial Regex FilePathElement();

    [GeneratedRegex(@"<Sequence\b[^>]*>\s*(?:<[^>]+>\s*)*?<Name>([^<]+)</Name>", RegexOptions.Singleline)]
    private static partial Regex SequenceName();

    [GeneratedRegex(@"<PremiereData\s+Version=""(\d+)""")]
    private static partial Regex PremiereVersion();
}
