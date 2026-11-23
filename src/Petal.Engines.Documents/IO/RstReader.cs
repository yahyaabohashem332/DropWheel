using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Petal.Engines.Documents.Model;

namespace Petal.Engines.Documents.IO;

/// <summary>
/// Reads reStructuredText (docutils and Sphinx): section titles by their adornments, paragraphs, bullet, numbered,
/// definition and field lists, literal and code blocks, quotes and notes, line blocks, grid, simple, list and CSV
/// tables, pictures, and inline emphasis, literals, links and roles. Directives that build a site (a table of
/// contents, an index, an include) say nothing on a page and are left out.
/// </summary>
internal sealed partial class RstReader
{
    private readonly string? _baseDir;
    private readonly Dictionary<string, string> _targets = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _substitutions = new(StringComparer.Ordinal);
    private readonly List<(char Char, bool Over)> _styles = [];
    private readonly DocModel _doc = new();

    private RstReader(string? baseDir) => _baseDir = baseDir;

    public static DocModel Read(string path) =>
        Parse(Text.TextFile.ReadAllText(path), Path.GetDirectoryName(Path.GetFullPath(path)));

    public static DocModel Parse(string text, string? baseDir = null)
    {
        var reader = new RstReader(baseDir);
        var lines = Text.TextFile.NormalizeNewlines(text).Split('\n').Select(ExpandTabs).Select(l => l.TrimEnd()).ToList();
        reader.CollectTargets(lines);
        reader.ParseBlocks(lines, reader._doc.Blocks, top: true);
        reader._doc.Metadata.Title ??= reader._doc.Blocks.OfType<Heading>().FirstOrDefault() is { } first
            ? string.Concat(first.Inlines.OfType<TextRun>().Select(r => r.Text)).Trim() : null;
        return reader._doc;
    }

