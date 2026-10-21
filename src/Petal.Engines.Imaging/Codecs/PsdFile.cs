using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;

namespace Petal.Engines.Imaging;

/// <summary>What a layer is, as Photoshop shows it in the Layers panel.</summary>
public enum PsdLayerKind
{
    Pixels,
    Text,
    SmartObject,
    Group,
    /// <summary>A layer with no pixels of its own (an empty layer, a fill or adjustment layer).</summary>
    Empty,
}

/// <summary>One entry of the Layers panel.</summary>
/// <param name="Index">Position in the file, bottom layer first.</param>
/// <param name="Group">The groups it sits in, outermost first ("Header › Logo").</param>
/// <param name="Hidden">Hidden itself or inside a hidden group.</param>
/// <param name="Opacity">0–255.</param>
/// <param name="FrameIndex">Its frame among the layers ImageMagick decodes (those with pixels), or -1.</param>
public sealed record PsdLayer(
    int Index,
    string Name,
    PsdLayerKind Kind,
    IReadOnlyList<string> Group,
    bool Hidden,
    byte Opacity,
    string BlendMode,
    int Left,
    int Top,
    int Width,
    int Height,
    int FrameIndex,
    string? Text,
    IReadOnlyList<string> Fonts);

/// <summary>A file placed in the document as a smart object.</summary>
/// <param name="Linked">True for a link to a file outside the document, false for an embedded copy.</param>
public sealed record PsdPlacedFile(string Name, bool Linked, string? Path);

/// <summary>
/// The structure of a Photoshop document (PSD, or PSB for large documents): size, colour mode,
/// layers with their real (Unicode) names, groups and visibility, the fonts of text layers, and
/// smart objects. Pixels are left to ImageMagick; this reads only what it doesn't expose.
/// </summary>
public sealed class PsdFile
{
    // Additional-information keys whose length is 8 bytes in a PSB.
    private static readonly HashSet<string> LongKeys = ["LMsk", "Lr16", "Lr32", "Layr", "Mt16", "Mt32", "Mtrn", "Alph", "FMsk", "lnk2", "FEid", "FXid", "PxSD"];

    public bool IsLarge { get; private init; }
    public int Width { get; private init; }
    public int Height { get; private init; }
    public int Channels { get; private init; }
    public int Depth { get; private init; }

    /// <summary>Bitmap, Grayscale, Indexed, RGB, CMYK, Multichannel, Duotone or Lab.</summary>
    public string ColorMode { get; private init; } = "";

    /// <summary>False when the file was saved without "Maximize compatibility": its flat picture is blank.</summary>
    public bool HasComposite { get; private init; } = true;

    /// <summary>Where in the file the "has a real flat picture" flag is, when the file records one.</summary>
    public long? CompositeFlagOffset { get; private init; }

    public double DpiX { get; private init; }
    public byte[]? Thumbnail { get; private init; }
    public byte[]? Xmp { get; private init; }
    public byte[]? IccProfile { get; private init; }
    public IReadOnlyList<PsdLayer> Layers { get; private init; } = [];
    public IReadOnlyList<PsdPlacedFile> PlacedFiles { get; private init; } = [];

    /// <summary>Every font the text layers use, once each.</summary>
    public IReadOnlyList<string> Fonts => Layers.SelectMany(l => l.Fonts).Distinct(StringComparer.Ordinal).ToList();

    /// <summary>Layers with pixels of their own, in the order ImageMagick returns them after the flat picture.</summary>
    public int FrameCount => Layers.Count(l => l.FrameIndex >= 0);

    public static bool IsPsd(ReadOnlySpan<byte> head) => head.Length >= 6 && head[..4].SequenceEqual("8BPS"u8) && head[4] == 0 && head[5] is 1 or 2;

    public static PsdFile Read(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
        return Read(stream);
    }

