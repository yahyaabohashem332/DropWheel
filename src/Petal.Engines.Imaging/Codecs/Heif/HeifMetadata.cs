using System.Buffers.Binary;
using System.Text;

namespace Petal.Engines.Imaging.Heif;

/// <summary>
/// The EXIF and XMP a HEIF file (HEIC, AVIF) carries as items of its own, read from the ISO base media boxes
/// (ISO/IEC 23008-12, 14496-12): the item information ("iinf") names each item's type, and the item locations
/// ("iloc") say where its bytes are. The picture itself is left to the decoder.
/// </summary>
internal static class HeifMetadata
{
    /// <summary>EXIF as "Exif\0\0" + TIFF, XMP as its XML bytes and the ICC profile; null where the file has none.</summary>
    public sealed record Items(byte[]? Exif, byte[]? Xmp, byte[]? Icc = null);

    public static Items Read(byte[] file)
    {
        try
        {
            return ReadCore(file);
        }
        catch (Exception e) when (e is ArgumentOutOfRangeException or IndexOutOfRangeException or OverflowException or InvalidDataException)
        {
            // A damaged or unusual container: the picture may still decode, without its metadata.
            return new Items(null, null);
        }
    }

    private static Items ReadCore(byte[] file)
    {
        var meta = Find(file, 0, file.Length, "meta");
        if (meta is not { } m)
            return new Items(null, null);
        // "meta" is a full box: version and flags come before its children.
        int start = m.Start + 4, end = m.End;
        var types = new Dictionary<uint, (string Type, string? ContentType)>();
        var locations = new Dictionary<uint, List<(long Offset, long Length)>>();
        var methods = new Dictionary<uint, int>();
        (int Start, int End)? idat = null;
        byte[]? icc = null;
        foreach (var (type, s, e) in Boxes(file, start, end))
        {
            if (type == "iinf")
                ReadItemInfo(file, s, e, types);
            else if (type == "iloc")
                ReadLocations(file, s, e, locations, methods);
            else if (type == "idat")
                idat = (s, e);
            else if (type == "iprp" && Find(file, s, e, "ipco") is { } ipco)
            {
                // A colour property ("colr") holding an ICC profile, rather than code points ("nclx").
                foreach (var (property, ps, pe) in Boxes(file, ipco.Start, ipco.End))
                {
                    if (property == "colr" && pe - ps > 4 && icc is null)
                    {
                        var kind = System.Text.Encoding.ASCII.GetString(file, ps, 4);
                        if (kind is "prof" or "rICC")
                            icc = file.AsSpan(ps + 4, pe - ps - 4).ToArray();
                    }
                }
            }
        }

        byte[]? Bytes(uint id)
        {
            if (!locations.TryGetValue(id, out var extents))
                return null;
            var method = methods.GetValueOrDefault(id);
            var output = new MemoryStream();
            foreach (var (offset, length) in extents)
            {
                long from = method == 1 && idat is { } d ? d.Start + offset : method == 0 ? offset : -1;
                var count = length == 0 ? (method == 1 && idat is { } d2 ? d2.End - from : file.Length - from) : length;
                if (from < 0 || count < 0 || from + count > file.Length)
                    return null;
                output.Write(file.AsSpan((int)from, (int)count));
            }
            return output.ToArray();
        }

        byte[]? exif = null, xmp = null;
        foreach (var (id, (type, contentType)) in types)
        {
            if (type == "Exif" && exif is null && Bytes(id) is { Length: > 8 } raw)
            {
                // The item starts with the offset, from just after it, of the TIFF header.
                var offset = (int)BinaryPrimitives.ReadUInt32BigEndian(raw);
                if (offset >= 0 && 4 + offset < raw.Length)
                    exif = [.. "Exif\0\0"u8, .. raw.AsSpan(4 + offset)];
            }
            else if (type == "mime" && xmp is null && contentType == "application/rdf+xml" && Bytes(id) is { Length: > 0 } x)
            {
                xmp = x;
            }
        }
        return new Items(exif, xmp, icc);
    }