    private static string ExpandTabs(string line)
    {
        if (!line.Contains('\t'))
            return line;
        var sb = new StringBuilder();
        foreach (var c in line)
        {
            if (c == '\t')
                sb.Append(' ', 8 - sb.Length % 8);
            else
                sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>Link targets (".. _name: address") and substitutions (".. |name| replace:: text"), wherever they are.</summary>
    private void CollectTargets(List<string> lines)
    {
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            var target = TargetLine().Match(line);
            if (target.Success)
            {
                _targets[Normalize(target.Groups[1].Value)] = target.Groups[2].Value.Trim();
                continue;
            }
            var substitution = SubstitutionLine().Match(line);
            if (substitution.Success)
                _substitutions[substitution.Groups[1].Value] = substitution.Groups[2].Value.Trim();
        }
    }

    private static string Normalize(string name) => Regex.Replace(name.Trim().Trim('`'), @"\s+", " ").ToLowerInvariant();

    private static bool Blank(string line) => line.Trim().Length == 0;

    private static int Indent(string line) => line.Length - line.TrimStart().Length;

    /// <summary>A line of one punctuation character repeated: a title's adornment or a transition.</summary>
    private static bool IsAdornment(string line)
    {
        var t = line.TrimEnd();
        if (t.Length < 2 || Indent(line) > 0)
            return false;
        var c = t[0];
        return !char.IsLetterOrDigit(c) && !char.IsWhiteSpace(c) && c < 128 && t.All(x => x == c);
    }

    /// <summary>The lines from <paramref name="start"/> on that are blank or indented by at least <paramref name="min"/>, without their last blank lines.</summary>
    private static int IndentedEnd(List<string> lines, int start, int min)
    {
        var end = start;
        var last = start;
        while (end < lines.Count && (Blank(lines[end]) || Indent(lines[end]) >= min))
        {
            if (!Blank(lines[end]))
                last = end + 1;
            end++;
        }
        return last;
    }

    private static List<string> Dedent(IEnumerable<string> lines)
    {
        var list = lines.ToList();
        var min = list.Where(l => !Blank(l)).Select(Indent).DefaultIfEmpty(0).Min();
        return list.Select(l => Blank(l) ? "" : l[Math.Min(min, l.Length)..]).ToList();
    }

    private void ParseBlocks(List<string> lines, List<Block> into, bool top)
    {
        var i = 0;
        while (i < lines.Count)
        {
            var line = lines[i];
            if (Blank(line))
            {
                i++;
                continue;
            }

            // An indented block on its own: a quotation.
            if (Indent(line) > 0)
            {
                var end = IndentedEnd(lines, i, 1);
                var quote = new Quote();
                ParseBlocks(Dedent(lines.Skip(i).Take(end - i)), quote.Blocks, top: false);
                if (quote.Blocks.Count > 0)
                    into.Add(quote);
                i = end;
                continue;
            }

            // A title with an overline and an underline.
            if (IsAdornment(line) && i + 2 < lines.Count && !Blank(lines[i + 1]) && !IsAdornment(lines[i + 1])
                && IsAdornment(lines[i + 2]) && lines[i + 2].TrimEnd()[0] == line.TrimEnd()[0])
            {
                AddHeading(lines[i + 1].Trim(), line.TrimEnd()[0], over: true, into, top);
                i += 3;
                continue;
            }
            // A transition: a line of four or more punctuation marks between blank lines.
            if (IsAdornment(line) && line.Trim().Length >= 4 && (i + 1 >= lines.Count || Blank(lines[i + 1])))
            {
                if (top)
                    into.Add(new HorizontalRule());
                i++;
                continue;
            }
            // A title underlined.
            if (i + 1 < lines.Count && IsAdornment(lines[i + 1]) && !IsAdornment(line) && !IsGridBorder(line)
                && lines[i + 1].Trim().Length >= Math.Min(RstWriter.Width(line.Trim()), 4))
            {
                AddHeading(line.Trim(), lines[i + 1].Trim()[0], over: false, into, top);
                i += 2;
                continue;
            }

            if (IsGridBorder(line))
            {
                i = GridTable(lines, i, into);
                continue;
            }
            if (SimpleBorder().IsMatch(line))
            {
                i = SimpleTable(lines, i, into);
                continue;
            }
            if (BulletLine().Match(line) is { Success: true })
            {
                i = List(lines, i, into, ordered: false);
                continue;
            }
            if (EnumeratedLine().Match(line) is { Success: true })
            {
                i = List(lines, i, into, ordered: true);
                continue;
            }
            if (line.StartsWith("..", StringComparison.Ordinal) && (line.Length == 2 || line[2] == ' '))
            {
                i = Directive(lines, i, into);
                continue;
            }
            if (FieldLine().IsMatch(line))
            {
                i = FieldList(lines, i, into);
                continue;
            }
            if (line.StartsWith("| ", StringComparison.Ordinal) || line == "|")
            {
                i = LineBlock(lines, i, into);
                continue;
            }
            if (line.StartsWith(">>>", StringComparison.Ordinal))
            {
                var end = i;
                while (end < lines.Count && !Blank(lines[end]))
                    end++;
                into.Add(new CodeBlock(string.Join('\n', lines.Skip(i).Take(end - i))) { Language = "pycon" });
                i = end;
                continue;
            }
            // A term with its definition indented under it.
            if (i + 1 < lines.Count && !Blank(lines[i + 1]) && Indent(lines[i + 1]) > 0)
            {
                i = DefinitionList(lines, i, into);
                continue;
            }
            i = Paragraph(lines, i, into);
        }
    }

    private void AddHeading(string text, char adornment, bool over, List<Block> into, bool top)
    {
        if (!top)
        {
            var bold = new Paragraph();
            bold.Inlines.AddRange(InlineRuns(text).Select(r => r is TextRun t ? new TextRun(t.Text, t.Style | TextStyle.Bold, t.Link) : r));
            into.Add(bold);
            return;
        }
        var level = _styles.IndexOf((adornment, over));
        if (level < 0)
        {
            _styles.Add((adornment, over));
            level = _styles.Count - 1;
        }
        var heading = new Heading(level + 1);
        heading.Inlines.AddRange(InlineRuns(text));
        into.Add(heading);
    }

    private int Paragraph(List<string> lines, int i, List<Block> into)
    {
        var end = i;
        while (end < lines.Count && !Blank(lines[end]) && Indent(lines[end]) == 0)
        {
            // A line that is followed by an adornment starts a title of its own.
            if (end > i && end + 1 < lines.Count && IsAdornment(lines[end + 1]))
                break;
            end++;
        }
        var text = string.Join(' ', lines.Skip(i).Take(end - i).Select(l => l.Trim()));
        var literal = text.EndsWith("::", StringComparison.Ordinal);
        if (literal)
            text = text == "::" ? "" : text.EndsWith(" ::", StringComparison.Ordinal) ? text[..^3] : text[..^1];
        if (text.Length > 0)
        {
            var paragraph = new Paragraph();
            paragraph.Inlines.AddRange(InlineRuns(text));
            into.Add(paragraph);
        }
        if (!literal)
            return end;
        // The literal block: the indented lines after the blank one (or quoted ones, each starting alike).
        var next = end;
        while (next < lines.Count && Blank(lines[next]))
            next++;
        if (next >= lines.Count)
            return next;
        if (Indent(lines[next]) > 0)
        {
            var stop = IndentedEnd(lines, next, 1);
            into.Add(new CodeBlock(string.Join('\n', Dedent(lines.Skip(next).Take(stop - next)))));
            return stop;
        }
        var quote = lines[next][0];
        if (!char.IsLetterOrDigit(quote) && !char.IsWhiteSpace(quote))
        {
            var stop = next;
            while (stop < lines.Count && !Blank(lines[stop]) && lines[stop][0] == quote)
                stop++;
            into.Add(new CodeBlock(string.Join('\n', lines.Skip(next).Take(stop - next))));
            return stop;
        }
        return next;
    }

    private int List(List<string> lines, int i, List<Block> into, bool ordered)
    {
        var list = new ListBlock(ordered);
        string? bullet = null;
        while (i < lines.Count)
        {
            var match = ordered ? EnumeratedLine().Match(lines[i]) : BulletLine().Match(lines[i]);
            if (!match.Success || Indent(lines[i]) > 0)
                break;
            var marker = ordered ? match.Groups[1].Value + match.Groups[2].Value : match.Groups[1].Value;
            var kind = ordered ? match.Groups[1].Value + (match.Groups[2].Value == "#" || char.IsAsciiDigit(match.Groups[2].Value[0]) ? "1" : "a") + match.Groups[3].Value : marker;
            bullet ??= kind;
            if (kind != bullet)
                break;
            if (ordered && list.Items.Count == 0 && int.TryParse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var start))
                list.Start = start;
            var content = match.Groups[^1];
            var contentIndent = content.Success && content.Length > 0 ? content.Index : match.Length;
            var first = content.Value;
            var end = IndentedEnd(lines, i + 1, Math.Max(1, contentIndent));
            var itemLines = new List<string> { first };
            itemLines.AddRange(lines.Skip(i + 1).Take(end - i - 1).Select(l => Blank(l) ? "" : l[Math.Min(Indent(l), contentIndent)..]));
            var item = new ListItem();
            ParseBlocks(itemLines, item.Blocks, top: false);
            list.Items.Add(item);
            i = end;
            while (i < lines.Count && Blank(lines[i]))
                i++;
        }
        into.Add(list);
        return i;
    }