    public static PsdFile Read(Stream stream)
    {
        var r = new Reader(stream);
        if (!r.Bytes(4).AsSpan().SequenceEqual("8BPS"u8))
            throw new InvalidDataException("This isn't a Photoshop file.");
        int version = r.U16();
        if (version is not (1 or 2))
            throw new InvalidDataException("This Photoshop file version isn't supported.");
        bool big = version == 2;
        r.Skip(6);
        int channels = r.U16();
        int height = (int)r.U32();
        int width = (int)r.U32();
        int depth = r.U16();
        int mode = r.U16();

        r.Skip(r.U32()); // colour mode data (palettes, duotone)

        // Image resources.
        long resourcesLength = r.U32();
        long resourcesEnd = r.Position + resourcesLength;
        bool hasComposite = true;
        long? compositeFlag = null;
        double dpi = 72;
        byte[]? thumbnail = null, xmp = null, icc = null;
        while (r.Position + 12 <= resourcesEnd)
        {
            r.Skip(4); // "8BIM" (or another vendor's signature)
            int id = r.U16();
            int nameLength = r.U8();
            r.Skip(nameLength + ((nameLength + 1) % 2));
            long size = r.U32();
            long next = r.Position + size + (size % 2);
            switch (id)
            {
                case 1005 when size >= 4:
                    dpi = r.U32() / 65536.0;
                    break;
                case 1036 when size > 28: // thumbnail: a 28-byte header, then a JPEG
                    r.Skip(28);
                    thumbnail = r.Bytes((int)size - 28);
                    break;
                case 1039:
                    icc = r.Bytes((int)size);
                    break;
                case 1057 when size >= 5: // version info: does the file hold a real flat picture?
                    r.Skip(4);
                    compositeFlag = r.Position;
                    hasComposite = r.U8() != 0;
                    break;
                case 1060:
                    xmp = r.Bytes((int)size);
                    break;
            }
            r.Seek(next);
        }
        r.Seek(resourcesEnd);

        // Layer and mask information.
        var records = new List<Record>();
        var placed = new List<PsdPlacedFile>();
        long sectionLength = big ? (long)r.U64() : r.U32();
        long sectionEnd = r.Position + sectionLength;
        if (sectionLength > 0)
        {
            long infoLength = big ? (long)r.U64() : r.U32();
            long infoEnd = r.Position + infoLength;
            if (infoLength > 0)
                ReadLayerInfo(r, big, records);
            r.Seek(infoEnd);
            if (r.Position + 4 <= sectionEnd)
                r.Skip(r.U32()); // global layer mask
            r.AlignToSignature(sectionEnd);
            // 16- and 32-bit documents keep their layers in an additional-information block instead.
            while (r.Position + 12 <= sectionEnd)
            {
                var signature = r.Text(4);
                if (signature is not ("8BIM" or "8B64"))
                    break;
                var key = r.Text(4);
                long length = big && LongKeys.Contains(key) ? (long)r.U64() : r.U32();
                long blockEnd = r.Position + length;
                if (key is "Lr16" or "Lr32" or "Layr" && records.Count == 0)
                    ReadLayerInfo(r, big, records);
                else if (key is "lnk2" or "lnk3" or "lnkD" or "lnkE" && length < 64L << 20)
                    ReadLinkedFiles(r.Bytes((int)length), placed);
                r.Seek(blockEnd);
                r.AlignToSignature(sectionEnd);
            }
        }

        return new PsdFile
        {
            IsLarge = big,
            Width = width,
            Height = height,
            Channels = channels,
            Depth = depth,
            ColorMode = mode switch
            {
                0 => "Bitmap",
                1 => "Grayscale",
                2 => "Indexed",
                3 => "RGB",
                4 => "CMYK",
                7 => "Multichannel",
                8 => "Duotone",
                9 => "Lab",
                _ => $"Mode {mode}",
            },
            HasComposite = hasComposite,
            CompositeFlagOffset = compositeFlag,
            DpiX = dpi,
            Thumbnail = thumbnail,
            Xmp = xmp,
            IccProfile = icc,
            Layers = BuildLayers(records),
            PlacedFiles = placed,
        };
    }

