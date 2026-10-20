namespace Petal.Engines.Media.Tests;

public class VideoToolTests
{
    [Fact]
    public async Task Compress_balanced_and_strong_shrink_video()
    {
        var input = await Fixtures.GetAsync("hq.mp4");
        var dir = Fixtures.NewDir();
        var progress = new RecordingProgress();

        var balanced = await MediaCompressor.CompressAsync(input, Path.Combine(dir, "balanced.mp4"), new CompressOptions(), progress);
        var strong = await MediaCompressor.CompressAsync(input, Path.Combine(dir, "strong.mp4"), new CompressOptions(CompressPreset.Strong));

        progress.AssertCompleteAndMonotonic();
        Assert.True(balanced.OutputBytes < balanced.OriginalBytes);
        Assert.True(strong.OutputBytes < balanced.OutputBytes);
        var info = await MediaAssert.ValidAsync(strong.OutputPath, 5.0);
        Assert.Equal((320, 240), (info.Width, info.Height));
        Assert.True(info.HasAudio);
    }

    [Fact]
    public async Task Compress_to_target_size_lands_near_the_target()
    {
        var input = await Fixtures.GetAsync("hq.mp4");
        var original = new FileInfo(input).Length;
        var target = original / 4;
        var progress = new RecordingProgress();

        var result = await MediaCompressor.CompressAsync(input, Path.Combine(Fixtures.NewDir(), "target.mp4"), new CompressOptions(TargetSizeBytes: target), progress);

        Assert.InRange(result.OutputBytes, target * 0.5, target * 1.15);
        progress.AssertCompleteAndMonotonic();
        await MediaAssert.ValidAsync(result.OutputPath, 5.0);
    }

    [Fact]
    public async Task Compress_by_percentage_and_max_resolution()
    {
        var input = await Fixtures.GetAsync("hq.mp4");
        var result = await MediaCompressor.CompressAsync(input, Path.Combine(Fixtures.NewDir(), "half.mp4"),
            new CompressOptions(ReductionPercent: 50, MaxResolution: 120));

        Assert.True(result.Ratio <= 0.6, $"ratio {result.Ratio}");
        var info = await MediaAssert.ValidAsync(result.OutputPath, 5.0);
        Assert.Equal((160, 120), (info.Width, info.Height));
    }

    [Fact]
    public async Task Compress_keeps_webm_and_gif_formats()
    {
        var dir = Fixtures.NewDir();
        var webm = await MediaCompressor.CompressAsync(await Fixtures.GetAsync("video.webm"), Path.Combine(dir, "c.webm"));
        var webmInfo = await MediaAssert.ValidAsync(webm.OutputPath, 1.0);
        Assert.Equal("vp9", webmInfo.VideoCodec);
        Assert.Equal("opus", webmInfo.AudioCodec);

        var gif = await MediaCompressor.CompressAsync(await Fixtures.GetAsync("video.gif"), Path.Combine(dir, "c.gif"), new CompressOptions(CompressPreset.Strong));
        var gifInfo = await MediaAssert.ValidAsync(gif.OutputPath, 1.0, 0.3);
        Assert.Equal("gif", gifInfo.VideoCodec);
        Assert.True(gif.OutputBytes < gif.OriginalBytes);
        Assert.Equal(120, gifInfo.Width);
    }

    [Fact]
    public async Task Compress_gif_to_a_target_size()
    {
        var input = await Fixtures.GetAsync("video.gif");
        var target = new FileInfo(input).Length / 3;
        var output = Path.Combine(Fixtures.NewDir(), "tiny.gif");
        var result = await MediaCompressor.CompressAsync(input, output, new CompressOptions(TargetSizeBytes: target));
        Assert.True(result.OutputBytes <= target * 1.1, $"{result.OutputBytes} > {target}");
        await MediaAssert.ValidAsync(output);
        MediaAssert.NoTempFiles(Path.GetDirectoryName(output)!);
    }

    [Fact]
    public async Task Remove_audio_copies_the_picture()
    {
        var input = await Fixtures.GetAsync("video.mp4");
        var output = Path.Combine(Fixtures.NewDir(), "muted.mp4");
        await VideoTools.RemoveAudioAsync(input, output);
        var info = await MediaAssert.ValidAsync(output, 1.0);
        Assert.False(info.HasAudio);
        Assert.Equal("h264", info.VideoCodec);
    }

