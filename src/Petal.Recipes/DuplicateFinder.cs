using System.IO;
using System.Security.Cryptography;
using Petal.Core.Formats;
using Petal.Engines.Imaging;
using Petal.Engines.Media;

namespace Petal.Recipes;

public enum DuplicateKind
{
    /// <summary>Byte-for-byte the same file.</summary>
    Identical,
    /// <summary>The same picture at another size, quality or format.</summary>
    SimilarPhotos,
    /// <summary>The same video re-encoded or resized.</summary>
    SimilarVideos,
    /// <summary>Photos of the same moment taken one after another (a burst): the sharpest is kept.</summary>
    SimilarShots,
}

public sealed record DuplicateFile(string Path, long Size, DateTime Modified)
{
    /// <summary>Pixels across × down, for pictures and videos.</summary>
    public int Width { get; init; }
    public int Height { get; init; }

    /// <summary>For similar shots: how sharp the photo is (higher is sharper).</summary>
    public double Sharpness { get; init; }
}

public sealed record DuplicateGroup(DuplicateKind Kind, IReadOnlyList<DuplicateFile> Files)
{
    /// <summary>What removing all but the largest would free.</summary>
    public long Spare => Files.Sum(f => f.Size) - Files.Max(f => f.Size);
}

public sealed record DuplicateOptions
{
    public bool SimilarPhotos { get; init; } = true;
    public bool SimilarVideos { get; init; } = true;
    public bool SimilarShots { get; init; }
    public bool IncludeSubfolders { get; init; } = true;

    /// <summary>Smaller files are left out (empty files are all "identical").</summary>
    public long MinimumBytes { get; init; } = 1;
}

/// <param name="Fraction">0 to 1.</param>
/// <param name="Files">Files looked at so far.</param>
public readonly record struct DuplicateProgress(double Fraction, int Files, string Current);

/// <summary>
/// Finds files that are the same (by content, not name) and, optionally, photos and videos that
/// are copies at another size or quality. Nothing is changed; the caller decides what goes.
/// </summary>
public static class DuplicateFinder
{
    /// <summary>The most pictures compared with each other in one search (each pair is compared).</summary>
    private const int MaxPictures = 40_000;

    public static async Task<IReadOnlyList<DuplicateGroup>> FindAsync(IReadOnlyList<string> paths, DuplicateOptions? options = null,
        IProgress<DuplicateProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        options ??= new DuplicateOptions();
        var files = Collect(paths, options, progress, cancellationToken);
        var groups = new List<DuplicateGroup>();

        // Identical: same size, then same first 64 KB, then the same SHA-256.
        var identical = new List<List<FileInfo>>();
        var bySize = files.GroupBy(f => f.Length).Where(g => g.Count() > 1).ToList();
        long toHash = bySize.Sum(g => g.Sum(f => f.Length)), hashed = 0;
        foreach (var sameSize in bySize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var sameStart in sameSize.GroupBy(f => Hash(f.FullName, 64 * 1024)).Where(g => g.Key is not null && g.Count() > 1))
            {
                var full = sameSize.Key <= 64 * 1024
                    ? [sameStart.ToList()]
                    : sameStart.GroupBy(f => Hash(f.FullName, long.MaxValue)).Where(g => g.Key is not null && g.Count() > 1).Select(g => g.ToList()).ToList();
                identical.AddRange(full);
            }
            hashed += sameSize.Sum(f => f.Length);
            progress?.Report(new DuplicateProgress(0.2 + 0.3 * hashed / Math.Max(1, toHash), files.Count, sameSize.First().DirectoryName ?? ""));
        }
        groups.AddRange(identical.Select(g => new DuplicateGroup(DuplicateKind.Identical, g.Select(File).OrderBy(f => f.Modified).ToList())));

