using System.Globalization;
using System.Text;
using Petal.Engines.Documents.Model;
using Petal.Engines.Documents.Text;
using Petal.Engines.Documents.Util;

namespace Petal.Engines.Documents.IO;

/// <summary>
/// Writes RTF 1.9 that Word, WordPad and LibreOffice all read: a style sheet with outline
/// levels for headings, real list tables (plus \listtext fallbacks), tables and embedded pictures.
/// </summary>
internal sealed class RtfWriter
{
    private const int BulletList = 1;
    private const int NumberList = 2;

    private readonly StringBuilder _sb = new();
    private readonly List<int> _orderedOverrides = [];
    private readonly int _contentTwips = (int)(Paper.ContentWidthPt * 20);

    // Style numbers in the style sheet.
    private const int ListParagraphStyle = 20;
    private const int QuoteStyle = 21;
    private const int CodeStyle = 22;

    private static readonly (int HalfPoints, int Color, bool Italic)[] HeadingFormats =
    [
        (40, 2, false), (32, 3, false), (28, 2, false), (24, 3, true), (22, 3, false), (22, 2, true),
    ];

    public static void Write(DocModel doc, string path) =>
        File.WriteAllText(path, Render(doc), Encoding.ASCII);

    public static string Render(DocModel doc)
    {
        var w = new RtfWriter();
        var body = new StringBuilder();
        w.WriteBlocks(doc.Blocks, body, inTable: false, style: null);

        var sb = w._sb;
        sb.Append(@"{\rtf1\ansi\ansicpg1252\deff0\nouicompat\deflang1033\deflangfe1033").Append('\n');
        sb.Append(@"{\fonttbl{\f0\fswiss\fcharset0 Calibri;}{\f1\fmodern\fcharset0 Consolas;}{\f2\fswiss\fcharset0 Arial;}}").Append('\n');
        sb.Append(@"{\colortbl ;\red5\green99\blue193;\red31\green56\blue100;\red47\green84\blue150;\red191\green191\blue191;\red64\green64\blue64;}").Append('\n');
        sb.Append(@"{\stylesheet{\s0\snext0\f0\fs22\sa160 Normal;}");
        for (var i = 0; i < 6; i++)
        {
            var (fs, cf, italic) = HeadingFormats[i];
            sb.Append($@"{{\s{i + 1}\sbasedon0\snext0\keepn\outlinelevel{i}\sb240\sa120\b\f0\fs{fs}\cf{cf}{(italic ? @"\i" : "")} heading {i + 1};}}");
        }
        sb.Append($@"{{\s{ListParagraphStyle}\sbasedon0\snext{ListParagraphStyle}\li720\sa60\f0\fs22 List Paragraph;}}");
        sb.Append($@"{{\s{QuoteStyle}\sbasedon0\snext0\li567\ri567\i\cf5\f0\fs22 Quote;}}");
        sb.Append($@"{{\s{CodeStyle}\sbasedon0\snext{CodeStyle}\sa0\f1\fs20 Source Code;}}");
        sb.Append(@"{\*\cs30\ul\cf1 Hyperlink;}}").Append('\n');

        sb.Append(@"{\*\listtable");
        sb.Append(ListDefinition(BulletList, ordered: false));
        sb.Append(ListDefinition(NumberList, ordered: true));
        sb.Append("}\n");
        sb.Append(@"{\*\listoverridetable{\listoverride\listid1\listoverridecount0\ls1}");
        for (var i = 0; i < w._orderedOverrides.Count; i++)
            sb.Append($@"{{\listoverride\listid2\listoverridecount1{{\lfolevel\listoverridestartat\levelstartat{w._orderedOverrides[i]}}}\ls{i + 2}}}");
        sb.Append("}\n");

        sb.Append(@"{\info");
        AppendInfo(sb, "title", doc.EffectiveTitle());
        AppendInfo(sb, "author", doc.Metadata.Author);
        AppendInfo(sb, "subject", doc.Metadata.Subject);
        AppendInfo(sb, "keywords", doc.Metadata.Keywords);
        sb.Append("}\n");

        var (pw, ph) = Paper.PageSizePt;
        var margin = (int)(Paper.MarginPt * 20);
        sb.Append($@"\paperw{(int)(pw * 20)}\paperh{(int)(ph * 20)}\margl{margin}\margr{margin}\margt{margin}\margb{margin}\viewkind4\uc1").Append('\n');
        sb.Append(body);
        sb.Append("}\n");
        return sb.ToString();
    }

