using System.Text;

namespace Petal.Engines.Documents.Model;

internal static class DocModelExtensions
{
    public static string PlainText(this IEnumerable<Inline> inlines)
    {
        var sb = new StringBuilder();
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case TextRun run: sb.Append(run.Text); break;
                case LineBreak: sb.Append('\n'); break;
            }
        }
        return sb.ToString();
    }

    /// <summary>All text of a block tree, paragraphs separated by newlines.</summary>
    public static string PlainText(this IEnumerable<Block> blocks)
    {
        var sb = new StringBuilder();
        AppendText(sb, blocks);
        return sb.ToString().TrimEnd('\n');
    }

    public static string PlainText(this DocModel doc) => doc.Blocks.PlainText();

    private static void AppendText(StringBuilder sb, IEnumerable<Block> blocks)
    {
        foreach (var block in blocks)
        {
            switch (block)
            {
                case Heading h: sb.Append(h.Inlines.PlainText()).Append('\n'); break;
                case Paragraph p: sb.Append(p.Inlines.PlainText()).Append('\n'); break;
                case CodeBlock c: sb.Append(c.Text).Append('\n'); break;
                case Quote q: AppendText(sb, q.Blocks); break;
                case ListBlock l:
                    foreach (var item in l.Items)
                        AppendText(sb, item.Blocks);
                    break;
                case Table t:
                    foreach (var row in t.Rows)
                        sb.Append(string.Join('\t', row.Cells.Select(c => c.Blocks.PlainText().Replace('\n', ' ')))).Append('\n');
                    break;
            }
        }
    }

    /// <summary>Every block, depth first, including those nested in lists, tables and quotes.</summary>
    public static IEnumerable<Block> Descendants(this IEnumerable<Block> blocks)
    {
        foreach (var block in blocks)
        {
            yield return block;
            IEnumerable<Block> children = block switch
            {
                Quote q => q.Blocks,
                ListBlock l => l.Items.SelectMany(i => i.Blocks),
                Table t => t.Rows.SelectMany(r => r.Cells).SelectMany(c => c.Blocks),
                _ => [],
            };
            foreach (var child in children.Descendants())
                yield return child;
        }
    }

    public static IEnumerable<Inline> AllInlines(this DocModel doc) =>
        doc.Blocks.Descendants().SelectMany(b => b switch
        {
            Heading h => h.Inlines,
            Paragraph p => p.Inlines,
            _ => Enumerable.Empty<Inline>(),
        });

    public static IEnumerable<ImageData> AllImages(this DocModel doc) =>
        doc.Blocks.Descendants().OfType<ImageBlock>().Select(b => b.Image)
            .Concat(doc.AllInlines().OfType<InlineImage>().Select(i => i.Image));

    /// <summary>Joins neighbouring runs with the same style and link, and drops empty ones.</summary>
    public static List<Inline> Coalesce(this IEnumerable<Inline> inlines)
    {
        var result = new List<Inline>();
        foreach (var inline in inlines)
        {
            if (inline is TextRun run)
            {
                if (run.Text.Length == 0)
                    continue;
                if (result.Count > 0 && result[^1] is TextRun last && last.Style == run.Style && last.Link == run.Link)
                {
                    last.Text += run.Text;
                    continue;
                }
                result.Add(new TextRun(run.Text, run.Style, run.Link));
                continue;
            }
            result.Add(inline);
        }
        return result;
    }

    /// <summary>Trims leading and trailing whitespace of an inline sequence (inner spacing is kept).</summary>
    public static void TrimInlines(this List<Inline> inlines)
    {
        while (inlines.Count > 0 && inlines[0] is TextRun first && first.Text.TrimStart().Length == 0)
            inlines.RemoveAt(0);
        while (inlines.Count > 0 && inlines[^1] is LineBreak)
            inlines.RemoveAt(inlines.Count - 1);
        while (inlines.Count > 0 && inlines[^1] is TextRun last && last.Text.TrimEnd().Length == 0)
            inlines.RemoveAt(inlines.Count - 1);
        if (inlines.Count > 0 && inlines[0] is TextRun f)
            f.Text = f.Text.TrimStart();
        if (inlines.Count > 0 && inlines[^1] is TextRun l)
            l.Text = l.Text.TrimEnd();
    }

    public static bool IsEmpty(this Paragraph p) =>
        p.Inlines.All(i => i is TextRun r && string.IsNullOrWhiteSpace(r.Text) || i is LineBreak);

    /// <summary>Title for metadata: the explicit one, else the first heading.</summary>
    public static string? EffectiveTitle(this DocModel doc) =>
        !string.IsNullOrWhiteSpace(doc.Metadata.Title)
            ? doc.Metadata.Title
            : doc.Blocks.OfType<Heading>().Select(h => h.Inlines.PlainText().Trim()).FirstOrDefault(t => t.Length > 0);
}
