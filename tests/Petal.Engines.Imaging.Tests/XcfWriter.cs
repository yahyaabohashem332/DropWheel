using System.Buffers.Binary;
using System.Text;

namespace Petal.Engines.Imaging.Tests;

/// <summary>A layer for <see cref="XcfWriter"/>: its place on the canvas, its pixels (RGBA), and how it shows.</summary>
public sealed record XcfLayer(string Name, int X, int Y, int Width, int Height, Func<int, int, (byte R, byte G, byte B, byte A)> Pixel, bool Visible = true, int Opacity = 255);

/// <summary>
/// Writes GIMP's XCF as GIMP 2 does (version 0, RGB, RLE-compressed tiles of 64×64), from the specification
/// (devel-docs/xcf.txt), so tests have layered files without GIMP. The first layer listed is the top one.
/// </summary>
public static class XcfWriter
{
    private const int PropEnd = 0, PropOpacity = 6, PropVisible = 8, PropOffsets = 15, PropCompression = 17;

    public static byte[] Write(int width, int height, IReadOnlyList<XcfLayer> layers)
    {
        var body = new List<byte>();
        void U32(int value) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteInt32BigEndian(b, value); body.AddRange(b.ToArray()); }
        void Patch(int at, int value) { var b = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(b, value); for (int i = 0; i < 4; i++) body[at + i] = b[i]; }
        void Text(string s) { var bytes = Encoding.UTF8.GetBytes(s + "\0"); U32(bytes.Length); body.AddRange(bytes); }
        void Prop(int type, params int[] values) { U32(type); U32(values.Length * 4); foreach (var v in values) U32(v); }

        body.AddRange(Encoding.ASCII.GetBytes("gimp xcf file\0"));
        U32(width);
        U32(height);
        U32(0); // RGB
        U32(PropCompression);
        U32(1);
        body.Add(1); // RLE, GIMP's own default
        U32(PropEnd);
        U32(0);
        int layerPointers = body.Count;
        foreach (var _ in layers)
            U32(0);
        U32(0); // end of layers
        U32(0); // no channels

        for (int l = 0; l < layers.Count; l++)
        {
            var layer = layers[l];
            Patch(layerPointers + l * 4, body.Count);
            U32(layer.Width);
            U32(layer.Height);
            U32(1); // RGBA
            Text(layer.Name);
            Prop(PropOpacity, layer.Opacity);
            Prop(PropVisible, layer.Visible ? 1 : 0);
            Prop(PropOffsets, layer.X, layer.Y);
            U32(PropEnd);
            U32(0);
            int hierarchyPointer = body.Count;
            U32(0);
            U32(0); // no mask

            Patch(hierarchyPointer, body.Count);
            U32(layer.Width);
            U32(layer.Height);
            U32(4); // bytes a pixel
            int levelPointer = body.Count;
            U32(0);
            U32(0); // end of levels

            Patch(levelPointer, body.Count);
            U32(layer.Width);
            U32(layer.Height);
            int tilesX = (layer.Width + 63) / 64, tilesY = (layer.Height + 63) / 64;
            int tilePointers = body.Count;
            for (int t = 0; t < tilesX * tilesY; t++)
                U32(0);
            U32(0);
            for (int ty = 0; ty < tilesY; ty++)
                for (int tx = 0; tx < tilesX; tx++)
                {
                    Patch(tilePointers + (ty * tilesX + tx) * 4, body.Count);
                    int w = Math.Min(64, layer.Width - tx * 64), h = Math.Min(64, layer.Height - ty * 64);
                    // RLE: each channel on its own, in runs of up to 127 bytes written as they are.
                    for (int channel = 0; channel < 4; channel++)
                    {
                        var plane = new List<byte>(w * h);
                        for (int y = 0; y < h; y++)
                            for (int x = 0; x < w; x++)
                            {
                                var (r, g, b, a) = layer.Pixel(tx * 64 + x, ty * 64 + y);
                                plane.Add(channel switch { 0 => r, 1 => g, 2 => b, _ => a });
                            }
                        for (int at = 0; at < plane.Count; at += 127)
                        {
                            int run = Math.Min(127, plane.Count - at);
                            body.Add((byte)(256 - run));
                            body.AddRange(plane.GetRange(at, run));
                        }
                    }
                }
        }
        return body.ToArray();
    }
}
