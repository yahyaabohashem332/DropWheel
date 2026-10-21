using Petal.Core.Formats;
using ImageMagick;
using Petal.Core.Jobs;
using Xunit.Abstractions;
using F = Petal.Core.Formats.Formats;

namespace Petal.Engines.Imaging.Tests;

public sealed class RawTests(ITestOutputHelper output)
{
    /// <summary>Left half red, right half blue, as a camera sensor would record it.</summary>
    private static string Write(string name, ushort orientation = 1)
    {
        var path = Path.Combine(Fixtures.NewDirectory(), name);
        File.WriteAllBytes(path, DngWriter.Write(96, 64, (x, _) => x < 48 ? (0.7, 0.05, 0.05) : (0.05, 0.05, 0.7), orientation));
        return path;
    }

    [Fact]
    public void A_RAW_file_is_recognised_by_its_extension_not_its_TIFF_insides()
    {
        var path = Write("IMG_0001.DNG");
        Assert.Equal(F.Raw, F.FromPath(path));
        Assert.Equal(F.Raw, ImageFormats.Detect(path));
        // A camera JPEG named like a RAW is still a JPEG.
        var jpeg = Path.Combine(Fixtures.NewDirectory(), "wrong.nef");
        using (var img = new MagickImage(MagickColors.Red, 8, 8))
            img.Write(jpeg, MagickFormat.Jpeg);
        Assert.Equal(F.Jpg, ImageFormats.Detect(jpeg));
    }

    [Fact]
    public void The_sensor_picture_is_developed_with_its_colours()
    {
        using var image = ImageLoader.Load(Write("colours.dng"));
        output.WriteLine($"{image.Width}x{image.Height}");
        Assert.InRange((int)image.Width, 90, 96);
        Assert.InRange((int)image.Height, 60, 64);
        using var pixels = image.GetPixels();
        var left = pixels.GetPixel(20, 30).ToColor()!;
        var right = pixels.GetPixel((int)image.Width - 20, 30).ToColor()!;
        output.WriteLine($"left {left.R},{left.G},{left.B}  right {right.R},{right.G},{right.B}");
        Assert.True(left.R > 150 && left.G < 90 && left.B < 90, $"left {left.R},{left.G},{left.B}");
        Assert.True(right.B > 150 && right.R < 90 && right.G < 90, $"right {right.R},{right.G},{right.B}");
    }

    [Fact]
    public void A_portrait_photo_comes_out_upright()
    {
        // Orientation 6: the camera was turned 90° clockwise; the left of the sensor is the top of the scene.
        using var image = ImageLoader.Load(Write("portrait.dng", orientation: 6));
        output.WriteLine($"{image.Width}x{image.Height}");
        Assert.True(image.Height > image.Width, $"{image.Width}x{image.Height}");
        using var pixels = image.GetPixels();
        var top = pixels.GetPixel((int)image.Width / 2, 15).ToColor()!;
        var bottom = pixels.GetPixel((int)image.Width / 2, (int)image.Height - 15).ToColor()!;
        Assert.True(top.R > 150 && top.B < 90, $"top {top.R},{top.G},{top.B}");
        Assert.True(bottom.B > 150 && bottom.R < 90, $"bottom {bottom.R},{bottom.G},{bottom.B}");
    }

    [Theory]
    [InlineData("jpg")]
    [InlineData("tiff")]
    [InlineData("png")]
    public async Task Converting_keeps_the_camera_details(string to)
    {
        var input = Write("camera.dng", orientation: 6);
        var target = F.FromId(to)!;
        var outputPath = Path.Combine(Fixtures.NewDirectory(), "camera." + target.WriteExtension);
        await new ImageConverter().ConvertAsync(new ConversionJob(input, F.Raw, target, outputPath, new ConversionSettings()), null, CancellationToken.None);
        // ImageMagick keeps a TIFF's EXIF only as loose properties; Petal's loader rebuilds it.
        using var image = target == F.Tiff ? ImageLoader.Load(outputPath) : new MagickImage(outputPath);
        Assert.True(image.Height > image.Width);
        var exif = image.GetExifProfile();
        Assert.NotNull(exif);
        Assert.Equal("Test Camera", exif.GetValue(ExifTag.Model)?.Value);
        Assert.Equal("2024:05:06 07:08:09", exif.GetValue(ExifTag.DateTimeOriginal)?.Value);
        Assert.Equal((ushort)200, exif.GetValue(ExifTag.ISOSpeedRatings)?.Value?.FirstOrDefault());
        // Already upright: viewers must not turn it again.
        var orientation = exif.GetValue(ExifTag.Orientation)?.Value;
        Assert.True(orientation is null or 1, $"orientation {orientation}");
    }

    [Fact]
    public async Task Every_RAW_target_writes_a_valid_picture()
    {
        var input = Write("all.dng");
        foreach (var target in ConversionMatrix.TargetsFor(F.Raw))
        {
            var outputPath = Path.Combine(Fixtures.NewDirectory(), "all." + target.WriteExtension);
            await new ImageConverter().ConvertAsync(new ConversionJob(input, F.Raw, target, outputPath, new ConversionSettings()), null, CancellationToken.None);
            Assert.Equal(target, ImageFormats.Detect(outputPath));
            if (target != F.Pdf)
            {
                using var image = new MagickImage(outputPath);
                Assert.InRange((int)image.Width, 90, 96);
            }
        }
    }

    [Fact]
    public async Task Camera_details_survive_in_folders_with_Arabic_names()
    {
        var folder = Path.Combine(Fixtures.NewDirectory(), "صور الرحلة");
        Directory.CreateDirectory(folder);
        var input = Path.Combine(folder, "لقطة.dng");
        File.Copy(Write("camera.dng"), input);
        var outputPath = Path.Combine(folder, "لقطة.jpg");
        await new ImageConverter().ConvertAsync(new ConversionJob(input, F.Raw, F.Jpg, outputPath, new ConversionSettings()), null, CancellationToken.None);
        using var image = new MagickImage(outputPath);
        Assert.Equal("Test Camera", image.GetExifProfile()?.GetValue(ExifTag.Model)?.Value);
    }

    [Fact]
    public void The_cameras_thumbnail_is_used_for_quick_looks_when_there_is_one()
    {
        // This test file has no embedded preview: the quick path must still decode the sensor picture.
        using var image = ImageLoader.Load(Write("quick.dng"), new LoadOptions { DecodeSizeHint = 256 });
        Assert.True(image.Width > 0);
    }
}
