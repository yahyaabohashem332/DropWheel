using System.Buffers.Binary;

namespace Petal.Engines.Imaging.Heif;

/// <summary>Splits an Annex-B HEVC stream into parameter sets and per-picture slice data.</summary>
internal sealed class HevcBitstream
{
    private const int VpsType = 32, SpsType = 33, PpsType = 34;

    public byte[] Vps { get; private set; } = [];

    public byte[] Sps { get; private set; } = [];

    public byte[] Pps { get; private set; } = [];

    /// <summary>Each coded picture as HEIF item data: 4-byte big-endian length + NAL unit, repeated.</summary>
    public List<byte[]> Pictures { get; } = [];

    public static HevcBitstream Parse(ReadOnlySpan<byte> annexB)
    {
        var result = new HevcBitstream();
        var current = new MemoryStream();
        Span<byte> len = stackalloc byte[4];
        foreach (var (start, length) in SplitNalUnits(annexB))
        {
            var nal = annexB.Slice(start, length);
            if (nal.Length < 3)
                continue;
            int type = (nal[0] >> 1) & 0x3F;
            switch (type)
            {
                case VpsType when result.Vps.Length == 0: result.Vps = nal.ToArray(); break;
                case SpsType when result.Sps.Length == 0: result.Sps = nal.ToArray(); break;
                case PpsType when result.Pps.Length == 0: result.Pps = nal.ToArray(); break;
                case < 32:
                    // first_slice_segment_in_pic_flag starts a new picture.
                    bool firstSlice = (nal[2] & 0x80) != 0;
                    if (firstSlice && current.Length > 0)
                    {
                        result.Pictures.Add(current.ToArray());
                        current.SetLength(0);
                    }
                    BinaryPrimitives.WriteUInt32BigEndian(len, (uint)nal.Length);
                    current.Write(len);
                    current.Write(nal);
                    break;
            }
        }
        if (current.Length > 0)
            result.Pictures.Add(current.ToArray());
        if (result.Vps.Length == 0 || result.Sps.Length == 0 || result.Pps.Length == 0)
            throw new InvalidDataException("HEVC stream has no parameter sets");
        return result;
    }

    internal static List<(int Start, int Length)> SplitNalUnits(ReadOnlySpan<byte> data)
    {
        var units = new List<(int, int)>();
        int i = 0, nalStart = -1;
        while (i + 2 < data.Length)
        {
            if (data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 1)
            {
                if (nalStart >= 0)
                    units.Add((nalStart, TrimTrailingZeros(data, nalStart, i) - nalStart));
                i += 3;
                nalStart = i;
                continue;
            }
            i++;
        }
        if (nalStart >= 0 && nalStart < data.Length)
            units.Add((nalStart, TrimTrailingZeros(data, nalStart, data.Length) - nalStart));
        return units;
    }

    private static int TrimTrailingZeros(ReadOnlySpan<byte> data, int start, int end)
    {
        while (end > start && data[end - 1] == 0)
            end--;
        return end;
    }

    /// <summary>HEVCDecoderConfigurationRecord (ISO/IEC 14496-15 §8.3.3) for the hvcC property.</summary>
    public byte[] BuildHvcC()
    {
        var sps = new SpsInfo(Sps);
        using var ms = new MemoryStream();
        var w = new BigEndianWriter(ms);
        w.U8(1);
        w.Bytes(sps.GeneralProfileTierLevel); // profile space/tier/idc, compat flags, constraint flags, level
        w.U16(0xF000);                         // min_spatial_segmentation_idc = 0
        w.U8(0xFC);                            // parallelismType = 0
        w.U8((byte)(0xFC | (sps.ChromaFormatIdc & 3)));
        w.U8((byte)(0xF8 | (sps.BitDepthLumaMinus8 & 7)));
        w.U8((byte)(0xF8 | (sps.BitDepthChromaMinus8 & 7)));
        w.U16(0);                              // avgFrameRate
        w.U8((byte)((1 << 3) | (1 << 2) | 3)); // 1 temporal layer, nested, 4-byte NAL lengths
        w.U8(3);
        foreach (var (type, nal) in new[] { (VpsType, Vps), (SpsType, Sps), (PpsType, Pps) })
        {
            w.U8((byte)(0x80 | type)); // array_completeness = 1
            w.U16(1);
            w.U16((ushort)nal.Length);
            w.Bytes(nal);
        }
        return ms.ToArray();
    }

