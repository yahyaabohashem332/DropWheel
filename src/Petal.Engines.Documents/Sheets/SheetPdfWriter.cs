using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Tables;
using MigraDoc.Rendering;
using Petal.Engines.Documents.Pdf;
using Petal.Engines.Documents.Text;
using Petal.Engines.Documents.Util;
using MColor = MigraDoc.DocumentObjectModel.Color;

namespace Petal.Engines.Documents.Sheets;

/// <summary>
/// Prints a workbook as PDF tables, one sheet after another. Wide sheets switch to landscape
/// and a smaller font; sheets still too wide are split into column groups.
/// </summary>
internal static class SheetPdfWriter
{
    private const double BaseFont = 9;
    private const double MinFont = 6;
    private const double Padding = 3;

    public static void Write(WorkbookModel model, string path, IProgress<double>? progress, CancellationToken ct)
    {
        PdfFonts.EnsureInitialized();
        var meta = new Model.DocMetadata { Title = model.Title };
        var document = PdfDocWriter.NewDocument(meta, model.Title);
        var sheets = model.Sheets.Where(s => !s.IsEmpty).ToList();
        var showNames = sheets.Count > 1;
        for (var i = 0; i < sheets.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            AddSheet(document, sheets[i], showNames);
            progress?.Report(0.6 * (i + 1) / sheets.Count);
        }
        if (sheets.Count == 0)
            PdfDocWriter.AddSection(document).AddParagraph();

        var renderer = new PdfDocumentRenderer { Document = document };
        renderer.RenderDocument();
        renderer.PdfDocument.Save(path);
        progress?.Report(1);
    }

    private static void AddSheet(Document document, SheetModel sheet, bool showName)
    {
        var columns = sheet.ColumnCount;
        var natural = new double[columns];
        foreach (var row in sheet.Rows.Take(400))
            for (var c = 0; c < row.Count; c++)
            {
                if (row[c].IsEmpty) continue;
                var longest = row[c].DisplayText.Split('\n').Max(l => l.Length);
                natural[c] = Math.Max(natural[c], Math.Min(50, longest) * BaseFont * 0.52 + 2 * Padding);
            }
        for (var c = 0; c < columns; c++)
            natural[c] = Math.Max(natural[c], 28);

        var total = natural.Sum();
        var portrait = Paper.ContentWidthPt;
        var landscape = Paper.PageSizePt.Height - 2 * Paper.MarginPt;
        var useLandscape = total > portrait;
        var available = useLandscape ? landscape : portrait;
        var fontSize = BaseFont;
        if (total > available)
            fontSize = Math.Max(MinFont, BaseFont * available / total);
        var scale = fontSize / BaseFont;
        var widths = natural.Select(w => w * scale).ToArray();

        var section = PdfDocWriter.AddSection(document, useLandscape);
        if (showName)
        {
            var title = section.AddParagraph(sheet.Name);
            title.Style = "Heading2";
        }

        // Column groups that each fit the page width.
        var groups = new List<(int Start, int Count)>();
        var start = 0;
        while (start < columns)
        {
            double sum = 0;
            var count = 0;
            while (start + count < columns && (count == 0 || sum + widths[start + count] <= available))
            {
                sum += widths[start + count];
                count++;
            }
            groups.Add((start, count));
            start += count;
        }

        var header = sheet.Rows.Count > 1 && sheet.Rows[0].Count > 0 && sheet.Rows[0].Where(c => !c.IsEmpty).All(c => c.Value is string);
        for (var g = 0; g < groups.Count; g++)
        {
            var (first, count) = groups[g];
            if (groups.Count > 1)
            {
                var note = section.AddParagraph($"Columns {ColumnName(first)}–{ColumnName(first + count - 1)}");
                note.Format.Font.Size = 8;
                note.Format.Font.Color = MColor.FromRgb(0x70, 0x70, 0x70);
                note.Format.SpaceBefore = g == 0 ? 0 : 10;
            }
            var groupWidths = widths.Skip(first).Take(count).ToArray();
            // Stretch narrow tables a little so the page does not look empty, but never past the margins.
            var stretch = Math.Min(1.25, available / groupWidths.Sum());
            if (stretch > 1)
                groupWidths = groupWidths.Select(w => w * stretch).ToArray();
            AddTable(section, sheet, first, count, groupWidths, fontSize, header);
        }
    }

    private static void AddTable(Section section, SheetModel sheet, int first, int count, double[] widths, double fontSize, bool header)
    {
        var table = section.AddTable();
        table.Borders.Width = 0.4;
        table.Borders.Color = MColor.FromRgb(0xC8, 0xC8, 0xC8);
        table.TopPadding = 1.5;
        table.BottomPadding = 1.5;
        table.LeftPadding = Padding;
        table.RightPadding = Padding;
        table.Format.Font.Size = fontSize;
        table.Format.SpaceAfter = 0;
        foreach (var w in widths)
            table.AddColumn(Unit.FromPoint(w));

        for (var r = 0; r < sheet.Rows.Count; r++)
        {
            var source = sheet.Rows[r];
            var row = table.AddRow();
            if (r == 0 && header)
            {
                row.HeadingFormat = true;
                row.Format.Font.Bold = true;
                row.Shading.Color = MColor.FromRgb(0xF0, 0xF0, 0xF0);
            }
            for (var c = 0; c < count; c++)
            {
                var index = first + c;
                if (index >= source.Count || source[index].IsEmpty)
                    continue;
                var cell = source[index];
                var target = row.Cells[c];
                var text = DocxSanitize(cell.DisplayText);
                var p = target.AddParagraph();
                if (cell.IsNumeric)
                    p.Format.Alignment = ParagraphAlignment.Right;
                if (Bidi.ContainsRtl(text))
                {
                    var lines = PdfFonts.BreakRtl(text, PdfFonts.Arabic, fontSize, r == 0 && header, false, widths[c] - 2 * Padding);
                    if (Bidi.IsRtlParagraph(text))
                        p.Format.Alignment = ParagraphAlignment.Right;
                    for (var i = 0; i < lines.Count; i++)
                    {
                        if (i > 0) p.AddLineBreak();
                        p.AddFormattedText(lines[i]).Font.Name = PdfFonts.Arabic;
                    }
                    continue;
                }
                var parts = text.Split('\n');
                for (var i = 0; i < parts.Length; i++)
                {
                    if (i > 0) p.AddLineBreak();
                    foreach (var (segment, font) in PdfFonts.SplitByFont(parts[i]))
                    {
                        var ft = p.AddFormattedText(segment);
                        if (font is not null) ft.Font.Name = font;
                    }
                }
            }
        }
        var spacer = section.AddParagraph();
        spacer.Format.Font.Size = 4;
    }

    private static string DocxSanitize(string s) => IO.DocxWriter.Sanitize(s).Replace("\r", "");

    /// <summary>0 → A, 25 → Z, 26 → AA.</summary>
    internal static string ColumnName(int index)
    {
        var name = "";
        for (var n = index + 1; n > 0; n = (n - 1) / 26)
            name = (char)('A' + (n - 1) % 26) + name;
        return name;
    }
}
