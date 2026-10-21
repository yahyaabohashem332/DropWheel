using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Petal.Engines.Documents.Model;

namespace Petal.Engines.Documents.IO;

/// <summary>
/// InDesign Markup (IDML): a ZIP of XML parts. The text lives in stories; their order on the
/// pages comes from the text frames on the spreads. Headings come from paragraph style names,
/// bold and italic from font styles; tables keep their rows and columns.
/// </summary>
internal static partial class IdmlReader
{
    public static DocModel Read(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var doc = new DocModel();
        var stories = zip.Entries.Where(e => e.FullName.StartsWith("Stories/", StringComparison.OrdinalIgnoreCase) && e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(e => StoryId().Match(e.FullName) is { Success: true } m ? m.Groups[1].Value : e.FullName, e => e);

        // Reading order: stories as their frames appear on the spreads, in the order the document lists the spreads.
        var order = new List<string>();
        foreach (var spread in SpreadsInOrder(zip))
        {
            foreach (Match m in ParentStory().Matches(Read(spread)))
            {
                if (!order.Contains(m.Groups[1].Value))
                    order.Add(m.Groups[1].Value);
            }
        }
        order.AddRange(stories.Keys.Where(k => !order.Contains(k)).Order(StringComparer.Ordinal));

        foreach (var id in order)
        {
            if (!stories.TryGetValue(id, out var entry))
                continue;
            var story = XDocument.Parse(Read(entry)).Descendants().FirstOrDefault(e => e.Name.LocalName == "Story");
            if (story is null)
                continue;
            AddBlocks(story, doc.Blocks);
        }
        if (zip.GetEntry("META-INF/metadata.xml") is { } metadata)
        {
            var m = Title().Match(Read(metadata));
            if (m.Success)
                doc.Metadata.Title = System.Net.WebUtility.HtmlDecode(m.Groups[1].Value);
        }
        return doc;
    }

    private static IEnumerable<ZipArchiveEntry> SpreadsInOrder(ZipArchive zip)
    {
        var listed = new List<ZipArchiveEntry>();
        if (zip.GetEntry("designmap.xml") is { } designmap)
        {
            foreach (Match m in SpreadSource().Matches(Read(designmap)))
            {
                if (zip.GetEntry(m.Groups[1].Value) is { } entry)
                    listed.Add(entry);
            }
        }
        return listed.Count > 0 ? listed : zip.Entries.Where(e => e.FullName.StartsWith("Spreads/", StringComparison.OrdinalIgnoreCase)).OrderBy(e => e.FullName, StringComparer.Ordinal);
    }

    private static string Read(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>Paragraphs of a story or table cell: text runs until each Br, under the style of their ParagraphStyleRange.</summary>
    private static void AddBlocks(XElement container, List<Block> blocks)
    {
        Block? current = null;
        List<Inline>? inlines = null;
        string? paragraphStyle = null;

        void Start()
        {
            int level = HeadingLevel(paragraphStyle);
            if (level > 0)
            {
                var heading = new Heading(level);
                current = heading;
                inlines = heading.Inlines;
            }
            else
            {
                var paragraph = new Paragraph();
                current = paragraph;
                inlines = paragraph.Inlines;
            }
        }

        void End()
        {
            if (current is not null && inlines is { Count: > 0 } && inlines.OfType<TextRun>().Any(r => r.Text.Trim().Length > 0))
                blocks.Add(current);
            current = null;
            inlines = null;
        }

        void Walk(XElement element, TextStyle style)
        {
            foreach (var node in element.Elements())
            {
                switch (node.Name.LocalName)
                {
                    case "ParagraphStyleRange":
                        End();
                        paragraphStyle = node.Attribute("AppliedParagraphStyle")?.Value;
                        Walk(node, style);
                        End();
                        break;
                    case "CharacterStyleRange":
                        Walk(node, style | StyleOf(node));
                        break;
                    case "Content":
                        if (current is null)
                            Start();
                        // InDesign marks special characters with processing instructions and U+2028 for forced line breaks.
                        var text = node.Value.Replace('\u2028', '\n');
                        var parts = text.Split('\n');
                        for (int i = 0; i < parts.Length; i++)
                        {
                            if (i > 0)
                                inlines!.Add(new LineBreak());
                            if (parts[i].Length > 0)
                                inlines!.Add(new TextRun(parts[i], style));
                        }
                        break;
                    case "Br":
                        End();
                        break;
                    case "Table":
                        End();
                        blocks.Add(TableOf(node));
                        break;
                    default:
                        Walk(node, style);
                        break;
                }
            }
        }

        Walk(container, TextStyle.None);
        End();
    }

    private static TextStyle StyleOf(XElement range)
    {
        var font = (range.Attribute("FontStyle")?.Value ?? "") + " " + (range.Attribute("AppliedCharacterStyle")?.Value ?? "");
        var style = TextStyle.None;
        if (font.Contains("Bold", StringComparison.OrdinalIgnoreCase) || font.Contains("Black", StringComparison.OrdinalIgnoreCase) || font.Contains("Heavy", StringComparison.OrdinalIgnoreCase))
            style |= TextStyle.Bold;
        if (font.Contains("Italic", StringComparison.OrdinalIgnoreCase) || font.Contains("Oblique", StringComparison.OrdinalIgnoreCase))
            style |= TextStyle.Italic;
        if (range.Attribute("Underline")?.Value == "true")
            style |= TextStyle.Underline;
        if (range.Attribute("StrikeThru")?.Value == "true")
            style |= TextStyle.Strike;
        return style;
    }

    /// <summary>"ParagraphStyle/Heading 2" → 2, "ParagraphStyle/Title" → 1, anything else → 0.</summary>
    internal static int HeadingLevel(string? style)
    {
        if (style is null)
            return 0;
        var name = Uri.UnescapeDataString(style[(style.LastIndexOf('/') + 1)..]).Replace("%3a", ":");
        var m = HeadingName().Match(name);
        if (m.Success)
            return m.Groups[1].Success ? Math.Clamp(int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture), 1, 6) : 1;
        return name.Equals("Title", StringComparison.OrdinalIgnoreCase) ? 1 : name.Equals("Subtitle", StringComparison.OrdinalIgnoreCase) ? 2 : 0;
    }

    /// <summary>Cells are named "column:row".</summary>
    private static Table TableOf(XElement table)
    {
        var result = new Table();
        var cells = table.Elements().Where(e => e.Name.LocalName == "Cell").Select(cell =>
        {
            var name = cell.Attribute("Name")?.Value ?? "0:0";
            var parts = name.Split(':');
            int col = int.TryParse(parts[0], out var c) ? c : 0, row = parts.Length > 1 && int.TryParse(parts[1], out var r) ? r : 0;
            var content = new TableCell
            {
                ColSpan = int.TryParse(cell.Attribute("ColumnSpan")?.Value, out var cs) ? Math.Max(1, cs) : 1,
                RowSpan = int.TryParse(cell.Attribute("RowSpan")?.Value, out var rs) ? Math.Max(1, rs) : 1,
            };
            AddBlocks(cell, content.Blocks);
            return (row, col, content);
        });
        int headerRows = int.TryParse(table.Attribute("HeaderRowCount")?.Value, out var h) ? h : 0;
        foreach (var row in cells.GroupBy(c => c.row).OrderBy(g => g.Key))
        {
            var tableRow = new TableRow { IsHeader = row.Key < headerRows };
            tableRow.Cells.AddRange(row.OrderBy(c => c.col).Select(c => c.content));
            result.Rows.Add(tableRow);
        }
        return result;
    }

    [GeneratedRegex(@"Story_(\w+)\.xml$", RegexOptions.IgnoreCase)]
    private static partial Regex StoryId();

    [GeneratedRegex(@"ParentStory=""(\w+)""")]
    private static partial Regex ParentStory();

    [GeneratedRegex(@"<idPkg:Spread\s+src=""([^""]+)""")]
    private static partial Regex SpreadSource();

    [GeneratedRegex(@"<dc:title>\s*<rdf:Alt>\s*<rdf:li[^>]*>([^<]*)</rdf:li>", RegexOptions.Singleline)]
    private static partial Regex Title();

    [GeneratedRegex(@"^(?:Heading|Head|Titre|Überschrift|Kop)\s*(\d)?", RegexOptions.IgnoreCase)]
    private static partial Regex HeadingName();
}
