using ImageMagick;
using ImageMagick.Drawing;
using Petal.Core.Models;
using Xunit.Abstractions;

namespace Petal.Engines.Imaging.Tests;

public sealed class ObjectEraserTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("petal-erase-").FullName;

    public void Dispose()
    {
        if (Environment.GetEnvironmentVariable("PETAL_KEEP_TEST_OUTPUT") == "1")
            return;
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void Strokes_become_a_mask()
    {
        var mask = ObjectEraser.Mask(100, 80, [new BrushStroke([(10, 40), (90, 40)], 5)]);
        Assert.Equal(255, mask[40 * 100 + 50]);
        Assert.Equal(255, mask[44 * 100 + 10]);
        Assert.Equal(0, mask[50 * 100 + 50]);
        Assert.Equal(0, mask[40 * 100 + 97]);
    }

    /// <summary>With PETAL_LAMA pointing at a folder holding lama_fp32.onnx, an object vanishes into its background.</summary>
    [Fact]
    public async Task A_brushed_object_disappears()
    {
        var folder = Environment.GetEnvironmentVariable("PETAL_LAMA");
        if (folder is null)
        {
            output.WriteLine("Skipped: PETAL_LAMA isn't set.");
            return;
        }
        var saved = ModelStore.Folder;
        ModelStore.Folder = folder;
        try
        {
            // A grassy-green, slightly noisy field with a red ball on it.
            var photo = Path.Combine(_dir, "field.jpg");
            using (var image = new MagickImage("gradient:#4F8A3A-#2E5E24", 1600, 1200))
            {
                image.AddNoise(NoiseType.Gaussian, 0.3);
                new Drawables().FillColor(new MagickColor("#D22020")).Circle(900, 600, 980, 600).Draw(image);
                image.Quality = 92;
                image.Write(photo);
            }
            var erased = Path.Combine(_dir, "field Erased.jpg");
            await ObjectEraser.EraseAsync(photo, [new BrushStroke([(900, 600)], 95)], erased);

            using var result = new MagickImage(erased);
            using var before = new MagickImage(photo);
            (double R, double G) Mean(MagickImage image, int x, int y, int w, int h)
            {
                using var area = (MagickImage)image.Clone();
                area.Crop(new MagickGeometry(x, y, (uint)w, (uint)h));
                var stats = area.Statistics();
                return (stats.GetChannel(PixelChannel.Red)!.Mean / Quantum.Max, stats.GetChannel(PixelChannel.Green)!.Mean / Quantum.Max);
            }
            var ball = Mean(result, 860, 560, 80, 80);
            var ballBefore = Mean(before, 860, 560, 80, 80);
            var far = Mean(result, 100, 100, 200, 200);
            var farBefore = Mean(before, 100, 100, 200, 200);
            output.WriteLine($"ball before R{ballBefore.R:0.00} G{ballBefore.G:0.00}, after R{ball.R:0.00} G{ball.G:0.00}");
            Assert.True(ball.R < 0.45 && ball.G > ball.R, "The red ball is gone and grass is in its place.");
            Assert.True(Math.Abs(far.R - farBefore.R) < 0.02 && Math.Abs(far.G - farBefore.G) < 0.02, "Away from the brush nothing changed.");
            Assert.Equal((1600u, 1200u), (result.Width, result.Height));
        }
        finally
        {
            ModelStore.Folder = saved;
        }
    }
}