    private int DefinitionList(List<string> lines, int i, List<Block> into)
    {
        while (i + 1 < lines.Count && !Blank(lines[i]) && Indent(lines[i]) == 0 && !Blank(lines[i + 1]) && Indent(lines[i + 1]) > 0)
        {
            var term = lines[i].Trim();
            var classifier = term.IndexOf(" : ", StringComparison.Ordinal);
            var paragraph = new Paragraph();
            paragraph.Inlines.AddRange(InlineRuns(classifier > 0 ? term[..classifier] : term)
                .Select(r => r is TextRun t ? new TextRun(t.Text, t.Style | TextStyle.Bold, t.Link) : r));
            if (classifier > 0)
                paragraph.Inlines.Add(new TextRun(" (" + term[(classifier + 3)..].Trim() + ")", TextStyle.Italic));
            into.Add(paragraph);
            var end = IndentedEnd(lines, i + 1, 1);
            var definition = new Quote();
            ParseBlocks(Dedent(lines.Skip(i + 1).Take(end - i - 1)), definition.Blocks, top: false);
            into.Add(definition);
            i = end;
            while (i < lines.Count && Blank(lines[i]))
                i++;
        }
        return i;
    }

    private int FieldList(List<string> lines, int i, List<Block> into)
    {
        while (i < lines.Count && FieldLine().Match(lines[i]) is { Success: true } match)
        {
            var end = IndentedEnd(lines, i + 1, 1);
            var body = string.Join(' ', new[] { match.Groups[2].Value }.Concat(lines.Skip(i + 1).Take(end - i - 1).Select(l => l.Trim())).Where(s => s.Length > 0));
            var name = match.Groups[1].Value.Trim();
            var key = name.ToLowerInvariant();
            if (into == _doc.Blocks && key is "author" or "authors" && _doc.Metadata.Author is null)
                _doc.Metadata.Author = body;
            var paragraph = new Paragraph();
            paragraph.Inlines.Add(new TextRun(name + ": ", TextStyle.Bold));
            paragraph.Inlines.AddRange(InlineRuns(body));
            into.Add(paragraph);
            i = end;
            while (i < lines.Count && Blank(lines[i]))
                i++;
        }
        return i;
    }

