using ImageMagick;
using Petal.Engines.Imaging.Heif;
using F = Petal.Core.Formats.Formats;

namespace Petal.Engines.Imaging.Tests;

public sealed class HeicTests
{
    [Fact]
    public void FfmpegHevcIsAvailable() => Assert.True(HeifWriter.IsAvailable, "FFmpeg with libx265 is required for HEIC output");

    [Theory]
    [InlineData(63, 47)]   // odd: padded and cropped with 'clap'
    [InlineData(2, 2)]
    [InlineData(1, 1)]
    public async Task OddAndTinySizesRoundTrip(int width, int height)
    {
        var dir = Fixtures.NewDirectory();
        using var image = new MagickImage(new MagickColor(30, 120, 220), (uint)width, (uint)height);
        var path = Path.Combine(dir, "odd.heic");
        await ImageWriter.WriteAsync(image, path, F.Heic, ImageWriteOptions.Default, default);

        using var back = new MagickImage(path);
        Assert.Equal((uint)width, back.Width);
        Assert.Equal((uint)height, back.Height);
        Fixtures.AssertColor(new MagickColor(30, 120, 220), Fixtures.PixelAt(back, width / 2, height / 2), 6, "colour");
    }

    [Fact]
    public async Task LargeImagesBecomeAGridOfTiles()
    {
        var dir = Fixtures.NewDirectory();
        using var image = Fixtures.Quadrants(3201, 2901); // 9.3 MP, above the single-image limit, odd sizes
        var path = Path.Combine(dir, "grid.heic");
        await ImageWriter.WriteAsync(image, path, F.Heic, ImageWriteOptions.Default, default);

        var bytes = File.ReadAllBytes(path);
        Assert.True(bytes.AsSpan().IndexOf("grid"u8) > 0, "expected a grid item");
        using var back = new MagickImage(path);
        Assert.Equal(3201u, back.Width);
        Assert.Equal(2901u, back.Height);
        Fixtures.AssertQuadrants(back, 8, "grid");
        // A pixel right at a tile seam must match too.
        Fixtures.AssertColor(Fixtures.Red, Fixtures.PixelAt(back, 512, 512), 10, "seam");
    }

    [Fact]
    public async Task KeepsIccAndExif()
    {
        var dir = Fixtures.NewDirectory();
        using var image = Fixtures.Quadrants();
        var exif = new ExifProfile();
        exif.SetValue(ExifTag.Model, "Heic Model");
        image.SetProfile(exif);
        image.SetProfile(ColorProfiles.AdobeRGB1998);
        var path = Path.Combine(dir, "meta.heic");
        await ImageWriter.WriteAsync(image, path, F.Heic, ImageWriteOptions.Default, default);

        using var back = new MagickImage(path);
        Assert.Equal("Heic Model", back.GetExifProfile()?.GetValue(ExifTag.Model)?.Value);
        Assert.Contains("Adobe", back.GetColorProfile()?.Description ?? "");
    }

    /// <summary>
    /// iPhone HEICs store rotation as an 'irot' property and repeat it in EXIF. The decoder applies
    /// irot; the loader must not rotate a second time because of the EXIF tag.
    /// </summary>
    [Fact]
    public async Task IrotIsAppliedOnceEvenWithExifOrientation()
    {
        var dir = Fixtures.NewDirectory();
        // Graphics cards' HEVC encoders take nothing much smaller.
        using var image = Fixtures.Quadrants(256, 192);
        var rgb = image.GetPixels().ToByteArray(PixelMapping.RGB)!;
        var annexB = await FfmpegStillEncoder.EncodeHevcAsync(new MemoryStream(rgb), 256, 192, 90, "fast", true, default);
        var stream = HevcBitstream.Parse(annexB);
        var exif = new ExifProfile();
        exif.SetValue(ExifTag.Orientation, (ushort)6);
        var path = Path.Combine(dir, "iphone-like.heic");
        await using (var fs = File.Create(path))
        {
            HeifMuxer.Write(fs, new HeifImageSpec
            {
                Width = 256, Height = 192, CodedWidth = 256, CodedHeight = 192,
                HvcC = stream.BuildHvcC(), Pictures = stream.Pictures,
                Exif = [.. "Exif\0\0"u8, .. StripExifHeader(exif.ToByteArray()!)],
                RotationQuarterTurns = 3, // 270° counter-clockwise = 90° clockwise, as iPhones write it
            });
        }

        var info = ImageInspector.Read(path);
        Assert.Equal((192, 256), (info.DisplayWidth, info.DisplayHeight));
        var preview = await ImagePreview.DecodeBgraAsync(path, 1000);
        Assert.Equal((192, 256), (preview.Width, preview.Height));

        using var loaded = ImageLoader.Load(path);
        Assert.Equal(192u, loaded.Width);
        Assert.Equal(256u, loaded.Height);
        // Rotated 90° clockwise once: the red top-left quadrant ends up top-right.
        Fixtures.AssertColor(Fixtures.Red, Fixtures.PixelAt(loaded, 144, 64), 12, "TR");
        Fixtures.AssertColor(Fixtures.Blue, Fixtures.PixelAt(loaded, 48, 64), 12, "TL");
    }

    [Fact]
    public async Task FfmpegAvifFallbackKeepsAlpha()
    {
        Assert.True(FfmpegStillEncoder.CanEncodeAvif);
        using var image = Fixtures.Quadrants(transparentBand: true);
        using var ms = new MemoryStream();
        await FfmpegStillEncoder.EncodeAvifAsync(image, ms, 90, default);
        using var back = new MagickImage(ms.ToArray());
        Assert.Equal(64u, back.Width);
        Assert.True(back.HasAlpha);
        Assert.True(Fixtures.PixelAt(back, 10, 24).A < 20, "band should stay transparent");
        Fixtures.AssertQuadrants(back, 16, "avif fallback");
    }

    [Fact]
    public async Task HvcCMatchesSpsFields()
    {
        var rgb = new byte[256 * 192 * 3];
        var annexB = await FfmpegStillEncoder.EncodeHevcAsync(new MemoryStream(rgb), 256, 192, 80, "fast", true, default);
        var stream = HevcBitstream.Parse(annexB);
        var info = new HevcBitstream.SpsInfo(stream.Sps);
        Assert.Equal(1, info.ChromaFormatIdc);
        Assert.Equal(0, info.BitDepthLumaMinus8);
        Assert.Equal(256, info.Width);
        Assert.Equal(192, info.Height);
        Assert.Single(stream.Pictures);
        var hvcC = stream.BuildHvcC();
        Assert.Equal(1, hvcC[0]);
        Assert.Equal(info.GeneralProfileTierLevel, hvcC[1..13]);
        Assert.Equal(3, hvcC[22]); // VPS, SPS and PPS arrays
    }

    private static byte[] StripExifHeader(byte[] bytes) =>
        bytes.AsSpan().StartsWith("Exif\0\0"u8) ? bytes[6..] : bytes;
}
