using System.IO.Compression;
using System.Xml.Linq;
using Petal.Engines.Documents.Model;
using static Petal.Engines.Documents.IO.Odf;

namespace Petal.Engines.Documents.IO;

/// <summary>Reads OpenDocument Text: headings, styled spans, lists, tables, links and pictures.</summary>
internal sealed class OdtReader
{
    private sealed class StyleInfo
    {
        public string? Parent;
        public string? Family;
        public string? DisplayName;
        public int? OutlineLevel;
        public bool? Bold, Italic, Underline, Strike, Mono;
        public bool? Super, Sub;
        public Alignment? Align;
        public bool? BreakBefore, BreakAfter, Rtl;
        public string? ListStyle;
    }

    private readonly ZipArchive _zip;
    private readonly Dictionary<string, StyleInfo> _styles = new();
    private readonly Dictionary<string, Dictionary<int, bool>> _listStyles = new();

    private OdtReader(ZipArchive zip) => _zip = zip;

    public static DocModel Read(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var reader = new OdtReader(zip);
        var content = LoadPart(zip, "content.xml") ?? throw new InvalidDataException("The document has no content.xml.");
        var styles = LoadPart(zip, "styles.xml");
        if (styles is not null)
            reader.LoadStyles(styles.Root!);
        reader.LoadStyles(content.Root!);

        var doc = new DocModel();
        if (LoadPart(zip, "meta.xml")?.Root?.Element(OfficeNs + "meta") is { } meta)
        {
            doc.Metadata.Title = Value(meta.Element(DcNs + "title"));
            doc.Metadata.Author = Value(meta.Element(DcNs + "creator")) ?? Value(meta.Element(MetaNs + "initial-creator"));
            doc.Metadata.Subject = Value(meta.Element(DcNs + "subject"));
            var keywords = meta.Elements(MetaNs + "keyword").Select(k => k.Value.Trim()).Where(k => k.Length > 0).ToList();
            doc.Metadata.Keywords = keywords.Count > 0 ? string.Join(", ", keywords) : null;
            doc.Metadata.Language = Value(meta.Element(DcNs + "language"));
        }

        var text = content.Root!.Element(OfficeNs + "body")?.Element(OfficeNs + "text");
        if (text is not null)
            reader.ReadBlocks(text.Elements(), doc.Blocks);
        return doc;
    }

    private static string? Value(XElement? e) => e is null || string.IsNullOrWhiteSpace(e.Value) ? null : e.Value.Trim();

