using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Petal.Shell;

/// <summary>A Photoshop layer as the preview pane lists it: its name, whether it shows, and how deep in groups it is.</summary>
public sealed class PsdLayer
{
    public PsdLayer(string name, bool visible, int depth, bool group)
    {
        Name = name;
        Visible = visible;
        Depth = depth;
        Group = group;
    }

    public string Name { get; }
    public bool Visible { get; }
    public int Depth { get; }
    public bool Group { get; }
}

/// <summary>
/// The layers of a Photoshop file (PSD or PSB), top first as Photoshop's panel shows them: each one's name (its
/// Unicode name when it has one), whether it is hidden, and the groups it is in.
/// </summary>
public static class PsdLayers
{
    public static List<PsdLayer> Read(Stream s)
    {
        var layers = new List<PsdLayer>();
        try
        {
            s.Position = 0;
            var head = new byte[26];
            if (Fill(s, head) < 26 || head[0] != '8' || head[1] != 'B' || head[2] != 'P' || head[3] != 'S')
                return layers;
            bool big = (head[4] << 8 | head[5]) == 2;
            Skip(s, Big32(s)); // colour mode data
            Skip(s, Big32(s)); // image resources
            long section = big ? Big64(s) : Big32(s);
            if (section == 0)
                return layers;
            long info = big ? Big64(s) : Big32(s);
            if (info == 0)
                return layers;
            int count = Math.Abs((short)Big16(s));
            var found = new List<(string Name, bool Visible, int Divider)>();
            for (int i = 0; i < count && i < 10000; i++)
            {
                s.Position += 16; // the layer's rectangle
                int channels = Big16(s);
                s.Position += channels * (big ? 10 : 6);
                s.Position += 4 + 4; // blend signature and mode
                s.Position += 2; // opacity, clipping
                int flags = s.ReadByte();
                s.Position += 1; // filler
                long extra = Big32(s);
                long end = s.Position + extra;
                Skip(s, Big32(s)); // mask data
                Skip(s, Big32(s)); // blending ranges
                int nameLength = s.ReadByte();
                var nameBytes = new byte[nameLength];
                Fill(s, nameBytes);
                // Latin-1 for the short name; the Unicode one, when there is one, replaces it below.
                var latin = new char[nameBytes.Length];
                for (int k = 0; k < nameBytes.Length; k++)
                    latin[k] = (char)nameBytes[k];
                string name = new string(latin);
                // The name is padded to a multiple of four, counting its length byte.
                int padded = (nameLength + 1 + 3) / 4 * 4;
                s.Position += padded - (nameLength + 1);
                int divider = 0;
                while (s.Position + 12 <= end)
                {
                    var sig = new byte[4];
                    Fill(s, sig);
                    if (sig[0] != '8' || sig[1] != 'B')
                        break;
                    var keyBytes = new byte[4];
                    Fill(s, keyBytes);
                    var key = Encoding.ASCII.GetString(keyBytes);
                    bool wide = big && key is "LMsk" or "Lr16" or "Lr32" or "Layr" or "Mt16" or "Mt32" or "Mtrn" or "Alph" or "FMsk" or "lnk2" or "FEid" or "FXid" or "PxSD";
                    long length = wide ? Big64(s) : Big32(s);
                    long next = s.Position + length + (length % 2);
                    if (key == "luni" && length >= 4)
                    {
                        int chars = (int)Big32(s);
                        var text = new byte[Math.Min(chars, 1000) * 2];
                        Fill(s, text);
                        name = Encoding.BigEndianUnicode.GetString(text).TrimEnd('\0');
                    }
                    else if (key is "lsct" or "lsdk" && length >= 4)
                        divider = (int)Big32(s);
                    s.Position = next;
                }
                s.Position = end;
                found.Add((name, (flags & 2) == 0, divider));
            }
            // Bottom first in the file: from the top down, a group opens at its own record and closes at its divider.
            int depth = 0;
            for (int i = found.Count - 1; i >= 0; i--)
            {
                var (name, visible, divider) = found[i];
                if (divider == 3)
                {
                    depth = Math.Max(0, depth - 1);
                    continue;
                }
                bool group = divider is 1 or 2;
                layers.Add(new PsdLayer(name, visible, depth, group));
                if (group)
                    depth++;
            }
        }
        catch (Exception ex) when (ex is EndOfStreamException or IOException or ArgumentException or OverflowException)
        {
        }
        return layers;
    }

    /// <summary>Past a block whose length was just read: the length first, then the move (s.Position += … would read the place before the length).</summary>
    private static void Skip(Stream s, long count) => s.Position += count;

    private static int Fill(Stream s, byte[] buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int n = s.Read(buffer, total, buffer.Length - total);
            if (n <= 0)
                throw new EndOfStreamException();
            total += n;
        }
        return total;
    }

    private static int Big16(Stream s)
    {
        var b = new byte[2];
        Fill(s, b);
        return b[0] << 8 | b[1];
    }

    private static long Big32(Stream s)
    {
        var b = new byte[4];
        Fill(s, b);
        return (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]);
    }

    private static long Big64(Stream s) => Big32(s) << 32 | Big32(s);
}
