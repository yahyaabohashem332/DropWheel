using Petal.Core.Jobs;
using Petal.Engines.Documents.Converters;
using Petal.Engines.Documents.Pdf;
using Petal.Engines.Documents.Util;

namespace Petal.Engines.Documents.Tests;

/// <summary>PDF pages to pictures: drawn from one load of the file and saved several at once.</summary>
public sealed class PageImagesTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("petal-pages-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    /// <summary>A PDF of <paramref name="count"/> pages, a different picture on each.</summary>
    private byte[] Pdf(int count)
    {
        using var document = new PdfSharp.Pdf.PdfDocument();
        for (int i = 0; i < count; i++)
        {
            var page = document.AddPage();
            page.Width = PdfSharp.Drawing.XUnit.FromPoint(300 + 20 * i);
            page.Height = PdfSharp.Drawing.XUnit.FromPoint(200);
            using var g = PdfSharp.Drawing.XGraphics.FromPdfPage(page);
            g.DrawRectangle(PdfSharp.Drawing.XBrushes.SteelBlue, 10 + 25 * i, 20, 40, 120);
        }
        var path = Path.Combine(_dir, "pages.pdf");
        document.Save(path);
        return File.ReadAllBytes(path);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Every_page_comes_out_in_order_as_it_would_drawn_alone(bool jpeg)
    {
        var pdf = Pdf(7);
        var settings = new ConversionSettings { RenderDpi = 96 };
        var ext = jpeg ? "jpg" : "png";
        var files = PageImages.Write(pdf, Path.Combine(_dir, "pages.pdf"), Path.Combine(_dir, $"pages.{ext}"), jpeg, settings, null, 0, CancellationToken.None);

        Assert.Equal(Enumerable.Range(1, 7).Select(n => $"pages {n}.{ext}"), files.Select(Path.GetFileName));
        for (int i = 0; i < 7; i++)
        {
            // The same picture as drawing that page on its own.
            var alone = Path.Combine(_dir, $"alone {i}.{ext}");
            using (var bitmap = PdfRaster.Render(pdf, i, settings.RenderDpi))
                ImageTools.Save(bitmap, alone, jpeg, settings.ImageQuality);
            Assert.Equal(File.ReadAllBytes(alone), File.ReadAllBytes(files[i]));
        }
        Assert.Empty(Directory.GetDirectories(_dir, "*" + SafeOutput.TempSuffix));
    }

    [Fact]
    public void A_cancelled_conversion_leaves_nothing_behind()
    {
        var pdf = Pdf(7);
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() =>
            PageImages.Write(pdf, Path.Combine(_dir, "pages.pdf"), Path.Combine(_dir, "pages.png"), false, new ConversionSettings { RenderDpi = 96 }, null, 0, cancel.Token));
        Assert.Empty(Directory.GetDirectories(_dir));
        Assert.Equal(["pages.pdf"], Directory.GetFiles(_dir).Select(Path.GetFileName));
    }
}
