using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using Petal.Engines.Documents.Pdf;
using SkiaSharp;

namespace Petal.Engines.Documents.Tests;

public sealed class PdfToolTests
{
    private static string Sample => Fixtures.Instance.Get("pdf");

    private static string Copy(string name)
    {
        var dir = Fixtures.Instance.NewOutputDir(name);
        return Fixtures.Instance.CopyTo(Sample, dir);
    }

    private static List<string> PageTexts(string path)
    {
        using var pdf = UglyToad.PdfPig.PdfDocument.Open(path);
        // Letters in content order without spaces: rotated pages have no reliable words.
        return pdf.GetPages().Select(p => new string(p.Text.Where(c => !char.IsWhiteSpace(c)).ToArray())).ToList();
    }

    [Fact]
    public void Document_view_reports_pages_and_renders_thumbnails()
    {
        using var view = PdfDocumentView.Open(Sample);
        Assert.Equal(3, view.PageCount);
        Assert.InRange(view.PageSizes[0].Width, 595, 596);
        Assert.InRange(view.PageSizes[0].Height, 841, 842);
        var bitmap = view.RenderPage(0, 200);
        Assert.Equal(200, bitmap.Width);
        Assert.InRange(bitmap.Height, 280, 285);
        Assert.Equal(bitmap.Stride * bitmap.Height, bitmap.Pixels.Length);
        var rotated = view.RenderPage(0, 200, rotation: 90);
        Assert.True(rotated.Height < rotated.Width * 2);
        var png = view.RenderPagePng(1, 120);
        using var decoded = SKBitmap.Decode(png);
        Assert.Equal(120, decoded.Width);
        Assert.Equal(3, PdfInfo.GetPageCount(Sample));
    }

    [Fact]
    public async Task Split_writes_one_file_per_page()
    {
        var input = Copy("split");
        var folder = Path.Combine(Path.GetDirectoryName(input)!, "sample Pages");
        var files = await PdfPageTools.SplitAsync(input, folder);
        Assert.Equal(3, files.Count);
        Assert.Equal(["sample 1.pdf", "sample 2.pdf", "sample 3.pdf"], files.Select(Path.GetFileName));
        for (var i = 0; i < 3; i++)
        {
            using var doc = PdfReader.Open(files[i], PdfDocumentOpenMode.Import);
            Assert.Equal(1, doc.PageCount);
        }
        Assert.Contains("Appendix", PageTexts(files[1])[0]);
        Assert.False(Directory.Exists(folder + ".petal-tmp"));
    }

    [Fact]
    public async Task Merge_joins_pages_in_order_and_keeps_bookmarks()
    {
        var dir = Fixtures.Instance.NewOutputDir("merge");
        var a = Fixtures.Instance.CopyTo(Sample, dir);
        var b = Path.Combine(dir, "second.pdf");
        using (var doc = new PdfDocument())
        {
            for (var i = 0; i < 2; i++)
            {
                var page = doc.AddPage();
                using var g = XGraphics.FromPdfPage(page);
                g.DrawString($"Second file page {i + 1}", new XFont("Arial", 18), XBrushes.Black, 60, 80);
            }
            doc.Outlines.Add("Second file start", doc.Pages[0]);
            doc.Save(b);
        }
        var output = Path.Combine(dir, "merged.pdf");
        var pages = await PdfPageTools.MergeAsync([a, b], output, new PdfMergeOptions(BookmarkEachFile: true));
        Assert.Equal(5, pages);
        var texts = PageTexts(output);
        Assert.Equal(5, texts.Count);
        Assert.Contains("Quarterly", texts[0]);
        Assert.Contains("Secondfilepage2", texts[4]);
        using var merged = PdfReader.Open(output, PdfDocumentOpenMode.Import);
        Assert.Equal(["sample", "second"], merged.Outlines.Select(o => o.Title));
        Assert.Equal("Second file start", merged.Outlines[1].Outlines[0].Title);
    }