    private static void ReadItemInfo(ReadOnlySpan<byte> file, int start, int end, Dictionary<uint, (string, string?)> types)
    {
        var version = file[start];
        int p = start + 4;
        p += version == 0 ? 2 : 4;
        foreach (var (type, s, e) in Boxes(file, p, end))
        {
            if (type != "infe")
                continue;
            var v = file[s];
            if (v < 2)
                continue;
            int q = s + 4;
            uint id = v == 2 ? BinaryPrimitives.ReadUInt16BigEndian(file[q..]) : BinaryPrimitives.ReadUInt32BigEndian(file[q..]);
            q += v == 2 ? 2 : 4;
            q += 2; // item_protection_index
            var itemType = Encoding.ASCII.GetString(file.Slice(q, 4));
            q += 4;
            var name = String(file, ref q, e);
            string? contentType = itemType == "mime" ? String(file, ref q, e) : null;
            _ = name;
            types[id] = (itemType, contentType);
        }
    }

    private static string String(ReadOnlySpan<byte> file, ref int at, int end)
    {
        int zero = at;
        while (zero < end && file[zero] != 0)
            zero++;
        var text = Encoding.UTF8.GetString(file[at..zero]);
        at = Math.Min(end, zero + 1);
        return text;
    }

    private static void ReadLocations(ReadOnlySpan<byte> file, int start, int end, Dictionary<uint, List<(long, long)>> locations, Dictionary<uint, int> methods)
    {
        var version = file[start];
        int p = start + 4;
        int offsetSize = file[p] >> 4, lengthSize = file[p] & 0xF, baseOffsetSize = file[p + 1] >> 4;
        int indexSize = version is 1 or 2 ? file[p + 1] & 0xF : 0;
        p += 2;
        uint count = version < 2 ? BinaryPrimitives.ReadUInt16BigEndian(file[p..]) : BinaryPrimitives.ReadUInt32BigEndian(file[p..]);
        p += version < 2 ? 2 : 4;
        for (uint i = 0; i < count && p < end; i++)
        {
            uint id = version < 2 ? BinaryPrimitives.ReadUInt16BigEndian(file[p..]) : BinaryPrimitives.ReadUInt32BigEndian(file[p..]);
            p += version < 2 ? 2 : 4;
            if (version is 1 or 2)
            {
                methods[id] = BinaryPrimitives.ReadUInt16BigEndian(file[p..]) & 0xF;
                p += 2;
            }
            p += 2; // data_reference_index
            long baseOffset = Number(file, ref p, baseOffsetSize);
            int extents = BinaryPrimitives.ReadUInt16BigEndian(file[p..]);
            p += 2;
            var list = new List<(long, long)>();
            for (int k = 0; k < extents; k++)
            {
                if (indexSize > 0)
                    Number(file, ref p, indexSize);
                long offset = Number(file, ref p, offsetSize);
                long length = Number(file, ref p, lengthSize);
                list.Add((baseOffset + offset, length));
            }
            locations[id] = list;
        }
    }

    private static long Number(ReadOnlySpan<byte> file, ref int at, int size)
    {
        long value = size switch
        {
            0 => 0,
            4 => BinaryPrimitives.ReadUInt32BigEndian(file[at..]),
            8 => (long)BinaryPrimitives.ReadUInt64BigEndian(file[at..]),
            _ => throw new InvalidDataException("Unusual iloc field size"),
        };
        at += size;
        return value;
    }

    private static (int Start, int End)? Find(ReadOnlySpan<byte> file, int start, int end, string wanted)
    {
        foreach (var (type, s, e) in Boxes(file, start, end))
            if (type == wanted)
                return (s, e);
        return null;
    }

    /// <summary>The boxes between two offsets: their type and where their contents start and end.</summary>
    private static List<(string Type, int Start, int End)> Boxes(ReadOnlySpan<byte> file, int start, int end)
    {
        var boxes = new List<(string, int, int)>();
        int p = start;
        while (p + 8 <= end)
        {
            long size = BinaryPrimitives.ReadUInt32BigEndian(file[p..]);
            var type = Encoding.ASCII.GetString(file.Slice(p + 4, 4));
            int header = 8;
            if (size == 1)
            {
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(file[(p + 8)..]);
                header = 16;
            }
            else if (size == 0)
            {
                size = end - p;
            }
            if (size < header || p + size > end)
                break;
            boxes.Add((type, p + header, (int)(p + size)));
            p += (int)size;
        }
        return boxes;
    }
}
