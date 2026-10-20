using System.Globalization;

namespace Petal.Engines.Documents.Sheets;

/// <summary>A cell value: string, double, bool, DateTime or TimeSpan (null when empty), plus how it was displayed.</summary>
internal readonly record struct SheetCell(object? Value, string? Display = null)
{
    public bool IsEmpty => Value is null || Value is string { Length: 0 };

    /// <summary>Text for CSV/TSV and other plain outputs: raw values, invariant culture, ISO dates.</summary>
    public string RawText => Value switch
    {
        null => "",
        string s => s,
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        bool b => b ? "TRUE" : "FALSE",
        DateTime dt => dt.TimeOfDay == TimeSpan.Zero ? dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        TimeSpan ts => ts.ToString(ts.TotalDays >= 1 ? @"d\.hh\:mm\:ss" : @"hh\:mm\:ss", CultureInfo.InvariantCulture),
        _ => Convert.ToString(Value, CultureInfo.InvariantCulture) ?? "",
    };

    /// <summary>What a reader of a printed sheet should see.</summary>
    public string DisplayText => Display ?? Value switch
    {
        double d => d.ToString("G15", CultureInfo.CurrentCulture),
        DateTime dt => dt.TimeOfDay == TimeSpan.Zero ? dt.ToString("d", CultureInfo.CurrentCulture) : dt.ToString("g", CultureInfo.CurrentCulture),
        _ => RawText,
    };

    public bool IsNumeric => Value is double;

    /// <summary>
    /// Text from CSV becomes a number only when writing the number back gives the very same
    /// text, so "007", "1e5" or 20-digit IDs stay text and CSV → XLSX → CSV is lossless.
    /// </summary>
    public static SheetCell FromText(string text)
    {
        if (text.Length is > 0 and <= 17 && (char.IsAsciiDigit(text[0]) || text[0] == '-' && text.Length > 1) &&
            double.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var d) &&
            d.ToString("R", CultureInfo.InvariantCulture) == text && text != "-0")
            return new SheetCell(d);
        return new SheetCell(text.Length == 0 ? null : text);
    }
}

internal sealed class SheetModel(string name)
{
    public string Name { get; set; } = name;
    public List<List<SheetCell>> Rows { get; } = [];

    /// <summary>Column widths in Excel character units, when the source had them.</summary>
    public Dictionary<int, double> ColumnWidths { get; } = [];

    public int ColumnCount => Rows.Count == 0 ? 0 : Rows.Max(r => r.Count);

    public bool IsEmpty => Rows.All(r => r.All(c => c.IsEmpty));

    /// <summary>Drops empty trailing cells and rows.</summary>
    public void Trim()
    {
        foreach (var row in Rows)
            while (row.Count > 0 && row[^1].IsEmpty)
                row.RemoveAt(row.Count - 1);
        while (Rows.Count > 0 && Rows[^1].Count == 0)
            Rows.RemoveAt(Rows.Count - 1);
    }
}

internal sealed class WorkbookModel
{
    public List<SheetModel> Sheets { get; } = [];

    public string? Title { get; set; }
}
