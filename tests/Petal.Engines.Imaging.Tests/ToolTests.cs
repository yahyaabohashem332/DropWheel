using ImageMagick;
using ZXing;
using ZXing.Common;
using F = Petal.Core.Formats.Formats;

namespace Petal.Engines.Imaging.Tests;

public sealed class CropTests
{
    [Fact]
    public async Task CropsToRectangle()
    {
        var dir = Fixtures.NewDirectory();
        using var source = Fixtures.Quadrants();
        var input = Fixtures.Save(source, dir, F.Png);
        var output = Path.Combine(dir, "red.png");
        await ImageCropper.CropAsync(input, output, new PixelRect(2, 2, 28, 20));

        using var result = new MagickImage(output);
        Assert.Equal(28u, result.Width);
        Assert.Equal(20u, result.Height);
        Fixtures.AssertColor(Fixtures.Red, Fixtures.PixelAt(result, 27, 19), 0, "corner");
    }

    [Fact]
    public async Task RectangleIsInUprightCoordinates()
    {
        var dir = Fixtures.NewDirectory();
        var input = Path.Combine(dir, "rotated.jpg");
        using (var source = Fixtures.Quadrants())
        {
            var exif = new ExifProfile();
            exif.SetValue(ExifTag.Make, "PetalCam");
            exif.SetValue(ExifTag.Orientation, (ushort)6);
            source.SetProfile(exif);
            source.Orientation = Orientation.RightTop;
            source.Quality = 95;
            source.Write(input, MagickFormat.Jpeg);
        }
        var output = Path.Combine(dir, "cropped.jpg");
        await ImageCropper.CropAsync(input, output, new PixelRect(0, 0, 20, 28)); // displayed top-left = blue

        using var result = new MagickImage(output);
        Assert.Equal(20u, result.Width);
        Assert.Equal(28u, result.Height);
        Fixtures.AssertColor(Fixtures.Blue, Fixtures.PixelAt(result, 10, 14), 20, "blue");
        Assert.Equal("PetalCam", result.GetExifProfile()?.GetValue(ExifTag.Make)?.Value);
        Assert.True(result.Orientation is Orientation.TopLeft or Orientation.Undefined);
    }

    [Fact]
    public async Task BatchCropsToAspectAndExactSize()
    {
        var dir = Fixtures.NewDirectory();
        using var source = Fixtures.Quadrants();
        var a = Fixtures.Save(source, dir, F.Png, "a");
        var b = Fixtures.Save(source, dir, F.Jpg, "b");

        var square = await ImageCropper.CropBatchAsync(
            [new ImageJobItem(a, Path.Combine(dir, "a1.png")), new ImageJobItem(b, Path.Combine(dir, "b1.jpg")), new ImageJobItem(Path.Combine(dir, "missing.png"), Path.Combine(dir, "x.png"))],
            new CropSpec.AspectRatio(1, 1));
        Assert.True(square[0].Succeeded && square[1].Succeeded);
        Assert.False(square[2].Succeeded);
        using (var img = new MagickImage(square[0].OutputPath))
        {
            Assert.Equal(48u, img.Width);
            Assert.Equal(48u, img.Height);
        }

        await ImageCropper.CropAsync(a, Path.Combine(dir, "exact.png"), new CropSpec.ExactSize(30, 20));
        using (var img = new MagickImage(Path.Combine(dir, "exact.png")))
            Assert.Equal((30u, 20u), (img.Width, img.Height));

        await ImageCropper.CropAsync(a, Path.Combine(dir, "window.png"), new CropSpec.ExactSize(100, 10, ScaleToFill: false));
        using (var img = new MagickImage(Path.Combine(dir, "window.png")))
            Assert.Equal((64u, 10u), (img.Width, img.Height));
    }

    [Fact]
    public void CenteredRectMatchesAspect()
    {
        Assert.Equal(new PixelRect(8, 0, 48, 48), ImageCropper.CenteredRect(64, 48, 1, 1));
        Assert.Equal(new PixelRect(0, 6, 64, 36), ImageCropper.CenteredRect(64, 48, 16, 9));
    }
}

