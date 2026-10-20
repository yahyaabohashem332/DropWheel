using System.Globalization;
using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;
using Petal.Engines.Documents.IO;
using static Petal.Engines.Documents.IO.Odf;

namespace Petal.Engines.Documents.Sheets;

/// <summary>OpenDocument Spreadsheet reading and writing.</summary>
internal static class OdsSheets
{
    /// <summary>Repeated empty rows and columns (LibreOffice writes a million of them) are only kept when followed by data.</summary>
    private const int MaxRepeat = 10_000;

    public static WorkbookModel Read(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var content = LoadPart(zip, "content.xml") ?? throw new InvalidDataException("The spreadsheet has no content.xml.");
        var model = new WorkbookModel();
        var spreadsheet = content.Root?.Element(OfficeNs + "body")?.Element(OfficeNs + "spreadsheet");
        if (spreadsheet is null)
            return model;
        foreach (var table in spreadsheet.Elements(TableNs + "table"))
        {
            var sheet = new SheetModel((string?)table.Attribute(TableNs + "name") ?? $"Sheet{model.Sheets.Count + 1}");
            var pendingEmptyRows = 0;
            foreach (var row in Rows(table))
            {
                var repeat = Math.Max(1, (int?)row.Attribute(TableNs + "number-rows-repeated") ?? 1);
                var cells = ReadRow(row);
                if (cells.All(c => c.IsEmpty))
                {
                    pendingEmptyRows += repeat;
                    continue;
                }
                for (var i = 0; i < Math.Min(pendingEmptyRows, MaxRepeat); i++)
                    sheet.Rows.Add([]);
                pendingEmptyRows = 0;
                for (var i = 0; i < Math.Min(repeat, MaxRepeat); i++)
                    sheet.Rows.Add([.. cells]);
            }
            sheet.Trim();
            model.Sheets.Add(sheet);
        }
        return model;
    }

    private static IEnumerable<XElement> Rows(XElement container)
    {
        foreach (var e in container.Elements())
        {
            if (e.Name == TableNs + "table-row")
                yield return e;
            else if (e.Name == TableNs + "table-header-rows" || e.Name == TableNs + "table-row-group" || e.Name == TableNs + "table-rows")
                foreach (var nested in Rows(e))
                    yield return nested;
        }
    }

    private static List<SheetCell> ReadRow(XElement row)
    {
        var cells = new List<SheetCell>();
        var pendingEmpty = 0;
        foreach (var cell in row.Elements().Where(e => e.Name == TableNs + "table-cell" || e.Name == TableNs + "covered-table-cell"))
        {
            var repeat = Math.Max(1, (int?)cell.Attribute(TableNs + "number-columns-repeated") ?? 1);
            var value = ReadCell(cell);
            if (value.IsEmpty)
            {
                pendingEmpty += repeat;
                continue;
            }
            for (var i = 0; i < Math.Min(pendingEmpty, MaxRepeat); i++)
                cells.Add(default);
            pendingEmpty = 0;
            for (var i = 0; i < Math.Min(repeat, 1024); i++)
                cells.Add(value);
        }
        return cells;
    }

    private static SheetCell ReadCell(XElement cell)
    {
        var paragraphs = cell.Elements(TextNs + "p").Select(PlainText).ToList();
        var display = paragraphs.Count > 0 ? string.Join("\n", paragraphs) : null;
        var type = (string?)cell.Attribute(OfficeNs + "value-type");
        var inv = CultureInfo.InvariantCulture;
        switch (type)
        {
            case "float" or "percentage" or "currency":
                if (double.TryParse((string?)cell.Attribute(OfficeNs + "value"), NumberStyles.Float, inv, out var d))
                    return new SheetCell(d, display);
                break;
            case "date":
                var dateText = (string?)cell.Attribute(OfficeNs + "date-value");
                if (dateText is not null && DateTime.TryParse(dateText, inv, DateTimeStyles.AllowWhiteSpaces, out var dt))
                    return new SheetCell(dt, display);
                break;
            case "time":
                var timeText = (string?)cell.Attribute(OfficeNs + "time-value");
                if (timeText is not null)
                {
                    try { return new SheetCell(XmlConvert.ToTimeSpan(timeText), display); }
                    catch (FormatException) { }
                }
                break;
            case "boolean":
                var boolText = (string?)cell.Attribute(OfficeNs + "boolean-value");
                if (boolText is not null)
                    return new SheetCell(boolText is "true" or "1", display);
                break;
        }
        var text = (string?)cell.Attribute(OfficeNs + "string-value") ?? display;
        return string.IsNullOrEmpty(text) ? default : new SheetCell(text, null);
    }

