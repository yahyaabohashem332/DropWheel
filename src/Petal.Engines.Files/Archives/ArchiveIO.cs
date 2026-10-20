using System.Formats.Tar;
using System.IO.Compression;
using Petal.Core.Formats;
using SharpCompress.Archives;
using SharpCompress.Readers;
using F = Petal.Core.Formats.Formats;

namespace Petal.Engines.Files.Archives;

/// <summary>One entry read sequentially from an archive. <see cref="Open"/> is only valid until the next entry.</summary>
public sealed record ArchiveEntry(string Path, bool IsDirectory, DateTimeOffset? Modified, long? Size, Func<Stream> Open);

public sealed class EncryptedArchiveException() : Exception("Password-protected archives aren't supported yet.");

/// <summary>Streams entries in and out of ZIP, TAR and TAR.GZ (native .NET) and RAR / 7Z (read-only, SharpCompress).</summary>
public static class ArchiveIO
{
    /// <summary>Reads entries one by one; <paramref name="onBytesRead"/> reports how far through the archive file we are.</summary>
    public static IEnumerable<ArchiveEntry> Read(string path, FileFormat format, Action<double>? onProgress = null)
    {
        using var file = new ProgressStream(File.OpenRead(path), onProgress);
        switch (format.Id)
        {
            case "zip":
            {
                using var zip = new ZipArchive(file, ZipArchiveMode.Read);
                foreach (var e in zip.Entries)
                {
                    bool dir = e.FullName.EndsWith('/') || e.FullName.EndsWith('\\');
                    yield return new ArchiveEntry(Normalize(e.FullName), dir, e.LastWriteTime, dir ? null : e.Length, e.Open);
                }
                break;
            }
            case "tar":
            case "tgz":
            {
                Stream source = format == F.Tgz ? new GZipStream(file, CompressionMode.Decompress) : file;
                using var reader = new TarReader(source, leaveOpen: false);
                while (reader.GetNextEntry(copyData: false) is { } e)
                {
                    if (e.EntryType is TarEntryType.Directory)
                        yield return new ArchiveEntry(Normalize(e.Name), true, e.ModificationTime, null, () => Stream.Null);
                    else if (e.EntryType is TarEntryType.RegularFile or TarEntryType.V7RegularFile or TarEntryType.ContiguousFile)
                        yield return new ArchiveEntry(Normalize(e.Name), false, e.ModificationTime, e.Length, () => e.DataStream ?? Stream.Null);
                    // Links, devices and global headers are skipped.
                }
                break;
            }
            default:
            {
                using var archive = ArchiveFactory.OpenArchive(new FileInfo(path), new ReaderOptions());
                if (archive.IsEncrypted || archive.Entries.Any(e => e.IsEncrypted))
                    throw new EncryptedArchiveException();
                using var reader = archive.ExtractAllEntries();
                long total = Math.Max(1, archive.TotalUncompressedSize);
                long done = 0;
                while (reader.MoveToNextEntry())
                {
                    var e = reader.Entry;
                    var r = reader;
                    yield return new ArchiveEntry(Normalize(e.Key ?? ""), e.IsDirectory, e.LastModifiedTime, e.IsDirectory ? null : e.Size, () => r.OpenEntryStream());
                    done += e.Size;
                    onProgress?.Invoke(Math.Min(1, (double)done / total));
                }
                break;
            }
        }
    }

    /// <summary>Writes entries to a new ZIP, TAR or TAR.GZ file.</summary>
    public static void Write(string path, FileFormat format, IEnumerable<ArchiveEntry> entries, CancellationToken cancellationToken)
    {
        using var output = File.Create(path);
        switch (format.Id)
        {
            case "zip":
            {
                using var zip = new ZipArchive(output, ZipArchiveMode.Create);
                foreach (var e in entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var name = e.Path.Replace('\\', '/');
                    if (e.IsDirectory)
                    {
                        zip.CreateEntry(name.TrimEnd('/') + "/");
                        continue;
                    }
                    var entry = zip.CreateEntry(name, AlreadyCompressed(name) ? CompressionLevel.NoCompression : CompressionLevel.Optimal);
                    if (e.Modified is { } m && m.Year >= 1980)
                        entry.LastWriteTime = m;
                    using var source = e.Open();
                    using var target = entry.Open();
                    source.CopyTo(target);
                }
                break;
            }
            case "tar":
            case "tgz":
            {
                Stream sink = format == F.Tgz ? new GZipStream(output, CompressionLevel.Optimal) : output;
                using (sink)
                using (var writer = new TarWriter(sink, TarEntryFormat.Pax, leaveOpen: true))
                {
                    foreach (var e in entries)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var name = e.Path.Replace('\\', '/');
                        if (e.IsDirectory)
                        {
                            writer.WriteEntry(new PaxTarEntry(TarEntryType.Directory, name.TrimEnd('/') + "/") { ModificationTime = e.Modified ?? DateTimeOffset.Now });
                            continue;
                        }
                        using var data = Seekable(e.Open());
                        writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name)
                        {
                            DataStream = data,
                            ModificationTime = e.Modified ?? DateTimeOffset.Now,
                        });
                    }
                }
                break;
            }
            default:
                throw new NotSupportedException($"Petal can't create {format.Label} archives.");
        }
    }

    /// <summary>Archive paths always use forward slashes and never start with one.</summary>
    public static string Normalize(string path) => path.Replace('\\', '/').TrimStart('/');

    private static bool AlreadyCompressed(string name) => Path.GetExtension(name).ToLowerInvariant() is
        ".zip" or ".gz" or ".7z" or ".rar" or ".jpg" or ".jpeg" or ".png" or ".webp" or ".heic" or ".avif" or
        ".mp3" or ".m4a" or ".aac" or ".ogg" or ".opus" or ".mp4" or ".mov" or ".mkv" or ".webm";

    /// <summary>TarWriter needs to know an entry's length up front, so buffer non-seekable streams.</summary>
    private static Stream Seekable(Stream source)
    {
        if (source.CanSeek)
            return source;
        var temp = new FileStream(Path.GetTempFileName(), FileMode.Create, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.DeleteOnClose);
        using (source)
            source.CopyTo(temp);
        temp.Position = 0;
        return temp;
    }

    private sealed class ProgressStream(Stream inner, Action<double>? onProgress) : Stream
    {
        private readonly long _length = inner.CanSeek ? Math.Max(1, inner.Length) : 1;
        private long _lastReport;

        public override bool CanRead => true;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int n = inner.Read(buffer, offset, count);
            Report();
            return n;
        }

        public override int Read(Span<byte> buffer)
        {
            int n = inner.Read(buffer);
            Report();
            return n;
        }

        private void Report()
        {
            if (onProgress is null || !inner.CanSeek) return;
            var pos = inner.Position;
            if (pos - _lastReport < 256 * 1024 && pos < _length) return;
            _lastReport = pos;
            onProgress(Math.Min(1, (double)pos / _length));
        }

        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