    private void LoadStyles(XElement root)
    {
        foreach (var container in new[] { root.Element(OfficeNs + "styles"), root.Element(OfficeNs + "automatic-styles") })
        {
            if (container is null)
                continue;
            foreach (var s in container.Elements(StyleNs + "style"))
            {
                var name = (string?)s.Attribute(StyleNs + "name");
                if (name is null)
                    continue;
                var info = new StyleInfo
                {
                    Parent = (string?)s.Attribute(StyleNs + "parent-style-name"),
                    Family = (string?)s.Attribute(StyleNs + "family"),
                    DisplayName = (string?)s.Attribute(StyleNs + "display-name") ?? name.Replace("_20_", " "),
                    OutlineLevel = (int?)s.Attribute(StyleNs + "default-outline-level"),
                    ListStyle = (string?)s.Attribute(StyleNs + "list-style-name"),
                };
                if (s.Element(StyleNs + "text-properties") is { } tp)
                {
                    var weight = (string?)tp.Attribute(FoNs + "font-weight");
                    if (weight is not null) info.Bold = weight is "bold" or "600" or "700" or "800" or "900";
                    var fontStyle = (string?)tp.Attribute(FoNs + "font-style");
                    if (fontStyle is not null) info.Italic = fontStyle is "italic" or "oblique";
                    var underline = (string?)tp.Attribute(StyleNs + "text-underline-style");
                    if (underline is not null) info.Underline = underline != "none";
                    var strike = (string?)tp.Attribute(StyleNs + "text-line-through-style");
                    if (strike is not null) info.Strike = strike != "none";
                    var position = (string?)tp.Attribute(StyleNs + "text-position");
                    if (position is not null)
                    {
                        info.Super = position.StartsWith("super", StringComparison.Ordinal) || position.Length > 0 && char.IsDigit(position[0]) && !position.StartsWith('0');
                        info.Sub = position.StartsWith("sub", StringComparison.Ordinal) || position.StartsWith('-');
                    }
                    var font = (string?)tp.Attribute(StyleNs + "font-name") ?? (string?)tp.Attribute(FoNs + "font-family");
                    if (font is not null)
                        info.Mono = font.Contains("Mono", StringComparison.OrdinalIgnoreCase) || font.Contains("Courier", StringComparison.OrdinalIgnoreCase) || font.Contains("Consolas", StringComparison.OrdinalIgnoreCase);
                }
                if (s.Element(StyleNs + "paragraph-properties") is { } pp)
                {
                    info.Align = (string?)pp.Attribute(FoNs + "text-align") switch
                    {
                        "center" => Alignment.Center,
                        "end" or "right" => Alignment.End,
                        "justify" => Alignment.Justify,
                        "start" or "left" => Alignment.Start,
                        _ => null,
                    };
                    if ((string?)pp.Attribute(FoNs + "break-before") == "page") info.BreakBefore = true;
                    if ((string?)pp.Attribute(FoNs + "break-after") == "page") info.BreakAfter = true;
                    var mode = (string?)pp.Attribute(StyleNs + "writing-mode");
                    if (mode is not null) info.Rtl = mode.StartsWith("rl", StringComparison.Ordinal);
                }
                _styles[name] = info;
            }

            foreach (var ls in container.Elements(TextNs + "list-style"))
            {
                var name = (string?)ls.Attribute(StyleNs + "name");
                if (name is null)
                    continue;
                var levels = new Dictionary<int, bool>();
                foreach (var level in ls.Elements())
                {
                    var n = (int?)level.Attribute(TextNs + "level") ?? 1;
                    levels[n] = level.Name == TextNs + "list-level-style-number" && (string?)level.Attribute(StyleNs + "num-format") is not "";
                }
                _listStyles[name] = levels;
            }
        }
    }

    private IEnumerable<StyleInfo> Chain(string? name)
    {
        var seen = new HashSet<string>();
        while (name is not null && seen.Add(name) && _styles.TryGetValue(name, out var info))
        {
            yield return info;
            name = info.Parent;
        }
    }

    private T? First<T>(string? style, Func<StyleInfo, T?> pick) where T : struct
    {
        foreach (var s in Chain(style))
            if (pick(s) is { } v)
                return v;
        return null;
    }

    private TextStyle TextStyleOf(string? style)
    {
        var result = TextStyle.None;
        if (First(style, s => s.Bold) == true) result |= TextStyle.Bold;
        if (First(style, s => s.Italic) == true) result |= TextStyle.Italic;
        if (First(style, s => s.Underline) == true) result |= TextStyle.Underline;
        if (First(style, s => s.Strike) == true) result |= TextStyle.Strike;
        if (First(style, s => s.Super) == true) result |= TextStyle.Superscript;
        if (First(style, s => s.Sub) == true) result |= TextStyle.Subscript;
        if (First(style, s => s.Mono) == true) result |= TextStyle.Code;
        return result;
    }

    private int? HeadingLevelOf(string? style)
    {
        foreach (var s in Chain(style))
        {
            if (s.OutlineLevel is { } level)
                return level;
            if (s.DisplayName is { } display && display.StartsWith("Heading ", StringComparison.OrdinalIgnoreCase) && int.TryParse(display.AsSpan(8), out var n))
                return n;
            if (s.DisplayName is "Title")
                return 1;
        }
        return null;
    }

