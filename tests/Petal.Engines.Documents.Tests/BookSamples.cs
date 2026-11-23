using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Petal.Engines.Documents.Tests;

/// <summary>
/// Kindle books and comic archives made here to their specifications, as KindleGen, Calibre, WinRAR and 7-Zip lay
/// them out: a Mobipocket book (PalmDOC compression, trailing entries, EXTH, a picture), a KF8 book (FDST, skeleton
/// and fragment indexes), the two combined, a RAR 4 and a 7z of stored pictures.
/// </summary>
internal static class BookSamples
{
    // ---- Mobipocket / KF8 ----

    /// <summary>A part of a Kindle book: its text, its pictures and its own indexes, laid out from a record number.</summary>
    private sealed class Section
    {
        public required byte[] Text { get; init; }
        public required int Version { get; init; }
        public required string Title { get; init; }
        public required string Author { get; init; }
        public List<byte[]> Pictures { get; init; } = [];
        public (List<(int Start, int Length, int Fragments)> Skeletons, List<(int Insert, int Start, int Length)> Fragments)? Kf8 { get; init; }
        public int? Boundary { get; set; }
    }

    /// <summary>A Mobipocket (KF7) book: the HTML as KindleGen leaves it, pictures named by recindex.</summary>
    public static byte[] Mobi(string html, string title, string author, params byte[][] pictures) =>
        PalmDatabase(title, [new Section { Text = Encoding.UTF8.GetBytes(html), Version = 6, Title = title, Author = author, Pictures = [.. pictures] }]);

    /// <summary>A KF8 book: each XHTML file cut into its skeleton and one fragment (the body's content), as KindleGen does.</summary>
    public static byte[] Azw3(string title, string author, IReadOnlyList<(string Skeleton, string Body)> files, params byte[][] pictures) =>
        PalmDatabase(title, [Kf8(title, author, files, pictures)]);

    /// <summary>A combined file: a KF7 book, a boundary, then a KF8 book, which readers take.</summary>
    public static byte[] Combined(string kf7Html, IReadOnlyList<(string Skeleton, string Body)> kf8Files, string title, string author, params byte[][] pictures) =>
        PalmDatabase(title,
        [
            new Section { Text = Encoding.UTF8.GetBytes(kf7Html), Version = 6, Title = title, Author = author, Pictures = [.. pictures] },
            Kf8(title, author, kf8Files, pictures),
        ]);

    private static Section Kf8(string title, string author, IReadOnlyList<(string Skeleton, string Body)> files, byte[][] pictures)
    {
        var raw = new List<byte>();
        var skeletons = new List<(int, int, int)>();
        var fragments = new List<(int, int, int)>();
        foreach (var (skeleton, body) in files)
        {
            var skel = Encoding.UTF8.GetBytes(skeleton);
            var frag = Encoding.UTF8.GetBytes(body);
            var start = raw.Count;
            var insert = start + Encoding.UTF8.GetByteCount(skeleton[..skeleton.IndexOf("</body>", StringComparison.Ordinal)]);
            raw.AddRange(skel);
            skeletons.Add((start, skel.Length, 1));
            fragments.Add((insert, raw.Count, frag.Length));
            raw.AddRange(frag);
        }
        // A style sheet after the XHTML: the second flow, which isn't text to read.
        raw.AddRange("p { margin: 0 }"u8.ToArray());
        return new Section { Text = [.. raw], Version = 8, Title = title, Author = author, Pictures = [.. pictures], Kf8 = (skeletons, fragments) };
    }

