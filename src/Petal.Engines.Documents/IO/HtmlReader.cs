using System.Text.RegularExpressions;
using HtmlAgilityPack;
using Petal.Engines.Documents.Model;

namespace Petal.Engines.Documents.IO;

/// <summary>Reads HTML (and the XHTML inside EPUBs) into the document model.</summary>
internal sealed partial class HtmlReader
{
    private static readonly HashSet<string> Skipped = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "style", "head", "noscript", "template", "svg", "math", "iframe", "object", "button", "select", "textarea", "nav",
    };

    private static readonly HashSet<string> BlockContainers = new(StringComparer.OrdinalIgnoreCase)
    {
        "div", "section", "article", "main", "header", "footer", "aside", "body", "html", "figure", "center", "form", "fieldset", "details", "summary", "address", "hgroup", "dl",
    };

    /// <summary>Resolves an image src (relative path, URL…) to bytes; null when unavailable.</summary>
    private readonly Func<string, byte[]?> _resolveImage;

    private HtmlReader(Func<string, byte[]?> resolveImage) => _resolveImage = resolveImage;

    public static DocModel Read(string path)
    {
        var html = Text.TextFile.ReadAllText(path);
        var baseDir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        return Parse(html, src => ResolveFile(baseDir, src));
    }

    public static DocModel Parse(string html, Func<string, byte[]?>? resolveImage = null)
    {
        var reader = new HtmlReader(resolveImage ?? (src => ResolveFile(null, src)));
        var doc = new DocModel();
        reader.ParseInto(html, doc, doc.Blocks);
        return doc;
    }

    /// <summary>Parses into an existing model (EPUB chapters).</summary>
    public static void ParseInto(string html, DocModel doc, Func<string, byte[]?> resolveImage) =>
        new HtmlReader(resolveImage).ParseInto(html, doc, doc.Blocks);

    private void ParseInto(string html, DocModel doc, List<Block> target)
    {
        var hdoc = new HtmlDocument { OptionFixNestedTags = true, OptionAutoCloseOnEnd = true };
        hdoc.LoadHtml(html);
        var root = hdoc.DocumentNode;

        var title = root.SelectSingleNode("//title")?.InnerText;
        if (!string.IsNullOrWhiteSpace(title) && doc.Metadata.Title is null)
            doc.Metadata.Title = Clean(HtmlEntity.DeEntitize(title));
        foreach (var meta in root.SelectNodes("//meta[@name]") ?? Enumerable.Empty<HtmlNode>())
        {
            var name = meta.GetAttributeValue("name", "").ToLowerInvariant();
            var content = HtmlEntity.DeEntitize(meta.GetAttributeValue("content", "")).Trim();
            if (content.Length == 0) continue;
            if (name == "author") doc.Metadata.Author ??= content;
            else if (name == "description") doc.Metadata.Subject ??= content;
            else if (name == "keywords") doc.Metadata.Keywords ??= content;
        }
        var lang = root.SelectSingleNode("//html")?.GetAttributeValue("lang", null);
        if (!string.IsNullOrWhiteSpace(lang))
            doc.Metadata.Language ??= lang;

        var body = root.SelectSingleNode("//body") ?? root;
        var ctx = new BlockContext(target);
        WalkBlocks(body, ctx);
        ctx.Flush();
    }

    /// <summary>Collects inline content into a pending paragraph until a block element ends it.</summary>
    private sealed class BlockContext(List<Block> target)
    {
        public List<Block> Target { get; } = target;
        public List<Inline> Pending { get; } = [];
        public Alignment Alignment { get; set; }

        public void Flush()
        {
            if (Pending.Count == 0)
                return;
            var inlines = Pending.Coalesce();
            Pending.Clear();
            inlines.TrimInlines();
            if (inlines.Count == 0)
                return;
            var p = new Paragraph { Alignment = Alignment };
            p.Inlines.AddRange(inlines);
            Target.Add(p);
            Alignment = Alignment.Start;
        }
    }

    private void WalkBlocks(HtmlNode parent, BlockContext ctx)
    {
        foreach (var node in parent.ChildNodes)
            WalkNode(node, ctx, TextStyle.None, null, preformatted: false);
    }

    private void WalkNode(HtmlNode node, BlockContext ctx, TextStyle style, string? link, bool preformatted)
    {
        if (node.NodeType == HtmlNodeType.Comment)
            return;
        if (node.NodeType == HtmlNodeType.Text)
        {
            var raw = HtmlEntity.DeEntitize(((HtmlTextNode)node).Text);
            var text = preformatted ? raw : CollapseWhitespace(raw);
            if (text.Length > 0)
                ctx.Pending.Add(new TextRun(text, style, link));
            return;
        }
        if (node.NodeType != HtmlNodeType.Element)
            return;

        var name = node.Name.ToLowerInvariant();
        if (Skipped.Contains(name) || IsHidden(node))
            return;

        var css = node.GetAttributeValue("style", "");
        if (BreaksBefore(css))
        {
            ctx.Flush();
            ctx.Target.Add(new PageBreak());
        }

        switch (name)
        {
            case "h1" or "h2" or "h3" or "h4" or "h5" or "h6":
            {
                ctx.Flush();
                var h = new Heading(name[1] - '0');
                h.Inlines.AddRange(CollectInlines(node, style));
                if (h.Inlines.Count > 0)
                    ctx.Target.Add(h);
                break;
            }
            case "p":
            {
                ctx.Flush();
                ctx.Alignment = AlignmentOf(node);
                foreach (var child in node.ChildNodes)
                    WalkNode(child, ctx, style, link, preformatted);
                ctx.Flush();
                break;
            }
            case "br":
                ctx.Pending.Add(new LineBreak());
                break;
            case "hr":
                ctx.Flush();
                ctx.Target.Add(new HorizontalRule());
                break;
            case "ul" or "ol" or "menu":
                ctx.Flush();
                ctx.Target.Add(ReadList(node, name == "ol", style));
                break;
            case "table":
                ctx.Flush();
                if (ReadTable(node, style) is { } table)
                    ctx.Target.Add(table);
                break;
            case "blockquote":
            {
                ctx.Flush();
                var q = new Quote();
                var inner = new BlockContext(q.Blocks);
                foreach (var child in node.ChildNodes)
                    WalkNode(child, inner, style, link, preformatted);
                inner.Flush();
                if (q.Blocks.Count > 0)
                    ctx.Target.Add(q);
                break;
            }
            case "pre":
            {
                ctx.Flush();
                var text = HtmlEntity.DeEntitize(node.InnerText).Replace("\r\n", "\n").Trim('\n');
                ctx.Target.Add(new CodeBlock(text));
                break;
            }
            case "img":
            {
                if (ReadImage(node) is { } image)
                {
                    // Images directly in a block container become their own block.
                    if (ctx.Pending.Count == 0 && node.ParentNode is { } parentNode && BlockContainers.Contains(parentNode.Name))
                        ctx.Target.Add(new ImageBlock(image));
                    else
                        ctx.Pending.Add(new InlineImage(image));
                }
                break;
            }
            case "dt" or "dd" or "li" or "figcaption" or "caption" or "legend":
                ctx.Flush();
                foreach (var child in node.ChildNodes)
                    WalkNode(child, ctx, name == "dt" ? style | TextStyle.Bold : style, link, preformatted);
                ctx.Flush();
                break;
            default:
            {
                if (BlockContainers.Contains(name))
                {
                    ctx.Flush();
                    foreach (var child in node.ChildNodes)
                        WalkNode(child, ctx, style, link, preformatted);
                    ctx.Flush();
                }
                else
                {
                    var childStyle = style | InlineStyle(name, css);
                    var childLink = link;
                    if (name == "a" && node.GetAttributeValue("href", null) is { Length: > 0 } href && !href.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase))
                        childLink = HtmlEntity.DeEntitize(href);
                    foreach (var child in node.ChildNodes)
                        WalkNode(child, ctx, childStyle, childLink, preformatted);
                }
                break;
            }
        }

        if (BreaksAfter(css))
        {
            ctx.Flush();
            ctx.Target.Add(new PageBreak());
        }
    }

    private List<Inline> CollectInlines(HtmlNode node, TextStyle style)
    {
        var temp = new List<Block>();
        var ctx = new BlockContext(temp);
        foreach (var child in node.ChildNodes)
            WalkNode(child, ctx, style, null, false);
        var inlines = ctx.Pending.Coalesce();
        // Nested blocks inside a heading are rare; fold their text in.
        foreach (var b in temp.OfType<Paragraph>())
            inlines.AddRange(b.Inlines);
        inlines.TrimInlines();
        return inlines;
    }

    private ListBlock ReadList(HtmlNode node, bool ordered, TextStyle style)
    {
        var list = new ListBlock(ordered);
        if (ordered && int.TryParse(node.GetAttributeValue("start", "1"), out var start))
            list.Start = start;
        foreach (var li in node.ChildNodes.Where(n => n.NodeType == HtmlNodeType.Element))
        {
            var item = new ListItem();
            var ctx = new BlockContext(item.Blocks);
            if (li.Name.Equals("li", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var child in li.ChildNodes)
                    WalkNode(child, ctx, style, null, false);
            }
            else
            {
                WalkNode(li, ctx, style, null, false);
            }
            ctx.Flush();
            if (item.Blocks.Count > 0)
                list.Items.Add(item);
        }
        return list;
    }

    private Table? ReadTable(HtmlNode node, TextStyle style)
    {
        var table = new Table();
        var rows = node.Descendants("tr").Where(tr => tr.Ancestors("table").FirstOrDefault() == node);
        foreach (var tr in rows)
        {
            var row = new TableRow
            {
                IsHeader = tr.ParentNode?.Name.Equals("thead", StringComparison.OrdinalIgnoreCase) == true,
            };
            var cells = tr.ChildNodes.Where(c => c.Name is "td" or "th" or "TD" or "TH").ToList();
            if (cells.Count > 0 && cells.All(c => c.Name.Equals("th", StringComparison.OrdinalIgnoreCase)))
                row.IsHeader = true;
            foreach (var td in cells)
            {
                var cell = new TableCell
                {
                    ColSpan = Math.Max(1, td.GetAttributeValue("colspan", 1)),
                    RowSpan = Math.Max(1, td.GetAttributeValue("rowspan", 1)),
                };
                var ctx = new BlockContext(cell.Blocks);
                var cellStyle = td.Name.Equals("th", StringComparison.OrdinalIgnoreCase) && !row.IsHeader ? style | TextStyle.Bold : style;
                foreach (var child in td.ChildNodes)
                    WalkNode(child, ctx, cellStyle, null, false);
                ctx.Flush();
                row.Cells.Add(cell);
            }
            if (row.Cells.Count > 0)
                table.Rows.Add(row);
        }
        // Header rows must lead the table.
        var seenBody = false;
        foreach (var row in table.Rows)
        {
            if (!row.IsHeader) seenBody = true;
            else if (seenBody) row.IsHeader = false;
        }
        return table.Rows.Count > 0 ? table : null;
    }

    private ImageData? ReadImage(HtmlNode node)
    {
        var src = HtmlEntity.DeEntitize(node.GetAttributeValue("src", ""));
        if (src.Length == 0)
            return null;
        byte[]? bytes = null;
        if (src.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            bytes = DecodeDataUri(src);
        else
        {
            try { bytes = _resolveImage(src); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
        }
        if (bytes is null)
            return null;
        var image = ImageData.Create(bytes, HtmlEntity.DeEntitize(node.GetAttributeValue("alt", "")));
        if (image is null)
            return null;
        var w = node.GetAttributeValue("width", 0);
        if (w > 0)
            image.WidthPt = w * 0.75;
        var h = node.GetAttributeValue("height", 0);
        if (h > 0)
            image.HeightPt = h * 0.75;
        if (image.WidthPt is not null && image.HeightPt is null && image.PixelWidth > 0)
            image.HeightPt = image.WidthPt * image.PixelHeight / image.PixelWidth;
        return image;
    }

    public static byte[]? DecodeDataUri(string uri)
    {
        var comma = uri.IndexOf(',');
        if (comma < 0)
            return null;
        var header = uri[..comma];
        var data = uri[(comma + 1)..];
        try
        {
            return header.Contains(";base64", StringComparison.OrdinalIgnoreCase)
                ? Convert.FromBase64String(Uri.UnescapeDataString(data))
                : System.Text.Encoding.UTF8.GetBytes(Uri.UnescapeDataString(data));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>Loads a local file referenced by a relative or file: URL. Web URLs are never fetched.</summary>
    public static byte[]? ResolveFile(string? baseDir, string src)
    {
        if (src.StartsWith("http:", StringComparison.OrdinalIgnoreCase) || src.StartsWith("https:", StringComparison.OrdinalIgnoreCase) || src.StartsWith("//", StringComparison.Ordinal))
            return null;
        string path;
        if (src.StartsWith("file:", StringComparison.OrdinalIgnoreCase) && Uri.TryCreate(src, UriKind.Absolute, out var uri))
            path = uri.LocalPath;
        else if (baseDir is null)
            return null;
        else
        {
            var clean = Uri.UnescapeDataString(src.Split('?', '#')[0]).Replace('/', Path.DirectorySeparatorChar);
            path = Path.IsPathRooted(clean) ? clean : Path.GetFullPath(Path.Combine(baseDir, clean));
        }
        return File.Exists(path) ? File.ReadAllBytes(path) : null;
    }

    private static TextStyle InlineStyle(string name, string css)
    {
        var s = name switch
        {
            "b" or "strong" => TextStyle.Bold,
            "i" or "em" or "cite" or "dfn" or "var" => TextStyle.Italic,
            "u" or "ins" => TextStyle.Underline,
            "s" or "strike" or "del" => TextStyle.Strike,
            "code" or "kbd" or "samp" or "tt" => TextStyle.Code,
            "sup" => TextStyle.Superscript,
            "sub" => TextStyle.Subscript,
            _ => TextStyle.None,
        };
        if (css.Length > 0)
        {
            var lower = css.ToLowerInvariant();
            if (BoldCss().IsMatch(lower)) s |= TextStyle.Bold;
            if (lower.Contains("font-style") && lower.Contains("italic")) s |= TextStyle.Italic;
            if (lower.Contains("underline")) s |= TextStyle.Underline;
            if (lower.Contains("line-through")) s |= TextStyle.Strike;
        }
        return s;
    }

    private static Alignment AlignmentOf(HtmlNode node)
    {
        var align = node.GetAttributeValue("align", "").ToLowerInvariant();
        var css = node.GetAttributeValue("style", "").ToLowerInvariant();
        var m = TextAlignCss().Match(css);
        if (m.Success) align = m.Groups[1].Value;
        return align switch
        {
            "center" => Alignment.Center,
            "right" or "end" => Alignment.End,
            "justify" => Alignment.Justify,
            _ => Alignment.Start,
        };
    }

    private static bool IsHidden(HtmlNode node)
    {
        if (node.Attributes.Contains("hidden"))
            return true;
        var css = node.GetAttributeValue("style", "");
        return css.Length > 0 && HiddenCss().IsMatch(css.ToLowerInvariant());
    }

    private static bool BreaksBefore(string css) => css.Length > 0 && BreakBeforeCss().IsMatch(css.ToLowerInvariant());

    private static bool BreaksAfter(string css) => css.Length > 0 && BreakAfterCss().IsMatch(css.ToLowerInvariant());

    private static string CollapseWhitespace(string text) => Whitespace().Replace(text, " ");

    private static string Clean(string s) => Whitespace().Replace(s, " ").Trim();

    [GeneratedRegex(@"[\s-[ ]]+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"font-weight\s*:\s*(bold|bolder|[6-9]00)")]
    private static partial Regex BoldCss();

    [GeneratedRegex(@"text-align\s*:\s*(\w+)")]
    private static partial Regex TextAlignCss();

    [GeneratedRegex(@"display\s*:\s*none|visibility\s*:\s*hidden")]
    private static partial Regex HiddenCss();

    [GeneratedRegex(@"(page-break-before|break-before)\s*:\s*(always|page)")]
    private static partial Regex BreakBeforeCss();

    [GeneratedRegex(@"(page-break-after|break-after)\s*:\s*(always|page)")]
    private static partial Regex BreakAfterCss();
}
