using Petal.Engines.Documents.Model;
using Petal.Engines.Documents.Pdf;
using Petal.Engines.Documents.Sheets;
using Petal.Engines.Documents.Text;
using Petal.Engines.Documents.Util;
using M = Petal.Engines.Documents.Model;

namespace Petal.Engines.Documents.Tests;

public sealed class UnitTests
{
    [Fact]
    public void Arabic_is_shaped_into_presentation_forms_and_reversed()
    {
        // "سلام": initial seen, then lam-alef as one (final) ligature, then isolated meem; drawn right to left.
        Assert.Equal("ﻡﻼﺳ", Bidi.ToVisual("سلام"));
        // A lone lam-alef is the isolated ligature.
        Assert.Equal("ﻻ", Bidi.Shape("لا"));
    }

    [Fact]
    public void Built_in_pdf_renderer_shapes_arabic_and_wraps_it_right_to_left()
    {
        var dir = Fixtures.Instance.NewOutputDir("arabic-pdf");
        var path = Path.Combine(dir, "arabic.pdf");
        var doc = new DocModel();
        doc.Blocks.Add(Fixtures.Para("مرحبا بالعالم"));
        // Long enough to wrap onto several lines.
        doc.Blocks.Add(Fixtures.Para(string.Join(" ", Enumerable.Repeat("هذا نص عربي طويل للتجربة", 12))));
        doc.Blocks.Add(Fixtures.Para("English stays as it is."));
        PdfDocWriter.Write(doc, path);

        using var pdf = UglyToad.PdfPig.PdfDocument.Open(path);
        var letters = pdf.GetPage(1).Letters;
        // Contextual forms, not the isolated base letters.
        Assert.Contains(letters, l => l.Value.Length == 1 && l.Value[0] is >= 'ﹰ' and <= '﻿');
        // Right-aligned: Arabic lines end at the right margin.
        var arabicLines = letters.Where(l => Bidi.IsRtlChar(l.Value[0])).GroupBy(l => Math.Round(l.StartBaseLine.Y)).ToList();
        Assert.True(arabicLines.Count >= 3);
        var right = Paper.PageSizePt.Width - Paper.MarginPt;
        Assert.All(arabicLines, line => Assert.InRange(line.Max(l => l.EndBaseLine.X), right - 12, right + 1));
        Assert.Contains("English", string.Join(" ", pdf.GetPage(1).GetWords().Select(w => w.Text)));
    }

    [Fact]
    public void Mixed_direction_lines_keep_numbers_and_latin_left_to_right()
    {
        var visual = Bidi.ToVisual("عدد 25 Petal");
        Assert.Contains("25", visual);
        Assert.Contains("Petal", visual);
        // In a right-to-left line the first logical word ends up on the right.
        Assert.True(visual.IndexOf("Petal", StringComparison.Ordinal) < visual.IndexOf("25", StringComparison.Ordinal));
        Assert.Equal("Plain text", Bidi.ToVisual("Plain text"));
    }

    [Theory]
    [InlineData("1,250", 1250.0, "#,##0")]
    [InlineData("12.5%", 0.125, "0.0%")]
    [InlineData("(42)", -42.0, null)]
    [InlineData("$3.10", 3.1, "\"$\"#,##0.00")]
    [InlineData("3.14", 3.14, "0.00")]
    public void Pdf_table_numbers_are_parsed(string text, double value, string? format)
    {
        var parsed = PdfToXlsx.ParseNumber(text);
        Assert.NotNull(parsed);
        Assert.Equal(value, parsed.Value.Value, 6);
        Assert.Equal(format, parsed.Value.Format);
    }

    [Theory]
    [InlineData("007")]
    [InlineData("Rose")]
    [InlineData("1,23")]
    [InlineData("12-05")]
    public void Pdf_table_text_stays_text(string text) => Assert.Null(PdfToXlsx.ParseNumber(text));

    [Theory]
    [InlineData("42", true)]
    [InlineData("-3.5", true)]
    [InlineData("007", false)]
    [InlineData("1e5", false)]
    [InlineData("12345678901234567890", false)]
    [InlineData("", false)]
    public void Csv_cells_become_numbers_only_when_lossless(string text, bool numeric) =>
        Assert.Equal(numeric, SheetCell.FromText(text).Value is double);

    [Fact]
    public void Sheet_names_follow_excel_rules()
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Assert.Equal("Q1_2024", ExcelSheets.SheetName("Q1/2024", used));
        Assert.Equal("Q1_2024 (2)", ExcelSheets.SheetName("Q1:2024", used));
        Assert.Equal(31, ExcelSheets.SheetName(new string('x', 40), used).Length);
        Assert.Equal("AA", SheetPdfWriter.ColumnName(26));
    }

    [Fact]
    public void Table_layout_resolves_spans()
    {
        var table = new M.Table();
        var r1 = new M.TableRow();
        r1.Cells.Add(new M.TableCell { RowSpan = 2 });
        r1.Cells.Add(new M.TableCell { ColSpan = 2 });
        var r2 = new M.TableRow();
        r2.Cells.Add(new M.TableCell());
        r2.Cells.Add(new M.TableCell());
        table.Rows.Add(r1);
        table.Rows.Add(r2);
        var (rows, columns) = TableLayout.Layout(table);
        Assert.Equal(3, columns);
        Assert.Equal([0, 1, 2], rows[1].Select(s => s.Column));
        Assert.True(rows[1][0].IsContinuation);
    }

    [Fact]
    public void Text_files_in_legacy_encodings_are_decoded()
    {
        TextFile.EnsureCodePages();
        var arabic = System.Text.Encoding.GetEncoding(1256).GetBytes("مرحبا");
        var latin = System.Text.Encoding.GetEncoding(1252).GetBytes("café");
        Assert.Equal("café", TextFile.Decode(latin).Normalize());
        Assert.Equal("مرحبا", TextFile.Decode(System.Text.Encoding.UTF8.GetBytes("مرحبا")));
        Assert.Equal("hi", TextFile.Decode([0xFF, 0xFE, (byte)'h', 0, (byte)'i', 0]));
        Assert.NotEmpty(TextFile.Decode(arabic));
    }

    [Fact]
    public void Font_names_from_pdfs_map_to_installed_families()
    {
        Assert.Equal("Times New Roman", PdfToPptx.FamilyOf("ABCDEF+TimesNewRomanPS-BoldMT"));
        Assert.Equal("Arial", PdfToPptx.FamilyOf("ArialMT"));
        Assert.Equal("Calibri", PdfToPptx.FamilyOf("Calibri,Bold"));
        Assert.Equal("Segoe UI", PdfToPptx.FamilyOf("SegoeUI"));
    }

    [Fact]
    public void Safe_output_builds_page_folders_atomically()
    {
        var dir = Fixtures.Instance.NewOutputDir("pages");
        var input = Path.Combine(dir, "deck.pdf");
        File.WriteAllText(input, "");
        Directory.CreateDirectory(Path.Combine(dir, "deck"));
        var paths = SafeOutput.WritePages(input, Path.Combine(dir, "deck.png"), 2, (i, p) => File.WriteAllText(p, i.ToString()));
        Assert.Equal(Path.Combine(dir, "deck 2", "deck 1.png"), paths[0]);
        Assert.Single(SafeOutput.WritePages(input, Path.Combine(dir, "single.png"), 1, (_, p) => File.WriteAllText(p, "x")));
        Assert.True(File.Exists(Path.Combine(dir, "single.png")));
    }
}
