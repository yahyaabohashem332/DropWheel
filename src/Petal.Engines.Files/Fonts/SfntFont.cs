using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Petal.Engines.Files.Fonts;

/// <summary>A font as OpenType keeps it: a kind of outline and a set of named tables.</summary>
internal sealed class SfntFont
{
    public const uint TrueType = 0x00010000;
    public const uint Cff = 0x4F54544F; // "OTTO"

    public uint Flavor { get; set; } = TrueType;

    public SortedDictionary<string, byte[]> Tables { get; } = new(StringComparer.Ordinal);

    public bool HasCffOutlines => Tables.ContainsKey("CFF ");

    public bool HasGlyfOutlines => Tables.ContainsKey("glyf");

    public byte[] Table(string tag) =>
        Tables.TryGetValue(tag, out var table) ? table : throw new InvalidDataException($"This font has no {tag.Trim()} table.");

    /// <summary>Reads a TTF, OTF, WOFF or WOFF2 file.</summary>
    public static SfntFont Read(byte[] file)
    {
        if (file.Length < 12)
            throw new InvalidDataException("This isn't a font file.");
        uint signature = BinaryPrimitives.ReadUInt32BigEndian(file);
        return signature switch
        {
            TrueType or Cff or 0x74727565 /* true */ => ReadSfnt(file),
            0x774F4646 /* wOFF */ => ReadWoff(file),
            0x774F4632 /* wOF2 */ => Woff2.Read(file),
            0x74746366 /* ttcf */ => throw new InvalidDataException("This is a collection of several fonts: DropWheel converts single fonts."),
            _ => throw new InvalidDataException("This isn't a font file."),
        };
    }

    private static SfntFont ReadSfnt(byte[] file)
    {
        var font = new SfntFont { Flavor = BinaryPrimitives.ReadUInt32BigEndian(file) };
        int count = BinaryPrimitives.ReadUInt16BigEndian(file.AsSpan(4));
        for (int i = 0; i < count; i++)
        {
            var entry = file.AsSpan(12 + i * 16, 16);
            string tag = Encoding.ASCII.GetString(entry[..4]);
            int offset = checked((int)BinaryPrimitives.ReadUInt32BigEndian(entry[8..])), length = checked((int)BinaryPrimitives.ReadUInt32BigEndian(entry[12..]));
            if (offset < 0 || length < 0 || offset + length > file.Length)
                throw new InvalidDataException("This font file is damaged.");
            font.Tables[tag] = file.AsSpan(offset, length).ToArray();
        }
        return font;
    }

