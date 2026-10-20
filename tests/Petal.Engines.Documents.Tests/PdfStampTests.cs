using PdfSharp.Drawing;
using PdfSharp.Pdf;
using Petal.Engines.Documents.Pdf;
using SkiaSharp;

namespace Petal.Engines.Documents.Tests;

/// <summary>Password protection, stamping (watermarks, signatures), page numbers and picture extraction.</summary>
public sealed class PdfStampTests
{
    /// <summary>
    /// Three pages: plain A4, A4 shown rotated by 90°, and A4 with a crop box inset from the media
    /// box. The same JPEG is on pages 1 and 2 and a PNG on page 3.
    /// </summary>
    private static string BuildSample(string dir)
    {
        var path = Path.Combine(dir, "sample.pdf");
        using var document = new PdfDocument();
        var jpeg = Encode(SKEncodedImageFormat.Jpeg, SKColors.SteelBlue);
        var png = Encode(SKEncodedImageFormat.Png, SKColors.Orange);
        for (int i = 0; i < 3; i++)
        {
            var page = document.AddPage();
            page.Width = XUnit.FromPoint(595);
            page.Height = XUnit.FromPoint(842);
            using (var gfx = XGraphics.FromPdfPage(page))
            using (var image = XImage.FromStream(new MemoryStream(i < 2 ? jpeg : png)))
                gfx.DrawImage(image, 200, 300, 160, 120);
            if (i == 1)
                page.Rotate = 90;
            if (i == 2)
                page.CropBox = new PdfRectangle(new XPoint(50, 60), new XPoint(545, 782));
        }
        document.Save(path);
        return path;
    }

    private static byte[] Encode(SKEncodedImageFormat format, SKColor color)
    {
        using var bitmap = new SKBitmap(200, 150);
        bitmap.Erase(color);
        using var image = SKImage.FromBitmap(bitmap);
        return image.Encode(format, 90).ToArray();
    }

