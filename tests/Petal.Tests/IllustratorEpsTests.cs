using System.Text;
using ImageMagick;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using Petal.Core.Formats;
using Petal.Core.Jobs;
using Petal.Engines.Documents;
using Petal.Engines.Documents.Pdf;
using Petal.Engines.Imaging;
using Xunit.Abstractions;
using F = Petal.Core.Formats.Formats;

namespace Petal.Tests;

public sealed class IllustratorEpsTests(ITestOutputHelper output)
{
    private readonly string _dir = Directory.CreateTempSubdirectory("petal-adobe-").FullName;
    private static readonly ConverterRegistry Converters = Petal.App.EngineSetup.CreateConverters();

    /// <summary>
    /// An Illustrator file as Illustrator saves it with PDF content: the artwork (a red square on
    /// a transparent artboard), plus a large private editing stream reached through /PieceInfo.
    /// </summary>
    private string WriteIllustrator(string name)
    {
        using var document = new PdfDocument();
        var page = document.AddPage();
        page.Width = XUnit.FromPoint(200);
        page.Height = XUnit.FromPoint(100);
        using (var g = XGraphics.FromPdfPage(page))
            g.DrawRectangle(XBrushes.Red, 20, 20, 60, 60);
        var editing = new PdfDictionary(document);
        editing.CreateStream(Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("%AI24_ZStandard_Data private editing data ", 8000))));
        document.Internals.AddObject(editing);
        var privateData = new PdfDictionary(document);
        privateData.Elements["/AIPrivateData1"] = editing.Reference;
        var illustrator = new PdfDictionary(document);
        illustrator.Elements["/Private"] = privateData;
        var pieceInfo = new PdfDictionary(document);
        pieceInfo.Elements["/Illustrator"] = illustrator;
        page.Elements["/PieceInfo"] = pieceInfo;
        var path = Path.Combine(_dir, name);
        document.Save(path);
        return path;
    }

    private static async Task<string> ConvertAsync(string input, FileFormat to, string folder)
    {
        var from = F.FromPath(input)!;
        var outputPath = Path.Combine(folder, Path.GetFileNameWithoutExtension(input) + "." + to.WriteExtension);
        var converter = Converters.Find(from, to) ?? throw new InvalidOperationException($"No engine for {from} → {to}");
        await converter.ConvertAsync(new ConversionJob(input, from, to, outputPath, new ConversionSettings { RenderDpi = 144 }), null, CancellationToken.None);
        return outputPath;
    }

    [Fact]
    public async Task An_Illustrator_file_becomes_a_picture_with_a_transparent_background()
    {
        var ai = WriteIllustrator("logo.ai");
        Assert.Equal(F.Ai, F.FromPath(ai));
        var png = await ConvertAsync(ai, F.Png, _dir);
        using var image = new MagickImage(png);
        // 200 × 100 points at 144 DPI.
        Assert.Equal((400u, 200u), (image.Width, image.Height));
        using var pixels = image.GetPixels();
        var square = pixels.GetPixel(100, 100).ToColor()!;
        var empty = pixels.GetPixel(300, 100).ToColor()!;
        Assert.Equal((255, 0, 0, 255), (square.R, square.G, square.B, square.A));
        Assert.Equal(0, empty.A);
    }

    [Fact]
    public async Task An_Illustrator_file_becomes_a_plain_PDF_without_its_editing_data()
    {
        var ai = WriteIllustrator("label.ai");
        var pdf = await ConvertAsync(ai, F.Pdf, _dir);
        var before = new FileInfo(ai).Length;
        var after = new FileInfo(pdf).Length;
        output.WriteLine($"{before} → {after} bytes");
        Assert.True(after < before / 3, $"{before} → {after}");
        var text = Encoding.Latin1.GetString(File.ReadAllBytes(pdf));
        Assert.DoesNotContain("/PieceInfo", text);
        Assert.DoesNotContain("AIPrivateData", text);
        AssertSamePicture(ai, pdf);
    }

    /// <summary>Both files draw the same artwork.</summary>
    private static void AssertSamePicture(string a, string b)
    {
        using var first = new MagickImage(IllustratorPdf.Render(a, 0, 72, transparent: false));
        using var second = new MagickImage(IllustratorPdf.Render(b, 0, 72, transparent: false));
        Assert.Equal((first.Width, first.Height), (second.Width, second.Height));
        Assert.True(first.Compare(second, ErrorMetric.RootMeanSquared) < 0.01);
    }

    /// <summary>A DOS EPS (as Illustrator and Photoshop save them): PostScript plus a TIFF preview in the header.</summary>
    private string WriteDosEps(string name)
    {
        var postScript = Encoding.ASCII.GetBytes("%!PS-Adobe-3.0 EPSF-3.0\n%%Creator: Adobe Illustrator(R) 24.0\n%%BoundingBox: 0 0 120 60\n%%HiResBoundingBox: 0 0 120.5 60.25\n%%EndComments\n1 0 0 setrgbcolor 0 0 120 60 rectfill\nshowpage\n%%EOF\n");
        using var preview = new MagickImage(MagickColors.Red, 120, 60);
        var tiff = preview.ToByteArray(MagickFormat.Tiff);
        var header = new byte[30];
        header[0] = 0xC5; header[1] = 0xD0; header[2] = 0xD3; header[3] = 0xC6;
        BitConverter.GetBytes(30).CopyTo(header, 4);
        BitConverter.GetBytes(postScript.Length).CopyTo(header, 8);
        BitConverter.GetBytes(30 + postScript.Length).CopyTo(header, 20);
        BitConverter.GetBytes(tiff.Length).CopyTo(header, 24);
        header[28] = 0xFF; header[29] = 0xFF;
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, [.. header, .. postScript, .. tiff]);
        return path;
    }

    [Fact]
    public void An_EPS_shows_the_picture_it_carries_and_its_size()
    {
        var eps = WriteDosEps("badge.eps");
        Assert.Equal(F.Eps, ImageInspector.DetectFormat(eps));
        using var preview = EpsFile.LoadPreview(eps)!;
        Assert.Equal((120u, 60u), (preview.Width, preview.Height));
        Assert.Equal((120.5, 60.25), EpsFile.BoundingBox(eps));
        Assert.Equal("Adobe Illustrator(R) 24.0", EpsFile.Creator(eps));
    }

    [Fact]
    public void An_EPSI_preview_is_read_from_its_hex_rows()
    {
        // 8 × 2, one bit per pixel: a black/white checker of 4-pixel blocks, then the inverse.
        var eps = "%!PS-Adobe-3.0 EPSF-3.0\n%%BoundingBox: 0 0 8 2\n%%BeginPreview: 8 2 1 2\n% 0F\n% F0\n%%EndPreview\nshowpage\n";
        using var image = EpsFile.Epsi(eps)!;
        Assert.Equal((8u, 2u), (image.Width, image.Height));
        using var pixels = image.GetPixels();
        Assert.Equal(0, pixels.GetPixel(0, 0).ToColor()!.R);   // 0 is black
        Assert.Equal(255, pixels.GetPixel(7, 0).ToColor()!.R);
        Assert.Equal(255, pixels.GetPixel(0, 1).ToColor()!.R);
        Assert.Equal(0, pixels.GetPixel(7, 1).ToColor()!.R);
    }

    [Fact]
    public async Task Without_Ghostscript_an_EPS_conversion_says_what_is_needed()
    {
        if (Petal.Core.Util.Ghostscript.IsAvailable)
        {
            output.WriteLine("Skipped: Ghostscript is installed here.");
            return;
        }
        var eps = WriteDosEps("needs.eps");
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => ConvertAsync(eps, F.Png, _dir));
        Assert.Equal(Petal.Core.Util.Ghostscript.Needed, ex.Message);
        ex = await Assert.ThrowsAnyAsync<Exception>(() => ConvertAsync(eps, F.Pdf, _dir));
        Assert.Equal(Petal.Core.Util.Ghostscript.Needed, ex.Message);
    }

    /// <summary>Real files (copies) in PETAL_ADOBE_SAMPLES: Illustrator files draw and slim down; EPS previews load. Structure only.</summary>
    [Fact]
    public async Task Real_Illustrator_and_EPS_files()
    {
        var folder = Environment.GetEnvironmentVariable("PETAL_ADOBE_SAMPLES");
        if (folder is null)
        {
            output.WriteLine("Skipped: PETAL_ADOBE_SAMPLES isn't set.");
            return;
        }
        var outDir = Directory.CreateTempSubdirectory("petal-adobe-out-").FullName;
        foreach (var ai in Directory.GetFiles(folder, "*.ai"))
        {
            var png = await ConvertAsync(ai, F.Png, outDir);
            using var image = new MagickImage(png);
            using var pixels = image.GetPixels();
            int opaque = 0, total = 0;
            for (int y = 0; y < image.Height; y += 7)
                for (int x = 0; x < image.Width; x += 7, total++)
                    if (pixels.GetPixel(x, y).ToColor()!.A > 0)
                        opaque++;
            var pdf = await ConvertAsync(ai, F.Pdf, outDir);
            var text = Encoding.Latin1.GetString(File.ReadAllBytes(pdf));
            Assert.DoesNotContain("AIPrivateData", text);
            AssertSamePicture(ai, pdf);
            output.WriteLine($"{Path.GetFileName(ai)}: {image.Width}x{image.Height}, {100.0 * opaque / total:0}% drawn; PDF {new FileInfo(ai).Length} → {new FileInfo(pdf).Length} bytes");
            Assert.InRange(opaque, total / 100 + 1, total);
        }
        foreach (var eps in Directory.GetFiles(folder, "*.eps"))
        {
            using var preview = EpsFile.LoadPreview(eps);
            output.WriteLine($"{Path.GetFileName(eps)}: preview {(preview is null ? "none" : $"{preview.Width}x{preview.Height}")}, box {EpsFile.BoundingBox(eps)}, by {EpsFile.Creator(eps)}");
        }
    }
}