public sealed class CompressTests
{
    private static string PhotoJpeg(string dir, int quality = 95, int w = 800, int h = 600)
    {
        var path = Path.Combine(dir, $"photo{quality}.jpg");
        using var image = Fixtures.Photo(w, h);
        var exif = new ExifProfile();
        exif.SetValue(ExifTag.Make, "PetalCam");
        image.SetProfile(exif);
        image.Quality = (uint)quality;
        image.Write(path, MagickFormat.Jpeg);
        return path;
    }

    [Fact]
    public async Task PresetsShrinkJpegs()
    {
        var dir = Fixtures.NewDirectory();
        var input = PhotoJpeg(dir);
        var balanced = await ImageCompressor.CompressAsync(input, Path.Combine(dir, "balanced.jpg"));
        var strong = await ImageCompressor.CompressAsync(input, Path.Combine(dir, "strong.jpg"), new ImageCompressOptions { Preset = CompressionPreset.Strong });

        Assert.True(balanced.OutputBytes < balanced.OriginalBytes, $"{balanced.OutputBytes} vs {balanced.OriginalBytes}");
        Assert.True(strong.OutputBytes < balanced.OutputBytes);
        Assert.Equal(80, balanced.Quality);
        Assert.Equal(new FileInfo(Path.Combine(dir, "balanced.jpg")).Length, balanced.OutputBytes);
        using var img = new MagickImage(balanced.OutputPath);
        Assert.Equal("PetalCam", img.GetExifProfile()?.GetValue(ExifTag.Make)?.Value);
    }

    [Fact]
    public async Task CanStripMetadataWhileCompressing()
    {
        var dir = Fixtures.NewDirectory();
        var input = PhotoJpeg(dir, quality: 40);
        var result = await ImageCompressor.CompressAsync(input, Path.Combine(dir, "bare.jpg"), new ImageCompressOptions { KeepMetadata = false });
        Assert.False(result.KeptOriginal);
        using var image = new MagickImage(result.OutputPath);
        Assert.Null(image.GetExifProfile());
    }

    [Fact]
    public async Task NeverMakesAFileBigger()
    {
        var dir = Fixtures.NewDirectory();
        var input = PhotoJpeg(dir, quality: 40);
        var result = await ImageCompressor.CompressAsync(input, Path.Combine(dir, "out.jpg"));
        Assert.True(result.OutputBytes <= result.OriginalBytes);
    }

    [Fact]
    public async Task HitsTargetSize()
    {
        var dir = Fixtures.NewDirectory();
        var input = PhotoJpeg(dir);
        const long target = 30_000;
        var result = await ImageCompressor.CompressAsync(input, Path.Combine(dir, "target.jpg"), new ImageCompressOptions { TargetBytes = target });
        Assert.True(result.TargetReached);
        Assert.True(result.OutputBytes <= target, $"{result.OutputBytes} > {target}");
        Assert.Equal(800, result.Width); // quality alone was enough

        var tiny = await ImageCompressor.CompressAsync(input, Path.Combine(dir, "tiny.jpg"), new ImageCompressOptions { TargetBytes = 4_000 });
        Assert.True(tiny.OutputBytes <= 4_000, $"{tiny.OutputBytes}");
        Assert.True(tiny.Width < 800, "had to downscale");
        Assert.True(tiny.TargetReached);
    }

    [Fact]
    public async Task ReductionPercentAndResize()
    {
        var dir = Fixtures.NewDirectory();
        var input = PhotoJpeg(dir);
        var half = await ImageCompressor.CompressAsync(input, Path.Combine(dir, "half.jpg"), new ImageCompressOptions { ReductionPercent = 50 });
        Assert.True(half.OutputBytes <= half.OriginalBytes / 2);

        var small = await ImageCompressor.CompressAsync(input, Path.Combine(dir, "small.jpg"), new ImageCompressOptions { Resize = ImageResize.LongEdge(200) });
        Assert.Equal((200, 150), (small.Width, small.Height));
        var pct = await ImageCompressor.CompressAsync(input, Path.Combine(dir, "pct.jpg"), new ImageCompressOptions { Resize = ImageResize.Percent(25) });
        Assert.Equal((200, 150), (pct.Width, pct.Height));
    }

