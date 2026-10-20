using ImageMagick;
using ImageMagick.Drawing;
using Petal.Core.Models;
using Xunit.Abstractions;
using F = Petal.Core.Formats.Formats;

namespace Petal.Engines.Imaging.Tests;

/// <summary>
/// The AI tools against their real models: the enlargement model ships with Petal (tools\models);
/// the background model is downloaded once into %LOCALAPPDATA%\Petal\models.
/// </summary>
public sealed class AiTests(ITestOutputHelper output)
{
    [Fact]
    public void Bundled_enlargement_model_is_present()
    {
        Assert.True(AiUpscaler.IsReady, "tools\\bin\\models is filled by tools\\scripts\\fetch-tools.ps1");
        Assert.True(AiUpscaler.CanEnlarge(2000, 2000));
        Assert.False(AiUpscaler.CanEnlarge(4000, 3000));
    }

    [Fact]
    public async Task Ai_enlargement_is_four_times_and_keeps_colours()
    {
        var dir = Fixtures.NewDirectory();
        using var source = Fixtures.Quadrants(64, 48);
        var input = Fixtures.Save(source, dir, F.Png);
        var path = Path.Combine(dir, "big.png");
        var started = DateTime.UtcNow;
        await ImageTransformer.TransformAsync(input, path, new ImageTransform { Mode = ResizeMode.Percent, Percent = 400, Enlarge = true, AiEnlarge = true });
        output.WriteLine($"AI 4× of 64×48 took {(DateTime.UtcNow - started).TotalMilliseconds:0} ms");

        using var result = new MagickImage(path);
        Assert.Equal((256u, 192u), (result.Width, result.Height));
        Fixtures.AssertColor(Fixtures.Red, Fixtures.PixelAt(result, 40, 40), 14, "red quadrant");
        Fixtures.AssertColor(Fixtures.Blue, Fixtures.PixelAt(result, 40, 150), 14, "blue quadrant");
        Fixtures.AssertColor(Fixtures.Light, Fixtures.PixelAt(result, 200, 150), 14, "light quadrant");
    }

    [Fact]
    public async Task Ai_enlargement_keeps_edges_sharper_than_plain_resizing()
    {
        var dir = Fixtures.NewDirectory();
        // A dark diagonal stroke on white: plain enlargement blurs it, the model shouldn't.
        using (var art = new MagickImage(MagickColors.White, 96, 96))
        {
            new Drawables().StrokeColor(MagickColors.Black).StrokeWidth(3).Line(8, 88, 88, 8).Draw(art);
            art.Write(Path.Combine(dir, "line.png"), MagickFormat.Png);
        }
        var input = Path.Combine(dir, "line.png");
        await ImageTransformer.TransformAsync(input, Path.Combine(dir, "ai.png"), new ImageTransform { Mode = ResizeMode.Percent, Percent = 400, Enlarge = true, AiEnlarge = true });
        await ImageTransformer.TransformAsync(input, Path.Combine(dir, "plain.png"), new ImageTransform { Mode = ResizeMode.Percent, Percent = 400, Enlarge = true });
        double Grey(string name)
        {
            // Share of in-between greys: a blurred edge has many, a crisp one few.
            using var image = new MagickImage(Path.Combine(dir, name));
            var pixels = image.GetPixels().ToByteArray(PixelMapping.RGB)!;
            int mid = 0;
            for (int i = 0; i < pixels.Length; i += 3)
                if (pixels[i] is > 40 and < 215) mid++;
            return (double)mid / (pixels.Length / 3);
        }
        double ai = Grey("ai.png"), plain = Grey("plain.png");
        output.WriteLine($"in-between greys: AI {ai:P2}, plain {plain:P2}");
        Assert.True(ai < plain * 0.8, $"AI {ai:P2} vs plain {plain:P2}");
    }

    [Fact]
    public async Task Removes_the_background_around_a_clear_subject()
    {
        if (!BackgroundRemover.IsReady)
        {
            output.WriteLine($"Skipped: {ModelCatalog.Background.FileName} isn't in {ModelStore.Folder}.");
            return;
        }
        var dir = Fixtures.NewDirectory();
        var input = Path.Combine(dir, "subject.jpg");
        using (var art = new MagickImage(new MagickColor(232, 236, 240), 640, 480))
        {
            new Drawables().FillColor(new MagickColor(200, 40, 50)).StrokeColor(new MagickColor(90, 10, 20)).StrokeWidth(4).Ellipse(320, 250, 150, 170, 0, 360).Draw(art);
            art.Quality = 92;
            art.Write(input, MagickFormat.Jpeg);
        }
        var path = Path.Combine(dir, "subject No Background.png");
        var started = DateTime.UtcNow;
        await BackgroundRemover.RemoveAsync(input, path);
        output.WriteLine($"background removal of 640×480 took {(DateTime.UtcNow - started).TotalMilliseconds:0} ms");

        using var result = new MagickImage(path);
        Assert.True(result.HasAlpha);
        Assert.True(Fixtures.PixelAt(result, 320, 250).A > 240, "the disc stays opaque");
        Assert.True(Fixtures.PixelAt(result, 20, 20).A < 15, "the corner becomes transparent");
        Assert.True(Fixtures.PixelAt(result, 620, 460).A < 15, "the other corner too");
    }

    [Fact]
    public async Task A_colour_fill_and_crop_replace_the_background()
    {
        if (!BackgroundRemover.IsReady)
            return;
        var dir = Fixtures.NewDirectory();
        var input = Path.Combine(dir, "subject.png");
        using (var art = new MagickImage(new MagickColor(240, 240, 240), 400, 300))
        {
            new Drawables().FillColor(new MagickColor(30, 90, 200)).Rectangle(140, 80, 260, 240).Draw(art);
            art.Write(input, MagickFormat.Png);
        }
        var path = Path.Combine(dir, "filled.jpg");
        await BackgroundRemover.RemoveAsync(input, path, new RemoveBackgroundOptions { Fill = MagickColors.White, CropToSubject = true });
        using var result = new MagickImage(path);
        Assert.False(result.HasAlpha);
        // Cropped close to the 120 × 160 block.
        Assert.InRange((int)result.Width, 110, 160);
        Assert.InRange((int)result.Height, 150, 200);
    }
}
