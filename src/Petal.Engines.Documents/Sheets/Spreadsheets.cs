using System.Globalization;
using System.Text;
using ClosedXML.Excel;
using Petal.Engines.Documents.Util;

namespace Petal.Engines.Documents.Sheets;

/// <summary>A table to write: a header row, then rows of text, numbers, dates and durations.</summary>
/// <param name="Formats">Excel number formats by column, e.g. a size shown in KB and MB.</param>
public sealed record SheetTable(string Name, IReadOnlyList<string> Headers, IReadOnlyList<IReadOnlyList<object?>> Rows, IReadOnlyDictionary<int, string>? Formats = null);

/// <summary>Writes tables as an Excel workbook (one sheet each) or as CSV, for reports.</summary>
public static class Spreadsheets
{
    /// <summary>
    /// Each table on its own sheet: a bold header row that stays in view, filters on it, and columns as
    /// wide as their contents. Right-to-left sheets for an Arabic report.
    /// </summary>
    public static void WriteXlsx(string path, IReadOnlyList<SheetTable> tables, bool rightToLeft = false, string? title = null) =>
        SafeOutput.Write(path, temp =>
        {
            using var wb = new XLWorkbook();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var table in tables)
            {
                var ws = wb.AddWorksheet(ExcelSheets.SheetName(table.Name, names));
                ws.RightToLeft = rightToLeft;
                for (int c = 0; c < table.Headers.Count; c++)
                    ws.Cell(1, c + 1).Value = table.Headers[c];
                var header = ws.Range(1, 1, 1, Math.Max(1, table.Headers.Count));
                header.Style.Font.Bold = true;
                header.Style.Fill.BackgroundColor = XLColor.FromHtml("#EEF0F4");
                for (int r = 0; r < table.Rows.Count; r++)
                {
                    var row = table.Rows[r];
                    for (int c = 0; c < row.Count; c++)
                        Set(ws.Cell(r + 2, c + 1), row[c]);
                }
                if (table.Formats is { } formats)
                {
                    foreach (var (column, format) in formats)
                        ws.Column(column + 1).Style.NumberFormat.Format = format;
                }
                if (table.Rows.Count > 0)
                    ws.Range(1, 1, table.Rows.Count + 1, Math.Max(1, table.Headers.Count)).SetAutoFilter();
                ws.SheetView.FreezeRows(1);
                try
                {
                    ws.Columns(1, Math.Max(1, table.Headers.Count)).AdjustToContents(1, Math.Min(table.Rows.Count + 1, 1000), 6, 70);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    // Column measuring needs fonts; widths are cosmetic.
                }
            }
            if (!string.IsNullOrWhiteSpace(title))
                wb.Properties.Title = title;
            // ClosedXML checks the file extension, and outputs are written under a temporary name.
            using var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.ReadWrite);
            wb.SaveAs(stream);
        });

    /// <summary>One table as CSV: UTF-8 with a byte-order mark so Excel reads Arabic names, plain numbers, ISO dates.</summary>
    public static void WriteCsv(string path, SheetTable table) =>
        SafeOutput.Write(path, temp =>
        {
            var csv = new StringBuilder();
            csv.AppendLine(string.Join(',', table.Headers.Select(Quote)));
            foreach (var row in table.Rows)
                csv.AppendLine(string.Join(',', row.Select(v => Quote(Text(v)))));
            File.WriteAllText(temp, csv.ToString(), new UTF8Encoding(true));
        });

    private static void Set(IXLCell cell, object? value)
    {
        switch (value)
        {
            case null:
                break;
            case string s:
                cell.Value = s;
                break;
            case DateTime d:
                cell.Value = d;
                cell.Style.DateFormat.Format = "yyyy-mm-dd hh:mm";
                break;
            case TimeSpan t:
                cell.Value = t;
                cell.Style.NumberFormat.Format = t.TotalHours >= 1 ? "[h]:mm:ss" : "mm:ss";
                break;
            case IConvertible number when value is int or long or double or float or decimal:
                cell.Value = number.ToDouble(CultureInfo.InvariantCulture);
                break;
            default:
                cell.Value = Convert.ToString(value, CultureInfo.InvariantCulture);
                break;
        }
    }

    private static string Text(object? value) => value switch
    {
        null => "",
        DateTime d => d.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        TimeSpan t => t.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };

    private static string Quote(string text) =>
        text.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + text.Replace("\"", "\"\"") + "\"" : text;
}
