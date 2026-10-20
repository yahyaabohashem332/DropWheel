using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Tables;
using MigraDoc.Rendering;
using Petal.Engines.Documents.Model;
using Petal.Engines.Documents.Text;
using Petal.Engines.Documents.Util;
using M = Petal.Engines.Documents.Model;
using MDoc = MigraDoc.DocumentObjectModel;

namespace Petal.Engines.Documents.Pdf;

/// <summary>Lays the document model out as a PDF with real, selectable text (MigraDoc).</summary>
internal sealed class PdfDocWriter
{
    private static readonly double[] HeadingSizes = [20, 16, 14, 12, 11, 11];
    private static readonly MDoc.Color HeadingColor = MDoc.Color.FromRgb(0x1F, 0x38, 0x64);
    private static readonly MDoc.Color LinkColor = MDoc.Color.FromRgb(0x05, 0x63, 0xC1);
    private static readonly MDoc.Color RuleColor = MDoc.Color.FromRgb(0xBF, 0xBF, 0xBF);

    private const double BodySize = 11;
    private readonly double _contentWidth = Paper.ContentWidthPt;

    public static void Write(DocModel doc, string path)
    {
        PdfFonts.EnsureInitialized();
        var document = new PdfDocWriter().Build(doc);
        var renderer = new PdfDocumentRenderer { Document = document };
        renderer.RenderDocument();
        renderer.PdfDocument.Save(path);
    }

    internal static MDoc.Document NewDocument(DocMetadata metadata, string? title)
    {
        var document = new MDoc.Document();
        document.Info.Title = title ?? "";
        document.Info.Author = metadata.Author ?? "";
        document.Info.Subject = metadata.Subject ?? "";
        document.Info.Keywords = metadata.Keywords ?? "";

        var normal = document.Styles[StyleNames.Normal]!;
        normal.Font.Name = PdfFonts.Body;
        normal.Font.Size = BodySize;
        normal.ParagraphFormat.SpaceAfter = 6;
        normal.ParagraphFormat.LineSpacingRule = LineSpacingRule.Multiple;
        normal.ParagraphFormat.LineSpacing = 1.1;

        for (var i = 0; i < 6; i++)
        {
            var style = document.Styles[$"Heading{i + 1}"]!;
            style.Font.Name = PdfFonts.Body;
            style.Font.Size = HeadingSizes[i];
            style.Font.Bold = true;
            style.Font.Italic = i is 3 or 5;
            style.Font.Color = HeadingColor;
            style.ParagraphFormat.SpaceBefore = i == 0 ? 18 : 12;
            style.ParagraphFormat.SpaceAfter = 6;
            style.ParagraphFormat.KeepWithNext = true;
            style.ParagraphFormat.OutlineLevel = (OutlineLevel)i;
        }

        var code = document.Styles.AddStyle("Code", StyleNames.Normal);
        code.Font.Name = PdfFonts.Mono;
        code.Font.Size = 9;
        code.ParagraphFormat.Shading.Color = MDoc.Color.FromRgb(0xF5, 0xF5, 0xF5);
        code.ParagraphFormat.LineSpacing = 1.0;

        var quote = document.Styles.AddStyle("Quote", StyleNames.Normal);
        quote.Font.Italic = true;
        quote.Font.Color = MDoc.Color.FromRgb(0x40, 0x40, 0x40);
        quote.ParagraphFormat.LeftIndent = 14;
        quote.ParagraphFormat.Borders.Left.Width = 2;
        quote.ParagraphFormat.Borders.Left.Color = MDoc.Color.FromRgb(0xD0, 0xD0, 0xD0);
        quote.ParagraphFormat.Borders.DistanceFromLeft = 6;

        var cell = document.Styles.AddStyle("TableText", StyleNames.Normal);
        cell.Font.Size = 10;
        cell.ParagraphFormat.SpaceAfter = 0;
        return document;
    }

    internal static Section AddSection(MDoc.Document document, bool landscape = false)
    {
        var section = document.AddSection();
        var (w, h) = Paper.PageSizePt;
        section.PageSetup.PageWidth = Unit.FromPoint(landscape ? h : w);
        section.PageSetup.PageHeight = Unit.FromPoint(landscape ? w : h);
        section.PageSetup.TopMargin = Unit.FromPoint(Paper.MarginPt);
        section.PageSetup.BottomMargin = Unit.FromPoint(Paper.MarginPt);
        section.PageSetup.LeftMargin = Unit.FromPoint(Paper.MarginPt);
        section.PageSetup.RightMargin = Unit.FromPoint(Paper.MarginPt);
        return section;
    }

