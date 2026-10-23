using System.Globalization;
using System.Text.RegularExpressions;
using ClosedXML.Excel;

namespace Petal.Engines.Documents.Pdf;

/// <summary>
/// PDF → Excel: one sheet per page. Word positions are clustered into columns, every visual
/// row becomes a sheet row, and numbers (with thousands separators, percentages, currency or
/// accounting negatives) become real numbers.
/// </summary>
internal static partial class PdfToXlsx
{
    public static void Write(List<PdfPageContent> pages, string output, CancellationToken ct)
    {
        using var wb = new XLWorkbook();
        foreach (var page in pages)
        {
            ct.ThrowIfCancellationRequested();
            var ws = wb.AddWorksheet($"Page {page.Index + 1}");
            var rows = PdfLayout.BuildRows(page);
            var columns = Columns(rows);
            var r = 1;
            double? lastBottom = null;
            foreach (var row in rows)
            {
                // A wide vertical gap becomes an empty row, which keeps separate tables apart.
                if (lastBottom is { } lb && row.Top - lb > 1.5 * row.Segments.Average(s => s.FontSize))
                    r++;
                lastBottom = row.Bottom;
                var cells = new Dictionary<int, List<PdfLine>>();
                foreach (var seg in row.Segments)
                {
                    var col = ColumnFor(columns, seg);
                    if (!cells.TryGetValue(col, out var list))
                        cells[col] = list = [];
                    list.Add(seg);
                }
                foreach (var (col, segs) in cells)
                {
                    var text = string.Join(" ", segs.OrderBy(s => s.Left).Select(s => s.Text)).Trim();
                    if (text.Length == 0)
                        continue;
                    var cell = ws.Cell(r, col + 1);
                    if (ParseNumber(text) is { } number)
                    {
                        cell.Value = number.Value;
                        if (number.Format is not null)
                            cell.Style.NumberFormat.Format = number.Format;
                    }
                    else
                        cell.Value = text;
                    if (segs.All(s => s.Bold))
                        cell.Style.Font.Bold = true;
                }
                r++;
            }
            try
            {
                ws.Columns().AdjustToContents(1, Math.Min(r, 500), 4, 80);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Measuring needs fonts; column widths are cosmetic.
            }
        }
        if (wb.Worksheets.Count == 0)
            wb.AddWorksheet("Page 1");
        // ClosedXML checks the file extension, and outputs are written under a temporary name.
        using (var stream = new FileStream(output, FileMode.CreateNew, FileAccess.ReadWrite))
            wb.SaveAs(stream);
    }

    /// <summary>Column bands: the union of overlapping segment extents of rows that have several segments.</summary>
    internal static List<(double Left, double Right)> Columns(List<PdfRow> rows)
    {
        var spans = rows.Where(r => r.Segments.Count >= 2).SelectMany(r => r.Segments).Select(s => (s.Left, s.Right)).OrderBy(s => s.Left).ToList();
        var columns = new List<(double Left, double Right)>();
        foreach (var (l, r) in spans)
        {
            if (columns.Count > 0 && l <= columns[^1].Right + 1)
                columns[^1] = (columns[^1].Left, Math.Max(columns[^1].Right, r));
            else
                columns.Add((l, r));
        }
        return columns;
    }

    internal static int ColumnFor(List<(double Left, double Right)> columns, PdfLine seg)
    {
        if (columns.Count == 0)
            return 0;
        var best = 0;
        var bestScore = double.MinValue;
        for (var c = 0; c < columns.Count; c++)
        {
            var overlap = Math.Min(columns[c].Right, seg.Right) - Math.Max(columns[c].Left, seg.Left);
            // Prefer overlap; otherwise the nearest band by distance (negative score).
            var score = overlap > 0 ? overlap : -Math.Min(Math.Abs(columns[c].Left - seg.Right), Math.Abs(seg.Left - columns[c].Right));
            if (score > bestScore)
            {
                bestScore = score;
                best = c;
            }
        }
        return best;
    }

    internal readonly record struct ParsedNumber(double Value, string? Format);

    internal static ParsedNumber? ParseNumber(string text)
    {
        var t = text.Trim().Replace("\u00A0", "").Replace(" ", "");
        if (t.Length == 0 || t.Length > 24)
            return null;
        var negative = false;
        if (t.StartsWith('(') && t.EndsWith(')'))
        {
            negative = true;
            t = t[1..^1];
        }
        if (t.StartsWith('-') || t.StartsWith('−'))
        {
            negative = !negative;
            t = t[1..];
        }
        string? currency = null;
        if (t.Length > 0 && "$€£¥".Contains(t[0]))
        {
            currency = t[0].ToString();
            t = t[1..];
        }
        var percent = t.EndsWith('%');
        if (percent)
            t = t[..^1];
        if (!NumberText().IsMatch(t))
            return null;
        // Leading zeros ("007") are identifiers, not numbers.
        if (t.Length > 1 && t[0] == '0' && t[1] != '.')
            return null;
        var grouped = t.Contains(',');
        if (!double.TryParse(t.Replace(",", ""), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value))
            return null;
        if (negative) value = -value;
        var decimals = t.Contains('.') ? t.Length - t.IndexOf('.') - 1 : 0;
        var digits = decimals > 0 ? "0." + new string('0', decimals) : "0";
        string? format = null;
        if (percent)
        {
            value /= 100;
            format = digits + "%";
        }
        else if (currency is not null)
            format = $"\"{currency}\"#,##{digits}";
        else if (grouped)
            format = "#,##" + digits;
        else if (decimals > 0)
            format = digits;
        return new ParsedNumber(value, format);
    }

    [GeneratedRegex(@"^(\d{1,3}(,\d{3})+|\d+)(\.\d+)?$")]
    private static partial Regex NumberText();
}
