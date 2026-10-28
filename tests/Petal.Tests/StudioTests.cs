using Petal.Core.Imaging;
using Petal.Core.Recording;
using Xunit;

namespace Petal.Tests;

public class StudioTests
{
    private static StudioProject Project(double duration = 20, int width = 1920, int height = 1080, IEnumerable<PointerSample>? pointer = null,
        IEnumerable<ClickSample>? clicks = null, IEnumerable<TypingSample>? typing = null, StudioEdit? edit = null)
    {
        var p = new StudioProject
        {
            Width = width,
            Height = height,
            Duration = duration,
            HasPointer = true,
            Pointer = pointer?.ToList() ?? [new PointerSample(0, width / 2.0, height / 2.0, true)],
            Clicks = clicks?.ToList() ?? [],
            Typing = typing?.ToList() ?? [],
        };
        p.Edit = edit ?? new StudioEdit { TrimEnd = duration };
        return p;
    }

    [Fact]
    public void Clicks_close_together_are_one_zoom_and_a_lone_one_its_own()
    {
        var p = Project(30, clicks: [new(2, 100, 100), new(2.5, 120, 110), new(10, 900, 500), new(29.8, 10, 10)]);
        var zooms = ZoomPlanner.Auto(p, 1.8);
        Assert.Equal(2, zooms.Count);
        Assert.Equal(2 - ZoomPlanner.LeadIn, zooms[0].Start, 3);
        Assert.Equal(2.5 + ZoomPlanner.Hold, zooms[0].End, 3);
        Assert.Equal(10 - ZoomPlanner.LeadIn, zooms[1].Start, 3);
        // The click that stopped the recording is not zoomed on.
        Assert.All(zooms, z => Assert.True(z.End < 29));
        Assert.All(zooms, z => Assert.Equal(1.8, z.Level));
        Assert.Empty(ZoomPlanner.Auto(p, 1));
    }

    [Fact]
    public void A_run_of_typing_is_zoomed_on_and_a_stray_key_is_not()
    {
        var typing = new List<TypingSample> { new(5, 0, 0, false) };
        for (int i = 0; i < 6; i++)
            typing.Add(new TypingSample(12 + i * 0.4, 400, 300, true));
        var zooms = ZoomPlanner.Auto(Project(30, typing: typing), 2);
        var zoom = Assert.Single(zooms);
        Assert.Equal(12 - 0.6, zoom.Start, 3);
        Assert.Equal(14 + 1.6, zoom.End, 3);
    }

    [Fact]
    public void The_finished_video_skips_cuts_and_plays_sped_up_stretches_faster()
    {
        var timeline = new EditTimeline(60, 5, 55, [new EditSegment(10, 20, EditKind.Cut), new EditSegment(30, 40, EditKind.Speed, 4)]);
        Assert.Equal(3, timeline.Pieces.Count(p => p.Speed == 1));
        Assert.Equal(5 + 10 + 2.5 + 15, timeline.Length, 6);
        Assert.Equal(5, timeline.ToSource(0), 6);
        Assert.Equal(20, timeline.ToSource(5), 6);
        Assert.Equal(34, timeline.ToSource(16), 6);
        Assert.Equal(40, timeline.ToSource(17.5), 6);
        Assert.Null(timeline.ToOutput(15));
        Assert.Null(timeline.ToOutput(2));
        Assert.Equal(16, timeline.ToOutput(34)!.Value, 6);
        Assert.Equal(20, timeline.NextKept(15));
        Assert.Equal(5, timeline.NextKept(1));
        Assert.Equal(4, timeline.At(35)!.Value.Speed);
        // A cut over a speed-up wins; untouched, the whole recording is kept.
        var both = new EditTimeline(10, 0, 10, [new EditSegment(2, 6, EditKind.Speed, 2), new EditSegment(4, 8, EditKind.Cut)]);
        Assert.Equal(2 + 1 + 2, both.Length, 6);
        Assert.Equal(10, new EditTimeline(10, 0, 0, []).Length, 6);
    }

    [Theory]
    [InlineData("recorded", "auto", 1920, 1080, 1920, 1080)]
    [InlineData("recorded", "1440", 1920, 1080, 2560, 1440)]
    [InlineData("9:16", "auto", 1920, 1080, 1080, 1920)]
    [InlineData("1:1", "1080", 1280, 720, 1080, 1080)]
    [InlineData("recorded", "auto", 2560, 1600, 3456, 2160)]
    [InlineData("recorded", "auto", 3840, 2160, 3840, 2160)]
    [InlineData("recorded", "auto", 801, 601, 1440, 1080)]
    public void The_finished_size_follows_the_shape_and_keeps_the_detail(string shape, string quality, int w, int h, int ow, int oh)
    {
        var size = StudioLayout.OutputSize(w, h, new StudioLook { Shape = shape }, new StudioOutput { Quality = quality });
        Assert.Equal((ow, oh), size);
    }