    private sealed class Record
    {
        public int Top, Left, Bottom, Right;
        public string Name = "";
        public string? UnicodeName;
        public bool Hidden;
        public byte Opacity;
        public string Blend = "norm";
        public int Section; // 0 normal, 1–2 group start (open/closed), 3 group end
        public bool IsText, IsSmart, IsFill;
        public string? Text;
        public List<string> Fonts = [];
    }

    private static void ReadLayerInfo(Reader r, bool big, List<Record> records)
    {
        int count = Math.Abs((short)r.U16());
        var channelLengths = new List<long>();
        for (int i = 0; i < count; i++)
        {
            var rec = new Record
            {
                Top = (int)r.U32(),
                Left = (int)r.U32(),
                Bottom = (int)r.U32(),
                Right = (int)r.U32(),
            };
            int channels = r.U16();
            for (int c = 0; c < channels; c++)
            {
                r.Skip(2);
                channelLengths.Add(big ? (long)r.U64() : r.U32());
            }
            r.Skip(4); // "8BIM"
            rec.Blend = r.Text(4);
            rec.Opacity = r.U8();
            r.Skip(1); // clipping
            byte flags = r.U8();
            rec.Hidden = (flags & 0x02) != 0;
            r.Skip(1);
            long extraLength = r.U32();
            long extraEnd = r.Position + extraLength;
            r.Skip(r.U32()); // mask
            r.Skip(r.U32()); // blending ranges
            int nameLength = r.U8();
            rec.Name = Encoding.Latin1.GetString(r.Bytes(nameLength));
            r.Skip((4 - (nameLength + 1) % 4) % 4);
            while (r.Position + 12 <= extraEnd)
            {
                var signature = r.Text(4);
                if (signature is not ("8BIM" or "8B64"))
                    break;
                var key = r.Text(4);
                long length = big && LongKeys.Contains(key) ? (long)r.U64() : r.U32();
                long blockEnd = r.Position + length;
                switch (key)
                {
                    case "luni" when length >= 4:
                        int chars = (int)r.U32();
                        rec.UnicodeName = Encoding.BigEndianUnicode.GetString(r.Bytes(Math.Min(chars * 2, (int)length - 4))).TrimEnd('\0');
                        break;
                    case "lsct" or "lsdk" when length >= 4:
                        rec.Section = (int)r.U32();
                        break;
                    case "TySh" when length < 16 << 20:
                        rec.IsText = true;
                        ReadText(r.Bytes((int)length), rec);
                        break;
                    case "SoLd" or "SoLE" or "PlLd":
                        rec.IsSmart = true;
                        break;
                    case "SoCo" or "GdFl" or "PtFl" or "vscg":
                        rec.IsFill = true;
                        break;
                }
                r.Seek(blockEnd);
                r.AlignToSignature(extraEnd);
            }
            r.Seek(extraEnd);
            records.Add(rec);
        }
        // The pixels of every layer follow; ImageMagick decodes those.
        r.Skip(channelLengths.Sum());
    }

