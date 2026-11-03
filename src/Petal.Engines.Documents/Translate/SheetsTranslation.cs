using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using SheetText = DocumentFormat.OpenXml.Spreadsheet.Text;
using TextTranslator = System.Func<System.Collections.Generic.IReadOnlyList<string>, System.IProgress<double>?, System.Threading.CancellationToken, System.Threading.Tasks.Task<System.Collections.Generic.IReadOnlyList<string>>>;

namespace Petal.Engines.Documents.Translate;

/// <summary>
/// An Excel workbook in another language, changed in place on its copy: the words in its cells only, kept in its shared
/// and inline strings, a rich one collapsing into its first run. Formulas, numbers and dates are not touched, nor are
/// sheet names, which formulas refer to, nor the headings of Excel tables, which must match the table's own column names and
/// which formulas refer to too.
/// </summary>
internal static class SheetsTranslation
{
    public static async Task RunAsync(string path, string from, string to, TextTranslator translate, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        using var document = SpreadsheetDocument.Open(path, true);
        var workbook = document.WorkbookPart ?? throw new InvalidDataException("The workbook has no sheets.");
        var sheets = workbook.WorksheetParts.ToList();
        var (sharedKept, inlineKept) = TableHeadings(sheets);
        var slots = new List<TextSlot>();
        if (workbook.SharedStringTablePart?.SharedStringTable is { } table)
        {
            int index = 0;
            foreach (var item in table.Elements<SharedStringItem>())
            {
                if (!sharedKept.Contains(index++))
                    Add(item, slots);
            }
        }
        foreach (var sheet in sheets)
            foreach (var cell in sheet.Worksheet?.Descendants<Cell>() ?? [])
                if (cell.DataType?.Value == CellValues.InlineString && cell.InlineString is { } inline && !inlineKept.Contains(cell))
                    Add(inline, slots);
        await DocumentTranslator.FillAsync(slots, translate, progress, cancellationToken).ConfigureAwait(false);
        var known = DocumentTranslator.Known(from, slots);
        bool fromRtl = DocumentTranslator.IsRightToLeft(known), toRtl = DocumentTranslator.IsRightToLeft(to);
        if (fromRtl != toRtl)
        {
            foreach (var sheet in sheets)
                foreach (var view in sheet.Worksheet?.Descendants<SheetView>() ?? [])
                    view.RightToLeft = toRtl ? true : null;
        }
        document.Save();
    }

    /// <summary>A string with its runs' words as one, or its own text; the translation replaces them all in the first.</summary>
    private static void Add(OpenXmlCompositeElement item, List<TextSlot> slots)
    {
        var runs = item.Elements<Run>().ToList();
        var words = runs.Count > 0 ? string.Concat(runs.Select(r => r.Text?.Text ?? "")) : item.GetFirstChild<SheetText>()?.Text ?? "";
        slots.Add(new TextSlot(words, translated =>
        {
            var text = runs.Count > 0 ? runs[0].Text ??= new SheetText() : item.GetFirstChild<SheetText>() ?? item.PrependChild(new SheetText());
            text.Text = translated;
            text.Space = SpaceProcessingModeValues.Preserve;
            foreach (var run in runs.Skip(1))
                run.Remove();
        }));
    }

    /// <summary>The cells heading a table's columns: Excel repairs a file whose headings differ from the names its tables hold.</summary>
    private static (HashSet<int> Shared, HashSet<Cell> Inline) TableHeadings(List<WorksheetPart> sheets)
    {
        var shared = new HashSet<int>();
        var inline = new HashSet<Cell>();
        foreach (var sheet in sheets)
        {
            foreach (var tablePart in sheet.TableDefinitionParts)
            {
                if (tablePart.Table is not { } table || table.HeaderRowCount?.Value == 0 || table.Reference?.Value is not { } reference)
                    continue;
                var (first, last, row) = HeadingCells(reference);
                foreach (var cell in sheet.Worksheet?.Descendants<Row>().FirstOrDefault(r => r.RowIndex?.Value == row)?.Elements<Cell>() ?? [])
                {
                    if (cell.CellReference?.Value is not { } at || Column(at) < first || Column(at) > last)
                        continue;
                    if (cell.DataType?.Value == CellValues.InlineString)
                        inline.Add(cell);
                    else if (cell.DataType?.Value == CellValues.SharedString && int.TryParse(cell.CellValue?.Text, out int index))
                        shared.Add(index);
                }
            }
        }
        return (shared, inline);
    }

    private static (int First, int Last, uint Row) HeadingCells(string reference)
    {
        var corners = reference.Split(':');
        var digits = new string(corners[0].Where(char.IsAsciiDigit).ToArray());
        return (Column(corners[0]), Column(corners[^1]), uint.TryParse(digits, out uint row) ? row : 1);
    }

    /// <summary>"C7" is column 3.</summary>
    private static int Column(string reference)
    {
        int column = 0;
        foreach (char c in reference)
        {
            if (!char.IsAsciiLetter(c))
                break;
            column = column * 26 + char.ToUpperInvariant(c) - 'A' + 1;
        }
        return column;
    }
}
