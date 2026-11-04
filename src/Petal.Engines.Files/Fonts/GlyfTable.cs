using System.Buffers.Binary;

namespace Petal.Engines.Files.Fonts;

/// <summary>A point of a TrueType outline; off the curve, it is a control point.</summary>
internal readonly record struct OutlinePoint(double X, double Y, bool On);

/// <summary>TrueType outlines: read, with composite glyphs flattened, and written.</summary>
internal static class GlyfTable
{
    /// <summary>Each glyph's contours, composites flattened into the contours of their parts.</summary>
    public static List<List<OutlinePoint>>[] Read(SfntFont font)
    {
        var glyf = font.Table("glyf");
        var loca = font.Table("loca");
        int glyphs = font.GlyphCount;
        bool longOffsets = BinaryPrimitives.ReadInt16BigEndian(font.Table("head").AsSpan(50)) != 0;
        int Offset(int g) => longOffsets ? (int)BinaryPrimitives.ReadUInt32BigEndian(loca.AsSpan(g * 4)) : BinaryPrimitives.ReadUInt16BigEndian(loca.AsSpan(g * 2)) * 2;
        var result = new List<List<OutlinePoint>>[glyphs];
        for (int g = 0; g < glyphs; g++)
            result[g] = Glyph(glyf, g, glyphs, Offset, 0);
        return result;
    }

    private static List<List<OutlinePoint>> Glyph(byte[] glyf, int g, int glyphs, Func<int, int> offset, int depth)
    {
        var contours = new List<List<OutlinePoint>>();
        if (g >= glyphs || depth > 8)
            return contours;
        int from = offset(g), to = offset(g + 1);
        if (to <= from)
            return contours;
        var d = glyf.AsSpan(from, to - from);
        short count = BinaryPrimitives.ReadInt16BigEndian(d);
        if (count >= 0)
        {
            Simple(d, count, contours);
            return contours;
        }
        int at = 10;
        bool more = true;
        while (more)
        {
            int flags = BinaryPrimitives.ReadUInt16BigEndian(d[at..]);
            int part = BinaryPrimitives.ReadUInt16BigEndian(d[(at + 2)..]);
            at += 4;
            double dx = 0, dy = 0;
            if ((flags & 1) != 0)
            {
                if ((flags & 2) != 0)
                    (dx, dy) = (BinaryPrimitives.ReadInt16BigEndian(d[at..]), BinaryPrimitives.ReadInt16BigEndian(d[(at + 2)..]));
                at += 4;
            }
            else
            {
                if ((flags & 2) != 0)
                    (dx, dy) = ((sbyte)d[at], (sbyte)d[at + 1]);
                at += 2;
            }
            double a = 1, b = 0, c = 0, e = 1;
            static double F2(ReadOnlySpan<byte> s) => BinaryPrimitives.ReadInt16BigEndian(s) / 16384.0;
            if ((flags & 8) != 0)
            {
                a = e = F2(d[at..]);
                at += 2;
            }
            else if ((flags & 0x40) != 0)
            {
                a = F2(d[at..]);
                e = F2(d[(at + 2)..]);
                at += 4;
            }
            else if ((flags & 0x80) != 0)
            {
                a = F2(d[at..]);
                b = F2(d[(at + 2)..]);
                c = F2(d[(at + 4)..]);
                e = F2(d[(at + 6)..]);
                at += 8;
            }
            foreach (var contour in Glyph(glyf, part, glyphs, offset, depth + 1))
                contours.Add(contour.Select(p => new OutlinePoint(p.X * a + p.Y * c + dx, p.X * b + p.Y * e + dy, p.On)).ToList());
            more = (flags & 0x20) != 0;
        }
        return contours;
    }

