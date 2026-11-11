using System.Buffers.Binary;
using System.Text;

namespace Petal.Engines.Media.Foundation;

/// <summary>Colour as ITU-T H.273 numbers it (what FFmpeg's color_space, color_trc and color_primaries hold); 0 is "not said".</summary>
internal sealed record MatroskaColour(int Matrix, int Range, int Transfer, int Primaries);

internal abstract record MatroskaTrack(int Number);

/// <summary>Uncompressed video: <paramref name="Fourcc"/> names the layout of each frame's bytes, as FFmpeg's raw formats do.</summary>
internal sealed record MatroskaVideoTrack(int Number, int Width, int Height, int DisplayWidth, int DisplayHeight, uint Fourcc,
    MatroskaColour? Colour, double FrameRate) : MatroskaTrack(Number);

/// <summary>32-bit float sound, channels interleaved.</summary>
internal sealed record MatroskaAudioTrack(int Number, int SampleRate, int Channels) : MatroskaTrack(Number);

/// <summary>
/// Writes Matroska as a stream, for a reader that can't seek (a pipe): a segment of unknown size, then one cluster
/// for each block. Times are in microseconds.
/// </summary>
internal sealed class MatroskaWriter(Stream output)
{
    private const long TimestampScaleNs = 1000;

    public static uint Fourcc(string code) => (uint)(code[0] | code[1] << 8 | code[2] << 16 | code[3] << 24);

    /// <summary>FFmpeg's own tag for yuv420p10le: 'Y', '3', 11, 10.</summary>
    public static readonly uint Yuv420P10 = (uint)('Y' | '3' << 8 | 11 << 16 | 10 << 24);

    public void WriteHeader(IReadOnlyList<MatroskaTrack> tracks, double durationSeconds)
    {
        var ebml = new Ebml();
        ebml.Master(0x1A45DFA3, e =>
        {
            e.UInt(0x4286, 1);
            e.UInt(0x42F7, 1);
            e.UInt(0x42F2, 4);
            e.UInt(0x42F3, 8);
            e.String(0x4282, "matroska");
            e.UInt(0x4287, 4);
            e.UInt(0x4285, 2);
        });
        ebml.Id(0x18538067);
        ebml.UnknownSize();
        ebml.Master(0x1549A966, e =>
        {
            e.UInt(0x2AD7B1, TimestampScaleNs);
            if (durationSeconds > 0)
                e.Float(0x4489, durationSeconds * 1e9 / TimestampScaleNs);
            e.String(0x4D80, "DropWheel");
            e.String(0x5741, "DropWheel");
        });
        ebml.Master(0x1654AE6B, e =>
        {
            foreach (var track in tracks)
            {
                e.Master(0xAE, t =>
                {
                    t.UInt(0xD7, (ulong)track.Number);
                    t.UInt(0x73C5, (ulong)track.Number);
                    t.UInt(0x9C, 0);
                    switch (track)
                    {
                        case MatroskaVideoTrack v:
                            t.UInt(0x83, 1);
                            t.String(0x86, "V_UNCOMPRESSED");
                            if (v.FrameRate > 0)
                                t.UInt(0x23E383, (ulong)Math.Round(1e9 / v.FrameRate));
                            t.Master(0xE0, video =>
                            {
                                video.UInt(0xB0, (ulong)v.Width);
                                video.UInt(0xBA, (ulong)v.Height);
                                if (v.DisplayWidth != v.Width || v.DisplayHeight != v.Height)
                                {
                                    video.UInt(0x54B0, (ulong)v.DisplayWidth);
                                    video.UInt(0x54BA, (ulong)v.DisplayHeight);
                                }
                                Span<byte> fourcc = stackalloc byte[4];
                                BinaryPrimitives.WriteUInt32LittleEndian(fourcc, v.Fourcc);
                                video.Binary(0x2EB524, fourcc);
                                if (v.Colour is { } c)
                                {
                                    video.Master(0x55B0, colour =>
                                    {
                                        if (c.Matrix > 0) colour.UInt(0x55B1, (ulong)c.Matrix);
                                        if (c.Range > 0) colour.UInt(0x55B9, (ulong)c.Range);
                                        if (c.Transfer > 0) colour.UInt(0x55BA, (ulong)c.Transfer);
                                        if (c.Primaries > 0) colour.UInt(0x55BB, (ulong)c.Primaries);
                                    });
                                }
                            });
                            break;
                        case MatroskaAudioTrack a:
                            t.UInt(0x83, 2);
                            t.String(0x86, "A_PCM/FLOAT/IEEE");
                            t.Master(0xE1, audio =>
                            {
                                audio.Float(0xB5, a.SampleRate);
                                audio.UInt(0x9F, (ulong)a.Channels);
                                audio.UInt(0x6264, 32);
                            });
                            break;
                    }
                });
            }
        });
        output.Write(ebml.Bytes);
    }

