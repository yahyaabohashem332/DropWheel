using Petal.Core.Formats;
using Petal.Core.Jobs;

namespace Petal.Engines.Files.Archives;

public static class ArchiveExtractor
{
    /// <summary>
    /// Unpacks an archive beside itself. When everything sits in one top-level folder, that folder
    /// is extracted directly (no "project/project" nesting); otherwise a folder named after the
    /// archive is created. Returns the folder (or single file) that was created.
    /// </summary>
    public static Task<string> ExtractAsync(string archivePath, IProgress<double>? progress, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            var format = Formats.FromPath(archivePath) ?? throw new NotSupportedException("Not an archive.");
            var parent = Path.GetDirectoryName(Path.GetFullPath(archivePath))!;

            // First pass over the names decides the layout (cheap for every format we read).
            var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool anyNested = false;
            foreach (var e in ArchiveIO.Read(archivePath, format))
            {
                var parts = e.Path.Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0) continue;
                roots.Add(parts[0]);
                anyNested |= parts.Length > 1 || e.IsDirectory;
            }

            bool singleFolder = roots.Count == 1 && anyNested;
            string destination;
            string stripPrefix;
            if (singleFolder)
            {
                var root = roots.First();
                destination = OutputNaming.Unique(parent, root, null);
                stripPrefix = root + "/";
            }
            else
            {
                destination = OutputNaming.Unique(parent, Formats.BaseName(archivePath), null);
                stripPrefix = "";
            }

            var staging = destination + ".petal-tmp";
            Directory.CreateDirectory(staging);
            var stagingFull = Path.GetFullPath(staging) + Path.DirectorySeparatorChar;
            try
            {
                foreach (var e in ArchiveIO.Read(archivePath, format, f => progress?.Report(f * 0.99)))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var relative = e.Path;
                    if (stripPrefix.Length > 0 && relative.StartsWith(stripPrefix, StringComparison.OrdinalIgnoreCase))
                        relative = relative[stripPrefix.Length..];
                    else if (stripPrefix.Length > 0 && relative.TrimEnd('/').Equals(stripPrefix.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (relative.Length == 0)
                        continue;

                    var target = Path.GetFullPath(Path.Combine(staging, SafeRelative(relative)));
                    // Zip-slip: never write outside the destination.
                    if (!target.StartsWith(stagingFull, StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (e.IsDirectory)
                    {
                        Directory.CreateDirectory(target);
                        continue;
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    using (var source = e.Open())
                    using (var file = File.Create(target))
                        source.CopyTo(file);
                    if (e.Modified is { } modified)
                    {
                        try { File.SetLastWriteTime(target, modified.LocalDateTime); } catch { /* cosmetic */ }
                    }
                }
                Directory.Move(staging, destination);
                progress?.Report(1);
                return destination;
            }
            catch
            {
                try { Directory.Delete(staging, recursive: true); } catch { /* best effort */ }
                throw;
            }
        }, cancellationToken);

    /// <summary>Removes characters Windows rejects in names (":" "*" "?" etc.) from each path segment.</summary>
    private static string SafeRelative(string path)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Where(p => p != "." && p != "..")
            .Select(p => new string(p.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).TrimEnd(' ', '.'))
            .Where(p => p.Length > 0);
        return Path.Combine(parts.ToArray());
    }
}
