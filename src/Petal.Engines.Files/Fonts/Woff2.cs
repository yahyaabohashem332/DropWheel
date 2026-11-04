using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Petal.Engines.Files.Fonts;

/// <summary>
/// WOFF2, as W3C specifies it: all tables packed together with Brotli, TrueType outlines first taken apart into separate
/// streams that pack much better. Both ways round; the transform of horizontal metrics is read but not written.
/// </summary>
internal static class Woff2
{
    private static readonly string[] KnownTags =
    [
        "cmap", "head", "hhea", "hmtx", "maxp", "name", "OS/2", "post", "cvt ", "fpgm", "glyf", "loca", "prep", "CFF ", "VORG", "EBDT",
        "EBLC", "gasp", "hdmx", "kern", "LTSH", "PCLT", "VDMX", "vhea", "vmtx", "BASE", "GDEF", "GPOS", "GSUB", "EBSC", "JSTF", "MATH",
        "CBDT", "CBLC", "sbix", "acnt", "avar", "bdat", "bloc", "bsln", "cvar", "fdsc", "feat", "fmtx", "fvar", "gvar", "hsty", "just",
        "lcar", "mort", "morx", "opbd", "prop", "trak", "Zapf", "Silf", "Glat", "Gloc", "Feat", "Sill", "EBXX", "CFF2", "HVAR",
    ];

    // ---- Numbers as WOFF2 packs them.

    private static uint ReadBase128(byte[] data, ref int at)
    {
        uint value = 0;
        for (int i = 0; i < 5; i++)
        {
            byte b = data[at++];
            if (i == 0 && b == 0x80)
                throw new InvalidDataException("This font file is damaged.");
            if ((value & 0xFE000000) != 0)
                throw new InvalidDataException("This font file is damaged.");
            value = value << 7 | (uint)(b & 0x7F);
            if ((b & 0x80) == 0)
                return value;
        }
        throw new InvalidDataException("This font file is damaged.");
    }

    private static void WriteBase128(Stream to, uint value)
    {
        int size = 1;
        for (uint v = value >> 7; v != 0; v >>= 7)
            size++;
        for (int i = size - 1; i >= 0; i--)
            to.WriteByte((byte)((value >> (i * 7)) & 0x7F | (i > 0 ? 0x80u : 0)));
    }

