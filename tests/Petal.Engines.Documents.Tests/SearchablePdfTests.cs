using PdfSharp.Drawing;
using Petal.Core.Jobs;
using Petal.Engines.Documents.Pdf;
using Petal.Engines.Documents.Text;
using SkiaSharp;
using UglyToad.PdfPig;
using Xunit.Abstractions;
using F = Petal.Core.Formats.Formats;

namespace Petal.Engines.Documents.Tests;

public sealed class SearchablePdfTests(ITestOutputHelper output)
{
    private static string Dir => Fixtures.Instance.Dir;

    /// <summary>A "scan" at 200 DPI with the text only as pixels; right-to-left lines are shaped first.</summary>
    private static byte[] ScanPng(int width, int height, string[] lines)
    {
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.White);
            using var typeface = SKTypeface.FromFamilyName("Arial");
            using var font = new SKFont(typeface, 48);
            using var paint = new SKPaint { Color = SKColors.Black, IsAntialias = true };
            for (int i = 0; i < lines.Length; i++)
            {
                bool rtl = Bidi.ContainsRtl(lines[i]);
                canvas.DrawText(Bidi.ToVisual(lines[i]), rtl ? width - 140 : 140, 260 + i * 110, rtl ? SKTextAlign.Right : SKTextAlign.Left, font, paint);
            }
        }
        using var image = SKImage.FromBitmap(bitmap);
        return image.Encode(SKEncodedImageFormat.Png, 100).ToArray();
    }

    /// <summary>One page showing the scan upright, whatever the page's rotation and crop box.</summary>
    private static string ScanPdf(string name, int rotate, bool cropped, params string[] lines)
    {
        var path = Path.Combine(Dir, name);
        using var document = new PdfSharp.Pdf.PdfDocument();
        var page = document.AddPage();
        page.Width = XUnit.FromPoint(595.28);
        page.Height = XUnit.FromPoint(841.89);
        page.Rotate = rotate;
        if (cropped)
            page.CropBox = new PdfSharp.Pdf.PdfRectangle(new XPoint(30, 40), new XPoint(565, 800));
        var (width, height) = PdfStamper.DisplayedSize(page);
        var png = ScanPng((int)(width / 72 * 200), (int)(height / 72 * 200), lines);
        using (var gfx = PdfStamper.Upright(page))
        using (var image = XImage.FromStream(new MemoryStream(png)))
            gfx.DrawImage(image, 0, 0, width, height);
        document.Save(path);
        return path;
    }

    private static async Task<string> TextOf(string pdf)
    {
        var converter = Fixtures.ConverterFor(F.Pdf, F.Txt, new DocumentEngineOptions { OcrScannedPages = false })!;
        var result = await Fixtures.ConvertAsync(converter, pdf, F.Pdf, F.Txt);
        return File.ReadAllText(result.OutputPaths[0]);
    }

    private static byte[] Render(string pdf) =>
        PdfRaster.Render(File.ReadAllBytes(pdf), 0, 72).Encode(SKEncodedImageFormat.Png, 100).ToArray();

    [Fact]
    public async Task A_scan_gets_invisible_text_over_its_words()
    {
        if (!SearchablePdf.IsAvailable)
        {
            output.WriteLine("Skipped: no Windows OCR language installed.");
            return;
        }
        var scan = Fixtures.Instance.Get("scanned");
        var searchable = Path.Combine(Dir, "scanned searchable.pdf");

        var result = await SearchablePdf.MakeAsync(scan, searchable, new SearchablePdfOptions { Languages = ["en-US"] });

        Assert.True(result.Written);
        Assert.Equal((1, 0), (result.PagesRead, result.PagesWithText));
        Assert.True(result.Words >= 15, $"{result.Words} words");
        var text = await TextOf(searchable);
        output.WriteLine(text);
        OutputVerifier.ContainsText(text, "quick brown fox jumps over the lazy dog");

        // The words sit where the pixels are: the line starts 140 px in, "The " is about 80 px
        // wide at that size, and the baseline is at 420 px (of 1654 × 2339 for A4).
        using (var pdf = PdfDocument.Open(searchable))
        {
            var quick = pdf.GetPage(1).GetWords().First(w => w.Text.Contains("quick", StringComparison.OrdinalIgnoreCase));
            output.WriteLine($"quick at {quick.BoundingBox}, baseline {quick.Letters[0].StartBaseLine}");
            Assert.InRange(quick.BoundingBox.Left, 220 / 1654.0 * 595.28 - 4, 220 / 1654.0 * 595.28 + 4);
            Assert.InRange(quick.Letters[0].StartBaseLine.Y, 841.89 - 420 / 2339.0 * 841.89 - 3, 841.89 - 420 / 2339.0 * 841.89 + 3);
        }
        // Nothing visible changes.
        Assert.Equal(Render(scan), Render(searchable));
        PdfPageBoxTests.AssertNoEmptyBoxes(searchable);
        PdfStructureTests.AssertReferencesResolve(searchable);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(90, false)]
    [InlineData(270, true)]
    [InlineData(180, true)]
    public async Task Rotated_and_cropped_pages_get_their_text_the_right_way_up(int rotate, bool cropped)
    {
        if (!SearchablePdf.IsAvailable)
            return;
        var scan = ScanPdf($"rotated-{rotate}{(cropped ? "-cropped" : "")}.pdf", rotate, cropped, "ROTATED PAGE HEADING", "Searchable text keeps its place on the page.");
        var searchable = Path.ChangeExtension(scan, null) + " searchable.pdf";

        var result = await SearchablePdf.MakeAsync(scan, searchable, new SearchablePdfOptions { Languages = ["en-US"] });

        Assert.Equal(1, result.PagesRead);
        var text = await TextOf(searchable);
        output.WriteLine(text);
        OutputVerifier.ContainsText(text, "Searchable text keeps its place on the page");
        OutputVerifier.ContainsText(text, "ROTATED PAGE HEADING");
        // The text is laid out as displayed: the heading comes first, not sideways letters.
        Assert.True(text.IndexOf("ROTATED", StringComparison.OrdinalIgnoreCase) < text.IndexOf("Searchable", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(Render(scan), Render(searchable));
        PdfPageBoxTests.AssertNoEmptyBoxes(searchable);
        PdfStructureTests.AssertReferencesResolve(searchable);
    }

    [Fact]
    public async Task Arabic_scans_read_back_in_logical_order()
    {
        if (!SearchablePdf.IsAvailable || !Petal.Engines.Documents.Ocr.OcrService.InstalledLanguages.Any(l => l.StartsWith("ar", StringComparison.OrdinalIgnoreCase)))
        {
            output.WriteLine("Skipped: no Arabic OCR language installed.");
            return;
        }
        var scan = ScanPdf("arabic scan.pdf", 0, false, "مرحبا بكم في بتلة", "هذا المستند صار قابلا للبحث");
        var searchable = Path.Combine(Dir, "arabic searchable.pdf");

        var result = await SearchablePdf.MakeAsync(scan, searchable, new SearchablePdfOptions { Languages = ["ar-SA"] });

        Assert.Equal(1, result.PagesRead);
        var text = await TextOf(searchable);
        output.WriteLine(text);
        Assert.Contains("المستند", text);
        Assert.Contains("بكم", text);
        Assert.Equal(Render(scan), Render(searchable));
        PdfPageBoxTests.AssertNoEmptyBoxes(searchable);
        PdfStructureTests.AssertReferencesResolve(searchable);
    }

    [Fact]
    public async Task Pages_that_already_have_text_are_left_alone()
    {
        if (!SearchablePdf.IsAvailable)
            return;
        var pdf = Fixtures.Instance.Get("pdf");
        var result = await SearchablePdf.MakeAsync(pdf, Path.Combine(Dir, "already.pdf"));
        Assert.False(result.Written);
        Assert.Equal(0, result.PagesRead);
        Assert.False(File.Exists(Path.Combine(Dir, "already.pdf")));
    }
}
