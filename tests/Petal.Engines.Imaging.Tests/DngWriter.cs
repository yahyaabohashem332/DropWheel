using System.Buffers.Binary;
using System.Text;

namespace Petal.Engines.Imaging.Tests;

/// <summary>
/// Writes a minimal camera RAW file in Adobe's DNG format (DNG specification 1.4): an RGGB Bayer
/// mosaic of 12-bit samples with a colour matrix that makes the camera's colours plain linear sRGB,
/// an orientation, and EXIF exposure data, as a camera would.
/// </summary>
internal static class DngWriter
{
    private sealed record Entry(ushort Tag, ushort Type, uint Count, byte[] Data);

    /// <param name="pixel">Linear RGB (0–1) wanted at each pixel; the mosaic keeps one colour per pixel.</param>
    public static byte[] Write(int width, int height, Func<int, int, (double R, double G, double B)> pixel, ushort orientation = 1)
    {
        const int white = 4095;
        var raw = new byte[width * height * 2];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                var (r, g, b) = pixel(x, y);
                // R G / G B
                double v = (y % 2, x % 2) switch { (0, 0) => r, (1, 1) => b, _ => g };
                BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan((y * width + x) * 2), (ushort)Math.Round(Math.Clamp(v, 0, 1) * white));
            }
        }

        // XYZ → linear sRGB (D65): the camera "is" sRGB.
        double[] matrix = [3.2406, -1.5372, -0.4986, -0.9689, 1.8758, 0.0415, 0.0557, -0.2040, 1.0570];
        var ifd0 = new List<Entry>
        {
            Long(254, 0),
            Long(256, (uint)width),
            Long(257, (uint)height),
            Short(258, 16),
            Short(259, 1),
            Short(262, 32803), // CFA
            Ascii(271, "Petal"),
            Ascii(272, "Test Camera"),
            Long(273, 0), // strip offset, filled in below
            Short(274, orientation),
            Short(277, 1),
            Long(278, (uint)height),
            Long(279, (uint)raw.Length),
            Short(284, 1),
            Ascii(306, "2024:05:06 07:08:09"),
            new(33421, 3, 2, [2, 0, 2, 0]),
            new(33422, 1, 4, [0, 1, 1, 2]),
            Long(34665, 0), // EXIF IFD offset, filled in below
            new(50706, 1, 4, [1, 4, 0, 0]),
            new(50707, 1, 4, [1, 1, 0, 0]),
            Ascii(50708, "Petal Test Camera"),
            Long(50717, white),
            SRationals(50721, matrix),
            Rationals(50728, [1, 1, 1]),
            Short(50778, 21), // D65
        };
        var exif = new List<Entry>
        {
            Rationals(33434, [1.0 / 125]),
            Short(34855, 200),
            Ascii(36867, "2024:05:06 07:08:09"),
            Rationals(37386, [35]),
        };

        // Layout: header, IFD0 (+ its data), EXIF IFD (+ its data), pixels.
        int ifd0Offset = 8;
        int ifd0Size = IfdSize(ifd0);
        int exifOffset = ifd0Offset + ifd0Size;
        int exifSize = IfdSize(exif);
        int pixelsOffset = exifOffset + exifSize;
        ifd0[ifd0.FindIndex(e => e.Tag == 273)] = Long(273, (uint)pixelsOffset);
        ifd0[ifd0.FindIndex(e => e.Tag == 34665)] = Long(34665, (uint)exifOffset);

        var o = new MemoryStream();
        o.Write("II*\0"u8);
        U32(o, (uint)ifd0Offset);
        WriteIfd(o, ifd0, ifd0Offset);
        WriteIfd(o, exif, exifOffset);
        o.Write(raw);
        return o.ToArray();
    }

    private static int IfdSize(List<Entry> entries) =>
        2 + entries.Count * 12 + 4 + entries.Sum(e => e.Data.Length > 4 ? e.Data.Length + e.Data.Length % 2 : 0);

    private static void WriteIfd(Stream o, List<Entry> entries, int offset)
    {
        entries.Sort((a, b) => a.Tag.CompareTo(b.Tag));
        int dataAt = offset + 2 + entries.Count * 12 + 4;
        var data = new MemoryStream();
        U16(o, (ushort)entries.Count);
        foreach (var e in entries)
        {
            U16(o, e.Tag);
            U16(o, e.Type);
            U32(o, e.Count);
            if (e.Data.Length <= 4)
            {
                var inline = new byte[4];
                e.Data.CopyTo(inline, 0);
                o.Write(inline);
            }
            else
            {
                U32(o, (uint)(dataAt + data.Length));
                data.Write(e.Data);
                if (e.Data.Length % 2 == 1)
                    data.WriteByte(0);
            }
        }
        U32(o, 0);
        o.Write(data.ToArray());
    }

    private static Entry Short(ushort tag, ushort value)
    {
        var b = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(b, value);
        return new(tag, 3, 1, b);
    }

    private static Entry Long(ushort tag, uint value)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, value);
        return new(tag, 4, 1, b);
    }

    private static Entry Ascii(ushort tag, string text)
    {
        var b = Encoding.ASCII.GetBytes(text + "\0");
        return new(tag, 2, (uint)b.Length, b);
    }

    private static Entry Rationals(ushort tag, double[] values)
    {
        var b = new byte[values.Length * 8];
        for (int i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(i * 8), (uint)Math.Round(values[i] * 1_000_000));
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(i * 8 + 4), 1_000_000);
        }
        return new(tag, 5, (uint)values.Length, b);
    }

    private static Entry SRationals(ushort tag, double[] values)
    {
        var b = new byte[values.Length * 8];
        for (int i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(i * 8), (int)Math.Round(values[i] * 10_000));
            BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(i * 8 + 4), 10_000);
        }
        return new(tag, 10, (uint)values.Length, b);
    }

    private static void U16(Stream s, ushort value)
    {
        Span<byte> b = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(b, value);
        s.Write(b);
    }

    private static void U32(Stream s, uint value)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, value);
        s.Write(b);
    }
}
