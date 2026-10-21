using System.Buffers.Binary;
using System.Text;

namespace Petal.Engines.Files.Design;

/// <summary>
/// A chunk of a RIFF/RIFX file (After Effects projects are RIFX: big-endian sizes). A LIST
/// holds child chunks; any other chunk holds bytes. Reading and writing back an unchanged
/// tree gives the same bytes, so a project can be edited without disturbing anything else.
/// </summary>
public sealed class RiffChunk
{
    public string Id { get; init; } = "";

    /// <summary>For LIST chunks (and the root): the list type ("Egg!" for an After Effects project).</summary>
    public string? ListType { get; init; }

    public byte[] Data { get; set; } = [];

    public List<RiffChunk> Children { get; } = [];

    public bool IsList => ListType is not null;

    public IEnumerable<RiffChunk> Descendants() => Children.SelectMany(c => c.Descendants().Prepend(c));
}

public static class Rifx
{
    /// <summary>Reads a RIFX (or RIFF) file into a tree; null when it isn't one.</summary>
    public static RiffChunk? Parse(byte[] data)
    {
        if (data.Length < 12)
            return null;
        var magic = Encoding.ASCII.GetString(data, 0, 4);
        if (magic is not ("RIFX" or "RIFF"))
            return null;
        bool big = magic == "RIFX";
        long size = ReadSize(data, 4, big);
        if (size < 4 || 8 + size > data.Length)
            return null;
        var root = new RiffChunk { Id = magic, ListType = Encoding.ASCII.GetString(data, 8, 4) };
        ReadChildren(data, 12, 8 + (int)size, big, root);
        return root;
    }

    private static void ReadChildren(byte[] data, int start, int end, bool big, RiffChunk parent)
    {
        int at = start;
        while (at + 8 <= end)
        {
            var id = Encoding.ASCII.GetString(data, at, 4);
            long size = ReadSize(data, at + 4, big);
            if (size < 0 || at + 8 + size > end)
                throw new InvalidDataException("A chunk runs past its container.");
            if (id == "LIST" && size >= 4)
            {
                var list = new RiffChunk { Id = id, ListType = Encoding.ASCII.GetString(data, at + 8, 4) };
                ReadChildren(data, at + 12, at + 8 + (int)size, big, list);
                parent.Children.Add(list);
            }
            else
            {
                parent.Children.Add(new RiffChunk { Id = id, Data = data.AsSpan(at + 8, (int)size).ToArray() });
            }
            at += 8 + (int)size + (int)(size % 2);
        }
        if (at != end && at != end + 1)
            throw new InvalidDataException("The chunks don't fill their container.");
    }

    private static long ReadSize(byte[] data, int at, bool big) =>
        big ? BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(at)) : BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at));

    public static byte[] Write(RiffChunk root)
    {
        bool big = root.Id == "RIFX";
        var o = new MemoryStream();
        WriteChunk(o, root, big);
        return o.ToArray();
    }

    private static void WriteChunk(Stream o, RiffChunk chunk, bool big)
    {
        o.Write(Encoding.ASCII.GetBytes(chunk.Id));
        if (chunk.IsList)
        {
            var body = new MemoryStream();
            body.Write(Encoding.ASCII.GetBytes(chunk.ListType!));
            foreach (var child in chunk.Children)
                WriteChunk(body, child, big);
            WriteSize(o, body.Length, big);
            o.Write(body.ToArray());
            if (body.Length % 2 == 1)
                o.WriteByte(0);
        }
        else
        {
            WriteSize(o, chunk.Data.Length, big);
            o.Write(chunk.Data);
            if (chunk.Data.Length % 2 == 1)
                o.WriteByte(0);
        }
    }

    private static void WriteSize(Stream o, long size, bool big)
    {
        Span<byte> b = stackalloc byte[4];
        if (big)
            BinaryPrimitives.WriteUInt32BigEndian(b, (uint)size);
        else
            BinaryPrimitives.WriteUInt32LittleEndian(b, (uint)size);
        o.Write(b);
    }
}