    private int LineBlock(List<string> lines, int i, List<Block> into)
    {
        var paragraph = new Paragraph();
        var first = true;
        while (i < lines.Count && !Blank(lines[i]))
        {
            var line = lines[i];
            if (line.StartsWith('|'))
            {
                if (!first)
                    paragraph.Inlines.Add(new LineBreak());
                paragraph.Inlines.AddRange(InlineRuns(line.Length > 1 ? line[2..].TrimEnd() : ""));
            }
            else
            {
                paragraph.Inlines.Add(new TextRun(" "));
                paragraph.Inlines.AddRange(InlineRuns(line.Trim()));
            }
            first = false;
            i++;
        }
        into.Add(paragraph);
        return i;
    }

    private int Directive(List<string> lines, int i, List<Block> into)
    {
        var head = lines[i][2..].Trim();
        var end = IndentedEnd(lines, i + 1, 1);
        var body = Dedent(lines.Skip(i + 1).Take(end - i - 1));
        // A footnote or a citation: its mark and words, as a paragraph.
        if (FootnoteHead().Match(head) is { Success: true } note)
        {
            var paragraph = new Paragraph();
            paragraph.Inlines.Add(new TextRun(note.Groups[1].Value, TextStyle.Superscript));
            paragraph.Inlines.Add(new TextRun(" "));
            paragraph.Inlines.AddRange(InlineRuns(string.Join(' ', new[] { note.Groups[2].Value }.Concat(body.Select(l => l.Trim())).Where(t => t.Length > 0))));
            into.Add(paragraph);
            return end;
        }
        var match = DirectiveHead().Match(head);
        if (!match.Success)
            return end; // a comment or a link target
        var name = match.Groups[2].Value.ToLowerInvariant();
        var argument = match.Groups[3].Value.Trim();
        // The options (":alt: …") come first in the body, the content after a blank line.
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var at = 0;
        while (at < body.Count && OptionLine().Match(body[at]) is { Success: true } option)
        {
            options[option.Groups[1].Value] = option.Groups[2].Value.Trim();
            at++;
        }
        // An argument that goes on onto more lines (a long title) before the options.
        var content = body.Skip(at).ToList();
        if (match.Groups[1].Success)
            return end; // a substitution's definition, used where it is named

        switch (name)
        {
            case "code" or "code-block" or "sourcecode":
                into.Add(new CodeBlock(string.Join('\n', Dedent(content)).Trim('\n')) { Language = argument.Length > 0 ? argument : null });
                break;
            case "math":
                into.Add(new CodeBlock(string.Join('\n', Dedent(content)).Trim('\n').Length > 0 ? string.Join('\n', Dedent(content)).Trim('\n') : argument) { Language = "latex" });
                break;
            case "image" or "figure":
            {
                if (Image(argument, options) is { } image)
                    into.Add(new ImageBlock(image));
                if (name == "figure" && content.Any(l => !Blank(l)))
                {
                    var caption = new List<Block>();
                    ParseBlocks(Dedent(content), caption, top: false);
                    foreach (var block in caption)
                    {
                        if (block is Paragraph p)
                            foreach (var run in p.Inlines.OfType<TextRun>())
                                run.Style |= TextStyle.Italic;
                        into.Add(block);
                    }
                }
                break;
            }
            case "note" or "warning" or "tip" or "important" or "hint" or "caution" or "attention" or "danger" or "error"
                or "admonition" or "seealso" or "topic" or "sidebar" or "versionadded" or "versionchanged" or "deprecated":
            {
                var quote = new Quote();
                var title = name switch
                {
                    "admonition" or "topic" or "sidebar" => argument,
                    "seealso" => "See also",
                    "versionadded" => "New in version " + argument,
                    "versionchanged" => "Changed in version " + argument,
                    "deprecated" => "Deprecated since version " + argument,
                    _ => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(name),
                };
                var label = new Paragraph();
                label.Inlines.Add(new TextRun(title, TextStyle.Bold));
                quote.Blocks.Add(label);
                // A note's words may start on its own line ".. note:: Words".
                var words = name is "admonition" or "topic" or "sidebar" or "versionadded" or "versionchanged" or "deprecated" ? new List<string>() : [argument];
                ParseBlocks([.. words, "", .. Dedent(content)], quote.Blocks, top: false);
                into.Add(quote);
                break;
            }
            case "rubric":
            {
                var paragraph = new Paragraph();
                paragraph.Inlines.Add(new TextRun(argument, TextStyle.Bold));
                into.Add(paragraph);
                break;
            }
            case "epigraph" or "highlights" or "pull-quote":
            {
                var quote = new Quote();
                ParseBlocks(Dedent(content), quote.Blocks, top: false);
                into.Add(quote);
                break;
            }
            case "container" or "class" or "rst-class" or "only" or "compound" or "line-block" or "parsed-literal" or "centered":
                if (name == "centered")
                    goto case "rubric";
                ParseBlocks(Dedent(content), into, top: false);
                break;
            case "list-table":
                ListTable(content, options, argument, into);
                break;
            case "csv-table":
                CsvTable(content, options, argument, into);
                break;
        }
        // Anything else (toctree, contents, index, include, raw, meta, autodoc…) builds a site and shows nothing here.
        return end;
    }

