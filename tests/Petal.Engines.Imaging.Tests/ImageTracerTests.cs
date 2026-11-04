using System.Text;
using System.Text.RegularExpressions;
using ImageMagick;
using ImageMagick.Drawing;
using Xunit;

namespace Petal.Engines.Imaging.Tests;

/// <summary>Tracing a picture: its colours stay, what it draws comes back close to it, and the result is a real SVG.</summary>
public sealed class ImageTracerTests
{
    /// <summary>A logo: a ring and a square on white, drawn soft-edged as real ones are.</summary>
    private static MagickImage Logo()
    {
        var image = new MagickImage(MagickColors.White, 400, 300);
        new Drawables()
            .FillColor(new MagickColor(200, 30, 40)).StrokeColor(new MagickColor(200, 30, 40)).StrokeWidth(0).Circle(130, 150, 230, 150)
            .FillColor(MagickColors.White).Circle(130, 150, 180, 150)
            .FillColor(new MagickColor(20, 60, 180)).StrokeColor(new MagickColor(20, 60, 180)).Rectangle(260, 90, 370, 210)
            .Draw(image);
        return image;
    }

    private static MagickImage Render(string svg, uint width, uint height)
    {
        var settings = new MagickReadSettings { Format = MagickFormat.Svg, BackgroundColor = MagickColors.White };
        var image = new MagickImage(Encoding.UTF8.GetBytes(svg), settings);
        image.Alpha(AlphaAction.Remove);
        image.Resize(new MagickGeometry(width, height) { IgnoreAspectRatio = true });
        return image;
    }

    private static double Difference(MagickImage a, MagickImage b) => a.Compare(b, ErrorMetric.MeanAbsolute);

    [Fact]
    public void A_logo_comes_back_close_to_itself_and_is_a_real_svg()
    {
        using var logo = Logo();
        var result = ImageTracer.Trace(logo, new TraceOptions());
        Assert.StartsWith("<svg", result.Svg);
        Assert.Contains("viewBox=\"0 0 400 300\"", result.Svg);
        System.Xml.Linq.XDocument.Parse(result.Svg);
        Assert.InRange(result.Colours, 3, 5);
        Assert.Contains("#c81e28", result.Svg);
        Assert.Contains("#143cb4", result.Svg);
        using var back = Render(result.Svg, 400, 300);
        Assert.True(Difference(logo, back) < 0.01, Difference(logo, back).ToString());
        // A vector, not thousands of stair-steps.
        Assert.True(result.Svg.Length < 8000, result.Svg.Length.ToString());
    }

    [Fact]
    public void The_number_of_colours_can_be_chosen()
    {
        using var logo = Logo();
        Assert.Equal(2, ImageTracer.Trace(logo, new TraceOptions(2)).Colours);
    }

    [Fact]
    public void A_straight_edge_is_a_line_and_a_square_has_its_corners()
    {
        using var image = new MagickImage(MagickColors.White, 800, 800);
        new Drawables().FillColor(MagickColors.Black).Rectangle(200, 200, 599, 599).Draw(image);
        var svg = ImageTracer.Trace(image, new TraceOptions(2)).Svg;
        var black = Regex.Match(svg, "fill=\"#000000\"[^>]*d=\"([^\"]*)\"").Groups[1].Value;
        Assert.DoesNotContain("C", black);
        var coordinates = Regex.Matches(black, @"[ML]([\d.]+) ([\d.]+)").Select(m => (double.Parse(m.Groups[1].Value), double.Parse(m.Groups[2].Value))).ToList();
        Assert.InRange(coordinates.Count, 4, 8);
        Assert.True(coordinates.Min(c => c.Item1) <= 200.6 && coordinates.Max(c => c.Item1) >= 599.4);
    }

    [Fact]
    public void A_transparent_picture_stays_transparent_where_it_was()
    {
        using var image = new MagickImage(MagickColors.Transparent, 400, 400);
        new Drawables().FillColor(new MagickColor(10, 120, 60)).Circle(200, 200, 300, 200).Draw(image);
        var result = ImageTracer.Trace(image, new TraceOptions(1));
        Assert.Equal(1, result.Colours);
        using var back = Render(result.Svg, 400, 400);
        Assert.Equal(255, back.GetPixels().GetPixel(10, 10).GetChannel(1) >> 0 & 0xFF);
        Assert.InRange((int)back.GetPixels().GetPixel(200, 200).GetChannel(1), 100, 140);
    }

    [Fact]
    public void The_colour_around_the_edge_can_be_left_out()
    {
        using var logo = Logo();
        var result = ImageTracer.Trace(logo, new TraceOptions(0, 0.6, true));
        Assert.DoesNotContain("#ffffff", result.Svg);
        Assert.Contains("#c81e28", result.Svg);
    }

    [Fact]
    public void Holes_in_a_shape_are_holes()
    {
        const int w = 20, h = 20;
        var mask = new bool[w * h];
        for (int y = 2; y < 18; y++)
            for (int x = 2; x < 18; x++)
                mask[y * w + x] = !(x is >= 8 and < 12 && y is >= 8 and < 12);
        var loops = ImageTracer.Outlines(mask, w, h);
        Assert.Equal(2, loops.Count);
        Assert.Contains(loops, l => l.Contains((2, 2)));
        Assert.Contains(loops, l => l.Contains((8, 8)));
    }

    [Fact]
    public void Shapes_touching_at_a_corner_are_followed_without_getting_lost()
    {
        const int w = 6, h = 6;
        var mask = new bool[w * h];
        mask[1 * w + 1] = mask[2 * w + 2] = true;
        var loops = ImageTracer.Outlines(mask, w, h);
        Assert.Equal(8, loops.Sum(l => l.Count));
    }

    [Fact]
    public void A_speck_takes_the_colour_around_it()
    {
        const int w = 10, h = 10;
        var labels = new int[w * h];
        labels[55] = 1;
        ImageTracer.RemoveSpecks(labels, w, h, 2, 4);
        Assert.All(labels, l => Assert.Equal(0, l));
        var large = new int[w * h];
        for (int i = 0; i < 20; i++)
            large[i] = 1;
        ImageTracer.RemoveSpecks(large, w, h, 2, 4);
        Assert.Equal(20, large.Count(l => l == 1));
    }

    [Fact]
    public async Task A_picture_file_can_be_traced()
    {
        var path = Path.Combine(Path.GetTempPath(), $"trace-{Guid.NewGuid():N}.png");
        try
        {
            using (var logo = Logo())
                logo.Write(path);
            Assert.Contains("<path", (await ImageTracer.TraceFileAsync(path)).Svg);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