    private static int Read255(byte[] data, ref int at)
    {
        int code = data[at++];
        return code switch
        {
            253 => BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan((at += 2) - 2)),
            255 => data[at++] + 253,
            254 => data[at++] + 506,
            _ => code,
        };
    }

    private static void Write255(Stream to, int value)
    {
        if (value < 253)
            to.WriteByte((byte)value);
        else if (value < 506)
        {
            to.WriteByte(255);
            to.WriteByte((byte)(value - 253));
        }
        else if (value < 759)
        {
            to.WriteByte(254);
            to.WriteByte((byte)(value - 506));
        }
        else
        {
            to.WriteByte(253);
            to.WriteByte((byte)(value >> 8));
            to.WriteByte((byte)value);
        }
    }

    private static void WriteShort(Stream to, int value)
    {
        to.WriteByte((byte)(value >> 8));
        to.WriteByte((byte)value);
    }

    // ---- Writing.

    public static byte[] Write(SfntFont font)
    {
        // TrueType outlines are taken apart when that can be undone exactly; a font whose outlines can't be read goes as it is.
        byte[]? transformed = font.HasGlyfOutlines && font.Tables.ContainsKey("loca") ? TransformGlyf(font) : null;
        var directory = new MemoryStream();
        var data = new MemoryStream();
        int written = 0;
        var tags = font.Tables.Keys.Where(t => t != "loca").ToList();
        foreach (var tag in tags)
        {
            var table = font.Tables[tag];
            int known = Array.IndexOf(KnownTags, tag, 0, 55);
            int version = 0;
            bool glyf = tag == "glyf";
            if (glyf && transformed is null)
                version = 3;
            directory.WriteByte((byte)((known >= 0 ? known : 63) | version << 6));
            if (known < 0)
                directory.Write(Encoding.ASCII.GetBytes(tag));
            WriteBase128(directory, (uint)table.Length);
            if (glyf && transformed is not null)
            {
                WriteBase128(directory, (uint)transformed.Length);
                data.Write(transformed);
            }
            else
                data.Write(table);
            written++;
            if (glyf)
            {
                // "loca" follows "glyf" at once, and with its outlines taken apart it has nothing of its own.
                var loca = font.Table("loca");
                directory.WriteByte((byte)(Array.IndexOf(KnownTags, "loca") | (transformed is null ? 3 : 0) << 6));
                WriteBase128(directory, (uint)loca.Length);
                if (transformed is not null)
                    WriteBase128(directory, 0);
                else
                    data.Write(loca);
                written++;
            }
        }
        var packed = new byte[BrotliEncoder.GetMaxCompressedLength((int)data.Length)];
        if (!BrotliEncoder.TryCompress(data.ToArray(), packed, out int packedSize, 11, 24))
            throw new InvalidDataException("The font couldn't be compressed.");
        int size = 48 + (int)directory.Length + packedSize;
        var padded = (size + 3) & ~3;
        var file = new byte[padded];
        var head = file.AsSpan();
        BinaryPrimitives.WriteUInt32BigEndian(head, 0x774F4632);
        BinaryPrimitives.WriteUInt32BigEndian(head[4..], font.Flavor);
        BinaryPrimitives.WriteUInt32BigEndian(head[8..], (uint)padded);
        BinaryPrimitives.WriteUInt16BigEndian(head[12..], (ushort)written);
        BinaryPrimitives.WriteUInt32BigEndian(head[16..], (uint)font.ToSfnt().Length);
        BinaryPrimitives.WriteUInt32BigEndian(head[20..], (uint)packedSize);
        BinaryPrimitives.WriteUInt16BigEndian(head[24..], 1);
        directory.ToArray().CopyTo(file, 48);
        packed.AsSpan(0, packedSize).CopyTo(file.AsSpan(48 + (int)directory.Length));
        return file;
    }

    /// <summary>The "glyf" table taken apart into streams; null when a glyph isn't what a font should hold, so it goes as it is.</summary>
    private static byte[]? TransformGlyf(SfntFont font)
    {
        var glyf = font.Table("glyf");
        var loca = font.Table("loca");
        int glyphs = font.GlyphCount;
        bool longOffsets = BinaryPrimitives.ReadInt16BigEndian(font.Table("head").AsSpan(50)) != 0;
        if (loca.Length < (glyphs + 1) * (longOffsets ? 4 : 2))
            return null;
        int Offset(int g) => longOffsets ? (int)BinaryPrimitives.ReadUInt32BigEndian(loca.AsSpan(g * 4)) : BinaryPrimitives.ReadUInt16BigEndian(loca.AsSpan(g * 2)) * 2;
        MemoryStream contours = new(), points = new(), flags = new(), stream = new(), composites = new(), boxes = new(), instructions = new();
        var bitmap = new byte[(glyphs + 31) / 32 * 4];
        try
        {
            for (int g = 0; g < glyphs; g++)
            {
                int from = Offset(g), to = Offset(g + 1);
                if (to <= from)
                {
                    WriteShort(contours, 0);
                    continue;
                }
                var d = glyf.AsSpan(from, to - from);
                short count = BinaryPrimitives.ReadInt16BigEndian(d);
                WriteShort(contours, count);
                if (count < 0)
                {
                    // A composite: its records as they are, its box always stated, its instructions apart.
                    bitmap[g >> 3] |= (byte)(0x80 >> (g & 7));
                    boxes.Write(d.Slice(2, 8));
                    int at = 10;
                    bool more = true, have = false;
                    while (more)
                    {
                        int f = BinaryPrimitives.ReadUInt16BigEndian(d[at..]);
                        int size = 4 + ((f & 1) != 0 ? 4 : 2) + ((f & 8) != 0 ? 2 : (f & 0x40) != 0 ? 4 : (f & 0x80) != 0 ? 8 : 0);
                        composites.Write(d.Slice(at, size));
                        at += size;
                        more = (f & 0x20) != 0;
                        have |= (f & 0x100) != 0;
                    }
                    if (have)
                    {
                        int length = BinaryPrimitives.ReadUInt16BigEndian(d[at..]);
                        Write255(stream, length);
                        instructions.Write(d.Slice(at + 2, length));
                    }
                    continue;
                }
                if (count == 0)
                    continue;
                int total = 0, place = 10;
                int previous = -1;
                var ends = new int[count];
                for (int c = 0; c < count; c++, place += 2)
                {
                    ends[c] = BinaryPrimitives.ReadUInt16BigEndian(d[place..]);
                    Write255(points, ends[c] - previous);
                    previous = ends[c];
                }
                total = previous + 1;
                int instructionLength = BinaryPrimitives.ReadUInt16BigEndian(d[place..]);
                var code = d.Slice(place + 2, instructionLength);
                place += 2 + instructionLength;
                var f2 = new byte[total];
                for (int p = 0; p < total;)
                {
                    byte f = d[place++];
                    f2[p++] = f;
                    if ((f & 8) != 0)
                        for (int r = d[place++]; r > 0; r--)
                            f2[p++] = f;
                }
                var dx = new int[total];
                var dy = new int[total];
                for (int p = 0; p < total; p++)
                {
                    byte f = f2[p];
                    if ((f & 2) != 0)
                    {
                        int v = d[place++];
                        dx[p] = (f & 16) != 0 ? v : -v;
                    }
                    else if ((f & 16) == 0)
                    {
                        dx[p] = BinaryPrimitives.ReadInt16BigEndian(d[place..]);
                        place += 2;
                    }
                }
                for (int p = 0; p < total; p++)
                {
                    byte f = f2[p];
                    if ((f & 4) != 0)
                    {
                        int v = d[place++];
                        dy[p] = (f & 32) != 0 ? v : -v;
                    }
                    else if ((f & 32) == 0)
                    {
                        dy[p] = BinaryPrimitives.ReadInt16BigEndian(d[place..]);
                        place += 2;
                    }
                }
                int x = 0, y = 0, minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
                for (int p = 0; p < total; p++)
                {
                    Triplet(flags, stream, dx[p], dy[p], (f2[p] & 1) != 0);
                    x += dx[p];
                    y += dy[p];
                    (minX, maxX, minY, maxY) = (Math.Min(minX, x), Math.Max(maxX, x), Math.Min(minY, y), Math.Max(maxY, y));
                }
                Write255(stream, instructionLength);
                instructions.Write(code);
                // A box differing from what the points give has to be stated.
                short sx0 = BinaryPrimitives.ReadInt16BigEndian(d[2..]), sy0 = BinaryPrimitives.ReadInt16BigEndian(d[4..]), sx1 = BinaryPrimitives.ReadInt16BigEndian(d[6..]), sy1 = BinaryPrimitives.ReadInt16BigEndian(d[8..]);
                if (sx0 != minX || sy0 != minY || sx1 != maxX || sy1 != maxY)
                {
                    bitmap[g >> 3] |= (byte)(0x80 >> (g & 7));
                    boxes.Write(d.Slice(2, 8));
                }
            }
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentOutOfRangeException)
        {
            return null;
        }
        var result = new MemoryStream();
        WriteShort(result, 0);
        WriteShort(result, 0);
        WriteShort(result, glyphs);
        WriteShort(result, longOffsets ? 1 : 0);
        void Size(long s)
        {
            Span<byte> b = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(b, (uint)s);
            result.Write(b);
        }
        Size(contours.Length);
        Size(points.Length);
        Size(flags.Length);
        Size(stream.Length);
        Size(composites.Length);
        Size(boxes.Length + bitmap.Length);
        Size(instructions.Length);
        foreach (var part in new[] { contours, points, flags, stream, composites })
            part.WriteTo(result);
        result.Write(bitmap);
        boxes.WriteTo(result);
        instructions.WriteTo(result);
        return result.ToArray();
    }

    private static void Triplet(Stream flags, Stream data, int dx, int dy, bool onCurve)
    {
        int ax = Math.Abs(dx), ay = Math.Abs(dy);
        int xSign = dx < 0 ? 0 : 1, ySign = dy < 0 ? 0 : 1;
        int off = onCurve ? 0 : 128;
        int flag;
        if (dx == 0 && ay < 1280)
        {
            flag = (ay >> 8 << 1) + ySign;
            flags.WriteByte((byte)(flag + off));
            data.WriteByte((byte)ay);
        }
        else if (dy == 0 && ax < 1280)
        {
            flag = 10 + (ax >> 8 << 1) + xSign;
            flags.WriteByte((byte)(flag + off));
            data.WriteByte((byte)ax);
        }
        else if (ax is >= 1 and <= 64 && ay is >= 1 and <= 64)
        {
            int a = ax - 1, b = ay - 1;
            flag = 20 + (a >> 4 << 4) + (b >> 4 << 2) + xSign + 2 * ySign;
            flags.WriteByte((byte)(flag + off));
            data.WriteByte((byte)((a & 15) << 4 | (b & 15)));
        }
        else if (ax is >= 1 and <= 768 && ay is >= 1 and <= 768)
        {
            int a = ax - 1, b = ay - 1;
            flag = 84 + 12 * (a >> 8) + 4 * (b >> 8) + xSign + 2 * ySign;
            flags.WriteByte((byte)(flag + off));
            data.WriteByte((byte)a);
            data.WriteByte((byte)b);
        }
        else if (ax < 4096 && ay < 4096)
        {
            flag = 120 + xSign + 2 * ySign;
            flags.WriteByte((byte)(flag + off));
            data.WriteByte((byte)(ax >> 4));
            data.WriteByte((byte)((ax & 15) << 4 | ay >> 8));
            data.WriteByte((byte)ay);
        }
        else
        {
            flag = 124 + xSign + 2 * ySign;
            flags.WriteByte((byte)(flag + off));
            data.WriteByte((byte)(ax >> 8));
            data.WriteByte((byte)ax);
            data.WriteByte((byte)(ay >> 8));
            data.WriteByte((byte)ay);
        }
    }

    // ---- Reading.

    public static SfntFont Read(byte[] file)
    {
        uint flavor = BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(4));
        if (flavor == 0x74746366)
            throw new InvalidDataException("This is a collection of several fonts: DropWheel converts single fonts.");
        int count = BinaryPrimitives.ReadUInt16BigEndian(file.AsSpan(12));
        int packedSize = (int)BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(20));
        int at = 48;
        var entries = new List<(string Tag, int Version, int Length, int Stored)>();
        for (int i = 0; i < count; i++)
        {
            byte flags = file[at++];
            int index = flags & 63, version = flags >> 6;
            string tag;
            if (index == 63)
            {
                tag = Encoding.ASCII.GetString(file, at, 4);
                at += 4;
            }
            else
                tag = KnownTags[index];
            int length = (int)ReadBase128(file, ref at), stored = length;
            bool transformed = tag is "glyf" or "loca" ? version == 0 : version != 0;
            if (transformed)
                stored = (int)ReadBase128(file, ref at);
            entries.Add((tag, version, length, stored));
        }
        int total = entries.Sum(e => e.Stored);
        var data = new byte[total];
        using (var brotli = new BrotliStream(new MemoryStream(file, at, packedSize), CompressionMode.Decompress))
            brotli.ReadExactly(data);
        var font = new SfntFont { Flavor = flavor };
        int offset = 0;
        byte[]? xMins = null;
        var later = new List<(byte[] Data, int Length)>();
        foreach (var (tag, version, length, stored) in entries)
        {
            var part = data.AsSpan(offset, stored).ToArray();
            offset += stored;
            if (tag == "glyf" && version == 0)
            {
                var (glyf, loca, mins) = Untransform(part);
                font.Tables["glyf"] = glyf;
                font.Tables["loca"] = loca;
                xMins = mins;
            }
            else if (tag == "loca" && version == 0)
                continue;
            else if (tag == "hmtx" && version == 1)
                later.Add((part, length));
            else
                font.Tables[tag] = part;
        }
        foreach (var (part, length) in later)
            font.Tables["hmtx"] = UntransformMetrics(part, length, font, xMins ?? throw new InvalidDataException("This font file is damaged."));
        return font;
    }

    private static (byte[] Glyf, byte[] Loca, byte[] XMins) Untransform(byte[] t)
    {
        int glyphs = BinaryPrimitives.ReadUInt16BigEndian(t.AsSpan(4));
        bool longOffsets = BinaryPrimitives.ReadUInt16BigEndian(t.AsSpan(6)) != 0;
        bool overlap = (BinaryPrimitives.ReadUInt16BigEndian(t.AsSpan(2)) & 1) != 0;
        int Size(int n) => (int)BinaryPrimitives.ReadUInt32BigEndian(t.AsSpan(8 + n * 4));
        int contours = 36, points = contours + Size(0), flags = points + Size(1), stream = flags + Size(2), composite = stream + Size(3), bboxes = composite + Size(4);
        int instructions = bboxes + Size(5);
        int bitmapLength = (glyphs + 31) / 32 * 4;
        int bboxData = bboxes + bitmapLength;
        int overlapAt = instructions + Size(6);
        var output = new MemoryStream();
        var locaOut = new int[glyphs + 1];
        var xMins = new byte[glyphs * 2];
        for (int g = 0; g < glyphs; g++)
        {
            locaOut[g] = (int)output.Length;
            short count = BinaryPrimitives.ReadInt16BigEndian(t.AsSpan(contours + g * 2));
            bool hasBox = (t[bboxes + (g >> 3)] & (0x80 >> (g & 7))) != 0;
            if (count == 0)
                continue;
            if (count < 0)
            {
                // Composite: its records, its box, its instructions.
                int start = composite;
                bool more = true, have = false;
                while (more)
                {
                    int f = BinaryPrimitives.ReadUInt16BigEndian(t.AsSpan(composite));
                    composite += 4 + ((f & 1) != 0 ? 4 : 2) + ((f & 8) != 0 ? 2 : (f & 0x40) != 0 ? 4 : (f & 0x80) != 0 ? 8 : 0);
                    more = (f & 0x20) != 0;
                    have |= (f & 0x100) != 0;
                }
                WriteShort(output, -1);
                output.Write(t, bboxData, 8);
                t.AsSpan(bboxData, 2).CopyTo(xMins.AsSpan(g * 2));
                bboxData += 8;
                output.Write(t, start, composite - start);
                if (have)
                {
                    int length = Read255(t, ref stream);
                    WriteShort(output, length);
                    output.Write(t, instructions, length);
                    instructions += length;
                }
            }
            else
            {
                var ends = new int[count];
                int total = 0;
                for (int c = 0; c < count; c++)
                {
                    total += Read255(t, ref points);
                    ends[c] = total - 1;
                }
                var on = new bool[total];
                var xs = new int[total];
                var ys = new int[total];
                int x = 0, y = 0;
                for (int p = 0; p < total; p++)
                {
                    int flag = t[flags++];
                    on[p] = flag >> 7 == 0;
                    var (dx, dy) = ReadTriplet(flag & 0x7F, t, ref stream);
                    x += dx;
                    y += dy;
                    xs[p] = x;
                    ys[p] = y;
                }
                int codeLength = Read255(t, ref stream);
                int minX = total == 0 ? 0 : xs.Min(), minY = total == 0 ? 0 : ys.Min(), maxX = total == 0 ? 0 : xs.Max(), maxY = total == 0 ? 0 : ys.Max();
                if (hasBox)
                {
                    minX = BinaryPrimitives.ReadInt16BigEndian(t.AsSpan(bboxData));
                    minY = BinaryPrimitives.ReadInt16BigEndian(t.AsSpan(bboxData + 2));
                    maxX = BinaryPrimitives.ReadInt16BigEndian(t.AsSpan(bboxData + 4));
                    maxY = BinaryPrimitives.ReadInt16BigEndian(t.AsSpan(bboxData + 6));
                    bboxData += 8;
                }
                BinaryPrimitives.WriteInt16BigEndian(xMins.AsSpan(g * 2), (short)minX);
                WriteShort(output, count);
                WriteShort(output, minX);
                WriteShort(output, minY);
                WriteShort(output, maxX);
                WriteShort(output, maxY);
                foreach (int end in ends)
                    WriteShort(output, end);
                WriteShort(output, codeLength);
                output.Write(t, instructions, codeLength);
                instructions += codeLength;
                bool firstOverlap = overlap && (t[overlapAt + (g >> 3)] & (0x80 >> (g & 7))) != 0;
                GlyfTable.WritePoints(output, on, xs, ys, firstOverlap);
            }
            while (output.Length % 4 != 0)
                output.WriteByte(0);
        }
        locaOut[glyphs] = (int)output.Length;
        var loca = new byte[(glyphs + 1) * (longOffsets ? 4 : 2)];
        for (int g = 0; g <= glyphs; g++)
            if (longOffsets)
                BinaryPrimitives.WriteUInt32BigEndian(loca.AsSpan(g * 4), (uint)locaOut[g]);
            else
                BinaryPrimitives.WriteUInt16BigEndian(loca.AsSpan(g * 2), (ushort)(locaOut[g] / 2));
        return (output.ToArray(), loca, xMins);
    }

    private static (int X, int Y) ReadTriplet(int flag, byte[] d, ref int at)
    {
        static int Signed(int flag, int size) => (flag & 1) != 0 ? size : -size;
        if (flag < 10)
            return (0, Signed(flag, ((flag & 14) << 7) + d[at++]));
        if (flag < 20)
            return (Signed(flag, (((flag - 10) & 14) << 7) + d[at++]), 0);
        if (flag < 84)
        {
            int b0 = flag - 20, b1 = d[at++];
            return (Signed(flag, 1 + (b0 & 0x30) + (b1 >> 4)), Signed(flag >> 1, 1 + ((b0 & 0x0C) << 2) + (b1 & 0x0F)));
        }
        if (flag < 120)
        {
            int b0 = flag - 84, a = d[at++], b = d[at++];
            return (Signed(flag, 1 + ((b0 / 12) << 8) + a), Signed(flag >> 1, 1 + (((b0 % 12) >> 2) << 8) + b));
        }
        if (flag < 124)
        {
            int a = d[at++], b = d[at++], c = d[at++];
            return (Signed(flag, (a << 4) + (b >> 4)), Signed(flag >> 1, ((b & 0x0F) << 8) + c));
        }
        int a4 = d[at++], b4 = d[at++], c4 = d[at++], d4 = d[at++];
        return (Signed(flag, (a4 << 8) + b4), Signed(flag >> 1, (c4 << 8) + d4));
    }

    /// <summary>Horizontal metrics whose side bearings were left out because the outlines give them.</summary>
    private static byte[] UntransformMetrics(byte[] t, int length, SfntFont font, byte[] xMins)
    {
        int flags = t[0], glyphs = font.GlyphCount;
        int metrics = BinaryPrimitives.ReadUInt16BigEndian(font.Table("hhea").AsSpan(34));
        var result = new byte[length];
        int at = 1;
        bool noProportional = (flags & 1) != 0, noMono = (flags & 2) != 0;
        int lsbAt = at + metrics * 2;
        for (int g = 0; g < metrics; g++)
        {
            t.AsSpan(at + g * 2, 2).CopyTo(result.AsSpan(g * 4));
            if (noProportional)
                xMins.AsSpan(g * 2, 2).CopyTo(result.AsSpan(g * 4 + 2));
            else
            {
                t.AsSpan(lsbAt, 2).CopyTo(result.AsSpan(g * 4 + 2));
                lsbAt += 2;
            }
        }
        for (int g = metrics; g < glyphs; g++)
        {
            int to = metrics * 4 + (g - metrics) * 2;
            if (noMono)
                xMins.AsSpan(g * 2, 2).CopyTo(result.AsSpan(to));
            else
            {
                t.AsSpan(lsbAt, 2).CopyTo(result.AsSpan(to));
                lsbAt += 2;
            }
        }
        return result;
    }
}
