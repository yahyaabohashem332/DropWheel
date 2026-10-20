using Petal.Core.Jobs;
using Petal.Engines.Documents.IO;
using Petal.Engines.Documents.Model;
using Petal.Engines.Documents.Sheets;
using F = Petal.Core.Formats.Formats;
using M = Petal.Engines.Documents.Model;

namespace Petal.Engines.Documents.Tests;

public sealed class RoundTripTests
{
    private static readonly DocumentEngineOptions NoOffice = new() { UseMicrosoftOffice = false, UseLibreOffice = false };

    [Fact]
    public async Task Docx_to_markdown_and_back_keeps_headings_and_lists()
    {
        var dir = Fixtures.Instance.NewOutputDir("docx-md-docx");
        var docx = Fixtures.Instance.CopyTo(Fixtures.Instance.Get("docx"), dir);
        var converter = new DocumentConverter(NoOffice);

        var md = (await Fixtures.ConvertAsync(converter, docx, F.Docx, F.Md)).Primary;
        var markdown = File.ReadAllText(md);
        Assert.Contains("# Petal Word Report", markdown);
        Assert.Contains("## Fruit", markdown);
        Assert.Contains("- Apple", markdown);
        Assert.Contains("1. Wash", markdown);
        Assert.Contains("**bold**", markdown);
        Assert.Contains("| Item", markdown);
        // The picture is saved beside the Markdown file and linked relatively.
        Assert.Contains("](sample%20images/image1.png)", markdown);
        Assert.True(File.Exists(Path.Combine(dir, "sample images", "image1.png")));

        var back = (await converter.ConvertAsync(new ConversionJob(md, F.Md, F.Docx, Path.Combine(dir, "back.docx"), new()), null, default)).Primary;
        var original = DocxReader.Read(docx);
        var roundTripped = DocxReader.Read(back);

        Assert.Equal(Headings(original), Headings(roundTripped));
        Assert.Equal(Lists(original), Lists(roundTripped));
        Assert.Contains(roundTripped.Blocks.Descendants(), b => b is M.Table);
        Assert.Contains(roundTripped.AllImages(), _ => true);
    }

    private static List<string> Headings(DocModel doc) =>
        doc.Blocks.OfType<Heading>().Select(h => $"{h.Level}:{h.Inlines.PlainText().Trim()}").ToList();

    private static List<string> Lists(DocModel doc)
    {
        var result = new List<string>();
        void Walk(M.ListBlock list, int depth)
        {
            foreach (var item in list.Items)
            {
                foreach (var block in item.Blocks)
                {
                    if (block is M.Paragraph p)
                        result.Add($"{depth}:{(list.Ordered ? "ol" : "ul")}:{p.Inlines.PlainText().Trim()}");
                    else if (block is M.ListBlock nested)
                        Walk(nested, depth + 1);
                }
            }
        }
        foreach (var list in doc.Blocks.OfType<M.ListBlock>())
            Walk(list, 0);
        return result;
    }

