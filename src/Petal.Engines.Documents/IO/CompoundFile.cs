using System.Buffers.Binary;
using System.Text;

namespace Petal.Engines.Documents.IO;

/// <summary>
/// Reads the streams of a compound file ([MS-CFB]), the container of Word 97–2003 documents and other older Office
/// files: a little file system of sectors, chained by a table (the FAT), with small streams kept in a stream of
/// their own in 64-byte pieces.
/// </summary>
internal sealed class CompoundFile
{
    private static readonly byte[] Signature = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];

    private const uint EndOfChain = 0xFFFFFFFE;
    private const uint FreeSector = 0xFFFFFFFF;

    private readonly byte[] _file;
    private readonly int _sectorSize;
    private readonly uint[] _fat;
    private readonly uint[] _miniFat;
    private readonly uint _miniCutoff;
    private readonly byte[] _miniStream;
    private readonly Dictionary<string, (uint Start, long Size)> _streams = new(StringComparer.OrdinalIgnoreCase);

    private CompoundFile(byte[] file)
    {
        _file = file;
        _sectorSize = 1 << BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(30));
        if (_sectorSize is not (512 or 4096))
            throw new InvalidDataException("This is not a compound file.");
        var fatSectors = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(44));
        var directoryStart = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(48));
        _miniCutoff = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(56));
        var miniFatStart = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(60));
        var difatStart = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(68));

        // Where the FAT's own sectors are: 109 in the header, the rest in a chain of DIFAT sectors.
        var fatList = new List<uint>();
        for (var i = 0; i < 109 && fatList.Count < fatSectors; i++)
            fatList.Add(BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(76 + i * 4)));
        var perSector = _sectorSize / 4;
        for (uint difat = difatStart, guard = 0; difat < EndOfChain - 2 && fatList.Count < fatSectors && guard++ < 1 << 20;)
        {
            var sector = Sector(difat);
            for (var i = 0; i < perSector - 1 && fatList.Count < fatSectors; i++)
                fatList.Add(BinaryPrimitives.ReadUInt32LittleEndian(sector[(i * 4)..]));
            difat = BinaryPrimitives.ReadUInt32LittleEndian(sector[((perSector - 1) * 4)..]);
        }
        var fat = new List<uint>(fatList.Count * perSector);
        foreach (var at in fatList)
        {
            var sector = Sector(at);
            for (var i = 0; i < perSector; i++)
                fat.Add(BinaryPrimitives.ReadUInt32LittleEndian(sector[(i * 4)..]));
        }
        _fat = [.. fat];

        var directory = ReadChain(directoryStart, long.MaxValue);
        uint rootStart = EndOfChain;
        long rootSize = 0;
        for (var at = 0; at + 128 <= directory.Length; at += 128)
        {
            var entry = directory.AsSpan(at, 128);
            var type = entry[66];
            if (type == 0)
                continue;
            int nameBytes = BinaryPrimitives.ReadUInt16LittleEndian(entry[64..]);
            var name = Encoding.Unicode.GetString(entry[..Math.Clamp(nameBytes - 2, 0, 62)]);
            var start = BinaryPrimitives.ReadUInt32LittleEndian(entry[116..]);
            var size = _sectorSize == 512
                ? BinaryPrimitives.ReadUInt32LittleEndian(entry[120..])
                : (long)BinaryPrimitives.ReadUInt64LittleEndian(entry[120..]);
            if (type == 5)
                (rootStart, rootSize) = (start, size);
            else if (type == 2)
                _streams.TryAdd(name, (start, size));
        }
        _miniStream = rootStart < EndOfChain - 2 ? ReadChain(rootStart, rootSize) : [];
        var miniFat = miniFatStart < EndOfChain - 2 ? ReadChain(miniFatStart, long.MaxValue) : [];
        _miniFat = new uint[miniFat.Length / 4];
        for (var i = 0; i < _miniFat.Length; i++)
            _miniFat[i] = BinaryPrimitives.ReadUInt32LittleEndian(miniFat.AsSpan(i * 4));
    }

    /// <summary>Whether the bytes begin as a compound file does.</summary>
    public static bool Is(ReadOnlySpan<byte> head) => head.Length >= 8 && head[..8].SequenceEqual(Signature);

    public static CompoundFile Open(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < 512 || !Is(bytes))
            throw new InvalidDataException("This is not a compound file.");
        return new CompoundFile(bytes);
    }

    /// <summary>The names of the streams at any depth.</summary>
    public IEnumerable<string> Streams => _streams.Keys;

    /// <summary>A stream's bytes; null when there is none of that name.</summary>
    public byte[]? Read(string name)
    {
        if (!_streams.TryGetValue(name, out var stream))
            return null;
        return stream.Size < _miniCutoff ? ReadMini(stream.Start, stream.Size) : ReadChain(stream.Start, stream.Size);
    }

    private ReadOnlySpan<byte> Sector(uint index)
    {
        var at = (long)(index + 1) * _sectorSize;
        if (at + _sectorSize > _file.Length)
            throw new InvalidDataException("The file is cut short.");
        return _file.AsSpan((int)at, _sectorSize);
    }

    private byte[] ReadChain(uint start, long size)
    {
        using var output = new MemoryStream();
        var seen = new HashSet<uint>();
        for (var sector = start; sector < EndOfChain - 2 && output.Length < size; sector = sector < _fat.Length ? _fat[sector] : FreeSector)
        {
            if (!seen.Add(sector))
                throw new InvalidDataException("The file's sectors go round in a circle.");
            output.Write(Sector(sector));
        }
        var bytes = output.ToArray();
        return size < bytes.Length ? bytes[..(int)size] : bytes;
    }

    private byte[] ReadMini(uint start, long size)
    {
        const int piece = 64;
        var output = new byte[size];
        long written = 0;
        var seen = new HashSet<uint>();
        for (var sector = start; sector < EndOfChain - 2 && written < size; sector = sector < _miniFat.Length ? _miniFat[sector] : FreeSector)
        {
            if (!seen.Add(sector))
                throw new InvalidDataException("The file's sectors go round in a circle.");
            var at = (long)sector * piece;
            if (at + piece > _miniStream.Length)
                throw new InvalidDataException("The file is cut short.");
            var take = (int)Math.Min(piece, size - written);
            Array.Copy(_miniStream, at, output, written, take);
            written += take;
        }
        return output;
    }
}
