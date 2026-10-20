using System.Text;
using Petal.Engines.Documents.Model;

namespace Petal.Engines.Documents.IO;

/// <summary>
/// Writes GitHub-flavoured Markdown. Pictures are saved to a "&lt;name&gt; images" folder next to
/// the file and linked relatively, the way most Markdown editors expect.
/// </summary>
internal sealed class MarkdownWriter
{
    private readonly StringBuilder _sb = new();
    private readonly Func<ImageData, string> _imagePath;

    private MarkdownWriter(Func<ImageData, string> imagePath) => _imagePath = imagePath;

    /// <summary>Writes <paramref name="doc"/> to <paramref name="tempPath"/>; images go beside <paramref name="finalPath"/>.</summary>
    /// <returns>The images folder when one was created.</returns>
    public static string? Write(DocModel doc, string tempPath, string finalPath)
    {
        string? folder = null;
        var saved = new Dictionary<ImageData, string>(ReferenceEqualityComparer.Instance);
        var writer = new MarkdownWriter(image =>
        {
            if (saved.TryGetValue(image, out var rel))
                return rel;
            if (folder is null)
            {
                var dir = Path.GetDirectoryName(Path.GetFullPath(finalPath))!;
                folder = Petal.Core.Jobs.OutputNaming.Unique(dir, Path.GetFileNameWithoutExtension(finalPath) + " images", null);
                Directory.CreateDirectory(folder);
            }
            var name = $"image{saved.Count + 1}.{image.Extension}";
            File.WriteAllBytes(Path.Combine(folder, name), image.Bytes);
            rel = Uri.EscapeDataString(Path.GetFileName(folder)) + "/" + name;
            saved[image] = rel;
            return rel;
        });
        File.WriteAllText(tempPath, writer.Render(doc), Text.TextFile.Utf8NoBom);
        return folder;
    }

    public static string ToMarkdown(DocModel doc, Func<ImageData, string>? imagePath = null) =>
        new MarkdownWriter(imagePath ?? HtmlWriter.DataUri).Render(doc);

    private string Render(DocModel doc)
    {
        _sb.Clear();
        WriteBlocks(doc.Blocks, "");
        return _sb.ToString().TrimEnd() + "\n";
    }

    private void WriteBlocks(IReadOnlyList<Block> blocks, string indent)
    {
        for (var i = 0; i < blocks.Count; i++)
        {
            WriteBlock(blocks[i], indent);
            // Two lists in a row would merge; a comment keeps them apart.
            if (blocks[i] is ListBlock && i + 1 < blocks.Count && blocks[i + 1] is ListBlock)
                _sb.Append(indent).Append("<!-- -->\n\n");
        }
    }

    private void WriteBlock(Block block, string indent)
    {
        switch (block)
        {
            case Heading h:
                _sb.Append(indent).Append('#', h.Level).Append(' ').Append(InlineText(h.Inlines, " ")).Append("\n\n");
                break;
            case Paragraph p:
            {
                var text = InlineText(p.Inlines);
                if (text.Trim().Length == 0)
                    break;
                AppendLines(EscapeLineStarts(text), indent);
                _sb.Append('\n');
                break;
            }
            case ListBlock l:
                WriteList(l, indent);
                _sb.Append('\n');
                break;
            case Table t:
                WriteTable(t, indent);
                _sb.Append('\n');
                break;
            case ImageBlock img:
                _sb.Append(indent).Append(ImageMarkdown(img.Image)).Append("\n\n");
                break;
            case CodeBlock c:
            {
                var fence = c.Text.Contains("```", StringComparison.Ordinal) ? "~~~~" : "```";
                _sb.Append(indent).Append(fence).Append(c.Language ?? "").Append('\n');
                AppendLines(c.Text, indent);
                _sb.Append(indent).Append(fence).Append("\n\n");
                break;
            }
            case Quote q:
            {
                var inner = new MarkdownWriter(_imagePath);
                inner.WriteBlocks(q.Blocks, "");
                foreach (var line in inner._sb.ToString().TrimEnd().Split('\n'))
                    _sb.Append(indent).Append(line.Length == 0 ? ">" : "> " + line).Append('\n');
                _sb.Append('\n');
                break;
            }
            case HorizontalRule:
                _sb.Append(indent).Append("---\n\n");
                break;
            case PageBreak:
                break;
        }
    }

    private void WriteList(ListBlock list, string indent)
    {
        var number = list.Start;
        foreach (var item in list.Items)
        {
            var marker = list.Ordered ? $"{number++}. " : "- ";
            var childIndent = indent + new string(' ', marker.Length);
            var first = true;
            foreach (var block in item.Blocks)
            {
                if (first && block is Paragraph p)
                {
                    var text = EscapeLineStarts(InlineText(p.Inlines));
                    var lines = text.Split('\n');
                    _sb.Append(indent).Append(marker).Append(lines[0]).Append('\n');
                    foreach (var line in lines.Skip(1))
                        _sb.Append(childIndent).Append(line).Append('\n');
                }
                else
                {
                    if (first)
                        _sb.Append(indent).Append(marker.TrimEnd()).Append('\n');
                    if (block is ListBlock nested)
                        WriteList(nested, childIndent);
                    else
                    {
                        _sb.Append('\n');
                        WriteBlock(block, childIndent);
                    }
                }
                first = false;
            }
            if (item.Blocks.Count == 0)
                _sb.Append(indent).Append(marker.TrimEnd()).Append('\n');
        }
    }