    private ImageData? Image(string path, Dictionary<string, string> options)
    {
        byte[]? bytes = null;
        try
        {
            bytes = HtmlReader.ResolveFile(_baseDir, path.Trim());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
        }
        if (bytes is null)
            return null;
        var image = ImageData.Create(bytes, options.GetValueOrDefault("alt") ?? "");
        if (image is not null && options.GetValueOrDefault("width") is { } width && Regex.Match(width, @"^(\d+(?:\.\d+)?)\s*(px)?$") is { Success: true } px)
        {
            image.WidthPt = double.Parse(px.Groups[1].Value, CultureInfo.InvariantCulture) * 0.75;
            if (image.PixelWidth > 0)
                image.HeightPt = image.WidthPt * image.PixelHeight / image.PixelWidth;
        }
        return image;
    }

    private void ListTable(List<string> content, Dictionary<string, string> options, string title, List<Block> into)
    {
        var blocks = new List<Block>();
        ParseBlocks(Dedent(content), blocks, top: false);
        if (blocks.OfType<ListBlock>().FirstOrDefault() is not { } rows)
            return;
        var headerRows = int.TryParse(options.GetValueOrDefault("header-rows"), out var h) ? h : 0;
        var table = new Table();
        foreach (var item in rows.Items)
        {
            var row = new TableRow { IsHeader = table.Rows.Count < headerRows };
            foreach (var cellItem in item.Blocks.OfType<ListBlock>().FirstOrDefault()?.Items ?? [])
            {
                var cell = new TableCell();
                cell.Blocks.AddRange(cellItem.Blocks);
                row.Cells.Add(cell);
            }
            if (row.Cells.Count > 0)
                table.Rows.Add(row);
        }
        AddTable(table, title, into);
    }

    private void CsvTable(List<string> content, Dictionary<string, string> options, string title, List<Block> into)
    {
        var table = new Table();
        if (options.GetValueOrDefault("header") is { } header)
            table.Rows.Add(CsvRow(header, isHeader: true));
        var headerRows = int.TryParse(options.GetValueOrDefault("header-rows"), out var h) ? h : 0;
        foreach (var line in Dedent(content).Where(l => !Blank(l)))
            table.Rows.Add(CsvRow(line, isHeader: table.Rows.Count < headerRows));
        AddTable(table, title, into);
    }

    private TableRow CsvRow(string line, bool isHeader)
    {
        var row = new TableRow { IsHeader = isHeader };
        var field = new StringBuilder();
        var quoted = false;
        for (var i = 0; i <= line.Length; i++)
        {
            var c = i < line.Length ? line[i] : ',';
            if (quoted)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { field.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else field.Append(c);
                continue;
            }
            if (c == '"' && field.ToString().Trim().Length == 0) { field.Clear(); quoted = true; continue; }
            if (c == ',')
            {
                row.Cells.Add(Cell(field.ToString().Trim()));
                field.Clear();
                continue;
            }
            field.Append(c);
        }
        return row;
    }

    private TableCell Cell(string text)
    {
        var cell = new TableCell();
        var paragraph = new Paragraph();
        paragraph.Inlines.AddRange(InlineRuns(text));
        cell.Blocks.Add(paragraph);
        return cell;
    }

    private void AddTable(Table table, string title, List<Block> into)
    {
        if (table.Rows.Count == 0)
            return;
        if (title.Length > 0)
        {
            var caption = new Paragraph();
            caption.Inlines.Add(new TextRun(title, TextStyle.Bold));
            into.Add(caption);
        }
        into.Add(table);
    }

    private static bool IsGridBorder(string line) => GridBorder().IsMatch(line);

