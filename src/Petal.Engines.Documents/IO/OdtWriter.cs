using System.Xml.Linq;
using Petal.Engines.Documents.Model;
using Petal.Engines.Documents.Text;
using Petal.Engines.Documents.Util;
using static Petal.Engines.Documents.IO.Odf;

namespace Petal.Engines.Documents.IO;

/// <summary>Writes OpenDocument Text with named heading, list and table styles that LibreOffice and Word recognise.</summary>
internal sealed class OdtWriter
{
    private readonly Dictionary<TextStyle, string> _textStyles = [];
    private readonly Dictionary<string, XElement> _automatic = [];
    private readonly List<(string Name, byte[] Data, string MediaType)> _pictures = [];
    private int _tableCount;
    private int _frameCount;

    public static void Write(DocModel doc, string path)
    {
        var writer = new OdtWriter();
        var text = new XElement(OfficeNs + "text");
        foreach (var e in writer.Blocks(doc.Blocks, null))
            text.Add(e);

        var content = new XDocument(new XElement(OfficeNs + "document-content", Declarations(),
            FontFaces(),
            new XElement(OfficeNs + "automatic-styles", writer._automatic.Values),
            new XElement(OfficeNs + "body", text)));

        var parts = new List<(string, byte[], string)>
        {
            ("content.xml", Serialize(content), "text/xml"),
            ("styles.xml", Serialize(Styles()), "text/xml"),
            ("meta.xml", Serialize(MetaDocument(doc)), "text/xml"),
        };
        parts.AddRange(writer._pictures);
        WritePackage(path, "application/vnd.oasis.opendocument.text", parts);
    }

