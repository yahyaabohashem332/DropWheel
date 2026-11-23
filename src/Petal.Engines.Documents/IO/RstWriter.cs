using System.Globalization;
using System.Text;
using Petal.Engines.Documents.Model;

namespace Petal.Engines.Documents.IO;

/// <summary>
/// Writes reStructuredText as docutils and Sphinx read it: titles underlined (the first also overlined), lists,
/// grid tables, literal and code blocks, quotes, and pictures saved to a "&lt;name&gt; images" folder beside the file.
/// </summary>
internal sealed class RstWriter
{
    // One adornment a level: "=" over and under for the title, then "=", "-", "~", "^" and '"' under.
    private static readonly char[] Adornments = ['=', '=', '-', '~', '^', '"'];

    private readonly StringBuilder _sb = new();
    private readonly Func<ImageData, string> _imagePath;

    private RstWriter(Func<ImageData, string> imagePath) => _imagePath = imagePath;

    /// <summary>Writes <paramref name="doc"/> to <paramref name="tempPath"/>; images go beside <paramref name="finalPath"/>.</summary>
    /// <returns>The images folder when one was made.</returns>
    public static string? Write(DocModel doc, string tempPath, string finalPath)
    {
        string? folder = null;
        var saved = new Dictionary<ImageData, string>(ReferenceEqualityComparer.Instance);
        var writer = new RstWriter(image =>
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
            rel = Path.GetFileName(folder) + "/" + name;
            saved[image] = rel;
            return rel;
        });
        File.WriteAllText(tempPath, writer.Render(doc), Text.TextFile.Utf8NoBom);
        return folder;
    }

    public static string ToRst(DocModel doc, Func<ImageData, string>? imagePath = null) =>
        new RstWriter(imagePath ?? (_ => "image.png")).Render(doc);

    private string Render(DocModel doc)
    {
        _sb.Clear();
        WriteBlocks(doc.Blocks, "");
        // A transition can't end a document.
        var text = _sb.ToString().TrimEnd();
        while (text.EndsWith("----------", StringComparison.Ordinal))
            text = text[..^10].TrimEnd();
        return text + "\n";
    }

    private void WriteBlocks(IReadOnlyList<Block> blocks, string indent)
    {
        for (var i = 0; i < blocks.Count; i++)
        {
            // Two lists in a row would be read as one; an empty comment keeps them apart.
            if (i > 0 && blocks[i] is ListBlock && blocks[i - 1] is ListBlock)
                _sb.Append(indent).Append("..\n\n");
            // A transition can't open a document or follow another.
            if (blocks[i] is HorizontalRule && (_sb.Length == 0 || i > 0 && blocks[i - 1] is HorizontalRule))
                continue;
            WriteBlock(blocks[i], indent);
        }
    }

    private void WriteBlock(Block block, string indent)
    {
        switch (block)
        {
            case Heading h:
            {
                var text = Inlines(h.Inlines, oneLine: true).Trim();
                if (text.Length == 0)
                    break;
                // Titles exist only at the top level; inside a list or quote they are bold.
                if (indent.Length > 0)
                {
                    _sb.Append(indent).Append(Strong(text)).Append("\n\n");
                    break;
                }
                var line = new string(Adornments[h.Level - 1], Math.Max(3, Width(text)));
                if (h.Level == 1)
                    _sb.Append(line).Append('\n');
                _sb.Append(text).Append('\n').Append(line).Append("\n\n");
                break;
            }
            case Paragraph p:
            {
                if (p.Inlines.Any(i => i is LineBreak))
                {
                    // Lines that must stay lines: a line block.
                    foreach (var line in Inlines(p.Inlines, oneLine: false).Split('\n'))
                        _sb.Append(indent).Append(line.Trim().Length == 0 ? "|" : "| " + line.Trim()).Append('\n');
                    _sb.Append('\n');
                    break;
                }
                var text = Inlines(p.Inlines, oneLine: true).Trim();
                if (text.Length == 0)
                    break;
                _sb.Append(indent).Append(EscapeLineStart(text)).Append("\n\n");
                break;
            }
            case ListBlock l:
                WriteList(l, indent);
                break;
            case Table t:
                WriteTable(t, indent);
                break;
            case ImageBlock img:
                WriteImage(img.Image, indent);
                break;
            case CodeBlock c:
            {
                _sb.Append(indent).Append(string.IsNullOrWhiteSpace(c.Language) ? "::" : ".. code-block:: " + c.Language.Trim()).Append("\n\n");
                foreach (var line in Text.TextFile.NormalizeNewlines(c.Text).TrimEnd('\n').Split('\n'))
                    _sb.Append(line.Length == 0 ? "" : indent + "   " + line).Append('\n');
                _sb.Append('\n');
                break;
            }
            case Quote q:
                WriteBlocks(q.Blocks, indent + "   ");
                break;
            case HorizontalRule:
                if (indent.Length == 0)
                    _sb.Append("----------\n\n");
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
            var marker = list.Ordered ? number++.ToString(CultureInfo.InvariantCulture) + ". " : "- ";
            var childIndent = indent + new string(' ', marker.Length);
            var first = true;
            foreach (var block in item.Blocks)
            {
                if (first && block is Paragraph p && !p.Inlines.Any(i => i is LineBreak))
                    _sb.Append(indent).Append(marker).Append(Inlines(p.Inlines, oneLine: true).Trim()).Append("\n\n");
                else
                {
                    if (first)
                        _sb.Append(indent).Append(marker.TrimEnd()).Append("\n\n");
                    WriteBlock(block, childIndent);
                }
                first = false;
            }
            if (item.Blocks.Count == 0)
                _sb.Append(indent).Append(marker.TrimEnd()).Append("\n\n");
        }
    }

    private void WriteImage(ImageData image, string indent)
    {
        _sb.Append(indent).Append(".. image:: ").Append(_imagePath(image)).Append('\n');
        if (!string.IsNullOrWhiteSpace(image.Alt))
            _sb.Append(indent).Append("   :alt: ").Append(image.Alt.ReplaceLineEndings(" ").Trim()).Append('\n');
        if (image.WidthPt is { } width)
            _sb.Append(indent).Append("   :width: ").Append(Math.Round(width / 0.75).ToString(CultureInfo.InvariantCulture)).Append("px\n");
        _sb.Append('\n');
    }

    /// <summary>A grid table: every cell on one line, the first row a header when the table marks it so.</summary>
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
            return (Cells: cells.Take(columns).ToList(), r.IsHeader);
        }).ToList();
        if (rows.Count == 0)
            return;
        var widths = Enumerable.Range(0, columns).Select(i => Math.Max(1, rows.Max(r => Width(r.Cells[i])))).ToArray();
        string Border(char c) => indent + "+" + string.Join("+", widths.Select(w => new string(c, w + 2))) + "+\n";
        _sb.Append(Border('-'));
        var headerDone = false;
        for (var r = 0; r < rows.Count; r++)
        {
            _sb.Append(indent).Append('|');
            for (var i = 0; i < columns; i++)
                _sb.Append(' ').Append(rows[r].Cells[i]).Append(' ', widths[i] - Width(rows[r].Cells[i]) + 1).Append('|');
            _sb.Append('\n');
            var header = !headerDone && rows[r].IsHeader && r + 1 < rows.Count && !rows[r + 1].IsHeader;
            headerDone |= header;
            _sb.Append(Border(header ? '=' : '-'));
        }
        _sb.Append('\n');
    }

    private string CellText(TableCell cell) =>
        string.Join(" ", cell.Blocks.Select(block => block switch
        {
            Paragraph p => Inlines(p.Inlines, oneLine: true),
            Heading h => Strong(Inlines(h.Inlines, oneLine: true)),
            ListBlock l => string.Join(" ", l.Items.Select(i => "• " + i.Blocks.PlainText())),
            CodeBlock c => c.Text.ReplaceLineEndings(" "),
            _ => "",
        }).Where(t => t.Trim().Length > 0).Select(t => t.Trim()));

    /// <summary>
    /// Inline text. Markup needs a space or punctuation on each side, so where a word goes on straight after it, an
    /// escaped space ("\ "), which shows as nothing, stands between.
    /// </summary>
    private string Inlines(IEnumerable<Inline> inlines, bool oneLine)
    {
        var sb = new StringBuilder();
        var pendingClose = false;
        foreach (var inline in inlines)
        {
            string piece;
            var markup = false;
            switch (inline)
            {
                case TextRun run:
                    (piece, markup) = Run(run);
                    break;
                case LineBreak:
                    piece = oneLine ? " " : "\n";
                    break;
                case InlineImage:
                    // A picture inside a line needs a substitution; its place is kept as nothing.
                    continue;
                default:
                    continue;
            }
            if (piece.Length == 0)
                continue;
            if (pendingClose && !StartsWithSpaceOrPunctuation(piece))
                sb.Append("\\ ");
            if (markup && sb.Length > 0 && !EndsWithSpaceOrPunctuation(sb))
                sb.Append("\\ ");
            sb.Append(piece);
            pendingClose = markup;
        }
        var text = sb.ToString();
        return oneLine ? text.Replace('\n', ' ') : text;
    }

    private static bool StartsWithSpaceOrPunctuation(string s) => s.Length == 0 || char.IsWhiteSpace(s[0]) || char.IsPunctuation(s[0]) && s[0] != '_';

    private static bool EndsWithSpaceOrPunctuation(StringBuilder sb) => char.IsWhiteSpace(sb[^1]) || char.IsPunctuation(sb[^1]) && sb[^1] != '\\';

    /// <summary>A run of text and whether it ends as markup; spaces at its edges stay outside the markup.</summary>
    private static (string Text, bool Markup) Run(TextRun run)
    {
        if (run.Text.Length == 0)
            return ("", false);
        var text = run.Text.Replace('\n', ' ');
        var lead = text.Length - text.TrimStart().Length;
        var trail = text.Length - text.TrimEnd().Length;
        var inner = text.Trim();
        if (inner.Length == 0)
            return (text, false);
        string marked;
        if (run.Has(TextStyle.Code) && !inner.Contains("``", StringComparison.Ordinal))
            marked = "``" + inner + "``";
        else if (run.Link is not null)
            marked = "`" + Escape(inner).Replace("<", "\\<").Replace("`", "\\`") + " <" + run.Link.Replace(" ", "%20").Replace(">", "%3E") + ">`__";
        else if (run.Has(TextStyle.Bold))
            marked = Strong(Escape(inner));
        else if (run.Has(TextStyle.Italic))
            marked = "*" + Escape(inner) + "*";
        else if (run.Has(TextStyle.Superscript))
            marked = ":sup:`" + Escape(inner) + "`";
        else if (run.Has(TextStyle.Subscript))
            marked = ":sub:`" + Escape(inner) + "`";
        else
            return (Escape(text), false);
        return (text[..lead] + marked + text[(text.Length - trail)..], trail == 0);
    }

    private static string Strong(string text) => "**" + text + "**";

    /// <summary>Text that would otherwise start markup: emphasis, literals, references and substitutions.</summary>
    private static string Escape(string text)
    {
        var sb = new StringBuilder(text.Length + 8);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            switch (c)
            {
                case '\\' or '*' or '`' or '|':
                    sb.Append('\\').Append(c);
                    break;
                case '_':
                    // "word_" is a reference; an underscore inside a word isn't.
                    var after = i + 1 < text.Length && char.IsLetterOrDigit(text[i + 1]);
                    if (!after) sb.Append('\\');
                    sb.Append(c);
                    break;
                default:
                    sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }

    /// <summary>Stops a paragraph that begins like a list, a directive or an adornment line from being read as one.</summary>
    private static string EscapeLineStart(string text)
    {
        if (text.Length == 0)
            return text;
        if (text[0] is '-' or '+' or '•' && (text.Length == 1 || text[1] == ' ')
            || text.StartsWith("..", StringComparison.Ordinal) || text.StartsWith("#.", StringComparison.Ordinal)
            || text.StartsWith(">>>", StringComparison.Ordinal)
            || text.Length >= 2 && !char.IsLetterOrDigit(text[0]) && !char.IsWhiteSpace(text[0]) && text.All(c => c == text[0])
            || System.Text.RegularExpressions.Regex.IsMatch(text, @"^:[^:\s][^:]*:(\s|$)"))
            return "\\" + text;
        var digits = 0;
        while (digits < text.Length && char.IsAsciiDigit(text[digits])) digits++;
        if (digits > 0 && digits < text.Length && text[digits] is '.' or ')' && (digits + 1 == text.Length || text[digits + 1] == ' '))
            return "\\" + text;
        return text;
    }

    /// <summary>How many columns text takes: two for East Asian wide letters, none for marks over others.</summary>
    internal static int Width(string text)
    {
        var width = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark or UnicodeCategory.Format)
                continue;
            var v = rune.Value;
            width += v is >= 0x1100 and <= 0x115F or >= 0x2E80 and <= 0xA4CF or >= 0xAC00 and <= 0xD7A3 or >= 0xF900 and <= 0xFAFF
                or >= 0xFE30 and <= 0xFE4F or >= 0xFF00 and <= 0xFF60 or >= 0xFFE0 and <= 0xFFE6 or >= 0x1F300 and <= 0x1FAFF or >= 0x20000 and <= 0x3FFFD
                ? 2 : 1;
        }
        return width;
    }
}