    private static SfntFont ReadWoff(byte[] file)
    {
        var font = new SfntFont { Flavor = BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(4)) };
        int count = BinaryPrimitives.ReadUInt16BigEndian(file.AsSpan(12));
        for (int i = 0; i < count; i++)
        {
            var entry = file.AsSpan(44 + i * 20, 20);
            string tag = Encoding.ASCII.GetString(entry[..4]);
            int offset = checked((int)BinaryPrimitives.ReadUInt32BigEndian(entry[4..])), packed = checked((int)BinaryPrimitives.ReadUInt32BigEndian(entry[8..])), length = checked((int)BinaryPrimitives.ReadUInt32BigEndian(entry[12..]));
            if (offset + packed > file.Length)
                throw new InvalidDataException("This font file is damaged.");
            if (packed == length)
            {
                font.Tables[tag] = file.AsSpan(offset, length).ToArray();
                continue;
            }
            using var zlib = new ZLibStream(new MemoryStream(file, offset, packed), CompressionMode.Decompress);
            var table = new byte[length];
            zlib.ReadExactly(table);
            font.Tables[tag] = table;
        }
        return font;
    }

    private static uint Checksum(ReadOnlySpan<byte> data)
    {
        uint sum = 0;
        int i = 0;
        for (; i + 4 <= data.Length; i += 4)
            sum += BinaryPrimitives.ReadUInt32BigEndian(data[i..]);
        if (i < data.Length)
        {
            Span<byte> last = stackalloc byte[4];
            last.Clear();
            data[i..].CopyTo(last);
            sum += BinaryPrimitives.ReadUInt32BigEndian(last);
        }
        return sum;
    }

    /// <summary>The checksum of a table as the directory wants it: "head" counted without its adjustment.</summary>
    private static uint TableChecksum(string tag, byte[] table)
    {
        if (tag != "head" || table.Length < 12)
            return Checksum(table);
        var copy = (byte[])table.Clone();
        copy.AsSpan(8, 4).Clear();
        return Checksum(copy);
    }

    /// <summary>The font as a TTF or OTF file.</summary>
    public byte[] ToSfnt()
    {
        int count = Tables.Count;
        int size = 12 + count * 16 + Tables.Values.Sum(t => (t.Length + 3) & ~3);
        var file = new byte[size];
        BinaryPrimitives.WriteUInt32BigEndian(file, Flavor);
        BinaryPrimitives.WriteUInt16BigEndian(file.AsSpan(4), (ushort)count);
        int power = 1, log = 0;
        while (power * 2 <= count)
        {
            power *= 2;
            log++;
        }
        BinaryPrimitives.WriteUInt16BigEndian(file.AsSpan(6), (ushort)(power * 16));
        BinaryPrimitives.WriteUInt16BigEndian(file.AsSpan(8), (ushort)log);
        BinaryPrimitives.WriteUInt16BigEndian(file.AsSpan(10), (ushort)(count * 16 - power * 16));
        int entry = 12, offset = 12 + count * 16, headAt = -1;
        foreach (var (tag, table) in Tables)
        {
            Encoding.ASCII.GetBytes(tag, file.AsSpan(entry));
            BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(entry + 4), TableChecksum(tag, table));
            BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(entry + 8), (uint)offset);
            BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(entry + 12), (uint)table.Length);
            table.CopyTo(file, offset);
            if (tag == "head")
                headAt = offset;
            offset += (table.Length + 3) & ~3;
            entry += 16;
        }
        if (headAt >= 0)
        {
            file.AsSpan(headAt + 8, 4).Clear();
            BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(headAt + 8), unchecked(0xB1B0AFBA - Checksum(file)));
        }
        return file;
    }

    /// <summary>The font as a WOFF file: each table packed on its own, if that makes it smaller.</summary>
    public byte[] ToWoff()
    {
        var sfnt = ToSfnt();
        var packed = new List<(string Tag, byte[] Data, int Length, uint Checksum)>();
        foreach (var (tag, table) in Tables)
        {
            using var memory = new MemoryStream();
            using (var zlib = new ZLibStream(memory, CompressionLevel.SmallestSize, true))
                zlib.Write(table);
            packed.Add((tag, memory.Length < table.Length ? memory.ToArray() : table, table.Length, TableChecksum(tag, table)));
        }
        int size = 44 + packed.Count * 20 + packed.Sum(p => (p.Data.Length + 3) & ~3);
        var file = new byte[size];
        var head = file.AsSpan();
        BinaryPrimitives.WriteUInt32BigEndian(head, 0x774F4646);
        BinaryPrimitives.WriteUInt32BigEndian(head[4..], Flavor);
        BinaryPrimitives.WriteUInt32BigEndian(head[8..], (uint)size);
        BinaryPrimitives.WriteUInt16BigEndian(head[12..], (ushort)packed.Count);
        BinaryPrimitives.WriteUInt32BigEndian(head[16..], (uint)sfnt.Length);
        BinaryPrimitives.WriteUInt16BigEndian(head[20..], 1);
        int entry = 44, offset = 44 + packed.Count * 20;
        foreach (var (tag, data, length, checksum) in packed)
        {
            Encoding.ASCII.GetBytes(tag, file.AsSpan(entry));
            BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(entry + 4), (uint)offset);
            BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(entry + 8), (uint)data.Length);
            BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(entry + 12), (uint)length);
            BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(entry + 16), checksum);
            data.CopyTo(file, offset);
            offset += (data.Length + 3) & ~3;
            entry += 20;
        }
        return file;
    }

    public int UnitsPerEm => BinaryPrimitives.ReadUInt16BigEndian(Table("head").AsSpan(18));

    public int GlyphCount => BinaryPrimitives.ReadUInt16BigEndian(Table("maxp").AsSpan(4));

    /// <summary>A font name from the name table, in English where there is a choice.</summary>
    public string? NameOf(int id)
    {
        if (!Tables.TryGetValue("name", out var name) || name.Length < 6)
            return null;
        int count = BinaryPrimitives.ReadUInt16BigEndian(name.AsSpan(2)), strings = BinaryPrimitives.ReadUInt16BigEndian(name.AsSpan(4));
        string? best = null;
        for (int i = 0; i < count && 6 + (i + 1) * 12 <= name.Length; i++)
        {
            var record = name.AsSpan(6 + i * 12, 12);
            if (BinaryPrimitives.ReadUInt16BigEndian(record[6..]) != id)
                continue;
            int platform = BinaryPrimitives.ReadUInt16BigEndian(record), language = BinaryPrimitives.ReadUInt16BigEndian(record[4..]);
            int length = BinaryPrimitives.ReadUInt16BigEndian(record[8..]), offset = strings + BinaryPrimitives.ReadUInt16BigEndian(record[10..]);
            if (offset + length > name.Length)
                continue;
            var text = platform == 1 ? Encoding.Latin1.GetString(name, offset, length) : Encoding.BigEndianUnicode.GetString(name, offset, length);
            if (best is null || (platform == 3 && language == 0x409))
                best = text;
        }
        return best;
    }

    /// <summary>Which character each glyph shows, from the cmap's Unicode subtables.</summary>
    public Dictionary<int, int> CharacterOfGlyph()
    {
        var result = new Dictionary<int, int>();
        if (!Tables.TryGetValue("cmap", out var cmap) || cmap.Length < 4)
            return result;
        int count = BinaryPrimitives.ReadUInt16BigEndian(cmap.AsSpan(2));
        for (int i = 0; i < count && 4 + (i + 1) * 8 <= cmap.Length; i++)
        {
            int platform = BinaryPrimitives.ReadUInt16BigEndian(cmap.AsSpan(4 + i * 8)), encoding = BinaryPrimitives.ReadUInt16BigEndian(cmap.AsSpan(6 + i * 8));
            int offset = (int)BinaryPrimitives.ReadUInt32BigEndian(cmap.AsSpan(8 + i * 8));
            if (!(platform == 0 || (platform == 3 && encoding is 1 or 10)) || offset + 2 > cmap.Length)
                continue;
            int format = BinaryPrimitives.ReadUInt16BigEndian(cmap.AsSpan(offset));
            if (format == 4)
                Format4(cmap, offset, result);
            else if (format == 12)
                Format12(cmap, offset, result);
        }
        return result;
    }

    private static void Format4(byte[] cmap, int at, Dictionary<int, int> result)
    {
        int segments = BinaryPrimitives.ReadUInt16BigEndian(cmap.AsSpan(at + 6)) / 2;
        int ends = at + 14, starts = ends + segments * 2 + 2, deltas = starts + segments * 2, ranges = deltas + segments * 2;
        for (int s = 0; s < segments; s++)
        {
            int end = BinaryPrimitives.ReadUInt16BigEndian(cmap.AsSpan(ends + s * 2)), start = BinaryPrimitives.ReadUInt16BigEndian(cmap.AsSpan(starts + s * 2));
            short delta = BinaryPrimitives.ReadInt16BigEndian(cmap.AsSpan(deltas + s * 2));
            int range = BinaryPrimitives.ReadUInt16BigEndian(cmap.AsSpan(ranges + s * 2));
            for (int c = start; c <= end && c != 0xFFFF; c++)
            {
                int glyph;
                if (range == 0)
                    glyph = (c + delta) & 0xFFFF;
                else
                {
                    int place = ranges + s * 2 + range + (c - start) * 2;
                    if (place + 2 > cmap.Length)
                        continue;
                    glyph = BinaryPrimitives.ReadUInt16BigEndian(cmap.AsSpan(place));
                    if (glyph != 0)
                        glyph = (glyph + delta) & 0xFFFF;
                }
                if (glyph != 0)
                    result.TryAdd(glyph, c);
            }
        }
    }

    private static void Format12(byte[] cmap, int at, Dictionary<int, int> result)
    {
        int groups = (int)BinaryPrimitives.ReadUInt32BigEndian(cmap.AsSpan(at + 12));
        for (int g = 0; g < groups && at + 16 + (g + 1) * 12 <= cmap.Length; g++)
        {
            int start = (int)BinaryPrimitives.ReadUInt32BigEndian(cmap.AsSpan(at + 16 + g * 12)), end = (int)BinaryPrimitives.ReadUInt32BigEndian(cmap.AsSpan(at + 20 + g * 12));
            int glyph = (int)BinaryPrimitives.ReadUInt32BigEndian(cmap.AsSpan(at + 24 + g * 12));
            for (int c = start; c <= end; c++)
                result.TryAdd(glyph + c - start, c);
        }
    }
}