    [Fact]
    public async Task Trim_is_frame_accurate_when_reencoding()
    {
        var input = await Fixtures.GetAsync("hq.mp4");
        var output = Path.Combine(Fixtures.NewDir(), "trim.mp4");
        var progress = new RecordingProgress();
        await VideoTools.TrimAsync(input, output, new TrimVideoOptions(1.2, 2.8), progress);
        var info = await MediaAssert.ValidAsync(output, 1.6, 0.1);
        Assert.True(info.HasAudio);
        progress.AssertCompleteAndMonotonic();
    }

    [Fact]
    public async Task Trim_fast_copy_cuts_on_keyframes()
    {
        var input = await Fixtures.GetAsync("hq.mp4");
        var output = Path.Combine(Fixtures.NewDir(), "trim-copy.mp4");
        await VideoTools.TrimAsync(input, output, new TrimVideoOptions(1.0, 3.0, FastCopy: true));
        var info = await MediaAssert.ValidAsync(output, 2.0, 0.35);
        Assert.Equal("h264", info.VideoCodec);
    }

    [Fact]
    public async Task Trim_and_crop_a_gif_keep_it_a_gif()
    {
        var input = await Fixtures.GetAsync("video.gif");
        var dir = Fixtures.NewDir();
        await VideoTools.TrimAsync(input, Path.Combine(dir, "t.gif"), new TrimVideoOptions(0.2, 0.8));
        var trimmed = await MediaAssert.ValidAsync(Path.Combine(dir, "t.gif"), 0.6, 0.15);
        Assert.Equal("gif", trimmed.VideoCodec);

        await VideoTools.CropAsync(input, Path.Combine(dir, "c.gif"), new CropVideoOptions(10, 10, 101, 61));
        var cropped = await MediaAssert.ValidAsync(Path.Combine(dir, "c.gif"), 1.0, 0.3);
        Assert.Equal((101, 61), (cropped.Width, cropped.Height));
    }

    [Fact]
    public async Task Crop_uses_even_sizes_and_keeps_audio()
    {
        var input = await Fixtures.GetAsync("hq.mp4");
        var output = Path.Combine(Fixtures.NewDir(), "crop.mp4");
        await VideoTools.CropAsync(input, output, new CropVideoOptions(20, 30, 201, 151));
        var info = await MediaAssert.ValidAsync(output, 5.0);
        Assert.Equal((200, 150), (info.Width, info.Height));
        Assert.True(info.HasAudio);
        Assert.Equal("aac", info.AudioCodec);
    }

    [Theory]
    [InlineData(2.0, true)]
    [InlineData(0.5, true)]
    [InlineData(4.0, false)]
    [InlineData(0.25, true)]
    public async Task Change_speed_scales_duration(double factor, bool preservePitch)
    {
        var input = await Fixtures.GetAsync("hq.mp4");
        var output = Path.Combine(Fixtures.NewDir(), $"speed{factor}.mp4");
        var progress = new RecordingProgress();
        await VideoTools.ChangeSpeedAsync(input, output, new ChangeSpeedOptions(factor, preservePitch), progress);
        var info = await MediaAssert.ValidAsync(output, 5.0 / factor, 0.15 + 0.03 * (5.0 / factor));
        Assert.True(info.HasAudio);
        Assert.NotNull(info.Audio!.Duration);
        Assert.InRange(info.Audio.Duration!.Value.TotalSeconds, 5.0 / factor - 0.3, 5.0 / factor + 0.3);
        Assert.True(info.FrameRate <= 15.5, $"fps {info.FrameRate}");
        progress.AssertCompleteAndMonotonic();
    }

    [Fact]
    public async Task Change_speed_works_on_silent_video_and_gif()
    {
        var dir = Fixtures.NewDir();
        await VideoTools.ChangeSpeedAsync(await Fixtures.GetAsync("silent.mp4"), Path.Combine(dir, "s.mp4"), new ChangeSpeedOptions(2));
        var silent = await MediaAssert.ValidAsync(Path.Combine(dir, "s.mp4"), 0.5, 0.15);
        Assert.False(silent.HasAudio);

        await VideoTools.ChangeSpeedAsync(await Fixtures.GetAsync("video.gif"), Path.Combine(dir, "s.gif"), new ChangeSpeedOptions(0.5));
        var gif = await MediaAssert.ValidAsync(Path.Combine(dir, "s.gif"), 2.0, 0.3);
        Assert.Equal("gif", gif.VideoCodec);
    }

