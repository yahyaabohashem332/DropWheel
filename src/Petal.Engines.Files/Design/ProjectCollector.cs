using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Petal.Engines.Files.Design;

/// <summary>A file a project uses.</summary>
/// <param name="Recorded">The path the project recorded.</param>
/// <param name="Found">Where it is on this PC, or null when it's missing.</param>
public sealed record CollectItem(string Recorded, string? Found, long Size);

/// <summary>A font a document uses, and the installed font that matches it (null: not on this PC).</summary>
public sealed record CollectFont(DesignFont Font, InstalledFont? Installed);

public sealed record CollectPlan(string Project, DesignDetails Details, IReadOnlyList<CollectItem> Files, IReadOnlyList<CollectFont> Fonts)
{
    public int Missing => Files.Count(f => f.Found is null);

    public long Size => new FileInfo(Project).Length + Files.Sum(f => f.Size);

    /// <summary>Premiere, After Effects and IDML copies are pointed at the collected files.</summary>
    public bool CanRelink => Path.GetExtension(Project).ToLowerInvariant() is ".prproj" or ".aep" or ".idml";
}

/// <param name="CopyFonts">Also copy the installed fonts the document uses (into "Document fonts", which InDesign loads by itself).</param>
public sealed record CollectOptions(bool CopyFonts = true);

/// <param name="Relinked">True when the copied project points at the collected files.</param>
public sealed record CollectResult(string Folder, string Project, int Copied, int Missing, int Fonts, bool Relinked);

