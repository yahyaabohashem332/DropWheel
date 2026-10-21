using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Petal.Engines.Documents.Model;
using Petal.Engines.Documents.Text;
using M = Petal.Engines.Documents.Model;

namespace Petal.Engines.Documents.Pdf;

/// <summary>A horizontal run of words with no large gap: one line of a paragraph, or one table cell's line.</summary>
internal sealed class PdfLine
{
    public PdfLine(List<PdfWord> words)
    {
        Words = words;
        Left = words.Min(w => w.Left);
        Right = words.Max(w => w.Right);
        Top = words.Min(w => w.Top);
        Bottom = words.Max(w => w.Bottom);
        var chars = words.Sum(w => w.Text.Length);
        FontSize = WeightedMedian(words.Select(w => (w.FontSize, (double)w.Text.Length)));
        Bold = words.Where(w => w.Bold).Sum(w => w.Text.Length) >= 0.9 * chars;
        Italic = words.Where(w => w.Italic).Sum(w => w.Text.Length) >= 0.9 * chars;
        Rtl = words.Count(w => w.IsRtl) * 2 > words.Count;
        Ordered = Rtl ? words.OrderByDescending(w => w.Right).ToList() : words.OrderBy(w => w.Left).ToList();
        Text = PdfLayout.JoinWords(Ordered);
    }

    public PdfLine(PdfPicture picture)
    {
        Picture = picture;
        Words = [];
        Ordered = [];
        Left = picture.Left;
        Right = picture.Right;
        Top = picture.Top;
        Bottom = picture.Bottom;
        FontSize = 10;
        Text = "";
    }

    public List<PdfWord> Words { get; }
    /// <summary>Words in reading order (right to left for Arabic and Hebrew lines).</summary>
    public List<PdfWord> Ordered { get; }
    public double Left { get; }
    public double Right { get; }
    public double Top { get; }
    public double Bottom { get; }
    public double FontSize { get; }
    public bool Bold { get; }
    public bool Italic { get; }
    public bool Rtl { get; }
    public string Text { get; }
    public PdfPicture? Picture { get; }
    public int Row { get; set; }

    private static double WeightedMedian(IEnumerable<(double Value, double Weight)> items)
    {
        var list = items.OrderBy(i => i.Value).ToList();
        if (list.Count == 0) return 10;
        var half = list.Sum(i => i.Weight) / 2;
        double acc = 0;
        foreach (var (value, weight) in list)
        {
            acc += weight;
            if (acc >= half) return value;
        }
        return list[^1].Value;
    }
}

/// <summary>Words that share a baseline across the whole page, split into segments at large gaps.</summary>
internal sealed class PdfRow
{
    public List<PdfLine> Segments { get; } = [];
    public double Top => Segments.Min(s => s.Top);
    public double Bottom => Segments.Max(s => s.Bottom);
}

internal sealed class PdfLayoutOptions
{
    /// <summary>Insert page breaks between PDF pages (off: text flows, split paragraphs are rejoined).</summary>
    public bool KeepPageBreaks { get; init; }

    public bool RemoveHeadersAndFooters { get; init; } = true;
}

/// <summary>
/// Rebuilds document structure from positioned words: reading order (including multi-column
/// pages), paragraphs, headings from font sizes, bulleted and numbered lists, tables from
/// aligned columns, pictures, and running headers and page numbers removed.
/// </summary>
internal static partial class PdfLayout
{
    private const string BulletChars = "•·▪◦●○■□►▸‣⁃–—-*✓✔➢➤→\uF0B7\uF0A7\uF076\uF0D8\uF0FC\uF0A8\uF06E\uF0E0⁃∙";

    // ───────────────────────────── rows and segments ─────────────────────────────

