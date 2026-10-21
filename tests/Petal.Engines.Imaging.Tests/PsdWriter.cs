using System.Buffers.Binary;
using System.Text;

namespace Petal.Engines.Imaging.Tests;

/// <summary>A layer for <see cref="PsdWriter"/>: a solid colour rectangle, or a group marker.</summary>
internal sealed record TestLayer(string Name, int Left, int Top, int Width, int Height, (byte R, byte G, byte B) Colour, byte Opacity = 255, bool Hidden = false, int Section = 0);

/// <summary>
/// Writes small uncompressed Photoshop files the way Photoshop structures them (Adobe's PSD
/// specification): layer records with Unicode names and group markers, and a flat copy.
/// </summary>
internal static class PsdWriter
{
    /// <param name="layers">Bottom first, groups bracketed by an end marker (Section 3) and a start (Section 1).</param>
    /// <param name="composite">The flat copy; null writes a blank one and marks it as missing.</param>
    public static byte[] Write(int width, int height, IReadOnlyList<TestLayer> layers, Func<int, int, (byte R, byte G, byte B, byte A)>? composite, bool large = false, string? xmp = null)
    {
        var o = new MemoryStream();
        o.Write("8BPS"u8);
        U16(o, large ? 2 : 1);
        o.Write(new byte[6]);
        U16(o, 4); // RGBA
        U32(o, (uint)height);
        U32(o, (uint)width);
        U16(o, 8);
        U16(o, 3); // RGB
        U32(o, 0); // colour mode data

        // Resources: version info, saying whether the flat copy is real.
        var resource = new MemoryStream();
        U32(resource, 1);
        resource.WriteByte(composite is null ? (byte)0 : (byte)1);
        UnicodeString(resource, "Adobe Photoshop");
        UnicodeString(resource, "Adobe Photoshop 2024");
        U32(resource, 1);
        var resources = new MemoryStream();
        resources.Write("8BIM"u8);
        U16(resources, 1057);
        U16(resources, 0); // empty Pascal name, padded to even
        U32(resources, (uint)resource.Length);
        resources.Write(resource.ToArray());
        if (resource.Length % 2 == 1)
            resources.WriteByte(0);
        if (xmp is not null)
        {
            var xmpBytes = Encoding.UTF8.GetBytes(xmp);
            resources.Write("8BIM"u8);
            U16(resources, 1060);
            U16(resources, 0);
            U32(resources, (uint)xmpBytes.Length);
            resources.Write(xmpBytes);
            if (xmpBytes.Length % 2 == 1)
                resources.WriteByte(0);
        }
        U32(o, (uint)resources.Length);
        o.Write(resources.ToArray());

        // Layers.
        var info = new MemoryStream();
        U16(info, (ushort)layers.Count);
        foreach (var layer in layers)
        {
            bool marker = layer.Section != 0;
            int w = marker ? 0 : layer.Width, h = marker ? 0 : layer.Height;
            U32(info, (uint)(marker ? 0 : layer.Top));
            U32(info, (uint)(marker ? 0 : layer.Left));
            U32(info, (uint)(marker ? 0 : layer.Top + h));
            U32(info, (uint)(marker ? 0 : layer.Left + w));
            U16(info, 4);
            foreach (short id in new short[] { -1, 0, 1, 2 })
            {
                U16(info, (ushort)id);
                Size(info, 2 + (long)w * h, large);
            }
            info.Write("8BIMnorm"u8);
            info.WriteByte(layer.Opacity);
            info.WriteByte(0);
            info.WriteByte(layer.Hidden ? (byte)2 : (byte)0);
            info.WriteByte(0);
            var extra = new MemoryStream();
            U32(extra, 0); // mask
            U32(extra, 0); // blending ranges
            var ascii = Encoding.Latin1.GetBytes(new string(layer.Name.Select(c => c < 128 ? c : '?').ToArray()));
            extra.WriteByte((byte)ascii.Length);
            extra.Write(ascii);
            while ((extra.Length - 8) % 4 != 0)
                extra.WriteByte(0);
            var unicode = Encoding.BigEndianUnicode.GetBytes(layer.Name);
            extra.Write("8BIMluni"u8);
            U32(extra, (uint)(4 + unicode.Length));
            U32(extra, (uint)layer.Name.Length);
            extra.Write(unicode);
            if (layer.Section != 0)
            {
                extra.Write("8BIMlsct"u8);
                U32(extra, 4);
                U32(extra, (uint)layer.Section);
            }
            U32(info, (uint)extra.Length);
            info.Write(extra.ToArray());
        }
        foreach (var layer in layers)
        {
            int w = layer.Section != 0 ? 0 : layer.Width, h = layer.Section != 0 ? 0 : layer.Height;
            foreach (byte value in new[] { (byte)255, layer.Colour.R, layer.Colour.G, layer.Colour.B })
            {
                U16(info, 0); // raw
                info.Write(Enumerable.Repeat(value, w * h).ToArray());
            }
        }
        if (info.Length % 2 == 1)
            info.WriteByte(0);
        var section = new MemoryStream();
        Size(section, info.Length, large);
        section.Write(info.ToArray());
        U32(section, 0); // global layer mask
        Size(o, section.Length, large);
        o.Write(section.ToArray());

        // The flat copy, one plane per channel.
        U16(o, 0);
        foreach (int channel in new[] { 0, 1, 2, 3 })
        {
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    var (r, g, b, a) = composite?.Invoke(x, y) ?? (255, 255, 255, 255);
                    o.WriteByte(channel switch { 0 => r, 1 => g, 2 => b, _ => a });
                }
            }
        }
        return o.ToArray();
    }

    private static void UnicodeString(Stream s, string text)
    {
        U32(s, (uint)text.Length);
        s.Write(Encoding.BigEndianUnicode.GetBytes(text));
    }

    private static void Size(Stream s, long value, bool large)
    {
        if (large)
        {
            Span<byte> b = stackalloc byte[8];
            BinaryPrimitives.WriteUInt64BigEndian(b, (ulong)value);
            s.Write(b);
        }
        else
        {
            U32(s, (uint)value);
        }
    }

    private static void U16(Stream s, ushort value)
    {
        Span<byte> b = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(b, value);
        s.Write(b);
    }

    private static void U16(Stream s, int value) => U16(s, (ushort)value);

    private static void U32(Stream s, uint value)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(b, value);
        s.Write(b);
    }
}