    public static void Write(WorkbookModel model, string path)
    {
        var inv = CultureInfo.InvariantCulture;
        var automatic = new XElement(OfficeNs + "automatic-styles",
            new XElement(NumberNs + "date-style", new XAttribute(StyleNs + "name", "N1"),
                new XElement(NumberNs + "year", new XAttribute(NumberNs + "style", "long")), new XElement(NumberNs + "text", "-"),
                new XElement(NumberNs + "month", new XAttribute(NumberNs + "style", "long")), new XElement(NumberNs + "text", "-"),
                new XElement(NumberNs + "day", new XAttribute(NumberNs + "style", "long"))),
            new XElement(NumberNs + "date-style", new XAttribute(StyleNs + "name", "N2"),
                new XElement(NumberNs + "year", new XAttribute(NumberNs + "style", "long")), new XElement(NumberNs + "text", "-"),
                new XElement(NumberNs + "month", new XAttribute(NumberNs + "style", "long")), new XElement(NumberNs + "text", "-"),
                new XElement(NumberNs + "day", new XAttribute(NumberNs + "style", "long")), new XElement(NumberNs + "text", " "),
                new XElement(NumberNs + "hours", new XAttribute(NumberNs + "style", "long")), new XElement(NumberNs + "text", ":"),
                new XElement(NumberNs + "minutes", new XAttribute(NumberNs + "style", "long")), new XElement(NumberNs + "text", ":"),
                new XElement(NumberNs + "seconds", new XAttribute(NumberNs + "style", "long"))),
            new XElement(NumberNs + "time-style", new XAttribute(StyleNs + "name", "N3"),
                new XElement(NumberNs + "hours", new XAttribute(NumberNs + "style", "long")), new XElement(NumberNs + "text", ":"),
                new XElement(NumberNs + "minutes", new XAttribute(NumberNs + "style", "long")), new XElement(NumberNs + "text", ":"),
                new XElement(NumberNs + "seconds", new XAttribute(NumberNs + "style", "long"))),
            new XElement(StyleNs + "style", new XAttribute(StyleNs + "name", "ceDate"), new XAttribute(StyleNs + "family", "table-cell"), new XAttribute(StyleNs + "data-style-name", "N1")),
            new XElement(StyleNs + "style", new XAttribute(StyleNs + "name", "ceDateTime"), new XAttribute(StyleNs + "family", "table-cell"), new XAttribute(StyleNs + "data-style-name", "N2")),
            new XElement(StyleNs + "style", new XAttribute(StyleNs + "name", "ceTime"), new XAttribute(StyleNs + "family", "table-cell"), new XAttribute(StyleNs + "data-style-name", "N3")),
            new XElement(StyleNs + "style", new XAttribute(StyleNs + "name", "ceWrap"), new XAttribute(StyleNs + "family", "table-cell"),
                new XElement(StyleNs + "table-cell-properties", new XAttribute(FoNs + "wrap-option", "wrap"))));

        var spreadsheet = new XElement(OfficeNs + "spreadsheet");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var columnStyles = new Dictionary<string, XElement>();
        foreach (var sheet in model.Sheets.Count > 0 ? model.Sheets : [new SheetModel("Sheet1")])
        {
            var table = new XElement(TableNs + "table", new XAttribute(TableNs + "name", ExcelSheets.SheetName(sheet.Name, names)));
            var columns = Math.Max(1, sheet.ColumnCount);
            for (var c = 0; c < columns; c++)
            {
                // Width from the source if known, else from the longest text in the first rows.
                var chars = sheet.ColumnWidths.TryGetValue(c, out var w) && w > 0 ? w
                    : Math.Clamp(sheet.Rows.Take(500).Select(r => c < r.Count ? r[c].DisplayText.Split('\n').Max(l => l.Length) : 0).DefaultIfEmpty(0).Max() + 2, 6, 60);
                var cm = Cm(chars * 5.6);
                var styleName = "co_" + cm.Replace('.', '_');
                if (!columnStyles.ContainsKey(styleName))
                    columnStyles[styleName] = new XElement(StyleNs + "style", new XAttribute(StyleNs + "name", styleName), new XAttribute(StyleNs + "family", "table-column"),
                        new XElement(StyleNs + "table-column-properties", new XAttribute(StyleNs + "column-width", cm)));
                table.Add(new XElement(TableNs + "table-column", new XAttribute(TableNs + "style-name", styleName)));
            }
            foreach (var row in sheet.Rows)
            {
                var tr = new XElement(TableNs + "table-row");
                for (var c = 0; c < row.Count; c++)
                    tr.Add(CellElement(row[c], inv));
                if (row.Count == 0)
                    tr.Add(new XElement(TableNs + "table-cell"));
                table.Add(tr);
            }
            if (sheet.Rows.Count == 0)
                table.Add(new XElement(TableNs + "table-row", new XElement(TableNs + "table-cell")));
            spreadsheet.Add(table);
        }
        automatic.Add(columnStyles.Values);

        var content = new XDocument(new XElement(OfficeNs + "document-content", Declarations(), automatic, new XElement(OfficeNs + "body", spreadsheet)));
        var styles = new XDocument(new XElement(OfficeNs + "document-styles", Declarations(),
            new XElement(OfficeNs + "styles",
                new XElement(StyleNs + "default-style", new XAttribute(StyleNs + "family", "table-cell"),
                    new XElement(StyleNs + "text-properties", new XAttribute(StyleNs + "font-name", "Calibri"), new XAttribute(FoNs + "font-size", "11pt"))))));
        var doc = new Model.DocModel();
        doc.Metadata.Title = model.Title;
        WritePackage(path, "application/vnd.oasis.opendocument.spreadsheet",
        [
            ("content.xml", Serialize(content), "text/xml"),
            ("styles.xml", Serialize(styles), "text/xml"),
            ("meta.xml", Serialize(OdtWriter.MetaDocument(doc)), "text/xml"),
        ]);
    }

