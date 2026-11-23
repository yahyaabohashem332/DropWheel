using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;
using Petal.Engines.Documents.Model;

namespace Petal.Engines.Documents.IO;

/// <summary>
/// Reads Kindle books without DRM: MOBI (Mobipocket, "KF7") and AZW3 ("KF8"), and the combined files KindleGen
/// writes, whose KF8 part is read. The book is a Palm database: record 0 holds the headers and the metadata (EXTH),
/// the next ones its text, compressed (PalmDOC's LZ77 or HUFF/CDIC), then its pictures. A KF8 book's text is its
/// XHTML files cut into a skeleton and fragments, which two index tables put back together.
/// </summary>
internal static partial class Mobi
{
    private sealed class Book(byte[] file, int[] offsets)
    {
        public int Count => offsets.Length;

        public ReadOnlySpan<byte> Record(int index)
        {
            if (index < 0 || index >= offsets.Length)
                throw new InvalidDataException("The book is cut short.");
            var end = index + 1 < offsets.Length ? offsets[index + 1] : file.Length;
            var start = offsets[index];
            if (start > end || end > file.Length)
                throw new InvalidDataException("The book is cut short.");
            return file.AsSpan(start, end - start);
        }
    }

    /// <summary>The headers of one part of the book (a MOBI, or the KF8 half of a combined file).</summary>
    private sealed class Header
    {
        public required int Base { get; init; }
        public required int Compression { get; init; }
        public required int TextLength { get; init; }
        public required int TextRecords { get; init; }
        public required Encoding Encoding { get; init; }
        public required int Version { get; init; }
        public required int FirstResource { get; init; }
        public required int HuffRecord { get; init; }
        public required int HuffCount { get; init; }
        public required int ExtraFlags { get; init; }
        public required int Fdst { get; init; }
        public required int Fragments { get; init; }
        public required int Skeletons { get; init; }
        public required string Title { get; init; }
        public required Dictionary<int, List<byte[]>> Exth { get; init; }
    }

    private const int None = unchecked((int)0xFFFFFFFF);

