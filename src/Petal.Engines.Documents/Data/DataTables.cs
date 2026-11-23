using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Petal.Engines.Documents.Sheets;

namespace Petal.Engines.Documents.Data;

/// <summary>
/// Records as tables and tables as records. A table's first row names the columns; each row after it becomes an
/// object. A list of objects becomes a table whose columns are every name the objects use, in the order met; a
/// value that is itself a list or an object stays in its cell as JSON. A workbook of several sheets is an object
/// of lists, by sheet name, and back.
/// </summary>
internal static class DataTables
{
    private static readonly JsonSerializerOptions Compact = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    // ---------- tables to records ----------

    public static JsonNode ToJson(WorkbookModel workbook)
    {
        var sheets = workbook.Sheets.Where(s => !s.IsEmpty).ToList();
        if (sheets.Count == 0)
            return new JsonArray();
        if (sheets.Count == 1)
            return Records(sheets[0]);
        var obj = new JsonObject();
        foreach (var sheet in sheets)
        {
            var name = sheet.Name;
            for (int n = 2; obj.ContainsKey(name); n++)
                name = $"{sheet.Name} {n}";
            obj[name] = Records(sheet);
        }
        return obj;
    }

    private static JsonArray Records(SheetModel sheet)
    {
        sheet.Trim();
        var array = new JsonArray();
        if (sheet.Rows.Count == 0)
            return array;
        var columns = Headers(sheet.Rows[0], sheet.ColumnCount);
        foreach (var row in sheet.Rows.Skip(1))
        {
            if (row.All(c => c.IsEmpty))
                continue;
            var record = new JsonObject();
            for (int c = 0; c < columns.Count; c++)
                record[columns[c]] = c < row.Count ? Value(row[c]) : null;
            array.Add(record);
        }
        return array;
    }

    /// <summary>Column names from the first row; an empty one is "Column 3", one used twice "Name 2".</summary>
    private static List<string> Headers(List<SheetCell> row, int count)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        var names = new List<string>();
        for (int c = 0; c < count; c++)
        {
            var name = c < row.Count && !row[c].IsEmpty ? row[c].RawText.Trim() : "";
            if (name.Length == 0)
                name = $"Column {c + 1}";
            var unique = name;
            for (int n = 2; !used.Add(unique); n++)
                unique = $"{name} {n}";
            names.Add(unique);
        }
        return names;
    }

    private static JsonNode? Value(SheetCell cell) => cell.Value switch
    {
        null => null,
        string { Length: 0 } => null,
        string s => JsonValue.Create(s),
        double d when double.IsFinite(d) && d == Math.Floor(d) && Math.Abs(d) < 9e15 => JsonValue.Create((long)d),
        double d when double.IsFinite(d) => JsonValue.Create(d),
        bool b => JsonValue.Create(b),
        _ => JsonValue.Create(cell.RawText),
    };

    // ---------- records to tables ----------

    /// <summary>
    /// Records as a workbook: a list as one sheet; an object whose every value is a list as one sheet each; a single
    /// object as a table of one row.
    /// </summary>
    public static WorkbookModel ToWorkbook(JsonNode? node, string title)
    {
        var workbook = new WorkbookModel { Title = title };
        switch (node)
        {
            case JsonArray array:
                workbook.Sheets.Add(Sheet("Sheet1", array));
                break;
            case JsonObject obj when obj.Count > 0 && obj.All(p => p.Value is JsonArray):
                foreach (var (name, value) in obj)
                    workbook.Sheets.Add(Sheet(SheetName(name), (JsonArray)value!));
                break;
            case JsonObject obj:
                workbook.Sheets.Add(Sheet("Sheet1", [obj.DeepClone()]));
                break;
            default:
                throw new DataShapeException("A single value can't become a table: a table needs a list of records.");
        }
        return workbook;
    }

    /// <summary>Excel's limits on a sheet's name: 31 characters, none of : \ / ? * [ ].</summary>
    private static string SheetName(string name)
    {
        foreach (var c in @":\/?*[]")
            name = name.Replace(c, ' ');
        name = name.Trim();
        if (name.Length == 0) name = "Sheet";
        return name.Length > 31 ? name[..31] : name;
    }

    private static SheetModel Sheet(string name, JsonArray items)
    {
        var sheet = new SheetModel(name);
        if (items.Count > 0 && items.All(i => i is JsonArray))
        {
            // Rows as they are.
            foreach (var item in items)
                sheet.Rows.Add(((JsonArray)item!).Select(Cell).ToList());
            return sheet;
        }
        if (items.Any(i => i is JsonObject))
        {
            var columns = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in items.OfType<JsonObject>())
                foreach (var (key, _) in item)
                    if (seen.Add(key))
                        columns.Add(key);
            bool plain = items.Any(i => i is not JsonObject);
            if (plain)
                columns.Add("value");
            sheet.Rows.Add(columns.Select(c => new SheetCell(c)).ToList());
            foreach (var item in items)
            {
                if (item is JsonObject record)
                    sheet.Rows.Add(columns.Select(c => record.TryGetPropertyValue(c, out var v) ? Cell(v) : new SheetCell(null)).ToList());
                else
                    sheet.Rows.Add([.. columns.Take(columns.Count - 1).Select(_ => new SheetCell(null)), Cell(item)]);
            }
            return sheet;
        }
        // A list of single values: one column.
        sheet.Rows.Add([new SheetCell("value")]);
        foreach (var item in items)
            sheet.Rows.Add([Cell(item)]);
        return sheet;
    }

    private static SheetCell Cell(JsonNode? node) => node switch
    {
        null => new SheetCell(null),
        JsonValue v => v.GetValueKind() switch
        {
            JsonValueKind.String => new SheetCell(v.GetValue<string>()),
            JsonValueKind.True => new SheetCell(true),
            JsonValueKind.False => new SheetCell(false),
            // Numbers that a spreadsheet would change (long IDs, many decimals) stay as text.
            JsonValueKind.Number => double.TryParse(v.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
                                    && SheetCell.FromText(v.ToJsonString()).Value is double ? new SheetCell(d) : new SheetCell(v.ToJsonString()),
            _ => new SheetCell(null),
        },
        _ => new SheetCell(node.ToJsonString(Compact)),
    };
}