    /// <summary>A text layer's words and fonts, from its descriptor and its text engine data.</summary>
    private static void ReadText(byte[] block, Record rec)
    {
        // The descriptor stores the words as "Txt " TEXT: a 4-byte count of UTF-16 characters, then the characters.
        int at = block.AsSpan().IndexOf("Txt TEXT"u8);
        if (at >= 0 && at + 12 <= block.Length)
        {
            int chars = BinaryPrimitives.ReadInt32BigEndian(block.AsSpan(at + 8));
            int bytes = Math.Min(chars * 2, block.Length - at - 12);
            if (bytes > 0)
                rec.Text = Encoding.BigEndianUnicode.GetString(block, at + 12, bytes).TrimEnd('\0').Replace('\r', '\n');
        }
        // The engine data lists fonts as "/Name (" + UTF-16 with a byte-order mark + ")", with \( \) \\ escaped.
        var span = block.AsSpan();
        int from = 0;
        while (true)
        {
            int i = span[from..].IndexOf(NameMarker);
            if (i < 0)
                break;
            int start = from + i + NameMarker.Length;
            var raw = new List<byte>();
            int j = start;
            while (j < span.Length)
            {
                byte b = span[j];
                if (b == (byte)'\\' && j + 1 < span.Length)
                {
                    raw.Add(span[j + 1]);
                    j += 2;
                    continue;
                }
                // A closing parenthesis ends the string only where a character could end.
                if (b == (byte)')' && (raw.Count % 2 == 0))
                    break;
                raw.Add(b);
                j++;
            }
            var name = Encoding.BigEndianUnicode.GetString(raw.ToArray()).Trim('\0', ' ');
            // AdobeInvisFont holds invisible characters; it isn't a font anyone chose.
            if (name.Length > 0 && name != "AdobeInvisFont" && !rec.Fonts.Contains(name))
                rec.Fonts.Add(name);
            from = j;
        }
    }

    private static readonly byte[] NameMarker = [(byte)'/', (byte)'N', (byte)'a', (byte)'m', (byte)'e', (byte)' ', (byte)'(', 0xFE, 0xFF];

    /// <summary>Files placed as smart objects: embedded copies ("liFD") and links ("liFE", "liFA").</summary>
    private static void ReadLinkedFiles(byte[] block, List<PsdPlacedFile> placed)
    {
        int at = 0;
        while (at + 16 <= block.Length)
        {
            long length = (long)BinaryPrimitives.ReadUInt64BigEndian(block.AsSpan(at));
            if (length <= 0 || at + 8 + length > block.Length)
                break;
            var entry = block.AsSpan(at + 8, (int)length);
            var type = Encoding.ASCII.GetString(entry[..4]);
            try
            {
                int p = 8; // type, version
                int idLength = entry[p];
                p += 1 + idLength;
                int chars = BinaryPrimitives.ReadInt32BigEndian(entry[p..]);
                p += 4;
                var name = Encoding.BigEndianUnicode.GetString(entry.Slice(p, chars * 2)).TrimEnd('\0');
                string? fullPath = null;
                if (type != "liFD")
                {
                    // Links keep their location in a descriptor; its "fullPath" text is enough here.
                    fullPath = DescriptorText(entry, "fullPath") ?? DescriptorText(entry, "relPath");
                }
                if (name.Length > 0 && !placed.Any(f => f.Name == name && f.Path == fullPath))
                    placed.Add(new PsdPlacedFile(name, type != "liFD", fullPath));
            }
            catch (ArgumentOutOfRangeException)
            {
                // A damaged entry: skip it.
            }
            at += 8 + (int)((length + 3) & ~3L);
        }
    }

    /// <summary>A TEXT value in a descriptor: key length 0 + 4-char key, or key length + key, then "TEXT", count, UTF-16.</summary>
    private static string? DescriptorText(ReadOnlySpan<byte> data, string key)
    {
        var marker = Encoding.ASCII.GetBytes(key + "TEXT");
        int i = data.IndexOf(marker);
        if (i < 0 || i + marker.Length + 4 > data.Length)
            return null;
        int chars = BinaryPrimitives.ReadInt32BigEndian(data[(i + marker.Length)..]);
        int start = i + marker.Length + 4;
        if (chars <= 0 || start + chars * 2 > data.Length)
            return null;
        var text = Encoding.BigEndianUnicode.GetString(data.Slice(start, chars * 2)).TrimEnd('\0');
        // Photoshop writes URLs ("file:///C:/…") or plain paths.
        if (text.StartsWith("file:", StringComparison.OrdinalIgnoreCase) && Uri.TryCreate(text, UriKind.Absolute, out var uri))
            text = uri.LocalPath;
        return text;
    }

