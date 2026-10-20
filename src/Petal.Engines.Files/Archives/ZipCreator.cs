using ICSharpCode.SharpZipLib.Zip;
using Petal.Core.Jobs;

namespace Petal.Engines.Files.Archives;

public enum ZipLevel
{
    /// <summary>No compression: fastest, for files that are already compressed.</summary>
    Store,
    Fast,
    Normal,
    Best,
}

public sealed record ZipOptions
{
    public ZipLevel Level { get; init; } = ZipLevel.Normal;

    /// <summary>When set, every file is encrypted with AES-256 (opens in Windows 11, 7-Zip and WinRAR).</summary>
    public string? Password { get; init; }
}

/// <summary>Packs files and folders into one ZIP archive.</summary>
public static class ZipCreator
{
    /// <summary>
    /// Where the archive goes by default: next to the first item, named after it ("Photos.zip",
    /// "report.zip"), never over an existing file.
    /// </summary>
    public static string DefaultOutput(IReadOnlyList<string> paths)
    {
        var first = paths[0].TrimEnd('\\', '/');
        var directory = Path.GetDirectoryName(first) ?? Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        var stem = Directory.Exists(first) ? Path.GetFileName(first) : Path.GetFileNameWithoutExtension(first);
        if (string.IsNullOrWhiteSpace(stem))
            stem = "Archive";
        return OutputNaming.Unique(directory, stem, "zip");
    }

    public static Task<string> CreateAsync(IReadOnlyList<string> paths, string output, ZipOptions? options = null,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            options ??= new ZipOptions();
            var entries = Collect(paths).ToList();
            long total = Math.Max(1, entries.Where(e => e.File is not null).Sum(e => e.Length));
            long done = 0;
            var temp = output + ".petal-tmp";
            try
            {
                using (var file = File.Create(temp))
                {
                    var zip = new ZipOutputStream(file) { IsStreamOwner = false };
                    bool finished = false;
                    try
                    {
                        if (!string.IsNullOrEmpty(options.Password))
                            zip.Password = options.Password;
                        var buffer = new byte[1 << 16];
                        foreach (var e in entries)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            var entry = new ZipEntry(e.Name)
                            {
                                DateTime = e.Modified,
                                // Names in UTF-8, so Arabic and other scripts survive in every unzipper.
                                IsUnicodeText = true,
                            };
                            if (!string.IsNullOrEmpty(options.Password) && e.File is not null)
                                entry.AESKeySize = 256;
                            zip.SetLevel(e.File is not null && ArchiveIO.AlreadyCompressed(e.Name) ? 0 : LevelOf(options.Level));
                            if (e.File is null)
                            {
                                zip.PutNextEntry(entry);
                                zip.CloseEntry();
                                continue;
                            }
                            entry.Size = e.Length;
                            zip.PutNextEntry(entry);
                            using (var source = File.OpenRead(e.File))
                            {
                                int read;
                                while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
                                {
                                    cancellationToken.ThrowIfCancellationRequested();
                                    zip.Write(buffer, 0, read);
                                    done += read;
                                    progress?.Report((double)done / total);
                                }
                            }
                            zip.CloseEntry();
                        }
                        zip.Finish();
                        finished = true;
                    }
                    finally
                    {
                        // After a failure the half-written entry can't be closed cleanly; the file is deleted anyway.
                        try { zip.Dispose(); } catch when (!finished) { }
                    }
                }
                File.Move(temp, output, overwrite: true);
                progress?.Report(1);
                return output;
            }
            catch
            {
                TryDelete(temp);
                throw;
            }
        }, cancellationToken);

    private sealed record Item(string Name, string? File, long Length, DateTime Modified);

    /// <summary>Files under their own name; folders keep their name as the top folder in the archive.</summary>
    private static IEnumerable<Item> Collect(IReadOnlyList<string> paths)
    {
        foreach (var raw in paths)
        {
            var path = raw.TrimEnd('\\', '/');
            if (File.Exists(path))
            {
                var info = new FileInfo(path);
                yield return new Item(info.Name, info.FullName, info.Length, info.LastWriteTime);
                continue;
            }
            if (!Directory.Exists(path))
                continue;
            var root = new DirectoryInfo(path);
            var parent = root.Parent?.FullName ?? root.FullName;
            yield return new Item(Relative(parent, root.FullName) + "/", null, 0, root.LastWriteTime);
            foreach (var entry in root.EnumerateFileSystemInfos("*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.System, IgnoreInaccessible = true }))
            {
                if (entry is FileInfo f)
                    yield return new Item(Relative(parent, f.FullName), f.FullName, f.Length, f.LastWriteTime);
                else if (entry is DirectoryInfo d && !d.EnumerateFileSystemInfos().Any())
                    yield return new Item(Relative(parent, d.FullName) + "/", null, 0, d.LastWriteTime);
            }
        }
    }

    private static string Relative(string parent, string path) => Path.GetRelativePath(parent, path).Replace('\\', '/');

    private static int LevelOf(ZipLevel level) => level switch
    {
        ZipLevel.Store => 0,
        ZipLevel.Fast => 1,
        ZipLevel.Best => 9,
        _ => 6,
    };

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // Leave it; it's named so nothing confuses it with a real archive.
        }
    }
}