    private static void AppendInfo(StringBuilder sb, string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;
        sb.Append('{').Append('\\').Append(key).Append(' ').Append(Escape(value)).Append('}');
    }

    private static string ListDefinition(int id, bool ordered)
    {
        var sb = new StringBuilder();
        sb.Append($@"{{\list\listtemplateid{id}\listhybrid");
        string[] bullets = ["•", "◦", "▪"];
        int[] formats = [0, 4, 2]; // decimal, lower letter, lower roman
        for (var level = 0; level < 9; level++)
        {
            var indent = 720 * (level + 1);
            if (ordered)
            {
                // \leveltext: length byte, then the level placeholder, then the suffix.
                sb.Append($@"{{\listlevel\levelnfc{formats[level % 3]}\levelnfcn{formats[level % 3]}\leveljc0\leveljcn0\levelfollow0\levelstartat1");
                sb.Append($@"{{\leveltext\'02\'{level:x2}.;}}{{\levelnumbers\'01;}}\fi-360\li{indent}\lin{indent} }}");
            }
            else
            {
                var code = (int)bullets[level % 3][0];
                sb.Append(@"{\listlevel\levelnfc23\levelnfcn23\leveljc0\leveljcn0\levelfollow0\levelstartat1");
                sb.Append($@"{{\leveltext\'01\u{code} ?;}}{{\levelnumbers;}}\f0\fi-360\li{indent}\lin{indent} }}");
            }
        }
        sb.Append($@"{{\listname ;}}\listid{id}}}");
        return sb.ToString();
    }

    private void WriteBlocks(IEnumerable<Block> blocks, StringBuilder o, bool inTable, string? style)
    {
        foreach (var block in blocks)
            WriteBlock(block, o, inTable, style);
    }

    private static string ParagraphStart(bool inTable) => inTable ? @"\pard\plain\intbl" : @"\pard\plain";

    private void WriteBlock(Block block, StringBuilder o, bool inTable, string? style)
    {
        switch (block)
        {
            case Heading h:
            {
                var (fs, cf, italic) = HeadingFormats[h.Level - 1];
                o.Append(ParagraphStart(inTable)).Append($@"\s{h.Level}\keepn\outlinelevel{h.Level - 1}\sb240\sa120");
                AppendDirection(o, h.Inlines);
                o.Append($@"\b\f0\fs{fs}\cf{cf}").Append(italic ? @"\i" : "").Append(' ');
                WriteInlines(h.Inlines, o);
                o.Append(@"\par").Append('\n');
                break;
            }
            case Paragraph p:
            {
                o.Append(ParagraphStart(inTable)).Append(style ?? @"\s0\sa160\sl264\slmult1");
                o.Append(p.Alignment switch { Alignment.Center => @"\qc", Alignment.End => @"\qr", Alignment.Justify => @"\qj", _ => "" });
                AppendDirection(o, p.Inlines);
                o.Append(@"\f0\fs22 ");
                WriteInlines(p.Inlines, o);
                o.Append(@"\par").Append('\n');
                break;
            }
            case ListBlock l:
                WriteList(l, o, 0, inTable);
                break;
            case Model.Table t:
                if (inTable)
                {
                    // RTF nesting is rarely read back correctly; nested tables become tab-separated lines.
                    var text = string.Join("\n", t.Rows.Select(r => string.Join("\t", r.Cells.Select(c => c.Blocks.PlainText()))));
                    WriteBlocks([new Paragraph { Inlines = { new TextRun(text) } }], o, true, style);
                }
                else
                    WriteTable(t, o);
                break;
            case ImageBlock img:
                o.Append(ParagraphStart(inTable)).Append(@"\s0\sa160 ");
                WritePicture(img.Image, o);
                o.Append(@"\par").Append('\n');
                break;
            case CodeBlock c:
                foreach (var line in TextFile.NormalizeNewlines(c.Text).Split('\n'))
                    o.Append(ParagraphStart(inTable)).Append($@"\s{CodeStyle}\sa0\f1\fs20 ").Append(Escape(line)).Append(@"\par").Append('\n');
                break;
            case Quote q:
                WriteBlocks(q.Blocks, o, inTable, $@"\s{QuoteStyle}\li567\ri567\sa160\i\cf5");
                break;
            case HorizontalRule:
                o.Append(ParagraphStart(inTable)).Append(@"\s0\brdrb\brdrs\brdrw10\brsp20\brdrcf4\sa160\par").Append('\n');
                break;
            case PageBreak:
                if (!inTable)
                    o.Append(@"\pard\plain\s0\page").Append('\n');
                break;
        }
    }