    internal static XDocument MetaDocument(DocModel doc)
    {
        var meta = new XElement(OfficeNs + "meta",
            new XElement(MetaNs + "generator", "Petal"),
            new XElement(MetaNs + "creation-date", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss")));
        if (doc.EffectiveTitle() is { } title) meta.Add(new XElement(DcNs + "title", title));
        if (doc.Metadata.Author is { } author)
        {
            meta.Add(new XElement(MetaNs + "initial-creator", author));
            meta.Add(new XElement(DcNs + "creator", author));
        }
        if (doc.Metadata.Subject is { } subject) meta.Add(new XElement(DcNs + "subject", subject));
        if (doc.Metadata.Keywords is { } keywords)
            foreach (var k in keywords.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                meta.Add(new XElement(MetaNs + "keyword", k));
        if (doc.Metadata.Language is { } lang) meta.Add(new XElement(DcNs + "language", lang));
        return new XDocument(new XElement(OfficeNs + "document-meta", Declarations(), meta));
    }

    private static XElement FontFaces() => new(OfficeNs + "font-face-decls",
        new XElement(StyleNs + "font-face", new XAttribute(StyleNs + "name", "Calibri"), new XAttribute(SvgNs + "font-family", "Calibri"), new XAttribute(StyleNs + "font-family-generic", "swiss")),
        new XElement(StyleNs + "font-face", new XAttribute(StyleNs + "name", "Arial"), new XAttribute(SvgNs + "font-family", "Arial"), new XAttribute(StyleNs + "font-family-generic", "swiss")),
        new XElement(StyleNs + "font-face", new XAttribute(StyleNs + "name", "Consolas"), new XAttribute(SvgNs + "font-family", "Consolas"), new XAttribute(StyleNs + "font-pitch", "fixed")));

    private IEnumerable<XElement> Blocks(IEnumerable<Block> blocks, string? paragraphStyle)
    {
        foreach (var block in blocks)
            foreach (var e in BlockElements(block, paragraphStyle))
                yield return e;
    }

    private IEnumerable<XElement> BlockElements(Block block, string? paragraphStyle)
    {
        switch (block)
        {
            case Heading h:
            {
                var e = new XElement(TextNs + "h",
                    new XAttribute(TextNs + "style-name", $"Heading_20_{h.Level}"),
                    new XAttribute(TextNs + "outline-level", h.Level));
                AddInlines(e, h.Inlines);
                yield return e;
                break;
            }
            case Paragraph p:
            {
                var e = new XElement(TextNs + "p", new XAttribute(TextNs + "style-name", ParagraphStyle(paragraphStyle ?? "Standard", p.Alignment, Bidi.IsRtlParagraph(p.Inlines.PlainText()))));
                AddInlines(e, p.Inlines);
                yield return e;
                break;
            }
            case ListBlock l:
                yield return ListElement(l);
                break;
            case Model.Table t:
                yield return TableElement(t);
                break;
            case ImageBlock img:
            {
                var e = new XElement(TextNs + "p", new XAttribute(TextNs + "style-name", "Standard"));
                e.Add(Frame(img.Image));
                yield return e;
                break;
            }
            case CodeBlock c:
                foreach (var line in TextFile.NormalizeNewlines(c.Text).Split('\n'))
                {
                    var e = new XElement(TextNs + "p", new XAttribute(TextNs + "style-name", "Preformatted_20_Text"));
                    AppendEncodedText(e, line);
                    yield return e;
                }
                break;
            case Quote q:
                foreach (var e in Blocks(q.Blocks, "Quotations"))
                    yield return e;
                break;
            case HorizontalRule:
                yield return new XElement(TextNs + "p", new XAttribute(TextNs + "style-name", "Horizontal_20_Line"));
                break;
            case PageBreak:
                yield return new XElement(TextNs + "p", new XAttribute(TextNs + "style-name", PageBreakStyle()));
                break;
        }
    }

    private XElement ListElement(ListBlock list)
    {
        var e = new XElement(TextNs + "list", new XAttribute(TextNs + "style-name", list.Ordered ? "L_Number" : "L_Bullet"));
        var first = true;
        foreach (var item in list.Items)
        {
            var li = new XElement(TextNs + "list-item");
            if (first && list.Ordered && list.Start != 1)
                li.Add(new XAttribute(TextNs + "start-value", list.Start));
            first = false;
            foreach (var block in item.Blocks)
            {
                if (block is ListBlock nested)
                    li.Add(ListElement(nested));
                else
                    foreach (var child in BlockElements(block, "List_20_Paragraph"))
                        li.Add(child);
            }
            if (!li.HasElements)
                li.Add(new XElement(TextNs + "p", new XAttribute(TextNs + "style-name", "List_20_Paragraph")));
            e.Add(li);
        }
        return e;
    }

    private XElement TableElement(Model.Table table)
    {
        var (rows, columns) = TableLayout.Layout(table);
        columns = Math.Max(1, columns);
        var name = $"Table{++_tableCount}";
        EnsureTableStyles();
        var t = new XElement(TableNs + "table",
            new XAttribute(TableNs + "name", name),
            new XAttribute(TableNs + "style-name", "PetalTable"),
            new XElement(TableNs + "table-column", new XAttribute(TableNs + "number-columns-repeated", columns)));

        XElement? headerRows = null;
        for (var r = 0; r < rows.Count; r++)
        {
            var isHeader = table.Rows[r].IsHeader;
            var tr = new XElement(TableNs + "table-row");
            var col = 0;
            foreach (var slot in rows[r])
            {
                while (col < slot.Column)
                {
                    tr.Add(EmptyCell());
                    col++;
                }
                if (slot.IsContinuation)
                {
                    for (var k = 0; k < slot.ColSpan; k++)
                        tr.Add(new XElement(TableNs + "covered-table-cell"));
                }
                else
                {
                    var cell = new XElement(TableNs + "table-cell",
                        new XAttribute(TableNs + "style-name", "PetalCell"),
                        new XAttribute(OfficeNs + "value-type", "string"));
                    if (slot.ColSpan > 1) cell.Add(new XAttribute(TableNs + "number-columns-spanned", slot.ColSpan));
                    if (slot.Cell.RowSpan > 1) cell.Add(new XAttribute(TableNs + "number-rows-spanned", slot.Cell.RowSpan));
                    foreach (var e in Blocks(slot.Cell.Blocks, isHeader ? "Table_20_Heading" : "Table_20_Contents"))
                        cell.Add(e);
                    if (!cell.Elements().Any(x => x.Name == TextNs + "p" || x.Name == TextNs + "h" || x.Name == TextNs + "list" || x.Name == TableNs + "table"))
                        cell.Add(new XElement(TextNs + "p", new XAttribute(TextNs + "style-name", "Table_20_Contents")));
                    tr.Add(cell);
                    for (var k = 1; k < slot.ColSpan; k++)
                        tr.Add(new XElement(TableNs + "covered-table-cell"));
                }
                col += slot.ColSpan;
            }
            while (col < columns)
            {
                tr.Add(EmptyCell());
                col++;
            }

            if (isHeader && r == (headerRows?.Elements().Count() ?? 0))
            {
                headerRows ??= new XElement(TableNs + "table-header-rows");
                headerRows.Add(tr);
                if (headerRows.Parent is null)
                    t.Add(headerRows);
            }
            else
                t.Add(tr);
        }
        return t;
    }

    private static XElement EmptyCell() =>
        new(TableNs + "table-cell", new XAttribute(TableNs + "style-name", "PetalCell"), new XAttribute(OfficeNs + "value-type", "string"),
            new XElement(TextNs + "p", new XAttribute(TextNs + "style-name", "Table_20_Contents")));

    private void EnsureTableStyles()
    {
        if (_automatic.ContainsKey("PetalTable"))
            return;
        _automatic["PetalTable"] = new XElement(StyleNs + "style",
            new XAttribute(StyleNs + "name", "PetalTable"), new XAttribute(StyleNs + "family", "table"),
            new XElement(StyleNs + "table-properties", new XAttribute(StyleNs + "width", Cm(Paper.ContentWidthPt)), new XAttribute(TableNs + "align", "margins"),
                new XAttribute(FoNs + "margin-bottom", "0.3cm")));
        _automatic["PetalCell"] = new XElement(StyleNs + "style",
            new XAttribute(StyleNs + "name", "PetalCell"), new XAttribute(StyleNs + "family", "table-cell"),
            new XElement(StyleNs + "table-cell-properties", new XAttribute(FoNs + "padding", "0.1cm"), new XAttribute(FoNs + "border", "0.5pt solid #bfbfbf")));
    }

    private void AddInlines(XElement parent, IEnumerable<Inline> inlines)
    {
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case TextRun run:
                {
                    XElement target = parent;
                    if (run.Link is not null)
                    {
                        var a = new XElement(TextNs + "a",
                            new XAttribute(XLinkNs + "type", "simple"),
                            new XAttribute(XLinkNs + "href", run.Link),
                            new XAttribute(TextNs + "style-name", "Internet_20_link"));
                        parent.Add(a);
                        target = a;
                    }
                    if (run.Style != TextStyle.None)
                    {
                        var span = new XElement(TextNs + "span", new XAttribute(TextNs + "style-name", TextStyleName(run.Style)));
                        target.Add(span);
                        target = span;
                    }
                    AppendEncodedText(target, DocxWriter.Sanitize(run.Text));
                    break;
                }
                case LineBreak:
                    parent.Add(new XElement(TextNs + "line-break"));
                    break;
                case InlineImage img:
                    parent.Add(Frame(img.Image));
                    break;
            }
        }
    }

    private XElement Frame(ImageData image)
    {
        var index = _pictures.Count + 1;
        var name = $"Pictures/image{index}.{image.Extension}";
        _pictures.Add((name, image.Bytes, image.MediaType));
        if (!_automatic.ContainsKey("fr1"))
        {
            _automatic["fr1"] = new XElement(StyleNs + "style",
                new XAttribute(StyleNs + "name", "fr1"), new XAttribute(StyleNs + "family", "graphic"), new XAttribute(StyleNs + "parent-style-name", "Graphics"),
                new XElement(StyleNs + "graphic-properties", new XAttribute(StyleNs + "vertical-pos", "top"), new XAttribute(StyleNs + "vertical-rel", "baseline")));
        }
        var (w, h) = image.FitSize(Paper.ContentWidthPt, Paper.ContentHeightPt);
        return new XElement(DrawNs + "frame",
            new XAttribute(DrawNs + "style-name", "fr1"),
            new XAttribute(DrawNs + "name", $"Image{++_frameCount}"),
            new XAttribute(TextNs + "anchor-type", "as-char"),
            new XAttribute(SvgNs + "width", Cm(w)),
            new XAttribute(SvgNs + "height", Cm(h)),
            new XAttribute(DrawNs + "z-index", 0),
            image.Alt is { Length: > 0 } alt ? new XElement(SvgNs + "desc", alt) : null,
            new XElement(DrawNs + "image",
                new XAttribute(XLinkNs + "href", name),
                new XAttribute(XLinkNs + "type", "simple"),
                new XAttribute(XLinkNs + "show", "embed"),
                new XAttribute(XLinkNs + "actuate", "onLoad"),
                new XAttribute(DrawNs + "mime-type", image.MediaType)));
    }

    private string TextStyleName(TextStyle style)
    {
        if (_textStyles.TryGetValue(style, out var name))
            return name;
        name = $"T{_textStyles.Count + 1}";
        _textStyles[style] = name;
        var props = new XElement(StyleNs + "text-properties");
        if ((style & TextStyle.Bold) != 0)
            props.Add(new XAttribute(FoNs + "font-weight", "bold"), new XAttribute(StyleNs + "font-weight-asian", "bold"), new XAttribute(StyleNs + "font-weight-complex", "bold"));
        if ((style & TextStyle.Italic) != 0)
            props.Add(new XAttribute(FoNs + "font-style", "italic"), new XAttribute(StyleNs + "font-style-asian", "italic"), new XAttribute(StyleNs + "font-style-complex", "italic"));
        if ((style & TextStyle.Underline) != 0)
            props.Add(new XAttribute(StyleNs + "text-underline-style", "solid"), new XAttribute(StyleNs + "text-underline-width", "auto"), new XAttribute(StyleNs + "text-underline-color", "font-color"));
        if ((style & TextStyle.Strike) != 0)
            props.Add(new XAttribute(StyleNs + "text-line-through-style", "solid"));
        if ((style & TextStyle.Superscript) != 0)
            props.Add(new XAttribute(StyleNs + "text-position", "super 58%"));
        if ((style & TextStyle.Subscript) != 0)
            props.Add(new XAttribute(StyleNs + "text-position", "sub 58%"));
        if ((style & TextStyle.Code) != 0)
            props.Add(new XAttribute(StyleNs + "font-name", "Consolas"));
        _automatic[name] = new XElement(StyleNs + "style", new XAttribute(StyleNs + "name", name), new XAttribute(StyleNs + "family", "text"), props);
        return name;
    }

    private string ParagraphStyle(string parent, Alignment alignment, bool rtl)
    {
        if (alignment == Alignment.Start && !rtl)
            return parent;
        var name = $"P_{parent}_{alignment}{(rtl ? "_rtl" : "")}";
        if (!_automatic.ContainsKey(name))
        {
            var props = new XElement(StyleNs + "paragraph-properties");
            if (alignment != Alignment.Start)
                props.Add(new XAttribute(FoNs + "text-align", alignment switch { Alignment.Center => "center", Alignment.End => "end", _ => "justify" }));
            if (rtl)
                props.Add(new XAttribute(StyleNs + "writing-mode", "rl-tb"));
            _automatic[name] = new XElement(StyleNs + "style",
                new XAttribute(StyleNs + "name", name), new XAttribute(StyleNs + "family", "paragraph"), new XAttribute(StyleNs + "parent-style-name", parent), props);
        }
        return name;
    }

    private string PageBreakStyle()
    {
        const string name = "P_PageBreak";
        if (!_automatic.ContainsKey(name))
        {
            _automatic[name] = new XElement(StyleNs + "style",
                new XAttribute(StyleNs + "name", name), new XAttribute(StyleNs + "family", "paragraph"), new XAttribute(StyleNs + "parent-style-name", "Standard"),
                new XElement(StyleNs + "paragraph-properties", new XAttribute(FoNs + "break-before", "page")));
        }
        return name;
    }

    private static XDocument Styles()
    {
        var (w, h) = Paper.PageSizePt;
        static XElement ParaStyle(string name, string display, string? parent, XElement? paragraph, XElement? text, string? @class = null, int? outline = null)
        {
            var e = new XElement(StyleNs + "style",
                new XAttribute(StyleNs + "name", name),
                new XAttribute(StyleNs + "display-name", display),
                new XAttribute(StyleNs + "family", "paragraph"));
            if (parent is not null) e.Add(new XAttribute(StyleNs + "parent-style-name", parent));
            if (@class is not null) e.Add(new XAttribute(StyleNs + "class", @class));
            if (outline is not null) e.Add(new XAttribute(StyleNs + "default-outline-level", outline));
            if (paragraph is not null) e.Add(paragraph);
            if (text is not null) e.Add(text);
            return e;
        }

        var styles = new XElement(OfficeNs + "styles",
            new XElement(StyleNs + "default-style", new XAttribute(StyleNs + "family", "paragraph"),
                new XElement(StyleNs + "paragraph-properties", new XAttribute(FoNs + "margin-bottom", "0.28cm"), new XAttribute(FoNs + "line-height", "110%")),
                new XElement(StyleNs + "text-properties", new XAttribute(StyleNs + "font-name", "Calibri"), new XAttribute(FoNs + "font-size", "11pt"),
                    new XAttribute(StyleNs + "font-name-complex", "Arial"), new XAttribute(StyleNs + "font-size-complex", "11pt"))),
            ParaStyle("Standard", "Default Paragraph Style", null, null, null, "text"),
            ParaStyle("Heading", "Heading", "Standard",
                new XElement(StyleNs + "paragraph-properties", new XAttribute(FoNs + "margin-top", "0.42cm"), new XAttribute(FoNs + "margin-bottom", "0.21cm"), new XAttribute(FoNs + "keep-with-next", "always")),
                new XElement(StyleNs + "text-properties", new XAttribute(FoNs + "font-weight", "bold"), new XAttribute(StyleNs + "font-weight-complex", "bold"), new XAttribute(FoNs + "color", "#1f3864")),
                "text"));
        string[] sizes = ["20pt", "16pt", "14pt", "12pt", "11pt", "11pt"];
        for (var i = 0; i < 6; i++)
        {
            var text = new XElement(StyleNs + "text-properties", new XAttribute(FoNs + "font-size", sizes[i]), new XAttribute(StyleNs + "font-size-complex", sizes[i]));
            if (i >= 3) text.Add(new XAttribute(FoNs + "font-style", i == 3 ? "italic" : "normal"));
            styles.Add(ParaStyle($"Heading_20_{i + 1}", $"Heading {i + 1}", "Heading", null, text, "text", i + 1));
        }
        styles.Add(
            ParaStyle("List_20_Paragraph", "List Paragraph", "Standard", new XElement(StyleNs + "paragraph-properties", new XAttribute(FoNs + "margin-bottom", "0.1cm")), null, "list"),
            ParaStyle("Quotations", "Quotations", "Standard",
                new XElement(StyleNs + "paragraph-properties", new XAttribute(FoNs + "margin-left", "1cm"), new XAttribute(FoNs + "margin-right", "1cm"),
                    new XAttribute(FoNs + "border-left", "1.5pt solid #d0d0d0"), new XAttribute(FoNs + "padding-left", "0.25cm")),
                new XElement(StyleNs + "text-properties", new XAttribute(FoNs + "font-style", "italic"), new XAttribute(FoNs + "color", "#404040")), "html"),
            ParaStyle("Preformatted_20_Text", "Preformatted Text", "Standard",
                new XElement(StyleNs + "paragraph-properties", new XAttribute(FoNs + "margin-bottom", "0cm"), new XAttribute(FoNs + "background-color", "#f5f5f5")),
                new XElement(StyleNs + "text-properties", new XAttribute(StyleNs + "font-name", "Consolas"), new XAttribute(FoNs + "font-size", "10pt")), "html"),
            ParaStyle("Table_20_Contents", "Table Contents", "Standard", new XElement(StyleNs + "paragraph-properties", new XAttribute(FoNs + "margin-bottom", "0cm")), null, "extra"),
            ParaStyle("Table_20_Heading", "Table Heading", "Table_20_Contents", null,
                new XElement(StyleNs + "text-properties", new XAttribute(FoNs + "font-weight", "bold"), new XAttribute(StyleNs + "font-weight-complex", "bold")), "extra"),
            ParaStyle("Horizontal_20_Line", "Horizontal Line", "Standard",
                new XElement(StyleNs + "paragraph-properties", new XAttribute(FoNs + "border-bottom", "0.5pt solid #bfbfbf"), new XAttribute(FoNs + "padding", "0cm"), new XAttribute(FoNs + "margin-bottom", "0.5cm")), null, "html"),
            new XElement(StyleNs + "style", new XAttribute(StyleNs + "name", "Internet_20_link"), new XAttribute(StyleNs + "display-name", "Internet Link"), new XAttribute(StyleNs + "family", "text"),
                new XElement(StyleNs + "text-properties", new XAttribute(FoNs + "color", "#0563c1"), new XAttribute(StyleNs + "text-underline-style", "solid"), new XAttribute(StyleNs + "text-underline-width", "auto"), new XAttribute(StyleNs + "text-underline-color", "font-color"))),
            new XElement(StyleNs + "style", new XAttribute(StyleNs + "name", "Graphics"), new XAttribute(StyleNs + "family", "graphic"),
                new XElement(StyleNs + "graphic-properties", new XAttribute(TextNs + "anchor-type", "as-char"), new XAttribute(SvgNs + "y", "0cm"), new XAttribute(StyleNs + "wrap", "none"))),
            ListStyle("L_Bullet", ordered: false),
            ListStyle("L_Number", ordered: true));

        var automatic = new XElement(OfficeNs + "automatic-styles",
            new XElement(StyleNs + "page-layout", new XAttribute(StyleNs + "name", "pm1"),
                new XElement(StyleNs + "page-layout-properties",
                    new XAttribute(FoNs + "page-width", Cm(w)), new XAttribute(FoNs + "page-height", Cm(h)),
                    new XAttribute(StyleNs + "print-orientation", "portrait"),
                    new XAttribute(FoNs + "margin-top", Cm(Paper.MarginPt)), new XAttribute(FoNs + "margin-bottom", Cm(Paper.MarginPt)),
                    new XAttribute(FoNs + "margin-left", Cm(Paper.MarginPt)), new XAttribute(FoNs + "margin-right", Cm(Paper.MarginPt)))));
        var master = new XElement(OfficeNs + "master-styles",
            new XElement(StyleNs + "master-page", new XAttribute(StyleNs + "name", "Standard"), new XAttribute(StyleNs + "page-layout-name", "pm1")));

        return new XDocument(new XElement(OfficeNs + "document-styles", Declarations(), FontFaces(), styles, automatic, master));
    }

    private static XElement ListStyle(string name, bool ordered)
    {
        var e = new XElement(TextNs + "list-style", new XAttribute(StyleNs + "name", name));
        string[] bullets = ["•", "◦", "▪"];
        string[] formats = ["1", "a", "i"];
        for (var level = 1; level <= 10; level++)
        {
            var props = new XElement(StyleNs + "list-level-properties",
                new XAttribute(TextNs + "list-level-position-and-space-mode", "label-alignment"),
                new XElement(StyleNs + "list-level-label-alignment",
                    new XAttribute(TextNs + "label-followed-by", "listtab"),
                    new XAttribute(TextNs + "list-tab-stop-position", Cm(36 * level)),
                    new XAttribute(FoNs + "text-indent", Cm(-18)),
                    new XAttribute(FoNs + "margin-left", Cm(36 * level))));
            e.Add(ordered
                ? new XElement(TextNs + "list-level-style-number",
                    new XAttribute(TextNs + "level", level),
                    new XAttribute(StyleNs + "num-suffix", "."),
                    new XAttribute(StyleNs + "num-format", formats[(level - 1) % 3]),
                    props)
                : new XElement(TextNs + "list-level-style-bullet",
                    new XAttribute(TextNs + "level", level),
                    new XAttribute(TextNs + "bullet-char", bullets[(level - 1) % 3]),
                    props));
        }
        return e;
    }
}