        // One of each identical set stands for it when looking for near copies: the oldest, as
        // Keeper picks for identical files, so both sets keep the same file.
        var twins = identical.SelectMany(g => g.OrderBy(f => f.LastWriteTime).Skip(1)).Select(f => f.FullName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var single = files.Where(f => !twins.Contains(f.FullName)).ToList();

        if (options.SimilarPhotos)
            groups.AddRange(await SimilarPhotosAsync(single, progress, files.Count, cancellationToken));
        if (options.SimilarVideos)
            groups.AddRange(await SimilarVideosAsync(single, progress, files.Count, cancellationToken));
        if (options.SimilarShots)
        {
            // Pictures already in a set of copies stay out of the bursts.
            var inSets = groups.SelectMany(g => g.Files.Select(f => f.Path)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            groups.AddRange(await SimilarShotsAsync(single.Where(f => !inSets.Contains(f.FullName)).ToList(), cancellationToken));
        }
        progress?.Report(new DuplicateProgress(1, files.Count, ""));
        return groups.OrderByDescending(g => g.Spare).ToList();
    }

    private static DuplicateFile File(FileInfo f) => new(f.FullName, f.Length, f.LastWriteTime);

    /// <summary>Every file under the given files and folders, skipping hidden and system ones.</summary>
    private static List<FileInfo> Collect(IReadOnlyList<string> paths, DuplicateOptions options, IProgress<DuplicateProgress>? progress, CancellationToken ct)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = new List<FileInfo>();
        var enumeration = new EnumerationOptions
        {
            RecurseSubdirectories = options.IncludeSubfolders,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint,
        };
        void Add(FileInfo file)
        {
            if (file.Length >= options.MinimumBytes && seen.Add(file.FullName))
            {
                files.Add(file);
                if (files.Count % 500 == 0)
                    progress?.Report(new DuplicateProgress(0.2 * Math.Min(1, files.Count / 50_000.0), files.Count, file.DirectoryName ?? ""));
            }
        }
        foreach (var path in paths)
        {
            ct.ThrowIfCancellationRequested();
            if (Directory.Exists(path))
            {
                foreach (var file in new DirectoryInfo(path).EnumerateFiles("*", enumeration))
                {
                    ct.ThrowIfCancellationRequested();
                    Add(file);
                }
            }
            else if (System.IO.File.Exists(path))
            {
                Add(new FileInfo(path));
            }
        }
        return files;
    }