/// <summary>
/// Gathers a project and everything it uses into one folder, ready to hand over or archive:
/// the project, its pictures and footage ("Links" for print and design, "Media" for video), the
/// fonts, and a list of what was collected and what is missing.
/// </summary>
public static partial class ProjectCollector
{
    public static bool CanCollect(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".prproj" or ".aep" or ".indd" or ".indt" or ".idml" or ".ai";

    public static CollectPlan Plan(string project)
    {
        var details = DesignDocuments.Read(project);
        var folder = Path.GetDirectoryName(Path.GetFullPath(project))!;
        var files = details.Links.Select(recorded =>
        {
            var found = Resolve(recorded, folder);
            return new CollectItem(recorded, found, found is null ? 0 : new FileInfo(found).Length);
        }).ToList();
        var fonts = details.Fonts.Select(f => new CollectFont(f, FontIndex.Find(f))).ToList();
        return new CollectPlan(project, details, files, fonts);
    }

    /// <summary>The recorded path if it exists, else a file of the same name in the project's folder, its "Links", "Media" or "Footage" folder, or below.</summary>
    public static string? Resolve(string recorded, string projectFolder)
    {
        try
        {
            if (File.Exists(recorded))
                return Path.GetFullPath(recorded);
        }
        catch (ArgumentException)
        {
        }
        var name = recorded.Replace('/', '\\').Split('\\').LastOrDefault(p => p.Length > 0);
        if (string.IsNullOrEmpty(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return null;
        foreach (var sub in new[] { "", "Links", "Media", "Footage" })
        {
            var candidate = Path.Combine(projectFolder, sub, name);
            if (File.Exists(candidate))
                return candidate;
        }
        try
        {
            return Directory.EnumerateFiles(projectFolder, name, new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 3, IgnoreInaccessible = true }).FirstOrDefault();
        }
        catch (IOException)
        {
            return null;
        }
    }

    public static Task<CollectResult> CollectAsync(CollectPlan plan, string folder, CollectOptions options, IProgress<double>? progress = null, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            Directory.CreateDirectory(folder);
            var ext = Path.GetExtension(plan.Project).ToLowerInvariant();
            bool video = ext is ".prproj" or ".aep";
            var linksFolder = Path.Combine(folder, video ? "Media" : "Links");
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var report = new StringBuilder();
            int copied = 0, step = 0, steps = plan.Files.Count + plan.Fonts.Count + 1;
            foreach (var item in plan.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (item.Found is { } source)
                {
                    Directory.CreateDirectory(linksFolder);
                    var target = UniquePath(linksFolder, Path.GetFileName(source), used);
                    File.Copy(source, target);
                    map[item.Recorded] = target;
                    map.TryAdd(source, target);
                    copied++;
                    report.AppendLine($"Copied   {item.Recorded}  →  {Path.GetRelativePath(folder, target)}");
                }
                else
                {
                    report.AppendLine($"Missing  {item.Recorded}");
                }
                progress?.Report(++step / (double)steps);
            }

            int fonts = 0;
            if (options.CopyFonts)
            {
                var fontsFolder = Path.Combine(folder, "Document fonts");
                foreach (var font in plan.Fonts.Where(f => f.Installed is not null).DistinctBy(f => f.Installed!.File, StringComparer.OrdinalIgnoreCase))
                {
                    Directory.CreateDirectory(fontsFolder);
                    var target = Path.Combine(fontsFolder, Path.GetFileName(font.Installed!.File));
                    if (!File.Exists(target))
                        File.Copy(font.Installed.File, target);
                    fonts++;
                    progress?.Report(++step / (double)steps);
                }
            }
            foreach (var font in plan.Fonts.Where(f => f.Installed is null))
                report.AppendLine($"Font not on this PC: {font.Font.Name}");

            // The project itself, pointed at the collected copies where its format allows.
            var projectCopy = Path.Combine(folder, Path.GetFileName(plan.Project));
            bool relinked = ext switch
            {
                ".prproj" => RelinkPremiere(plan.Project, projectCopy, map),
                ".aep" => RelinkAfterEffects(plan.Project, projectCopy, map),
                ".idml" => RelinkIdml(plan.Project, projectCopy, map),
                _ => false,
            };
            if (!relinked)
                File.Copy(plan.Project, projectCopy, overwrite: true);
            report.Insert(0, $"{Path.GetFileName(plan.Project)}: {copied} files collected, {plan.Missing} missing, {fonts} fonts.{Environment.NewLine}" +
                (relinked ? "The copied project points at the collected files." : "The copied project still points at the original locations; relink to the Links folder if they move.") +
                Environment.NewLine + Environment.NewLine);
            File.WriteAllText(Path.Combine(folder, "Collected files.txt"), report.ToString(), new UTF8Encoding(true));
            progress?.Report(1);
            return new CollectResult(folder, projectCopy, copied, plan.Missing, fonts, relinked && copied > 0);
        }, cancellationToken);

    private static string UniquePath(string folder, string name, HashSet<string> used)
    {
        var stem = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name);
        var path = Path.Combine(folder, name);
        for (int n = 2; used.Contains(path) || File.Exists(path); n++)
            path = Path.Combine(folder, $"{stem} ({n}){ext}");
        used.Add(path);
        return path;
    }

    // ─────────────── Premiere Pro ───────────────

    /// <summary>Rewrites each Media item's FilePath, ActualMediaFilePath and RelativePath; everything else stays byte for byte.</summary>
    internal static bool RelinkPremiere(string project, string output, IReadOnlyDictionary<string, string> map)
    {
        if (map.Count == 0)
            return false;
        var xml = DesignDocuments.PremiereXml(project);
        var outputFolder = Path.GetDirectoryName(output)!;
        int changed = 0;
        var result = MediaElement().Replace(xml, media =>
        {
            var body = media.Value;
            string? newPath = null;
            foreach (Match m in PathElement().Matches(body))
            {
                var recorded = System.Net.WebUtility.HtmlDecode(m.Groups[2].Value).Trim();
                if (map.TryGetValue(recorded, out var target))
                {
                    newPath = target;
                    break;
                }
            }
            if (newPath is null)
                return body;
            changed++;
            var escaped = System.Security.SecurityElement.Escape(newPath)!;
            var relative = System.Security.SecurityElement.Escape(Path.GetRelativePath(outputFolder, newPath))!;
            body = PathElement().Replace(body, m => $"<{m.Groups[1].Value}>{escaped}</{m.Groups[1].Value}>");
            return RelativePathElement().Replace(body, $"<RelativePath>{relative}</RelativePath>");
        });
        if (changed == 0)
            return false;
        var bytes = Encoding.UTF8.GetBytes(result);
        using var file = File.Create(output);
        using var gz = new GZipStream(file, CompressionLevel.Optimal);
        gz.Write(bytes);
        return true;
    }