    /// <summary>A grid table: its columns where the first border has "+", rows between borders, the header above "+===+".</summary>
    private int GridTable(List<string> lines, int i, List<Block> into)
    {
        var end = i;
        while (end < lines.Count && !Blank(lines[end]) && (lines[end].StartsWith('+') || lines[end].StartsWith('|')))
            end++;
        var border = lines[i];
        var cuts = Enumerable.Range(0, border.Length).Where(k => border[k] == '+').ToList();
        var table = new Table();
        var pending = new List<string>();
        var rows = new List<(List<string> Lines, bool Header)>();
        var headerSeen = false;
        for (var k = i + 1; k < end; k++)
        {
            if (IsGridBorder(lines[k]))
            {
                if (pending.Count > 0)
                    rows.Add((pending, false));
                if (lines[k].Contains('=') && !headerSeen)
                {
                    headerSeen = true;
                    for (var r = 0; r < rows.Count; r++)
                        rows[r] = (rows[r].Lines, true);
                }
                pending = [];
                continue;
            }
            pending.Add(lines[k]);
        }
        foreach (var (rowLines, header) in rows)
        {
            var row = new TableRow { IsHeader = header };
            for (var c = 0; c + 1 < cuts.Count; c++)
            {
                var cellLines = rowLines.Select(l => cuts[c] + 1 < l.Length ? l[(cuts[c] + 1)..Math.Min(cuts[c + 1], l.Length)] : "").Select(l => l.TrimEnd()).ToList();
                var cell = new TableCell();
                ParseBlocks(Dedent(cellLines), cell.Blocks, top: false);
                row.Cells.Add(cell);
            }
            table.Rows.Add(row);
        }
        AddTable(table, "", into);
        return end;
    }

    /// <summary>A simple table: columns under the "=" runs of its borders; a border after the first rows ends the header.</summary>
    private int SimpleTable(List<string> lines, int i, List<Block> into)
    {
        var border = lines[i];
        var starts = SimpleColumn().Matches(border).Select(m => m.Index).ToList();
        var borders = new List<int> { i };
        var k = i + 1;
        while (k < lines.Count)
        {
            if (SimpleBorder().IsMatch(lines[k]))
            {
                borders.Add(k);
                // The bottom border has a blank line or the end after it; the one under the header has rows.
                if (k + 1 >= lines.Count || Blank(lines[k + 1]))
                    break;
            }
            k++;
        }
        var end = Math.Min(k + 1, lines.Count);
        var headerEnd = borders.Count >= 3 ? borders[1] : -1;
        var table = new Table();
        TableRow? row = null;
        for (var n = i + 1; n < end; n++)
        {
            if (borders.Contains(n) || Blank(lines[n]))
                continue;
            var line = lines[n];
            var cells = starts.Select((s, c) =>
            {
                var to = c + 1 < starts.Count ? starts[c + 1] : int.MaxValue;
                return s < line.Length ? line[s..Math.Min(to, line.Length)].Trim() : "";
            }).ToList();
            // A line whose first column is empty goes on with the row above.
            if (cells[0].Length == 0 && row is not null)
            {
                for (var c = 0; c < cells.Count; c++)
                {
                    if (cells[c].Length > 0 && row.Cells[c].Blocks.FirstOrDefault() is Paragraph p)
                        p.Inlines.AddRange([new TextRun(" "), .. InlineRuns(cells[c])]);
                }
                continue;
            }
            row = new TableRow { IsHeader = n < headerEnd };
            row.Cells.AddRange(cells.Select(Cell));
            table.Rows.Add(row);
        }
        AddTable(table, "", into);
        return end;
    }

    // ---- Inline markup ----

