using System.Diagnostics;
using ImageMagick;
using Petal.Engines.Imaging.Editing;
using Xunit.Abstractions;
using F = Petal.Core.Formats.Formats;

namespace Petal.Engines.Imaging.Tests;

public sealed class PhotoEditTests(ITestOutputHelper output)
{
    private static string PhotoFile(string dir, int width = 1600, int height = 1200, string name = "photo")
    {
        var path = Path.Combine(dir, name + ".jpg");
        using var image = Fixtures.Photo(width, height);
        var exif = new ExifProfile();
        exif.SetValue(ExifTag.Make, "EditCam");
        image.SetProfile(exif);
        image.Quality = 92;
        image.Write(path, MagickFormat.Jpeg);
        return path;
    }

    private static unsafe BgraBitmap RenderWithKernel(BgraBitmap source, PhotoAdjustments adjustments)
    {
        var frame = new PhotoFrame(source.Width, source.Height, source.Pixels);
        var plan = RenderPlan.Create(adjustments, frame.LongEdge, frame.Airlight);
        var result = new BgraBitmap(source.Width, source.Height);
        fixed (byte* p = result.Pixels)
            PhotoRenderer.Render(frame, plan, p, result.Stride);
        return result;
    }

    private static int MaxDifference(BgraBitmap a, BgraBitmap b) =>
        a.Pixels.Zip(b.Pixels, (x, y) => Math.Abs(x - y)).Max();

    private static (double Mean, double StdDev) Stats(BgraBitmap bitmap, Func<byte, byte, byte, double>? channel = null)
    {
        channel ??= (r, g, b) => 0.2126 * r + 0.7152 * g + 0.0722 * b;
        double sum = 0, sq = 0;
        int n = bitmap.Width * bitmap.Height;
        for (int i = 0; i < bitmap.Pixels.Length; i += 4)
        {
            double v = channel(bitmap.Pixels[i + 2], bitmap.Pixels[i + 1], bitmap.Pixels[i]);
            sum += v;
            sq += v * v;
        }
        double mean = sum / n;
        return (mean, Math.Sqrt(sq / n - mean * mean));
    }

    [Fact]
    public async Task NeutralIsIdentity()
    {
        var dir = Fixtures.NewDirectory();
        using var session = await PhotoEditSession.OpenAsync(PhotoFile(dir, 800, 600));
        var neutral = session.RenderPreview(PhotoAdjustments.Neutral);
        Assert.Equal(session.OriginalPreview.Pixels, neutral.Pixels);

        // The kernel itself (without the neutral shortcut) is exact too.
        Assert.Equal(0, MaxDifference(session.OriginalPreview, RenderWithKernel(session.OriginalPreview, new PhotoAdjustments())));
        // And it is continuous: a tiny change is a tiny difference.
        Assert.True(MaxDifference(session.OriginalPreview, session.RenderPreview(new PhotoAdjustments { Exposure = 0.001, Contrast = 0.1 })) <= 1);
    }

