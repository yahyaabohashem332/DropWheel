using System.Text;
using DiscUtils;
using DiscUtils.Iso9660;
using DiscUtils.Udf;

namespace Petal.Engines.Files.Archives;

/// <summary>
/// Disc images (.iso). Read through DiscUtils: UDF, which Windows' installation discs and DVDs use (their ISO 9660
/// side only says so), else ISO 9660 with Joliet's long names. Written as ISO 9660 with Joliet, which Windows, macOS,
/// Linux and every burning program open.
/// </summary>
internal static class DiscImage
{
    public static IEnumerable<ArchiveEntry> Read(Stream stream)
    {
        using DiscFileSystem disc = UdfReader.Detect(stream) ? new UdfReader(stream) : new CDReader(stream, joliet: true);
        foreach (var directory in disc.GetDirectories("", "*.*", SearchOption.AllDirectories))
            yield return new ArchiveEntry(ArchiveIO.Normalize(directory).TrimEnd('/') + "/", true, Time(disc, directory), null, () => Stream.Null);
        foreach (var file in disc.GetFiles("", "*.*", SearchOption.AllDirectories))
        {
            var path = file;
            yield return new ArchiveEntry(ArchiveIO.Normalize(StripVersion(path)), false, Time(disc, path), disc.GetFileLength(path),
                () => disc.OpenFile(path, FileMode.Open, FileAccess.Read));
        }
    }

    private static DateTimeOffset? Time(DiscFileSystem disc, string path)
    {
        try
        {
            var utc = disc.GetLastWriteTimeUtc(path);
            return utc.Year < 1980 ? null : new DateTimeOffset(utc, TimeSpan.Zero);
        }
        catch (Exception e) when (e is IOException or NotSupportedException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>ISO 9660 names may end in ";1", a version no other system keeps.</summary>
    private static string StripVersion(string path)
    {
        var semicolon = path.LastIndexOf(';');
        return semicolon > path.LastIndexOfAny(['\\', '/']) && path[(semicolon + 1)..].All(char.IsAsciiDigit) ? path[..semicolon] : path;
    }

    /// <summary>
    /// An ISO 9660 image with Joliet names (up to 64 letters each, any script). Its volume name is the file's, in the
    /// letters ISO 9660 allows. Every file is held until the image is built, so it is copied to a temporary file first.
    /// </summary>
    public static void Write(Stream output, IEnumerable<ArchiveEntry> entries, string name, CancellationToken ct)
    {
        var builder = new CDBuilder { UseJoliet = true, VolumeIdentifier = VolumeName(name) };
        var held = new List<Stream>();
        try
        {
            foreach (var entry in entries)
            {
                ct.ThrowIfCancellationRequested();
                var path = entry.Path.Replace('/', '\\').TrimEnd('\\');
                if (path.Length == 0)
                    continue;
                foreach (var part in path.Split('\\'))
                {
                    if (part.Length > 64)
                        throw new NotSupportedException($"A disc image's names are at most 64 letters long; \"{part}\" is longer.");
                }
                if (entry.IsDirectory)
                {
                    builder.AddDirectory(path);
                    continue;
                }
                var copy = new FileStream(Path.GetTempFileName(), FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 16, FileOptions.DeleteOnClose);
                held.Add(copy);
                using (var source = entry.Open())
                    source.CopyTo(copy);
                copy.Position = 0;
                var file = builder.AddFile(path, copy);
                if (entry.Modified is { } modified)
                    file.CreationTime = modified.UtcDateTime;
            }
            builder.Build(output);
        }
        finally
        {
            foreach (var stream in held)
                stream.Dispose();
        }
    }

    /// <summary>ISO 9660's volume name: capital letters, digits and "_", 32 at most.</summary>
    internal static string VolumeName(string name)
    {
        var sb = new StringBuilder();
        foreach (var c in name.ToUpperInvariant())
        {
            if (sb.Length == 32)
                break;
            sb.Append(c is >= 'A' and <= 'Z' or >= '0' and <= '9' ? c : '_');
        }
        var volume = sb.ToString().Trim('_');
        return volume.Length > 0 ? volume : "DROPWHEEL";
    }
}