    /// <summary>Inline markup into runs: **strong**, *emphasis*, ``literal``, `links &lt;…&gt;`_, :roles:`…`, |substitutions| and web addresses.</summary>
    internal List<Inline> InlineRuns(string text)
    {
        var runs = new List<Inline>();
        var plain = new StringBuilder();

        void Flush()
        {
            if (plain.Length > 0)
                AddPlain(plain.ToString(), runs);
            plain.Clear();
        }

        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (c == '\\' && i + 1 < text.Length)
            {
                // An escaped character is itself and starts nothing: "snake\_" isn't a reference.
                if (!char.IsWhiteSpace(text[i + 1]))
                {
                    Flush();
                    runs.Add(new TextRun(text[i + 1].ToString()));
                }
                i += 2;
                continue;
            }
            if (StartOk(text, i))
            {
                if (Span(text, i, "``", "``", out var literal, out var next))
                {
                    Flush();
                    runs.Add(new TextRun(literal, TextStyle.Code));
                    i = next;
                    continue;
                }
                if (Span(text, i, "**", "**", out var strong, out next))
                {
                    Flush();
                    runs.Add(new TextRun(Unescape(strong), TextStyle.Bold));
                    i = next;
                    continue;
                }
                if (Span(text, i, "*", "*", out var emphasis, out next))
                {
                    Flush();
                    runs.Add(new TextRun(Unescape(emphasis), TextStyle.Italic));
                    i = next;
                    continue;
                }
                if (RolePrefix().Match(text, i) is { Success: true } role && role.Index == i && Span(text, i + role.Length - 1, "`", "`", out var roleText, out next))
                {
                    Flush();
                    runs.Add(Role(role.Groups[1].Value, roleText));
                    i = next;
                    continue;
                }
                if (Span(text, i, "`", "`", out var interpreted, out next))
                {
                    Flush();
                    var reference = 0;
                    while (next < text.Length && text[next] == '_' && reference < 2) { reference++; next++; }
                    if (reference == 0 && RoleSuffix().Match(text, next) is { Success: true } suffix && suffix.Index == next)
                    {
                        runs.Add(Role(suffix.Groups[1].Value, interpreted));
                        i = next + suffix.Length;
                        continue;
                    }
                    runs.Add(reference > 0 ? Reference(interpreted) : new TextRun(Unescape(interpreted), TextStyle.Italic));
                    i = next;
                    continue;
                }
                if (c == '|' && Span(text, i, "|", "|", out var substitution, out next))
                {
                    Flush();
                    while (next < text.Length && text[next] == '_') next++;
                    plain.Append(_substitutions.GetValueOrDefault(substitution) ?? substitution);
                    i = next;
                    continue;
                }
                if (FootnoteReference().Match(text, i) is { Success: true } footnote && footnote.Index == i)
                {
                    Flush();
                    runs.Add(new TextRun(footnote.Groups[1].Value, TextStyle.Superscript));
                    i += footnote.Length;
                    continue;
                }
                if (WebAddress().Match(text, i) is { Success: true } url && url.Index == i)
                {
                    Flush();
                    runs.Add(new TextRun(url.Value, TextStyle.None, url.Value));
                    i += url.Length;
                    continue;
                }
            }
            plain.Append(c);
            i++;
        }
        Flush();
        return runs;
    }

    /// <summary>Plain text, where "word_" names a link target: the word, a link when the target is known.</summary>
    private void AddPlain(string text, List<Inline> runs)
    {
        var at = 0;
        foreach (Match m in WordReference().Matches(text))
        {
            if (m.Index > at)
                runs.Add(new TextRun(text[at..m.Index]));
            var name = m.Groups[1].Value;
            runs.Add(_targets.TryGetValue(Normalize(name), out var url) ? new TextRun(name, TextStyle.None, url) : new TextRun(name));
            at = m.Index + m.Length;
        }
        if (at < text.Length)
            runs.Add(new TextRun(text[at..]));
    }

    private TextRun Reference(string content)
    {
        var embedded = EmbeddedTarget().Match(content);
        if (embedded.Success)
        {
            var words = Unescape(embedded.Groups[1].Value.Trim());
            var target = embedded.Groups[2].Value.Trim();
            if (target.EndsWith('_') && _targets.TryGetValue(Normalize(target[..^1]), out var named))
                target = named;
            return new TextRun(words.Length > 0 ? words : target, TextStyle.None, target.Replace(" ", ""));
        }
        var text = Unescape(content);
        return _targets.TryGetValue(Normalize(text), out var url) ? new TextRun(text, TextStyle.None, url) : new TextRun(text);
    }

    private static TextRun Role(string role, string content)
    {
        var name = role.ToLowerInvariant();
        name = name[(name.LastIndexOf(':') + 1)..];
        var text = Unescape(content);
        // Sphinx's cross-references: "Title <target>" shows the title; "~a.b.c" shows "c"; "!x" shows "x".
        var embedded = EmbeddedTarget().Match(text);
        if (embedded.Success && embedded.Groups[1].Value.Trim().Length > 0)
            text = embedded.Groups[1].Value.Trim();
        if (text.StartsWith('~'))
            text = text[(text.LastIndexOf('.') + 1)..];
        text = text.TrimStart('!');
        return name switch
        {
            "code" or "literal" or "samp" or "file" or "command" or "kbd" or "math" or "envvar" or "program" or "option" or "regexp"
                or "func" or "meth" or "class" or "mod" or "attr" or "obj" or "data" or "const" or "exc" => new TextRun(text, TextStyle.Code),
            "sup" or "superscript" => new TextRun(text, TextStyle.Superscript),
            "sub" or "subscript" => new TextRun(text, TextStyle.Subscript),
            "strong" => new TextRun(text, TextStyle.Bold),
            "emphasis" or "title-reference" or "title" or "t" or "dfn" or "guilabel" or "menuselection" => new TextRun(text, TextStyle.Italic),
            "abbr" => new TextRun(Regex.Replace(text, @"\s*\(.*\)\s*$", "")),
            "pep" => new TextRun("PEP " + text, TextStyle.None, "https://peps.python.org/pep-" + text.PadLeft(4, '0') + "/"),
            "rfc" => new TextRun("RFC " + text, TextStyle.None, "https://www.rfc-editor.org/rfc/rfc" + text),
            _ => new TextRun(text),
        };
    }

    private static string Unescape(string text) => Regex.Replace(text, @"\\(\s|.)", m => char.IsWhiteSpace(m.Groups[1].Value[0]) ? "" : m.Groups[1].Value);

    /// <summary>Whether markup may start here: at the start, or after a space or an opening mark, and followed by no space.</summary>
    private static bool StartOk(string text, int i)
    {
        if (i > 0)
        {
            var before = text[i - 1];
            if (!char.IsWhiteSpace(before) && "-:/'\"<([{".IndexOf(before) < 0 && !char.IsPunctuation(before))
                return false;
        }
        return true;
    }

    /// <summary>
    /// Inline markup from <paramref name="i"/>: what lies between <paramref name="open"/> and the next
    /// <paramref name="close"/> that has no space before it and a space, a mark or the end after it.
    /// </summary>
    private static bool Span(string text, int i, string open, string close, out string inner, out int next)
    {
        inner = "";
        next = i;
        if (string.CompareOrdinal(text, i, open, 0, open.Length) != 0)
            return false;
        var from = i + open.Length;
        if (from >= text.Length || char.IsWhiteSpace(text[from]))
            return false;
        // "**" isn't emphasis, nor "``" a reference.
        if (open.Length == 1 && text[from] == open[0])
            return false;
        for (var j = from + 1; j <= text.Length - close.Length; j++)
        {
            if (string.CompareOrdinal(text, j, close, 0, close.Length) != 0)
                continue;
            if (char.IsWhiteSpace(text[j - 1]) || open != "``" && text[j - 1] == '\\')
                continue;
            var after = j + close.Length;
            if (open.Length == 1 && after < text.Length && text[after] == open[0])
                continue;
            if (after < text.Length && !char.IsWhiteSpace(text[after]) && !char.IsPunctuation(text[after]) && "-.,:;!?\\/'\")]}>".IndexOf(text[after]) < 0)
                continue;
            inner = text[from..j];
            next = after;
            return true;
        }
        return false;
    }

    [GeneratedRegex(@"^\.\.\s+_([^:]+|`[^`]+`):\s*(\S.*)$")]
    private static partial Regex TargetLine();

    [GeneratedRegex(@"^\.\.\s+\|([^|]+)\|\s+(?:replace|unicode)::\s*(.*)$")]
    private static partial Regex SubstitutionLine();

    [GeneratedRegex(@"^\[(\d+|#[\w\-]*|\*|[A-Za-z][\w\-.]*)\]\s*(.*)$")]
    private static partial Regex FootnoteHead();

    [GeneratedRegex(@"^(\|[^|]+\|\s+)?([\w\-.:+]+)::(?:\s+(.*))?$")]
    private static partial Regex DirectiveHead();

    [GeneratedRegex(@"^:([\w\-]+):\s*(.*)$")]
    private static partial Regex OptionLine();

    [GeneratedRegex(@"^([-*+•‣⁃])(?: +(.*)|$)")]
    private static partial Regex BulletLine();

    [GeneratedRegex(@"^(\(?)(\d+|#|[a-zA-Z])([.)])(?: +(.*)|$)")]
    private static partial Regex EnumeratedLine();

    [GeneratedRegex(@"^:([^:\s][^:]*):(?:\s+(.*)|$)")]
    private static partial Regex FieldLine();

    [GeneratedRegex(@"^\+(?:[-=]+\+)+\s*$")]
    private static partial Regex GridBorder();

    [GeneratedRegex(@"^=+(?: +=+)+\s*$")]
    private static partial Regex SimpleBorder();

    [GeneratedRegex(@"=+")]
    private static partial Regex SimpleColumn();

    [GeneratedRegex(@":([\w\-+.:]+):`")]
    private static partial Regex RolePrefix();

    [GeneratedRegex(@":([\w\-+.:]+):")]
    private static partial Regex RoleSuffix();

    [GeneratedRegex(@"\[(\d+|#[\w\-]*|\*)\]_")]
    private static partial Regex FootnoteReference();

    [GeneratedRegex(@"(?:https?://|mailto:)[^\s<>]*[^\s<>.,;:!?)\]'""]")]
    private static partial Regex WebAddress();

    [GeneratedRegex(@"\b([\w\-.]*\w)__?(?=$|[\s.,;:!?)\]'""])")]
    private static partial Regex WordReference();

    [GeneratedRegex(@"^(.*?)\s*<([^<>]+)>$", RegexOptions.Singleline)]
    private static partial Regex EmbeddedTarget();
}
