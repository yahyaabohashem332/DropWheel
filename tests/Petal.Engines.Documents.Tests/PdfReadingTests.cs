using System.IO.Compression;
using ClosedXML.Excel;
using DocumentFormat.OpenXml.Packaging;
using Petal.Core.Jobs;
using Petal.Engines.Documents.IO;
using Petal.Engines.Documents.Model;
using Petal.Engines.Documents.Pdf;
using Xunit.Abstractions;
using F = Petal.Core.Formats.Formats;
using M = Petal.Engines.Documents.Model;

namespace Petal.Engines.Documents.Tests;

public sealed class PdfReadingTests(ITestOutputHelper output)
{
    private static async Task<string> ConvertSampleAsync(string name, Petal.Core.Formats.FileFormat to, DocumentEngineOptions? options = null, string fixture = "pdf")
    {
        var dir = Fixtures.Instance.NewOutputDir(name);
        var input = Fixtures.Instance.CopyTo(Fixtures.Instance.Get(fixture), dir);
        var result = await Fixtures.ConvertAsync(new PdfConverter(options), input, F.Pdf, to);
        return result.Primary;
    }

    [Fact]
    public async Task Markdown_from_pdf_has_headings_lists_and_a_table()
    {
        var md = File.ReadAllText(await ConvertSampleAsync("pdf-md", F.Md));
        output.WriteLine(md);
        Assert.Contains("# Quarterly Petal Report", md);
        Assert.Contains("## Highlights", md);
        Assert.Contains("- Growth was strong", md);
        Assert.Contains("- Stems held firm", md);
        Assert.Contains("1. Water daily", md);
        Assert.Contains("2. Measure weekly", md);
        Assert.Matches(@"\|\s*Flower\s*\|\s*Petals\s*\|\s*Height\s*\|", md);
        Assert.Matches(@"\|\s*Rose\s*\|\s*32\s*\|\s*1,250\s*\|", md);
        // The wrapped paragraph is one paragraph, not one per line.
        Assert.Contains("during the quarter. It has several sentences", md);
        // Reading order: the closing line comes after the table, the appendix after that.
        Assert.True(md.IndexOf("Closing remarks", StringComparison.Ordinal) > md.IndexOf("Daisy", StringComparison.Ordinal));
        Assert.True(md.IndexOf("Appendix", StringComparison.Ordinal) > md.IndexOf("Closing remarks", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Word_from_pdf_has_editable_paragraphs_and_a_table()
    {
        var docx = await ConvertSampleAsync("pdf-docx", F.Docx);
        using var doc = WordprocessingDocument.Open(docx, false);
        var body = doc.MainDocumentPart!.Document!.Body!;
        var table = Assert.Single(body.Elements<DocumentFormat.OpenXml.Wordprocessing.Table>());
        Assert.Equal(4, table.Elements<DocumentFormat.OpenXml.Wordprocessing.TableRow>().Count());
        Assert.Contains("Rose", table.InnerText);
        var model = DocxReader.Read(docx);
        Assert.Contains(model.Blocks.OfType<Heading>(), h => h.Level == 1 && h.Inlines.PlainText().Contains("Quarterly"));
        Assert.Contains(model.Blocks.OfType<M.ListBlock>(), l => !l.Ordered && l.Items.Count == 3);
        Assert.Contains(model.Blocks.OfType<M.ListBlock>(), l => l.Ordered && l.Items.Count == 2);
    }

    [Fact]
    public async Task Excel_from_pdf_puts_table_cells_in_columns_and_numbers_as_numbers()
    {
        var xlsx = await ConvertSampleAsync("pdf-xlsx", F.Xlsx);
        using var wb = new XLWorkbook(xlsx);
        Assert.Equal(3, wb.Worksheets.Count);
        var ws = wb.Worksheet(1);
        var rose = ws.CellsUsed().First(c => c.GetString() == "Rose");
        var row = rose.Address.RowNumber;
        Assert.Equal(32.0, ws.Cell(row, rose.Address.ColumnNumber + 1).GetDouble());
        Assert.Equal(1250.0, ws.Cell(row, rose.Address.ColumnNumber + 2).GetDouble());
        Assert.Equal("Petals", ws.Cell(row - 1, rose.Address.ColumnNumber + 1).GetString());
    }

    [Fact]
    public async Task Scanned_pages_are_read_with_ocr()
    {
        if (!DocumentEngine.IsOcrAvailable)
        {
            output.WriteLine("Skipped: no Windows OCR language installed.");
            return;
        }
        var txt = File.ReadAllText(await ConvertSampleAsync("ocr-txt", F.Txt, fixture: "scanned"));
        output.WriteLine(txt);
        OutputVerifier.ContainsText(txt, "quick brown fox jumps over the lazy dog");
        OutputVerifier.ContainsText(txt, "SCANNED PETAL DOCUMENT");

        var md = File.ReadAllText(await ConvertSampleAsync("ocr-md", F.Md, fixture: "scanned"));
        // The large first line is recognised as the heading.
        Assert.StartsWith("# SCANNED PETAL DOCUMENT", md.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Ocr_can_be_switched_off()
    {
        var txt = File.ReadAllText(await ConvertSampleAsync("no-ocr", F.Txt, new DocumentEngineOptions { OcrScannedPages = false }, "scanned"));
        Assert.DoesNotContain("fox", txt);
    }

    [Fact]
    public async Task Fixed_layout_epub_has_one_picture_per_page()
    {
        var epub = await ConvertSampleAsync("pdf-epub-fixed", F.Epub, new DocumentEngineOptions { PdfToEpubLayout = PdfToEpubLayout.FixedLayout });
        using var zip = ZipFile.OpenRead(epub);
        Assert.Equal("mimetype", zip.Entries[0].FullName);
        Assert.Equal(3, zip.Entries.Count(e => e.FullName.StartsWith("OEBPS/images/page", StringComparison.Ordinal)));
        using var reader = new StreamReader(zip.GetEntry("OEBPS/content.opf")!.Open());
        Assert.Contains("pre-paginated", reader.ReadToEnd());
    }

    [Fact]
    public async Task Powerpoint_from_pdf_has_one_slide_per_page()
    {
        foreach (var mode in new[] { PdfToPptxMode.EditableText, PdfToPptxMode.Picture })
        {
            var pptx = await ConvertSampleAsync("pdf-pptx-" + mode, F.Pptx, new DocumentEngineOptions { PdfToPptxMode = mode });
            using var doc = PresentationDocument.Open(pptx, false);
            var slides = doc.PresentationPart!.SlideParts.ToList();
            Assert.Equal(3, slides.Count);
            var text = string.Concat(slides[0].Slide!.Descendants<DocumentFormat.OpenXml.Drawing.Text>().Select(t => t.Text));
            if (mode == PdfToPptxMode.EditableText)
                Assert.Contains("Quarterly Petal Report", text);
            else
                Assert.Equal("", text);
            Assert.All(slides, s => Assert.Single(s.ImageParts));
        }
    }

    [Fact]
    public async Task Pdf_to_images_writes_a_folder_at_the_render_resolution()
    {
        var dir = Fixtures.Instance.NewOutputDir("pdf-png");
        var input = Fixtures.Instance.CopyTo(Fixtures.Instance.Get("pdf"), dir);
        var result = await new PdfConverter().ConvertAsync(new ConversionJob(input, F.Pdf, F.Png, Path.Combine(dir, "sample.png"), new ConversionSettings { RenderDpi = 300 }), null, default);
        Assert.Equal(["sample 1.png", "sample 2.png", "sample 3.png"], result.OutputPaths.Select(Path.GetFileName));
        Assert.All(result.OutputPaths, p => Assert.Equal(Path.Combine(dir, "sample"), Path.GetDirectoryName(p)));
        using var bitmap = SkiaSharp.SKBitmap.Decode(result.OutputPaths[0]);
        // A4 at 300 DPI is 2480 × 3508.
        Assert.InRange(bitmap.Width, 2478, 2482);
        Assert.InRange(bitmap.Height, 3506, 3510);
    }

    [Fact]
    public void Running_headers_and_page_numbers_are_dropped()
    {
        var pages = Enumerable.Range(0, 3).Select(i =>
        {
            var page = new PdfPageContent { Index = i, Width = 600, Height = 800 };
            page.Words.Add(new PdfWord("ACME", 50, 90, 20, 30, 10, false, false, null));
            page.Words.Add(new PdfWord("Report", 95, 140, 20, 30, 10, false, false, null));
            page.Words.Add(new PdfWord($"Body{i}", 50, 100, 300, 310, 10, false, false, null));
            page.Words.Add(new PdfWord($"{i + 1}", 295, 305, 770, 780, 10, false, false, null));
            return page;
        }).ToList();
        var doc = PdfLayout.Analyze(pages, new DocMetadata(), new PdfLayoutOptions());
        var text = doc.PlainText();
        Assert.DoesNotContain("ACME", text);
        Assert.DoesNotContain(text.Split('\n'), l => l.Trim() is "1" or "2" or "3");
        Assert.Contains("Body1", text);
    }

    [Fact]
    public void Two_column_pages_read_column_by_column()
    {
        var page = new PdfPageContent { Index = 0, Width = 600, Height = 800 };
        void Line(double x, double y, string text)
        {
            var left = x;
            foreach (var word in text.Split(' '))
            {
                var width = word.Length * 5.5;
                page.Words.Add(new PdfWord(word, left, left + width, y, y + 10, 10, false, false, null));
                left += width + 3;
            }
        }
        for (var i = 0; i < 8; i++)
        {
            Line(50, 100 + i * 12, $"left column line number {i} has words");
            Line(320, 100 + i * 12, $"right column line number {i} has words");
        }
        var text = PdfLayout.Analyze([page], new DocMetadata(), new PdfLayoutOptions()).PlainText();
        Assert.True(text.IndexOf("left column line number 7", StringComparison.Ordinal) < text.IndexOf("right column line number 0", StringComparison.Ordinal), text);
    }
}