    [Fact]
    public async Task PngBalancedIsLosslessAndStrongUsesPalette()
    {
        var dir = Fixtures.NewDirectory();
        var input = Path.Combine(dir, "photo.png");
        using (var image = Fixtures.Photo(400, 300))
        {
            image.Settings.SetDefine(MagickFormat.Png, "compression-level", "1");
            image.Write(input, MagickFormat.Png);
        }
        var balanced = await ImageCompressor.CompressAsync(input, Path.Combine(dir, "b.png"));
        var strong = await ImageCompressor.CompressAsync(input, Path.Combine(dir, "s.png"), new ImageCompressOptions { Preset = CompressionPreset.Strong });

        Assert.True(balanced.OutputBytes < balanced.OriginalBytes);
        using (var a = new MagickImage(input))
        using (var b = new MagickImage(balanced.OutputPath))
            Assert.Equal(0, a.Compare(b, ErrorMetric.Absolute));
        Assert.True(strong.OutputBytes < balanced.OutputBytes * 0.7, $"strong {strong.OutputBytes} vs balanced {balanced.OutputBytes}");
        using var s = new MagickImage(strong.OutputPath);
        Assert.True(s.TotalColors <= 256);
    }

    [Theory]
    [InlineData("webp")]
    [InlineData("heic")]
    [InlineData("avif")]
    [InlineData("tiff")]
    [InlineData("bmp")]
    public async Task CompressesOtherFormats(string id)
    {
        var dir = Fixtures.NewDirectory();
        var format = F.FromId(id)!;
        string input;
        using (var image = Fixtures.Photo(320, 240))
            input = Fixtures.Save(image, dir, format);
        var result = await ImageCompressor.CompressAsync(input, Path.Combine(dir, "out." + format.WriteExtension),
            new ImageCompressOptions { Preset = CompressionPreset.Strong });
        Assert.True(result.OutputBytes <= result.OriginalBytes);
        Assert.Equal(format, ImageFormats.Detect(result.OutputPath));
        using var back = new MagickImage(result.OutputPath);
        Assert.Equal(320u, back.Width);
    }
}

public sealed class BarcodeTests
{
    private static BgraBitmap Qr(string text, int size)
    {
        var writer = new BarcodeWriterPixelData { Format = BarcodeFormat.QR_CODE, Options = new EncodingOptions { Width = size, Height = size, Margin = 2 } };
        var data = writer.Write(text);
        return new BgraBitmap(data.Width, data.Height, data.Pixels);
    }

    [Fact]
    public async Task ReadsQrCodeRoundTrip()
    {
        var dir = Fixtures.NewDirectory();
        var path = Path.Combine(dir, "qr.png");
        await ImageSaver.SaveAsync(Qr("https://petal.example/q?id=42", 300), path, F.Png);
        var results = await BarcodeScanner.ScanAsync(path);
        var hit = Assert.Single(results);
        Assert.Equal("https://petal.example/q?id=42", hit.Text);
        Assert.Equal("QR_CODE", hit.Format);
        Assert.NotEmpty(hit.Points);
    }

    [Fact]
    public async Task FindsSmallQrInLargePhoto()
    {
        var dir = Fixtures.NewDirectory();
        var path = Path.Combine(dir, "scene.jpg");
        using (var scene = Fixtures.Photo(2600, 1800))
        using (var qr = ImageSaver.FromBgra(Qr("PETAL-123", 260)))
        {
            scene.Composite(qr, 1900, 1200, CompositeOperator.Over);
            scene.Quality = 92;
            scene.Write(path, MagickFormat.Jpeg);
        }
        var results = await BarcodeScanner.ScanAsync(path);
        Assert.Contains(results, r => r.Text == "PETAL-123");
        var point = results.First(r => r.Text == "PETAL-123").Points[0];
        Assert.InRange(point.X, 1850, 2200); // mapped back to full-resolution coordinates
    }

