using System.Diagnostics;
using System.Globalization;
using Petal.App.Screen.Studio;
using Petal.Core.Imaging;
using Petal.Core.Recording;
using Petal.Core.Util;
using SkiaSharp;
using Xunit;

namespace Petal.Tests;

public sealed class StudioRenderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "petal-studio-render-" + Guid.NewGuid().ToString("N")[..8]);

    public StudioRenderTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        // PETAL_STUDIO_OUT keeps the frames and the video to look at.
        if (Environment.GetEnvironmentVariable("PETAL_STUDIO_OUT") is { Length: > 0 } keep)
        {
            Directory.CreateDirectory(keep);
            foreach (var f in Directory.GetFiles(_dir))
                File.Copy(f, Path.Combine(keep, Path.GetFileName(f)), overwrite: true);
        }
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    /// <summary>A made-up recording: a test picture with a tone, the pointer gliding across and clicking.</summary>
    private async Task<StudioProject> RecordingAsync(string ffmpeg, double duration = 6)
    {
        await MakeAsync(ffmpeg, Path.Combine(_dir, "screen.mp4"), "-f", "lavfi", "-i", $"testsrc2=size=1280x720:rate=30:duration={duration}",
            "-f", "lavfi", "-i", $"sine=frequency=440:duration={duration}", "-c:v", "h264_mf", "-rate_control", "quality", "-quality", "80", "-compression_level", "0", "-pix_fmt", "nv12", "-c:a", "aac_mf", "-sample_fmt", "s16", "-shortest");
        var pointer = new List<PointerSample>();
        for (int i = 0; i <= duration * 120; i++)
        {
            double t = i / 120.0;
            double f = Math.Min(1, t / 3);
            pointer.Add(new PointerSample(t, 200 + 800 * f, 200 + 300 * f, true, t > 3.5 ? PointerKind.Hand : PointerKind.Arrow));
        }
        var project = new StudioProject
        {
            Folder = _dir,
            Video = "screen.mp4",
            Width = 1280,
            Height = 720,
            Fps = 30,
            Duration = duration,
            HasPointer = true,
            Pointer = pointer,
            Clicks = [new ClickSample(1.5, 600, 350), new ClickSample(4, 1000, 500)],
            Keys = [new KeySample(2, "Ctrl + C")],
        };
        project.Edit = project.Automatic(new StudioLook(), new StudioOutput { Fps = 30 }, 2);
        return project;
    }

    [Fact]
    public void Every_pointer_has_a_sharp_picture_with_its_point_inside()
    {
        using var art = new PointerArt();
        foreach (var kind in Enum.GetValues<PointerKind>())
        {
            var p = art.For(kind, -1);
            Assert.True(p.Image.Height >= 64, kind.ToString());
            Assert.InRange(p.HotX, 0, p.Image.Width);
            Assert.InRange(p.HotY, 0, p.Image.Height);
            using var bitmap = SKBitmap.FromImage(p.Image);
            Assert.Contains(Enumerable.Range(0, bitmap.Height).SelectMany(y => Enumerable.Range(0, bitmap.Width).Select(x => bitmap.GetPixel(x, y))), c => c.Alpha > 200);
        }
        // The hand is Windows' own, pointing with a finger well right of where the arrow's tip is.
        Assert.True(art.For(PointerKind.Hand, -1).HotX > art.For(PointerKind.Arrow, -1).HotX + 12);
    }

    [Fact]
    public async Task A_frame_sits_on_its_background_and_the_view_goes_in_at_a_click()
    {
        if (ToolLocator.Ffmpeg is not { } ffmpeg)
            return;
        var project = await RecordingAsync(ffmpeg);
        using var art = new PointerArt(project);
        using var renderer = new StudioRenderer(project, 1920, 1080, art);
        using var screen = SKImage.FromEncodedData(await FrameAsync(ffmpeg, Path.Combine(_dir, "screen.mp4"), 1.5));
        Assert.Equal(1, renderer.Camera.At(0.1).Zoom, 2);
        Assert.True(renderer.Camera.At(2.2).Zoom > 1.8);
        foreach (var t in new[] { 0.1, 1.0, 2.2 })
        {
            using var target = new SKBitmap(new SKImageInfo(1920, 1080, SKColorType.Bgra8888, SKAlphaType.Premul));
            using (var canvas = new SKCanvas(target))
                renderer.Draw(canvas, screen, t, 1 / 30.0);
            // The corner is the violet background.
            var corner = target.GetPixel(4, 4);
            Assert.True(corner.Blue > 200 && corner.Red > 90 && corner.Green < 120, corner.ToString());
            using var png = File.Create(Path.Combine(_dir, $"frame {t.ToString(CultureInfo.InvariantCulture)}.png"));
            target.Encode(png, SKEncodedImageFormat.Png, 90);
        }
    }

    [Fact]
    public async Task The_finished_video_has_its_size_its_smoothness_and_the_edits()
    {
        if (ToolLocator.Ffmpeg is not { } ffmpeg)
            return;
        var project = await RecordingAsync(ffmpeg);
        project.Edit = project.Edit with
        {
            Edits = [new EditSegment(4.5, 5, EditKind.Cut), new EditSegment(1, 2, EditKind.Speed, 2)],
            Look = project.Edit.Look with { Shape = "9:16" },
        };
        var output = Path.Combine(_dir, "finished.mp4");
        var clock = Stopwatch.StartNew();
        await StudioExport.RenderAsync(project, output, null, CancellationToken.None);
        var info = await Petal.Engines.Media.MediaProbe.ProbeAsync(output);
        Assert.Equal(1080, info.Video!.Width);
        Assert.Equal(1920, info.Video.Height);
        Assert.InRange(info.FrameRate, 29.9, 30.1);
        Assert.True(info.HasAudio);
        // Six seconds, less half a second cut and half a second saved by playing one second twice as fast.
        Assert.InRange(info.DurationSeconds, 4.9, 5.15);
        Assert.True(clock.Elapsed < TimeSpan.FromMinutes(3), clock.Elapsed.ToString());
    }

    [Fact]
    public void The_sound_is_cut_sped_up_cleaned_and_evened_out_together()
    {
        var timeline = new EditTimeline(10, 0, 10, [new EditSegment(2, 3, EditKind.Cut), new EditSegment(5, 7, EditKind.Speed, 4)]);
        var graph = StudioExport.Graph([false, true], timeline.Pieces, new StudioLook());
        Assert.Contains("asplit=4", graph);
        Assert.Contains("atempo=2,atempo=2", graph);
        Assert.Contains("afftdn", graph);
        Assert.Contains("amix=inputs=2", graph);
        Assert.Contains("loudnorm", graph);
        Assert.DoesNotContain("afftdn", StudioExport.Graph([false], timeline.Pieces, new StudioLook()));
        Assert.Contains("alimiter", StudioExport.Graph([false], timeline.Pieces, new StudioLook { EvenVolume = false }));
    }

    private static async Task MakeAsync(string ffmpeg, string output, params string[] args)
    {
        var make = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (var a in new[] { "-v", "error" }.Concat(args).Concat(["-y", output]))
            make.ArgumentList.Add(a);
        using var p = Process.Start(make)!;
        await p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
    }

    /// <summary>A frame as DropWheel reads one: what its FFmpeg doesn't decode (H.264) comes through Windows.</summary>
    private async Task<string> FrameAsync(string ffmpeg, string video, double seconds)
    {
        var png = Path.Combine(_dir, $"source {seconds.ToString(CultureInfo.InvariantCulture)}.png");
        await File.WriteAllBytesAsync(png, await Petal.Engines.Media.MediaPreview.ExtractFrameAsync(video, TimeSpan.FromSeconds(seconds)));
        return png;
    }
}
