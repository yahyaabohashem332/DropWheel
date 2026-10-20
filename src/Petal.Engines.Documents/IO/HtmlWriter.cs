using System.Net;
using System.Text;
using Petal.Engines.Documents.Model;

namespace Petal.Engines.Documents.IO;

/// <summary>Writes the document model as HTML5. The markup is also well-formed XHTML, so EPUB reuses it.</summary>
internal sealed class HtmlWriter
{
    public const string Stylesheet = """
        body { font-family: "Segoe UI", system-ui, -apple-system, sans-serif; line-height: 1.55; max-width: 46em; margin: 2em auto; padding: 0 1.2em; color: #1f1f1f; }
        h1, h2, h3, h4, h5, h6 { line-height: 1.25; margin: 1.4em 0 0.5em; }
        p { margin: 0 0 0.8em; }
        img { max-width: 100%; height: auto; }
        table { border-collapse: collapse; margin: 0.8em 0 1.2em; }
        th, td { border: 1px solid #c8c8c8; padding: 0.35em 0.6em; vertical-align: top; text-align: start; }
        th { background: #f2f2f2; }
        pre { background: #f5f5f5; padding: 0.8em 1em; overflow-x: auto; white-space: pre-wrap; }
        code { font-family: Consolas, "Cascadia Mono", monospace; font-size: 0.92em; }
        blockquote { margin: 0.8em 0; padding: 0 1em; border-inline-start: 3px solid #d0d0d0; color: #444; }
        hr { border: 0; border-top: 1px solid #d0d0d0; margin: 1.6em 0; }
        .page-break { break-after: page; page-break-after: always; }
        """;

    private readonly StringBuilder _sb = new();

    /// <summary>Returns the src for a picture; the default embeds it as a data URI.</summary>
    public Func<ImageData, string> ImageSource { get; init; } = DataUri;

    /// <summary>EPUB documents need the XHTML namespace and an XML declaration.</summary>
    public bool Xhtml { get; init; }

    /// <summary>Linked stylesheet instead of the inline one (EPUB).</summary>
    public string? StylesheetHref { get; init; }

    public static string DataUri(ImageData image) => $"data:{image.MediaType};base64,{Convert.ToBase64String(image.Bytes)}";

    public static void Write(DocModel doc, string path) =>
        File.WriteAllText(path, new HtmlWriter().Render(doc.Blocks, doc.EffectiveTitle() ?? Path.GetFileNameWithoutExtension(path), doc.Metadata), Petal.Engines.Documents.Text.TextFile.Utf8NoBom);