    private MDoc.Document Build(DocModel doc)
    {
        var document = NewDocument(doc.Metadata, doc.EffectiveTitle());
        var section = AddSection(document);
        var lastWasBreak = true;
        foreach (var block in doc.Blocks)
        {
            if (block is M.PageBreak)
            {
                if (!lastWasBreak)
                    section.AddPageBreak();
                lastWasBreak = true;
                continue;
            }
            AddBlock(section.Elements, block, 0, null);
            lastWasBreak = false;
        }
        if (section.Elements.Count == 0)
            section.AddParagraph();
        return document;
    }

    /// <summary>Adds a block to a container (section or cell). <paramref name="indent"/> is the list/quote indent in points.</summary>
    private void AddBlock(DocumentElements target, Block block, double indent, string? style)
    {
        switch (block)
        {
            case Heading h:
            {
                var p = target.AddParagraph();
                p.Style = $"Heading{h.Level}";
                p.Format.LeftIndent = indent;
                AddInlines(p, h.Inlines, HeadingSizes[h.Level - 1], bold: true, italic: h.Level is 4 or 6, width: _contentWidth - indent);
                break;
            }
            case M.Paragraph para:
            {
                if (para.IsEmpty() && !para.Inlines.OfType<InlineImage>().Any())
                    break;
                var p = target.AddParagraph();
                if (style is not null) p.Style = style;
                p.Format.LeftIndent = indent + (style == "Quote" ? 14 : 0);
                p.Format.Alignment = para.Alignment switch
                {
                    M.Alignment.Center => ParagraphAlignment.Center,
                    M.Alignment.End => ParagraphAlignment.Right,
                    M.Alignment.Justify => ParagraphAlignment.Justify,
                    _ => ParagraphAlignment.Left,
                };
                AddInlines(p, para.Inlines, style == "TableText" ? 10 : BodySize, false, style == "Quote", _contentWidth - indent);
                break;
            }
            case M.ListBlock list:
                AddList(target, list, indent, 0);
                // List items sit close together; leave a normal gap after the whole list.
                if (target.Count > 0 && target[target.Count - 1] is MDoc.Paragraph last)
                    last.Format.SpaceAfter = 8;
                break;
            case M.Table table:
                AddTable(target, table, indent);
                break;
            case ImageBlock img:
            {
                var p = target.AddParagraph();
                p.Format.LeftIndent = indent;
                AddImage(p, img.Image, _contentWidth - indent);
                break;
            }
            case M.CodeBlock code:
            {
                var p = target.AddParagraph();
                p.Style = "Code";
                p.Format.LeftIndent = indent;
                var lines = TextFile.NormalizeNewlines(code.Text).Split('\n');
                for (var i = 0; i < lines.Length; i++)
                {
                    if (i > 0) p.AddLineBreak();
                    // MigraDoc collapses runs of spaces; no-break spaces keep code indentation.
                    var line = lines[i].Replace("\t", "    ").Replace(' ', ' ');
                    if (line.Length > 0) p.AddText(line);
                }
                break;
            }
            case Quote q:
                foreach (var inner in q.Blocks)
                    AddBlock(target, inner, indent, "Quote");
                break;
            case HorizontalRule:
            {
                var p = target.AddParagraph();
                p.Format.Borders.Bottom.Width = 0.75;
                p.Format.Borders.Bottom.Color = RuleColor;
                p.Format.SpaceAfter = 10;
                p.Format.Font.Size = 4;
                break;
            }
        }
    }

    private void AddList(DocumentElements target, M.ListBlock list, double indent, int depth)
    {
        const double step = 18;
        var number = list.Start;
        var markerIndent = indent + step;
        foreach (var item in list.Items)
        {
            var marker = list.Ordered ? OrderedMarker(number++, depth) : (depth % 3) switch { 0 => "•", 1 => "◦", _ => "▪" };
            var first = true;
            foreach (var block in item.Blocks)
            {
                if (block is M.ListBlock nested)
                {
                    AddList(target, nested, markerIndent, depth + 1);
                    first = false;
                    continue;
                }
                if (first && block is M.Paragraph para)
                {
                    var p = target.AddParagraph();
                    p.Format.LeftIndent = markerIndent;
                    p.Format.FirstLineIndent = -step;
                    p.Format.SpaceAfter = 3;
                    p.Format.TabStops.ClearAll();
                    p.Format.TabStops.AddTabStop(Unit.FromPoint(markerIndent));
                    var markerText = p.AddFormattedText(marker);
                    // "◦" and "▪" are not in every text font.
                    if (PdfFonts.FallbackFor(marker) is { } symbolFont)
                        markerText.Font.Name = symbolFont;
                    p.AddTab();
                    AddInlines(p, para.Inlines, BodySize, false, false, _contentWidth - markerIndent);
                }
                else
                    AddBlock(target, block, markerIndent, null);
                first = false;
            }
        }
    }

