using System.Buffers.Binary;
using System.Text;
using ImageMagick;

namespace Petal.Engines.Imaging;

/// <summary>
/// GIMP's XCF as one picture. ImageMagick decodes the layers (bottom first, each at its place) but leaves out how
/// see-through each layer is; that is read here from the file's own layer list (version 0 to GIMP 3's 64-bit
/// one), applied, and the visible layers are flattened over a transparent canvas the size of the image.
/// </summary>
internal static class XcfFile
{
    private const int PropEnd = 0, PropOpacity = 6, PropVisible = 8, PropFloatOpacity = 33;

    public static MagickImage Load(string path)
    {
        var bytes = File.ReadAllBytes(path);
        using var layers = new MagickImageCollection(bytes, new MagickReadSettings { Format = MagickFormat.Xcf });
        if (layers.Count == 0)
            throw new MagickCorruptImageErrorException("This GIMP file has no layers.");
        var (width, height) = CanvasSize(bytes) ?? ((int)layers[0].Page.Width, (int)layers[0].Page.Height);
        var shown = Layers(bytes);
        // The file lists the top layer first; ImageMagick gives the bottom first.
        if (shown is not null && shown.Count == layers.Count)
        {
            for (int i = 0; i < layers.Count; i++)
            {
                var (visible, opacity) = shown[layers.Count - 1 - i];
                var layer = (MagickImage)layers[i];
                // The file's own word on what shows.
                layer.Compose = visible ? CompositeOperator.Over : CompositeOperator.No;
                if (visible && opacity < 0.999)
                {
                    layer.Alpha(AlphaAction.Set);
                    layer.Evaluate(Channels.Alpha, EvaluateOperator.Multiply, Math.Clamp(opacity, 0, 1));
                }
            }
        }
        using var canvas = new MagickImage(MagickColors.Transparent, (uint)Math.Max(1, width), (uint)Math.Max(1, height));
        foreach (var layer in layers)
        {
            if (layer.Compose == CompositeOperator.No)
                continue;
            canvas.Composite(layer, layer.Page.X, layer.Page.Y, CompositeOperator.Over);
        }
        var result = (MagickImage)canvas.Clone();
        result.ResetPage();
        return result;
    }

    /// <summary>The canvas's width and height from the file's header.</summary>
    private static (int Width, int Height)? CanvasSize(byte[] b)
    {
        int at = HeaderEnd(b);
        if (at < 0 || at + 8 > b.Length)
            return null;
        return (BinaryPrimitives.ReadInt32BigEndian(b.AsSpan(at)), BinaryPrimitives.ReadInt32BigEndian(b.AsSpan(at + 4)));
    }

    /// <summary>Where the header's text ("gimp xcf file" or "gimp xcf v011", then a NUL) ends.</summary>
    private static int HeaderEnd(byte[] b) => b.Length > 14 && Encoding.ASCII.GetString(b, 0, 9) == "gimp xcf " ? 14 : -1;

    /// <summary>
    /// Each layer's visibility and opacity, top layer first, or null when the file can't be followed (another
    /// layout, a broken pointer): then the layers are shown as ImageMagick gives them.
    /// </summary>
    internal static List<(bool Visible, double Opacity)>? Layers(byte[] b)
    {
        try
        {
            int at = HeaderEnd(b);
            if (at < 0)
                return null;
            var version = Encoding.ASCII.GetString(b, 9, 4);
            int fileVersion = version == "file" ? 0 : version.StartsWith('v') && int.TryParse(version[1..], out var v) ? v : 0;
            bool wide = fileVersion >= 11; // 64-bit pointers from GIMP 2.10's file version 11
            at += 12; // width, height, base type
            if (fileVersion >= 4)
                at += 4; // precision
            at = SkipProperties(b, at);
            var pointers = new List<long>();
            while (true)
            {
                long pointer = wide ? BinaryPrimitives.ReadInt64BigEndian(b.AsSpan(at)) : BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(at));
                at += wide ? 8 : 4;
                if (pointer == 0)
                    break;
                pointers.Add(pointer);
                if (pointers.Count > 10_000)
                    return null;
            }
            var layers = new List<(bool, double)>();
            foreach (var pointer in pointers)
            {
                int p = checked((int)pointer) + 12; // width, height, type
                int nameLength = BinaryPrimitives.ReadInt32BigEndian(b.AsSpan(p));
                p += 4 + nameLength;
                bool visible = true;
                double opacity = 1;
                while (true)
                {
                    int type = BinaryPrimitives.ReadInt32BigEndian(b.AsSpan(p));
                    int length = BinaryPrimitives.ReadInt32BigEndian(b.AsSpan(p + 4));
                    var data = b.AsSpan(p + 8, length);
                    if (type == PropEnd)
                        break;
                    if (type == PropVisible && length >= 4)
                        visible = BinaryPrimitives.ReadInt32BigEndian(data) != 0;
                    else if (type == PropOpacity && length >= 4)
                        opacity = BinaryPrimitives.ReadInt32BigEndian(data) / 255.0;
                    else if (type == PropFloatOpacity && length >= 4)
                        opacity = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32BigEndian(data));
                    p += 8 + length;
                }
                layers.Add((visible, opacity));
            }
            return layers;
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or IndexOutOfRangeException or OverflowException)
        {
            return null;
        }
    }

    private static int SkipProperties(byte[] b, int at)
    {
        while (true)
        {
            int type = BinaryPrimitives.ReadInt32BigEndian(b.AsSpan(at));
            int length = BinaryPrimitives.ReadInt32BigEndian(b.AsSpan(at + 4));
            at += 8 + length;
            if (type == PropEnd)
                return at;
        }
    }
}
