using System.Buffers.Binary;
using System.Text;

namespace Petal.Engines.Files.Design;

/// <summary>An installed font face.</summary>
public sealed record InstalledFont(string Family, string Style, string FullName, string PostScriptName, string File);

/// <summary>
/// The fonts installed on this PC (for everyone and for this user), read from the font files'
/// own name tables, so a document's font can be matched however it was recorded: PostScript
/// name ("Arial-BoldMT"), full name ("Arial Bold") or family and style.
/// </summary>
public static class FontIndex
{
    private static readonly Lock Gate = new();
    private static IReadOnlyList<InstalledFont>? _fonts;

    /// <summary>Folders searched; tests point this elsewhere.</summary>
    public static IReadOnlyList<string> Folders { get; set; } =
    [
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows", "Fonts"),
    ];

    public static IReadOnlyList<InstalledFont> Installed
    {
        get
        {
            lock (Gate)
                return _fonts ??= Scan();
        }
    }

    public static void Reset()
    {
        lock (Gate)
            _fonts = null;
    }

    /// <summary>The installed font that is the one a document recorded, or null when it isn't on this PC.</summary>
    public static InstalledFont? Find(DesignFont font)
    {
        var fonts = Installed;
        static string Key(string? s) => new string((s ?? "").Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        if (font.PostScriptName is { Length: > 0 } ps && fonts.FirstOrDefault(f => Key(f.PostScriptName) == Key(ps)) is { } byPs)
            return byPs;
        if (font.FileName is { Length: > 0 } file && fonts.FirstOrDefault(f => Path.GetFileName(f.File).Equals(file, StringComparison.OrdinalIgnoreCase)) is { } byFile)
            return byFile;
        if (font.Family is { Length: > 0 } family)
        {
            var style = Key(font.Style is { Length: > 0 } s ? s : "Regular");
            var match = fonts.FirstOrDefault(f => Key(f.Family) == Key(family) && Key(f.Style) == style)
                ?? fonts.FirstOrDefault(f => Key(f.FullName) == Key(family + " " + font.Style));
            if (match is not null)
                return match;
        }
        return fonts.FirstOrDefault(f => Key(f.FullName) == Key(font.Name) || Key(f.PostScriptName) == Key(font.Name));
    }

    private static List<InstalledFont> Scan()
    {
        var fonts = new List<InstalledFont>();
        foreach (var folder in Folders.Where(Directory.Exists))
        {
            foreach (var file in Directory.EnumerateFiles(folder))
            {
                var ext = Path.GetExtension(file).ToLowerInvariant();
                if (ext is not (".ttf" or ".otf" or ".ttc" or ".otc"))
                    continue;
                try
                {
                    fonts.AddRange(Read(file));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException)
                {
                    // A damaged or locked font file: leave it out.
                }
            }
        }
        return fonts;
    }

    /// <summary>The faces in a TrueType/OpenType file or collection. Only the table directory and the name table are read.</summary>
    public static IEnumerable<InstalledFont> Read(string file)
    {
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096);
        var head = ReadAt(stream, 0, 12);
        var offsets = new List<long>();
        if (head.AsSpan(0, 4).SequenceEqual("ttcf"u8))
        {
            int count = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(8)), 256);
            var table = ReadAt(stream, 12, count * 4);
            for (int i = 0; i < count; i++)
                offsets.Add(BinaryPrimitives.ReadUInt32BigEndian(table.AsSpan(i * 4)));
        }
        else
        {
            offsets.Add(0);
        }
        var faces = new List<InstalledFont>();
        foreach (long offset in offsets)
        {
            if (Face(stream, offset, file) is { } face)
                faces.Add(face);
        }
        return faces;
    }

    private static byte[] ReadAt(Stream stream, long offset, int count)
    {
        if (offset < 0 || count < 0 || offset + count > stream.Length)
            throw new InvalidDataException("The font file is shorter than its tables say.");
        stream.Position = offset;
        var buffer = new byte[count];
        stream.ReadExactly(buffer);
        return buffer;
    }

    private static InstalledFont? Face(Stream stream, long offset, string file)
    {
        var header = ReadAt(stream, offset, 12);
        int tables = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4));
        var directory = ReadAt(stream, offset + 12, tables * 16);
        for (int t = 0; t < tables; t++)
        {
            if (!directory.AsSpan(t * 16, 4).SequenceEqual("name"u8))
                continue;
            long at = BinaryPrimitives.ReadUInt32BigEndian(directory.AsSpan(t * 16 + 8));
            int length = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(directory.AsSpan(t * 16 + 12)), 1 << 20);
            return Names(ReadAt(stream, at, length), 0, file);
        }
        return null;
    }

    /// <summary>Name IDs 1/2 (family/style), 16/17 (typographic family/style), 4 (full) and 6 (PostScript); English (Windows) first.</summary>
    private static InstalledFont? Names(byte[] data, int table, string file)
    {
        if (table + 6 > data.Length)
            return null;
        int count = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(table + 2));
        int strings = table + BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(table + 4));
        var best = new Dictionary<int, (int Rank, string Text)>();
        for (int i = 0; i < count; i++)
        {
            int record = table + 6 + i * 12;
            if (record + 12 > data.Length)
                break;
            int platform = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(record));
            int language = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(record + 4));
            int nameId = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(record + 6));
            int length = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(record + 8));
            int start = strings + BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(record + 10));
            if (nameId is not (1 or 2 or 4 or 6 or 16 or 17) || start + length > data.Length)
                continue;
            string text;
            int rank;
            if (platform == 3)
            {
                text = Encoding.BigEndianUnicode.GetString(data, start, length);
                rank = language == 0x409 ? 0 : 1;
            }
            else if (platform == 1)
            {
                text = Encoding.Latin1.GetString(data, start, length);
                rank = 2;
            }
            else
            {
                continue;
            }
            if (!best.TryGetValue(nameId, out var current) || rank < current.Rank)
                best[nameId] = (rank, text.Trim('\0', ' '));
        }
        string Get(int id) => best.TryGetValue(id, out var v) ? v.Text : "";
        var family = Get(16) is { Length: > 0 } typo ? typo : Get(1);
        var style = Get(17) is { Length: > 0 } typoStyle ? typoStyle : Get(2);
        if (family.Length == 0)
            return null;
        return new InstalledFont(family, style.Length > 0 ? style : "Regular", Get(4) is { Length: > 0 } full ? full : $"{family} {style}".Trim(), Get(6), file);
    }
}
