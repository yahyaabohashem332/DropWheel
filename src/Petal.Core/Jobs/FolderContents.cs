using System.Diagnostics;
using System.IO.Enumeration;
using Petal.Core.Formats;

namespace Petal.Core.Jobs;

/// <summary>A file of a drop. <see cref="Root"/> is the dropped folder it was found in, null for a file dropped itself.</summary>
/// <param name="RelativeDirectory">Its folder under <see cref="Root"/> ("" directly inside, "Day 1\Beach" deeper).</param>
public sealed record FolderFile(string Path, string? Root, string RelativeDirectory, FileFormat? Format, long Length)
{
    /// <summary>Pictures, video, audio… with read-only pictures (RAW, Photoshop, SVG) counted as pictures.</summary>
    public FormatFamily? Kind => Format is null ? null : FolderContents.KindOf(Format);
}

/// <summary>
/// What a drop holds when it includes folders: a folder stands for the files inside it and its
/// subfolders. Hidden and system files, temporary and half-downloaded files, links to other
/// folders and folders such as ".git" are left out. Files come in the order Explorer shows them.
/// </summary>
public sealed class FolderContents
{
    private static readonly string[] TemporaryEndings = [".tmp", ".temp", ".crdownload", ".part", ".partial", ".download", ".opdownload", ".petal-tmp"];
    private static readonly HashSet<string> SkippedFolders = new(StringComparer.OrdinalIgnoreCase) { "node_modules", "$RECYCLE.BIN", "System Volume Information" };

    private FolderContents(IReadOnlyList<string> folders, IReadOnlyList<FolderFile> files, bool complete)
    {
        Folders = folders;
        Files = files;
        Complete = complete;
    }

    /// <summary>The folders that were dropped.</summary>
    public IReadOnlyList<string> Folders { get; }

    public IReadOnlyList<FolderFile> Files { get; }

    /// <summary>False when <see cref="Read"/> ran out of time: a quick look, not the whole folder.</summary>
    public bool Complete { get; }

    public long TotalLength => Files.Sum(f => f.Length);

    public static bool HasFolders(IEnumerable<string> paths) => paths.Any(Directory.Exists);

    /// <summary>Read-only pictures count as pictures: a folder of RAW and JPEG photos is a folder of photos.</summary>
    public static FormatFamily KindOf(FileFormat format) => format.Family == FormatFamily.Vector ? FormatFamily.Image : format.Family;

    /// <summary>The kind most of the files are, or null when none is known.</summary>
    public FormatFamily? MainKind =>
        Files.Where(f => f.Kind is not null).GroupBy(f => f.Kind!.Value).OrderByDescending(g => g.Count()).Select(g => (FormatFamily?)g.Key).FirstOrDefault();

    public IReadOnlyList<FolderFile> OfKind(FormatFamily kind) => Files.Where(f => f.Kind == kind).ToList();

    /// <summary>
    /// The files of <paramref name="paths"/>: each file itself, and everything inside each folder.
    /// With a <paramref name="limit"/> the reading stops when time runs out (for the wheel, which
    /// must open at once) and <see cref="Complete"/> is false.
    /// </summary>
    public static FolderContents Read(IReadOnlyList<string> paths, TimeSpan? limit = null, CancellationToken cancellationToken = default)
    {
        var clock = Stopwatch.StartNew();
        var folders = new List<string>();
        var files = new List<FolderFile>();
        bool complete = true;
        foreach (var path in paths)
        {
            if (Directory.Exists(path))
            {
                var root = System.IO.Path.GetFullPath(path).TrimEnd(System.IO.Path.DirectorySeparatorChar);
                folders.Add(root);
                var found = new List<FolderFile>();
                foreach (var (file, length) in Inside(root))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (limit is { } l && found.Count % 64 == 0 && clock.Elapsed > l)
                    {
                        complete = false;
                        break;
                    }
                    var relative = System.IO.Path.GetRelativePath(root, System.IO.Path.GetDirectoryName(file)!);
                    found.Add(new FolderFile(file, root, relative == "." ? "" : relative, Formats.Formats.FromPath(file), length));
                }
                found.Sort((a, b) =>
                {
                    int byFolder = NaturalOrder.Compare(a.RelativeDirectory, b.RelativeDirectory);
                    return byFolder != 0 ? byFolder : NaturalOrder.Compare(System.IO.Path.GetFileName(a.Path), System.IO.Path.GetFileName(b.Path));
                });
                files.AddRange(found);
                if (!complete)
                    break;
            }
            else if (File.Exists(path))
            {
                long length = 0;
                try { length = new FileInfo(path).Length; } catch (IOException) { }
                files.Add(new FolderFile(System.IO.Path.GetFullPath(path), null, "", Formats.Formats.FromPath(path), length));
            }
        }
        return new FolderContents(folders, files, complete);
    }

    private static IEnumerable<(string Path, long Length)> Inside(string root) =>
        new FileSystemEnumerable<(string, long)>(root, (ref FileSystemEntry e) => (e.ToFullPath(), e.Length), new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
        })
        {
            ShouldIncludePredicate = (ref FileSystemEntry e) => !e.IsDirectory && !Temporary(e.FileName),
            // Links and junctions can point back up the tree; dot-folders hold tools' own files.
            ShouldRecursePredicate = (ref FileSystemEntry e) =>
                (e.Attributes & FileAttributes.ReparsePoint) == 0 && !e.FileName.StartsWith('.') && !SkippedFolders.Contains(e.FileName.ToString()),
        };

    private static bool Temporary(ReadOnlySpan<char> name)
    {
        if (name.StartsWith("~$") || name.Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase) || name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase))
            return true;
        foreach (var ending in TemporaryEndings)
            if (name.EndsWith(ending, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }
}

/// <summary>Names in the order Explorer shows them: "photo 2" before "photo 10".</summary>
public static class NaturalOrder
{
    public static int Compare(string? a, string? b)
    {
        if (ReferenceEquals(a, b)) return 0;
        if (a is null) return -1;
        if (b is null) return 1;
        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            if (char.IsAsciiDigit(a[i]) && char.IsAsciiDigit(b[j]))
            {
                int si = i, sj = j;
                while (i < a.Length && char.IsAsciiDigit(a[i])) i++;
                while (j < b.Length && char.IsAsciiDigit(b[j])) j++;
                var da = a.AsSpan(si, i - si).TrimStart('0');
                var db = b.AsSpan(sj, j - sj).TrimStart('0');
                if (da.Length != db.Length)
                    return da.Length.CompareTo(db.Length);
                int digits = da.SequenceCompareTo(db);
                if (digits != 0)
                    return digits;
                continue;
            }
            int c = string.Compare(a, i, b, j, 1, StringComparison.CurrentCultureIgnoreCase);
            if (c != 0)
                return c;
            i++;
            j++;
        }
        return (a.Length - i).CompareTo(b.Length - j);
    }
}
