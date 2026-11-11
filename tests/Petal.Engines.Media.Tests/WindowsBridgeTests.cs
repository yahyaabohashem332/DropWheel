using Petal.Core.Jobs;
using Petal.Engines.Media.Internal;
using Petal.Engines.Media.Foundation;
using F = Petal.Core.Formats.Formats;

namespace Petal.Engines.Media.Tests;

/// <summary>
/// Formats DropWheel's FFmpeg doesn't decode come through Windows. Each test makes FFmpeg treat a format it could
/// decode as one it can't, so that Windows' pictures and sound can be held up against FFmpeg's own.
/// </summary>
public sealed class WindowsBridgeTests
{
    private static async Task<byte[]> RawAsync(params string[] args)
    {
        using var buffer = new MemoryStream();
        await FfmpegProcess.RunReadingStdoutAsync(Ffmpeg.Exe, ["-hide_banner", "-nostdin", "-loglevel", "error", .. args], (s, ct) => s.CopyToAsync(buffer, ct), default);
        return buffer.ToArray();
    }

    private static async Task<byte[]> FrameAsync(string path, double at, string format = "gray")
    {
        return await RawAsync("-ss", Inv.N(at), "-i", path, "-map", "0:v:0", "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", format, "-");
    }

    private static float[] Floats(byte[] bytes)
    {
        var result = new float[bytes.Length / 4];
        Buffer.BlockCopy(bytes, 0, result, 0, result.Length * 4);
        return result;
    }

    [Fact]
    public async Task A_picture_from_Windows_is_FFmpeg_s_own_to_the_pixel()
    {
        var path = await Fixtures.GetAsync("hq.mp4");
        foreach (var at in new[] { 0.0, 2.0, 3.4 })
        {
            var own = await FrameAsync(path, at);
            byte[] windows;
            using (WindowsMedia.ForceWindows("h264"))
                windows = await FrameAsync(path, at);
            Assert.Equal(320 * 240, own.Length);
            Assert.Equal(own.Length, windows.Length);
            // H.264 decodes exactly: the same frame decodes to the same pixels.
            var differing = own.Zip(windows).Count(p => Math.Abs(p.First - p.Second) > 1);
            Assert.True(differing < own.Length / 1000, $"At {at} s, {differing} pixels differ");
        }
    }

    [Fact]
    public async Task Sound_from_Windows_lines_up_with_FFmpeg_s_own()
    {
        var path = await Fixtures.GetAsync("talk.mp4");
        string[] args = ["-i", path, "-map", "0:a:0", "-ac", "1", "-ar", "44100", "-f", "f32le", "-"];
        var own = Floats(await RawAsync(args));
        float[] windows;
        using (WindowsMedia.ForceWindows("aac"))
            windows = Floats(await RawAsync(args));
        Assert.InRange(windows.Length, own.Length - 2048, own.Length + 2048);

        // The offset at which the two agree best, within 50 ms either way.
        var best = (Offset: 0, Score: double.MinValue);
        for (var offset = -2205; offset <= 2205; offset += 1)
        {
            double sum = 0;
            for (var i = 44100; i < 44100 * 8; i += 7)
            {
                var j = i + offset;
                sum += own[i] * windows[j];
            }
            if (sum > best.Score)
                best = (offset, sum);
        }
        Assert.True(best.Offset is >= -44 and <= 44, $"Windows' sound is {best.Offset} samples off (length {windows.Length} against {own.Length})");
    }

    [Fact]
    public async Task A_file_converted_through_Windows_is_whole()
    {
        var path = await Fixtures.GetAsync("talk.mp4");
        var output = Path.Combine(Fixtures.NewDir(), "talk.webm");
        using (WindowsMedia.ForceWindows("h264", "aac"))
        {
            var converter = new MediaConverter();
            await converter.ConvertAsync(new ConversionJob(path, F.Mp4, F.Webm, output, ConversionSettings.Default), null, default);
        }
        var info = await MediaProbe.ProbeAsync(output);
        Assert.Equal("vp9", info.VideoCodec);
        Assert.Equal("opus", info.AudioCodec);
        Assert.Equal(1280, info.Width);
        Assert.Equal(720, info.Height);
        Assert.InRange(info.DurationSeconds, 11.8, 12.2);
    }