    private void ReadBlocks(IEnumerable<XElement> elements, List<Block> target)
    {
        foreach (var e in elements)
        {
            if (e.Name == TextNs + "h")
            {
                var style = (string?)e.Attribute(TextNs + "style-name");
                AddBreakBefore(style, target);
                var level = (int?)e.Attribute(TextNs + "outline-level") ?? HeadingLevelOf(style) ?? 1;
                var h = new Heading(level);
                var trailing = new List<Block>();
                h.Inlines.AddRange(ReadInlines(e, StripBold(TextStyleOf(style)), trailing));
                if (h.Inlines.Count > 0)
                    target.Add(h);
                target.AddRange(trailing);
            }
            else if (e.Name == TextNs + "p")
            {
                var style = (string?)e.Attribute(TextNs + "style-name");
                AddBreakBefore(style, target);
                var trailing = new List<Block>();
                var inlines = ReadInlines(e, TextStyleOf(style), trailing);
                if (HeadingLevelOf(style) is { } level && inlines.Count > 0)
                {
                    var h = new Heading(level);
                    h.Inlines.AddRange(inlines);
                    target.Add(h);
                }
                else if (inlines is [InlineImage only])
                    target.Add(new ImageBlock(only.Image));
                else if (inlines.Count > 0)
                {
                    var p = new Paragraph { Alignment = First(style, s => s.Align) ?? Alignment.Start };
                    p.Inlines.AddRange(inlines);
                    target.Add(p);
                }
                target.AddRange(trailing);
                if (First(style, s => s.BreakAfter) == true)
                    target.Add(new PageBreak());
            }
            else if (e.Name == TextNs + "list")
                target.Add(ReadList(e, (string?)e.Attribute(TextNs + "style-name"), 1));
            else if (e.Name == TableNs + "table")
                target.Add(ReadTable(e));
            else if (e.Name == TextNs + "section" || e.Name == TextNs + "index-body" || e.Name == TextNs + "table-of-content" || e.Name == TextNs + "alphabetical-index" || e.Name == TextNs + "illustration-index")
                ReadBlocks(e.Elements(), target);
            else if (e.Name == TextNs + "soft-page-break")
                continue;
        }
    }

    private static TextStyle StripBold(TextStyle s) => s & ~TextStyle.Bold;

    private void AddBreakBefore(string? style, List<Block> target)
    {
        if (First(style, s => s.BreakBefore) == true && target.Count > 0)
            target.Add(new PageBreak());
    }

    private Model.ListBlock ReadList(XElement list, string? styleName, int level)
    {
        var name = (string?)list.Attribute(TextNs + "style-name") ?? styleName;
        var ordered = name is not null && _listStyles.TryGetValue(name, out var levels) && levels.TryGetValue(level, out var o) && o;
        var result = new Model.ListBlock(ordered);
        foreach (var li in list.Elements().Where(x => x.Name == TextNs + "list-item" || x.Name == TextNs + "list-header"))
        {
            if (result.Items.Count == 0 && (int?)li.Attribute(TextNs + "start-value") is { } start)
                result.Start = start;
            var item = new ListItem();
            foreach (var child in li.Elements())
            {
                if (child.Name == TextNs + "list")
                    item.Blocks.Add(ReadList(child, name, level + 1));
                else
                    ReadBlocks([child], item.Blocks);
            }
            result.Items.Add(item);
        }
        return result;
    }

