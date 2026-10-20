using System.Buffers.Binary;

namespace Petal.Engines.Imaging.Metadata;

/// <summary>
/// EXIF in TIFF files. ImageMagick neither exposes a TIFF's EXIF as a profile nor writes EXIF
/// into TIFFs, so this reads the EXIF/GPS sub-IFDs (and the descriptive IFD0 tags) into a
/// standard EXIF block, and appends such a block to a finished TIFF by rewriting its first IFD.
/// </summary>
internal static class TiffExif
{
    private const ushort ExifPointer = 0x8769, GpsPointer = 0x8825, InteropPointer = 0xA005;

    /// <summary>Descriptive IFD0 tags that travel with EXIF (not the ones describing pixel layout).</summary>
    private static readonly HashSet<ushort> Ifd0MetadataTags =
        [0x010D, 0x010E, 0x010F, 0x0110, 0x0131, 0x0132, 0x013B, 0x013C, 0x8298, 0x9C9B, 0x9C9C, 0x9C9D, 0x9C9E, 0x9C9F, 0x4746, 0x4749];

    /// <summary>A TIFF directory entry with its value bytes in little-endian order.</summary>
    private sealed record Entry(ushort Tag, ushort Type, uint Count, byte[] Data);

    /// <summary>Builds "Exif\0\0" + TIFF bytes from a TIFF file's metadata, or null when it has none.</summary>
    public static byte[]? Extract(Stream tiff)
    {
        try
        {
            var reader = new Reader(tiff);
            var ifd0 = reader.ReadIfd(reader.FirstIfd, out _);
            var meta = ifd0.Where(e => Ifd0MetadataTags.Contains(e.Tag)).ToList();
            var exif = SubIfd(reader, ifd0, ExifPointer)?.Where(e => e.Tag != InteropPointer).ToList();
            var gps = SubIfd(reader, ifd0, GpsPointer);
            if (meta.Count == 0 && exif is not { Count: > 0 } && gps is not { Count: > 0 })
                return null;
            return [.. "Exif\0\0"u8, .. BuildBlob(meta, exif, gps)];
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or ArgumentException or IOException)
        {
            return null;
        }
    }

    public static byte[]? Extract(byte[] tiff)
    {
        using var ms = new MemoryStream(tiff, writable: false);
        return Extract(ms);
    }

