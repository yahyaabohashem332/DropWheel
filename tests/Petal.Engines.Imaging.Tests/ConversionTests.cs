using ImageMagick;
using Petal.Core.Formats;
using Petal.Core.Jobs;
using F = Petal.Core.Formats.Formats;

namespace Petal.Engines.Imaging.Tests;

/// <summary>One small sample of every readable image format, shared by the conversion theory.</summary>
public sealed class ConversionSamples : IDisposable
{
    public ConversionSamples()
    {
        Directory = Fixtures.NewDirectory();
        using var image = Fixtures.Quadrants();
        foreach (var format in F.RasterImages.Append(F.Svg))
            Sources[format] = Fixtures.Save(image, Directory, format);
    }

    public string Directory { get; }

    public Dictionary<FileFormat, string> Sources { get; } = [];

    public void Dispose()
    {
        try { System.IO.Directory.Delete(Directory, true); } catch (IOException) { }
    }
}

public sealed class ConversionTests(ConversionSamples samples) : IClassFixture<ConversionSamples>
{
    private readonly ImageConverter _converter = new();

    public static TheoryData<string, string> ImagePaths()
    {
        var data = new TheoryData<string, string>();
        foreach (var (from, to) in ConversionMatrix.AllPaths().Where(p => p.From.Family is FormatFamily.Image or FormatFamily.Vector))
            data.Add(from.Id, to.Id);
        return data;
    }

    [Fact]
    public void MatrixHasEveryImagePath() => Assert.Equal(8 * 8 + 9, ImagePaths().Count);

    [Theory]
    [MemberData(nameof(ImagePaths))]
    public async Task ConvertsEveryPath(string fromId, string toId)
    {
        var from = F.FromId(fromId)!;
        var to = F.FromId(toId)!;
        Assert.True(_converter.CanConvert(from, to), $"{from} → {to} reported unsupported");

        var output = Path.Combine(samples.Directory, $"{fromId}-to-{toId}.{to.WriteExtension}");
        var progress = new List<double>();
        var result = await _converter.ConvertAsync(
            new ConversionJob(samples.Sources[from], from, to, output, ConversionSettings.Default),
            new Progress<double>(progress.Add), CancellationToken.None);

        Assert.Equal(output, result.Primary);
        Assert.True(File.Exists(output));
        Assert.Equal(to, ImageFormats.Detect(output));
        Assert.Empty(Directory.GetFiles(samples.Directory, "*.petal-tmp"));

        int tolerance = 4 + (ImageFormats.IsLossy(from) ? 12 : 0) + (ImageFormats.IsLossy(to) ? 12 : 0);
        string context = $"{from}→{to}";
        if (to == F.Pdf)
        {
            Assert.Equal(1, Fixtures.CountPdfPages(output));
            var (pages, rendered) = await Fixtures.RenderPdfAsync(output);
            using (rendered)
            {
                Assert.Equal(1u, pages);
                Fixtures.AssertQuadrants(rendered, tolerance + 6, context);
            }
            return;
        }

        if (to == F.Ico)
        {
            using var frames = new MagickImageCollection(output);
            Assert.True(frames.Count >= 3, $"{context}: icon has {frames.Count} sizes");
            var largest = frames.OrderByDescending(f => f.Width).First();
            Assert.Equal(largest.Width, largest.Height);
            Assert.All(frames, f => Assert.Equal(f.Width, f.Height));
            return;
        }

        using var image = new MagickImage(output);
        if (from == F.Svg)
        {
            Assert.True(image.Width > 64 && image.Height > 48, $"{context}: SVG rendered too small ({image.Width}×{image.Height})");
            Assert.Equal(4.0 / 3, (double)image.Width / image.Height, 2);
        }
        else if (from == F.Ico)
        {
            // The icon master is the 64 px square frame (the 64×48 sample padded with transparency).
            Assert.Equal(64u, image.Width);
            Assert.Equal(64u, image.Height);
            return;
        }
        else
        {
            Assert.Equal(64u, image.Width);
            Assert.Equal(48u, image.Height);
        }
        Fixtures.AssertQuadrants(image, tolerance, context);
    }

