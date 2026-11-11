using Petal.Core.Formats;
using Petal.Core.Tools;

namespace Petal.Core.Jobs;

/// <summary>
/// Results are saved beside the original. A clash adds a counter before the extension:
/// "photo.png", "photo 2.png", "photo 3.png"; tools add a suffix: "photo Compressed.jpg".
/// </summary>
public static class OutputNaming
{
    private static readonly AsyncLocal<HashSet<string>?> Batch = new();

    /// <summary>
    /// While the returned scope is open, names handed out on this flow stay apart even before their
    /// files exist: files of one drop converted side by side ("photo.jpg" and "photo.png" to WEBP) must
    /// not both get "photo.webp". Names worked out for display outside the scope are unaffected.
    /// </summary>
    public static IDisposable KeepApart()
    {
        var previous = Batch.Value;
        Batch.Value = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return new Scope(() => Batch.Value = previous);
    }

    private sealed class Scope(Action close) : IDisposable
    {
        public void Dispose() => close();
    }

    /// <summary>
    /// Where the results of a file from a dropped folder go instead of beside it: the same place in a
    /// copy of the folder's tree ("Trip (WEBP)\Day 1"), or null for any other file. Results placed there
    /// keep the file's own name; the copy's name already says what was done.
    /// </summary>
    public static Func<string, string?>? PlacedIn { get; set; }

    /// <summary>The folder results of <paramref name="inputPath"/> go to when nothing else is asked for.</summary>
    public static string DirectoryFor(string inputPath) => PlacedIn?.Invoke(inputPath) ?? DirectoryOf(inputPath);

    public static string ForConversion(string inputPath, FileFormat target, string? outputDirectory = null) =>
        Unique(PlacedIn?.Invoke(inputPath) ?? outputDirectory ?? DirectoryOf(inputPath), Formats.Formats.BaseName(inputPath), target.WriteExtension);

    /// <summary>A file saved again with something changed, e.g. "photo 1600.jpg" beside "photo.jpg".</summary>
    public static string WithSuffix(string inputPath, string suffix, string extension, string? outputDirectory = null) =>
        PlacedIn?.Invoke(inputPath) is { } placed
            ? Unique(placed, Formats.Formats.BaseName(inputPath), extension)
            : Unique(outputDirectory ?? DirectoryOf(inputPath), $"{Formats.Formats.BaseName(inputPath)} {suffix}", extension);

    /// <param name="extensionOverride">Writes a different format than usual, e.g. "flac" when compressing WAV.</param>
    public static string ForTool(string inputPath, ToolInfo tool, string? suffixOverride = null, string? outputDirectory = null, string? extensionOverride = null)
    {
        var baseName = Formats.Formats.BaseName(inputPath);
        var placed = PlacedIn?.Invoke(inputPath);
        var suffix = placed is not null ? "" : suffixOverride ?? tool.OutputSuffix;
        var stem = suffix.Length == 0 ? baseName : $"{baseName} {suffix}";
        var dir = placed ?? outputDirectory ?? DirectoryOf(inputPath);

        if (extensionOverride is not null)
            return Unique(dir, stem, extensionOverride);
        return tool.OutputKind switch
        {
            ToolOutputKind.Folder => Unique(dir, stem, null),
            ToolOutputKind.FixedFormat => Unique(dir, stem, tool.OutputExtension),
            _ => Unique(dir, stem, WritableExtensionOf(inputPath)),
        };
    }

    /// <summary>
    /// The input's own extension, unless DropWheel reads that format without writing it: then the nearest one it
    /// writes, so a tool's result of a WMV is an MP4 and of a WMA an M4A.
    /// </summary>
    public static string WritableExtensionOf(string inputPath) => Formats.Formats.FromPath(inputPath) switch
    {
        { } f when f == Formats.Formats.Wmv => Formats.Formats.Mp4.WriteExtension,
        { } f when f == Formats.Formats.Wma => Formats.Formats.M4a.WriteExtension,
        _ => ExtensionOf(inputPath),
    };

    /// <summary>Picks "stem.ext", then "stem 2.ext", "stem 3.ext"… until nothing exists there.</summary>
    public static string Unique(string directory, string stem, string? extension, Func<string, bool>? exists = null)
    {
        var batch = exists is null ? Batch.Value : null;
        exists ??= p => File.Exists(p) || Directory.Exists(p);
        var ext = string.IsNullOrEmpty(extension) ? "" : "." + extension;
        if (batch is null)
            return Pick(directory, stem, ext, exists);
        lock (batch)
        {
            var name = Pick(directory, stem, ext, p => exists(p) || batch.Contains(p));
            batch.Add(name);
            return name;
        }
    }

    private static string Pick(string directory, string stem, string ext, Func<string, bool> exists)
    {
        var candidate = Path.Combine(directory, stem + ext);
        for (var n = 2; exists(candidate); n++)
            candidate = Path.Combine(directory, $"{stem} {n}{ext}");
        return candidate;
    }

    /// <summary>Extension as written, keeping double extensions such as "tar.gz".</summary>
    public static string ExtensionOf(string path)
    {
        var name = Path.GetFileName(path);
        if (name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
            return name[^6..];
        var ext = Path.GetExtension(name);
        return ext.Length > 1 ? ext[1..] : "";
    }

    private static string DirectoryOf(string path) =>
        Path.GetDirectoryName(Path.GetFullPath(path)) ?? Environment.CurrentDirectory;
}
