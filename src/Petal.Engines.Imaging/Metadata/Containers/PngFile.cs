using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Petal.Engines.Imaging.Metadata;

/// <summary>A PNG as a list of chunks, so metadata chunks can be swapped without re-encoding.</summary>
internal sealed class PngFile
{
    public const string XmpKeyword = "XML:com.adobe.xmp";

    public sealed record Chunk(string Type, byte[] Data);

    public List<Chunk> Chunks { get; } = [];

    public static PngFile Parse(byte[] data)
    {
        if (data.Length < 8 || !data.AsSpan(0, 8).SequenceEqual(ImageFormats.PngSignature))
            throw new InvalidDataException("Not a PNG file");
        var file = new PngFile();
        int i = 8;
        while (i + 12 <= data.Length)
        {
            int length = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(i));
            if (length < 0 || i + 12 + length > data.Length)
                throw new InvalidDataException("Truncated PNG chunk");
            var type = Encoding.ASCII.GetString(data, i + 4, 4);
            file.Chunks.Add(new Chunk(type, data[(i + 8)..(i + 8 + length)]));
            i += 12 + length;
            if (type == "IEND")
                break;
        }
        return file;
    }

    public byte[] Build()
    {
        using var ms = new MemoryStream();
        ms.Write(ImageFormats.PngSignature);
        Span<byte> buf = stackalloc byte[4];
        foreach (var chunk in Chunks)
        {
            BinaryPrimitives.WriteUInt32BigEndian(buf, (uint)chunk.Data.Length);
            ms.Write(buf);
            var typeBytes = Encoding.ASCII.GetBytes(chunk.Type);
            ms.Write(typeBytes);
            ms.Write(chunk.Data);
            uint crc = Crc32.Compute(typeBytes, chunk.Data);
            BinaryPrimitives.WriteUInt32BigEndian(buf, crc);
            ms.Write(buf);
        }
        return ms.ToArray();
    }

    /// <summary>Inserts ancillary chunks just before the first IDAT (where eXIf, iCCP and text belong).</summary>
    public void InsertBeforeData(IEnumerable<Chunk> chunks)
    {
        int index = Chunks.FindIndex(c => c.Type == "IDAT");
        if (index < 0)
            index = Math.Max(0, Chunks.Count - 1);
        Chunks.InsertRange(index, chunks);
    }

    /// <summary>The keyword of a tEXt/zTXt/iTXt chunk.</summary>
    public static string? TextKeyword(Chunk chunk)
    {
        if (chunk.Type is not ("tEXt" or "zTXt" or "iTXt"))
            return null;
        int nul = Array.IndexOf(chunk.Data, (byte)0);
        return nul <= 0 ? null : Encoding.Latin1.GetString(chunk.Data, 0, nul);
    }

    /// <summary>Decoded text of a tEXt/zTXt/iTXt chunk.</summary>
    public static string? TextValue(Chunk chunk)
    {
        int nul = Array.IndexOf(chunk.Data, (byte)0);
        if (nul < 0)
            return null;
        try
        {
            switch (chunk.Type)
            {
                case "tEXt":
                    return Encoding.Latin1.GetString(chunk.Data, nul + 1, chunk.Data.Length - nul - 1);
                case "zTXt":
                    return Encoding.Latin1.GetString(Inflate(chunk.Data.AsSpan(nul + 2)));
                case "iTXt":
                    bool compressed = chunk.Data[nul + 1] == 1;
                    int p = nul + 3;
                    int langEnd = Array.IndexOf(chunk.Data, (byte)0, p);
                    int transEnd = Array.IndexOf(chunk.Data, (byte)0, langEnd + 1);
                    var text = chunk.Data.AsSpan(transEnd + 1);
                    return Encoding.UTF8.GetString(compressed ? Inflate(text) : text.ToArray());
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or IndexOutOfRangeException)
        {
            return null;
        }
        return null;
    }

    /// <summary>tEXt when the text is Latin-1, otherwise uncompressed iTXt (UTF-8).</summary>
    public static Chunk TextChunk(string keyword, string text)
    {
        var key = Encoding.Latin1.GetBytes(keyword);
        bool latin1 = text.All(c => c <= 0xFF);
        if (latin1 && keyword != XmpKeyword)
            return new Chunk("tEXt", [.. key, 0, .. Encoding.Latin1.GetBytes(text)]);
        return new Chunk("iTXt", [.. key, 0, 0, 0, 0, 0, .. Encoding.UTF8.GetBytes(text)]);
    }

    public static Chunk ExifChunk(byte[] exif) =>
        new("eXIf", exif.AsSpan().StartsWith("Exif\0\0"u8) ? exif[6..] : exif);

    public static Chunk IccChunk(byte[] icc, string name = "ICC Profile") =>
        new("iCCP", [.. Encoding.Latin1.GetBytes(name), 0, 0, .. Deflate(icc)]);

    private static byte[] Inflate(ReadOnlySpan<byte> data)
    {
        using var input = new MemoryStream(data.ToArray());
        using var z = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        z.CopyTo(output);
        return output.ToArray();
    }

    private static byte[] Deflate(byte[] data)
    {
        using var output = new MemoryStream();
        using (var z = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
            z.Write(data);
        return output.ToArray();
    }
}

internal static class Crc32
{
    private static readonly uint[] Table = BuildTable();

    public static uint Compute(ReadOnlySpan<byte> first, ReadOnlySpan<byte> second)
    {
        uint crc = 0xFFFFFFFF;
        foreach (var b in first)
            crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        foreach (var b in second)
            crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFF;
    }

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }
}