    [Fact]
    public async Task NoCodeReturnsEmpty()
    {
        var dir = Fixtures.NewDirectory();
        using var image = Fixtures.Quadrants();
        Assert.Empty(await BarcodeScanner.ScanAsync(Fixtures.Save(image, dir, F.Png)));
    }
}

public sealed class PdfCreatorTests
{
    [Fact]
    public async Task OnePagePerImageInOrder()
    {
        var dir = Fixtures.NewDirectory();
        using var image = Fixtures.Quadrants();
        var inputs = new[]
        {
            Fixtures.Save(image, dir, F.Jpg), Fixtures.Save(image, dir, F.Png), Fixtures.Save(image, dir, F.Svg),
            Fixtures.Save(image, dir, F.Heic), Fixtures.Save(image, dir, F.Webp),
        };
        var output = Path.Combine(dir, "album.pdf");
        int pages = await PdfCreator.CreateAsync(inputs, output, new PdfCreateOptions { Title = "Album ✓" });

        Assert.Equal(5, pages);
        Assert.Equal(5, Fixtures.CountPdfPages(output));
        var (count, first) = await Fixtures.RenderPdfAsync(output);
        using (first)
        {
            Assert.Equal(5u, count);
            Fixtures.AssertQuadrants(first, 20, "page 1");
        }
    }

    [Fact]
    public async Task PaperSizesFollowImageOrientation()
    {
        var dir = Fixtures.NewDirectory();
        using var wide = Fixtures.Quadrants(300, 200);
        using var tall = Fixtures.Quadrants(200, 300);
        var output = Path.Combine(dir, "a4.pdf");
        await PdfCreator.CreateAsync([Fixtures.Save(wide, dir, F.Png, "wide"), Fixtures.Save(tall, dir, F.Png, "tall")], output,
            new PdfCreateOptions { PageSize = PdfPageSize.A4 });
        var text = System.Text.Encoding.Latin1.GetString(File.ReadAllBytes(output));
        Assert.Contains("/MediaBox [0 0 841.89 595.276]", text);
        Assert.Contains("/MediaBox [0 0 595.276 841.89]", text);
    }

    [Fact]
    public async Task HonoursExifOrientation()
    {
        var dir = Fixtures.NewDirectory();
        var input = Path.Combine(dir, "rotated.jpg");
        using (var source = Fixtures.Quadrants())
        {
            var exif = new ExifProfile();
            exif.SetValue(ExifTag.Orientation, (ushort)6);
            source.SetProfile(exif);
            source.Orientation = Orientation.RightTop;
            source.Write(input, MagickFormat.Jpeg);
        }
        var output = Path.Combine(dir, "rotated.pdf");
        await PdfCreator.CreateAsync([input], output);
        Assert.Contains("/MediaBox [0 0 48 64]", System.Text.Encoding.Latin1.GetString(File.ReadAllBytes(output)));
    }
}

public sealed class RedactTests
{
    [Fact]
    public async Task SolidAndBlurChangePixels()
    {
        var dir = Fixtures.NewDirectory();
        var input = Path.Combine(dir, "photo.jpg");
        using (var photo = Fixtures.Photo(400, 300))
        {
            var exif = new ExifProfile();
            exif.SetValue(ExifTag.Make, "PetalCam");
            photo.SetProfile(exif);
            photo.Quality = 95;
            photo.Write(input, MagickFormat.Jpeg);
        }
        var output = Path.Combine(dir, "redacted.png");
        await ImageRedactor.RedactAsync(input, output,
        [
            new RedactionRegion(new PixelRect(10, 10, 100, 80), RedactionStyle.Solid, 0xFF102030),
            new RedactionRegion(new PixelRect(200, 100, 150, 150), RedactionStyle.Blur),
        ]);

        using var original = new MagickImage(input);
        using var result = new MagickImage(output);
        Fixtures.AssertColor(new MagickColor(0x10, 0x20, 0x30), Fixtures.PixelAt(result, 50, 50), 0, "solid");
        using var before = (MagickImage)original.CloneArea(200, 100, 150, 150);
        using var after = (MagickImage)result.CloneArea(200, 100, 150, 150);
        Assert.True(before.Compare(after, ErrorMetric.RootMeanSquared) > 0.01, "blur changed the area");
        // Detail is gone: the blurred area is much smoother than the original.
        Assert.True(Roughness(after) < Roughness(before) * 0.5, $"{Roughness(after)} vs {Roughness(before)}");
        // Outside the regions nothing changes beyond JPEG→PNG.
        Fixtures.AssertColor(Fixtures.PixelAt(original, 380, 20), Fixtures.PixelAt(result, 380, 20), 0, "untouched");
        Assert.Equal("PetalCam", result.GetExifProfile()?.GetValue(ExifTag.Make)?.Value);
    }