    private static void Simple(ReadOnlySpan<byte> d, int count, List<List<OutlinePoint>> contours)
    {
        if (count == 0)
            return;
        int at = 10;
        var ends = new int[count];
        for (int c = 0; c < count; c++, at += 2)
            ends[c] = BinaryPrimitives.ReadUInt16BigEndian(d[at..]);
        int total = ends[^1] + 1;
        at += 2 + BinaryPrimitives.ReadUInt16BigEndian(d[at..]);
        var flags = new byte[total];
        for (int p = 0; p < total;)
        {
            byte f = d[at++];
            flags[p++] = f;
            if ((f & 8) != 0)
                for (int r = d[at++]; r > 0 && p < total; r--)
                    flags[p++] = f;
        }
        var xs = new int[total];
        var ys = new int[total];
        int value = 0;
        for (int p = 0; p < total; p++)
        {
            byte f = flags[p];
            if ((f & 2) != 0)
            {
                int v = d[at++];
                value += (f & 16) != 0 ? v : -v;
            }
            else if ((f & 16) == 0)
            {
                value += BinaryPrimitives.ReadInt16BigEndian(d[at..]);
                at += 2;
            }
            xs[p] = value;
        }
        value = 0;
        for (int p = 0; p < total; p++)
        {
            byte f = flags[p];
            if ((f & 4) != 0)
            {
                int v = d[at++];
                value += (f & 32) != 0 ? v : -v;
            }
            else if ((f & 32) == 0)
            {
                value += BinaryPrimitives.ReadInt16BigEndian(d[at..]);
                at += 2;
            }
            ys[p] = value;
        }
        int start = 0;
        foreach (int end in ends)
        {
            var contour = new List<OutlinePoint>(end - start + 1);
            for (int p = start; p <= end; p++)
                contour.Add(new OutlinePoint(xs[p], ys[p], (flags[p] & 1) != 0));
            contours.Add(contour);
            start = end + 1;
        }
    }

    /// <summary>A simple glyph's flags and coordinates, as small as they go.</summary>
    public static void WritePoints(Stream output, bool[] on, int[] xs, int[] ys, bool overlapFirst = false)
    {
        int total = on.Length;
        var flags = new byte[total];
        var xData = new MemoryStream();
        var yData = new MemoryStream();
        int lastX = 0, lastY = 0;
        for (int p = 0; p < total; p++)
        {
            int dx = xs[p] - lastX, dy = ys[p] - lastY;
            int f = on[p] ? 1 : 0;
            if (p == 0 && overlapFirst)
                f |= 0x40;
            if (dx == 0)
                f |= 16;
            else if (Math.Abs(dx) < 256)
            {
                f |= 2 | (dx > 0 ? 16 : 0);
                xData.WriteByte((byte)Math.Abs(dx));
            }
            else
            {
                xData.WriteByte((byte)(dx >> 8));
                xData.WriteByte((byte)dx);
            }
            if (dy == 0)
                f |= 32;
            else if (Math.Abs(dy) < 256)
            {
                f |= 4 | (dy > 0 ? 32 : 0);
                yData.WriteByte((byte)Math.Abs(dy));
            }
            else
            {
                yData.WriteByte((byte)(dy >> 8));
                yData.WriteByte((byte)dy);
            }
            flags[p] = (byte)f;
            (lastX, lastY) = (xs[p], ys[p]);
        }
        for (int p = 0; p < total;)
        {
            int repeat = 0;
            while (p + repeat + 1 < total && flags[p + repeat + 1] == flags[p] && repeat < 255)
                repeat++;
            if (repeat > 0)
            {
                output.WriteByte((byte)(flags[p] | 8));
                output.WriteByte((byte)repeat);
            }
            else
                output.WriteByte(flags[p]);
            p += repeat + 1;
        }
        xData.WriteTo(output);
        yData.WriteTo(output);
    }

    private static void Short(Stream to, int value)
    {
        to.WriteByte((byte)(value >> 8));
        to.WriteByte((byte)value);
    }