    [Fact]
    public async Task Organize_reorders_rotates_duplicates_and_drops_pages()
    {
        var input = Copy("organize");
        var output = Path.Combine(Path.GetDirectoryName(input)!, "sample Organized.pdf");
        await PdfPageTools.OrganizeAsync(input, output, [new(2), new(0, 90), new(0)]);
        using var doc = PdfReader.Open(output, PdfDocumentOpenMode.Import);
        Assert.Equal(3, doc.PageCount);
        Assert.Equal(90, doc.Pages[1].Rotate);
        Assert.Equal(0, doc.Pages[2].Rotate);
        var texts = PageTexts(output);
        Assert.Contains("Thirdpage", texts[0]);
        Assert.Contains("Quarterly", texts[1]);
        Assert.Contains("Quarterly", texts[2]);
        Assert.DoesNotContain(texts, t => t.Contains("Appendix"));
    }

    [Fact]
    public async Task Organize_can_make_every_page_the_narrowest_width()
    {
        var dir = Fixtures.Instance.NewOutputDir("uniform");
        var input = Path.Combine(dir, "mixed.pdf");
        using (var doc = new PdfDocument())
        {
            foreach (var (w, h) in new[] { (595.0, 842.0), (842.0, 595.0), (300.0, 400.0) })
            {
                var page = doc.AddPage();
                page.Width = XUnit.FromPoint(w);
                page.Height = XUnit.FromPoint(h);
                using var g = XGraphics.FromPdfPage(page);
                g.DrawString("Petal", new XFont("Arial", 20), XBrushes.Black, 20, 40);
            }
            doc.Save(input);
        }
        var output = Path.Combine(dir, "uniform.pdf");
        await PdfPageTools.OrganizeAsync(input, output, [new(0), new(1), new(2, 90)], new PdfOrganizeOptions(UniformWidth: true));
        var sizes = PdfInfo.GetPageSizes(output);
        // Page 3 rotated is 400 wide, page 1 is 595, page 2 is 842: the narrowest is 400.
        Assert.All(sizes, s => Assert.InRange(s.Width, 399.5, 400.5));
        Assert.InRange(sizes[0].Height, 842 * 400 / 595.0 - 1, 842 * 400 / 595.0 + 1);
        Assert.Contains("Petal", PageTexts(output)[1]);
    }

    [Fact]
    public async Task Compress_never_makes_a_file_larger()
    {
        var input = Copy("compress-text");
        var output = Path.Combine(Path.GetDirectoryName(input)!, "sample Compressed.pdf");
        var result = await PdfCompressor.CompressAsync(input, output);
        Assert.True(result.OutputBytes <= result.OriginalBytes);
        if (result.Written)
            Assert.True(new FileInfo(output).Length < new FileInfo(input).Length);
        else
        {
            Assert.False(File.Exists(output));
            Assert.Equal(result.OriginalBytes, result.OutputBytes);
        }
    }

    [Fact]
    public async Task Compress_downsamples_large_pictures()
    {
        var dir = Fixtures.Instance.NewOutputDir("compress-photo");
        var input = Path.Combine(dir, "photo.pdf");
        MakePhotoPdf(input);
        var output = Path.Combine(dir, "photo Compressed.pdf");
        var result = await PdfCompressor.CompressAsync(input, output, new PdfCompressOptions { Preset = PdfCompressionPreset.Balanced });
        Assert.True(result.Written);
        Assert.True(result.ImagesRecompressed >= 1);
        Assert.True(result.OutputBytes < result.OriginalBytes * 0.6, $"{result.OriginalBytes} → {result.OutputBytes}");
        Assert.Equal(result.OutputBytes, new FileInfo(output).Length);
        using var doc = PdfReader.Open(output, PdfDocumentOpenMode.Import);
        Assert.Equal(1, doc.PageCount);

        var strong = Path.Combine(dir, "photo Strong.pdf");
        var strongResult = await PdfCompressor.CompressAsync(input, strong, new PdfCompressOptions { Preset = PdfCompressionPreset.Strong });
        Assert.True(strongResult.OutputBytes < result.OutputBytes);

        var target = Path.Combine(dir, "photo Target.pdf");
        // A target below what Balanced reaches forces stronger settings.
        var goal = strongResult.OutputBytes - 1;
        var targetResult = await PdfCompressor.CompressAsync(input, target, new PdfCompressOptions { TargetBytes = goal });
        Assert.True(targetResult.Written);
        Assert.True(targetResult.TargetReached);
        Assert.True(targetResult.OutputBytes <= goal);
    }