    private Model.Table ReadTable(XElement table)
    {
        var result = new Model.Table();
        void AddRows(IEnumerable<XElement> rows, bool header)
        {
            foreach (var tr in rows)
            {
                var row = new Model.TableRow { IsHeader = header };
                foreach (var tc in tr.Elements())
                {
                    if (tc.Name == TableNs + "covered-table-cell")
                        continue;
                    if (tc.Name != TableNs + "table-cell")
                        continue;
                    var cell = new Model.TableCell
                    {
                        ColSpan = Math.Max(1, (int?)tc.Attribute(TableNs + "number-columns-spanned") ?? 1),
                        RowSpan = Math.Max(1, (int?)tc.Attribute(TableNs + "number-rows-spanned") ?? 1),
                    };
                    ReadBlocks(tc.Elements(), cell.Blocks);
                    var repeat = Math.Clamp((int?)tc.Attribute(TableNs + "number-columns-repeated") ?? 1, 1, 64);
                    row.Cells.Add(cell);
                    for (var k = 1; k < repeat; k++)
                        row.Cells.Add(new Model.TableCell());
                }
                result.Rows.Add(row);
            }
        }
        foreach (var child in table.Elements())
        {
            if (child.Name == TableNs + "table-header-rows")
                AddRows(child.Elements(TableNs + "table-row"), true);
            else if (child.Name == TableNs + "table-row")
                AddRows([child], false);
            else if (child.Name == TableNs + "table-rows" || child.Name == TableNs + "table-row-group")
                AddRows(child.Elements(TableNs + "table-row"), false);
        }
        return result;
    }

    private List<Inline> ReadInlines(XElement element, TextStyle style, List<Block> trailing)
    {
        var output = new List<Inline>();
        AppendInlines(element, style, null, output, trailing);
        var coalesced = output.Coalesce();
        coalesced.TrimInlines();
        return coalesced;
    }

    private void AppendInlines(XElement element, TextStyle style, string? link, List<Inline> output, List<Block> trailing)
    {
        foreach (var node in element.Nodes())
        {
            if (node is XText t)
            {
                // ODF collapses whitespace in text nodes, like HTML.
                var text = System.Text.RegularExpressions.Regex.Replace(t.Value, "[ \t\r\n]+", " ");
                if (text.Length > 0)
                    output.Add(new TextRun(text, style, link));
                continue;
            }
            if (node is not XElement e)
                continue;
            var n = e.Name;
            if (n == TextNs + "span")
                AppendInlines(e, style | TextStyleOf((string?)e.Attribute(TextNs + "style-name")), link, output, trailing);
            else if (n == TextNs + "a")
                AppendInlines(e, style, (string?)e.Attribute(XLinkNs + "href") ?? link, output, trailing);
            else if (n == TextNs + "s")
                output.Add(new TextRun(new string(' ', Math.Clamp((int?)e.Attribute(TextNs + "c") ?? 1, 1, 1000)), style, link));
            else if (n == TextNs + "tab")
                output.Add(new TextRun("\t", style, link));
            else if (n == TextNs + "line-break")
                output.Add(new LineBreak());
            else if (n == DrawNs + "frame")
                ReadFrame(e, output, trailing);
            else if (n == DrawNs + "a")
                foreach (var frame in e.Elements(DrawNs + "frame"))
                    ReadFrame(frame, output, trailing);
            else if (n == TextNs + "note" || n == OfficeNs + "annotation" || n == OfficeNs + "annotation-end" || n == TextNs + "bookmark" ||
                     n == TextNs + "bookmark-start" || n == TextNs + "bookmark-end" || n == TextNs + "soft-page-break" || n == TextNs + "reference-mark-start")
                continue;
            else
                AppendInlines(e, style, link, output, trailing);
        }
    }

    private void ReadFrame(XElement frame, List<Inline> output, List<Block> trailing)
    {
        if (frame.Element(DrawNs + "image") is { } img && (string?)img.Attribute(XLinkNs + "href") is { } href && !href.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            var bytes = ReadEntry(_zip, href.StartsWith("./", StringComparison.Ordinal) ? href[2..] : href);
            if (bytes is not null && ImageData.Create(bytes) is { } image)
            {
                image.WidthPt = ParseLength((string?)frame.Attribute(SvgNs + "width"));
                image.HeightPt = ParseLength((string?)frame.Attribute(SvgNs + "height"));
                image.Alt = frame.Element(SvgNs + "desc")?.Value ?? frame.Element(SvgNs + "title")?.Value;
                output.Add(new InlineImage(image));
            }
        }
        if (frame.Element(DrawNs + "text-box") is { } box)
            ReadBlocks(box.Elements(), trailing);
    }
}