    [Fact]
    public async Task A_part_from_the_middle_starts_and_ends_where_asked()
    {
        var path = await Fixtures.GetAsync("long.mp4");
        var output = Path.Combine(Fixtures.NewDir(), "part.mkv");
        using (WindowsMedia.ForceWindows("h264", "aac"))
            await Ffmpeg.RunAsync(["-ss", "7.5", "-t", "4", "-i", path, "-map", "0:v:0", "-map", "0:a:0", "-c:v", "ffv1", "-c:a", "flac", output], 0, null, default);
        var info = await MediaProbe.ProbeAsync(output);
        Assert.InRange(info.DurationSeconds, 3.95, 4.1);
        // The first frame is the one at 7.5 s in the file.
        var expected = await FrameAsync(path, 7.5);
        var first = await FrameAsync(output, 0);
        Assert.True(expected.Zip(first).Count(p => Math.Abs(p.First - p.Second) > 1) < expected.Length / 1000);
    }

    [Fact]
    public async Task A_turned_phone_video_stays_turned()
    {
        var path = await Fixtures.GetAsync("rotated.mp4");
        var own = await FrameAsync(path, 0.5);
        byte[] windows;
        using (WindowsMedia.ForceWindows("h264"))
            windows = await FrameAsync(path, 0.5);
        Assert.Equal(own.Length, windows.Length);
        Assert.True(own.Zip(windows).Count(p => Math.Abs(p.First - p.Second) > 1) < own.Length / 1000);
    }

    [Fact]
    public async Task Copying_a_stream_reads_the_file_itself()
    {
        var path = await Fixtures.GetAsync("video.mp4");
        using (WindowsMedia.ForceWindows("h264", "aac"))
        {
            await using var prepared = await FfmpegCommand.PrepareAsync(["-i", path, "-map", "0", "-c", "copy", "out.mkv"], default);
            Assert.Empty(prepared.Bridges);
            await using var mixed = await FfmpegCommand.PrepareAsync(["-i", path, "-map", "0:v", "-map", "0:a", "-c:v", "copy", "-c:a", "flac", "out.mkv"], default);
            var bridge = Assert.Single(mixed.Bridges);
            // The picture is copied from the file, the sound decoded from the bridge (input 1).
            Assert.Equal(string.Join(" ", ["-i", path, "-f", "matroska", "-i", bridge.PipePath, "-map", "0:v", "-map", "1:0", "-c:v", "copy", "-c:a", "flac", "out.mkv"]), string.Join(" ", mixed.Arguments));
        }
    }

    [Fact]
    public async Task A_filter_graph_reads_from_the_bridge()
    {
        var path = await Fixtures.GetAsync("video.mp4");
        using (WindowsMedia.ForceWindows("h264", "aac"))
        {
            await using var prepared = await FfmpegCommand.PrepareAsync(
                ["-i", path, "-filter_complex", "[0:v]scale=80:60[v];[0:a]volume=0.5[a]", "-map", "[v]", "-map", "[a]", "out.mkv"], default);
            Assert.Single(prepared.Bridges);
            Assert.Contains("[1:0]scale=80:60[v];[1:1]volume=0.5[a]", prepared.Arguments);
        }
    }

    [Fact]
    public async Task Without_a_map_FFmpeg_s_own_choice_is_kept()
    {
        var path = await Fixtures.GetAsync("video.mp4");
        using (WindowsMedia.ForceWindows("h264", "aac"))
        {
            await using var prepared = await FfmpegCommand.PrepareAsync(["-i", path, "-c:v", "ffv1", "out.mkv"], default);
            Assert.Equal(["-i", path, "-f", "matroska", "-i", prepared.Bridges[0].PipePath, "-c:v", "ffv1", "-map", "1:0", "-map", "1:1", "out.mkv"], prepared.Arguments);
            await using var picture = await FfmpegCommand.PrepareAsync(["-i", path, "-frames:v", "1", "-f", "image2", "out.png"], default);
            Assert.Equal(["-map", "1:0", "out.png"], picture.Arguments.TakeLast(3));
        }
    }