    private static string OrderedMarker(int n, int depth) => (depth % 3) switch
    {
        1 => (n is >= 1 and <= 26 ? ((char)('a' + n - 1)).ToString() : n.ToString()) + ".",
        2 => Roman(n) + ".",
        _ => n + ".",
    };

    private static string Roman(int n)
    {
        if (n is <= 0 or > 3999) return n.ToString();
        (int, string)[] map = [(1000, "m"), (900, "cm"), (500, "d"), (400, "cd"), (100, "c"), (90, "xc"), (50, "l"), (40, "xl"), (10, "x"), (9, "ix"), (5, "v"), (4, "iv"), (1, "i")];
        var sb = new System.Text.StringBuilder();
        foreach (var (value, s) in map)
            while (n >= value) { sb.Append(s); n -= value; }
        return sb.ToString();
    }

    private void AddTable(DocumentElements target, M.Table model, double indent)
    {
        var (rows, columns) = TableLayout.Layout(model);
        if (rows.Count == 0 || columns == 0)
            return;
        var available = _contentWidth - indent;
        var widths = ColumnWidths(model, rows, columns, available);

        var table = target.AddTable();
        table.Borders.Width = 0.5;
        table.Borders.Color = RuleColor;
        table.TopPadding = 2;
        table.BottomPadding = 2;
        table.LeftPadding = 4;
        table.RightPadding = 4;
        table.Rows.LeftIndent = Unit.FromPoint(indent);
        table.Style = "TableText";
        foreach (var w in widths)
            table.AddColumn(Unit.FromPoint(w));

        for (var r = 0; r < rows.Count; r++)
        {
            var row = table.AddRow();
            var header = model.Rows[r].IsHeader;
            if (header)
            {
                row.HeadingFormat = true;
                row.Shading.Color = MDoc.Color.FromRgb(0xF2, 0xF2, 0xF2);
                row.Format.Font.Bold = true;
            }
            foreach (var slot in rows[r])
            {
                if (slot.IsContinuation || slot.Column >= columns)
                    continue;
                var cell = row.Cells[slot.Column];
                if (slot.ColSpan > 1)
                    cell.MergeRight = Math.Min(slot.ColSpan, columns - slot.Column) - 1;
                if (slot.Cell.RowSpan > 1)
                    cell.MergeDown = Math.Min(slot.Cell.RowSpan, rows.Count - r) - 1;
                var cellWidth = Enumerable.Range(slot.Column, Math.Min(slot.ColSpan, columns - slot.Column)).Sum(i => widths[i]) - 8;
                foreach (var block in slot.Cell.Blocks)
                    AddCellBlock(cell, block, cellWidth, header);
            }
        }
        // Space after the table, which MigraDoc does not add on its own.
        var spacer = target.AddParagraph();
        spacer.Format.SpaceAfter = 4;
        spacer.Format.Font.Size = 4;
    }

    private void AddCellBlock(Cell cell, Block block, double width, bool header)
    {
        switch (block)
        {
            case M.Paragraph or Heading:
            {
                var inlines = block is M.Paragraph mp ? mp.Inlines : ((Heading)block).Inlines;
                var p = cell.AddParagraph();
                AddInlines(p, inlines, 10, header || block is Heading, false, width);
                break;
            }
            case ImageBlock img:
                AddImage(cell.AddParagraph(), img.Image, width);
                break;
            case M.ListBlock list:
                foreach (var item in list.Items)
                {
                    var p = cell.AddParagraph();
                    p.AddText((list.Ordered ? "" : "• "));
                    AddInlines(p, item.Blocks.OfType<M.Paragraph>().SelectMany(x => x.Inlines).ToList(), 10, header, false, width);
                }
                break;
            case M.Table nested:
                // MigraDoc cannot nest tables; nested rows become lines of tab-separated text.
                foreach (var row in nested.Rows)
                    cell.AddParagraph(string.Join("    ", row.Cells.Select(c => c.Blocks.PlainText().Replace('\n', ' '))));
                break;
            case M.CodeBlock code:
                var cp = cell.AddParagraph(code.Text);
                cp.Style = "Code";
                break;
            case Quote q:
                foreach (var inner in q.Blocks)
                    AddCellBlock(cell, inner, width, header);
                break;
        }
    }

