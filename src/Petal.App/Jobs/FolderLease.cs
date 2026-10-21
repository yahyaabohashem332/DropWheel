using System.Collections.Concurrent;
using System.IO;
using Petal.Core.Jobs;

namespace Petal.App.Jobs;

/// <summary>
/// Results of the files inside a dropped folder go to a copy of its tree beside it ("Trip" gives
/// "Trip (WEBP)\Day 1\photo.webp"), never into the folder itself. A lease holds that placement while a
/// tool's window is open and while the jobs it started run; when the last of them ends the placement
/// goes, with every folder of the copy that stayed empty.
/// </summary>
public sealed class FolderLease
{
    private static readonly ConcurrentDictionary<string, (FolderLease Lease, string Directory)> Placed = new(StringComparer.OrdinalIgnoreCase);

    private readonly List<string> _copies = [];
    private readonly List<string> _inputs = [];
    private int _holds = 1;

    private FolderLease(IReadOnlyList<string> files) => Files = files;

    /// <summary>The files to work on, in order: those dropped themselves, then each folder's.</summary>
    public IReadOnlyList<string> Files { get; }

    /// <summary>The copies of the dropped folders, one per folder.</summary>
    public IReadOnlyList<string> Copies => _copies;

    /// <summary>Makes <see cref="OutputNaming"/> place results; called once at start.</summary>
    public static void Install() => OutputNaming.PlacedIn = DirectoryFor;

    /// <summary>
    /// Places the files of <paramref name="contents"/> that <paramref name="include"/> takes. Each dropped
    /// folder gets its copy, "name (<paramref name="label"/>)", beside it or in <paramref name="outputFolder"/>.
    /// The lease starts held once, by the caller.
    /// </summary>
    public static FolderLease Place(FolderContents contents, Func<FolderFile, bool> include, string label, Func<string, string?>? outputFolder = null)
    {
        var chosen = contents.Files.Where(include).ToList();
        var lease = new FolderLease(chosen.Select(f => f.Path).ToList());
        var copies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in chosen)
        {
            if (file.Root is not { } root)
                continue;
            if (!copies.TryGetValue(root, out var copy))
            {
                var parent = outputFolder?.Invoke(root) ?? Path.GetDirectoryName(root) ?? root;
                copy = OutputNaming.Unique(parent, $"{Path.GetFileName(root)} ({label})", null);
                // Made now, so a second lease for the same folder picks another name.
                Directory.CreateDirectory(copy);
                copies[root] = copy;
                lease._copies.Add(copy);
            }
            Placed[file.Path] = (lease, Path.Combine(copy, file.RelativeDirectory));
            lease._inputs.Add(file.Path);
        }
        return lease;
    }

    /// <summary>Where a placed file's results go (the folder is made when first asked for), or null.</summary>
    public static string? DirectoryFor(string input)
    {
        if (!Placed.TryGetValue(Path.GetFullPath(input), out var placed))
            return null;
        Directory.CreateDirectory(placed.Directory);
        return placed.Directory;
    }

    /// <summary>Holds every lease these inputs are placed by until the returned scope ends: a running job.</summary>
    public static IDisposable HoldFor(IEnumerable<string> inputs)
    {
        var leases = inputs.Select(i => Placed.TryGetValue(Path.GetFullPath(i), out var p) ? p.Lease : null).OfType<FolderLease>().Distinct().ToList();
        foreach (var lease in leases)
            lease.Hold();
        return new Scope(() => leases.ForEach(l => l.Release()));
    }

    public void Hold() => Interlocked.Increment(ref _holds);

    public void Release()
    {
        if (Interlocked.Decrement(ref _holds) != 0)
            return;
        foreach (var input in _inputs)
            if (Placed.TryGetValue(input, out var placed) && placed.Lease == this)
                Placed.TryRemove(input, out _);
        foreach (var copy in _copies)
            RemoveEmpty(copy);
    }

    /// <summary>Deletes the folders under <paramref name="directory"/>, and it, that hold nothing.</summary>
    private static void RemoveEmpty(string directory)
    {
        try
        {
            if (!Directory.Exists(directory))
                return;
            foreach (var sub in Directory.GetDirectories(directory))
                RemoveEmpty(sub);
            if (!Directory.EnumerateFileSystemEntries(directory).Any())
                Directory.Delete(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left as it is: something else is using it.
        }
    }

    private sealed class Scope(Action end) : IDisposable
    {
        private int _ended;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _ended, 1) == 0)
                end();
        }
    }
}