    public static DocModel Read(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < 86)
            throw new InvalidDataException("This is not a Kindle book.");
        var type = Encoding.ASCII.GetString(bytes, 60, 8);
        if (bytes.AsSpan(0, 4).SequenceEqual("CONT"u8))
            throw new NotSupportedException("This is a KFX book, Amazon's newest format; DropWheel reads MOBI and AZW3.");
        if (type is not ("BOOKMOBI" or "TEXtREAd"))
            throw new InvalidDataException("This is not a Kindle book.");
        int count = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(76));
        var offsets = new int[count];
        for (var i = 0; i < count; i++)
            offsets[i] = (int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(78 + i * 8));
        var book = new Book(bytes, offsets);

        var header = ReadHeader(book, 0);
        // A combined file: its KF8 half starts after the boundary record EXTH 121 names.
        if (header.Version < 8 && header.Exth.TryGetValue(121, out var boundary) && boundary[0].Length >= 4)
        {
            var kf8 = (int)BinaryPrimitives.ReadUInt32BigEndian(boundary[0]);
            if (kf8 > 0 && kf8 < book.Count)
            {
                try
                {
                    var kf8Header = ReadHeader(book, kf8);
                    if (kf8Header.Version >= 8)
                        header = kf8Header;
                }
                catch (InvalidDataException)
                {
                    // The KF7 part is whole on its own.
                }
            }
        }

        var text = Text(book, header);
        var doc = new DocModel();
        doc.Metadata.Title = First(header.Exth, 503, header.Encoding) ?? (header.Title.Length > 0 ? header.Title : null);
        doc.Metadata.Author = All(header.Exth, 100, header.Encoding);
        doc.Metadata.Subject = First(header.Exth, 103, header.Encoding) is { } description ? StripTags(description) : null;
        doc.Metadata.Keywords = All(header.Exth, 105, header.Encoding);
        doc.Metadata.Language = First(header.Exth, 524, header.Encoding);

        byte[]? Resource(int number)
        {
            var index = header.FirstResource + number - 1;
            return number >= 1 && header.FirstResource != None && index < book.Count ? book.Record(index).ToArray() : null;
        }

        if (header.Version >= 8)
        {
            var first = true;
            foreach (var part in Parts(book, header, text))
            {
                var html = KindleLinks().Replace(Encoding.UTF8.GetString(part), m => m.Groups[1].Value == "embed"
                    ? "petal-resource:" + Base32(m.Groups[2].Value)
                    : "#");
                var chapter = new DocModel();
                HtmlReader.ParseInto(html, chapter, src => src.StartsWith("petal-resource:", StringComparison.Ordinal)
                    && int.TryParse(src["petal-resource:".Length..], out var n) ? Resource(n) : null);
                if (chapter.Blocks.Count == 0)
                    continue;
                if (!first)
                    doc.Blocks.Add(new PageBreak());
                first = false;
                doc.Blocks.AddRange(chapter.Blocks);
            }
        }
        else
        {
            var html = header.Encoding.GetString(text);
            html = PageBreakTag().Replace(html, "<div style=\"page-break-before: always\"></div>");
            html = RecIndex().Replace(html, m => " src=\"petal-resource:" + int.Parse(m.Groups[1].Value) + "\"");
            // Mobipocket closes line breaks ("<br></br>"), which an HTML reader takes for two.
            html = Regex.Replace(html, @"</br\s*>", "", RegexOptions.IgnoreCase);
            html = FontHeadings(html);
            var model = HtmlReader.Parse(html, src => src.StartsWith("petal-resource:", StringComparison.Ordinal)
                && int.TryParse(src["petal-resource:".Length..], out var n) ? Resource(n) : null);
            doc.Blocks.AddRange(model.Blocks);
        }
        // A page break first or last says nothing.
        while (doc.Blocks.Count > 0 && doc.Blocks[0] is PageBreak)
            doc.Blocks.RemoveAt(0);
        while (doc.Blocks.Count > 0 && doc.Blocks[^1] is PageBreak)
            doc.Blocks.RemoveAt(doc.Blocks.Count - 1);

        // The cover (EXTH 201, counted from the first picture) opens the book, unless the text already shows it.
        if (header.Exth.TryGetValue(201, out var cover) && cover[0].Length >= 4
            && Resource((int)BinaryPrimitives.ReadUInt32BigEndian(cover[0]) + 1) is { } coverBytes
            && ImageData.Create(coverBytes, "Cover") is { } coverImage
            && !doc.AllImages().Any(i => i.Bytes.AsSpan().SequenceEqual(coverImage.Bytes)))
        {
            doc.Blocks.Insert(0, new ImageBlock(coverImage));
            if (doc.Blocks.Count > 1)
                doc.Blocks.Insert(1, new PageBreak());
        }
        return doc;
    }

    /// <summary>
    /// Mobipocket files mark titles by type size, not as headings: a paragraph that is nothing but words in
    /// &lt;font size="4"&gt; to "7" (and line breaks, bold, italics) becomes a heading, 7 the first level, 4 the fourth.
    /// </summary>
    internal static string FontHeadings(string html) => FontParagraph().Replace(html, m =>
    {
        var inner = m.Groups[2].Value;
        var sizes = FontSize().Matches(inner).Select(f => int.Parse(f.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)).ToList();
        if (sizes.Count == 0 || sizes.Min() < 4)
            return m.Value;
        // Only words inside the fonts, and few of them: nothing else is a heading's.
        var bare = HeadingMarkup().Replace(inner, "");
        var words = StripTags(bare).Trim();
        if (FontRun().Replace(bare, "").Trim().Length > 0 || words.Length == 0 || words.Length > 200)
            return m.Value;
        var level = Math.Clamp(8 - sizes.Max(), 1, 6);
        var content = FontTag().Replace(inner, "");
        return $"<h{level}>{content}</h{level}>";
    });

    private static Header ReadHeader(Book book, int at)
    {
        var record0 = book.Record(at).ToArray();
        ReadOnlySpan<byte> r0 = record0;
        if (r0.Length < 24)
            throw new InvalidDataException("This is not a Kindle book.");
        int compression = BinaryPrimitives.ReadUInt16BigEndian(r0);
        var textLength = (int)BinaryPrimitives.ReadUInt32BigEndian(r0[4..]);
        int records = BinaryPrimitives.ReadUInt16BigEndian(r0[8..]);
        int encryption = BinaryPrimitives.ReadUInt16BigEndian(r0[12..]);
        if (encryption != 0)
            throw new NotSupportedException("This book is protected by DRM, which DropWheel does not remove.");
        var hasMobi = r0.Length >= 20 && r0[16..20].SequenceEqual("MOBI"u8);
        if (!hasMobi)
        {
            // A plain PalmDOC: text only, 8-bit.
            Petal.Engines.Documents.Text.TextFile.EnsureCodePages();
            return new Header
            {
                Base = at, Compression = compression, TextLength = textLength, TextRecords = records, Encoding = Encoding.GetEncoding(1252),
                Version = 0, FirstResource = None, HuffRecord = None, HuffCount = 0, ExtraFlags = 0, Fdst = None, Fragments = None,
                Skeletons = None, Title = "", Exth = [],
            };
        }
        var mobiLength = (int)BinaryPrimitives.ReadUInt32BigEndian(r0[20..]);
        uint U32(int offset) => offset + 4 <= record0.Length && offset + 4 <= 16 + mobiLength ? BinaryPrimitives.ReadUInt32BigEndian(record0.AsSpan(offset)) : 0xFFFFFFFF;
        var codepage = U32(28);
        Petal.Engines.Documents.Text.TextFile.EnsureCodePages();
        var encoding = codepage == 65001 ? Encoding.UTF8 : Encoding.GetEncoding(1252);
        // The file version: 6 for MOBI, 8 for KF8.
        var version = (int)U32(36);
        var titleOffset = (int)U32(84);
        var titleLength = (int)U32(88);
        var title = titleOffset > 0 && titleOffset + titleLength <= r0.Length ? encoding.GetString(r0.Slice(titleOffset, titleLength)) : "";
        var extraFlags = 16 + mobiLength >= 0xF4 && r0.Length >= 0xF4 ? BinaryPrimitives.ReadUInt16BigEndian(r0[0xF2..]) : 0;
        var exth = new Dictionary<int, List<byte[]>>();
        if ((U32(0x80) & 0x40) != 0 && 16 + mobiLength + 12 <= r0.Length && r0.Slice(16 + mobiLength, 4).SequenceEqual("EXTH"u8))
        {
            var e = r0[(16 + mobiLength)..];
            var n = (int)BinaryPrimitives.ReadUInt32BigEndian(e[8..]);
            var pos = 12;
            for (var i = 0; i < n && pos + 8 <= e.Length; i++)
            {
                var kind = (int)BinaryPrimitives.ReadUInt32BigEndian(e[pos..]);
                var size = (int)BinaryPrimitives.ReadUInt32BigEndian(e[(pos + 4)..]);
                if (size < 8 || pos + size > e.Length)
                    break;
                (exth.TryGetValue(kind, out var list) ? list : exth[kind] = []).Add(e.Slice(pos + 8, size - 8).ToArray());
                pos += size;
            }
        }
        int Relative(int offset)
        {
            var value = U32(offset);
            return value == 0xFFFFFFFF || value == 0 && offset is not (0x6C) ? None : (int)value + at;
        }
        return new Header
        {
            Base = at,
            Compression = compression,
            TextLength = textLength,
            TextRecords = records,
            Encoding = encoding,
            Version = version,
            FirstResource = Relative(0x6C),
            HuffRecord = Relative(0x70),
            HuffCount = (int)(U32(0x74) == 0xFFFFFFFF ? 0 : U32(0x74)),
            ExtraFlags = extraFlags,
            Fdst = version >= 8 ? Relative(0xC0) : None,
            Fragments = version >= 8 ? Relative(0xF8) : None,
            Skeletons = version >= 8 ? Relative(0xFC) : None,
            Title = title,
            Exth = exth,
        };
    }

    private static string? First(Dictionary<int, List<byte[]>> exth, int kind, Encoding encoding) =>
        exth.TryGetValue(kind, out var values) ? NullIfBlank(encoding.GetString(values[0])) : null;

    private static string? All(Dictionary<int, List<byte[]>> exth, int kind, Encoding encoding) =>
        exth.TryGetValue(kind, out var values) ? NullIfBlank(string.Join(", ", values.Select(v => encoding.GetString(v).Trim()).Where(v => v.Length > 0))) : null;

    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim().TrimEnd('\0');

    private static string StripTags(string html) => System.Net.WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]+>", " ")).Trim();

    /// <summary>The text records, their trailing entries cut off, uncompressed and joined.</summary>
    private static byte[] Text(Book book, Header header)
    {
        Huffcdic? huff = null;
        if (header.Compression == 17480)
        {
            if (header.HuffRecord == None || header.HuffCount < 2)
                throw new InvalidDataException("The book's dictionary is missing.");
            huff = new Huffcdic(book.Record(header.HuffRecord));
            for (var i = 1; i < header.HuffCount; i++)
                huff.AddDictionary(book.Record(header.HuffRecord + i));
        }
        else if (header.Compression is not (1 or 2))
            throw new NotSupportedException("This book's compression is not one DropWheel knows.");

        using var output = new MemoryStream(header.TextLength);
        for (var i = 1; i <= header.TextRecords && header.Base + i < book.Count; i++)
        {
            var record = book.Record(header.Base + i);
            record = record[..(record.Length - TrailingSize(record, header.ExtraFlags))];
            switch (header.Compression)
            {
                case 1: output.Write(record); break;
                case 2: output.Write(PalmDoc(record)); break;
                default: output.Write(huff!.Unpack(record.ToArray())); break;
            }
        }
        var text = output.ToArray();
        return text.Length > header.TextLength && header.TextLength > 0 ? text[..header.TextLength] : text;
    }

    /// <summary>The size of the entries KindleGen leaves at a text record's end (index data, a multibyte overlap).</summary>
    internal static int TrailingSize(ReadOnlySpan<byte> record, int flags)
    {
        var size = 0;
        for (var bits = flags >> 1; bits != 0; bits >>= 1)
        {
            if ((bits & 1) == 0)
                continue;
            // A value written backwards, 7 bits a byte, its first byte marked.
            int value = 0, shift = 0, end = record.Length - size;
            while (end > 0)
            {
                var b = record[end - 1];
                value |= (b & 0x7F) << shift;
                shift += 7;
                end--;
                if ((b & 0x80) != 0 || shift >= 28)
                    break;
            }
            size += value;
        }
        if ((flags & 1) != 0 && record.Length - size - 1 >= 0)
            size += (record[record.Length - size - 1] & 0x3) + 1;
        return Math.Min(size, record.Length);
    }

    /// <summary>PalmDOC's compression: literal bytes, runs to copy from before, and a space folded into the next byte.</summary>
    internal static byte[] PalmDoc(ReadOnlySpan<byte> data)
    {
        var output = new List<byte>(data.Length * 2);
        for (var i = 0; i < data.Length;)
        {
            int c = data[i++];
            if (c is >= 1 and <= 8)
            {
                for (var k = 0; k < c && i < data.Length; k++)
                    output.Add(data[i++]);
            }
            else if (c < 0x80)
                output.Add((byte)c);
            else if (c >= 0xC0)
            {
                output.Add((byte)' ');
                output.Add((byte)(c ^ 0x80));
            }
            else if (i < data.Length)
            {
                c = (c << 8) | data[i++];
                var distance = (c >> 3) & 0x07FF;
                var length = (c & 7) + 3;
                if (distance == 0 || distance > output.Count)
                    continue;
                for (var k = 0; k < length; k++)
                    output.Add(output[^distance]);
            }
        }
        return [.. output];
    }

    /// <summary>Amazon's HUFF/CDIC compression: a Huffman code whose symbols are phrases of a dictionary, themselves coded.</summary>
    private sealed class Huffcdic
    {
        private readonly (int Length, bool Terminal, ulong Max)[] _table1 = new (int, bool, ulong)[256];
        private readonly ulong[] _min = new ulong[33];
        private readonly ulong[] _max = new ulong[33];
        private readonly List<(byte[] Phrase, bool Done)?> _dictionary = [];

        public Huffcdic(ReadOnlySpan<byte> huff)
        {
            if (!huff[..8].SequenceEqual("HUFF\0\0\0\x18"u8))
                throw new InvalidDataException("The book's dictionary is damaged.");
            var off1 = (int)BinaryPrimitives.ReadUInt32BigEndian(huff[8..]);
            var off2 = (int)BinaryPrimitives.ReadUInt32BigEndian(huff[12..]);
            for (var i = 0; i < 256; i++)
            {
                var v = BinaryPrimitives.ReadUInt32BigEndian(huff[(off1 + i * 4)..]);
                var length = (int)(v & 0x1F);
                var max = (((ulong)(v >> 8) + 1) << (32 - length)) - 1;
                _table1[i] = (length, (v & 0x80) != 0, max);
            }
            for (var length = 1; length <= 32; length++)
            {
                var min = BinaryPrimitives.ReadUInt32BigEndian(huff[(off2 + (length - 1) * 8)..]);
                var max = BinaryPrimitives.ReadUInt32BigEndian(huff[(off2 + (length - 1) * 8 + 4)..]);
                _min[length] = (ulong)min << (32 - length);
                _max[length] = (((ulong)max + 1) << (32 - length)) - 1;
            }
        }

        public void AddDictionary(ReadOnlySpan<byte> cdic)
        {
            if (!cdic[..8].SequenceEqual("CDIC\0\0\0\x10"u8))
                throw new InvalidDataException("The book's dictionary is damaged.");
            var phrases = (int)BinaryPrimitives.ReadUInt32BigEndian(cdic[8..]);
            var bits = (int)BinaryPrimitives.ReadUInt32BigEndian(cdic[12..]);
            var n = Math.Min(1 << bits, phrases - _dictionary.Count);
            for (var i = 0; i < n; i++)
            {
                int offset = BinaryPrimitives.ReadUInt16BigEndian(cdic[(16 + i * 2)..]);
                int length = BinaryPrimitives.ReadUInt16BigEndian(cdic[(16 + offset)..]);
                _dictionary.Add((cdic.Slice(18 + offset, length & 0x7FFF).ToArray(), (length & 0x8000) != 0));
            }
        }

        public byte[] Unpack(byte[] data)
        {
            var output = new List<byte>(data.Length * 3);
            Unpack(data, output, 0);
            return [.. output];
        }

        private void Unpack(byte[] data, List<byte> output, int depth)
        {
            if (depth > 32)
                throw new InvalidDataException("The book's dictionary is damaged.");
            var padded = new byte[data.Length + 8];
            data.CopyTo(padded, 0);
            long bitsLeft = data.Length * 8L;
            var pos = 0;
            var x = BinaryPrimitives.ReadUInt64BigEndian(padded);
            var n = 32;
            while (true)
            {
                if (n <= 0)
                {
                    pos += 4;
                    x = BinaryPrimitives.ReadUInt64BigEndian(padded.AsSpan(pos));
                    n += 32;
                }
                var code = (x >> n) & 0xFFFFFFFF;
                var (length, terminal, max) = _table1[code >> 24];
                if (!terminal)
                {
                    while (length < 32 && code < _min[length])
                        length++;
                    max = _max[length];
                }
                n -= length;
                bitsLeft -= length;
                if (bitsLeft < 0 || length == 0)
                    break;
                var r = (int)((max - code) >> (32 - length));
                if (r < 0 || r >= _dictionary.Count || _dictionary[r] is not { } entry)
                    throw new InvalidDataException("The book's dictionary is damaged.");
                if (!entry.Done)
                {
                    // A phrase coded itself: unpacked once, then kept.
                    _dictionary[r] = null;
                    var inner = new List<byte>();
                    Unpack(entry.Phrase, inner, depth + 1);
                    entry = ([.. inner], true);
                    _dictionary[r] = entry;
                }
                output.AddRange(entry.Phrase);
            }
        }
    }

    // ---- KF8 ----

    /// <summary>The XHTML files of a KF8 book, in reading order: each skeleton with its fragments put back where they go.</summary>
    private static List<byte[]> Parts(Book book, Header header, byte[] text)
    {
        // The first flow is the XHTML; the others are style sheets and drawings.
        var flowEnd = text.Length;
        if (header.Fdst != None && header.Fdst < book.Count)
        {
            var fdst = book.Record(header.Fdst);
            if (fdst.Length >= 20 && fdst[..4].SequenceEqual("FDST"u8))
                flowEnd = Math.Min(text.Length, (int)BinaryPrimitives.ReadUInt32BigEndian(fdst[16..]));
        }
        if (header.Skeletons == None || header.Fragments == None)
            return [text[..flowEnd]];

        var skeletons = IndexEntries(book, header.Skeletons).Select(e => (Fragments: e.Tags.GetValueOrDefault(1)?[0] ?? 0,
            Start: e.Tags.GetValueOrDefault(6)?[0] ?? 0, Length: e.Tags.GetValueOrDefault(6)?.ElementAtOrDefault(1) ?? 0)).ToList();
        var fragments = IndexEntries(book, header.Fragments).Select(e => (Insert: int.TryParse(Encoding.ASCII.GetString(e.Name), out var p) ? p : 0,
            Length: e.Tags.GetValueOrDefault(6)?.ElementAtOrDefault(1) ?? 0)).ToList();

        var parts = new List<byte[]>();
        var next = 0;
        foreach (var skeleton in skeletons)
        {
            if (skeleton.Start + skeleton.Length > text.Length)
                break;
            var part = new List<byte>(text.AsSpan(skeleton.Start, skeleton.Length).ToArray());
            var from = skeleton.Start + skeleton.Length;
            for (var i = 0; i < skeleton.Fragments && next < fragments.Count; i++, next++)
            {
                var (insert, length) = fragments[next];
                if (from + length > text.Length)
                    break;
                var at = Math.Clamp(insert - skeleton.Start, 0, part.Count);
                part.InsertRange(at, text.AsSpan(from, length).ToArray());
                from += length;
            }
            parts.Add([.. part]);
        }
        return parts;
    }

    private sealed record IndexEntry(byte[] Name, Dictionary<int, List<int>> Tags);

    /// <summary>An INDX table: its header record says how many records of entries follow, and the tags each entry has.</summary>
    private static List<IndexEntry> IndexEntries(Book book, int index)
    {
        var entries = new List<IndexEntry>();
        var main = book.Record(index);
        if (main.Length < 0xC0 || !main[..4].SequenceEqual("INDX"u8))
            return entries;
        var headerLength = (int)BinaryPrimitives.ReadUInt32BigEndian(main[4..]);
        var count = (int)BinaryPrimitives.ReadUInt32BigEndian(main[24..]);
        // The TAGX section: per tag, (tag, values per entry, mask, end of control byte).
        var tagx = main[headerLength..];
        if (tagx.Length < 12 || !tagx[..4].SequenceEqual("TAGX"u8))
            return entries;
        var firstEntry = (int)BinaryPrimitives.ReadUInt32BigEndian(tagx[4..]);
        var controlBytes = (int)BinaryPrimitives.ReadUInt32BigEndian(tagx[8..]);
        var tags = new List<(int Tag, int PerEntry, int Mask, bool End)>();
        for (var i = 12; i + 4 <= firstEntry && i + 4 <= tagx.Length; i += 4)
            tags.Add((tagx[i], tagx[i + 1], tagx[i + 2], tagx[i + 3] == 1));

        for (var r = index + 1; r <= index + count && r < book.Count; r++)
        {
            var data = book.Record(r);
            if (data.Length < 28 || !data[..4].SequenceEqual("INDX"u8))
                continue;
            var idxt = (int)BinaryPrimitives.ReadUInt32BigEndian(data[20..]);
            var n = (int)BinaryPrimitives.ReadUInt32BigEndian(data[24..]);
            var positions = new List<int>();
            for (var j = 0; j < n; j++)
                positions.Add(BinaryPrimitives.ReadUInt16BigEndian(data[(idxt + 4 + 2 * j)..]));
            positions.Add(idxt);
            for (var j = 0; j < n; j++)
            {
                var start = positions[j];
                int nameLength = data[start];
                var name = data.Slice(start + 1, nameLength).ToArray();
                entries.Add(new IndexEntry(name, TagMap(data, start + 1 + nameLength, controlBytes, tags)));
            }
        }
        return entries;
    }

    private static Dictionary<int, List<int>> TagMap(ReadOnlySpan<byte> data, int start, int controlBytes, List<(int Tag, int PerEntry, int Mask, bool End)> table)
    {
        var found = new List<(int Tag, int? Count, int? Bytes, int PerEntry)>();
        var control = 0;
        var at = start + controlBytes;
        foreach (var (tag, perEntry, mask, end) in table)
        {
            if (end)
            {
                control++;
                continue;
            }
            var value = data[start + control] & mask;
            if (value == 0)
                continue;
            if (value == mask)
            {
                if (System.Numerics.BitOperations.PopCount((uint)mask) > 1)
                {
                    var (used, length) = Forward(data, at);
                    at += used;
                    found.Add((tag, null, length, perEntry));
                }
                else
                    found.Add((tag, 1, null, perEntry));
            }
            else
            {
                var m = mask;
                while ((m & 1) == 0)
                {
                    m >>= 1;
                    value >>= 1;
                }
                found.Add((tag, value, null, perEntry));
            }
        }
        var map = new Dictionary<int, List<int>>();
        foreach (var (tag, count, bytes, perEntry) in found)
        {
            var values = new List<int>();
            if (count is { } c)
            {
                for (var k = 0; k < c * perEntry; k++)
                {
                    var (used, v) = Forward(data, at);
                    at += used;
                    values.Add(v);
                }
            }
            else
            {
                for (var consumed = 0; consumed < bytes!.Value;)
                {
                    var (used, v) = Forward(data, at);
                    at += used;
                    consumed += used;
                    values.Add(v);
                }
            }
            map[tag] = values;
        }
        return map;
    }

    /// <summary>A number written 7 bits a byte, its last byte marked.</summary>
    private static (int Used, int Value) Forward(ReadOnlySpan<byte> data, int at)
    {
        int value = 0, used = 0;
        while (at + used < data.Length)
        {
            var b = data[at + used++];
            value = (value << 7) | (b & 0x7F);
            if ((b & 0x80) != 0)
                break;
        }
        return (used, value);
    }

    /// <summary>Kindle's base 32 (0–9, A–V).</summary>
    private static int Base32(string digits)
    {
        var value = 0;
        foreach (var c in digits.ToUpperInvariant())
            value = value * 32 + (c <= '9' ? c - '0' : c - 'A' + 10);
        return value;
    }

    [GeneratedRegex(@"kindle:(embed|flow|pos):([0-9A-Va-v]+)[^""')\s]*")]
    private static partial Regex KindleLinks();

    [GeneratedRegex(@"<(p|div)\b[^>]*>(.*?)</\1>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex FontParagraph();

    [GeneratedRegex(@"<font\b[^>]*\bsize\s*=\s*[""']?(\d)[""']?[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex FontSize();

    [GeneratedRegex(@"<font\b[^>]*>[^<]*</font>", RegexOptions.IgnoreCase)]
    private static partial Regex FontRun();

    [GeneratedRegex(@"</?font\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex FontTag();

    [GeneratedRegex(@"</?(?:br|b|i|u|strong|em)\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex HeadingMarkup();

    [GeneratedRegex(@"<mbp:pagebreak\s*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex PageBreakTag();

    [GeneratedRegex(@"\s(?:hi)?recindex\s*=\s*[""']?(\d+)[""']?", RegexOptions.IgnoreCase)]
    private static partial Regex RecIndex();
}