    private static void AppendDirection(StringBuilder o, IEnumerable<Inline> inlines)
    {
        if (Bidi.IsRtlParagraph(inlines.PlainText()))
            o.Append(@"\rtlpar");
    }

    private void WriteList(ListBlock list, StringBuilder o, int level, bool inTable)
    {
        int ls;
        if (list.Ordered)
        {
            _orderedOverrides.Add(list.Start);
            ls = _orderedOverrides.Count + 1;
        }
        else
            ls = 1;

        var number = list.Start;
        var indent = 720 * (level + 1);
        foreach (var item in list.Items)
        {
            var first = true;
            foreach (var block in item.Blocks)
            {
                if (block is ListBlock nested)
                {
                    WriteList(nested, o, Math.Min(level + 1, 8), inTable);
                    first = false;
                    continue;
                }
                if (first && block is Paragraph p)
                {
                    var marker = list.Ordered ? $"{number}." : (level % 3) switch { 0 => "•", 1 => "◦", _ => "▪" };
                    o.Append(ParagraphStart(inTable)).Append($@"\s{ListParagraphStyle}\fi-360\li{indent}\sa60\ls{ls}\ilvl{level}");
                    AppendDirection(o, p.Inlines);
                    o.Append(@"\f0\fs22 {\listtext\pard\plain\f0\fs22 ").Append(Escape(marker)).Append(@"\tab}");
                    WriteInlines(p.Inlines, o);
                    o.Append(@"\par").Append('\n');
                }
                else
                    WriteBlock(block, o, inTable, $@"\s{ListParagraphStyle}\li{indent}\sa60");
                first = false;
            }
            number++;
        }
    }

    private void WriteTable(Model.Table table, StringBuilder o)
    {
        var (rows, columns) = TableLayout.Layout(table);
        columns = Math.Max(1, columns);
        var colWidth = _contentTwips / columns;
        for (var r = 0; r < rows.Count; r++)
        {
            var slots = rows[r];
            var row = new StringBuilder();
            row.Append(@"\trowd\trgaph100\trleft0");
            if (table.Rows[r].IsHeader)
                row.Append(@"\trhdr");
            // Cells: fill gaps so every row covers the grid.
            var cells = new List<(GridCell? Slot, int Column, int Span)>();
            var col = 0;
            foreach (var slot in slots)
            {
                for (; col < slot.Column; col++)
                    cells.Add((null, col, 1));
                cells.Add((slot, slot.Column, slot.ColSpan));
                col = slot.Column + slot.ColSpan;
            }
            for (; col < columns; col++)
                cells.Add((null, col, 1));

            foreach (var (slot, column, span) in cells)
            {
                if (slot is { IsContinuation: true })
                    row.Append(@"\clvmrg");
                else if (slot is { } s && s.Cell.RowSpan > 1)
                    row.Append(@"\clvmgf");
                row.Append(@"\clbrdrt\brdrs\brdrw10\brdrcf4\clbrdrl\brdrs\brdrw10\brdrcf4\clbrdrb\brdrs\brdrw10\brdrcf4\clbrdrr\brdrs\brdrw10\brdrcf4");
                row.Append(@"\cellx").Append(colWidth * (column + span));
            }
            o.Append(row).Append('\n');

            foreach (var (slot, _, _) in cells)
            {
                if (slot is { IsContinuation: false } s)
                {
                    var content = new StringBuilder();
                    var blocks = s.Cell.Blocks.Count == 0 ? [new Paragraph()] : s.Cell.Blocks;
                    WriteBlocks(blocks, content, inTable: true, style: table.Rows[r].IsHeader ? @"\s0\sa0\b" : @"\s0\sa0");
                    var text = content.ToString().TrimEnd('\n');
                    // The last paragraph of a cell ends with \cell instead of \par.
                    if (text.EndsWith(@"\par", StringComparison.Ordinal))
                        text = text[..^4];
                    o.Append(text).Append(@"\cell").Append('\n');
                }
                else
                    o.Append(@"\pard\plain\intbl\s0\sa0 \cell").Append('\n');
            }
            o.Append(row).Append(@"\row").Append('\n');
        }
        o.Append(@"\pard\plain\s0\sa160").Append('\n');
    }