    [Fact]
    public void Speed_suffix_matches_tangerine_naming()
    {
        Assert.Equal("Speed 2x", VideoTools.SpeedSuffix(2));
        Assert.Equal("Speed 0.5x", VideoTools.SpeedSuffix(0.5));
        Assert.Equal("Speed 1.5x", VideoTools.SpeedSuffix(1.5));
    }

    [Fact]
    public async Task Snapshots_are_full_resolution_pngs()
    {
        var input = await Fixtures.GetAsync("hq.mp4");
        var dir = Fixtures.NewDir();
        var shots = new[] { new VideoSnapshot(0.5, Path.Combine(dir, "a.png")), new VideoSnapshot(4.99, Path.Combine(dir, "b.png")), new VideoSnapshot(60, Path.Combine(dir, "c.png")) };
        var progress = new RecordingProgress();
        var written = await VideoTools.SaveSnapshotsAsync(input, new VideoSnapshotsOptions(shots), progress);

        Assert.Equal(3, written.Count);
        foreach (var path in written)
            Assert.Equal((320, 240), Fixtures.PngSize(await File.ReadAllBytesAsync(path)));
        progress.AssertCompleteAndMonotonic();
        MediaAssert.NoTempFiles(dir);
    }

    [Fact]
    public async Task Split_at_times_names_clips_after_the_source()
    {
        var input = await Fixtures.GetAsync("hq.mp4");
        var folder = Path.Combine(Fixtures.NewDir(), "hq Clips");
        var progress = new RecordingProgress();
        var clips = await VideoTools.SplitAsync(input, folder, new SplitVideoOptions([3.0, 1.5]), progress);

        Assert.Equal(["hq 1.mp4", "hq 2.mp4", "hq 3.mp4"], clips.Select(Path.GetFileName));
        double[] expected = [1.5, 1.5, 2.0];
        for (var i = 0; i < clips.Count; i++)
            await MediaAssert.ValidAsync(clips[i], expected[i], 0.12);
        progress.AssertCompleteAndMonotonic();
    }

    [Fact]
    public async Task Split_into_equal_parts()
    {
        var input = await Fixtures.GetAsync("hq.mp4");
        var folder = Path.Combine(Fixtures.NewDir(), "parts");
        var clips = await VideoTools.SplitAsync(input, folder, new SplitVideoOptions(EqualParts: 2));
        Assert.Equal(2, clips.Count);
        foreach (var clip in clips)
            await MediaAssert.ValidAsync(clip, 2.5, 0.12);
    }

    [Fact]
    public async Task Join_mixes_containers_sizes_and_silent_clips()
    {
        string[] inputs =
        [
            await Fixtures.GetAsync("video.mp4"),   // 160×120, 15 fps, sound
            await Fixtures.GetAsync("big.mov"),     // 320×180, 30 fps, no sound
            await Fixtures.GetAsync("video.mkv"),   // 160×120, sound
        ];
        var output = Path.Combine(Fixtures.NewDir(), "joined.mp4");
        var progress = new RecordingProgress();
        await VideoTools.JoinAsync(inputs, output, null, progress);

        var info = await MediaAssert.ValidAsync(output, 3.0, 0.2);
        Assert.Equal((160, 120), (info.Width, info.Height));
        Assert.Equal(15, info.FrameRate, 1);
        Assert.True(info.HasAudio);
        Assert.InRange(info.Audio!.Duration!.Value.TotalSeconds, 2.8, 3.2);
        progress.AssertCompleteAndMonotonic();
    }

    [Fact]
    public async Task Join_of_silent_clips_has_no_audio()
    {
        string[] inputs = [await Fixtures.GetAsync("silent.mp4"), await Fixtures.GetAsync("big.mov")];
        var output = Path.Combine(Fixtures.NewDir(), "joined-silent.mp4");
        await VideoTools.JoinAsync(inputs, output);
        var info = await MediaAssert.ValidAsync(output, 2.0, 0.2);
        Assert.False(info.HasAudio);
    }