    /// <summary>Turns the flat list (bottom first, groups bracketed by start/end markers) into panel entries.</summary>
    private static List<PsdLayer> BuildLayers(List<Record> records)
    {
        var layers = new PsdLayer[records.Count];
        var groups = new Stack<(string Name, bool Hidden)>();
        // Walk from the top of the panel down: a group's own record comes before its contents.
        for (int i = records.Count - 1; i >= 0; i--)
        {
            var rec = records[i];
            var name = rec.UnicodeName is { Length: > 0 } u ? u : rec.Name;
            bool groupHidden = groups.Any(g => g.Hidden);
            var path = groups.Reverse().Select(g => g.Name).ToList();
            if (rec.Section == 3)
            {
                if (groups.Count > 0)
                    groups.Pop();
                layers[i] = new PsdLayer(i, name, PsdLayerKind.Empty, path, true, rec.Opacity, rec.Blend, 0, 0, 0, 0, -1, null, []);
                continue;
            }
            int w = rec.Right - rec.Left, h = rec.Bottom - rec.Top;
            var kind = rec.Section is 1 or 2 ? PsdLayerKind.Group
                : w <= 0 || h <= 0 ? PsdLayerKind.Empty
                : rec.IsText ? PsdLayerKind.Text
                : rec.IsSmart ? PsdLayerKind.SmartObject
                : PsdLayerKind.Pixels;
            layers[i] = new PsdLayer(i, name, kind, path, rec.Hidden || groupHidden, rec.Opacity, rec.Blend,
                rec.Left, rec.Top, Math.Max(0, w), Math.Max(0, h), -1, rec.Text, rec.Fonts);
            if (kind == PsdLayerKind.Group)
                groups.Push((name, rec.Hidden || groupHidden));
        }
        // ImageMagick returns a frame for each layer with an area, bottom first.
        int frame = 0;
        for (int i = 0; i < layers.Length; i++)
        {
            var rec = records[i];
            if (rec.Section is 0 && rec.Right - rec.Left > 0 && rec.Bottom - rec.Top > 0)
                layers[i] = layers[i] with { FrameIndex = frame++ };
        }
        return [.. layers];
    }

    private sealed class Reader(Stream stream)
    {
        private readonly byte[] _buffer = new byte[8];

        public long Position => stream.Position;

        public void Seek(long position) => stream.Position = position;

        public void Skip(long count) => stream.Seek(count, SeekOrigin.Current);

        public byte U8()
        {
            int b = stream.ReadByte();
            return b < 0 ? throw new EndOfStreamException("The Photoshop file ends early.") : (byte)b;
        }

        public ushort U16()
        {
            stream.ReadExactly(_buffer, 0, 2);
            return BinaryPrimitives.ReadUInt16BigEndian(_buffer);
        }

        public uint U32()
        {
            stream.ReadExactly(_buffer, 0, 4);
            return BinaryPrimitives.ReadUInt32BigEndian(_buffer);
        }

        public ulong U64()
        {
            stream.ReadExactly(_buffer, 0, 8);
            return BinaryPrimitives.ReadUInt64BigEndian(_buffer);
        }

        public byte[] Bytes(int count)
        {
            var bytes = new byte[count];
            stream.ReadExactly(bytes);
            return bytes;
        }

        public string Text(int count) => Encoding.ASCII.GetString(Bytes(count));

        /// <summary>Some writers pad blocks without counting the padding: step over up to 3 bytes to the next signature.</summary>
        public void AlignToSignature(long limit)
        {
            long start = stream.Position;
            Span<byte> sig = stackalloc byte[4];
            for (int pad = 0; pad < 4 && start + pad + 4 <= limit; pad++)
            {
                stream.Position = start + pad;
                stream.ReadExactly(sig);
                if (sig.SequenceEqual("8BIM"u8) || sig.SequenceEqual("8B64"u8))
                {
                    stream.Position = start + pad;
                    return;
                }
            }
            stream.Position = start;
        }
    }
}
