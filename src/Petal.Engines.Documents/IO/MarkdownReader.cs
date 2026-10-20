using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Extensions.Yaml;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Petal.Engines.Documents.Model;
using MdTable = Markdig.Extensions.Tables.Table;
using MdTableRow = Markdig.Extensions.Tables.TableRow;
using MdTableCell = Markdig.Extensions.Tables.TableCell;
using MdListBlock = Markdig.Syntax.ListBlock;
using MdListItem = Markdig.Syntax.ListItemBlock;
using MdQuote = Markdig.Syntax.QuoteBlock;
using MdCode = Markdig.Syntax.CodeBlock;
using MdParagraph = Markdig.Syntax.ParagraphBlock;
using MdHeading = Markdig.Syntax.HeadingBlock;
using Inline = Petal.Engines.Documents.Model.Inline;

namespace Petal.Engines.Documents.IO;

/// <summary>Reads CommonMark plus GitHub tables, strikethrough and autolinks.</summary>
internal sealed class MarkdownReader
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables()
        .UseGridTables()
        .UseEmphasisExtras()
        .UseAutoLinks()
        .UseTaskLists()
        .UseYamlFrontMatter()
        .Build();

    private readonly string? _baseDir;
    private TextStyle _htmlStyle;

    private MarkdownReader(string? baseDir) => _baseDir = baseDir;

    public static DocModel Read(string path) =>
        Parse(Text.TextFile.ReadAllText(path), Path.GetDirectoryName(Path.GetFullPath(path)));

    public static DocModel Parse(string markdown, string? baseDir = null)
    {
        var md = Markdown.Parse(markdown, Pipeline);
        var reader = new MarkdownReader(baseDir);
        var doc = new DocModel();
        foreach (var block in md)
        {
            if (block is YamlFrontMatterBlock yaml)
            {
                reader.ReadFrontMatter(yaml, doc);
                continue;
            }
            reader.ReadBlock(block, doc.Blocks);
        }
        return doc;
    }

    private void ReadFrontMatter(YamlFrontMatterBlock yaml, DocModel doc)
    {
        foreach (var line in yaml.Lines.Lines)
        {
            var text = line.ToString();
            var colon = text.IndexOf(':');
            if (colon <= 0)
                continue;
            var key = text[..colon].Trim().ToLowerInvariant();
            var value = text[(colon + 1)..].Trim().Trim('"', '\'');
            if (value.Length == 0)
                continue;
            switch (key)
            {
                case "title": doc.Metadata.Title = value; break;
                case "author": doc.Metadata.Author = value; break;
                case "description" or "subject": doc.Metadata.Subject = value; break;
                case "keywords" or "tags": doc.Metadata.Keywords = value.Trim('[', ']'); break;
                case "lang" or "language": doc.Metadata.Language = value; break;
            }
        }
    }

    private void ReadBlock(Markdig.Syntax.Block block, List<Model.Block> target)
    {
        switch (block)
        {
            case MdHeading h:
            {
                var heading = new Heading(h.Level);
                heading.Inlines.AddRange(ReadInlines(h.Inline));
                target.Add(heading);
                break;
            }
            case MdParagraph p:
            {
                var inlines = ReadInlines(p.Inline);
                // A paragraph that is only an image becomes an image block.
                if (inlines is [InlineImage only])
                {
                    target.Add(new ImageBlock(only.Image));
                    break;
                }
                var para = new Paragraph();
                para.Inlines.AddRange(inlines);
                if (para.Inlines.Count > 0)
                    target.Add(para);
                break;
            }
            case MdListBlock l:
            {
                var list = new Model.ListBlock(l.IsOrdered);
                if (l.IsOrdered && int.TryParse(l.OrderedStart, out var start))
                    list.Start = start;
                foreach (var child in l)
                {
                    if (child is not MdListItem li)
                        continue;
                    var item = new ListItem();
                    foreach (var inner in li)
                        ReadBlock(inner, item.Blocks);
                    list.Items.Add(item);
                }
                target.Add(list);
                break;
            }
            case MdQuote q:
            {
                var quote = new Quote();
                foreach (var inner in q)
                    ReadBlock(inner, quote.Blocks);
                target.Add(quote);
                break;
            }
            case HtmlBlock html:
            {
                var parsed = HtmlReader.Parse(html.Lines.ToString(), src => HtmlReader.ResolveFile(_baseDir, src));
                target.AddRange(parsed.Blocks);
                break;
            }
            case MdCode code:
            {
                var cb = new Model.CodeBlock(code.Lines.ToString().TrimEnd('\n', '\r'));
                if (code is FencedCodeBlock fenced && !string.IsNullOrWhiteSpace(fenced.Info))
                    cb.Language = fenced.Info;
                target.Add(cb);
                break;
            }
            case ThematicBreakBlock:
                target.Add(new HorizontalRule());
                break;
            case MdTable t:
                target.Add(ReadTable(t));
                break;
            case ContainerBlock container:
                foreach (var inner in container)
                    ReadBlock(inner, target);
                break;
        }
    }

    private Model.Table ReadTable(MdTable t)
    {
        var table = new Model.Table();
        foreach (var rowObj in t)
        {
            if (rowObj is not MdTableRow mdRow)
                continue;
            var row = new Model.TableRow { IsHeader = mdRow.IsHeader };
            foreach (var cellObj in mdRow)
            {
                if (cellObj is not MdTableCell mdCell)
                    continue;
                var cell = new Model.TableCell { ColSpan = Math.Max(1, mdCell.ColumnSpan), RowSpan = Math.Max(1, mdCell.RowSpan) };
                foreach (var inner in mdCell)
                    ReadBlock(inner, cell.Blocks);
                row.Cells.Add(cell);
            }
            table.Rows.Add(row);
        }
        return table;
    }

    private List<Inline> ReadInlines(ContainerInline? container)
    {
        var result = new List<Inline>();
        if (container is not null)
            AppendInlines(container, TextStyle.None, null, result);
        var coalesced = result.Coalesce();
        coalesced.TrimInlines();
        return coalesced;
    }

    private void AppendInlines(ContainerInline container, TextStyle style, string? link, List<Inline> output)
    {
        foreach (var inline in container)
        {
            var effective = style | _htmlStyle;
            switch (inline)
            {
                case LiteralInline lit:
                    output.Add(new TextRun(lit.Content.ToString(), effective, link));
                    break;
                case HtmlEntityInline entity:
                    output.Add(new TextRun(entity.Transcoded.ToString(), effective, link));
                    break;
                case CodeInline code:
                    output.Add(new TextRun(code.Content, effective | TextStyle.Code, link));
                    break;
                case LineBreakInline br:
                    output.Add(br.IsHard ? new LineBreak() : new TextRun(" ", effective, link));
                    break;
                case AutolinkInline auto:
                    output.Add(new TextRun(auto.Url, effective, auto.IsEmail ? "mailto:" + auto.Url : auto.Url));
                    break;
                case LinkInline { IsImage: true } img:
                {
                    var alt = string.Concat(img.Descendants<LiteralInline>().Select(l => l.Content.ToString()));
                    var image = LoadImage(img.Url, alt);
                    if (image is not null)
                        output.Add(new InlineImage(image));
                    else if (alt.Length > 0)
                        output.Add(new TextRun(alt, effective, link));
                    break;
                }
                case LinkInline lnk:
                    AppendInlines(lnk, style, lnk.Url ?? link, output);
                    break;
                case EmphasisInline em:
                {
                    var add = em.DelimiterChar switch
                    {
                        '~' => em.DelimiterCount == 2 ? TextStyle.Strike : TextStyle.Subscript,
                        '^' => TextStyle.Superscript,
                        '+' => TextStyle.Underline,
                        '=' => TextStyle.Bold,
                        _ => em.DelimiterCount >= 2 ? TextStyle.Bold : TextStyle.Italic,
                    };
                    if (em.DelimiterCount == 3)
                        add = TextStyle.Bold | TextStyle.Italic;
                    AppendInlines(em, style | add, link, output);
                    break;
                }
                case HtmlInline html:
                    ApplyHtmlTag(html.Tag, output, effective, link);
                    break;
                case ContainerInline nested:
                    AppendInlines(nested, style, link, output);
                    break;
            }
        }
    }

    /// <summary>Understands the few inline tags people mix into Markdown: br, u, sup, sub, mark.</summary>
    private void ApplyHtmlTag(string tag, List<Inline> output, TextStyle style, string? link)
    {
        var t = tag.Trim().ToLowerInvariant();
        if (t.StartsWith("<br", StringComparison.Ordinal))
        {
            output.Add(new LineBreak());
            return;
        }
        var closing = t.StartsWith("</", StringComparison.Ordinal);
        var name = t.Trim('<', '>', '/', ' ').Split(' ')[0];
        var flag = name switch
        {
            "u" or "ins" => TextStyle.Underline,
            "sup" => TextStyle.Superscript,
            "sub" => TextStyle.Subscript,
            "b" or "strong" or "mark" => TextStyle.Bold,
            "i" or "em" => TextStyle.Italic,
            "s" or "del" or "strike" => TextStyle.Strike,
            _ => TextStyle.None,
        };
        if (flag == TextStyle.None)
            return;
        _htmlStyle = closing ? _htmlStyle & ~flag : _htmlStyle | flag;
    }

    private ImageData? LoadImage(string? url, string alt)
    {
        if (string.IsNullOrEmpty(url))
            return null;
        try
        {
            var bytes = url.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
                ? HtmlReader.DecodeDataUri(url)
                : HtmlReader.ResolveFile(_baseDir, url);
            return bytes is null ? null : ImageData.Create(bytes, alt);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }
}