    /// <summary>Writes the "glyf", "loca" and a new "maxp" and bounding box from outlines with whole-number points.</summary>
    public static void Write(SfntFont font, IReadOnlyList<List<List<OutlinePoint>>> glyphs)
    {
        var data = new MemoryStream();
        var offsets = new int[glyphs.Count + 1];
        var xMins = new int[glyphs.Count];
        int maxPoints = 0, maxContours = 0;
        int boxX0 = int.MaxValue, boxY0 = int.MaxValue, boxX1 = int.MinValue, boxY1 = int.MinValue;
        for (int g = 0; g < glyphs.Count; g++)
        {
            offsets[g] = (int)data.Length;
            var contours = glyphs[g].Where(c => c.Count > 0).ToList();
            if (contours.Count == 0)
                continue;
            var points = contours.SelectMany(c => c).ToList();
            var xs = points.Select(p => (int)Math.Round(p.X)).ToArray();
            var ys = points.Select(p => (int)Math.Round(p.Y)).ToArray();
            var on = points.Select(p => p.On).ToArray();
            int x0 = xs.Min(), x1 = xs.Max(), y0 = ys.Min(), y1 = ys.Max();
            xMins[g] = x0;
            (boxX0, boxY0, boxX1, boxY1) = (Math.Min(boxX0, x0), Math.Min(boxY0, y0), Math.Max(boxX1, x1), Math.Max(boxY1, y1));
            maxPoints = Math.Max(maxPoints, points.Count);
            maxContours = Math.Max(maxContours, contours.Count);
            Short(data, contours.Count);
            Short(data, x0);
            Short(data, y0);
            Short(data, x1);
            Short(data, y1);
            int end = -1;
            foreach (var contour in contours)
            {
                end += contour.Count;
                Short(data, end);
            }
            Short(data, 0);
            WritePoints(data, on, xs, ys);
            while (data.Length % 4 != 0)
                data.WriteByte(0);
        }
        offsets[glyphs.Count] = (int)data.Length;
        bool longOffsets = data.Length >= 0x20000;
        var loca = new byte[(glyphs.Count + 1) * (longOffsets ? 4 : 2)];
        for (int g = 0; g <= glyphs.Count; g++)
            if (longOffsets)
                BinaryPrimitives.WriteUInt32BigEndian(loca.AsSpan(g * 4), (uint)offsets[g]);
            else
                BinaryPrimitives.WriteUInt16BigEndian(loca.AsSpan(g * 2), (ushort)(offsets[g] / 2));
        font.Tables["glyf"] = data.ToArray();
        font.Tables["loca"] = loca;

        var head = (byte[])font.Table("head").Clone();
        BinaryPrimitives.WriteInt16BigEndian(head.AsSpan(50), (short)(longOffsets ? 1 : 0));
        if (boxX0 != int.MaxValue)
        {
            BinaryPrimitives.WriteInt16BigEndian(head.AsSpan(36), (short)boxX0);
            BinaryPrimitives.WriteInt16BigEndian(head.AsSpan(38), (short)boxY0);
            BinaryPrimitives.WriteInt16BigEndian(head.AsSpan(40), (short)boxX1);
            BinaryPrimitives.WriteInt16BigEndian(head.AsSpan(42), (short)boxY1);
        }
        font.Tables["head"] = head;

        var maxp = new byte[32];
        BinaryPrimitives.WriteUInt32BigEndian(maxp, 0x00010000);
        BinaryPrimitives.WriteUInt16BigEndian(maxp.AsSpan(4), (ushort)glyphs.Count);
        BinaryPrimitives.WriteUInt16BigEndian(maxp.AsSpan(6), (ushort)maxPoints);
        BinaryPrimitives.WriteUInt16BigEndian(maxp.AsSpan(8), (ushort)maxContours);
        BinaryPrimitives.WriteUInt16BigEndian(maxp.AsSpan(14), 1); // maxZones
        font.Tables["maxp"] = maxp;

        // Side bearings follow the outlines.
        if (font.Tables.TryGetValue("hmtx", out var original) && font.Tables.TryGetValue("hhea", out var hhea))
        {
            var hmtx = (byte[])original.Clone();
            int metrics = BinaryPrimitives.ReadUInt16BigEndian(hhea.AsSpan(34));
            for (int g = 0; g < glyphs.Count; g++)
            {
                int at = g < metrics ? g * 4 + 2 : metrics * 4 + (g - metrics) * 2;
                if (at + 2 <= hmtx.Length)
                    BinaryPrimitives.WriteInt16BigEndian(hmtx.AsSpan(at), (short)xMins[g]);
            }
            font.Tables["hmtx"] = hmtx;
        }
    }
}
