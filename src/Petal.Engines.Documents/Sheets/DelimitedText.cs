using System.Text;

namespace Petal.Engines.Documents.Sheets;

/// <summary>CSV and TSV per RFC 4180: quoted fields, doubled quotes, embedded delimiters and newlines, any encoding in, UTF-8 with BOM out.</summary>
internal static class DelimitedText
{
    public static WorkbookModel Read(string path, char delimiter)
    {
        var text = Text.TextFile.ReadAllText(path);
        if (delimiter == ',' && PrefersSemicolon(text))
            delimiter = ';';
        var sheet = new SheetModel(Petal.Core.Formats.Formats.BaseName(path));
        foreach (var record in Parse(text, delimiter))
            sheet.Rows.Add(record.Select(SheetCell.FromText).ToList());
        sheet.Trim();
        var wb = new WorkbookModel();
        wb.Sheets.Add(sheet);
        return wb;
    }

    /// <summary>European Excel writes ";"-separated "CSV"; detect it from the first line.</summary>
    private static bool PrefersSemicolon(string text)
    {
        int commas = 0, semicolons = 0;
        var inQuotes = false;
        foreach (var c in text)
        {
            if (c == '"') inQuotes = !inQuotes;
            else if (!inQuotes && c is '\n' or '\r') break;
            else if (!inQuotes && c == ',') commas++;
            else if (!inQuotes && c == ';') semicolons++;
        }
        return semicolons > commas;
    }

    public static IEnumerable<List<string>> Parse(string text, char delimiter)
    {
        var field = new StringBuilder();
        var record = new List<string>();
        var inQuotes = false;
        var fieldStarted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        field.Append('"');
                        i++;
                    }
                    else
                        inQuotes = false;
                }
                else if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                {
                    // Line breaks inside a cell are LF, as in Excel.
                    field.Append('\n');
                    i++;
                }
                else
                    field.Append(c);
                continue;
            }
            if (c == '"' && field.Length == 0 && !fieldStarted)
            {
                inQuotes = true;
                fieldStarted = true;
            }
            else if (c == delimiter)
            {
                record.Add(field.ToString());
                field.Clear();
                fieldStarted = false;
            }
            else if (c is '\r' or '\n')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                    i++;
                record.Add(field.ToString());
                field.Clear();
                fieldStarted = false;
                yield return record;
                record = [];
            }
            else
            {
                field.Append(c);
                fieldStarted = true;
            }
        }
        if (field.Length > 0 || fieldStarted || record.Count > 0)
        {
            record.Add(field.ToString());
            yield return record;
        }
    }

    public static void Write(SheetModel sheet, string path, char delimiter)
    {
        var sb = new StringBuilder();
        var columns = sheet.ColumnCount;
        foreach (var row in sheet.Rows)
        {
            for (var c = 0; c < columns; c++)
            {
                if (c > 0) sb.Append(delimiter);
                var value = c < row.Count ? row[c].RawText : "";
                sb.Append(Quote(value, delimiter));
            }
            sb.Append("\r\n");
        }
        File.WriteAllText(path, sb.ToString(), Text.TextFile.Utf8Bom);
    }

    public static string Quote(string value, char delimiter)
    {
        var needs = value.Length > 0 && (value.IndexOfAny([delimiter, '"', '\r', '\n']) >= 0 || char.IsWhiteSpace(value[0]) || char.IsWhiteSpace(value[^1]));
        return needs ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
    }
}
