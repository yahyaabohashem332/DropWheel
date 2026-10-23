using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using Petal.Engines.Documents.Pdf;
using SkiaSharp;

namespace Petal.Engines.Documents.Ocr;

/// <summary>A table read from a picture: its rows of cells, first column first (the rightmost one in a right-to-left table).</summary>
public sealed record PictureTable(IReadOnlyList<IReadOnlyList<string>> Rows, bool RightToLeft)
{
    public int Columns => Rows.Count == 0 ? 0 : Rows.Max(r => r.Count);

    public bool IsEmpty => Rows.All(r => r.All(string.IsNullOrWhiteSpace));
}

/// <summary>
/// Tables in pictures (a screenshot of a report, a photo of a price list) read into rows and columns with the
/// text reading built into Windows, the way PDF → Excel reads a page: words close together make a cell, cells
/// that line up make a column. Saved as an Excel sheet with real numbers, or copied as cells that paste into
/// any spreadsheet.
/// </summary>
public static partial class PictureTables
{
    /// <summary>Cells read a second time at most, one by one, when the first reading left them empty.</summary>
    private const int MostRereads = 60;

    /// <summary>A table and where its rows and columns are in the picture that was read.</summary>
    private sealed record Layout(PictureTable Table, List<(double Top, double Bottom)> Rows, List<(double Left, double Right)> Columns);

