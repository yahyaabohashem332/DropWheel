using System.Buffers.Binary;
using System.Text;

namespace Petal.Engines.Imaging.Metadata;

/// <summary>A WebP RIFF container as chunks, so EXIF/XMP/ICC can change without re-encoding.</summary>
internal sealed class WebpFile
{
    private const byte IccFlag = 0x20, AlphaFlag = 0x10, ExifFlag = 0x08, XmpFlag = 0x04, AnimationFlag = 0x02;

    public sealed record Chunk(string FourCC, byte[] Data);

    public List<Chunk> Chunks { get; } = [];

    public static WebpFile Parse(byte[] data)
    {
        if (data.Length < 12 || !data.AsSpan(0, 4).SequenceEqual("RIFF"u8) || !data.AsSpan(8, 4).SequenceEqual("WEBP"u8))
            throw new InvalidDataException("Not a WebP file");
        var file = new WebpFile();
        int end = (int)Math.Min(data.Length, 8 + (long)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4)));
        int i = 12;
        while (i + 8 <= end)
        {
            var fourcc = Encoding.ASCII.GetString(data, i, 4);
            int size = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(i + 4));
            if (size < 0 || i + 8 + size > end)
                throw new InvalidDataException("Truncated WebP chunk");
            file.Chunks.Add(new Chunk(fourcc, data[(i + 8)..(i + 8 + size)]));
            i += 8 + size + (size & 1);
        }
        return file;
    }

    public Chunk? Find(string fourcc) => Chunks.FirstOrDefault(c => c.FourCC == fourcc);

    public byte[]? Exif => Find("EXIF")?.Data;

    public byte[]? Xmp => Find("XMP ")?.Data;

    public byte[]? Icc => Find("ICCP")?.Data;

    /// <summary>Replaces the metadata chunks (null removes one) and rebuilds the file with a correct VP8X header.</summary>
    public byte[] Build(byte[]? icc, byte[]? exif, byte[]? xmp, int canvasWidth, int canvasHeight)
    {
        var image = Chunks.Where(c => c.FourCC is not ("VP8X" or "ICCP" or "EXIF" or "XMP ")).ToList();
        bool extended = icc is not null || exif is not null || xmp is not null || Find("VP8X") is not null;

        var chunks = new List<Chunk>();
        if (extended)
        {
            var old = Find("VP8X")?.Data;
            byte flags = old is { Length: >= 1 } ? old[0] : (byte)0;
            flags = (byte)(flags & ~(IccFlag | ExifFlag | XmpFlag));
            if (icc is not null) flags |= IccFlag;
            if (exif is not null) flags |= ExifFlag;
            if (xmp is not null) flags |= XmpFlag;
            if (image.Any(c => c.FourCC == "ALPH") || HasLosslessAlpha(image)) flags |= AlphaFlag;
            if (image.Any(c => c.FourCC == "ANIM")) flags |= AnimationFlag;
            var vp8x = new byte[10];
            vp8x[0] = flags;
            if (old is { Length: >= 10 })
                old.AsSpan(4, 6).CopyTo(vp8x.AsSpan(4));
            else
            {
                WriteUInt24(vp8x.AsSpan(4), canvasWidth - 1);
                WriteUInt24(vp8x.AsSpan(7), canvasHeight - 1);
            }
            chunks.Add(new Chunk("VP8X", vp8x));
            if (icc is not null)
                chunks.Add(new Chunk("ICCP", icc));
        }
        chunks.AddRange(image);
        if (exif is not null)
            chunks.Add(new Chunk("EXIF", exif.AsSpan().StartsWith("Exif\0\0"u8) ? exif[6..] : exif));
        if (xmp is not null)
            chunks.Add(new Chunk("XMP ", xmp));

        using var ms = new MemoryStream();
        Span<byte> buf = stackalloc byte[4];
        ms.Write("RIFF"u8);
        ms.Write(buf); // size, patched below
        ms.Write("WEBP"u8);
        foreach (var c in chunks)
        {
            ms.Write(Encoding.ASCII.GetBytes(c.FourCC));
            BinaryPrimitives.WriteUInt32LittleEndian(buf, (uint)c.Data.Length);
            ms.Write(buf);
            ms.Write(c.Data);
            if ((c.Data.Length & 1) != 0)
                ms.WriteByte(0);
        }
        var bytes = ms.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), (uint)(bytes.Length - 8));
        return bytes;
    }

    private static bool HasLosslessAlpha(List<Chunk> chunks)
    {
        var vp8l = chunks.FirstOrDefault(c => c.FourCC == "VP8L")?.Data;
        // VP8L header: 0x2F, 14 bits width-1, 14 bits height-1, 1 bit alpha_is_used.
        return vp8l is { Length: >= 5 } && vp8l[0] == 0x2F && (vp8l[4] & 0x10) != 0;
    }

    private static void WriteUInt24(Span<byte> span, int value)
    {
        span[0] = (byte)value;
        span[1] = (byte)(value >> 8);
        span[2] = (byte)(value >> 16);
    }
}