    [Fact]
    public async Task Redact_solid_box_only_during_its_time_range()
    {
        var input = await Fixtures.GetAsync("hq.mp4");
        var output = Path.Combine(Fixtures.NewDir(), "redact.mp4");
        var boxes = new[] { new RedactionBox(0, 0, 100, 80, 1.0, 3.0) };
        await VideoTools.RedactAsync(input, output, new RedactVideoOptions(boxes, RedactStyle.Solid, "#FF0000"));
        var info = await MediaAssert.ValidAsync(output, 5.0);
        Assert.True(info.HasAudio);

        var during = await Fixtures.RgbFrameAsync(output, 2.0);
        var (r, g, b) = Pixel(during, 40, 40);
        Assert.True(r > 200 && g < 60 && b < 60, $"expected red, got {r},{g},{b}");

        // Outside its range the box is gone: the picture matches the original again.
        var original = await Fixtures.RgbFrameAsync(input, 4.0);
        var after = await Fixtures.RgbFrameAsync(output, 4.0);
        var region = MeanDifference(original, after, (x, y) => x < 100 && y < 80);
        Assert.True(region < 6, $"box area differs by {region} after its range");
        var before = MeanDifference(await Fixtures.RgbFrameAsync(input, 2.0), during, (x, y) => x < 100 && y < 80);
        Assert.True(before > 30, $"box area should differ during its range ({before})");
    }

    [Fact]
    public async Task Redact_blur_changes_only_the_box()
    {
        var input = await Fixtures.GetAsync("hq.mp4");
        var output = Path.Combine(Fixtures.NewDir(), "blur.mp4");
        var box = new RedactionBox(100, 60, 120, 100);
        await VideoTools.RedactAsync(input, output, new RedactVideoOptions([box], RedactStyle.Blur));

        var original = await Fixtures.RgbFrameAsync(input, 2.0);
        var blurred = await Fixtures.RgbFrameAsync(output, 2.0);
        var inside = MeanDifference(original, blurred, (x, y) => x >= 110 && x < 210 && y >= 70 && y < 150);
        var outside = MeanDifference(original, blurred, (x, y) => x < 90 || x >= 230 || y < 50 || y >= 170);
        Assert.True(inside > 8, $"inside diff {inside}");
        Assert.True(outside < 4, $"outside diff {outside}");
    }

    private static (int R, int G, int B) Pixel((byte[] Rgb, int Width, int Height) frame, int x, int y)
    {
        var i = (y * frame.Width + x) * 3;
        return (frame.Rgb[i], frame.Rgb[i + 1], frame.Rgb[i + 2]);
    }

    private static double MeanDifference((byte[] Rgb, int Width, int Height) a, (byte[] Rgb, int Width, int Height) b, Func<int, int, bool> region)
    {
        double sum = 0;
        long n = 0;
        for (var y = 0; y < a.Height; y++)
        for (var x = 0; x < a.Width; x++)
        {
            if (!region(x, y))
                continue;
            var i = (y * a.Width + x) * 3;
            sum += Math.Abs(a.Rgb[i] - b.Rgb[i]) + Math.Abs(a.Rgb[i + 1] - b.Rgb[i + 1]) + Math.Abs(a.Rgb[i + 2] - b.Rgb[i + 2]);
            n += 3;
        }
        return sum / n;
    }

    [Fact]
    public async Task Cancelling_a_tool_leaves_nothing_behind()
    {
        var input = await Fixtures.GetAsync("long.mp4");
        var dir = Fixtures.NewDir();
        using var cts = new CancellationTokenSource();
        var progress = new RecordingProgress();
        progress.Reported += v =>
        {
            if (v > 0.01)
                cts.Cancel();
        };
        cts.CancelAfter(TimeSpan.FromSeconds(8));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            MediaCompressor.CompressAsync(input, Path.Combine(dir, "c.mp4"), new CompressOptions(CompressPreset.Strong), progress, cts.Token));
        Assert.Empty(Directory.EnumerateFileSystemEntries(dir));
    }
}
