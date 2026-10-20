using System.Text;

namespace Petal.Engines.Imaging.Metadata;

/// <summary>
/// A JPEG split into its header segments (APPn, COM, DQT, SOF, …), the coded scan data up to
/// and including EOI, and anything appended after EOI (e.g. MPF gain maps). Lets metadata be
/// replaced without touching the compressed pixels.
/// </summary>
internal sealed class JpegFile
{
    public const int MaxSegmentPayload = 65533;

    internal static readonly byte[] ExifHeader = "Exif\0\0"u8.ToArray();
    internal static readonly byte[] XmpHeader = "http://ns.adobe.com/xap/1.0/\0"u8.ToArray();
    internal static readonly byte[] ExtendedXmpHeader = "http://ns.adobe.com/xmp/extension/\0"u8.ToArray();
    internal static readonly byte[] IccHeader = "ICC_PROFILE\0"u8.ToArray();
    internal static readonly byte[] PhotoshopHeader = "Photoshop 3.0\0"u8.ToArray();

    public sealed record Segment(byte Marker, byte[] Payload)
    {
        public bool Is(byte marker, byte[] header) => Marker == marker && Payload.AsSpan().StartsWith(header);

        public bool IsExif => Is(0xE1, ExifHeader);
        public bool IsXmp => Is(0xE1, XmpHeader);
        public bool IsExtendedXmp => Is(0xE1, ExtendedXmpHeader);
        public bool IsIcc => Is(0xE2, IccHeader);
        public bool IsPhotoshop => Is(0xED, PhotoshopHeader);
        public bool IsJfif => Is(0xE0, "JFIF\0"u8.ToArray());
        public bool IsAdobe => Is(0xEE, "Adobe"u8.ToArray());
        public bool IsComment => Marker == 0xFE;
        public bool IsApp => Marker is >= 0xE0 and <= 0xEF;
    }

    public List<Segment> Segments { get; } = [];

    /// <summary>From the first SOS marker through EOI.</summary>
    public byte[] ScanData { get; private set; } = [];

    /// <summary>Bytes after the primary image's EOI.</summary>
    public byte[] Trailer { get; set; } = [];

    public static JpegFile Parse(byte[] data)
    {
        if (data.Length < 4 || data[0] != 0xFF || data[1] != 0xD8)
            throw new InvalidDataException("Not a JPEG file");
        var file = new JpegFile();
        int i = 2;
        while (i + 1 < data.Length)
        {
            if (data[i] != 0xFF)
            {
                // Some encoders pad between segments; skip to the next marker.
                int next = Array.IndexOf(data, (byte)0xFF, i);
                if (next < 0 || next - i > 4096)
                    throw new InvalidDataException("Corrupt JPEG marker");
                i = next;
                continue;
            }
            byte marker = data[i + 1];
            if (marker == 0xFF)
            {
                i++;
                continue;
            }
            if (marker == 0xDA)
            {
                int end = FindEndOfImage(data, i);
                file.ScanData = data[i..end];
                file.Trailer = data[end..];
                return file;
            }
            if (marker is 0x01 or (>= 0xD0 and <= 0xD7))
            {
                i += 2;
                continue;
            }
            if (marker == 0xD9 || i + 4 > data.Length)
                break;
            int length = (data[i + 2] << 8) | data[i + 3];
            if (length < 2 || i + 2 + length > data.Length)
                throw new InvalidDataException("Truncated JPEG segment");
            file.Segments.Add(new Segment(marker, data[(i + 4)..(i + 2 + length)]));
            i += 2 + length;
        }
        throw new InvalidDataException("JPEG has no image data");
    }

    /// <summary>Walks entropy-coded data (and any later DHT/SOS of progressive files) to EOI.</summary>
    private static int FindEndOfImage(byte[] data, int sos)
    {
        int i = sos;
        while (i + 1 < data.Length)
        {
            if (data[i] != 0xFF)
            {
                i++;
                continue;
            }
            byte m = data[i + 1];
            if (m == 0x00 || m == 0xFF || m is >= 0xD0 and <= 0xD7)
            {
                i += m == 0xFF ? 1 : 2;
                continue;
            }
            if (m == 0xD9)
                return i + 2;
            if (i + 4 > data.Length)
                break;
            // A marker segment between scans (SOS, DHT, DQT, DRI, …): skip its header.
            int length = (data[i + 2] << 8) | data[i + 3];
            i += 2 + length;
        }
        return data.Length; // no EOI: keep everything
    }