    /// <summary>Reads the table in a picture of BGRA pixels (straight alpha; see-through parts read as white).</summary>
    public static async Task<PictureTable> ReadAsync(byte[] bgra, int width, int height, CancellationToken cancellationToken = default)
    {
        using var raw = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Unpremul));
        Marshal.Copy(bgra, 0, raw.GetPixels(), Math.Min(bgra.Length, width * height * 4));
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.White);
            canvas.DrawBitmap(raw, 0, 0);
        }
        return await ReadAsync(bitmap, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads the table in a picture file (PNG, JPEG, WebP, BMP, GIF).</summary>
    public static async Task<PictureTable> ReadFileAsync(string path, CancellationToken cancellationToken = default)
    {
        using var raw = SKBitmap.Decode(path) ?? throw new InvalidOperationException("This picture can't be opened.");
        using var bitmap = new SKBitmap(new SKImageInfo(raw.Width, raw.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.White);
            canvas.DrawBitmap(raw, 0, 0);
        }
        return await ReadAsync(bitmap, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<PictureTable> ReadAsync(SKBitmap bitmap, CancellationToken cancellationToken)
    {
        if (!OcrService.IsAvailable)
            throw new InvalidOperationException("Windows has no language for reading text. Add one under Settings › Time & language › Language.");
        int width = bitmap.Width, height = bitmap.Height, max = OcrService.MaxImageDimension;
        // Small text (a screenshot) reads better larger; a huge photo must fit the reader.
        double scale = Math.Max(width, height) * 2 <= max && height < 1200 ? 2 : Math.Max(width, height) > max ? (double)max / Math.Max(width, height) : 1;
        SKBitmap? resized = scale == 1 ? null
            : bitmap.Resize(new SKImageInfo((int)(width * scale), (int)(height * scale), SKColorType.Bgra8888, SKAlphaType.Premul), new SKSamplingOptions(SKCubicResampler.Mitchell));
        try
        {
            var read = resized ?? bitmap;
            var words = await OcrService.RecognizeAsync(read, null, cancellationToken).ConfigureAwait(false);
            var layout = Arrange(words);
            return await RereadGapsAsync(layout, read, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            resized?.Dispose();
        }
    }

    /// <summary>Rows and columns from recognised words and where they are.</summary>
    internal static PictureTable FromWords(IReadOnlyList<OcrWordBox> words) => Arrange(words).Table;

    private static Layout Arrange(IReadOnlyList<OcrWordBox> words)
    {
        if (words.Count == 0)
            return new Layout(new PictureTable([], false), [], []);
        var page = new PdfPageContent { Width = words.Max(w => w.X + w.Width), Height = words.Max(w => w.Y + w.Height) };
        // Like a scanned page: a line's size from its tallest word, its words on one band.
        foreach (var line in words.GroupBy(w => w.Line))
        {
            var size = line.Max(w => w.Height) / 0.9;
            var centre = (line.Min(w => w.Y) + line.Max(w => w.Y + w.Height)) / 2;
            foreach (var w in line)
                page.Words.Add(new PdfWord(Tidy(w.Text), w.X, w.X + w.Width, centre - size / 2, centre + size / 2, size, false, false, null));
        }
        bool rtl = page.Words.Count(w => w.IsRtl) * 2 > page.Words.Count;
        var rows = PdfLayout.BuildRows(page);
        var columns = PdfToXlsx.Columns(rows);
        int count = Math.Max(1, columns.Count);
        var table = new List<IReadOnlyList<string>>();
        foreach (var row in rows)
        {
            var cells = new string[count];
            foreach (var group in row.Segments.GroupBy(s => PdfToXlsx.ColumnFor(columns, s)))
            {
                var parts = rtl ? group.OrderByDescending(s => s.Right) : group.OrderBy(s => s.Left);
                int column = rtl ? count - 1 - group.Key : group.Key;
                cells[column] = string.Join(" ", parts.Select(s => s.Text)).Trim();
            }
            table.Add(cells.Select(c => c ?? "").ToList());
        }
        // Empty columns at the end say nothing.
        int used = Math.Max(1, table.Max(r => Array.FindLastIndex(r.ToArray(), c => c.Length > 0)) + 1);
        // The columns' bands in the table's order (right to left ones mirrored), as wide as the gaps between them allow.
        var bands = columns.Count == 0 ? [(page.Words.Min(w => w.Left), page.Words.Max(w => w.Right))] : columns.ToList();
        if (rtl)
            bands.Reverse();
        return new Layout(
            new PictureTable(table.Select(r => (IReadOnlyList<string>)r.Take(used).ToList()).ToList(), rtl),
            rows.Select(r => (r.Segments.Min(s => s.Top), r.Segments.Max(s => s.Bottom))).ToList(),
            bands.Take(used).ToList());
    }

    /// <summary>
    /// Windows' reading skips short lone words now and then (a "41%" at the end of a row). A cell left empty in a
    /// column that is mostly filled is read again on its own, larger, in every reading language, and the
    /// answer most of them agree on is kept.
    /// </summary>
    private static async Task<PictureTable> RereadGapsAsync(Layout layout, SKBitmap picture, CancellationToken cancellationToken)
    {
        var rows = layout.Table.Rows;
        if (rows.Count < 2 || layout.Columns.Count == 0)
            return layout.Table;
        var languages = OcrService.InstalledLanguages;
        var filled = rows.Select(r => r.ToArray()).ToList();
        int reread = 0;
        bool rtl = layout.Table.RightToLeft;
        // Bands sorted left to right, to find each one's neighbours on the picture.
        var leftToRight = layout.Columns.OrderBy(c => c.Left).ToList();
        for (int c = 0; c < layout.Columns.Count; c++)
        {
            int full = rows.Count(r => c < r.Count && r[c].Length > 0);
            if (full < 2 || full * 2 < rows.Count)
                continue;
            var band = layout.Columns[c];
            int at = leftToRight.IndexOf(band);
            double left = at > 0 ? (leftToRight[at - 1].Right + band.Left) / 2 : Math.Max(0, band.Left - (band.Right - band.Left) / 2);
            double right = at < leftToRight.Count - 1 ? (band.Right + leftToRight[at + 1].Left) / 2 : Math.Min(picture.Width, band.Right + (band.Right - band.Left) / 2 + 20);
            for (int r = 0; r < rows.Count && reread < MostRereads; r++)
            {
                if (c < rows[r].Count && rows[r][c].Length > 0)
                    continue;
                var (top, bottom) = layout.Rows[r];
                double margin = (bottom - top) * 0.35;
                var area = SKRectI.Round(new SKRect((float)left, (float)Math.Max(0, top - margin), (float)right, (float)Math.Min(picture.Height, bottom + margin)));
                if (area.Width < 4 || area.Height < 4)
                    continue;
                reread++;
                if (await ReadCellAsync(picture, area, languages, cancellationToken).ConfigureAwait(false) is { Length: > 0 } text)
                {
                    while (filled[r].Length <= c)
                        filled[r] = [.. filled[r], ""];
                    filled[r][c] = text;
                }
            }
        }
        return reread == 0 ? layout.Table : new PictureTable(filled.Select(r => (IReadOnlyList<string>)r.ToList()).ToList(), rtl);
    }

    /// <summary>One cell, alone on white, three times larger, read in every language; the reading most agree on.</summary>
    private static async Task<string?> ReadCellAsync(SKBitmap picture, SKRectI area, IReadOnlyList<string> languages, CancellationToken cancellationToken)
    {
        const int Pad = 30;
        double scale = Math.Min(3, (OcrService.MaxImageDimension - 2.0 * Pad) / Math.Max(area.Width, area.Height));
        int w = (int)(area.Width * scale) + 2 * Pad, h = (int)(area.Height * scale) + 2 * Pad;
        using var cell = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(cell))
        {
            canvas.Clear(SKColors.White);
            using var image = SKImage.FromBitmap(picture);
            canvas.DrawImage(image, SKRect.Create(area.Left, area.Top, area.Width, area.Height), SKRect.Create(Pad, Pad, w - 2 * Pad, h - 2 * Pad), new SKSamplingOptions(SKCubicResampler.Mitchell));
        }
        var answers = new List<string>();
        foreach (var language in languages.Count > 0 ? languages : [null!])
        {
            var words = await OcrService.RecognizeAsync(cell, language is null ? null : [language], cancellationToken).ConfigureAwait(false);
            var text = Tidy(string.Join(" ", words.OrderBy(x => x.Line).ThenBy(x => x.X).Select(x => x.Text)).Trim());
            if (text.Length > 0)
                answers.Add(text);
        }
        // The most common answer; between equals, one that reads as a number.
        return answers.GroupBy(a => a)
            .OrderByDescending(g => g.Count())
            .ThenByDescending(g => PdfToXlsx.ParseNumber(WesternDigits(g.Key)) is not null)
            .Select(g => g.Key)
            .FirstOrDefault();
    }

    /// <summary>
    /// Common misreadings of numbers mended: a percent sign read as "0/0" ("330/0" is 33%) or put first by a
    /// right-to-left reader ("%33").
    /// </summary>
    internal static string Tidy(string word)
    {
        var m = PercentAsZeros().Match(word);
        if (m.Success)
            return m.Groups[1].Value + "%";
        m = PercentFirst().Match(word);
        return m.Success ? m.Groups[1].Value + "%" : word;
    }

    [GeneratedRegex(@"^(\d[\d.,]*)0/0$")]
    private static partial Regex PercentAsZeros();

    [GeneratedRegex(@"^%(\d[\d.,]*)$")]
    private static partial Regex PercentFirst();

    /// <summary>The table as an Excel sheet, numbers as numbers, right to left when the table is.</summary>
    public static void SaveXlsx(PictureTable table, string output, string sheetName = "Table")
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.AddWorksheet(sheetName);
        sheet.RightToLeft = table.RightToLeft;
        for (int r = 0; r < table.Rows.Count; r++)
        {
            for (int c = 0; c < table.Rows[r].Count; c++)
            {
                var text = table.Rows[r][c];
                if (text.Length == 0)
                    continue;
                var cell = sheet.Cell(r + 1, c + 1);
                if (PdfToXlsx.ParseNumber(WesternDigits(text)) is { } number)
                {
                    cell.Value = number.Value;
                    if (number.Format is not null)
                        cell.Style.NumberFormat.Format = number.Format;
                }
                else
                    cell.Value = text;
            }
        }
        if (table.Rows.Count > 0)
        {
            // The first row is usually the headings.
            sheet.Row(1).Style.Font.Bold = true;
            try
            {
                sheet.Columns().AdjustToContents(1, Math.Min(table.Rows.Count, 500), 4, 80);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Measuring needs fonts; column widths are cosmetic.
            }
        }
        using var stream = new FileStream(output, FileMode.Create, FileAccess.ReadWrite);
        workbook.SaveAs(stream);
    }

    /// <summary>Cells separated by tabs and rows by lines: pasted into Excel or Google Sheets, each lands in its own cell.</summary>
    public static string ToTabs(PictureTable table)
    {
        var sb = new StringBuilder();
        foreach (var row in table.Rows)
            sb.Append(string.Join('\t', row.Select(c => c.Replace('\t', ' ')))).Append("\r\n");
        return sb.ToString();
    }

    /// <summary>Arabic and Persian digits as 0–9, so "١٢٣" counts as a number.</summary>
    internal static string WesternDigits(string text)
    {
        var chars = text.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            int c = chars[i];
            if (c is >= 0x0660 and <= 0x0669)
                chars[i] = (char)('0' + c - 0x0660);
            else if (c is >= 0x06F0 and <= 0x06F9)
                chars[i] = (char)('0' + c - 0x06F0);
            else if (c == 0x066B)
                chars[i] = '.';
            else if (c == 0x066C)
                chars[i] = ',';
        }
        return new string(chars);
    }
}
