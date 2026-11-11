using System.Diagnostics;
using Petal.Core.Models;
using Petal.Engines.Media.Internal;
using Xunit;
using Xunit.Abstractions;

namespace Petal.Engines.Media.Tests;

/// <summary>Blurring faces and plates by itself: things are followed through a video and what is covered can't come back.</summary>
public sealed class AutoBlurTests(ITestOutputHelper output)
{
    private static BlurSighting See(double x, double y, double w = 0.1, double h = 0.1, BlurKind kind = BlurKind.Face, double score = 0.9) => new(kind, x, y, w, h, score);

    private static (double, IReadOnlyList<BlurSighting>) At(double t, params BlurSighting[] found) => (t, found);

    [Fact]
    public void A_face_moving_across_is_one_track()
    {
        var tracks = AutoBlur.Follow([At(0, See(0.1, 0.1)), At(0.2, See(0.13, 0.1)), At(0.4, See(0.16, 0.1))]);
        var track = Assert.Single(tracks);
        Assert.Equal(3, track.Keys.Count);
        Assert.Equal((0.0, 0.4), (track.Start, track.End));
    }

    [Fact]
    public void Two_faces_are_two_tracks_even_side_by_side()
    {
        var tracks = AutoBlur.Follow([At(0, See(0.1, 0.1), See(0.6, 0.1)), At(0.2, See(0.11, 0.1), See(0.61, 0.1))]);
        Assert.Equal(2, tracks.Count);
        Assert.All(tracks, t => Assert.Equal(2, t.Keys.Count));
    }

    [Fact]
    public void A_short_gap_is_the_same_thing_and_a_long_one_isnt()
    {
        Assert.Single(AutoBlur.Follow([At(0, See(0.1, 0.1)), At(0.2, See(0.1, 0.1)), At(0.9, See(0.1, 0.1))]));
        Assert.Equal(2, AutoBlur.Follow([At(0, See(0.1, 0.1)), At(0.2, See(0.1, 0.1)), At(3, See(0.1, 0.1)), At(3.2, See(0.1, 0.1))]).Count);
    }

    [Fact]
    public void A_face_and_a_plate_in_one_place_stay_apart()
    {
        var tracks = AutoBlur.Follow([At(0, See(0.1, 0.1), See(0.1, 0.1, kind: BlurKind.Plate)), At(0.2, See(0.1, 0.1), See(0.1, 0.1, kind: BlurKind.Plate))]);
        Assert.Equal(2, tracks.Count);
    }

    [Fact]
    public void One_unsure_glimpse_isnt_a_face_but_one_sure_one_is()
    {
        Assert.Empty(AutoBlur.Follow([At(0, See(0.1, 0.1, score: 0.65))]));
        Assert.Single(AutoBlur.Follow([At(0, See(0.1, 0.1, score: 0.95))]));
    }

    [Fact]
    public void Between_sightings_the_place_is_on_the_line_and_a_little_beyond_it_stays()
    {
        var track = AutoBlur.Follow([At(1, See(0.2, 0.2)), At(2, See(0.25, 0.2))])[0];
        Assert.Equal(0.225, track.At(1.5, 0)!.Value.X, 5);
        Assert.Null(track.At(0.5, 0));
        Assert.Equal(0.2, track.At(0.8, 0.35)!.Value.X, 5);
        Assert.Equal(0.25, track.At(2.2, 0.35)!.Value.X, 5);
        Assert.Null(track.At(2.5, 0.35));
    }

    [Fact]
    public void Boxes_on_the_same_place_are_one()
    {
        var kept = AutoBlurDetector.Best([See(0.1, 0.1, score: 0.7), See(0.11, 0.1, score: 0.9), See(0.6, 0.6, score: 0.8)]);
        Assert.Equal(2, kept.Count);
        Assert.Equal(0.9, kept[0].Score);
    }

    [Fact]
    public void Covered_goes_beyond_the_box_and_stays_in_the_frame()
    {
        var (x, y, w, h) = AutoBlur.Covered(new BlurKey(0, 0.4, 0.4, 0.2, 0.2), BlurKind.Face, 1000, 1000);
        Assert.True(x < 400 && y < 400 && x + w > 600 && y + h > 600);
        var edge = AutoBlur.Covered(new BlurKey(0, 0.95, 0.95, 0.2, 0.2), BlurKind.Plate, 1000, 1000);
        Assert.Equal(1000, edge.X + edge.W);
        Assert.Equal(1000, edge.Y + edge.H);
    }