    private void WriteTable(Table table, string indent)
    {
        var columns = Math.Max(1, table.ColumnCount);
        var rows = table.Rows.Select(r =>
        {
            var cells = new List<string>();
            foreach (var c in r.Cells)
            {
                cells.Add(CellText(c));
                for (var k = 1; k < c.ColSpan; k++)
                    cells.Add("");
            }
            while (cells.Count < columns) cells.Add("");
            return cells;
        }).ToList();
        if (rows.Count == 0)
            return;

        // Pipe tables always have a header line; use the first row when none is marked.
        var header = rows[0];
        var body = rows.Skip(1);
        var widths = Enumerable.Range(0, columns).Select(i => Math.Max(3, rows.Max(r => r[i].Length))).ToArray();
        void Row(List<string> cells) =>
            _sb.Append(indent).Append("| ").Append(string.Join(" | ", cells.Select((c, i) => c.PadRight(Math.Min(widths[i], 40))))).Append(" |\n");
        Row(header);
        _sb.Append(indent).Append("| ").Append(string.Join(" | ", widths.Select(w => new string('-', Math.Min(w, 40))))).Append(" |\n");
        foreach (var r in body)
            Row(r);
    }

    private string CellText(TableCell cell)
    {
        var parts = new List<string>();
        foreach (var block in cell.Blocks)
        {
            parts.Add(block switch
            {
                Paragraph p => InlineText(p.Inlines, "<br>"),
                Heading h => InlineText(h.Inlines, " "),
                ImageBlock img => ImageMarkdown(img.Image),
                ListBlock l => string.Join("<br>", l.Items.Select(i => "• " + i.Blocks.PlainText())),
                _ => block is CodeBlock c ? c.Text : "",
            });
        }
        return string.Join("<br>", parts.Where(p => p.Length > 0)).Replace("\n", "<br>").Replace("|", "\\|");
    }

    private string InlineText(IEnumerable<Inline> inlines, string lineBreak = "\\\n")
    {
        var sb = new StringBuilder();
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case TextRun run:
                    sb.Append(RunText(run));
                    break;
                case LineBreak:
                    sb.Append(lineBreak);
                    break;
                case InlineImage img:
                    sb.Append(ImageMarkdown(img.Image));
                    break;
            }
        }
        return sb.ToString();
    }

    private static string RunText(TextRun run)
    {
        if (run.Text.Length == 0)
            return "";
        string core;
        if (run.Has(TextStyle.Code))
        {
            var ticks = run.Text.Contains('`') ? "``" : "`";
            var pad = run.Text.StartsWith('`') || run.Text.EndsWith('`') ? " " : "";
            core = ticks + pad + run.Text + pad + ticks;
        }
        else
            core = Escape(run.Text);

        // Emphasis markers must hug the text, so surrounding spaces stay outside them.
        var lead = core.Length - core.TrimStart().Length;
        var trail = core.Length - core.TrimEnd().Length;
        var inner = core.Trim();
        if (inner.Length == 0)
            return core;
        if (run.Has(TextStyle.Strike)) inner = "~~" + inner + "~~";
        if (run.Has(TextStyle.Italic)) inner = "*" + inner + "*";
        if (run.Has(TextStyle.Bold)) inner = "**" + inner + "**";
        if (run.Has(TextStyle.Underline) && run.Link is null) inner = "<u>" + inner + "</u>";
        if (run.Has(TextStyle.Superscript)) inner = "<sup>" + inner + "</sup>";
        if (run.Has(TextStyle.Subscript)) inner = "<sub>" + inner + "</sub>";
        if (run.Link is not null)
            inner = "[" + inner + "](" + LinkTarget(run.Link) + ")";
        return core[..lead] + inner + core[(core.Length - trail)..];
    }

    private string ImageMarkdown(ImageData image) =>
        "![" + Escape(image.Alt ?? "") + "](" + LinkTarget(_imagePath(image)) + ")";

    private static string LinkTarget(string url) =>
        url.Contains(' ') || url.Contains('(') || url.Contains(')') ? "<" + url.Replace("<", "%3C").Replace(">", "%3E") + ">" : url;

    private static string Escape(string text)
    {
        var sb = new StringBuilder(text.Length + 8);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            switch (c)
            {
                case '\\' or '`' or '*' or '[' or ']' or '<' or '>':
                    sb.Append('\\').Append(c);
                    break;
                case '_':
                    // Underscores inside words never start emphasis, so only escape them at word edges.
                    var before = i > 0 && char.IsLetterOrDigit(text[i - 1]);
                    var after = i + 1 < text.Length && char.IsLetterOrDigit(text[i + 1]);
                    if (!(before && after)) sb.Append('\\');
                    sb.Append(c);
                    break;
                case '\n':
                    sb.Append(' ');
                    break;
                default:
                    sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }

    /// <summary>Stops text that happens to begin like a heading, list or quote from becoming one.</summary>
    private static string EscapeLineStarts(string text)
    {
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.TrimStart();
            if (trimmed.Length == 0)
                continue;
            if (trimmed[0] is '#' or '+' or '=' || trimmed.StartsWith("- ", StringComparison.Ordinal) || trimmed == "-" || trimmed.StartsWith("---", StringComparison.Ordinal))
                lines[i] = line[..(line.Length - trimmed.Length)] + "\\" + trimmed;
            else
            {
                var digits = 0;
                while (digits < trimmed.Length && char.IsAsciiDigit(trimmed[digits])) digits++;
                if (digits > 0 && digits < trimmed.Length && trimmed[digits] is '.' or ')' && (digits + 1 == trimmed.Length || trimmed[digits + 1] == ' '))
                    lines[i] = line[..(line.Length - trimmed.Length)] + trimmed[..digits] + "\\" + trimmed[digits..];
            }
        }
        return string.Join('\n', lines);
    }

    private void AppendLines(string text, string indent)
    {
        foreach (var line in text.Split('\n'))
            _sb.Append(indent).Append(line).Append('\n');
    }
}