    public byte[] Build(bool keepTrailer = true)
    {
        using var ms = new MemoryStream();
        ms.WriteByte(0xFF);
        ms.WriteByte(0xD8);
        foreach (var s in Segments)
        {
            if (s.Payload.Length > MaxSegmentPayload)
                throw new InvalidDataException("A JPEG metadata block is larger than 64 KB");
            ms.WriteByte(0xFF);
            ms.WriteByte(s.Marker);
            int length = s.Payload.Length + 2;
            ms.WriteByte((byte)(length >> 8));
            ms.WriteByte((byte)length);
            ms.Write(s.Payload);
        }
        ms.Write(ScanData);
        if (keepTrailer)
            ms.Write(Trailer);
        return ms.ToArray();
    }

    /// <summary>Removes segments matching <paramref name="match"/> and inserts <paramref name="replacement"/> where the first one was (or after APP0/APP1).</summary>
    public void Replace(Func<Segment, bool> match, IEnumerable<Segment> replacement)
    {
        int index = Segments.FindIndex(s => match(s));
        Segments.RemoveAll(s => match(s));
        if (index < 0)
            index = InsertionPoint();
        Segments.InsertRange(Math.Min(index, Segments.Count), replacement);
    }

    /// <summary>After JFIF/EXIF/XMP APP segments, before the first non-APP segment.</summary>
    private int InsertionPoint()
    {
        int i = 0;
        while (i < Segments.Count && Segments[i].IsApp)
            i++;
        return i;
    }

    public static Segment ExifSegment(byte[] exifWithHeader) => new(0xE1, exifWithHeader.AsSpan().StartsWith(ExifHeader)
        ? exifWithHeader
        : [.. ExifHeader, .. exifWithHeader]);

    public static Segment XmpSegment(byte[] xmp) => new(0xE1, [.. XmpHeader, .. xmp]);

    public static Segment CommentSegment(string text) => new(0xFE, Encoding.UTF8.GetBytes(text));

    /// <summary>IPTC goes in an APP13 Photoshop block as image resource 0x0404; other resources are kept.</summary>
    public static Segment PhotoshopSegment(Segment? existing, byte[]? iptc, bool keepOtherResources)
    {
        var resources = existing is null ? [] : ParseResources(existing.Payload.AsSpan(PhotoshopHeader.Length));
        if (!keepOtherResources)
            resources.RemoveAll(r => r.Id != 0x0404);
        resources.RemoveAll(r => r.Id == 0x0404);
        if (iptc is { Length: > 0 })
            resources.Add((0x0404, [], iptc));
        using var ms = new MemoryStream();
        ms.Write(PhotoshopHeader);
        foreach (var (id, name, data) in resources)
        {
            ms.Write("8BIM"u8);
            ms.WriteByte((byte)(id >> 8));
            ms.WriteByte((byte)id);
            ms.WriteByte((byte)name.Length);
            ms.Write(name);
            if ((name.Length + 1) % 2 != 0)
                ms.WriteByte(0);
            ms.WriteByte((byte)(data.Length >> 24));
            ms.WriteByte((byte)(data.Length >> 16));
            ms.WriteByte((byte)(data.Length >> 8));
            ms.WriteByte((byte)data.Length);
            ms.Write(data);
            if (data.Length % 2 != 0)
                ms.WriteByte(0);
        }
        return new Segment(0xED, ms.ToArray());
    }

    internal static List<(ushort Id, byte[] Name, byte[] Data)> ParseResources(ReadOnlySpan<byte> span)
    {
        var list = new List<(ushort, byte[], byte[])>();
        int i = 0;
        while (i + 12 <= span.Length && span.Slice(i, 4).SequenceEqual("8BIM"u8))
        {
            ushort id = (ushort)((span[i + 4] << 8) | span[i + 5]);
            int nameLength = span[i + 6];
            var name = span.Slice(i + 7, Math.Min(nameLength, span.Length - i - 7)).ToArray();
            int p = i + 7 + nameLength;
            if ((nameLength + 1) % 2 != 0)
                p++;
            if (p + 4 > span.Length)
                break;
            int size = (span[p] << 24) | (span[p + 1] << 16) | (span[p + 2] << 8) | span[p + 3];
            p += 4;
            if (size < 0 || p + size > span.Length)
                break;
            list.Add((id, name, span.Slice(p, size).ToArray()));
            i = p + size + (size % 2);
        }
        return list;
    }

    /// <summary>A minimal JFIF APP0 (no thumbnail) carrying the density of an existing one.</summary>
    public static Segment CleanJfif(Segment jfif)
    {
        var p = jfif.Payload;
        if (p.Length < 14)
            return jfif;
        var clean = p[..14].ToArray();
        clean[12] = 0; // thumbnail width
        clean[13] = 0; // thumbnail height
        return new Segment(0xE0, clean);
    }
}