    [Fact]
    public async Task SlidersMoveTheImageTheRightWay()
    {
        var dir = Fixtures.NewDirectory();
        using var session = await PhotoEditSession.OpenAsync(PhotoFile(dir, 640, 480));
        var original = session.OriginalPreview;
        var (mean, std) = Stats(original);
        BgraBitmap R(PhotoAdjustments a) => session.RenderPreview(a);

        Assert.True(Stats(R(new() { Exposure = 1 })).Mean > mean + 15);
        Assert.True(Stats(R(new() { Exposure = -1 })).Mean < mean - 15);
        Assert.True(Stats(R(new() { Brightness = 60 })).Mean > mean + 5);
        Assert.True(Stats(R(new() { Gamma = 60 })).Mean > mean + 5);
        Assert.True(Stats(R(new() { Contrast = 80 })).StdDev > std * 1.1);
        Assert.True(Stats(R(new() { Contrast = -80 })).StdDev < std * 0.9);
        Assert.True(Stats(R(new() { Blacks = 80 })).Mean > mean);
        Assert.True(Stats(R(new() { Whites = -80 })).Mean < mean);
        Assert.True(Stats(R(new() { Shadows = 100 })).Mean > mean);
        Assert.True(Stats(R(new() { Highlights = -100 })).Mean < mean);

        double Warmth(BgraBitmap b) => Stats(b, (r, _, bl) => r - bl).Mean;
        Assert.True(Warmth(R(new() { Temperature = 60 })) > Warmth(original) + 8);
        Assert.True(Warmth(R(new() { Temperature = -60 })) < Warmth(original) - 8);
        double Greenness(BgraBitmap b) => Stats(b, (r, g, bl) => g - (r + bl) / 2.0).Mean;
        output.WriteLine($"greenness {Greenness(original):0.0} → tint+60 {Greenness(R(new() { Tint = 60 })):0.0}, warmth {Warmth(original):0.0} → +60 {Warmth(R(new() { Temperature = 60 })):0.0}");
        Assert.True(Greenness(R(new() { Tint = 60 })) < Greenness(original) - 3);

        double Chroma(BgraBitmap b) => Stats(b, (r, g, bl) => Math.Max(r, Math.Max(g, bl)) - Math.Min(r, Math.Min(g, bl))).Mean;
        Assert.True(Chroma(R(new() { Saturation = -100 })) < 1.5);
        Assert.True(Chroma(R(new() { Saturation = 60 })) > Chroma(original) * 1.3);
        Assert.True(Chroma(R(new() { Vibrance = 80 })) > Chroma(original) * 1.1);
        Assert.True(MaxDifference(original, R(new() { Hue = 120 })) > 40);

        double Roughness(BgraBitmap b)
        {
            double sum = 0;
            for (int i = 4; i < b.Pixels.Length; i += 4)
                sum += Math.Abs(b.Pixels[i + 1] - b.Pixels[i - 3]);
            return sum / (b.Width * b.Height);
        }
        double rough = Roughness(original);
        Assert.True(Roughness(R(new() { Sharpness = 100 })) > rough * 1.2);
        Assert.True(Roughness(R(new() { NoiseReduction = 100 })) < rough * 0.9);
        Assert.True(Roughness(R(new() { Grain = 100 })) > rough * 1.2);
        Assert.True(Stats(R(new() { Clarity = 100 })).StdDev > std);

        // Dehaze is judged on a hazy photo: the original seen through a bright veil.
        var hazyPath = Path.Combine(dir, "hazy.png");
        using (var hazy = Fixtures.Photo(640, 480))
        {
            hazy.Evaluate(Channels.RGB, EvaluateOperator.Multiply, 0.45);
            hazy.Evaluate(Channels.RGB, EvaluateOperator.Add, Quantum.Max * 0.5);
            hazy.Write(hazyPath, MagickFormat.Png);
        }
        using (var hazySession = await PhotoEditSession.OpenAsync(hazyPath))
        {
            double hazyStd = Stats(hazySession.OriginalPreview).StdDev;
            Assert.True(Stats(hazySession.RenderPreview(new() { Dehaze = 100 })).StdDev > hazyStd * 1.3);
            Assert.True(Stats(hazySession.RenderPreview(new() { Dehaze = -100 })).StdDev < hazyStd * 0.9);
        }

        var vignette = R(new() { Vignette = -100 });
        double Corner(BgraBitmap b) => b.Pixels[1];
        double Centre(BgraBitmap b) => b.Pixels[((b.Height / 2) * b.Width + b.Width / 2) * 4 + 1];
        Assert.True(Corner(vignette) < Corner(original) * 0.5 + 2);
        Assert.Equal(Centre(original), Centre(vignette), 2);
    }

    [Fact]
    public async Task PreviewRendersFast()
    {
        var dir = Fixtures.NewDirectory();
        using var session = await PhotoEditSession.OpenAsync(PhotoFile(dir, 3200, 2400));
        Assert.Equal((1600, 1200), (session.PreviewWidth, session.PreviewHeight));
        Assert.Equal((3200, 2400), (session.SourceWidth, session.SourceHeight));

        var everything = new PhotoAdjustments
        {
            Exposure = 0.4, Brightness = 10, Contrast = 20, Highlights = -30, Shadows = 25, Whites = 10, Blacks = -10, Gamma = 5,
            Temperature = 15, Tint = -5, Saturation = 10, Vibrance = 20, Hue = 5,
            Clarity = 30, Sharpness = 40, NoiseReduction = 20, Dehaze = 15, Grain = 20, Vignette = -25,
        };
        var lightOnly = new PhotoAdjustments { Exposure = 0.3, Contrast = 15, Temperature = 10, Saturation = 10 };
        var buffer = new byte[session.PreviewWidth * session.PreviewHeight * 4];

        double Measure(PhotoAdjustments a)
        {
            for (int i = 0; i < 5; i++)
                session.RenderPreview(a, buffer, session.PreviewWidth * 4);
            var times = new List<double>();
            for (int i = 0; i < 25; i++)
            {
                var sw = Stopwatch.StartNew();
                session.RenderPreview(a with { Exposure = a.Exposure + i * 0.001 }, buffer, session.PreviewWidth * 4);
                times.Add(sw.Elapsed.TotalMilliseconds);
            }
            times.Sort();
            return times[times.Count / 2];
        }

        double all = Measure(everything), light = Measure(lightOnly);
        var hist = Stopwatch.StartNew();
        var histogram = ImageHistogram.Compute(buffer, session.PreviewWidth, session.PreviewHeight, session.PreviewWidth * 4);
        double histMs = hist.Elapsed.TotalMilliseconds;
        bool optimized = typeof(PhotoEditSession).Assembly.GetCustomAttributes(typeof(DebuggableAttribute), false)
            .OfType<DebuggableAttribute>().All(a => !a.IsJITOptimizerDisabled);
        output.WriteLine($"1600×1200 preview, {Environment.ProcessorCount} logical CPUs, AVX2 {PhotoRendererAvx2.IsSupported}, " +
            $"{(optimized ? "Release" : "Debug (unoptimised JIT)")}: all sliders {all:0.0} ms, light/colour only {light:0.0} ms, histogram {histMs:0.0} ms");
        Assert.Equal(1600 * 1200, histogram.Luminance.Sum());
        // The 40 ms budget applies to optimised builds; Debug builds run the JIT without optimisation.
        Assert.True(all < (optimized ? 40 : 400), $"render took {all:0.0} ms");
    }