    private static XElement CellElement(SheetCell cell, CultureInfo inv)
    {
        var e = new XElement(TableNs + "table-cell");
        if (cell.IsEmpty)
            return e;
        var display = cell.Display ?? cell.RawText;
        switch (cell.Value)
        {
            case double d when double.IsFinite(d):
                e.Add(new XAttribute(OfficeNs + "value-type", "float"), new XAttribute(OfficeNs + "value", d.ToString("R", inv)));
                break;
            case bool b:
                e.Add(new XAttribute(OfficeNs + "value-type", "boolean"), new XAttribute(OfficeNs + "boolean-value", b ? "true" : "false"));
                break;
            case DateTime dt:
                e.Add(new XAttribute(OfficeNs + "value-type", "date"),
                    new XAttribute(OfficeNs + "date-value", dt.ToString("yyyy-MM-ddTHH:mm:ss", inv)),
                    new XAttribute(TableNs + "style-name", dt.TimeOfDay == TimeSpan.Zero ? "ceDate" : "ceDateTime"));
                break;
            case TimeSpan ts:
                e.Add(new XAttribute(OfficeNs + "value-type", "time"), new XAttribute(OfficeNs + "time-value", XmlConvert.ToString(ts)),
                    new XAttribute(TableNs + "style-name", "ceTime"));
                break;
            default:
                e.Add(new XAttribute(OfficeNs + "value-type", "string"));
                if (display.Contains('\n'))
                    e.Add(new XAttribute(TableNs + "style-name", "ceWrap"));
                break;
        }
        foreach (var line in display.Split('\n'))
        {
            var p = new XElement(TextNs + "p");
            AppendEncodedText(p, IO.DocxWriter.Sanitize(line));
            e.Add(p);
        }
        return e;
    }
}