    /// <summary>Fine detail: a black and white chequer of single pixels, with random noise.</summary>
    private static byte[] Detail(int width, int height)
    {
        var random = new Random(3);
        var frame = new byte[width * height * 3];
        random.NextBytes(frame);
        return frame;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void What_is_covered_loses_its_detail_and_the_rest_stays(bool oval)
    {
        const int w = 200, h = 200;
        var frame = Detail(w, h);
        var was = (byte[])frame.Clone();
        AutoBlur.Cover(frame, w, h, (50, 50, 100, 100), oval);
        // Outside the box nothing changed.
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                if (x < 50 || x >= 150 || y < 50 || y >= 150)
                    Assert.Equal(was[(y * w + x) * 3], frame[(y * w + x) * 3]);
        // In the middle of it, neighbouring pixels are nearly the same: nothing of the original's detail is left.
        int largest = 0;
        for (int y = 80; y < 120; y++)
            for (int x = 80; x < 120; x++)
                largest = Math.Max(largest, Math.Abs(frame[(y * w + x) * 3] - frame[(y * w + x + 1) * 3]));
        Assert.True(largest < 6, largest.ToString());
    }

    [Fact]
    public async Task A_video_comes_out_covered_with_its_length_and_sound()
    {
        var folder = Path.Combine(Path.GetTempPath(), "Petal", "blur-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var input = Path.Combine(folder, "in.mp4");
            await Run("-f", "lavfi", "-i", "testsrc2=size=320x240:rate=15:duration=2,noise=alls=70:allf=t", "-f", "lavfi", "-i", "sine=frequency=440:duration=2", "-c:v", "h264_mf", "-rate_control", "quality", "-quality", "95", "-pix_fmt", "nv12", "-c:a", "aac_mf", "-sample_fmt", "s16", "-shortest", input);
            var track = Assert.Single(AutoBlur.Follow(Enumerable.Range(0, 5).Select(i => At(i * 0.5, See(0.25, 0.25, 0.5, 0.5))).ToList()));
            var result = Path.Combine(folder, "out.mp4");
            await AutoBlur.ApplyAsync(input, result, [track]);
            var info = await MediaProbe.ProbeAsync(result);
            Assert.Equal(320, info.Width);
            Assert.Equal(240, info.Height);
            Assert.True(info.HasAudio);
            Assert.InRange(info.DurationSeconds, 1.8, 2.2);
            var (blurred, original) = (await MiddleDifferenceAsync(result), await MiddleDifferenceAsync(input));
            Assert.True(blurred * 3 < original, $"{blurred} against {original}");

            // Kept, it isn't covered.
            track.Keep = true;
            var kept = Path.Combine(folder, "kept.mp4");
            await AutoBlur.ApplyAsync(input, kept, [track]);
            var sharp = await MiddleDifferenceAsync(kept);
            Assert.True(sharp * 2 > original, $"{sharp} against {original}");
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    /// <summary>How much neighbouring pixels differ in the middle of a frame: a blurred one hardly at all.</summary>
    private static async Task<double> MiddleDifferenceAsync(string video)
    {
        // Through DropWheel's own way of running FFmpeg: H.264 comes through Windows.
        using var memory = new MemoryStream();
        await FfmpegProcess.RunReadingStdoutAsync(Ffmpeg.Exe,
            ["-hide_banner", "-loglevel", "error", "-ss", "1", "-i", video, "-frames:v", "1", "-vf", "crop=120:90:100:75,format=gray", "-f", "rawvideo", "-"],
            (s, ct) => s.CopyToAsync(memory, ct), default);
        var bytes = memory.ToArray();
        double sum = 0;
        for (int i = 1; i < bytes.Length; i++)
            sum += Math.Abs(bytes[i] - bytes[i - 1]);
        return sum / Math.Max(1, bytes.Length);
    }

    private static async Task Run(params string[] args)
    {
        var start = new ProcessStartInfo(Ffmpeg.Exe) { RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "-hide_banner", "-loglevel", "error", "-y" }.Concat(args))
            start.ArgumentList.Add(a);
        using var process = Process.Start(start)!;
        await process.WaitForExitAsync();
        Assert.Equal(0, process.ExitCode);
    }

    /// <summary>With the real models, on real pictures: PETAL_BLUR is a folder with the two models and some pictures; results go in its "out".</summary>
    [Fact]
    public async Task Real_pictures_for_looking_at()
    {
        if (Environment.GetEnvironmentVariable("PETAL_BLUR") is not { Length: > 0 } folder)
            return;
        var was = ModelStore.Folder;
        ModelStore.Folder = folder;
        try
        {
            File.Copy(Path.Combine(folder, "face.onnx"), Path.Combine(folder, ModelCatalog.FaceFinder.FileName), true);
            File.Copy(Path.Combine(folder, "plate.onnx"), Path.Combine(folder, ModelCatalog.PlateFinder.FileName), true);
            var results = Path.Combine(folder, "out");
            Directory.CreateDirectory(results);
            foreach (var picture in Directory.EnumerateFiles(folder).Where(f => Path.GetExtension(f) is ".jpg" or ".png"))
            {
                var video = Path.Combine(results, Path.GetFileNameWithoutExtension(picture) + ".mp4");
                await Run("-loop", "1", "-i", picture, "-t", "2", "-r", "15", "-vf", "scale=trunc(iw/2)*2:trunc(ih/2)*2", "-c:v", "h264_mf", "-rate_control", "quality", "-quality", "80", "-compression_level", "0", "-pix_fmt", "nv12", video);
                var clock = Stopwatch.StartNew();
                var tracks = await AutoBlur.FindAsync(video, new AutoBlurOptions());
                output.WriteLine($"{Path.GetFileName(picture)}: {tracks.Count(t => t.Kind == BlurKind.Face)} faces, {tracks.Count(t => t.Kind == BlurKind.Plate)} plates, {clock.Elapsed.TotalSeconds:0.0}s for 12 looks");
                var blurred = Path.Combine(results, Path.GetFileNameWithoutExtension(picture) + ".blurred.mp4");
                await AutoBlur.ApplyAsync(video, blurred, tracks);
                await Run("-ss", "1", "-i", blurred, "-frames:v", "1", Path.Combine(results, Path.GetFileNameWithoutExtension(picture) + ".blurred.jpg"));
            }
        }
        finally
        {
            ModelStore.Folder = was;
        }
    }
}