    [Fact]
    public async Task ExportMatchesPreviewAndKeepsMetadata()
    {
        var dir = Fixtures.NewDirectory();
        var input = PhotoFile(dir, 3200, 2400);
        using var session = await PhotoEditSession.OpenAsync(input);
        var adjustments = new PhotoAdjustments
        {
            Exposure = 0.5, Contrast = 30, Shadows = 40, Highlights = -40, Temperature = 20, Saturation = 20,
            Clarity = 40, Vignette = -40, Dehaze = 20, Grain = 10, Sharpness = 30,
        };
        var preview = session.RenderPreview(adjustments);
        var exported = Path.Combine(dir, "edited.jpg");
        await session.ExportAsync(exported, adjustments);

        using var full = new MagickImage(exported);
        Assert.Equal((3200u, 2400u), (full.Width, full.Height));
        Assert.Equal("EditCam", full.GetExifProfile()?.GetValue(ExifTag.Make)?.Value);
        full.Resize(1600, 1200);
        var downscaled = ImagePreview.ToBgra(full);
        double diff = 0;
        for (int i = 0; i < preview.Pixels.Length; i += 4)
            diff += Math.Abs(preview.Pixels[i + 1] - downscaled.Pixels[i + 1]);
        diff /= preview.Width * preview.Height;
        output.WriteLine($"mean |preview − downscaled export| = {diff:0.00} levels");
        Assert.True(diff < 6, $"export differs from preview by {diff:0.00} levels on average");
    }

    [Theory]
    [InlineData("png")]
    [InlineData("heic")]
    [InlineData("webp")]
    public async Task ExportKeepsTheOriginalFormat(string id)
    {
        var dir = Fixtures.NewDirectory();
        using var image = Fixtures.Quadrants(transparentBand: id != "heic");
        var input = Fixtures.Save(image, dir, F.FromId(id)!);
        using var session = await PhotoEditSession.OpenAsync(input);
        var output = Path.Combine(dir, "edited." + F.FromId(id)!.WriteExtension);
        await session.ExportAsync(output, new PhotoAdjustments { Exposure = -1 });
        Assert.Equal(F.FromId(id), ImageFormats.Detect(output));
        using var result = new MagickImage(output);
        var tl = Fixtures.PixelAt(result, 16, 12);
        Assert.True(tl.R < Fixtures.Red.R - 40, $"darker red expected, got {tl}");
        if (id != "heic")
            Assert.True(Fixtures.PixelAt(result, 10, 24).A < 10, "transparency survives");
    }

    [Fact]
    public async Task SimdAndScalarKernelsAgree()
    {
        if (!PhotoRendererAvx2.IsSupported)
            return;
        var dir = Fixtures.NewDirectory();
        using var session = await PhotoEditSession.OpenAsync(PhotoFile(dir, 1003, 701)); // odd width: rows end with scalar pixels
        foreach (var a in new[]
        {
            new PhotoAdjustments
            {
                Exposure = 0.7, Brightness = 10, Contrast = 20, Highlights = -30, Shadows = 25, Whites = 10, Blacks = -10, Gamma = 5,
                Temperature = 15, Tint = -5, Saturation = 10, Vibrance = 20, Hue = 5,
                Clarity = 30, Sharpness = 40, NoiseReduction = 20, Dehaze = 15, Grain = 20, Vignette = -25,
            },
            new PhotoAdjustments { Dehaze = -40, Vibrance = -30, Vignette = 40, Exposure = -0.5 },
        })
        {
            var simd = session.RenderPreview(a);
            PhotoRenderer.ForceScalar = true;
            try
            {
                var scalar = session.RenderPreview(a);
                Assert.True(MaxDifference(simd, scalar) <= 1, $"max difference {MaxDifference(simd, scalar)}");
            }
            finally
            {
                PhotoRenderer.ForceScalar = false;
            }
        }
    }

    [Fact]
    public void SliderMetadataCoversEveryProperty()
    {
        var props = typeof(PhotoAdjustments).GetProperties().Where(p => p.PropertyType == typeof(double)).ToList();
        Assert.Equal(props.Count, PhotoAdjustments.Sliders.Count);
        var a = PhotoAdjustments.Neutral;
        foreach (var slider in PhotoAdjustments.Sliders)
        {
            var changed = slider.Set(a, slider.Maximum);
            Assert.Equal(slider.Maximum, slider.Get(changed));
            Assert.False(changed.IsNeutral);
        }
    }
}
