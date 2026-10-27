using System.Text.Json.Serialization;
using Petal.Core.Formats;

namespace Petal.Core.Jobs;

/// <summary>Which new files a watched folder reacts to.</summary>
public enum WatchedKind
{
    Pictures,
    Videos,
    Audio,
    Documents,
    Any,
}

public enum WatchAction
{
    /// <summary>Convert to the format <see cref="WatchRule.Target"/> names.</summary>
    Convert,
    /// <summary>Run the favourite whose id <see cref="WatchRule.Target"/> holds.</summary>
    Favourite,
    /// <summary>Run the recipe whose id <see cref="WatchRule.Target"/> holds.</summary>
    Recipe,
    /// <summary>Move each file into a folder for its kind (screenshots, invoices, pictures…).</summary>
    Organize,
}

/// <summary>
/// An automation: files that arrive in a folder are converted as they land. Results always go to a
/// "DropWheel" folder inside the watched one, which is never watched itself, so a rule can't feed itself.
/// </summary>
public sealed class WatchRule
{
    public const string OutputFolderName = "DropWheel";

    /// <summary>Where results went before the app was called DropWheel; never watched either.</summary>
    public const string PreviousOutputFolderName = "Petal";

    public Guid Id { get; set; } = Guid.NewGuid();

    public bool Enabled { get; set; } = true;

    public string Folder { get; set; } = "";

    public bool IncludeSubfolders { get; set; }

    public WatchedKind Kind { get; set; } = WatchedKind.Pictures;

    public WatchAction Action { get; set; } = WatchAction.Convert;

    /// <summary>A format id for <see cref="WatchAction.Convert"/>, a favourite's id for <see cref="WatchAction.Favourite"/>.</summary>
    public string Target { get; set; } = Formats.Formats.Jpg.Id;

    /// <summary>Organize: pictures, videos, music, documents, archives and installers to folders of their own.</summary>
    public bool SortByKind { get; set; } = true;

    /// <summary>Organize: screen captures to a folder of their own.</summary>
    public bool SortScreenshots { get; set; } = true;

    /// <summary>Organize: invoices and receipts, recognised by their words, to a folder of their own.</summary>
    public bool SortInvoices { get; set; } = true;

    /// <summary>Organize: photos renamed after the moment they were taken.</summary>
    public bool RenameByDate { get; set; }

    [JsonIgnore]
    public string OutputFolder => Path.Combine(Folder, OutputFolderName);

    public WatchRule Copy() => (WatchRule)MemberwiseClone();

    /// <summary>
    /// True when a new file at <paramref name="path"/> is one this rule should handle: inside the
    /// folder (or a subfolder when asked), not in the results folder, finished downloading, and of
    /// the right kind.
    /// </summary>
    public bool Wants(string path)
    {
        if (string.IsNullOrWhiteSpace(Folder))
            return false;
        string full, root;
        try
        {
            full = Path.GetFullPath(path);
            root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Folder));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
        var dir = Path.GetDirectoryName(full);
        if (dir is null || !IsInside(dir, root))
            return false;
        // Sorting looks only at what lands in the folder itself, never at the folders it sorts into.
        if ((!IncludeSubfolders || Action == WatchAction.Organize) && !string.Equals(Path.TrimEndingDirectorySeparator(dir), root, StringComparison.OrdinalIgnoreCase))
            return false;
        if (IsInside(dir, Path.Combine(root, OutputFolderName)) || IsInside(dir, Path.Combine(root, PreviousOutputFolderName)))
            return false;
        if (IsUnfinished(Path.GetFileName(full)))
            return false;
        return Formats.Formats.FromPath(full) is { } format && Accepts(Kind, format);
    }

    public static bool Accepts(WatchedKind kind, FileFormat format) => kind switch
    {
        WatchedKind.Pictures => format.Family is FormatFamily.Image or FormatFamily.Vector,
        WatchedKind.Videos => format.Family == FormatFamily.Video,
        WatchedKind.Audio => format.Family == FormatFamily.Audio,
        WatchedKind.Documents => format.Family is FormatFamily.Pdf or FormatFamily.Document or FormatFamily.Text
            or FormatFamily.Ebook or FormatFamily.Slides or FormatFamily.Spreadsheet,
        _ => true,
    };

    /// <summary>Partial downloads, Office lock files and hidden dot-files.</summary>
    public static bool IsUnfinished(string name)
    {
        if (name.StartsWith("~$", StringComparison.Ordinal) || name.StartsWith('.'))
            return true;
        var ext = Path.GetExtension(name).ToLowerInvariant();
        return ext is ".crdownload" or ".part" or ".partial" or ".download" or ".tmp" or ".temp"
            or ".!ut" or ".!qb" or ".opdownload" or ".crswap";
    }

    private static bool IsInside(string dir, string root)
    {
        dir = Path.TrimEndingDirectorySeparator(dir);
        root = Path.TrimEndingDirectorySeparator(root);
        return dir.Equals(root, StringComparison.OrdinalIgnoreCase)
            || dir.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