    [Fact]
    public async Task Csv_to_xlsx_and_back_keeps_every_cell()
    {
        var dir = Fixtures.Instance.NewOutputDir("csv-xlsx-csv");
        var csv = Fixtures.Instance.CopyTo(Fixtures.Instance.Get("csv"), dir);
        var converter = new SpreadsheetConverter(NoOffice);

        var xlsx = (await Fixtures.ConvertAsync(converter, csv, F.Csv, F.Xlsx)).Primary;
        var back = (await converter.ConvertAsync(new ConversionJob(xlsx, F.Xlsx, F.Csv, Path.Combine(dir, "back.csv"), new()), null, default)).Primary;

        var expected = DelimitedText.Parse(Text.TextFile.ReadAllText(csv), ',').ToList();
        var actual = DelimitedText.Parse(Text.TextFile.ReadAllText(back), ',').ToList();
        Assert.Equal(expected.Count, actual.Count);
        for (var r = 0; r < expected.Count; r++)
            Assert.Equal(expected[r], actual[r]);

        // Numbers became numbers, "007" stayed text.
        var model = ExcelSheets.ReadXlsx(xlsx);
        Assert.Equal(12.0, model.Sheets[0].Rows[1][1].Value);
        Assert.Equal("007", model.Sheets[0].Rows[4][0].Value);
        Assert.Equal("مرحبا", model.Sheets[0].Rows[3][0].Value);
        Assert.Equal("line one\nline two", model.Sheets[0].Rows[2][3].Value);
        // Excel needs the byte order mark to read UTF-8.
        var bytes = File.ReadAllBytes(back);
        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);
    }

    [Fact]
    public void Csv_quoting_follows_rfc_4180()
    {
        var rows = DelimitedText.Parse("a,\"b,c\",\"d\"\"e\"\r\n\"x\r\ny\",,z", ',').ToList();
        Assert.Equal(["a", "b,c", "d\"e"], rows[0]);
        Assert.Equal(["x\ny", "", "z"], rows[1]);
        Assert.Equal("\"b,c\"", DelimitedText.Quote("b,c", ','));
        Assert.Equal("\"say \"\"hi\"\"\"", DelimitedText.Quote("say \"hi\"", ','));
        Assert.Equal("\" pad\"", DelimitedText.Quote(" pad", ','));
        Assert.Equal("plain", DelimitedText.Quote("plain", ','));
    }

    [Fact]
    public async Task Workbook_with_several_sheets_becomes_a_folder_of_csv_files()
    {
        var dir = Fixtures.Instance.NewOutputDir("multi-sheet");
        var path = Path.Combine(dir, "book.xlsx");
        using (var wb = new ClosedXML.Excel.XLWorkbook())
        {
            wb.AddWorksheet("North").Cell(1, 1).Value = "Petal north";
            wb.AddWorksheet("South").Cell(1, 1).Value = "Petal south";
            wb.SaveAs(path);
        }
        var result = await Fixtures.ConvertAsync(new SpreadsheetConverter(NoOffice), path, F.Xlsx, F.Csv);
        Assert.Equal(2, result.OutputPaths.Count);
        Assert.All(result.OutputPaths, p => Assert.Equal(Path.Combine(dir, "book"), Path.GetDirectoryName(p)));
        Assert.Equal(["book North.csv", "book South.csv"], result.OutputPaths.Select(Path.GetFileName));
        Assert.Contains("Petal south", File.ReadAllText(result.OutputPaths[1]));
    }

    [Theory]
    [InlineData("odt")]
    [InlineData("rtf")]
    [InlineData("epub")]
    [InlineData("docx")]
    [InlineData("html")]
    [InlineData("md")]
    public void Writers_and_readers_agree(string format)
    {
        var dir = Fixtures.Instance.NewOutputDir("model-" + format);
        var path = Path.Combine(dir, "doc." + format);
        var doc = Fixtures.RichModel();
        var target = F.FromId(format)!;
        Converters.DocumentIO.Write(doc, target, path);
        var back = Converters.DocumentIO.Read(path, target);

        Assert.Equal(Headings(doc), Headings(back));
        Assert.Equal(Lists(doc), Lists(back));
        var table = back.Blocks.Descendants().OfType<M.Table>().Single();
        Assert.Equal(3, table.Rows.Count);
        Assert.Equal("Petal", table.Rows[1].Cells[0].Blocks.PlainText());
        var runs = back.AllInlines().OfType<TextRun>().ToList();
        Assert.Contains(runs, r => r.Text == "bold" && r.Has(TextStyle.Bold));
        Assert.Contains(runs, r => r.Text == "italic" && r.Has(TextStyle.Italic));
        Assert.Contains(runs, r => r.Link == "https://example.com/petal");
        Assert.Single(back.AllImages());
    }

    [Fact]
    public void Text_files_keep_line_breaks_and_paragraphs()
    {
        var doc = PlainTextIO.Parse(Fixtures.PlainText);
        var paragraphs = doc.Blocks.OfType<M.Paragraph>().ToList();
        Assert.Equal(3, paragraphs.Count);
        Assert.Contains(paragraphs[1].Inlines, i => i is M.LineBreak);
    }

    [Fact]
    public void Html_reader_understands_lists_tables_and_direction()
    {
        var doc = HtmlReader.Parse(Fixtures.Html);
        Assert.Equal("Petal HTML Page", doc.Metadata.Title);
        var ordered = doc.Blocks.OfType<M.ListBlock>().Single(l => l.Ordered);
        Assert.Equal(3, ordered.Start);
        var table = doc.Blocks.OfType<M.Table>().Single();
        Assert.True(table.Rows[0].IsHeader);
        Assert.Equal(2, table.Rows[2].Cells[0].ColSpan);
        Assert.Contains(doc.Blocks.OfType<M.Paragraph>(), p => p.Inlines.PlainText() == "مرحبا بالعالم");
    }
}
