using System.Text;
using ImageMagick;
using Xunit;

namespace Petal.Engines.Imaging.Tests;

/// <summary>For looking at: PETAL_TRACE is a folder of pictures; each gets a ".svg" and that SVG drawn back as a ".svg.png" in its "out".</summary>
public sealed class ImageTracerBench
{
    [Fact]
    public async Task Pictures_for_looking_at()
    {
        if (Environment.GetEnvironmentVariable("PETAL_TRACE") is not { Length: > 0 } folder)
            return;
        var results = Path.Combine(folder, "out");
        Directory.CreateDirectory(results);
        foreach (var file in Directory.EnumerateFiles(folder))
        {
            var result = await ImageTracer.TraceFileAsync(file);
            var name = Path.Combine(results, Path.GetFileNameWithoutExtension(file));
            await File.WriteAllTextAsync(name + ".svg", result.Svg);
            using var image = new MagickImage(Encoding.UTF8.GetBytes(result.Svg), new MagickReadSettings { Format = MagickFormat.Svg, BackgroundColor = MagickColors.White });
            image.Write(name + ".svg.png");
            await File.WriteAllTextAsync(name + ".txt", $"{result.Colours} colours, {result.Shapes} shapes, {result.Svg.Length / 1024} KB");
        }
    }
}