    [Fact]
    public void The_recording_keeps_its_proportions_inside_the_margin()
    {
        var look = new StudioLook();
        var content = StudioLayout.Content(1920, 1080, 1920, 1080, look);
        double margin = StudioLayout.Margin(1920, 1080, look);
        Assert.Equal(65, margin);
        Assert.Equal(1080 - 130, content.Height, 6);
        Assert.Equal(1920 / 1080.0, content.Width / content.Height, 6);
        Assert.Equal(960, content.CenterX, 6);
        // Without a background it fills the frame; a tall frame cuts the recording to its shape.
        var none = StudioLayout.Content(1920, 1080, 1920, 1080, look with { Background = "none" });
        Assert.Equal(new RectD(0, 0, 1920, 1080), none);
        var tall = StudioLayout.Content(1080, 1920, 1920, 1080, look with { Shape = "9:16" });
        var (bw, bh) = StudioLayout.BaseView(1920, 1080, tall.Width / tall.Height);
        Assert.Equal(1080, bh, 6);
        Assert.Equal(tall.Width / tall.Height, bw / bh, 6);
    }

    [Fact]
    public void The_view_goes_in_on_a_zoom_looks_at_the_pointer_and_comes_back_out()
    {
        var p = Project(10, pointer: [new PointerSample(0, 1600, 200, true)], edit: new StudioEdit { TrimEnd = 10, Zooms = [new ZoomSegment(1, 4, 2)] });
        var pointer = new PointerPlan(p);
        var camera = new CameraPlan(p, pointer, 1920 / 1080.0);
        Assert.Equal(1, camera.At(0.5).Zoom, 3);
        Assert.Equal(960, camera.At(0.5).X, 1);
        var inside = camera.At(3.5);
        Assert.InRange(inside.Zoom, 1.95, 2.01);
        var view = camera.View(inside);
        Assert.InRange(1600, view.X, view.Right);
        Assert.InRange(200, view.Y, view.Bottom);
        Assert.Equal(1, camera.At(8).Zoom, 2);
        for (double t = 0; t < 10; t += 0.05)
        {
            var v = camera.View(t);
            Assert.True(v.X >= -0.01 && v.Y >= -0.01 && v.Right <= 1920.01 && v.Bottom <= 1080.01, $"out at {t}: {v}");
        }
    }

    [Fact]
    public void A_zoom_with_a_fixed_point_looks_there_and_a_small_move_doesnt_sway_the_view()
    {
        var p = Project(10, edit: new StudioEdit { TrimEnd = 10, Zooms = [new ZoomSegment(0.5, 6, 2, Follow: false, X: 700, Y: 400)] });
        var camera = new CameraPlan(p, new PointerPlan(p), 1920 / 1080.0);
        Assert.Equal(700, camera.At(4).X, 0);
        Assert.Equal(400, camera.At(4).Y, 0);

        // The pointer at 900,500, then a small move: the view stays.
        var moving = Project(10, pointer: [new PointerSample(0, 900, 500, true), new PointerSample(3, 900, 500, true), new PointerSample(3.2, 980, 540, true)],
            edit: new StudioEdit { TrimEnd = 10, Zooms = [new ZoomSegment(0.5, 9, 2)] });
        var follow = new CameraPlan(moving, new PointerPlan(moving), 1920 / 1080.0);
        Assert.Equal(follow.At(2.9).X, follow.At(5).X, 0);
    }

    [Theory]
    [InlineData(16 / 9.0, 2.5)]
    [InlineData(9 / 16.0, 1.6)]
    public void A_pointer_moving_on_and_on_in_a_zoom_never_leaves_the_view(double aspect, double level)
    {
        var samples = Enumerable.Range(0, 120 * 6 + 1).Select(i =>
        {
            double t = i / 120.0;
            return new PointerSample(t, 100 + 280 * t, 540 + 300 * Math.Sin(t * 2), true);
        }).ToList();
        var p = Project(6, pointer: samples, edit: new StudioEdit { TrimEnd = 6, Zooms = [new ZoomSegment(0.2, 5.8, level)] });
        var pointer = new PointerPlan(p);
        var camera = new CameraPlan(p, pointer, aspect);
        for (double t = 1.2; t < 5.6; t += 1 / 60.0)
        {
            var at = pointer.At(t);
            var view = camera.View(t);
            Assert.True(at.X > view.X && at.X < view.Right && at.Y > view.Y && at.Y < view.Bottom, $"{t:0.00}: {at.X:0},{at.Y:0} outside {view}");
        }
    }

