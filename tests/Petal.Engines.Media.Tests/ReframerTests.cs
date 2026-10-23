namespace Petal.Engines.Media.Tests;

/// <summary>Vertical Video: where the subject is, a steady camera path, and the tall result in each mode.</summary>
public sealed class ReframerTests
{
    [Fact]
    public void Motion_is_found_where_the_picture_changed()
    {
        int w = 160, h = 90;
        var before = new byte[w * h];
        var now = new byte[w * h];
        for (int y = 30; y < 60; y++)
            for (int x = 120; x < 140; x++)
                now[y * w + x] = 200;
        Assert.InRange(Reframer.Centre(now, before, w, h)!.Value, 0.80, 0.83);
        // A still picture has no opinion.
        Assert.Null(Reframer.Centre(before, before, w, h));
    }

    [Fact]
    public void The_camera_holds_still_for_small_moves_and_stays_inside_the_picture()
    {
        // A subject that wobbles a little around the middle, then walks to the right edge.
        var samples = new List<(double, double)>();
        for (int i = 0; i < 10; i++)
            samples.Add((i, 0.5 + (i % 2 == 0 ? 0.02 : -0.02)));
        for (int i = 10; i < 20; i++)
            samples.Add((i, 0.97));
        var path = Reframer.Path(samples, windowShare: 0.316);
        // While it wobbles, one position.
        Assert.Single(path.Where(p => p.T < 10).Select(p => Math.Round(p.X, 4)).Distinct());
        // At the edge the window stops at the picture's side, never beyond.
        Assert.Equal(1 - 0.316 / 2, path[^1].X, 3);
        Assert.All(path, p => Assert.InRange(p.X, 0.158 - 1e-9, 0.842 + 1e-9));
    }

    [Fact]
    public void The_window_glides_between_positions()
    {
        var expression = Reframer.Expression([(0, 0.5), (4, 0.7)], 600, 1920);
        Assert.Equal("if(lt(t,4),if(lt(t,3.2),660,660+(1044-660)*(t-3.2)/0.8),1044)", expression);
        Assert.Equal("660", Reframer.Expression([(0, 0.5)], 600, 1920));
    }

    [Theory]
    [InlineData(ReframeMode.Follow)]
    [InlineData(ReframeMode.Centre)]
    [InlineData(ReframeMode.Blurred)]
    public async Task A_wide_video_becomes_tall(ReframeMode mode)
    {
        var input = await Fixtures.GetAsync("hq.mp4");
        var output = Path.Combine(Fixtures.NewDir(), $"hq Vertical {mode}.mp4");
        await Reframer.ReframeAsync(input, output, new ReframeOptions(mode, 360, 640));
        var info = await MediaProbe.ProbeAsync(output);
        Assert.Equal((360, 640), (info.Width, info.Height));
        Assert.NotNull(info.Audio);
        Assert.InRange(info.Duration.TotalSeconds, 4.8, 5.3);
    }

    [Fact]
    public async Task A_finder_tells_where_to_follow()
    {
        var input = await Fixtures.GetAsync("hq.mp4");
        var output = Path.Combine(Fixtures.NewDir(), "hq Vertical found.mp4");
        int asked = 0;
        await Reframer.ReframeAsync(input, output, new ReframeOptions(ReframeMode.Follow, 360, 640), (_, _) =>
        {
            Interlocked.Increment(ref asked);
            return Task.FromResult<double?>(0.2);
        });
        Assert.True(asked >= 8, asked.ToString());
        Assert.True(File.Exists(output));
    }

    [Fact]
    public void A_long_video_s_path_stays_shallow_and_short_enough_for_FFmpeg()
    {
        // An hour followed twice a second, the subject crossing the picture again and again.
        var samples = Enumerable.Range(0, 7200).Select(i => (i / 2.0, 0.15 + 0.7 * ((i / 20) % 2))).ToList();
        var path = Reframer.Path(samples, windowShare: 0.316);
        Assert.InRange(path.Count, 2, Reframer.MostPoints);
        var expression = Reframer.Expression(path, 600, 1920);
        int depth = 0, deepest = 0;
        foreach (var c in expression)
        {
            if (c == '(') deepest = Math.Max(deepest, ++depth);
            else if (c == ')') depth--;
        }
        Assert.Equal(0, depth);
        Assert.True(deepest < 30, deepest.ToString());
        Assert.True(expression.Length < 20_000, expression.Length.ToString());
    }
}
