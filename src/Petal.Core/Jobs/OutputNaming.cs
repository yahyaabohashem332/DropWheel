using Petal.Core.Formats;
using Petal.Core.Tools;

namespace Petal.Core.Jobs;

/// <summary>
/// Results are saved beside the original. A clash adds a counter before the extension:
/// "photo.png", "photo 2.png", "photo 3.png"; tools add a suffix: "photo Compressed.jpg".
/// </summary>
public static class OutputNaming
{
    public static string ForConversion(string inputPath, FileFormat target, string? outputDirectory = null) =>
        Unique(outputDirectory ?? DirectoryOf(inputPath), Formats.Formats.BaseName(inputPath), target.WriteExtension);

    /// <param name="extensionOverride">Writes a different format than usual, e.g. "flac" when compressing WAV.</param>
    public static string ForTool(string inputPath, ToolInfo tool, string? suffixOverride = null, string? outputDirectory = null, string? extensionOverride = null)
    {
        var baseName = Formats.Formats.BaseName(inputPath);
        var suffix = suffixOverride ?? tool.OutputSuffix;
        var stem = suffix.Length == 0 ? baseName : $"{baseName} {suffix}";
        var dir = outputDirectory ?? DirectoryOf(inputPath);

        if (extensionOverride is not null)
            return Unique(dir, stem, extensionOverride);
        return tool.OutputKind switch
        {
            ToolOutputKind.Folder => Unique(dir, stem, null),
            ToolOutputKind.FixedFormat => Unique(dir, stem, tool.OutputExtension),
            _ => Unique(dir, stem, ExtensionOf(inputPath)),
        };
    }

    /// <summary>Picks "stem.ext", then "stem 2.ext", "stem 3.ext"… until nothing exists there.</summary>
    public static string Unique(string directory, string stem, string? extension, Func<string, bool>? exists = null)
    {
        exists ??= p => File.Exists(p) || Directory.Exists(p);
        var ext = string.IsNullOrEmpty(extension) ? "" : "." + extension;
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