    [Fact]
    public void A_tall_frame_follows_the_action_across_the_recording()
    {
        var p = Project(8, pointer: [new PointerSample(0, 200, 540, true), new PointerSample(3, 200, 540, true), new PointerSample(3.5, 1800, 540, true)]);
        var camera = new CameraPlan(p, new PointerPlan(p), 9 / 16.0);
        Assert.InRange(camera.View(2).X, 0, 0.5);
        var later = camera.View(7.5);
        Assert.InRange(1800, later.X, later.Right);
        Assert.True(later.Right <= 1920.01);
    }

    [Fact]
    public void The_pointer_is_smoothed_held_to_clicks_faded_at_rest_and_pressed_in()
    {
        var samples = new List<PointerSample>();
        var random = new Random(3);
        for (int i = 0; i <= 120 * 2; i++)
        {
            double t = i / 120.0;
            samples.Add(new PointerSample(t, 400 + t * 200 + random.NextDouble() * 6 - 3, 300 + random.NextDouble() * 6 - 3, true));
        }
        // Then it rests until the end.
        samples.Add(new PointerSample(2.05, 800, 300, true));
        var p = Project(8, pointer: samples, clicks: [new ClickSample(1, 600, 302)]);
        var plan = new PointerPlan(p);
        double rawJitter = 0, smoothJitter = 0;
        for (int i = 20; i < 220; i++)
        {
            double t = i / 120.0;
            rawJitter += Math.Abs(samples[i].Y - 300);
            smoothJitter += Math.Abs(plan.At(t).Y - 300);
        }
        Assert.True(smoothJitter < rawJitter * 0.5, $"{smoothJitter} vs {rawJitter}");
        Assert.InRange(plan.At(1).X, 597, 603);
        Assert.Equal(1, plan.At(1.02).Press, 3);
        Assert.Equal(0, plan.At(1.4).Press, 3);
        Assert.Equal(1, plan.At(1.5).Opacity, 3);
        Assert.Equal(0, plan.At(7).Opacity, 3);
        // Without a recorded pointer nothing is drawn.
        p.HasPointer = false;
        Assert.False(new PointerPlan(p).Any);
    }

    [Fact]
    public void A_project_is_saved_and_read_back_whole()
    {
        var folder = Path.Combine(Path.GetTempPath(), "petal-studio-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var p = Project(12, pointer: [new PointerSample(0.5, 10, 20, true, PointerKind.Text), new PointerSample(1, 30, 40, false, PointerKind.Other, 0)],
                clicks: [new ClickSample(1, 30, 40, 2)], typing: [new TypingSample(2, 5, 6, true)]);
            p.Folder = folder;
            p.Keys.Add(new KeySample(3, "Ctrl + C"));
            p.Shapes.Add(new PointerShape("shape0.png", 4, 5, 32, 32));
            p.Edit = p.Edit with
            {
                Zooms = [new ZoomSegment(1, 3, 2.2, Follow: false, X: 50, Y: 60)],
                Edits = [new EditSegment(4, 5, EditKind.Speed, 4)],
                Look = new StudioLook { Background = "ocean", Shape = "9:16" },
                Output = new StudioOutput { Quality = "1440", Fps = 30 },
            };
            p.Save();
            var back = StudioProject.Load(folder)!;
            Assert.Equal(folder, back.Folder);
            Assert.Equal(p.Pointer, back.Pointer);
            Assert.Equal(p.Clicks, back.Clicks);
            Assert.Equal(p.Typing, back.Typing);
            Assert.Equal(p.Keys, back.Keys);
            Assert.Equal(p.Shapes, back.Shapes);
            Assert.Equal(p.Edit.Zooms, back.Edit.Zooms);
            Assert.Equal(p.Edit.Edits, back.Edit.Edits);
            Assert.Equal(p.Edit.Look, back.Edit.Look);
            Assert.Equal(p.Edit.Output, back.Edit.Output);
            Assert.Null(StudioProject.Load(Path.Combine(folder, "missing")));
        }
        finally
        {
            try { Directory.Delete(folder, true); } catch (IOException) { }
        }
    }
}