    public static List<PdfRow> BuildRows(PdfPageContent page, IEnumerable<PdfWord>? words = null)
    {
        var sorted = (words ?? page.Words).OrderBy(w => w.Top).ThenBy(w => w.Left).ToList();
        var rows = new List<(List<PdfWord> Words, double Top, double Bottom)>();
        foreach (var w in sorted)
        {
            var best = -1;
            var bestRatio = 0.0;
            for (var i = rows.Count - 1; i >= 0 && i >= rows.Count - 6; i--)
            {
                var r = rows[i];
                var overlap = Math.Min(r.Bottom, w.Bottom) - Math.Max(r.Top, w.Top);
                var ratio = overlap / Math.Max(0.1, Math.Min(r.Bottom - r.Top, w.Height));
                if (ratio > 0.5 && ratio > bestRatio)
                {
                    best = i;
                    bestRatio = ratio;
                }
            }
            if (best >= 0)
            {
                var r = rows[best];
                r.Words.Add(w);
                // Keep the row's band anchored to its first, typically full-size, words.
                rows[best] = (r.Words, r.Top, r.Bottom);
            }
            else
                rows.Add(([w], w.Top, w.Bottom));
        }

        var result = new List<PdfRow>();
        foreach (var (rowWords, _, _) in rows.OrderBy(r => r.Top))
        {
            var row = new PdfRow();
            var ordered = rowWords.OrderBy(w => w.Left).ToList();
            var current = new List<PdfWord> { ordered[0] };
            for (var i = 1; i < ordered.Count; i++)
            {
                var prev = current[^1];
                var gap = ordered[i].Left - prev.Right;
                var fs = Math.Min(prev.FontSize, ordered[i].FontSize);
                if (gap > 1.4 * fs)
                {
                    row.Segments.Add(new PdfLine(current));
                    current = [];
                }
                current.Add(ordered[i]);
            }
            row.Segments.Add(new PdfLine(current));
            result.Add(row);
        }
        for (var i = 0; i < result.Count; i++)
            foreach (var s in result[i].Segments)
                s.Row = i;
        return result;
    }