    [Fact]
    public async Task RedactionDropsExifThumbnail()
    {
        var dir = Fixtures.NewDirectory();
        var input = Path.Combine(dir, "thumb.jpg");
        using (var photo = Fixtures.Photo(400, 300))
        {
            using var thumb = (MagickImage)photo.Clone();
            thumb.Resize(160, 120);
            photo.SetProfile(Fixtures.ExifWithThumbnail(thumb.ToByteArray(MagickFormat.Jpeg), "PetalCam"));
            photo.Write(input, MagickFormat.Jpeg);
        }
        using (var check = new MagickImage(input))
            Assert.True(check.GetExifProfile()!.ThumbnailLength > 0, "fixture should carry a thumbnail");

        var output = Path.Combine(dir, "redacted.jpg");
        await ImageRedactor.RedactAsync(input, output, [new RedactionRegion(new PixelRect(0, 0, 50, 50))]);
        using var result = new MagickImage(output);
        Assert.Equal("PetalCam", result.GetExifProfile()?.GetValue(ExifTag.Make)?.Value);
        Assert.Equal(0u, result.GetExifProfile()?.ThumbnailLength ?? 0u);
    }

    [Fact]
    public async Task FaceDetectionRunsLocally()
    {
        var dir = Fixtures.NewDirectory();
        using var photo = Fixtures.Photo(640, 480);
        var faces = await ImageRedactor.DetectFacesAsync(Fixtures.Save(photo, dir, F.Jpg));
        Assert.NotNull(faces); // no synthetic face is feasible; the call must succeed and return a list
    }

    [Fact]
    public async Task FindsRenderedText()
    {
        var dir = Fixtures.NewDirectory();
        var path = Path.Combine(dir, "text.png");
        using (var label = new MagickImage("label:PETAL REDACT 2024", new MagickReadSettings
        {
            FontPointsize = 96,
            Font = "Arial",
            BackgroundColor = MagickColors.White,
            FillColor = MagickColors.Black,
        }))
        {
            label.BorderColor = MagickColors.White;
            label.Border(60);
            label.Write(path, MagickFormat.Png);
        }
        var text = await ImageRedactor.DetectTextAsync(path);
        Assert.NotEmpty(text);
        var line = text.First();
        Assert.Contains("REDACT", string.Join(" ", text.Select(t => t.Text)), StringComparison.OrdinalIgnoreCase);
        Assert.True(line.Bounds.X >= 40 && line.Bounds.Y >= 40, $"bounds {line.Bounds}");

        var words = await ImageRedactor.DetectTextAsync(path, TextGranularity.Words);
        Assert.True(words.Count >= 3);
    }

    private static double Roughness(MagickImage image)
    {
        using var copy = (MagickImage)image.Clone();
        copy.Grayscale();
        var px = copy.GetPixels().ToByteArray("R")!;
        int w = (int)copy.Width;
        double sum = 0;
        for (int i = 1; i < px.Length; i++)
            if (i % w != 0)
                sum += Math.Abs(px[i] - px[i - 1]);
        return sum / px.Length;
    }
}