    /// <summary>A page with a 2400×1800 noisy photo shown at 4×3 inches: far more pixels than needed.</summary>
    private static void MakePhotoPdf(string path)
    {
        using var bitmap = new SKBitmap(2400, 1800);
        var random = new Random(7);
        using (var canvas = new SKCanvas(bitmap))
        {
            using var shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(2400, 1800), [SKColors.OrangeRed, SKColors.SteelBlue], SKShaderTileMode.Clamp);
            using var paint = new SKPaint { Shader = shader };
            canvas.DrawRect(0, 0, 2400, 1800, paint);
            using var dot = new SKPaint { IsAntialias = true };
            for (var i = 0; i < 4000; i++)
            {
                dot.Color = new SKColor((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256));
                canvas.DrawCircle(random.Next(2400), random.Next(1800), random.Next(2, 14), dot);
            }
        }
        using var image = SKImage.FromBitmap(bitmap);
        var jpeg = image.Encode(SKEncodedImageFormat.Jpeg, 95).ToArray();
        using var doc = new PdfDocument();
        var page = doc.AddPage();
        using (var g = XGraphics.FromPdfPage(page))
        using (var x = XImage.FromStream(new MemoryStream(jpeg)))
            g.DrawImage(x, 72, 72, 288, 216);
        doc.Info.Title = "Photo";
        doc.Save(path);
    }

    [Fact]
    public async Task Metadata_can_be_read_edited_and_removed()
    {
        var input = Copy("metadata");
        var fields = await PdfMetadataEditor.ReadAsync(input);
        Assert.Equal("Quarterly Petal Report", fields.Single(f => f.Key == "Title").Value);
        Assert.Equal("Petal Tests", fields.Single(f => f.Key == "Author").Value);

        var edited = Path.Combine(Path.GetDirectoryName(input)!, "edited.pdf");
        await PdfMetadataEditor.WriteAsync(input, edited, new Dictionary<string, string?>
        {
            ["Title"] = "Petal — نسخة جديدة",
            ["Author"] = null,
            ["Producer"] = "Petal Producer",
            ["Keywords"] = "flowers, petals",
            ["CreationDate"] = "2024-05-01 13:45:00 +02:00",
        });
        var after = (await PdfMetadataEditor.ReadAsync(edited)).ToDictionary(f => f.Key, f => f.Value);
        Assert.Equal("Petal — نسخة جديدة", after["Title"]);
        Assert.Equal("", after["Author"]);
        Assert.Equal("Petal Producer", after["Producer"]);
        Assert.Equal("flowers, petals", after["Keywords"]);
        Assert.StartsWith("2024-05-01 13:45:00", after["CreationDate"]);
        using (var pig = UglyToad.PdfPig.PdfDocument.Open(edited))
        {
            Assert.Equal("Petal Producer", pig.Information.Producer);
            Assert.Equal(3, pig.NumberOfPages);
        }

        var stripped = Path.Combine(Path.GetDirectoryName(input)!, "stripped.pdf");
        await PdfMetadataEditor.RemoveAllAsync(edited, stripped);
        var none = await PdfMetadataEditor.ReadAsync(stripped);
        Assert.All(none.Where(f => f.Group == PdfMetadataGroup.Document), f => Assert.Equal("", f.Value));
        Assert.DoesNotContain(none, f => f.Group == PdfMetadataGroup.Xmp);
        using (var pig = UglyToad.PdfPig.PdfDocument.Open(stripped))
        {
            Assert.True(string.IsNullOrEmpty(pig.Information.Producer));
            Assert.True(string.IsNullOrEmpty(pig.Information.Title));
            Assert.Equal(3, pig.NumberOfPages);
        }
        Assert.DoesNotContain("PDFsharp", System.Text.Encoding.Latin1.GetString(File.ReadAllBytes(stripped)));
    }

    [Fact]
    public async Task Tools_refuse_to_overwrite_their_input()
    {
        var input = Copy("overwrite");
        await Assert.ThrowsAsync<ArgumentException>(() => PdfPageTools.OrganizeAsync(input, input, [new(0)]));
        await Assert.ThrowsAsync<ArgumentException>(() => PdfCompressor.CompressAsync(input, input));
        await Assert.ThrowsAsync<ArgumentException>(() => PdfMetadataEditor.RemoveAllAsync(input, input));
    }
}