    // ─────────────── After Effects ───────────────

    /// <summary>
    /// Points each footage alias at its collected copy. Only when the project reads back exactly
    /// as it was written (so nothing else can change) and every path took.
    /// </summary>
    internal static bool RelinkAfterEffects(string project, string output, IReadOnlyDictionary<string, string> map)
    {
        if (map.Count == 0)
            return false;
        var original = File.ReadAllBytes(project);
        var root = Rifx.Parse(original);
        if (root is null || !Rifx.Write(root).AsSpan().SequenceEqual(original))
            return false;
        int changed = 0;
        foreach (var alias in root.Descendants().Where(c => c.Id == "alas"))
        {
            var path = DesignDocuments.AliasPath(alias.Data);
            if (path is null || !map.TryGetValue(path, out var target))
                continue;
            var json = JsonNode.Parse(Encoding.UTF8.GetString(alias.Data).TrimEnd('\0'))!.AsObject();
            json["fullpath"] = target;
            alias.Data = Encoding.UTF8.GetBytes(json.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            changed++;
        }
        if (changed == 0)
            return false;
        var bytes = Rifx.Write(root);
        var check = Rifx.Parse(bytes);
        // Every footage path is now a collected copy (or one that wasn't collected because it is missing).
        var targets = map.Values.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (check is null || !DesignDocuments.AfterEffectsFootage(check).All(p => targets.Contains(p) || !map.ContainsKey(p)))
            return false;
        File.WriteAllBytes(output, bytes);
        return true;
    }

    // ─────────────── IDML ───────────────

    /// <summary>A copy of the package with each LinkResourceURI pointing at the collected file; the mimetype stays first and uncompressed.</summary>
    internal static bool RelinkIdml(string project, string output, IReadOnlyDictionary<string, string> map)
    {
        if (map.Count == 0)
            return false;
        int changed = 0;
        using (var source = ZipFile.OpenRead(project))
        using (var target = ZipFile.Open(output, ZipArchiveMode.Create))
        {
            foreach (var entry in source.Entries)
            {
                bool isMimetype = entry.FullName == "mimetype";
                var copy = target.CreateEntry(entry.FullName, isMimetype ? CompressionLevel.NoCompression : CompressionLevel.Optimal);
                copy.LastWriteTime = entry.LastWriteTime;
                using var from = entry.Open();
                using var to = copy.Open();
                if (entry.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                {
                    using var reader = new StreamReader(from, Encoding.UTF8);
                    var xml = reader.ReadToEnd();
                    var updated = LinkUri().Replace(xml, m =>
                    {
                        var recorded = XmpPacket.ToPath(System.Net.WebUtility.HtmlDecode(m.Groups[1].Value));
                        if (!map.TryGetValue(recorded, out var newPath))
                            return m.Value;
                        changed++;
                        return $"LinkResourceURI=\"{System.Security.SecurityElement.Escape(new Uri(newPath).AbsoluteUri)}\"";
                    });
                    var bytes = new UTF8Encoding(false).GetBytes(updated);
                    to.Write(bytes);
                }
                else
                {
                    from.CopyTo(to);
                }
            }
        }
        if (changed == 0)
        {
            File.Delete(output);
            return false;
        }
        return true;
    }

    [GeneratedRegex(@"<Media\b[^>]*>.*?</Media>", RegexOptions.Singleline)]
    private static partial Regex MediaElement();

    [GeneratedRegex(@"<(FilePath|ActualMediaFilePath)>([^<]*)</\1>")]
    private static partial Regex PathElement();

    [GeneratedRegex(@"<RelativePath>[^<]*</RelativePath>")]
    private static partial Regex RelativePathElement();

    [GeneratedRegex(@"LinkResourceURI=""([^""]+)""")]
    private static partial Regex LinkUri();
}
