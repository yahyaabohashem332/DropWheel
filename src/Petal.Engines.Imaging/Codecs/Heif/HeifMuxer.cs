namespace Petal.Engines.Imaging.Heif;

/// <summary>What to put in a HEIC file: one coded picture, or a grid of equally sized tiles.</summary>
internal sealed class HeifImageSpec
{
    /// <summary>Visible image size.</summary>
    public required int Width { get; init; }

    public required int Height { get; init; }

    /// <summary>Size of each coded picture (the whole padded image, or one tile).</summary>
    public required int CodedWidth { get; init; }

    public required int CodedHeight { get; init; }

    /// <summary>Grid layout; 1×1 means a single coded image (no grid item).</summary>
    public int Columns { get; init; } = 1;

    public int Rows { get; init; } = 1;

    public required byte[] HvcC { get; init; }

    /// <summary>Coded pictures in raster order (length-prefixed NAL units).</summary>
    public required IReadOnlyList<byte[]> Pictures { get; init; }

    /// <summary>"Exif\0\0" + TIFF bytes.</summary>
    public byte[]? Exif { get; init; }

    public byte[]? Xmp { get; init; }

    public byte[]? Icc { get; init; }

    public ushort ColourPrimaries { get; init; } = 1;   // BT.709 / sRGB

    public ushort TransferCharacteristics { get; init; } = 13; // sRGB

    public ushort MatrixCoefficients { get; init; } = 1;  // BT.709: what decoders take for Windows' encoder's streams

    public bool FullRange { get; init; } = true;

    /// <summary>Counter-clockwise quarter turns stored as an 'irot' property (tests only; Petal bakes orientation).</summary>
    public int RotationQuarterTurns { get; init; }

    public bool IsGrid => Columns > 1 || Rows > 1;
}

/// <summary>
/// Writes a still-image HEIF (HEIC) container per ISO/IEC 23008-12: ftyp, meta (hdlr, dinf,
/// pitm, iloc, iinf, iref, iprp) and mdat. Large images use a grid of tiles, like iPhone photos.
/// </summary>
internal static class HeifMuxer
{
    private sealed record Item(ushort Id, string Type, bool Hidden, byte[] Data, string? ContentType = null);

    public static void Write(Stream output, HeifImageSpec spec)
    {
        int tiles = spec.Columns * spec.Rows;
        if (spec.Pictures.Count != tiles)
            throw new InvalidDataException($"Expected {tiles} coded pictures, got {spec.Pictures.Count}");

        var items = new List<Item>();
        ushort nextId = 1;
        var primary = nextId++;
        var tileIds = new List<ushort>();
        if (spec.IsGrid)
        {
            items.Add(new Item(primary, "grid", false, GridDescriptor(spec)));
            foreach (var picture in spec.Pictures)
            {
                var id = nextId++;
                tileIds.Add(id);
                items.Add(new Item(id, "hvc1", true, picture));
            }
        }
        else
        {
            items.Add(new Item(primary, "hvc1", false, spec.Pictures[0]));
        }

        ushort? exifId = null, xmpId = null;
        if (spec.Exif is { Length: > 0 } exif)
        {
            exifId = nextId++;
            // The 4-byte prefix is the offset from here to the TIFF header ("Exif\0\0" is 6 bytes).
            int tiffOffset = exif.AsSpan().StartsWith("Exif\0\0"u8) ? 6 : 0;
            var data = new byte[4 + exif.Length];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(data, (uint)tiffOffset);
            exif.CopyTo(data, 4);
            items.Add(new Item(exifId.Value, "Exif", false, data));
        }
        if (spec.Xmp is { Length: > 0 } xmp)
        {
            xmpId = nextId++;
            items.Add(new Item(xmpId.Value, "mime", false, xmp, "application/rdf+xml"));
        }

        var ftyp = Box("ftyp", w =>
        {
            w.FourCC("heic");
            w.U32(0);
            w.FourCC("mif1");
            w.FourCC("heic");
        });

        // Two passes: iloc needs absolute offsets, which depend on the size of meta itself.
        var offsets = new uint[items.Count];
        var meta = BuildMeta(spec, items, offsets, primary, tileIds, exifId, xmpId);
        uint dataStart = (uint)(ftyp.Length + meta.Length + 8);
        uint cursor = dataStart;
        for (int i = 0; i < items.Count; i++)
        {
            offsets[i] = cursor;
            cursor += (uint)items[i].Data.Length;
        }
        meta = BuildMeta(spec, items, offsets, primary, tileIds, exifId, xmpId);
        if (dataStart != ftyp.Length + meta.Length + 8)
            throw new InvalidOperationException("HEIF meta box changed size between passes");

        output.Write(ftyp);
        output.Write(meta);
        long mdatSize = 8 + items.Sum(i => (long)i.Data.Length);
        if (mdatSize > uint.MaxValue)
            throw new NotSupportedException("Image is too large for a HEIC file");
        var mw = new BigEndianWriter(output);
        mw.U32((uint)mdatSize);
        mw.FourCC("mdat");
        foreach (var item in items)
            output.Write(item.Data);
    }

