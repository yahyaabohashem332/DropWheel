using System.Globalization;
using ClosedXML.Excel;
using ExcelDataReader;

namespace Petal.Engines.Documents.Sheets;

/// <summary>XLSX through ClosedXML (read and write) and legacy XLS through ExcelDataReader (read only).</summary>
internal static class ExcelSheets
{
    private const int MaxCellText = 32767;

    public static WorkbookModel ReadXlsx(string path)
    {
        using var wb = new XLWorkbook(path);
        var model = new WorkbookModel { Title = NullIfBlank(wb.Properties.Title) };
        foreach (var ws in wb.Worksheets)
        {
            var sheet = new SheetModel(ws.Name);
            var used = ws.RangeUsed(XLCellsUsedOptions.Contents);
            if (used is not null)
            {
                var lastRow = used.LastRow().RowNumber();
                var lastCol = used.LastColumn().ColumnNumber();
                for (var r = 1; r <= lastRow; r++)
                {
                    var row = new List<SheetCell>(lastCol);
                    for (var c = 1; c <= lastCol; c++)
                        row.Add(Read(ws.Cell(r, c)));
                    sheet.Rows.Add(row);
                }
                for (var c = 1; c <= lastCol; c++)
                    sheet.ColumnWidths[c - 1] = ws.Column(c).Width;
            }
            sheet.Trim();
            model.Sheets.Add(sheet);
        }
        return model;
    }

    private static SheetCell Read(IXLCell cell)
    {
        var value = cell.Value;
        object? raw = value.Type switch
        {
            XLDataType.Blank => null,
            XLDataType.Boolean => value.GetBoolean(),
            XLDataType.Number => value.GetNumber(),
            XLDataType.Text => value.GetText(),
            XLDataType.DateTime => value.GetDateTime(),
            XLDataType.TimeSpan => value.GetTimeSpan(),
            XLDataType.Error => "#" + value.GetError().ToString().ToUpperInvariant(),
            _ => value.ToString(),
        };
        if (raw is null)
            return default;
        string? display = null;
        try
        {
            display = cell.GetFormattedString();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Exotic number formats: fall back to the raw value.
        }
        return new SheetCell(raw, string.IsNullOrEmpty(display) ? null : display);
    }

    public static void WriteXlsx(WorkbookModel model, string path)
    {
        using var wb = new XLWorkbook();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var sheet in model.Sheets.Count > 0 ? model.Sheets : [new SheetModel("Sheet1")])
        {
            var ws = wb.AddWorksheet(SheetName(sheet.Name, names));
            for (var r = 0; r < sheet.Rows.Count; r++)
            {
                var row = sheet.Rows[r];
                for (var c = 0; c < row.Count; c++)
                {
                    var cell = row[c];
                    if (cell.IsEmpty)
                        continue;
                    var target = ws.Cell(r + 1, c + 1);
                    switch (cell.Value)
                    {
                        case double d when double.IsFinite(d):
                            target.Value = d;
                            break;
                        case bool b:
                            target.Value = b;
                            break;
                        case DateTime dt:
                            target.Value = dt;
                            target.Style.DateFormat.Format = dt.TimeOfDay == TimeSpan.Zero ? "yyyy-mm-dd" : "yyyy-mm-dd hh:mm:ss";
                            break;
                        case TimeSpan ts:
                            target.Value = ts;
                            break;
                        default:
                            var text = cell.RawText;
                            // Text that looks like a formula stays text: CSV is data, not code.
                            target.Value = text.Length > MaxCellText ? text[..MaxCellText] : text;
                            if (text.Contains('\n'))
                                target.Style.Alignment.WrapText = true;
                            break;
                    }
                }
            }
            if (sheet.ColumnWidths.Count > 0)
            {
                foreach (var (c, w) in sheet.ColumnWidths)
                    if (w > 0) ws.Column(c + 1).Width = w;
            }
            else if (sheet.Rows.Count > 0)
            {
                try
                {
                    ws.Columns(1, Math.Max(1, sheet.ColumnCount)).AdjustToContents(1, Math.Min(sheet.Rows.Count, 1000), 4, 60);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    // Column measuring needs fonts; widths are cosmetic.
                }
            }
        }
        if (!string.IsNullOrWhiteSpace(model.Title))
            wb.Properties.Title = model.Title;
        // ClosedXML checks the file extension, and outputs are written under a temporary name.
        using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite))
            wb.SaveAs(stream);
    }

    /// <summary>Excel sheet names: at most 31 characters, none of : \ / ? * [ ], unique.</summary>
    internal static string SheetName(string name, HashSet<string> used)
    {
        var clean = new string((name ?? "").Select(c => ":\\/?*[]".Contains(c) || char.IsControl(c) ? '_' : c).ToArray()).Trim().Trim('\'');
        if (clean.Length == 0 || clean.Equals("History", StringComparison.OrdinalIgnoreCase))
            clean = "Sheet";
        if (clean.Length > 31)
            clean = clean[..31];
        var candidate = clean;
        for (var n = 2; !used.Add(candidate); n++)
        {
            var suffix = $" ({n})";
            candidate = (clean.Length + suffix.Length > 31 ? clean[..(31 - suffix.Length)] : clean) + suffix;
        }
        return candidate;
    }

    public static WorkbookModel ReadXls(string path)
    {
        Text.TextFile.EnsureCodePages();
        using var stream = File.OpenRead(path);
        using var reader = ExcelReaderFactory.CreateReader(stream);
        var model = new WorkbookModel();
        do
        {
            var sheet = new SheetModel(reader.Name ?? $"Sheet{model.Sheets.Count + 1}");
            while (reader.Read())
            {
                var row = new List<SheetCell>(reader.FieldCount);
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    var value = reader.GetValue(i);
                    if (value is null)
                    {
                        row.Add(default);
                        continue;
                    }
                    object normalized = value switch
                    {
                        double or string or bool or DateTime or TimeSpan => value,
                        int n => (double)n,
                        long l => (double)l,
                        float f => (double)f,
                        decimal m => (double)m,
                        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "",
                    };
                    row.Add(new SheetCell(normalized, Display(normalized, reader.GetNumberFormatString(i))));
                }
                sheet.Rows.Add(row);
            }
            sheet.Trim();
            model.Sheets.Add(sheet);
        }
        while (reader.NextResult());
        return model;
    }

    private static string? Display(object value, string? format)
    {
        if (value is string or bool || string.IsNullOrEmpty(format) || format == "General")
            return null;
        try
        {
            var nf = new ExcelNumberFormat.NumberFormat(format);
            return nf.IsValid ? nf.Format(value, CultureInfo.CurrentCulture) : null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }

    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