public sealed class InfoPreviewSaveTests
{
    [Fact]
    public void ReadsInfoWithOrientation()
    {
        var dir = Fixtures.NewDirectory();
        var input = Path.Combine(dir, "rotated.jpg");
        using (var source = Fixtures.Quadrants())
        {
            var exif = new ExifProfile();
            exif.SetValue(ExifTag.Orientation, (ushort)6);
            source.SetProfile(exif);
            source.Orientation = Orientation.RightTop;
            source.Density = new Density(300, DensityUnit.PixelsPerInch);
            source.Write(input, MagickFormat.Jpeg);
        }
        var info = ImageInspector.Read(input);
        Assert.Equal(F.Jpg, info.Format);
        Assert.Equal((64, 48), (info.Width, info.Height));
        Assert.Equal(ImageOrientation.Rotate90, info.Orientation);
        Assert.Equal((48, 64), (info.DisplayWidth, info.DisplayHeight));
        Assert.Equal(300, info.DpiX, 0);
        Assert.False(info.HasAlpha);
        Assert.Equal(new FileInfo(input).Length, info.FileSize);
    }

    [Fact]
    public void ReadsIconAndAlphaInfo()
    {
        var dir = Fixtures.NewDirectory();
        using var image = Fixtures.Quadrants(transparentBand: true);
        var ico = Fixtures.Save(image, dir, F.Ico);
        var info = ImageInspector.Read(ico);
        Assert.Equal(F.Ico, info.Format);
        Assert.Equal(64, info.Width);
        Assert.True(info.FrameCount >= 3);
        Assert.True(ImageInspector.Read(Fixtures.Save(image, dir, F.Png)).HasAlpha);
    }

    [Theory]
    [InlineData("heic")]
    [InlineData("avif")]
    [InlineData("svg")]
    [InlineData("ico")]
    [InlineData("jpg")]
    public async Task PreviewDecodesAnyFormat(string id)
    {
        var dir = Fixtures.NewDirectory();
        using var image = Fixtures.Quadrants(640, 480);
        var path = Fixtures.Save(image, dir, F.FromId(id)!);
        var bitmap = await ImagePreview.DecodeBgraAsync(path, 200);
        Assert.True(bitmap.Width <= 200 && bitmap.Height <= 200, $"{bitmap.Width}×{bitmap.Height}");
        Assert.True(Math.Max(bitmap.Width, bitmap.Height) >= (id == "svg" ? 200 : 64));
        Assert.Equal(bitmap.Width * bitmap.Height * 4, bitmap.Pixels.Length);
        var png = await ImagePreview.DecodePngAsync(path, 120);
        Assert.Equal(F.Png, ImageFormats.Sniff(png));
    }

    [Fact]
    public async Task SavesBgraWithCopiedMetadata()
    {
        var dir = Fixtures.NewDirectory();
        var donor = Path.Combine(dir, "donor.jpg");
        using (var source = Fixtures.Quadrants())
        {
            var exif = new ExifProfile();
            exif.SetValue(ExifTag.Model, "Donor Model");
            exif.SetValue(ExifTag.Orientation, (ushort)6);
            source.SetProfile(exif);
            source.Orientation = Orientation.RightTop;
            source.Write(donor, MagickFormat.Jpeg);
        }
        var pixels = new byte[48 * 64 * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 255; pixels[i + 1] = 0; pixels[i + 2] = 0; pixels[i + 3] = 255; // blue
        }
        var output = Path.Combine(dir, "composed.jpg");
        await ImageSaver.SaveAsync(new BgraBitmap(48, 64, pixels), output, F.Jpg, new ImageSaveOptions { MetadataSourcePath = donor });

        using var result = new MagickImage(output);
        Assert.Equal("Donor Model", result.GetExifProfile()?.GetValue(ExifTag.Model)?.Value);
        Assert.True(result.Orientation is Orientation.TopLeft or Orientation.Undefined, "the composed pixels are already upright");
        Fixtures.AssertColor(MagickColors.Blue, Fixtures.PixelAt(result, 10, 10), 4, "blue");

        var transparent = new BgraBitmap(10, 10, new byte[400]);
        var png = Path.Combine(dir, "clear.png");
        await ImageSaver.SaveAsync(transparent, png, F.Png);
        using var clear = new MagickImage(png);
        Assert.True(clear.HasAlpha);
    }
}