    private static byte[] BuildMeta(HeifImageSpec spec, List<Item> items, uint[] offsets, ushort primary,
        List<ushort> tileIds, ushort? exifId, ushort? xmpId)
    {
        return FullBox("meta", 0, 0, w =>
        {
            w.Bytes(FullBox("hdlr", 0, 0, h =>
            {
                h.U32(0);
                h.FourCC("pict");
                h.U32(0); h.U32(0); h.U32(0);
                h.CString("");
            }));
            w.Bytes(Box("dinf", d => d.Bytes(FullBox("dref", 0, 0, r =>
            {
                r.U32(1);
                r.Bytes(FullBox("url ", 0, 1, _ => { }));
            }))));
            w.Bytes(FullBox("pitm", 0, 0, p => p.U16(primary)));
            w.Bytes(FullBox("iloc", 0, 0, l =>
            {
                l.U8(0x44); // offset_size = 4, length_size = 4
                l.U8(0x00); // base_offset_size = 0
                l.U16((ushort)items.Count);
                for (int i = 0; i < items.Count; i++)
                {
                    l.U16(items[i].Id);
                    l.U16(0); // data_reference_index: this file
                    l.U16(1); // extent_count
                    l.U32(offsets[i]);
                    l.U32((uint)items[i].Data.Length);
                }
            }));
            w.Bytes(FullBox("iinf", 0, 0, f =>
            {
                f.U16((ushort)items.Count);
                foreach (var item in items)
                {
                    f.Bytes(FullBox("infe", 2, item.Hidden ? 1u : 0u, e =>
                    {
                        e.U16(item.Id);
                        e.U16(0);
                        e.FourCC(item.Type);
                        e.CString("");
                        if (item.ContentType is not null)
                            e.CString(item.ContentType);
                    }));
                }
            }));

            if (spec.IsGrid || exifId is not null || xmpId is not null)
            {
                w.Bytes(FullBox("iref", 0, 0, r =>
                {
                    if (spec.IsGrid)
                        r.Bytes(Reference("dimg", primary, tileIds));
                    if (exifId is ushort e)
                        r.Bytes(Reference("cdsc", e, [primary]));
                    if (xmpId is ushort x)
                        r.Bytes(Reference("cdsc", x, [primary]));
                }));
            }

            w.Bytes(BuildProperties(spec, primary, tileIds));
        });
    }