    /// <summary>Column widths proportional to content length, each at least a readable minimum.</summary>
    private static double[] ColumnWidths(M.Table model, List<List<GridCell>> rows, int columns, double available)
    {
        var weights = new double[columns];
        foreach (var row in rows)
        {
            foreach (var slot in row)
            {
                if (slot.IsContinuation || slot.ColSpan != 1 || slot.Column >= columns)
                    continue;
                var text = slot.Cell.Blocks.PlainText();
                var longest = text.Split('\n').Select(l => l.Length).DefaultIfEmpty(0).Max();
                weights[slot.Column] = Math.Max(weights[slot.Column], Math.Min(60, Math.Max(3, longest)));
            }
        }
        for (var i = 0; i < columns; i++)
            if (weights[i] == 0) weights[i] = 6;
        var total = weights.Sum();
        var min = Math.Min(36, available / columns);
        var widths = weights.Select(w => Math.Max(min, available * w / total)).ToArray();
        var scale = available / widths.Sum();
        return widths.Select(w => w * scale).ToArray();
    }

    private static void AddImage(MDoc.Paragraph p, ImageData image, double maxWidth)
    {
        var (w, h) = image.FitSize(maxWidth, Paper.ContentHeightPt - 20);
        var img = p.AddImage("base64:" + Convert.ToBase64String(image.Bytes));
        img.Width = Unit.FromPoint(w);
        img.Height = Unit.FromPoint(h);
        img.LockAspectRatio = true;
    }

    /// <summary>Adds runs to a paragraph, laying right-to-left paragraphs out line by line.</summary>
    private static void AddInlines(MDoc.Paragraph p, IReadOnlyList<Inline> inlines, double size, bool bold, bool italic, double width)
    {
        var plain = inlines.PlainText();
        if (Bidi.IsRtlParagraph(plain) && !inlines.OfType<InlineImage>().Any())
        {
            var runs = inlines.OfType<TextRun>().Where(r => r.Text.Trim().Length > 0).ToList();
            var allBold = bold || runs.Count > 0 && runs.All(r => r.Has(TextStyle.Bold));
            var allItalic = italic || runs.Count > 0 && runs.All(r => r.Has(TextStyle.Italic));
            var lines = PdfFonts.BreakRtl(plain, PdfFonts.Arabic, size, allBold, allItalic, width);
            if (p.Format.Alignment != ParagraphAlignment.Center)
                p.Format.Alignment = ParagraphAlignment.Right;
            for (var i = 0; i < lines.Count; i++)
            {
                if (i > 0) p.AddLineBreak();
                var ft = p.AddFormattedText(lines[i]);
                ft.Font.Name = PdfFonts.Arabic;
                if (allBold) ft.Bold = true;
                if (allItalic) ft.Italic = true;
            }
            return;
        }

        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case TextRun run:
                    AddRun(p, run);
                    break;
                case M.LineBreak:
                    p.AddLineBreak();
                    break;
                case InlineImage img:
                    AddImage(p, img.Image, width);
                    break;
            }
        }
    }

    private static void AddRun(MDoc.Paragraph p, TextRun run)
    {
        var text = DocxWriterSanitize(run.Text);
        if (text.Length == 0)
            return;
        FormattedText? Container()
        {
            if (run.Link is null)
                return null;
            var type = run.Link.StartsWith('#') ? HyperlinkType.Bookmark : HyperlinkType.Web;
            var link = p.AddHyperlink(type == HyperlinkType.Bookmark ? run.Link[1..] : run.Link, type);
            return link.AddFormattedText();
        }

        var container = Container();
        var parts = text.Split('\t');
        for (var i = 0; i < parts.Length; i++)
        {
            if (i > 0)
            {
                if (container is not null) container.AddTab();
                else p.AddTab();
            }
            foreach (var (segment, font) in PdfFonts.SplitByFont(parts[i]))
            {
                var visual = Bidi.ContainsRtl(segment) ? Bidi.ToVisual(segment, rtlParagraph: false) : segment;
                var ft = container is not null ? container.AddFormattedText(visual) : p.AddFormattedText(visual);
                if (font is not null) ft.Font.Name = font;
                if (run.Has(TextStyle.Bold)) ft.Bold = true;
                if (run.Has(TextStyle.Italic)) ft.Italic = true;
                if (run.Has(TextStyle.Underline) || run.Link is not null) ft.Underline = Underline.Single;
                if (run.Has(TextStyle.Superscript)) ft.Superscript = true;
                if (run.Has(TextStyle.Subscript)) ft.Subscript = true;
                if (run.Has(TextStyle.Code)) ft.Font.Name = PdfFonts.Mono;
                if (run.Link is not null) ft.Color = LinkColor;
            }
        }
    }

    private static string DocxWriterSanitize(string s) => IO.DocxWriter.Sanitize(s).Replace("\r", "").Replace('\n', ' ');
}