    [Fact]
    public async Task Ten_bit_HEVC_keeps_its_ten_bits()
    {
        string path;
        try
        {
            path = await Fixtures.GetAsync("hevc10.mkv");
        }
        catch (InvalidOperationException)
        {
            return; // This FFmpeg can't make the sample.
        }
        var own = await FrameAsync(path, 0.5, "gray10le");
        byte[] windows;
        try
        {
            using (WindowsMedia.ForceWindows("hevc"))
                windows = await FrameAsync(path, 0.5, "gray10le");
        }
        catch (MediaException e) when (e.Message == WindowsMedia.Texts[1])
        {
            return; // No HEVC Video Extensions on this PC.
        }
        Assert.Equal(own.Length, windows.Length);
        var a = Floats(own);
        var differing = 0;
        for (var i = 0; i + 1 < own.Length; i += 2)
        {
            var x = own[i] | own[i + 1] << 8;
            var y = windows[i] | windows[i + 1] << 8;
            if (Math.Abs(x - y) > 2)
                differing++;
        }
        Assert.True(differing < own.Length / 2000, $"{differing} pixels differ");
        Assert.NotNull(a);
    }

    [Fact]
    public async Task What_Windows_can_t_decode_says_so()
    {
        var path = await Fixtures.GetAsync("odd.mkv");
        using (WindowsMedia.ForceWindows("ffv1"))
        {
            var e = await Assert.ThrowsAsync<MediaException>(() => FrameAsync(path, 0));
            Assert.Equal(WindowsMedia.Texts[2], e.Message);
        }
    }

    /// <summary>
    /// Windows doesn't keep a file's order of streams (it lists an MP4's sound tracks last first, before its picture):
    /// each track has to be told by what it holds.
    /// </summary>
    [Theory]
    [InlineData("mp4", "v,220,550")]
    [InlineData("mp4", "220,v,550,880")]
    [InlineData("mov", "v,220,550")]
    [InlineData("mkv", "v,220,550")]
    [InlineData("m4a", "220,550")]
    public async Task Each_sound_track_of_several_comes_through_as_itself(string extension, string layout)
    {
        var file = Path.Combine(Fixtures.NewDir(), "tracks." + extension);
        var parts = layout.Split(',');
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-y" };
        foreach (var part in parts)
            args.AddRange(part == "v" ? ["-f", "lavfi", "-i", "testsrc2=s=160x120:r=15:d=3"] : ["-f", "lavfi", "-i", $"sine=f={part}:r=48000:d=3"]);
        for (var i = 0; i < parts.Length; i++)
            args.AddRange(["-map", Inv.I(i)]);
        args.AddRange(["-c:v", "h264_mf", "-pix_fmt", "nv12", "-c:a", "aac_mf", "-b:a", "128k", "-sample_fmt", "s16", file]);
        var made = await Petal.Core.Util.ProcessRunner.RunAsync(Fixtures.Ffmpeg, args);
        Assert.True(made.Succeeded, made.StandardError);

        var tones = parts.Where(p => p != "v").Select(int.Parse).ToList();
        for (var n = 0; n < tones.Count; n++)
        {
            byte[] bytes;
            using (WindowsMedia.ForceWindows("h264", "aac"))
                bytes = await RawAsync("-i", file, "-map", $"0:a:{n}", "-ac", "1", "-ar", "48000", "-c:a", "pcm_f32le", "-f", "f32le", "-");
            var second = Floats(bytes).Skip(48000).Take(48000).ToArray();
            double Level(int frequency)
            {
                double sine = 0, cosine = 0;
                for (var i = 0; i < second.Length; i++)
                {
                    sine += second[i] * Math.Sin(2 * Math.PI * frequency * i / 48000);
                    cosine += second[i] * Math.Cos(2 * Math.PI * frequency * i / 48000);
                }
                return 2 * Math.Sqrt(sine * sine + cosine * cosine) / Math.Max(1, second.Length);
            }
            Assert.Equal(tones[n], tones.MaxBy(Level));
            Assert.True(Level(tones[n]) > 0.1, $"a:{n} {Level(tones[n]):0.000}");
        }
    }

    [Theory]
    [InlineData("0", 0)]
    [InlineData("1.5", 1.5)]
    [InlineData("00:01:02.5", 62.5)]
    [InlineData("1:02", 62)]
    [InlineData("250ms", 0.25)]
    [InlineData("-3", -3)]
    public void FFmpeg_s_times_are_read(string text, double seconds) =>
        Assert.Equal(seconds, FfmpegCommand.Time(text).TotalSeconds, 6);
}
