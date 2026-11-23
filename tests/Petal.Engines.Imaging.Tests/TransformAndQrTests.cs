using ImageMagick;
using Petal.Core.Jobs;
using F = Petal.Core.Formats.Formats;

namespace Petal.Engines.Imaging.Tests;

public sealed class TransformTests
{
    [Theory]
    [InlineData(ResizeMode.Percent, 50, 0, 0, 32, 24)]
    [InlineData(ResizeMode.Width, 0, 16, 0, 16, 12)]
    [InlineData(ResizeMode.Height, 0, 0, 12, 16, 12)]
    [InlineData(ResizeMode.Fit, 0, 20, 20, 20, 15)]
    [InlineData(ResizeMode.Fit, 0, 1000, 1000, 64, 48)]
    public void Sizes_keep_proportions_and_never_enlarge_by_default(ResizeMode mode, double percent, int w, int h, int ew, int eh) =>
        Assert.Equal((ew, eh), ImageTransformer.TargetSize(64, 48, new ImageTransform { Mode = mode, Percent = percent, Width = w, Height = h }));

    [Fact]
    public void Sizes_refer_to_the_rotated_picture()
    {
        var t = new ImageTransform { Mode = ResizeMode.Width, Width = 24, Rotation = 90 };
        Assert.Equal((24, 32), ImageTransformer.TargetSize(64, 48, t));
        Assert.Equal((1000, 750), ImageTransformer.TargetSize(64, 48, new ImageTransform { Mode = ResizeMode.Fit, Width = 1000, Height = 1000, Enlarge = true }));
    }

    [Fact]
    public async Task Rotates_clockwise_and_resizes()
    {
        var dir = Fixtures.NewDirectory();
        using var source = Fixtures.Quadrants();
        var input = Fixtures.Save(source, dir, F.Png);
        var output = Path.Combine(dir, "turned.png");
        await ImageTransformer.TransformAsync(input, output, new ImageTransform { Rotation = 90, Mode = ResizeMode.Percent, Percent = 50 });

        using var result = new MagickImage(output);
        Assert.Equal(24u, result.Width);
        Assert.Equal(32u, result.Height);
        // A quarter turn clockwise brings the bottom-left (blue) quadrant to the top-left.
        Fixtures.AssertColor(Fixtures.Blue, Fixtures.PixelAt(result, 3, 3), 12, "top-left");
        Fixtures.AssertColor(Fixtures.Red, Fixtures.PixelAt(result, 20, 3), 12, "top-right");
    }

    [Fact]
    public async Task Flips_mirror_the_picture()
    {
        var dir = Fixtures.NewDirectory();
        using var source = Fixtures.Quadrants();
        var input = Fixtures.Save(source, dir, F.Png);
        var output = Path.Combine(dir, "flipped.png");
        await ImageTransformer.TransformAsync(input, output, new ImageTransform { FlipHorizontal = true, FlipVertical = true });

        using var result = new MagickImage(output);
        Assert.Equal(64u, result.Width);
        Fixtures.AssertColor(Fixtures.Light, Fixtures.PixelAt(result, 2, 2), 2, "top-left after both flips");
        Fixtures.AssertColor(Fixtures.Red, Fixtures.PixelAt(result, 62, 46), 2, "bottom-right after both flips");
    }

    [Fact]
    public async Task Keeps_a_jpeg_a_jpeg()
    {
        var dir = Fixtures.NewDirectory();
        using var source = Fixtures.Photo();
        var input = Fixtures.Save(source, dir, F.Jpg);
        var output = Path.Combine(dir, "small.jpg");
        await ImageTransformer.TransformAsync(input, output, new ImageTransform { Mode = ResizeMode.Width, Width = 320 });
        using var result = new MagickImage(output);
        Assert.Equal(MagickFormat.Jpeg, result.Format);
        Assert.Equal(320u, result.Width);
        Assert.Equal(240u, result.Height);
    }
}

public sealed class OverlayTests
{
    [Fact]
    public async Task Draws_the_overlay_at_the_image_size()
    {
        var dir = Fixtures.NewDirectory();
        using var source = Fixtures.Quadrants();
        var input = Fixtures.Save(source, dir, F.Png);
        var output = Path.Combine(dir, "marked.png");
        (int W, int H) asked = default;
        await ImageOverlay.CompositeAsync(input, output, (w, h) =>
        {
            asked = (w, h);
            // Opaque white square in the top-left corner, transparent elsewhere.
            using var layer = new MagickImage(MagickColors.Transparent, (uint)w, (uint)h);
            Fixtures.Fill(layer, 0, 0, 8, 8, MagickColors.White);
            using var ms = new MemoryStream();
            layer.Write(ms, MagickFormat.Png32);
            return ms.ToArray();
        });
        Assert.Equal((64, 48), asked);
        using var result = new MagickImage(output);
        Fixtures.AssertColor(MagickColors.White, Fixtures.PixelAt(result, 2, 2), 2, "under the mark");
        Fixtures.AssertColor(Fixtures.Red, Fixtures.PixelAt(result, 20, 10), 2, "beside the mark");
    }
}