    private void WriteInlines(IEnumerable<Inline> inlines, StringBuilder o)
    {
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case TextRun run:
                    WriteRun(run, o);
                    break;
                case LineBreak:
                    o.Append(@"\line ");
                    break;
                case InlineImage img:
                    WritePicture(img.Image, o);
                    break;
            }
        }
    }

    private static void WriteRun(TextRun run, StringBuilder o)
    {
        var fmt = new StringBuilder();
        if (run.Has(TextStyle.Bold)) fmt.Append(@"\b");
        if (run.Has(TextStyle.Italic)) fmt.Append(@"\i");
        if (run.Has(TextStyle.Underline) || run.Link is not null) fmt.Append(@"\ul");
        if (run.Has(TextStyle.Strike)) fmt.Append(@"\strike");
        if (run.Has(TextStyle.Superscript)) fmt.Append(@"\super");
        if (run.Has(TextStyle.Subscript)) fmt.Append(@"\sub");
        if (run.Has(TextStyle.Code)) fmt.Append(@"\f1");
        if (run.Link is not null) fmt.Append(@"\cf1");
        if (Bidi.ContainsRtl(run.Text)) fmt.Append(@"\rtlch");

        var text = Escape(run.Text);
        if (run.Link is not null)
        {
            o.Append(@"{\field{\*\fldinst{HYPERLINK """).Append(Escape(run.Link.Replace("\"", "%22"))).Append(@"""}}{\fldrslt{");
            o.Append(fmt).Append(' ').Append(text).Append("}}}");
        }
        else if (fmt.Length > 0)
            o.Append('{').Append(fmt).Append(' ').Append(text).Append('}');
        else
            o.Append(text);
    }

    private void WritePicture(ImageData image, StringBuilder o)
    {
        var (w, h) = image.FitSize(Paper.ContentWidthPt, Paper.ContentHeightPt);
        o.Append(@"{\pict").Append(image.IsJpeg ? @"\jpegblip" : @"\pngblip");
        o.Append(@"\picw").Append(image.PixelWidth).Append(@"\pich").Append(image.PixelHeight);
        o.Append(@"\picwgoal").Append((int)(w * 20)).Append(@"\pichgoal").Append((int)(h * 20)).Append('\n');
        var hex = Convert.ToHexString(image.Bytes).ToLowerInvariant();
        for (var i = 0; i < hex.Length; i += 128)
            o.Append(hex, i, Math.Min(128, hex.Length - i)).Append('\n');
        o.Append('}');
    }

    /// <summary>Escapes RTF specials and writes everything outside ASCII as \uN? (signed 16-bit units).</summary>
    public static string Escape(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            switch (c)
            {
                case '\\': sb.Append(@"\\"); break;
                case '{': sb.Append(@"\{"); break;
                case '}': sb.Append(@"\}"); break;
                case '\t': sb.Append(@"\tab "); break;
                case '\n': sb.Append(@"\line "); break;
                case '\r': break;
                case ' ': sb.Append(@"\~"); break;
                default:
                    if (c < 0x20)
                        break;
                    if (c < 0x80)
                        sb.Append(c);
                    else
                        sb.Append(@"\u").Append(((short)c).ToString(CultureInfo.InvariantCulture)).Append('?');
                    break;
            }
        }
        return sb.ToString();
    }
}
