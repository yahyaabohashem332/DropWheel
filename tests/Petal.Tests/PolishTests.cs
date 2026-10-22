using System.Diagnostics;
using Petal.App.Screen;
using Petal.Core.Imaging;
using Petal.Core.Util;
using SkiaSharp;

namespace Petal.Tests;

/// <summary>Polished screen recordings: the camera's moves, one drawn frame, and a whole video through FFmpeg.</summary>
public sealed class PolishTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("petal-polish-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static List<CursorSample> Still(int frames, double x, double y) =>
        Enumerable.Range(0, frames).Select(_ => new CursorSample(x, y, true)).ToList();

    [Fact]
    public void Without_clicks_the_camera_stays_back()
    {
        var path = CameraPath.Build(30, 1280, 720, Still(90, 300, 200), [], 1.8);
        Assert.All(path, f => Assert.Equal(1, f.Zoom, 3));
        Assert.All(path, f => Assert.Equal(640, f.CenterX, 1));
    }

    [Fact]
    public void A_click_draws_the_camera_in_and_out_again()
    {
        var clicks = new[] { new ClickSample(2, 1000, 600) };
        var path = CameraPath.Build(30, 1280, 720, Still(30 * 9, 1000, 600), clicks, 2);
        Assert.Equal(1, path[15].Zoom, 2);
        // In by the click, near the full zoom a moment later.
        Assert.True(path[60].Zoom > 1.3, $"at the click {path[60].Zoom}");
        Assert.True(path[90].Zoom > 1.85, $"after {path[90].Zoom}");
        // Looking towards the click, but never past the edge of the recording.
        Assert.True(path[90].CenterX > 640);
        Assert.True(path[90].CenterX <= 1280 - 1280 / 2 / path[90].Zoom + 0.001);
        // And out again once the clicking stops.
        Assert.True(path[^1].Zoom < 1.05, $"at the end {path[^1].Zoom}");
    }

    [Fact]
    public void Clicks_close_together_keep_the_camera_in()
    {
        var segments = CameraPath.Segments([new ClickSample(1, 0, 0), new ClickSample(2.5, 0, 0), new ClickSample(9, 0, 0)], zooming: true);
        Assert.Equal(2, segments.Count);
        Assert.Equal(1 - CameraPath.LeadIn, segments[0].Start, 6);
        Assert.Equal(2.5 + CameraPath.Hold, segments[0].End, 6);
        Assert.Empty(CameraPath.Segments([new ClickSample(1, 0, 0)], zooming: false));
    }

    [Fact]
    public void A_shaky_pointer_is_smoothed()
    {
        var random = new Random(3);
        var shaky = Enumerable.Range(0, 120).Select(_ => new CursorSample(400 + random.Next(-6, 7), 300 + random.Next(-6, 7), true)).ToList();
        var path = CameraPath.Build(30, 800, 600, shaky, [], 1);
        double raw = shaky.Skip(30).Zip(shaky.Skip(31)).Average(p => Math.Abs(p.First.X - p.Second.X));
        double smooth = path.Skip(30).Zip(path.Skip(31)).Average(p => Math.Abs(p.First.CursorX - p.Second.CursorX));
        Assert.True(smooth < raw * 0.7, $"{smooth} vs {raw}");
    }

    [Fact]
    public void A_frame_is_drawn_on_its_background_with_the_pointer()
    {
        const int w = 320, h = 200;
        using var source = new SKBitmap(w, h);
        using (var c = new SKCanvas(source))
            c.Clear(new SKColor(0xF0, 0xF0, 0xF0));
        using var image = SKImage.FromBitmap(source);
        using var target = new SKBitmap(w, h);
        using var canvas = new SKCanvas(target);
        var options = new PolishOptions("ocean", 1.8, 1.6, ClickRings: true);
        RecordingPolish.Compose(canvas, image, w, h, new CameraFrame(1, w / 2.0, h / 2.0, 160, 100, true), 0.1, [new ClickSample(0, 160, 100)], options);
        canvas.Flush();
        // The corner is the background's colour, the middle is the recording, and the pointer is white.
        var corner = target.GetPixel(1, 1);
        Assert.True(corner.Blue > 150 && corner.Red < 60, corner.ToString());
        var middle = target.GetPixel(60, 60);
        Assert.True(middle.Red > 200 && middle.Green > 200, middle.ToString());
        using var png = File.Create(Path.Combine(_dir, "frame.png"));
        target.Encode(png, SKEncodedImageFormat.Png, 100);
    }

    [Fact]
    public async Task A_recording_is_polished_with_its_sound()
    {
        if (ToolLocator.Ffmpeg is not { } ffmpeg)
            return;
        var input = Path.Combine(_dir, "plain.mp4");
        var make = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (var a in new[] { "-v", "error", "-f", "lavfi", "-i", "testsrc=size=320x240:rate=30:duration=3", "-f", "lavfi", "-i", "sine=frequency=440:duration=3",
                     "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", "-y", input })
            make.ArgumentList.Add(a);
        using (var p = Process.Start(make)!)
        {
            await p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync();
        }
        var output = Path.Combine(_dir, "polished.mp4");
        var cursor = Enumerable.Range(0, 90).Select(i => new CursorSample(100 + i, 120, true)).ToList();
        bool ok = await RecordingPolish.RenderAsync(input, output, 320, 240, 30, cursor, [new ClickSample(1, 160, 120)], new PolishOptions("violet", 1.8, 1.4, true), null, CancellationToken.None);
        Assert.True(ok);
        if (Environment.GetEnvironmentVariable("PETAL_POLISH_OUT") is { Length: > 0 } keep)
            File.Copy(output, keep, overwrite: true);
        var probe = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (var a in new[] { "-hide_banner", "-i", output })
            probe.ArgumentList.Add(a);
        using var q = Process.Start(probe)!;
        var info = await q.StandardError.ReadToEndAsync();
        await q.WaitForExitAsync();
        Assert.Contains("320x240", info);
        Assert.Contains("Audio:", info);
        Assert.Contains("Duration: 00:00:0", info);
    }
}