    private static byte[] PalmDatabase(string name, IReadOnlyList<Section> sections)
    {
        var records = new List<byte[]>();
        for (var s = 0; s < sections.Count; s++)
        {
            var section = sections[s];
            if (s > 0)
            {
                records.Add("BOUNDARY"u8.ToArray());
                sections[0].Boundary = records.Count;
            }
            var start = records.Count;
            records.Add([]); // record 0, written once the rest is laid out

            var text = section.Text;
            var textRecords = 0;
            for (var at = 0; at < text.Length; at += 4096)
            {
                var chunk = text.AsSpan(at, Math.Min(4096, text.Length - at)).ToArray();
                // Then the trailing entries readers must cut off: a multibyte count (none) and one index entry of 3 bytes.
                records.Add([.. PalmDocCompress(chunk), 0x00, 0xAA, 0xBB, 0x83]);
                textRecords++;
            }
            var firstPicture = records.Count;
            records.AddRange(section.Pictures);
            int fdst = -1, skeletonIndex = -1, fragmentIndex = -1;
            if (section.Kf8 is { } kf8)
            {
                var xhtmlEnd = kf8.Fragments[^1].Start + kf8.Fragments[^1].Length;
                fdst = records.Count;
                var f = new byte[28];
                "FDST"u8.CopyTo(f);
                BinaryPrimitives.WriteUInt32BigEndian(f.AsSpan(4), 12);
                BinaryPrimitives.WriteUInt32BigEndian(f.AsSpan(8), 2);
                BinaryPrimitives.WriteUInt32BigEndian(f.AsSpan(12), 0);
                BinaryPrimitives.WriteUInt32BigEndian(f.AsSpan(16), (uint)xhtmlEnd);
                BinaryPrimitives.WriteUInt32BigEndian(f.AsSpan(20), (uint)xhtmlEnd);
                BinaryPrimitives.WriteUInt32BigEndian(f.AsSpan(24), (uint)text.Length);
                records.Add(f);
                skeletonIndex = records.Count;
                records.AddRange(Index([(1, 1, 0x03), (6, 2, 0x0C)],
                    kf8.Skeletons.Select((k, i) => ($"SKEL{i:D10}", (int[][])[[k.Fragments], [k.Start, k.Length]])).ToList()));
                fragmentIndex = records.Count;
                records.AddRange(Index([(2, 1, 0x01), (3, 1, 0x02), (4, 1, 0x04), (6, 2, 0x08)],
                    kf8.Fragments.Select((g, i) => ($"{g.Insert:D10}", (int[][])[[0], [i], [i], [g.Start, g.Length]])).ToList()));
            }
            records.Add("FLIS\0\0\0\x08"u8.ToArray());
            records[start] = Record0(section, text.Length, textRecords, firstPicture - start, fdst < 0 ? -1 : fdst - start,
                skeletonIndex < 0 ? -1 : skeletonIndex - start, fragmentIndex < 0 ? -1 : fragmentIndex - start);
        }
        // The boundary is known only once the KF8 half is laid out: write record 0 again.
        if (sections.Count > 1)
            records[0] = Record0(sections[0], sections[0].Text.Length, (sections[0].Text.Length + 4095) / 4096, 1 + (sections[0].Text.Length + 4095) / 4096, -1, -1, -1);

        var header = new byte[78 + records.Count * 8 + 2];
        var nameBytes = Encoding.ASCII.GetBytes(name.Length > 31 ? name[..31] : name);
        nameBytes.CopyTo(header, 0);
        "BOOKMOBI"u8.CopyTo(header.AsSpan(60));
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(76), (ushort)records.Count);
        var offset = header.Length;
        for (var i = 0; i < records.Count; i++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(78 + i * 8), (uint)offset);
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(78 + i * 8 + 4), (uint)(2 * i));
            offset += records[i].Length;
        }
        using var output = new MemoryStream();
        output.Write(header);
        foreach (var record in records)
            output.Write(record);
        return output.ToArray();
    }

    private static byte[] Record0(Section section, int textLength, int textRecords, int firstPicture, int fdst, int skeletons, int fragments)
    {
        const int mobiLength = 264;
        var exth = new List<(int, byte[])>
        {
            (100, Encoding.UTF8.GetBytes(section.Author)),
            (503, Encoding.UTF8.GetBytes(section.Title)),
            (524, "ar"u8.ToArray()),
        };
        if (section.Boundary is { } boundary)
            exth.Add((121, BigEndian(boundary)));
        var exthBytes = new List<byte>();
        foreach (var (kind, data) in exth)
        {
            exthBytes.AddRange(BigEndian(kind));
            exthBytes.AddRange(BigEndian(data.Length + 8));
            exthBytes.AddRange(data);
        }
        var exthBlock = new List<byte>("EXTH"u8.ToArray());
        exthBlock.AddRange(BigEndian(12 + exthBytes.Count));
        exthBlock.AddRange(BigEndian(exth.Count));
        exthBlock.AddRange(exthBytes);
        while (exthBlock.Count % 4 != 0)
            exthBlock.Add(0);
        var title = Encoding.UTF8.GetBytes(section.Title);
        var r = new byte[16 + mobiLength + exthBlock.Count + title.Length + 2];
        BinaryPrimitives.WriteUInt16BigEndian(r, 2);
        BinaryPrimitives.WriteUInt32BigEndian(r.AsSpan(4), (uint)textLength);
        BinaryPrimitives.WriteUInt16BigEndian(r.AsSpan(8), (ushort)textRecords);
        BinaryPrimitives.WriteUInt16BigEndian(r.AsSpan(10), 4096);
        "MOBI"u8.CopyTo(r.AsSpan(16));
        void U32(int at, long value) => BinaryPrimitives.WriteUInt32BigEndian(r.AsSpan(at), (uint)value);
        for (var at = 40; at < 16 + mobiLength; at += 4)
            U32(at, 0xFFFFFFFF);
        U32(20, mobiLength);
        U32(24, 2);
        U32(28, 65001);
        U32(32, 0x5E7A1);
        U32(36, section.Version);
        U32(80, firstPicture);
        U32(84, 16 + mobiLength + exthBlock.Count);
        U32(88, title.Length);
        U32(92, 9);
        U32(104, section.Version);
        U32(108, firstPicture);
        U32(112, 0);
        U32(116, 0);
        U32(128, 0x50);
        U32(0xC0, fdst < 0 ? 0xFFFFFFFF : fdst);
        U32(0xC4, fdst < 0 ? 0 : 2);
        U32(0xF0, 0);
        BinaryPrimitives.WriteUInt16BigEndian(r.AsSpan(0xF2), 3);
        U32(0xF8, fragments < 0 ? 0xFFFFFFFF : fragments);
        U32(0xFC, skeletons < 0 ? 0xFFFFFFFF : skeletons);
        exthBlock.CopyTo(r, 16 + mobiLength);
        title.CopyTo(r, 16 + mobiLength + exthBlock.Count);
        return r;
    }

    private static byte[] BigEndian(int value)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(b, (uint)value);
        return b;
    }

    /// <summary>An INDX table: its header record with the TAGX, then one record of entries with its IDXT.</summary>
    private static List<byte[]> Index(IReadOnlyList<(int Tag, int PerEntry, int Mask)> tags, IReadOnlyList<(string Name, int[][] Values)> entries)
    {
        const int headerLength = 0xC0;
        var main = new byte[headerLength + 12 + 4 * (tags.Count + 1)];
        "INDX"u8.CopyTo(main);
        BinaryPrimitives.WriteUInt32BigEndian(main.AsSpan(4), headerLength);
        BinaryPrimitives.WriteUInt32BigEndian(main.AsSpan(24), 1);
        BinaryPrimitives.WriteUInt32BigEndian(main.AsSpan(28), 65001);
        BinaryPrimitives.WriteUInt32BigEndian(main.AsSpan(36), (uint)entries.Count);
        "TAGX"u8.CopyTo(main.AsSpan(headerLength));
        BinaryPrimitives.WriteUInt32BigEndian(main.AsSpan(headerLength + 4), (uint)(12 + 4 * (tags.Count + 1)));
        BinaryPrimitives.WriteUInt32BigEndian(main.AsSpan(headerLength + 8), 1);
        for (var i = 0; i < tags.Count; i++)
        {
            main[headerLength + 12 + i * 4] = (byte)tags[i].Tag;
            main[headerLength + 13 + i * 4] = (byte)tags[i].PerEntry;
            main[headerLength + 14 + i * 4] = (byte)tags[i].Mask;
        }
        main[headerLength + 12 + tags.Count * 4 + 3] = 1;

        var body = new List<byte>(new byte[headerLength]);
        var positions = new List<int>();
        foreach (var (name, values) in entries)
        {
            positions.Add(body.Count);
            var nameBytes = Encoding.ASCII.GetBytes(name);
            body.Add((byte)nameBytes.Length);
            body.AddRange(nameBytes);
            var control = 0;
            for (var t = 0; t < tags.Count; t++)
            {
                var mask = tags[t].Mask;
                var shift = System.Numerics.BitOperations.TrailingZeroCount(mask);
                control |= (1 << shift) & mask; // one set of values each
            }
            body.Add((byte)control);
            foreach (var value in values.SelectMany(v => v))
                body.AddRange(Forward(value));
        }
        var idxt = body.Count;
        body.AddRange("IDXT"u8.ToArray());
        foreach (var p in positions)
        {
            body.Add((byte)(p >> 8));
            body.Add((byte)p);
        }
        var record = body.ToArray();
        "INDX"u8.CopyTo(record);
        BinaryPrimitives.WriteUInt32BigEndian(record.AsSpan(4), headerLength);
        BinaryPrimitives.WriteUInt32BigEndian(record.AsSpan(20), (uint)idxt);
        BinaryPrimitives.WriteUInt32BigEndian(record.AsSpan(24), (uint)entries.Count);
        return [main, record];
    }

    /// <summary>A number 7 bits a byte, most significant first, the last byte marked.</summary>
    private static byte[] Forward(int value)
    {
        var bytes = new List<byte> { (byte)(0x80 | (value & 0x7F)) };
        for (value >>= 7; value > 0; value >>= 7)
            bytes.Insert(0, (byte)(value & 0x7F));
        return [.. bytes];
    }

    /// <summary>PalmDOC compression as Mobipocket's tools do it: runs copied from up to 2047 bytes back, a space folded into the next character, literals.</summary>
    internal static byte[] PalmDocCompress(byte[] data)
    {
        var output = new List<byte>();
        var i = 0;
        while (i < data.Length)
        {
            // The longest run already written, 3 to 10 bytes long.
            int best = 0, distance = 0;
            for (var back = 1; back <= Math.Min(2047, i); back++)
            {
                var length = 0;
                while (length < 10 && i + length < data.Length && data[i + length] == data[i - back + length])
                    length++;
                if (length > best)
                    (best, distance) = (length, back);
            }
            if (best >= 3)
            {
                var code = 0x8000 | (distance << 3) | (best - 3);
                output.Add((byte)(code >> 8));
                output.Add((byte)code);
                i += best;
            }
            else if (data[i] == ' ' && i + 1 < data.Length && data[i + 1] is >= 0x40 and <= 0x7F)
            {
                output.Add((byte)(data[i + 1] ^ 0x80));
                i += 2;
            }
            else if (data[i] is 0 or >= 0x09 and <= 0x7F)
                output.Add(data[i++]);
            else
            {
                // Bytes 1–8 and 0x80 and above go as literals, up to 8 at once.
                var count = 0;
                while (count < 8 && i + count < data.Length && data[i + count] is >= 0x01 and <= 0x08 or >= 0x80)
                    count++;
                output.Add((byte)count);
                for (var k = 0; k < count; k++)
                    output.Add(data[i++]);
            }
        }
        return [.. output];
    }

    // ---- Comic archives ----

    public static byte[] Zip(IReadOnlyList<(string Name, byte[] Data)> files)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, data) in files)
            {
                using var s = zip.CreateEntry(name).Open();
                s.Write(data);
            }
        }
        return output.ToArray();
    }

    /// <summary>A RAR 4 archive of stored files, as WinRAR writes with "-m0".</summary>
    public static byte[] Rar4(IReadOnlyList<(string Name, byte[] Data)> files)
    {
        var output = new List<byte>([0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00]);
        void Block(byte type, ushort flags, byte[] rest, byte[]? data = null)
        {
            var header = new List<byte> { type };
            header.AddRange(LittleEndian16(flags));
            header.AddRange(LittleEndian16((ushort)(7 + rest.Length)));
            header.AddRange(rest);
            var crc = (ushort)(Crc32.HashToUInt32([.. header]) & 0xFFFF);
            output.AddRange(LittleEndian16(crc));
            output.AddRange(header);
            if (data is not null)
                output.AddRange(data);
        }
        Block(0x73, 0, new byte[6]);
        foreach (var (name, data) in files)
        {
            var nameBytes = Encoding.ASCII.GetBytes(name.Replace('/', '\\'));
            var rest = new List<byte>();
            rest.AddRange(LittleEndian32((uint)data.Length)); // packed
            rest.AddRange(LittleEndian32((uint)data.Length)); // unpacked
            rest.Add(2); // Windows
            rest.AddRange(LittleEndian32(Crc32.HashToUInt32(data)));
            rest.AddRange(LittleEndian32(0x5B770000)); // 2025-11-23
            rest.Add(20);
            rest.Add(0x30); // stored
            rest.AddRange(LittleEndian16((ushort)nameBytes.Length));
            rest.AddRange(LittleEndian32(0x20));
            rest.AddRange(nameBytes);
            Block(0x74, 0x8000, [.. rest], data);
        }
        Block(0x7B, 0x4000, []);
        return [.. output];
    }

    /// <summary>A 7z archive of files stored with the Copy coder, each in a folder of its own.</summary>
    public static byte[] SevenZip(IReadOnlyList<(string Name, byte[] Data)> files)
    {
        var packed = files.SelectMany(f => f.Data).ToArray();
        var h = new List<byte> { 0x01, 0x04, 0x06 };
        h.AddRange(Number(0));
        h.AddRange(Number(files.Count));
        h.Add(0x09);
        foreach (var f in files)
            h.AddRange(Number(f.Data.Length));
        h.Add(0x00);
        h.Add(0x07);
        h.Add(0x0B);
        h.AddRange(Number(files.Count));
        h.Add(0x00);
        foreach (var _ in files)
        {
            h.AddRange(Number(1)); // one coder
            h.Add(0x01);           // its id is one byte, simple, no properties
            h.Add(0x00);           // Copy
        }
        h.Add(0x0C);
        foreach (var f in files)
            h.AddRange(Number(f.Data.Length));
        h.Add(0x00);
        // The substreams: one file a folder (the default), each with its CRC, as 7-Zip writes them.
        h.Add(0x08);
        h.Add(0x0A);
        h.Add(0x01);
        foreach (var f in files)
            h.AddRange(LittleEndian32(Crc32.HashToUInt32(f.Data)));
        h.Add(0x00);
        h.Add(0x00); // end of the streams
        h.Add(0x05);
        h.AddRange(Number(files.Count));
        var names = new List<byte> { 0x00 };
        foreach (var f in files)
            names.AddRange(Encoding.Unicode.GetBytes(f.Name + "\0"));
        h.Add(0x11);
        h.AddRange(Number(names.Count));
        h.AddRange(names);
        h.Add(0x00);
        h.Add(0x00);
        var header = h.ToArray();

        var start = new byte[20];
        BinaryPrimitives.WriteUInt64LittleEndian(start, (ulong)packed.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(start.AsSpan(8), (ulong)header.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(start.AsSpan(16), Crc32.HashToUInt32(header));
        var signature = new List<byte> { (byte)'7', (byte)'z', 0xBC, 0xAF, 0x27, 0x1C, 0, 4 };
        signature.AddRange(LittleEndian32(Crc32.HashToUInt32(start)));
        signature.AddRange(start);
        return [.. signature, .. packed, .. header];
    }

    /// <summary>7z's numbers: the first byte's high bits say how many little-endian bytes follow.</summary>
    private static byte[] Number(long value)
    {
        if (value < 0x80)
            return [(byte)value];
        if (value < 0x4000)
            return [(byte)(0x80 | (value >> 8)), (byte)value];
        if (value < 0x200000)
            return [(byte)(0xC0 | (value >> 16)), (byte)value, (byte)(value >> 8)];
        return [(byte)(0xE0 | (value >> 24)), (byte)value, (byte)(value >> 8), (byte)(value >> 16)];
    }

    /// <summary>CRC-32 (IEEE), as ZIP, RAR and 7z use it.</summary>
    private static class Crc32
    {
        private static readonly uint[] Table = Enumerable.Range(0, 256).Select(n =>
        {
            var c = (uint)n;
            for (var k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            return c;
        }).ToArray();

        public static uint HashToUInt32(ReadOnlySpan<byte> data)
        {
            var crc = 0xFFFFFFFF;
            foreach (var b in data)
                crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
            return crc ^ 0xFFFFFFFF;
        }
    }

    private static byte[] LittleEndian16(ushort v) => [(byte)v, (byte)(v >> 8)];

    private static byte[] LittleEndian32(uint v) => [(byte)v, (byte)(v >> 8), (byte)(v >> 16), (byte)(v >> 24)];
}