    /// <summary>Returns <paramref name="tiff"/> with <paramref name="exifBlock"/>'s tags added to its first IFD.</summary>
    public static byte[] Inject(byte[] tiff, byte[] exifBlock)
    {
        var blob = exifBlock.AsSpan().StartsWith("Exif\0\0"u8) ? exifBlock[6..] : exifBlock;
        using var blobStream = new MemoryStream(blob, writable: false);
        var source = new Reader(blobStream);
        var sourceIfd0 = source.ReadIfd(source.FirstIfd, out _);
        var meta = sourceIfd0.Where(e => Ifd0MetadataTags.Contains(e.Tag)).ToList();
        var exif = SubIfd(source, sourceIfd0, ExifPointer)?.Where(e => e.Tag != InteropPointer).ToList();
        var gps = SubIfd(source, sourceIfd0, GpsPointer);

        using var targetStream = new MemoryStream(tiff, writable: false);
        var target = new Reader(targetStream);
        bool le = target.LittleEndian;
        uint firstIfd = target.FirstIfd;
        var raw = target.ReadRawIfd(firstIfd, out uint next);

        var replaced = new HashSet<ushort>(meta.Select(e => e.Tag)) { ExifPointer, GpsPointer };
        var kept = raw.Where(r => !replaced.Contains(r.Tag)).ToList();

        using var output = new MemoryStream();
        output.Write(tiff);
        Align(output);

        var added = new List<(ushort Tag, byte[] Record)>();
        if (exif is { Count: > 0 })
            added.Add((ExifPointer, Record(ExifPointer, 4, 1, U32((uint)WriteIfd(output, exif, 0, le), le), le)));
        if (gps is { Count: > 0 })
            added.Add((GpsPointer, Record(GpsPointer, 4, 1, U32((uint)WriteIfd(output, gps, 0, le), le), le)));
        foreach (var e in meta)
            added.Add((e.Tag, EncodeEntry(output, e, le)));

        Align(output);
        long newIfd = output.Position;
        if (newIfd > uint.MaxValue)
            throw new InvalidDataException("TIFF is too large");
        var all = kept.Select(k => (k.Tag, k.Record)).Concat(added).OrderBy(r => r.Item1).ToList();
        WriteU16(output, (ushort)all.Count, le);
        foreach (var (_, record) in all)
            output.Write(record);
        WriteU32(output, next, le);

        var bytes = output.ToArray();
        var header = bytes.AsSpan(4, 4);
        if (le) BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)newIfd);
        else BinaryPrimitives.WriteUInt32BigEndian(header, (uint)newIfd);
        return bytes;
    }

    private static List<Entry>? SubIfd(Reader reader, List<Entry> ifd, ushort pointerTag)
    {
        var pointer = ifd.FirstOrDefault(e => e.Tag == pointerTag && e.Data.Length >= 4);
        if (pointer is null)
            return null;
        uint offset = BinaryPrimitives.ReadUInt32LittleEndian(pointer.Data);
        return offset == 0 ? null : reader.ReadIfd(offset, out _);
    }

    /// <summary>A little-endian TIFF holding IFD0 (metadata + pointers), then the EXIF and GPS IFDs.</summary>
    private static byte[] BuildBlob(List<Entry> ifd0, List<Entry>? exif, List<Entry>? gps)
    {
        using var ms = new MemoryStream();
        ms.Write("II*\0"u8);
        WriteU32(ms, 8, true);

        // IFD0 gets placeholder pointers, patched once the sub-IFDs have positions.
        var entries = new List<Entry>(ifd0);
        if (exif is { Count: > 0 }) entries.Add(new Entry(ExifPointer, 4, 1, new byte[4]));
        if (gps is { Count: > 0 }) entries.Add(new Entry(GpsPointer, 4, 1, new byte[4]));
        long ifd0Start = ms.Position;
        WriteIfd(ms, entries, 0, true);

        long exifStart = exif is { Count: > 0 } ? WriteIfd(ms, exif, 0, true) : 0;
        long gpsStart = gps is { Count: > 0 } ? WriteIfd(ms, gps, 0, true) : 0;
        var bytes = ms.ToArray();

        // Patch the pointer values inside IFD0 (entries are sorted by tag).
        var sorted = entries.OrderBy(e => e.Tag).ToList();
        for (int k = 0; k < sorted.Count; k++)
        {
            long valueAt = ifd0Start + 2 + k * 12 + 8;
            if (sorted[k].Tag == ExifPointer)
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan((int)valueAt), (uint)exifStart);
            else if (sorted[k].Tag == GpsPointer)
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan((int)valueAt), (uint)gpsStart);
        }
        return bytes;
    }

    /// <summary>Writes an IFD (sorted) followed by its out-of-line values; returns where the IFD starts.</summary>
    private static long WriteIfd(MemoryStream ms, List<Entry> entries, uint next, bool le)
    {
        Align(ms);
        long start = ms.Position;
        var sorted = entries.OrderBy(e => e.Tag).ToList();
        long dataAt = start + 2 + sorted.Count * 12 + 4;
        var data = new MemoryStream();
        WriteU16(ms, (ushort)sorted.Count, le);
        foreach (var e in sorted)
        {
            var value = le ? e.Data : Swap(e.Data, e.Type);
            WriteU16(ms, e.Tag, le);
            WriteU16(ms, e.Type, le);
            WriteU32(ms, e.Count, le);
            if (value.Length <= 4)
            {
                var inline = new byte[4];
                value.CopyTo(inline, 0);
                ms.Write(inline);
            }
            else
            {
                WriteU32(ms, (uint)(dataAt + data.Length), le);
                data.Write(value);
                if ((data.Length & 1) != 0)
                    data.WriteByte(0);
            }
        }
        WriteU32(ms, next, le);
        data.Position = 0;
        data.CopyTo(ms);
        return start;
    }

    /// <summary>A 12-byte directory record for <paramref name="e"/>, writing a long value at the end of <paramref name="ms"/>.</summary>
    private static byte[] EncodeEntry(MemoryStream ms, Entry e, bool le)
    {
        var value = le ? e.Data : Swap(e.Data, e.Type);
        byte[] field;
        if (value.Length <= 4)
        {
            field = new byte[4];
            value.CopyTo(field, 0);
        }
        else
        {
            Align(ms);
            field = U32((uint)ms.Position, le);
            ms.Write(value);
        }
        return Record(e.Tag, e.Type, e.Count, field, le);
    }

    private static byte[] Record(ushort tag, ushort type, uint count, byte[] field, bool le)
    {
        var r = new byte[12];
        if (le)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(r, tag);
            BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(2), type);
            BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(4), count);
        }
        else
        {
            BinaryPrimitives.WriteUInt16BigEndian(r, tag);
            BinaryPrimitives.WriteUInt16BigEndian(r.AsSpan(2), type);
            BinaryPrimitives.WriteUInt32BigEndian(r.AsSpan(4), count);
        }
        field.CopyTo(r, 8);
        return r;
    }

    private static int TypeSize(ushort type) => type switch
    {
        1 or 2 or 6 or 7 => 1,
        3 or 8 => 2,
        4 or 9 or 11 or 13 => 4,
        5 or 10 or 12 => 8,
        _ => 0,
    };

    /// <summary>Reverses byte order per element (rationals are two 32-bit halves).</summary>
    private static byte[] Swap(byte[] data, ushort type)
    {
        int unit = type is 5 or 10 ? 4 : TypeSize(type);
        if (unit <= 1)
            return data;
        var copy = (byte[])data.Clone();
        for (int i = 0; i + unit <= copy.Length; i += unit)
            Array.Reverse(copy, i, unit);
        return copy;
    }

    private static void Align(Stream s)
    {
        if ((s.Position & 1) != 0)
            s.WriteByte(0);
    }

    private static byte[] U32(uint v, bool le)
    {
        var b = new byte[4];
        if (le) BinaryPrimitives.WriteUInt32LittleEndian(b, v);
        else BinaryPrimitives.WriteUInt32BigEndian(b, v);
        return b;
    }

    private static void WriteU16(Stream s, ushort v, bool le)
    {
        Span<byte> b = stackalloc byte[2];
        if (le) BinaryPrimitives.WriteUInt16LittleEndian(b, v);
        else BinaryPrimitives.WriteUInt16BigEndian(b, v);
        s.Write(b);
    }

    private static void WriteU32(Stream s, uint v, bool le)
    {
        Span<byte> b = stackalloc byte[4];
        if (le) BinaryPrimitives.WriteUInt32LittleEndian(b, v);
        else BinaryPrimitives.WriteUInt32BigEndian(b, v);
        s.Write(b);
    }

    private sealed class Reader
    {
        private const int MaxEntries = 4096;
        private readonly Stream _stream;

        public Reader(Stream stream)
        {
            _stream = stream;
            var header = Read(0, 8);
            LittleEndian = header[0] == 'I' && header[1] == 'I';
            if (!LittleEndian && !(header[0] == 'M' && header[1] == 'M'))
                throw new InvalidDataException("Not a TIFF structure");
            if (U16(header, 2) != 42)
                throw new InvalidDataException("Unsupported TIFF variant (BigTIFF?)");
            FirstIfd = U32(header, 4);
        }

        public bool LittleEndian { get; }

        public uint FirstIfd { get; }

        public List<Entry> ReadIfd(uint offset, out uint next)
        {
            var list = new List<Entry>();
            foreach (var (tag, record) in ReadRawIfd(offset, out next))
            {
                ushort type = U16(record, 2);
                uint count = U32(record, 4);
                int size = TypeSize(type);
                long length = (long)size * count;
                if (size == 0 || length > 16 * 1024 * 1024)
                    continue;
                var value = length <= 4 ? record.AsSpan(8, (int)length).ToArray() : Read(U32(record, 8), (int)length);
                list.Add(new Entry(tag, type, count, LittleEndian ? value : Swap(value, type)));
            }
            return list;
        }

        public List<(ushort Tag, byte[] Record)> ReadRawIfd(uint offset, out uint next)
        {
            int count = U16(Read(offset, 2), 0);
            if (count > MaxEntries)
                throw new InvalidDataException("Implausible TIFF directory");
            var block = Read(offset + 2, count * 12 + 4);
            var list = new List<(ushort, byte[])>(count);
            for (int k = 0; k < count; k++)
            {
                var record = block.AsSpan(k * 12, 12).ToArray();
                list.Add((U16(record, 0), record));
            }
            next = U32(block, count * 12);
            return list;
        }

        private byte[] Read(long offset, int length)
        {
            if (offset < 0 || offset + length > _stream.Length)
                throw new InvalidDataException("TIFF offset out of range");
            _stream.Position = offset;
            var buffer = new byte[length];
            _stream.ReadExactly(buffer);
            return buffer;
        }

        private ushort U16(byte[] b, int at) => LittleEndian ? BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(at)) : BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(at));

        private uint U32(byte[] b, int at) => LittleEndian ? BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(at)) : BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(at));
    }
}