    /// <summary>A cluster with no block in it, at the start: what a stream with nothing to give still needs to open.</summary>
    public void WriteEmptyCluster()
    {
        var timestamp = new Ebml();
        timestamp.UInt(0xE7, 0);
        Span<byte> head = stackalloc byte[16];
        var n = WriteId(head, 0x1F43B675);
        n += WriteSize(head[n..], timestamp.Bytes.Length);
        output.Write(head[..n]);
        output.Write(timestamp.Bytes);
    }

    /// <summary>A block in a cluster of its own, so that no size has to be known ahead and no time can overflow.</summary>
    public void WriteBlock(int track, long timeMicroseconds, ReadOnlySpan<byte> data)
    {
        Span<byte> head = stackalloc byte[48];
        var timestamp = new Ebml();
        timestamp.UInt(0xE7, (ulong)Math.Max(0, timeMicroseconds));
        // SimpleBlock: the track's number, a time relative to the cluster (0) and the flags (a key frame).
        var blockSize = 1 + 2 + 1 + data.Length;
        var blockHeaderLength = 1 + SizeLength(blockSize);
        var clusterSize = timestamp.Bytes.Length + blockHeaderLength + blockSize;

        var n = 0;
        n += WriteId(head[n..], 0x1F43B675);
        n += WriteSize(head[n..], clusterSize);
        timestamp.Bytes.CopyTo(head[n..]);
        n += timestamp.Bytes.Length;
        head[n++] = 0xA3;
        n += WriteSize(head[n..], blockSize);
        head[n++] = (byte)(0x80 | track);
        head[n++] = 0;
        head[n++] = 0;
        head[n++] = 0x80;
        output.Write(head[..n]);
        output.Write(data);
    }

    private static int SizeLength(long size)
    {
        var length = 1;
        while (length < 8 && size >= (1L << (7 * length)) - 1)
            length++;
        return length;
    }

    private static int WriteSize(Span<byte> to, long size)
    {
        var length = SizeLength(size);
        for (var i = 0; i < length; i++)
            to[i] = (byte)(size >> (8 * (length - 1 - i)));
        to[0] |= (byte)(0x80 >> (length - 1));
        return length;
    }

    private static int WriteId(Span<byte> to, uint id)
    {
        var length = id > 0xFFFFFF ? 4 : id > 0xFFFF ? 3 : id > 0xFF ? 2 : 1;
        for (var i = 0; i < length; i++)
            to[i] = (byte)(id >> (8 * (length - 1 - i)));
        return length;
    }

    /// <summary>EBML elements built in memory, for the small parts whose sizes are known.</summary>
    private sealed class Ebml
    {
        private readonly MemoryStream _bytes = new();

        public byte[] Bytes => _bytes.ToArray();

        public void Id(uint id)
        {
            Span<byte> b = stackalloc byte[4];
            _bytes.Write(b[..WriteId(b, id)]);
        }

        public void UnknownSize() => _bytes.Write([0x01, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF]);

        private void Size(long size)
        {
            Span<byte> b = stackalloc byte[8];
            _bytes.Write(b[..WriteSize(b, size)]);
        }

        public void UInt(uint id, ulong value)
        {
            var length = 1;
            while (length < 8 && value >> (8 * length) != 0)
                length++;
            Id(id);
            Size(length);
            for (var i = length - 1; i >= 0; i--)
                _bytes.WriteByte((byte)(value >> (8 * i)));
        }

        public void Float(uint id, double value)
        {
            Id(id);
            Size(8);
            Span<byte> b = stackalloc byte[8];
            BinaryPrimitives.WriteDoubleBigEndian(b, value);
            _bytes.Write(b);
        }

        public void String(uint id, string value) => Binary(id, Encoding.UTF8.GetBytes(value));

        public void Binary(uint id, ReadOnlySpan<byte> value)
        {
            Id(id);
            Size(value.Length);
            _bytes.Write(value);
        }

        public void Master(uint id, Action<Ebml> body)
        {
            var inner = new Ebml();
            body(inner);
            var bytes = inner.Bytes;
            Id(id);
            Size(bytes.Length);
            _bytes.Write(bytes);
        }
    }
}