    [Theory]
    [InlineData("png")]
    [InlineData("webp")]
    [InlineData("heic")]
    [InlineData("tiff")]
    [InlineData("avif")]
    [InlineData("jpg")]
    public async Task AppliesExifOrientation(string toId)
    {
        var dir = Fixtures.NewDirectory();
        using (var source = Fixtures.Quadrants())
        {
            // EXIF 6: display rotated 90° clockwise. ImageMagick writes the tag only into an existing EXIF profile.
            var exif = new ExifProfile();
            exif.SetValue(ExifTag.Orientation, (ushort)6);
            source.SetProfile(exif);
            source.Orientation = Orientation.RightTop;
            source.Quality = 95;
            source.Write(Path.Combine(dir, "rotated.jpg"), MagickFormat.Jpeg);
        }
        var to = F.FromId(toId)!;
        var output = Path.Combine(dir, "out." + to.WriteExtension);
        await _converter.ConvertAsync(new ConversionJob(Path.Combine(dir, "rotated.jpg"), F.Jpg, to, output, ConversionSettings.Default), null, default);

        using var image = new MagickImage(output);
        Assert.Equal(48u, image.Width);
        Assert.Equal(64u, image.Height);
        Assert.True(image.Orientation is Orientation.TopLeft or Orientation.Undefined, $"orientation is {image.Orientation}");
        int w = (int)image.Width, h = (int)image.Height;
        Fixtures.AssertColor(Fixtures.Blue, Fixtures.PixelAt(image, w / 4, h / 4), 30, "TL");
        Fixtures.AssertColor(Fixtures.Red, Fixtures.PixelAt(image, 3 * w / 4, h / 4), 30, "TR");
        Fixtures.AssertColor(Fixtures.Light, Fixtures.PixelAt(image, w / 4, 3 * h / 4), 30, "BL");
        Fixtures.AssertColor(Fixtures.Green, Fixtures.PixelAt(image, 3 * w / 4, 3 * h / 4), 30, "BR");
    }

    [Theory]
    [InlineData("jpg")]
    [InlineData("bmp")]
    [InlineData("heic")]
    public async Task FlattensTransparencyOnWhite(string toId)
    {
        var dir = Fixtures.NewDirectory();
        string source;
        using (var image = Fixtures.Quadrants(transparentBand: true))
            source = Fixtures.Save(image, dir, F.Png);
        var to = F.FromId(toId)!;
        var output = Path.Combine(dir, "flat." + to.WriteExtension);
        await _converter.ConvertAsync(new ConversionJob(source, F.Png, to, output, ConversionSettings.Default), null, default);

        using var result = new MagickImage(output);
        Assert.False(result.HasAlpha);
        Fixtures.AssertColor(MagickColors.White, Fixtures.PixelAt(result, 10, 24), 12, "band");
    }

    [Fact]
    public async Task PdfFromTransparentPngIsWhiteBehind()
    {
        var dir = Fixtures.NewDirectory();
        string source;
        using (var image = Fixtures.Quadrants(transparentBand: true))
            source = Fixtures.Save(image, dir, F.Png);
        var output = Path.Combine(dir, "flat.pdf");
        await _converter.ConvertAsync(new ConversionJob(source, F.Png, F.Pdf, output, ConversionSettings.Default), null, default);
        var (_, page) = await Fixtures.RenderPdfAsync(output);
        using (page)
            Fixtures.AssertColor(MagickColors.White, Fixtures.PixelAt(page, (int)page.Width / 6, (int)page.Height / 2), 10, "band");
    }