    private static byte[] BuildProperties(HeifImageSpec spec, ushort primary, List<ushort> tileIds)
    {
        var properties = new List<byte[]>();
        int Add(byte[] box)
        {
            properties.Add(box);
            return properties.Count; // ipma indices are 1-based
        }

        int hvcC = Add(Box("hvcC", w => w.Bytes(spec.HvcC)));
        int codedIspe = Add(Ispe(spec.CodedWidth, spec.CodedHeight));
        int nclx = Add(Box("colr", w =>
        {
            w.FourCC("nclx");
            w.U16(spec.ColourPrimaries);
            w.U16(spec.TransferCharacteristics);
            w.U16(spec.MatrixCoefficients);
            w.U8((byte)(spec.FullRange ? 0x80 : 0));
        }));
        int prof = spec.Icc is { Length: > 0 } icc
            ? Add(Box("colr", w => { w.FourCC("prof"); w.Bytes(icc); }))
            : 0;
        int pixi = Add(FullBox("pixi", 0, 0, w => { w.U8(3); w.U8(8); w.U8(8); w.U8(8); }));
        int outputIspe = spec.IsGrid ? Add(Ispe(spec.Width, spec.Height)) : 0;

        int clap = 0;
        if (!spec.IsGrid && (spec.Width != spec.CodedWidth || spec.Height != spec.CodedHeight))
        {
            // Clean aperture anchored top-left: offsets are relative to the coded picture's centre.
            clap = Add(Box("clap", w =>
            {
                w.U32((uint)spec.Width); w.U32(1);
                w.U32((uint)spec.Height); w.U32(1);
                w.I32(spec.Width - spec.CodedWidth); w.U32(2);
                w.I32(spec.Height - spec.CodedHeight); w.U32(2);
            }));
        }
        int irot = spec.RotationQuarterTurns % 4 != 0
            ? Add(Box("irot", w => w.U8((byte)(spec.RotationQuarterTurns & 3))))
            : 0;

        var associations = new List<(ushort Item, List<(int Index, bool Essential)> Props)>();
        if (spec.IsGrid)
        {
            var gridProps = new List<(int, bool)> { (outputIspe, false), (nclx, false) };
            if (prof > 0) gridProps.Add((prof, false));
            gridProps.Add((pixi, false));
            if (irot > 0) gridProps.Add((irot, true));
            associations.Add((primary, gridProps));
            foreach (var tile in tileIds)
                associations.Add((tile, [(hvcC, true), (codedIspe, false), (nclx, false), (pixi, false)]));
        }
        else
        {
            var props = new List<(int, bool)> { (hvcC, true), (codedIspe, false), (nclx, false) };
            if (prof > 0) props.Add((prof, false));
            props.Add((pixi, false));
            // Transformative properties come last, in the order they apply.
            if (clap > 0) props.Add((clap, true));
            if (irot > 0) props.Add((irot, true));
            associations.Add((primary, props));
        }

        if (properties.Count > 127)
            throw new InvalidOperationException("Too many HEIF properties");

        return Box("iprp", w =>
        {
            w.Bytes(Box("ipco", c =>
            {
                foreach (var p in properties)
                    c.Bytes(p);
            }));
            w.Bytes(FullBox("ipma", 0, 0, m =>
            {
                m.U32((uint)associations.Count);
                foreach (var (item, props) in associations)
                {
                    m.U16(item);
                    m.U8((byte)props.Count);
                    foreach (var (index, essential) in props)
                        m.U8((byte)((essential ? 0x80 : 0) | index));
                }
            }));
        });
    }

    private static byte[] GridDescriptor(HeifImageSpec spec)
    {
        bool large = spec.Width > ushort.MaxValue || spec.Height > ushort.MaxValue;
        using var ms = new MemoryStream();
        var w = new BigEndianWriter(ms);
        w.U8(0);
        w.U8((byte)(large ? 1 : 0));
        w.U8((byte)(spec.Rows - 1));
        w.U8((byte)(spec.Columns - 1));
        if (large)
        {
            w.U32((uint)spec.Width);
            w.U32((uint)spec.Height);
        }
        else
        {
            w.U16((ushort)spec.Width);
            w.U16((ushort)spec.Height);
        }
        return ms.ToArray();
    }

    private static byte[] Ispe(int width, int height) =>
        FullBox("ispe", 0, 0, w => { w.U32((uint)width); w.U32((uint)height); });

    private static byte[] Reference(string type, ushort from, IReadOnlyList<ushort> to) => Box(type, w =>
    {
        w.U16(from);
        w.U16((ushort)to.Count);
        foreach (var id in to)
            w.U16(id);
    });

    private static byte[] Box(string type, Action<BigEndianWriter> body)
    {
        using var content = new MemoryStream();
        body(new BigEndianWriter(content));
        using var ms = new MemoryStream();
        var w = new BigEndianWriter(ms);
        w.U32((uint)(8 + content.Length));
        w.FourCC(type);
        content.Position = 0;
        content.CopyTo(ms);
        return ms.ToArray();
    }

    private static byte[] FullBox(string type, byte version, uint flags, Action<BigEndianWriter> body) => Box(type, w =>
    {
        w.U32(((uint)version << 24) | (flags & 0xFFFFFF));
        body(w);
    });
}