    /// <summary>A transparent PNG of the page with a red block in its top-left quarter.</summary>
    private static byte[] TopLeftMark(PdfPageFrame frame)
    {
        using var bitmap = new SKBitmap((int)frame.Width, (int)frame.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        bitmap.Erase(SKColors.Transparent);
        using (var canvas = new SKCanvas(bitmap))
            canvas.DrawRect(0, 0, (float)(frame.Width / 4), (float)(frame.Height / 4), new SKPaint { Color = SKColors.Red });
        using var image = SKImage.FromBitmap(bitmap);
        return image.Encode(SKEncodedImageFormat.Png, 100).ToArray();
    }

    private static (byte R, byte G, byte B) Pixel(PdfPageBitmap bitmap, double fx, double fy)
    {
        int x = (int)(bitmap.Width * fx), y = (int)(bitmap.Height * fy);
        int i = y * bitmap.Stride + x * 4;
        return (bitmap.Pixels[i + 2], bitmap.Pixels[i + 1], bitmap.Pixels[i]);
    }

    private static bool IsRed((byte R, byte G, byte B) p) => p.R > 200 && p.G < 60 && p.B < 60;

    [Fact]
    public async Task Stamps_land_top_left_as_each_page_is_displayed()
    {
        var dir = Fixtures.Instance.NewOutputDir("stamp");
        var input = BuildSample(dir);
        var output = Path.Combine(dir, "stamped.pdf");
        var frames = new List<PdfPageFrame>();
        await PdfStamper.StampAsync(input, output, f => { frames.Add(f); return TopLeftMark(f); });

        Assert.Equal(3, frames.Count);
        Assert.Equal((842.0, 595.0), (frames[1].Width, frames[1].Height));
        Assert.Equal((495.0, 722.0), (frames[2].Width, frames[2].Height));
        for (int page = 0; page < 3; page++)
        {
            var bitmap = PdfInfo.RenderPage(output, page, 300);
            Assert.True(IsRed(Pixel(bitmap, 0.05, 0.05)), $"page {page + 1}: top-left should be red");
            Assert.False(IsRed(Pixel(bitmap, 0.9, 0.9)), $"page {page + 1}: bottom-right should not be red");
            Assert.False(IsRed(Pixel(bitmap, 0.9, 0.05)), $"page {page + 1}: top-right should not be red");
        }
    }

    [Fact]
    public async Task Pages_without_an_overlay_are_left_alone()
    {
        var dir = Fixtures.Instance.NewOutputDir("stamp-one");
        var input = BuildSample(dir);
        var output = Path.Combine(dir, "last-only.pdf");
        await PdfStamper.StampAsync(input, output, f => f.Index == 2 ? TopLeftMark(f) : null);
        Assert.False(IsRed(Pixel(PdfInfo.RenderPage(output, 0, 200), 0.05, 0.05)));
        Assert.True(IsRed(Pixel(PdfInfo.RenderPage(output, 2, 200), 0.05, 0.05)));
    }

    [Fact]
    public async Task Page_numbers_are_text_and_can_skip_the_cover()
    {
        var dir = Fixtures.Instance.NewOutputDir("numbers");
        var input = BuildSample(dir);
        var output = Path.Combine(dir, "numbered.pdf");
        await PdfStamper.NumberPagesAsync(input, output, new PageNumberOptions(Style: PageNumberStyle.NumberOfTotal, SkipFirstPage: true));

        using var pdf = UglyToad.PdfPig.PdfDocument.Open(output);
        Assert.DoesNotContain("/", pdf.GetPage(1).Text);
        Assert.Contains("1 / 2", pdf.GetPage(2).Text);
        Assert.Contains("2 / 2", pdf.GetPage(3).Text);
    }

    [Fact]
    public async Task Page_numbers_sit_at_the_bottom_of_rotated_pages()
    {
        var dir = Fixtures.Instance.NewOutputDir("numbers-rotated");
        var input = BuildSample(dir);
        var output = Path.Combine(dir, "numbered.pdf");
        await PdfStamper.NumberPagesAsync(input, output, new PageNumberOptions(FontSize: 40, Start: 7));
        // The big number darkens the bottom middle of the page as displayed, not its side.
        var bitmap = PdfInfo.RenderPage(output, 1, 1200);
        int Ink(double fx0, double fy0, double fx1, double fy1)
        {
            int dark = 0;
            for (int y = (int)(bitmap.Height * fy0); y < (int)(bitmap.Height * fy1); y++)
                for (int x = (int)(bitmap.Width * fx0); x < (int)(bitmap.Width * fx1); x++)
                {
                    int i = y * bitmap.Stride + x * 4;
                    if (bitmap.Pixels[i] + bitmap.Pixels[i + 1] + bitmap.Pixels[i + 2] < 300) dark++;
                }
            return dark;
        }
        string Where()
        {
            var cells = new List<string>();
            for (int gy = 0; gy < 4; gy++)
                for (int gx = 0; gx < 4; gx++)
                    if (Ink(gx / 4.0, gy / 4.0, (gx + 1) / 4.0, (gy + 1) / 4.0) is > 0 and var n)
                        cells.Add($"({gx},{gy})={n}");
            return string.Join(" ", cells);
        }
        Assert.True(Ink(0.4, 0.85, 0.6, 0.99) > 30, "number at the bottom centre; ink at " + Where());
        Assert.True(Ink(0.0, 0.4, 0.1, 0.6) == 0, "nothing on the left edge; ink at " + Where());
    }

    [Fact]
    public async Task Protecting_asks_for_the_password_and_unlocking_removes_it()
    {
        var dir = Fixtures.Instance.NewOutputDir("protect");
        var input = BuildSample(dir);
        var locked = Path.Combine(dir, "locked.pdf");
        await PdfSecurityTools.ProtectAsync(input, locked, new PdfProtectOptions("سر 2024 سر", AllowCopying: false));

        Assert.True(PdfSecurityTools.NeedsPassword(locked));
        Assert.False(PdfSecurityTools.CanOpen(locked, "wrong"));
        Assert.True(PdfSecurityTools.CanOpen(locked, "سر 2024 سر"));
        Assert.Contains("/V 5", System.Text.Encoding.Latin1.GetString(File.ReadAllBytes(locked)));

        var open = Path.Combine(dir, "open.pdf");
        await PdfSecurityTools.UnlockAsync(locked, open, "سر 2024 سر");
        Assert.False(PdfSecurityTools.NeedsPassword(open));
        Assert.Equal(3, PdfInfo.GetPageCount(open));
    }

    [Theory]
    [InlineData("Petal-2024!", true)]
    [InlineData("كلمة سر", true)]
    [InlineData("سر 2024 سر", true)]
    [InlineData("سر-123", false)]      // ends with a digit
    [InlineData("pass سر", false)]     // mixes Latin letters in
    [InlineData("", false)]
    public void Password_rules_follow_aes_256(string password, bool usable) =>
        Assert.Equal(usable, PdfSecurityTools.IsUsablePassword(password));

    [Fact]
    public async Task Unlocking_with_the_wrong_password_fails_and_writes_nothing()
    {
        var dir = Fixtures.Instance.NewOutputDir("protect-wrong");
        var input = BuildSample(dir);
        var locked = Path.Combine(dir, "locked.pdf");
        await PdfSecurityTools.ProtectAsync(input, locked, new PdfProtectOptions("right"));
        var open = Path.Combine(dir, "open.pdf");
        await Assert.ThrowsAsync<PdfOpenException>(() => PdfSecurityTools.UnlockAsync(locked, open, "wrong"));
        Assert.False(File.Exists(open));
    }

    [Fact]
    public async Task Extracts_each_picture_once_keeping_jpegs_as_they_are()
    {
        var dir = Fixtures.Instance.NewOutputDir("extract");
        var input = BuildSample(dir);
        var folder = Path.Combine(dir, "sample Images");
        var files = await PdfStamper.ExtractImagesAsync(input, folder);

        Assert.Equal(2, files.Count);
        Assert.Single(files, f => f.EndsWith(".jpg"));
        Assert.Single(files, f => f.EndsWith(".png"));
        Assert.All(files, f => Assert.True(File.Exists(f)));
        var jpg = File.ReadAllBytes(files.Single(f => f.EndsWith(".jpg")));
        Assert.Equal(0xFF, jpg[0]);
        Assert.Equal(0xD8, jpg[1]);
        using var decoded = SKBitmap.Decode(files.Single(f => f.EndsWith(".png")));
        Assert.Equal(200, decoded.Width);
    }
}