    [Theory]
    [InlineData("png")]
    [InlineData("webp")]
    [InlineData("tiff")]
    [InlineData("avif")]
    [InlineData("heic")]
    [InlineData("jpg")]
    public async Task KeepsExifAndColorProfile(string toId)
    {
        var dir = Fixtures.NewDirectory();
        var source = Path.Combine(dir, "tagged.jpg");
        using (var image = Fixtures.Quadrants())
        {
            var exif = new ExifProfile();
            exif.SetValue(ExifTag.Make, "PetalCam");
            exif.SetValue(ExifTag.Model, "Model 7");
            image.SetProfile(exif);
            image.SetProfile(ColorProfiles.AdobeRGB1998);
            image.Write(source, MagickFormat.Jpeg);
        }
        var to = F.FromId(toId)!;
        var output = Path.Combine(dir, "out." + to.WriteExtension);
        await _converter.ConvertAsync(new ConversionJob(source, F.Jpg, to, output, ConversionSettings.Default), null, default);

        using var result = ImageLoader.Load(output); // the loader also recovers EXIF from TIFF directories
        var exifOut = result.GetExifProfile();
        Assert.Equal("PetalCam", exifOut?.GetValue(ExifTag.Make)?.Value);
        Assert.Equal("Model 7", exifOut?.GetValue(ExifTag.Model)?.Value);
        var icc = result.GetColorProfile();
        Assert.NotNull(icc);
        Assert.Contains("Adobe", icc!.Description ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TiffExifSurvivesBothWays()
    {
        var dir = Fixtures.NewDirectory();
        var source = Path.Combine(dir, "camera.jpg");
        using (var image = Fixtures.Quadrants())
        {
            var exif = new ExifProfile();
            exif.SetValue(ExifTag.Make, "TiffCam");
            exif.SetValue(ExifTag.ExposureTime, new Rational(1, 250));
            exif.SetValue(ExifTag.GPSLatitudeRef, "S");
            exif.SetValue(ExifTag.GPSLatitude, [new Rational(33, 1), new Rational(51, 1), new Rational(0, 1)]);
            image.SetProfile(exif);
            image.Write(source, MagickFormat.Jpeg);
        }
        var tiff = Path.Combine(dir, "camera.tiff");
        await _converter.ConvertAsync(new ConversionJob(source, F.Jpg, F.Tiff, tiff, ConversionSettings.Default), null, default);
        var back = Path.Combine(dir, "back.jpg");
        await _converter.ConvertAsync(new ConversionJob(tiff, F.Tiff, F.Jpg, back, ConversionSettings.Default), null, default);

        using var image2 = new MagickImage(back);
        var e = image2.GetExifProfile()!;
        Assert.Equal("TiffCam", e.GetValue(ExifTag.Make)?.Value);
        Assert.Equal(new Rational(1, 250), e.GetValue(ExifTag.ExposureTime)?.Value);
        Assert.Equal("S", e.GetValue(ExifTag.GPSLatitudeRef)?.Value);
        using var decoded = new MagickImage(tiff);
        Fixtures.AssertQuadrants(decoded, 2, "tiff");

        // Windows' own TIFF decoder (WIC) is an independent reader of the spliced directory.
        var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(tiff);
        using var stream = await file.OpenReadAsync();
        var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream);
        Assert.Equal(64u, decoder.PixelWidth);
        var props = await decoder.BitmapProperties.GetPropertiesAsync(["/ifd/{ushort=271}", "/ifd/exif/{ushort=33434}", "/ifd/gps/{ushort=1}"]);
        Assert.Equal("TiffCam", props["/ifd/{ushort=271}"].Value as string);
        Assert.True(props.ContainsKey("/ifd/exif/{ushort=33434}"), "exposure time in the EXIF sub-IFD");
        Assert.Equal("S", props["/ifd/gps/{ushort=1}"].Value as string);
    }

    [Fact]
    public async Task PdfEmbedsJpegWithoutRecompression()
    {
        var dir = Fixtures.NewDirectory();
        var source = Path.Combine(dir, "photo.jpg");
        using (var image = Fixtures.Photo(320, 240))
        {
            image.Quality = 80;
            image.Write(source, MagickFormat.Jpeg);
        }
        var output = Path.Combine(dir, "photo.pdf");
        await _converter.ConvertAsync(new ConversionJob(source, F.Jpg, F.Pdf, output, ConversionSettings.Default), null, default);

        var jpeg = File.ReadAllBytes(source);
        var pdf = File.ReadAllBytes(output);
        Assert.True(pdf.AsSpan().IndexOf(jpeg) > 0, "original JPEG bytes should be embedded as-is");
        Assert.Contains("/DCTDecode", System.Text.Encoding.Latin1.GetString(pdf));
    }

    [Fact]
    public async Task IconSourceUsesLargestFrame()
    {
        var dir = Fixtures.NewDirectory();
        var ico = Path.Combine(dir, "multi.ico");
        using (var frames = new MagickImageCollection())
        {
            foreach (var (size, color) in new[] { (16, MagickColors.Red), (48, MagickColors.Blue), (32, MagickColors.Lime) })
                frames.Add(new MagickImage(color, (uint)size, (uint)size));
            frames.Write(ico, MagickFormat.Ico);
        }
        var output = Path.Combine(dir, "multi.png");
        await _converter.ConvertAsync(new ConversionJob(ico, F.Ico, F.Png, output, ConversionSettings.Default), null, default);
        using var image = new MagickImage(output);
        Assert.Equal(48u, image.Width);
        Fixtures.AssertColor(MagickColors.Blue, Fixtures.PixelAt(image, 24, 24), 2, "largest frame");
    }

    [Fact]
    public async Task MultiPageTiffAndAnimatedWebpUseFirstFrame()
    {
        var dir = Fixtures.NewDirectory();
        foreach (var (format, ext) in new[] { (MagickFormat.Tiff, "tiff"), (MagickFormat.WebP, "webp") })
        {
            var path = Path.Combine(dir, "pages." + ext);
            using (var frames = new MagickImageCollection())
            {
                frames.Add(new MagickImage(MagickColors.Red, 40, 30));
                frames.Add(new MagickImage(MagickColors.Blue, 40, 30));
                if (format == MagickFormat.WebP)
                    foreach (var frame in frames) frame.AnimationDelay = 50;
                frames.Write(path, format);
            }
            var from = F.FromPath(path)!;
            var output = Path.Combine(dir, $"first-{ext}.png");
            await _converter.ConvertAsync(new ConversionJob(path, from, F.Png, output, ConversionSettings.Default), null, default);
            using var image = new MagickImage(output);
            Fixtures.AssertColor(MagickColors.Red, Fixtures.PixelAt(image, 20, 15), 8, ext);
        }
    }

    [Fact]
    public async Task IconOutputHasStandardSizes()
    {
        var dir = Fixtures.NewDirectory();
        string source;
        using (var image = Fixtures.Photo(600, 400))
            source = Fixtures.Save(image, dir, F.Png);
        var output = Path.Combine(dir, "app.ico");
        await _converter.ConvertAsync(new ConversionJob(source, F.Png, F.Ico, output, ConversionSettings.Default), null, default);
        using var frames = new MagickImageCollection(output);
        Assert.Equal(new uint[] { 16, 24, 32, 48, 64, 128, 256 }, frames.Select(f => f.Width).Order().ToArray());
    }

    [Fact]
    public async Task CancellationLeavesNoFile()
    {
        var dir = Fixtures.NewDirectory();
        var output = Path.Combine(dir, "never.png");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _converter.ConvertAsync(new ConversionJob(samples.Sources[F.Jpg], F.Jpg, F.Png, output, ConversionSettings.Default), null, cts.Token));
        Assert.False(File.Exists(output));
    }

    [Fact]
    public async Task DetectsFormatFromContentNotExtension()
    {
        var dir = Fixtures.NewDirectory();
        var liar = Path.Combine(dir, "really-a-png.jpg");
        File.Copy(samples.Sources[F.Png], liar);
        var output = Path.Combine(dir, "fixed.webp");
        await _converter.ConvertAsync(new ConversionJob(liar, F.Jpg, F.Webp, output, ConversionSettings.Default), null, default);
        using var image = new MagickImage(output);
        Fixtures.AssertQuadrants(image, 14, "png named jpg");
    }
}