    public string Render(IEnumerable<Block> blocks, string title, DocMetadata? metadata = null)
    {
        _sb.Clear();
        var lang = metadata?.Language;
        if (Xhtml)
        {
            _sb.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<!DOCTYPE html>\n");
            _sb.Append("<html xmlns=\"http://www.w3.org/1999/xhtml\" xmlns:epub=\"http://www.idpf.org/2007/ops\"");
            if (!string.IsNullOrEmpty(lang))
                _sb.Append(" xml:lang=\"").Append(Attr(lang)).Append("\" lang=\"").Append(Attr(lang)).Append('"');
            _sb.Append(">\n");
        }
        else
        {
            _sb.Append("<!DOCTYPE html>\n<html");
            if (!string.IsNullOrEmpty(lang))
                _sb.Append(" lang=\"").Append(Attr(lang)).Append('"');
            _sb.Append(">\n");
        }

        _sb.Append("<head>\n<meta charset=\"utf-8\" />\n");
        if (!Xhtml)
            _sb.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\" />\n");
        _sb.Append("<title>").Append(Text(title)).Append("</title>\n");
        if (!string.IsNullOrWhiteSpace(metadata?.Author))
            _sb.Append("<meta name=\"author\" content=\"").Append(Attr(metadata.Author!)).Append("\" />\n");
        if (StylesheetHref is not null)
            _sb.Append("<link rel=\"stylesheet\" type=\"text/css\" href=\"").Append(Attr(StylesheetHref)).Append("\" />\n");
        else
            _sb.Append("<style>\n").Append(Stylesheet).Append("\n</style>\n");
        _sb.Append("</head>\n<body>\n");
        WriteBlocks(blocks);
        _sb.Append("</body>\n</html>\n");
        return _sb.ToString();
    }

    private void WriteBlocks(IEnumerable<Block> blocks)
    {
        foreach (var block in blocks)
            WriteBlock(block);
    }

    private void WriteBlock(Block block)
    {
        switch (block)
        {
            case Heading h:
                _sb.Append("<h").Append(h.Level).Append(" dir=\"auto\">");
                WriteInlines(h.Inlines);
                _sb.Append("</h").Append(h.Level).Append(">\n");
                break;
            case Paragraph p:
                _sb.Append("<p dir=\"auto\"");
                if (p.Alignment != Alignment.Start)
                    _sb.Append(" style=\"text-align: ").Append(p.Alignment switch { Alignment.Center => "center", Alignment.End => "end", _ => "justify" }).Append('"');
                _sb.Append('>');
                WriteInlines(p.Inlines);
                _sb.Append("</p>\n");
                break;
            case ListBlock l:
                var tag = l.Ordered ? "ol" : "ul";
                _sb.Append('<').Append(tag);
                if (l.Ordered && l.Start != 1)
                    _sb.Append(" start=\"").Append(l.Start).Append('"');
                _sb.Append(">\n");
                foreach (var item in l.Items)
                {
                    _sb.Append("<li dir=\"auto\">");
                    // A single paragraph is written inline so simple lists stay compact.
                    if (item.Blocks is [Paragraph only])
                        WriteInlines(only.Inlines);
                    else
                    {
                        for (var i = 0; i < item.Blocks.Count; i++)
                        {
                            if (i == 0 && item.Blocks[0] is Paragraph first)
                                WriteInlines(first.Inlines);
                            else
                                WriteBlock(item.Blocks[i]);
                        }
                    }
                    _sb.Append("</li>\n");
                }
                _sb.Append("</").Append(tag).Append(">\n");
                break;
            case Table t:
                WriteTable(t);
                break;
            case ImageBlock img:
                _sb.Append("<p>");
                WriteImage(img.Image);
                _sb.Append("</p>\n");
                break;
            case CodeBlock c:
                _sb.Append("<pre><code>").Append(Text(c.Text)).Append("</code></pre>\n");
                break;
            case Quote q:
                _sb.Append("<blockquote>\n");
                WriteBlocks(q.Blocks);
                _sb.Append("</blockquote>\n");
                break;
            case HorizontalRule:
                _sb.Append("<hr />\n");
                break;
            case PageBreak:
                _sb.Append("<div class=\"page-break\" style=\"break-after: page; page-break-after: always\"></div>\n");
                break;
        }
    }

    private void WriteTable(Table t)
    {
        _sb.Append("<table>\n");
        var headerRows = t.Rows.TakeWhile(r => r.IsHeader).ToList();
        if (headerRows.Count > 0)
        {
            _sb.Append("<thead>\n");
            foreach (var row in headerRows)
                WriteRow(row, "th");
            _sb.Append("</thead>\n");
        }
        _sb.Append("<tbody>\n");
        foreach (var row in t.Rows.Skip(headerRows.Count))
            WriteRow(row, row.IsHeader ? "th" : "td");
        _sb.Append("</tbody>\n</table>\n");
    }

    private void WriteRow(TableRow row, string cellTag)
    {
        _sb.Append("<tr>");
        foreach (var cell in row.Cells)
        {
            _sb.Append('<').Append(cellTag).Append(" dir=\"auto\"");
            if (cell.ColSpan > 1) _sb.Append(" colspan=\"").Append(cell.ColSpan).Append('"');
            if (cell.RowSpan > 1) _sb.Append(" rowspan=\"").Append(cell.RowSpan).Append('"');
            _sb.Append('>');
            for (var i = 0; i < cell.Blocks.Count; i++)
            {
                if (cell.Blocks[i] is Paragraph p)
                {
                    if (i > 0) _sb.Append("<br />");
                    WriteInlines(p.Inlines);
                }
                else
                    WriteBlock(cell.Blocks[i]);
            }
            _sb.Append("</").Append(cellTag).Append('>');
        }
        _sb.Append("</tr>\n");
    }

    private void WriteInlines(IEnumerable<Inline> inlines)
    {
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case TextRun run:
                    WriteRun(run);
                    break;
                case LineBreak:
                    _sb.Append("<br />");
                    break;
                case InlineImage img:
                    WriteImage(img.Image);
                    break;
            }
        }
    }

    private void WriteRun(TextRun run)
    {
        var tags = new List<string>();
        if (run.Link is not null)
        {
            _sb.Append("<a href=\"").Append(Attr(run.Link)).Append("\">");
        }
        if (run.Has(TextStyle.Bold)) tags.Add("strong");
        if (run.Has(TextStyle.Italic)) tags.Add("em");
        if (run.Has(TextStyle.Underline) && run.Link is null) tags.Add("u");
        if (run.Has(TextStyle.Strike)) tags.Add("s");
        if (run.Has(TextStyle.Code)) tags.Add("code");
        if (run.Has(TextStyle.Superscript)) tags.Add("sup");
        if (run.Has(TextStyle.Subscript)) tags.Add("sub");
        foreach (var t in tags)
            _sb.Append('<').Append(t).Append('>');
        _sb.Append(Text(run.Text));
        for (var i = tags.Count - 1; i >= 0; i--)
            _sb.Append("</").Append(tags[i]).Append('>');
        if (run.Link is not null)
            _sb.Append("</a>");
    }

    private void WriteImage(ImageData image)
    {
        _sb.Append("<img src=\"").Append(Attr(ImageSource(image))).Append("\" alt=\"").Append(Attr(image.Alt ?? "")).Append('"');
        if (image.WidthPt is { } w)
            _sb.Append(" width=\"").Append((int)Math.Round(w / 0.75)).Append('"');
        _sb.Append(" />");
    }

    public static string Text(string s)
    {
        var encoded = WebUtility.HtmlEncode(s);
        // HtmlEncode turns every non-ASCII character into an entity; keep real characters for readability.
        return DecodeNumericEntities(encoded);
    }

    public static string Attr(string s) => Text(s).Replace("\"", "&quot;");

    private static string DecodeNumericEntities(string s)
    {
        if (!s.Contains("&#", StringComparison.Ordinal))
            return s;
        var sb = new StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == '&' && i + 2 < s.Length && s[i + 1] == '#')
            {
                var end = s.IndexOf(';', i);
                if (end > i && int.TryParse(s.AsSpan(i + 2, end - i - 2), out var code) && code >= 160)
                {
                    sb.Append(char.ConvertFromUtf32(code));
                    i = end;
                    continue;
                }
            }
            sb.Append(s[i]);
        }
        return sb.ToString();
    }
}
