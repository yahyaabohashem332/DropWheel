using ClosedXML.Excel;
using Petal.Engines.Documents.Ocr;
using SkiaSharp;

namespace Petal.Engines.Documents.Tests;

/// <summary>Tables in pictures: words placed into rows and columns, saved with real numbers, and read from a drawn table.</summary>
public sealed class PictureTablesTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("petal-table-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    /// <summary>A word at a column's left edge, on a row 30 pixels tall.</summary>
    private static OcrWordBox Word(string text, double x, int row, int line) => new(text, x, 20 + row * 30, text.Length * 9, 16, line);

    [Fact]
    public void Words_fall_into_rows_and_columns()
    {
        var words = new List<OcrWordBox>();
        string[][] cells = [["Item", "Price", "Count"], ["Green", "tea", "4.50", "12"], ["Coffee", "1,200.00", "3"]];
        int line = 0;
        // Two-word cells stay together; columns start at 20, 220 and 360.
        words.Add(Word("Item", 20, 0, line));
        words.Add(Word("Price", 220, 0, line));
        words.Add(Word("Count", 360, 0, line++));
        words.Add(Word("Green", 20, 1, line));
        words.Add(Word("tea", 20 + 5 * 9 + 6, 1, line));
        words.Add(Word("4.50", 220, 1, line));
        words.Add(Word("12", 360, 1, line++));
        words.Add(Word("Coffee", 20, 2, line));
        words.Add(Word("1,200.00", 220, 2, line));
        words.Add(Word("3", 360, 2, line));
        var table = PictureTables.FromWords(words);
        Assert.False(table.RightToLeft);
        Assert.Equal(3, table.Columns);
        Assert.Equal(["Item", "Price", "Count"], table.Rows[0]);
        Assert.Equal(["Green tea", "4.50", "12"], table.Rows[1]);
        Assert.Equal(["Coffee", "1,200.00", "3"], table.Rows[2]);
        Assert.Equal("Item\tPrice\tCount\r\nGreen tea\t4.50\t12\r\nCoffee\t1,200.00\t3\r\n", PictureTables.ToTabs(table));

        var xlsx = Path.Combine(_dir, "table.xlsx");
        PictureTables.SaveXlsx(table, xlsx);
        using var workbook = new XLWorkbook(xlsx);
        var sheet = workbook.Worksheet(1);
        Assert.Equal(1200, sheet.Cell(3, 2).GetValue<double>());
        Assert.Equal(12, sheet.Cell(2, 3).GetValue<double>());
        Assert.Equal("Green tea", sheet.Cell(2, 1).GetString());
        Assert.True(sheet.Cell(1, 1).Style.Font.Bold);
    }

    [Fact]
    public void A_right_to_left_table_starts_from_the_right()
    {
        // Arabic: the first column is on the right.
        var words = new List<OcrWordBox>
        {
            Word("السعر", 40, 0, 0), Word("الصنف", 300, 0, 0),
            Word("٤٥", 40, 1, 1), Word("شاي", 300, 1, 1),
        };
        var table = PictureTables.FromWords(words);
        Assert.True(table.RightToLeft);
        Assert.Equal(["الصنف", "السعر"], table.Rows[0]);
        Assert.Equal(["شاي", "٤٥"], table.Rows[1]);
        var xlsx = Path.Combine(_dir, "arabic.xlsx");
        PictureTables.SaveXlsx(table, xlsx);
        using var workbook = new XLWorkbook(xlsx);
        Assert.True(workbook.Worksheet(1).RightToLeft);
        Assert.Equal(45, workbook.Worksheet(1).Cell(2, 2).GetValue<double>());
        Assert.Equal("123.5", PictureTables.WesternDigits("١٢٣٫٥"));
        // A percent sign misread as "0/0" or put first.
        Assert.Equal("33%", PictureTables.Tidy("330/0"));
        Assert.Equal("26%", PictureTables.Tidy("%26"));
        Assert.Equal("10/05", PictureTables.Tidy("10/05"));
    }

    [Fact]
    public async Task A_drawn_table_is_read_back()
    {
        if (!OcrService.IsAvailable || !OcrService.InstalledLanguages.Any(l => l.StartsWith("en", StringComparison.OrdinalIgnoreCase)))
            return;
        string[][] rows = [["City", "Visitors", "Share"], ["Amman", "15200", "41%"], ["Petra", "9800", "26%"], ["Aqaba", "12100", "33%"]];
        using var bitmap = new SKBitmap(560, 200);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.White);
            using var font = new SKFont(SKTypeface.FromFamilyName("Segoe UI"), 20);
            using var ink = new SKPaint { Color = SKColors.Black, IsAntialias = true };
            using var rule = new SKPaint { Color = new SKColor(0xCC, 0xCC, 0xCC), StrokeWidth = 1 };
            for (int r = 0; r < rows.Length; r++)
            {
                for (int c = 0; c < rows[r].Length; c++)
                    canvas.DrawText(rows[r][c], 24 + c * 190, 38 + r * 42, font, ink);
                canvas.DrawLine(12, 50 + r * 42, 548, 50 + r * 42, rule);
            }
        }
        var png = Path.Combine(_dir, "table.png");
        using (var stream = File.Create(png))
            bitmap.Encode(stream, SKEncodedImageFormat.Png, 100);
        var table = await PictureTables.ReadFileAsync(png);
        var dump = string.Join(" | ", table.Rows.Select(r => string.Join(" ; ", r)));
        Assert.True(table.Rows.Count == 4, dump);
        Assert.True(table.Rows[3].Count == 3 && table.Rows[3][2] == "33%", dump);
        Assert.Equal(3, table.Columns);
        Assert.Equal("Amman", table.Rows[1][0]);
        Assert.Equal("9800", table.Rows[2][1]);
        Assert.Equal("33%", table.Rows[3][2]);
    }
}