public sealed class QrTests
{
    [Theory]
    [InlineData("https://example.com/petal")]
    [InlineData("مرحبًا من Petal")]
    public async Task Codes_read_back(string text)
    {
        var dir = Fixtures.NewDirectory();
        var modules = QrGenerator.Encode(text);
        var png = Path.Combine(dir, "code.png");
        File.WriteAllBytes(png, QrGenerator.ToPng(modules, 8, MagickColors.Black, MagickColors.White));
        var found = await BarcodeScanner.ScanAsync(png);
        Assert.Equal(text, Assert.Single(found).Text);
    }

    [Fact]
    public void Svg_has_one_rectangle_per_run()
    {
        var modules = QrGenerator.Encode("Petal");
        var svg = QrGenerator.ToSvg(modules, "#000", "#fff");
        Assert.StartsWith("<svg", svg);
        Assert.Contains($"viewBox=\"0 0 {modules.GetLength(0) + 8} {modules.GetLength(0) + 8}\"", svg);
        Assert.Contains("h", svg);
    }

    [Fact]
    public void Wifi_payload_escapes_special_characters() =>
        Assert.Equal(@"WIFI:T:WPA;S:Cafe\;Net;P:pa\:ss;;", QrGenerator.WifiPayload("Cafe;Net", "pa:ss"));

    [Fact]
    public void Open_networks_have_no_password() =>
        Assert.Equal("WIFI:T:nopass;S:Guest;;", QrGenerator.WifiPayload("Guest", ""));

    [Fact]
    public void An_email_a_number_a_message_and_a_contact_are_written_as_phones_read_them()
    {
        Assert.Equal("mailto:hello@example.com?subject=Room%20booking&body=Is%20Friday%20free%3F", QrGenerator.EmailPayload(" hello@example.com ", "Room booking", "Is Friday free?"));
        Assert.Equal("mailto:hello@example.com", QrGenerator.EmailPayload("hello@example.com", "", ""));
        // Arabic and Persian digits dial as Western ones; spaces, brackets and dashes go.
        Assert.Equal("tel:+905321234567", QrGenerator.PhonePayload("+90 (532) 123-45-67"));
        Assert.Equal("tel:0599123456", QrGenerator.PhonePayload("٠٥٩٩١٢٣٤٥٦"));
        Assert.Equal("SMSTO:+12025550123:See you at 6", QrGenerator.SmsPayload("+1 202 555 0123", "See you at 6"));
        var card = QrGenerator.ContactPayload("Sara; Ali", "+90 532 000 00 00", "sara@example.com");
        Assert.Equal("BEGIN:VCARD\nVERSION:3.0\nN:;Sara\\; Ali;;;\nFN:Sara\\; Ali\nTEL;TYPE=CELL:+905320000000\nEMAIL:sara@example.com\nEND:VCARD", card);
        // Each still fits a code.
        foreach (var payload in new[] { card, QrGenerator.SmsPayload("123", "hi") })
            Assert.True(QrGenerator.Encode(payload).GetLength(0) > 20);
    }

    [Fact]
    public void Empty_text_is_refused() =>
        Assert.Throws<ArgumentException>(() => QrGenerator.Encode(""));
}

public class FavouriteSizeTests
{
    private readonly ImageConverter _converter = new();

    [Fact]
    public async Task Shrinks_to_the_longest_side_and_can_resave_in_the_same_format()
    {
        var dir = Fixtures.NewDirectory();
        using (var photo = Fixtures.Photo(640, 480))
        {
            var jpg = Fixtures.Save(photo, dir, F.Jpg);
            var output = Path.Combine(dir, "small.jpg");
            var settings = new ConversionSettings { MaxLongEdge = 200 };

            Assert.True(_converter.CanResave(F.Jpg));
            await _converter.ConvertAsync(new ConversionJob(jpg, F.Jpg, F.Jpg, output, settings), null, CancellationToken.None);
            using var small = new MagickImage(output);
            Assert.Equal((200u, 150u), (small.Width, small.Height));

            // Portrait pictures are limited by their height.
            var tall = Path.Combine(dir, "tall.png");
            using (var rotated = (MagickImage)photo.Clone())
            {
                rotated.Rotate(90);
                rotated.Write(tall);
            }
            var webp = Path.Combine(dir, "tall.webp");
            await _converter.ConvertAsync(new ConversionJob(tall, F.Png, F.Webp, webp, settings), null, CancellationToken.None);
            using var portrait = new MagickImage(webp);
            Assert.Equal((150u, 200u), (portrait.Width, portrait.Height));
        }

        // Smaller pictures are never enlarged.
        using var tiny = Fixtures.Quadrants();
        var png = Fixtures.Save(tiny, dir, F.Png, "tiny");
        var same = Path.Combine(dir, "tiny 200.png");
        await _converter.ConvertAsync(new ConversionJob(png, F.Png, F.Png, same, new ConversionSettings { MaxLongEdge = 200 }), null, CancellationToken.None);
        using var kept = new MagickImage(same);
        Assert.Equal((64u, 48u), (kept.Width, kept.Height));

        Assert.False(_converter.CanResave(F.Svg));
        Assert.False(_converter.CanResave(F.Pdf));
        Directory.Delete(dir, true);
    }
}