    /// <summary>SHA-256 of the first <paramref name="limit"/> bytes, or null when the file can't be read.</summary>
    private static string? Hash(string path, long limit)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 20, FileOptions.SequentialScan);
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[1 << 20];
            long left = limit;
            int read;
            while (left > 0 && (read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, left))) > 0)
            {
                sha.AppendData(buffer, 0, read);
                left -= read;
            }
            return Convert.ToHexString(sha.GetHashAndReset());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static async Task<List<DuplicateGroup>> SimilarPhotosAsync(List<FileInfo> files, IProgress<DuplicateProgress>? progress, int total, CancellationToken ct)
    {
        var pictures = files.Where(f => Formats.FromPath(f.FullName) is { Family: FormatFamily.Image, CanRead: true }).Take(MaxPictures).ToList();
        var prints = new ImagePrint?[pictures.Count];
        int done = 0;
        await Parallel.ForEachAsync(Enumerable.Range(0, pictures.Count), new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1) }, (i, _) =>
        {
            prints[i] = ImageFingerprint.Compute(pictures[i].FullName);
            int n = Interlocked.Increment(ref done);
            if (n % 20 == 0)
                progress?.Report(new DuplicateProgress(0.5 + 0.35 * n / Math.Max(1, pictures.Count), total, pictures[i].DirectoryName ?? ""));
            return ValueTask.CompletedTask;
        });

        // Near copies: few differing bits, and the same proportions (a crop is a different picture).
        var sets = new UnionFind(pictures.Count);
        for (int a = 0; a < pictures.Count; a++)
        {
            if (prints[a] is not { } pa)
                continue;
            for (int b = a + 1; b < pictures.Count; b++)
            {
                if (prints[b] is { } pb && ImageFingerprint.Distance(pa.Hash, pb.Hash) <= 6 && Math.Abs(pa.Aspect - pb.Aspect) < 0.03 * Math.Max(pa.Aspect, pb.Aspect))
                    sets.Join(a, b);
            }
        }
        return sets.Groups()
            .Select(g => new DuplicateGroup(DuplicateKind.SimilarPhotos, g
                .Select(i => File(pictures[i]) with { Width = prints[i]!.Width, Height = prints[i]!.Height })
                .OrderByDescending(f => (long)f.Width * f.Height).ThenByDescending(f => f.Size).ToList()))
            .ToList();
    }

    private static async Task<List<DuplicateGroup>> SimilarVideosAsync(List<FileInfo> files, IProgress<DuplicateProgress>? progress, int total, CancellationToken ct)
    {
        var videos = files.Where(f => Formats.FromPath(f.FullName) is { Family: FormatFamily.Video, CanRead: true }).ToList();
        if (videos.Count < 2)
            return [];
        var prints = new VideoPrint?[videos.Count];
        int done = 0;
        await Parallel.ForEachAsync(Enumerable.Range(0, videos.Count), new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = 3 }, async (i, token) =>
        {
            prints[i] = await VideoFingerprint.ComputeAsync(videos[i].FullName, token);
            int n = Interlocked.Increment(ref done);
            progress?.Report(new DuplicateProgress(0.85 + 0.15 * n / videos.Count, total, videos[i].DirectoryName ?? ""));
        });
        var sets = new UnionFind(videos.Count);
        for (int a = 0; a < videos.Count; a++)
        {
            for (int b = a + 1; b < videos.Count; b++)
            {
                if (prints[a] is { } pa && prints[b] is { } pb && pa.LooksLike(pb))
                    sets.Join(a, b);
            }
        }
        return sets.Groups()
            .Select(g => new DuplicateGroup(DuplicateKind.SimilarVideos, g
                .Select(i => File(videos[i]) with { Width = prints[i]!.Width, Height = prints[i]!.Height })
                .OrderByDescending(f => (long)f.Width * f.Height).ThenByDescending(f => f.Size).ToList()))
            .ToList();
    }

    /// <summary>
    /// Photos taken within two minutes of each other that look alike (but are not copies of one another):
    /// the moments of a burst. Each set is ordered sharpest first.
    /// </summary>
    private static async Task<List<DuplicateGroup>> SimilarShotsAsync(List<FileInfo> files, CancellationToken ct)
    {
        var pictures = files.Where(f => Formats.FromPath(f.FullName) is { Family: FormatFamily.Image, CanRead: true }).Take(MaxPictures).ToList();
        var shots = new ShotInfo?[pictures.Count];
        await Parallel.ForEachAsync(Enumerable.Range(0, pictures.Count), new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1) }, (i, _) =>
        {
            shots[i] = ImageQuality.Measure(pictures[i].FullName);
            return ValueTask.CompletedTask;
        });
        var timeline = Enumerable.Range(0, pictures.Count).Where(i => shots[i] is not null)
            .Select(i => (Index: i, When: shots[i]!.Taken ?? pictures[i].LastWriteTime)).OrderBy(t => t.When).ToList();
        return Bursts(timeline.Select(t => (t.When, shots[t.Index]!)).ToList())
            .Select(burst => new DuplicateGroup(DuplicateKind.SimilarShots, burst
                .Select(k => File(pictures[timeline[k].Index]) with { Width = shots[timeline[k].Index]!.Width, Height = shots[timeline[k].Index]!.Height, Sharpness = shots[timeline[k].Index]!.Sharpness })
                .OrderByDescending(f => f.Sharpness).ToList()))
            .ToList();
    }

    /// <summary>
    /// Runs of shots in time order where each follows the one before within two minutes, with the same
    /// shape and a similar look. Positions in the list; sets of two or more.
    /// </summary>
    public static List<List<int>> Bursts(IReadOnlyList<(DateTime When, ShotInfo Shot)> timeline)
    {
        var bursts = new List<List<int>>();
        List<int>? current = null;
        for (int i = 0; i < timeline.Count; i++)
        {
            if (current is not null)
            {
                var before = timeline[current[^1]];
                var now = timeline[i];
                bool close = now.When - before.When <= TimeSpan.FromMinutes(2);
                bool alike = ImageFingerprint.Distance(before.Shot.Hash, now.Shot.Hash) <= 20
                    && Math.Abs(before.Shot.Aspect - now.Shot.Aspect) < 0.05 * Math.Max(before.Shot.Aspect, now.Shot.Aspect);
                if (close && alike)
                {
                    current.Add(i);
                    continue;
                }
                if (current.Count > 1)
                    bursts.Add(current);
            }
            current = [i];
        }
        if (current is { Count: > 1 })
            bursts.Add(current);
        return bursts;
    }

    /// <summary>
    /// Which file of a group to keep: for identical files the oldest (the original), for near
    /// copies the one with the most pixels, then the largest; for similar shots the sharpest.
    /// </summary>
    public static DuplicateFile Keeper(DuplicateGroup group) => group.Kind switch
    {
        DuplicateKind.Identical => group.Files.MinBy(f => f.Modified)!,
        DuplicateKind.SimilarShots => group.Files.MaxBy(f => f.Sharpness)!,
        _ => group.Files.MaxBy(f => ((long)f.Width * f.Height, f.Size))!,
    };

    private sealed class UnionFind(int count)
    {
        private readonly int[] _parent = Enumerable.Range(0, count).ToArray();

        private int Root(int i)
        {
            while (_parent[i] != i)
                i = _parent[i] = _parent[_parent[i]];
            return i;
        }

        public void Join(int a, int b) => _parent[Root(a)] = Root(b);

        /// <summary>Sets of two or more.</summary>
        public IEnumerable<List<int>> Groups() =>
            Enumerable.Range(0, _parent.Length).GroupBy(Root).Where(g => g.Count() > 1).Select(g => g.ToList());
    }
}