    /// <summary>The handful of SPS fields hvcC needs.</summary>
    internal sealed class SpsInfo
    {
        public SpsInfo(byte[] spsNal)
        {
            var rbsp = RemoveEmulationPrevention(spsNal);
            var r = new BitReader(rbsp, 2); // skip the 2-byte NAL header
            r.Skip(4);
            int maxSubLayersMinus1 = (int)r.Read(3);
            r.Skip(1);

            int ptlStart = r.BitPosition;
            r.Skip(96);
            GeneralProfileTierLevel = r.CopyBits(ptlStart, 96);

            var profilePresent = new bool[8];
            var levelPresent = new bool[8];
            for (int i = 0; i < maxSubLayersMinus1; i++)
            {
                profilePresent[i] = r.Read(1) == 1;
                levelPresent[i] = r.Read(1) == 1;
            }
            if (maxSubLayersMinus1 > 0)
                for (int i = maxSubLayersMinus1; i < 8; i++)
                    r.Skip(2);
            for (int i = 0; i < maxSubLayersMinus1; i++)
            {
                if (profilePresent[i]) r.Skip(88);
                if (levelPresent[i]) r.Skip(8);
            }

            r.ReadUe(); // sps_seq_parameter_set_id
            ChromaFormatIdc = (int)r.ReadUe();
            if (ChromaFormatIdc == 3)
                r.Skip(1);
            Width = (int)r.ReadUe();
            Height = (int)r.ReadUe();
            if (r.Read(1) == 1)
            {
                r.ReadUe(); r.ReadUe(); r.ReadUe(); r.ReadUe();
            }
            BitDepthLumaMinus8 = (int)r.ReadUe();
            BitDepthChromaMinus8 = (int)r.ReadUe();
        }

        public byte[] GeneralProfileTierLevel { get; }

        public int ChromaFormatIdc { get; }

        public int Width { get; }

        public int Height { get; }

        public int BitDepthLumaMinus8 { get; }

        public int BitDepthChromaMinus8 { get; }
    }

    internal static byte[] RemoveEmulationPrevention(ReadOnlySpan<byte> nal)
    {
        var output = new List<byte>(nal.Length);
        int zeros = 0;
        foreach (var b in nal)
        {
            if (zeros >= 2 && b == 3)
            {
                zeros = 0;
                continue;
            }
            output.Add(b);
            zeros = b == 0 ? zeros + 1 : 0;
        }
        return [.. output];
    }

    private sealed class BitReader(byte[] data, int startByte)
    {
        private int _bit = startByte * 8;

        public int BitPosition => _bit;

        public uint Read(int count)
        {
            uint value = 0;
            for (int i = 0; i < count; i++)
            {
                int byteIndex = _bit >> 3;
                if (byteIndex >= data.Length)
                    throw new InvalidDataException("Truncated HEVC parameter set");
                value = (value << 1) | (uint)((data[byteIndex] >> (7 - (_bit & 7))) & 1);
                _bit++;
            }
            return value;
        }

        public void Skip(int count) => _bit += count;

        public uint ReadUe()
        {
            int leadingZeros = 0;
            while (Read(1) == 0)
            {
                if (++leadingZeros > 31)
                    throw new InvalidDataException("Bad Exp-Golomb code");
            }
            return leadingZeros == 0 ? 0 : (1u << leadingZeros) - 1 + Read(leadingZeros);
        }

        public byte[] CopyBits(int start, int count)
        {
            var result = new byte[(count + 7) / 8];
            for (int i = 0; i < count; i++)
            {
                int src = start + i;
                int bit = (data[src >> 3] >> (7 - (src & 7))) & 1;
                result[i >> 3] |= (byte)(bit << (7 - (i & 7)));
            }
            return result;
        }
    }
}

/// <summary>Minimal big-endian binary writer for ISO-BMFF boxes.</summary>
internal sealed class BigEndianWriter(Stream stream)
{
    public Stream Stream { get; } = stream;

    public void U8(byte value) => Stream.WriteByte(value);

    public void U16(ushort value)
    {
        Span<byte> b = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(b, value);
        Stream.Write(b);
    }

    public void U32(uint value)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(b, value);
        Stream.Write(b);
    }

    public void I32(int value) => U32(unchecked((uint)value));

    public void FourCC(string code)
    {
        if (code.Length != 4)
            throw new ArgumentException("A box type is four characters", nameof(code));
        foreach (var c in code)
            Stream.WriteByte((byte)c);
    }

    public void Bytes(ReadOnlySpan<byte> bytes) => Stream.Write(bytes);

    public void CString(string text)
    {
        Bytes(System.Text.Encoding.UTF8.GetBytes(text));
        Stream.WriteByte(0);
    }
}
