using System.Text;
using Petal.Engines.Documents.Model;

namespace Petal.Engines.Documents.IO;

/// <summary>
/// TXT: blank lines separate paragraphs, single newlines inside a paragraph are kept as line
/// breaks (so poems, addresses and hard-wrapped text keep their shape), form feeds are page breaks.
/// </summary>
internal static class PlainTextIO
{
    public static DocModel Read(string path) => Parse(Text.TextFile.ReadAllText(path));

    public static DocModel Parse(string text)
    {
        var doc = new DocModel();
        text = Text.TextFile.NormalizeNewlines(text);
        var pages = text.Split('\f');
        for (var pi = 0; pi < pages.Length; pi++)
        {
            if (pi > 0)
                doc.Blocks.Add(new PageBreak());
            var paragraphs = new List<List<string>>();
            List<string>? current = null;
            foreach (var line in pages[pi].Split('\n'))
            {
                if (line.Trim().Length == 0)
                {
                    current = null;
                    continue;
                }
                if (current is null)
                {
                    current = [];
                    paragraphs.Add(current);
                }
                current.Add(line.TrimEnd());
            }
            foreach (var lines in paragraphs)
            {
                var p = new Paragraph();
                for (var i = 0; i < lines.Count; i++)
                {
                    if (i > 0)
                        p.Inlines.Add(new LineBreak());
                    p.Inlines.Add(new TextRun(lines[i]));
                }
                doc.Blocks.Add(p);
            }
        }
        return doc;
    }

    public static void Write(DocModel doc, string path) =>
        File.WriteAllText(path, Render(doc), Text.TextFile.Utf8Bom);

    public static string Render(DocModel doc)
    {
        var sb = new StringBuilder();
        WriteBlocks(sb, doc.Blocks, "");
        return sb.ToString().TrimEnd() + Environment.NewLine;
    }

    private static void WriteBlocks(StringBuilder sb, IEnumerable<Block> blocks, string indent)
    {
        foreach (var block in blocks)
        {
            switch (block)
            {
                case Heading h:
                    AppendLines(sb, h.Inlines.PlainText(), indent);
                    sb.AppendLine();
                    break;
                case Paragraph p:
                    if (p.IsEmpty()) break;
                    AppendLines(sb, p.Inlines.PlainText(), indent);
                    sb.AppendLine();
                    break;
                case ListBlock l:
                    WriteList(sb, l, indent);
                    sb.AppendLine();
                    break;
                case Table t:
                    WriteTable(sb, t, indent);
                    sb.AppendLine();
                    break;
                case CodeBlock c:
                    AppendLines(sb, c.Text, indent + "    ");
                    sb.AppendLine();
                    break;
                case Quote q:
                    WriteBlocks(sb, q.Blocks, indent + "    ");
                    break;
                case HorizontalRule:
                    sb.Append(indent).AppendLine(new string('-', 40)).AppendLine();
                    break;
                case PageBreak:
                    sb.AppendLine();
                    break;
            }
        }
    }

    private static void WriteList(StringBuilder sb, ListBlock list, string indent)
    {
        var n = list.Start;
        foreach (var item in list.Items)
        {
            var marker = list.Ordered ? $"{n++}. " : "• ";
            var childIndent = indent + new string(' ', marker.Length);
            var first = true;
            foreach (var block in item.Blocks)
            {
                if (first && block is Paragraph p)
                {
                    var lines = p.Inlines.PlainText().Split('\n');
                    sb.Append(indent).Append(marker).AppendLine(lines[0]);
                    foreach (var line in lines.Skip(1))
                        sb.Append(childIndent).AppendLine(line);
                }
                else if (block is ListBlock nested)
                    WriteList(sb, nested, childIndent);
                else
                {
                    var inner = new StringBuilder();
                    WriteBlocks(inner, [block], childIndent);
                    sb.Append(first ? indent + marker + inner.ToString().TrimStart() : inner.ToString());
                }
                first = false;
            }
        }
    }

    /// <summary>Tables become space-aligned columns, or tab-separated when they are too wide for that.</summary>
    private static void WriteTable(StringBuilder sb, Table table, string indent)
    {
        var rows = table.Rows.Select(r => r.Cells.Select(c => c.Blocks.PlainText().Replace('\n', ' ').Trim()).ToList()).ToList();
        var columns = rows.Count == 0 ? 0 : rows.Max(r => r.Count);
        foreach (var r in rows)
            while (r.Count < columns) r.Add("");
        var widths = Enumerable.Range(0, columns).Select(i => rows.Max(r => r[i].Length)).ToArray();
        var aligned = widths.Sum() + 2 * columns <= 120;
        foreach (var r in rows)
        {
            sb.Append(indent);
            if (aligned)
                sb.AppendLine(string.Join("  ", r.Select((c, i) => c.PadRight(widths[i]))).TrimEnd());
            else
                sb.AppendLine(string.Join('\t', r));
        }
    }

    private static void AppendLines(StringBuilder sb, string text, string indent)
    {
        foreach (var line in text.Split('\n'))
            sb.Append(indent).AppendLine(line);
    }
}