    /// <summary>Joins words with spaces, except where glyphs touch (CJK, split ligatures).</summary>
    public static string JoinWords(IReadOnlyList<PdfWord> words)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < words.Count; i++)
        {
            if (i > 0)
            {
                var a = words[i - 1];
                var b = words[i];
                var gap = a.IsRtl && b.IsRtl ? a.Left - b.Right : b.Left - a.Right;
                if (gap > 0.1 * Math.Min(a.FontSize, b.FontSize))
                    sb.Append(' ');
            }
            sb.Append(words[i].Text);
        }
        return sb.ToString();
    }

    // ───────────────────────────── document analysis ─────────────────────────────

    private enum RawKind { Paragraph, ListItem, Table, Picture }

    private sealed class RawBlock
    {
        public RawKind Kind;
        public List<PdfLine> Lines = [];
        public double MarkerX, ColumnLeft, ColumnRight;
        public bool Ordered;
        public int Number = 1;
        public M.Table? Table;
        public PdfPicture? Picture;
        public int Page;

        public double FontSize => Lines.Count == 0 ? 0 : Lines.OrderBy(l => l.Text.Length).Last().FontSize;
        public bool AllBold => Lines.Count > 0 && Lines.All(l => l.Bold);
        public string Text => string.Join(' ', Lines.Select(l => l.Text));
    }

    public static DocModel Analyze(List<PdfPageContent> pages, DocMetadata metadata, PdfLayoutOptions options)
    {
        var doc = new DocModel();
        doc.Metadata.Title = metadata.Title;
        doc.Metadata.Author = metadata.Author;
        doc.Metadata.Subject = metadata.Subject;
        doc.Metadata.Keywords = metadata.Keywords;

        var pageRows = pages.Select(p => BuildRows(p)).ToList();
        if (options.RemoveHeadersAndFooters)
            RemoveRunningLines(pages, pageRows);

        var raw = new List<RawBlock>();
        for (var p = 0; p < pages.Count; p++)
        {
            var blocks = PageBlocks(pages[p], pageRows[p]);
            foreach (var b in blocks)
                b.Page = p;
            if (p > 0 && options.KeepPageBreaks)
                raw.Add(new RawBlock { Kind = RawKind.Paragraph, Page = -1 });
            else if (p > 0 && blocks.Count > 0 && raw.Count > 0)
                TryJoinAcrossPages(raw[^1], blocks[0], blocks);
            raw.AddRange(blocks);
        }

        var bodySize = BodySize(raw);
        var headingSizes = HeadingSizes(raw, bodySize);
        Convert(raw, bodySize, headingSizes, doc.Blocks);

        if (doc.Metadata.Title is null && doc.Blocks.OfType<Heading>().FirstOrDefault() is { } first)
            doc.Metadata.Title = first.Inlines.PlainText().Trim();
        return doc;
    }

    /// <summary>Removes lines repeated at the top or bottom of most pages, and bare page numbers.</summary>
    private static void RemoveRunningLines(List<PdfPageContent> pages, List<List<PdfRow>> pageRows)
    {
        if (pages.Count < 2)
            return;
        string Key(PdfLine l) => DigitRun().Replace(l.Text.Trim().ToLowerInvariant(), "#");
        var counts = new Dictionary<(bool Top, string Key), int>();
        for (var p = 0; p < pages.Count; p++)
        {
            var seen = new HashSet<(bool, string)>();
            foreach (var row in pageRows[p])
                foreach (var seg in row.Segments)
                    if (Zone(pages[p], seg) is { } top && seg.Text.Length > 0)
                        seen.Add((top, Key(seg)));
            foreach (var k in seen)
                counts[k] = counts.GetValueOrDefault(k) + 1;
        }
        var threshold = Math.Max(2, (int)Math.Ceiling(pages.Count * 0.5));
        for (var p = 0; p < pages.Count; p++)
        {
            foreach (var row in pageRows[p])
                row.Segments.RemoveAll(seg => Zone(pages[p], seg) is { } top &&
                                              (counts.GetValueOrDefault((top, Key(seg))) >= threshold || PageNumber().IsMatch(Key(seg))));
            pageRows[p].RemoveAll(r => r.Segments.Count == 0);
        }
    }

    /// <summary>True for the top band, false for the bottom band, null in between.</summary>
    private static bool? Zone(PdfPageContent page, PdfLine line)
    {
        if (line.Bottom < page.Height * 0.09) return true;
        if (line.Top > page.Height * 0.91) return false;
        return null;
    }

    private static void TryJoinAcrossPages(RawBlock last, RawBlock next, List<RawBlock> nextBlocks)
    {
        if (last.Kind != RawKind.Paragraph || next.Kind != RawKind.Paragraph || last.Lines.Count == 0 || next.Lines.Count == 0)
            return;
        var tail = last.Lines[^1].Text.TrimEnd();
        var head = next.Lines[0].Text.TrimStart();
        if (tail.Length == 0 || head.Length == 0)
            return;
        if (".!?:;\"”’)".Contains(tail[^1]) || !char.IsLower(head[0]))
            return;
        if (Math.Abs(last.FontSize - next.FontSize) > 0.1 * last.FontSize || last.AllBold != next.AllBold)
            return;
        last.Lines.AddRange(next.Lines);
        nextBlocks.RemoveAt(0);
    }

    private static List<RawBlock> PageBlocks(PdfPageContent page, List<PdfRow> rows)
    {
        // Pictures take part in reading order like lines do.
        var pictureRows = page.Pictures.Select(pic => { var r = new PdfRow(); r.Segments.Add(new PdfLine(pic)); return r; });
        var all = rows.Concat(pictureRows).OrderBy(r => r.Top).ToList();
        var columns = DetectColumns(page, all);

        var blocks = new List<RawBlock>();
        var columnRows = columns.Select(_ => new List<List<PdfLine>>()).ToList();
        var spanning = new List<List<PdfLine>>();

        void FlushColumns()
        {
            for (var c = 0; c < columnRows.Count; c++)
            {
                if (columnRows[c].Count > 0)
                    blocks.AddRange(ColumnBlocks(columnRows[c]));
                columnRows[c] = [];
            }
        }
        void FlushSpanning()
        {
            if (spanning.Count > 0)
                blocks.AddRange(ColumnBlocks(spanning));
            spanning = [];
        }

        foreach (var row in all)
        {
            var assigned = row.Segments.Select(s => (Segment: s, Column: ColumnOf(columns, s))).ToList();
            if (columns.Count > 1 && assigned.Any(a => a.Column < 0))
            {
                FlushColumns();
                spanning.Add(row.Segments);
            }
            else
            {
                FlushSpanning();
                foreach (var group in assigned.GroupBy(a => Math.Max(0, a.Column)))
                {
                    var target = columnRows[group.Key];
                    target.Add(group.Select(g => g.Segment).ToList());
                }
            }
        }
        FlushSpanning();
        FlushColumns();
        return blocks;
    }

    private static int ColumnOf(List<(double Left, double Right)> columns, PdfLine line)
    {
        if (columns.Count <= 1)
            return 0;
        for (var c = 0; c < columns.Count; c++)
            if (line.Left >= columns[c].Left - 3 && line.Right <= columns[c].Right + 3)
                return c;
        return -1;
    }

    /// <summary>Finds vertical gutters that separate text columns. One column when there are none.</summary>
    private static List<(double Left, double Right)> DetectColumns(PdfPageContent page, List<PdfRow> rows)
    {
        var text = rows.SelectMany(r => r.Segments).Where(s => s.Picture is null && s.Words.Count >= 3).ToList();
        var single = new List<(double, double)> { (0, page.Width) };
        if (text.Count < 6)
            return single;
        var width = (int)Math.Ceiling(page.Width) + 1;
        var coverage = new int[width];
        foreach (var s in text)
            for (var x = Math.Max(0, (int)s.Left); x < Math.Min(width, (int)Math.Ceiling(s.Right)); x++)
                coverage[x]++;
        var max = coverage.Max();
        var minX = (int)text.Min(s => s.Left) + 10;
        var maxX = (int)text.Max(s => s.Right) - 10;
        var fs = text.Select(s => s.FontSize).Order().ElementAt(text.Count / 2);

        var gutters = new List<(double Left, double Right)>();
        var x0 = -1;
        for (var x = minX; x <= maxX; x++)
        {
            var low = x < width && coverage[x] <= max * 0.08;
            if (low && x0 < 0) x0 = x;
            if ((!low || x == maxX) && x0 >= 0)
            {
                var x1 = low ? x : x - 1;
                if (x1 - x0 >= Math.Max(6, 0.6 * fs))
                {
                    var left = text.Count(s => s.Right <= x0 + 2);
                    var right = text.Count(s => s.Left >= x1 - 2);
                    if (left >= 3 && right >= 3 && left >= text.Count * 0.15 && right >= text.Count * 0.15)
                        gutters.Add((x0, x1));
                }
                x0 = -1;
            }
        }
        if (gutters.Count == 0)
            return single;
        var columns = new List<(double, double)>();
        var start = 0.0;
        foreach (var (gl, gr) in gutters)
        {
            columns.Add((start, gl));
            start = gr;
        }
        columns.Add((start, page.Width));
        return columns;
    }

    /// <summary>Turns the rows of one column (or one full-width band) into paragraphs, list items, tables and pictures.</summary>
    private static List<RawBlock> ColumnBlocks(List<List<PdfLine>> rows)
    {
        var blocks = new List<RawBlock>();
        var lines = rows.SelectMany(r => r).Where(l => l.Picture is null).ToList();
        var colLeft = lines.Count > 0 ? lines.Min(l => l.Left) : 0;
        var colRight = lines.Count > 0 ? lines.Max(l => l.Right) : 0;
        RawBlock? current = null;

        void Close()
        {
            if (current is not null)
                blocks.Add(current);
            current = null;
        }

        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            if (row.Count == 1 && row[0].Picture is { } pic)
            {
                Close();
                blocks.Add(new RawBlock { Kind = RawKind.Picture, Picture = pic, Lines = [row[0]] });
                continue;
            }
            if (row.Count >= 2 && TryTable(rows, i, out var table, out var consumed))
            {
                Close();
                blocks.Add(new RawBlock { Kind = RawKind.Table, Table = table });
                i += consumed - 1;
                continue;
            }

            // Several segments that are not a table: read them in order as one line.
            var line = row.Count == 1 ? row[0] : new PdfLine(row.Where(s => s.Picture is null).SelectMany(s => s.Words).ToList());
            if (MarkerOf(line) is { } marker)
            {
                Close();
                current = new RawBlock
                {
                    Kind = RawKind.ListItem, Lines = [line], MarkerX = line.Rtl ? colRight - line.Right : line.Left,
                    Ordered = marker.Ordered, Number = marker.Number, ColumnLeft = colLeft, ColumnRight = colRight,
                };
                continue;
            }
            if (current is not null && Continues(current, line, colLeft, colRight))
            {
                current.Lines.Add(line);
                continue;
            }
            Close();
            current = new RawBlock { Kind = RawKind.Paragraph, Lines = [line], ColumnLeft = colLeft, ColumnRight = colRight };
        }
        Close();
        return blocks;
    }

    private static bool Continues(RawBlock block, PdfLine line, double colLeft, double colRight)
    {
        var prev = block.Lines[^1];
        var fs = prev.FontSize;
        var gap = line.Top - prev.Bottom;
        if (gap < -0.5 * fs || gap > 0.55 * fs)
            return false;
        if (Math.Abs(line.FontSize - fs) > 0.15 * fs)
            return false;
        if (block.Kind == RawKind.ListItem)
        {
            // Wrapped item text sits right of the marker; a line back at the margin ends the item.
            var indent = line.Rtl ? colRight - line.Right : line.Left;
            return indent > block.MarkerX + 0.6 * fs;
        }
        if (prev.Bold != line.Bold)
            return false;
        // The previous line stopped well short of the margin and ended a sentence.
        var shortBy = prev.Rtl ? prev.Left - colLeft : colRight - prev.Right;
        var t = prev.Text.TrimEnd();
        if (t.Length > 0 && shortBy > Math.Max(0.12 * (colRight - colLeft), 3 * fs) && ".!?:;\"”’)]".Contains(t[^1]))
            return false;
        // A first-line indent starts a new paragraph.
        var lineIndent = line.Rtl ? colRight - line.Right : line.Left - colLeft;
        var prevIndent = prev.Rtl ? colRight - prev.Right : prev.Left - colLeft;
        if (lineIndent > prevIndent + 1.2 * fs && prevIndent < 0.5 * fs && block.Lines.Count >= 2)
            return false;
        return true;
    }

    private readonly record struct Marker(bool Ordered, int Number);

    private static Marker? MarkerOf(PdfLine line)
    {
        if (line.Ordered.Count < 2)
            return null;
        var first = line.Ordered[0];
        var text = first.Text;
        if (text.Length == 1 && BulletChars.Contains(text[0]))
            return new Marker(false, 1);
        if (text == "o" && first.FontName is { } font && (font.Contains("Courier", StringComparison.OrdinalIgnoreCase) || font.Contains("Symbol", StringComparison.OrdinalIgnoreCase) || font.Contains("Wingdings", StringComparison.OrdinalIgnoreCase)))
            return new Marker(false, 1);
        var m = NumberMarker().Match(text);
        if (m.Success)
        {
            var token = m.Groups[1].Value;
            var number = int.TryParse(token, out var n) ? n : token.Length == 1 && char.IsLetter(token[0]) ? char.ToLowerInvariant(token[0]) - 'a' + 1 : 1;
            // "1." followed by text is a list; "2019." alone is a year, so cap the number.
            if (number is >= 0 and <= 999)
                return new Marker(true, number);
        }
        return null;
    }

    // ───────────────────────────── tables ─────────────────────────────

    private static bool TryTable(List<List<PdfLine>> rows, int start, out M.Table table, out int consumed)
    {
        table = null!;
        consumed = 0;
        var fs = rows[start].Select(s => s.FontSize).Average();
        var included = new List<int> { start };
        var multi = 1;
        double minLeft = rows[start].Min(s => s.Left), maxRight = rows[start].Max(s => s.Right);
        for (var j = start + 1; j < rows.Count; j++)
        {
            var row = rows[j];
            if (row.Any(s => s.Picture is not null))
                break;
            var gap = row.Min(s => s.Top) - rows[included[^1]].Max(s => s.Bottom);
            if (row.Count >= 2 && gap <= 3.0 * fs)
            {
                included.Add(j);
                multi++;
                minLeft = Math.Min(minLeft, row.Min(s => s.Left));
                maxRight = Math.Max(maxRight, row.Max(s => s.Right));
            }
            else if (row.Count == 1 && gap <= 1.0 * fs && row[0].Left >= minLeft - 2 && row[0].Right <= maxRight + 2)
                included.Add(j);
            else
                break;
        }
        while (included.Count > 0 && rows[included[^1]].Count < 2)
            included.RemoveAt(included.Count - 1);
        if (multi < 2 || included.Count < 2)
            return false;

        // Columns are the union of overlapping segment extents across the multi-segment rows.
        var spans = included.Where(i => rows[i].Count >= 2).SelectMany(i => rows[i]).Select(s => (s.Left, s.Right)).OrderBy(s => s.Left).ToList();
        var columns = new List<(double Left, double Right)>();
        foreach (var (l, r) in spans)
        {
            if (columns.Count > 0 && l <= columns[^1].Right + 1)
                columns[^1] = (columns[^1].Left, Math.Max(columns[^1].Right, r));
            else
                columns.Add((l, r));
        }
        if (columns.Count < 2)
            return false;

        // Two wide columns of running text are a page layout, not a table.
        var segs = included.Where(i => rows[i].Count >= 2).SelectMany(i => rows[i]).ToList();
        if (columns.Count == 2 && segs.Average(s => s.Words.Count) >= 6)
            return false;

        // A single-segment row that straddles columns is a paragraph: stop the table before it.
        var final = new List<int>();
        foreach (var i in included)
        {
            if (rows[i].Count == 1 && Overlapping(columns, rows[i][0]).Count > 1)
                break;
            final.Add(i);
        }
        while (final.Count > 0 && rows[final[^1]].Count < 2)
            final.RemoveAt(final.Count - 1);
        if (final.Count(i => rows[i].Count >= 2) < 2)
            return false;

        var cellRows = new List<(List<PdfLine>[] Cells, double Top, double Bottom)>();
        foreach (var i in final)
        {
            var cells = Enumerable.Range(0, columns.Count).Select(_ => new List<PdfLine>()).ToArray();
            foreach (var seg in rows[i])
            {
                var best = Overlapping(columns, seg).OrderByDescending(c => c.Overlap).Select(c => c.Index).DefaultIfEmpty(NearestColumn(columns, seg)).First();
                cells[best].Add(seg);
            }
            var top = rows[i].Min(s => s.Top);
            var bottom = rows[i].Max(s => s.Bottom);
            // Wrapped text inside a cell: tight line gap and an empty first column.
            if (cellRows.Count > 0 && top - cellRows[^1].Bottom <= 0.35 * fs && cells[0].Count == 0 && cellRows[^1].Cells[0].Count > 0)
            {
                for (var c = 0; c < columns.Count; c++)
                    cellRows[^1].Cells[c].AddRange(cells[c]);
                cellRows[^1] = (cellRows[^1].Cells, cellRows[^1].Top, bottom);
                continue;
            }
            cellRows.Add((cells, top, bottom));
        }

        table = new M.Table();
        foreach (var (cells, _, _) in cellRows)
        {
            var row = new M.TableRow();
            foreach (var cell in cells)
            {
                var tc = new M.TableCell();
                if (cell.Count > 0)
                {
                    var p = new M.Paragraph();
                    p.Inlines.AddRange(Runs(cell, stripBold: false));
                    tc.Blocks.Add(p);
                }
                row.Cells.Add(tc);
            }
            table.Rows.Add(row);
        }
        var firstWords = cellRows[0].Cells.SelectMany(c => c).SelectMany(l => l.Words).ToList();
        if (firstWords.Count > 0 && firstWords.All(w => w.Bold) && cellRows.Skip(1).SelectMany(r => r.Cells).SelectMany(c => c).SelectMany(l => l.Words).Any(w => !w.Bold))
        {
            table.Rows[0].IsHeader = true;
            foreach (var run in table.Rows[0].Cells.SelectMany(c => c.Blocks).OfType<M.Paragraph>().SelectMany(p => p.Inlines).OfType<TextRun>())
                run.Style &= ~TextStyle.Bold;
        }
        consumed = final[^1] - start + 1;
        return true;
    }

    private static List<(int Index, double Overlap)> Overlapping(List<(double Left, double Right)> columns, PdfLine seg)
    {
        var result = new List<(int, double)>();
        for (var c = 0; c < columns.Count; c++)
        {
            var overlap = Math.Min(columns[c].Right, seg.Right) - Math.Max(columns[c].Left, seg.Left);
            if (overlap > 0)
                result.Add((c, overlap));
        }
        return result;
    }

    private static int NearestColumn(List<(double Left, double Right)> columns, PdfLine seg)
    {
        var center = (seg.Left + seg.Right) / 2;
        return Enumerable.Range(0, columns.Count).OrderBy(c => Math.Abs((columns[c].Left + columns[c].Right) / 2 - center)).First();
    }

    // ───────────────────────────── classification ─────────────────────────────

    private static double BodySize(List<RawBlock> raw)
    {
        var weights = new Dictionary<double, int>();
        foreach (var line in raw.Where(b => b.Kind is RawKind.Paragraph or RawKind.ListItem).SelectMany(b => b.Lines))
        {
            var key = Math.Round(line.FontSize * 2) / 2;
            weights[key] = weights.GetValueOrDefault(key) + line.Text.Length;
        }
        return weights.Count == 0 ? 10 : weights.MaxBy(kv => kv.Value).Key;
    }

    private static bool IsSizeHeading(RawBlock b, double body) =>
        b.Kind == RawKind.Paragraph && b.Lines.Count <= 4 && b.Text.Length <= 300 && b.FontSize >= body * 1.15 && b.Text.Any(char.IsLetter);

    private static bool IsBoldHeading(RawBlock b, double body) =>
        b.Kind == RawKind.Paragraph && b.Lines.Count == 1 && b.AllBold && b.Text.Length is > 1 and <= 120 &&
        b.FontSize >= body * 0.95 && !b.Text.TrimEnd().EndsWith('.') && b.Text.Any(char.IsLetter);

    private static List<double> HeadingSizes(List<RawBlock> raw, double body)
    {
        var sizes = raw.Where(b => IsSizeHeading(b, body)).Select(b => b.FontSize).OrderDescending().ToList();
        var clusters = new List<double>();
        foreach (var s in sizes)
            if (clusters.Count == 0 || clusters[^1] - s > 0.75)
                clusters.Add(s);
        return clusters;
    }

    private static int LevelFor(double size, List<double> clusters)
    {
        for (var i = 0; i < clusters.Count; i++)
            if (size >= clusters[i] - 0.75)
                return Math.Min(i + 1, 6);
        return Math.Min(clusters.Count + 1, 6);
    }

    private static void Convert(List<RawBlock> raw, double body, List<double> headingSizes, List<Block> output)
    {
        var listRun = new List<RawBlock>();

        void FlushList()
        {
            if (listRun.Count == 0)
                return;
            var step = Math.Max(10, 1.5 * listRun.Average(b => b.FontSize));
            var baseX = listRun.Min(b => b.MarkerX);
            var entries = listRun.Select(b =>
            {
                var p = new M.Paragraph();
                p.Inlines.AddRange(ParagraphInlines(b.Lines, skipFirstWord: true));
                var level = (int)Math.Clamp(Math.Round((b.MarkerX - baseX) / step), 0, 5);
                return new ListEntry(level, b.Ordered, p, b.Number);
            }).ToList();
            output.AddRange(ListGrouping.Build(entries));
            listRun.Clear();
        }

        foreach (var b in raw)
        {
            if (b.Kind == RawKind.ListItem)
            {
                listRun.Add(b);
                continue;
            }
            FlushList();
            switch (b.Kind)
            {
                case RawKind.Paragraph when b.Page == -1:
                    output.Add(new M.PageBreak());
                    break;
                case RawKind.Paragraph when IsSizeHeading(b, body):
                {
                    var h = new Heading(LevelFor(b.FontSize, headingSizes));
                    h.Inlines.AddRange(ParagraphInlines(b.Lines, skipFirstWord: false, stripBold: true));
                    output.Add(h);
                    break;
                }
                case RawKind.Paragraph when IsBoldHeading(b, body):
                {
                    var h = new Heading(Math.Min(headingSizes.Count + 1, 6));
                    h.Inlines.AddRange(ParagraphInlines(b.Lines, skipFirstWord: false, stripBold: true));
                    output.Add(h);
                    break;
                }
                case RawKind.Paragraph:
                {
                    var p = new M.Paragraph { Alignment = AlignmentOf(b) };
                    p.Inlines.AddRange(ParagraphInlines(b.Lines, skipFirstWord: false));
                    if (p.Inlines.Count > 0)
                        output.Add(p);
                    break;
                }
                case RawKind.Table when b.Table is not null:
                    output.Add(b.Table);
                    break;
                case RawKind.Picture when b.Picture is not null:
                    output.Add(new ImageBlock(b.Picture.Image));
                    break;
            }
        }
        FlushList();
    }

    private static M.Alignment AlignmentOf(RawBlock b)
    {
        var width = b.ColumnRight - b.ColumnLeft;
        if (width <= 0 || b.Lines.Any(l => l.Rtl))
            return M.Alignment.Start;
        var fs = b.FontSize;
        var centered = b.Lines.All(l =>
        {
            var left = l.Left - b.ColumnLeft;
            var right = b.ColumnRight - l.Right;
            return left > 4 * fs && right > 4 * fs && Math.Abs(left - right) < 1.5 * fs;
        });
        if (centered)
            return M.Alignment.Center;
        var rightAligned = b.Lines.All(l => b.ColumnRight - l.Right < 0.5 * fs && l.Left - b.ColumnLeft > 0.3 * width);
        return rightAligned ? M.Alignment.End : M.Alignment.Start;
    }

    /// <summary>The runs of a paragraph: lines joined (hyphenation undone), formatting and links kept.</summary>
    private static List<Inline> ParagraphInlines(List<PdfLine> lines, bool skipFirstWord, bool stripBold = false)
    {
        var words = new List<PdfWord>();
        var breaks = new HashSet<int>();
        for (var i = 0; i < lines.Count; i++)
        {
            var ordered = lines[i].Ordered;
            if (i == 0 && skipFirstWord && ordered.Count > 1)
                ordered = ordered.Skip(1).ToList();
            if (words.Count > 0)
                breaks.Add(words.Count);
            words.AddRange(ordered);
        }
        return Runs(words, breaks, stripBold, lines.Count > 0 ? lines[0].FontSize : 10);
    }

    private static List<Inline> Runs(List<PdfLine> lines, bool stripBold) =>
        ParagraphInlines(lines, skipFirstWord: false, stripBold);

    private static List<Inline> Runs(List<PdfWord> words, HashSet<int> lineStarts, bool stripBold, double lineSize)
    {
        var result = new List<Inline>();
        var sb = new StringBuilder();
        TextStyle style = TextStyle.None;
        string? link = null;

        void Flush()
        {
            if (sb.Length > 0)
                result.Add(new TextRun(sb.ToString(), style, link));
            sb.Clear();
        }

        for (var i = 0; i < words.Count; i++)
        {
            var w = words[i];
            var s = TextStyle.None;
            if (w.Bold && !stripBold) s |= TextStyle.Bold;
            if (w.Italic) s |= TextStyle.Italic;
            if (w.Link is not null) s |= TextStyle.Underline;
            // Small raised text is a superscript (footnote markers, exponents).
            if (w.FontSize < 0.75 * lineSize && i > 0 && w.Bottom < words[i - 1].Bottom - 0.2 * lineSize)
                s |= TextStyle.Superscript;
            if (w.Link is not null) s &= ~TextStyle.Underline;

            var separator = "";
            if (i > 0)
            {
                var prev = words[i - 1];
                if (lineStarts.Contains(i))
                {
                    // Undo end-of-line hyphenation: "exam-" + "ple".
                    var tail = sb.Length > 0 ? sb.ToString() : (result.LastOrDefault() as TextRun)?.Text ?? "";
                    if (tail.Length > 1 && tail[^1] == '-' && char.IsLetter(tail[^2]) && w.Text.Length > 0 && char.IsLower(w.Text[0]))
                    {
                        if (sb.Length > 0) sb.Length--;
                        else if (result.LastOrDefault() is TextRun lr) lr.Text = lr.Text[..^1];
                    }
                    else
                        separator = " ";
                }
                else
                {
                    var gap = prev.IsRtl && w.IsRtl ? prev.Left - w.Right : w.Left - prev.Right;
                    separator = gap > 0.1 * Math.Min(prev.FontSize, w.FontSize) ? " " : "";
                }
            }

            if (i == 0 || s != style || w.Link != link)
            {
                // The space between differently styled words stays with the earlier run.
                sb.Append(separator);
                Flush();
                style = s;
                link = w.Link;
            }
            else
                sb.Append(separator);
            sb.Append(w.Text);
        }
        Flush();
        return result.Coalesce();
    }

    [GeneratedRegex(@"\d+")]
    private static partial Regex DigitRun();

    [GeneratedRegex(@"^[\-–—\s]*(page\s*|p\.\s*|صفحة\s*)?#(\s*(of|/|من)\s*#)?[\-–—\s]*$")]
    private static partial Regex PageNumber();

    [GeneratedRegex(@"^\(?(\d{1,3}|[a-zA-Z]|[ivxlcdm]{1,6}|[IVXLCDM]{1,6})[.)]$")]
    private static partial Regex NumberMarker();
}
