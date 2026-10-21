using Petal.Core.Formats;
using Petal.Core.Jobs;
using Petal.Engines.Media.Internal;
using F = Petal.Core.Formats.Formats;

namespace Petal.Engines.Media.Tests;

public class ConverterTests
{
    private static readonly MediaConverter Converter = new();

    public static TheoryData<string, string> MediaPaths()
    {
        var data = new TheoryData<string, string>();
        foreach (var (from, to) in ConversionMatrix.AllPaths())
        {
            if (from.Family is FormatFamily.Audio or FormatFamily.Video)
                data.Add(from.Id, to.Id);
        }
        return data;
    }

    [Fact]
    public void Matrix_has_every_audio_and_video_path_covered_by_this_converter()
    {
        var paths = MediaPaths();
        Assert.Equal(8 * 7 + 6 * 7 + 6, paths.Count);
        foreach (var (from, to) in ConversionMatrix.AllPaths().Where(p => p.From.Family is FormatFamily.Audio or FormatFamily.Video))
            Assert.True(Converter.CanConvert(from, to), $"{from} → {to}");
    }

    [Fact]
    public void Does_not_claim_other_families_or_impossible_pairs()
    {
        Assert.False(Converter.CanConvert(F.Jpg, F.Png));
        Assert.False(Converter.CanConvert(F.Mp3, F.Mp4));
        Assert.False(Converter.CanConvert(F.Gif, F.Mp3));
        Assert.False(Converter.CanConvert(F.Mp4, F.Mp4));
        Assert.False(Converter.CanConvert(F.Pdf, F.Mp3));
        Assert.True(Converter.CanConvert(F.Mkv, F.Flac));
    }

    [Theory]
    [MemberData(nameof(MediaPaths))]
    public async Task Converts(string fromId, string toId)
    {
        var from = MediaAssert.Format(fromId);
        var to = MediaAssert.Format(toId);
        var input = await Fixtures.ForFormatAsync(from);
        var dir = Fixtures.NewDir();
        var output = Path.Combine(dir, "out." + to.WriteExtension);
        var progress = new RecordingProgress();

        var result = await Converter.ConvertAsync(new ConversionJob(input, from, to, output, new ConversionSettings()), progress, CancellationToken.None);

        Assert.Equal(output, result.Primary);
        var source = await MediaProbe.ProbeAsync(input);
        var info = await MediaAssert.ValidAsync(output, duration: 1.0, tolerance: 0.3);
        progress.AssertCompleteAndMonotonic();
        MediaAssert.NoTempFiles(dir);

        var spec = Containers.For(to);
        if (to.Family == FormatFamily.Video)
        {
            Assert.True(info.HasVideo);
            Assert.Equal(source.Width, info.Width);
            Assert.Equal(source.Height, info.Height);
            var expectedVideo = from != F.Gif && spec.CopyVideo.Contains(source.VideoCodec!) ? source.VideoCodec : DefaultCodec(spec.Video);
            Assert.Equal(expectedVideo, info.VideoCodec);
            Assert.Equal(source.HasAudio && to != F.Gif, info.HasAudio);
        }
        else
        {
            Assert.True(info.HasAudio);
            Assert.False(info.HasVideo);
            Assert.Equal(2, info.AudioChannels);
        }
    }

    private static string DefaultCodec(VideoCodecKind kind) => kind switch
    {
        VideoCodecKind.H264 => "h264",
        VideoCodecKind.Vp9 => "vp9",
        VideoCodecKind.Mpeg4 => "mpeg4",
        VideoCodecKind.Wmv2 => "wmv2",
        VideoCodecKind.Gif => "gif",
        _ => "none",
    };

    [Fact]
    public async Task Mov_with_h264_and_aac_is_remuxed_into_mp4_without_reencoding()
    {
        var input = await Fixtures.GetAsync("video.mov");
        var info = await MediaProbe.ProbeAsync(input);
        var job = new ConversionJob(input, F.Mov, F.Mp4, Path.Combine(Fixtures.NewDir(), "remux.mp4"), new ConversionSettings());

        var plan = MediaConverter.Plan(job, info);
        Assert.Equal(StreamMode.Copy, plan.Video);
        Assert.Equal(StreamMode.Copy, plan.Audio);

        await Converter.ConvertAsync(job, null, CancellationToken.None);
        var output = await MediaAssert.ValidAsync(job.OutputPath, 1.0);
        Assert.Equal("h264", output.VideoCodec);
        Assert.Equal("aac", output.AudioCodec);
        // A copied stream keeps its exact bitrate.
        Assert.InRange(output.VideoBitRate, info.VideoBitRate * 0.97, info.VideoBitRate * 1.03);
    }

    [Fact]
    public async Task Hevc_is_copied_into_mp4_with_the_hvc1_tag()
    {
        var input = await Fixtures.GetAsync("hevc.mov");
        var output = Path.Combine(Fixtures.NewDir(), "hevc.mp4");
        await Converter.ConvertAsync(new ConversionJob(input, F.Mov, F.Mp4, output, new ConversionSettings()), null, CancellationToken.None);

        var info = await MediaAssert.ValidAsync(output, 1.0);
        Assert.Equal("hevc", info.VideoCodec);
        Assert.Equal("hvc1", info.Video!.CodecTag);
    }

    [Fact]
    public async Task Webm_streams_are_copied_into_mkv_but_reencoded_for_avi()
    {
        var input = await Fixtures.GetAsync("video.webm");
        var info = await MediaProbe.ProbeAsync(input);

        var mkv = MediaConverter.Plan(new ConversionJob(input, F.Webm, F.Mkv, "x.mkv", new ConversionSettings()), info);
        Assert.Equal(StreamMode.Copy, mkv.Video);
        Assert.Equal(StreamMode.Copy, mkv.Audio);

        var avi = MediaConverter.Plan(new ConversionJob(input, F.Webm, F.Avi, "x.avi", new ConversionSettings()), info);
        Assert.Equal(StreamMode.Encode, avi.Video);
        Assert.Equal(StreamMode.Encode, avi.Audio);
    }

    [Fact]
    public async Task Ogg_opus_is_remuxed_to_opus()
    {
        var input = await Fixtures.GetAsync("audio.opus");
        var ogg = Path.Combine(Fixtures.NewDir(), "a.ogg");
        await Converter.ConvertAsync(new ConversionJob(input, F.Opus, F.Ogg, ogg, new ConversionSettings()), null, CancellationToken.None);
        var info = await MediaAssert.ValidAsync(ogg, 1.0);
        Assert.Equal("opus", info.AudioCodec);
    }

    [Fact]
    public async Task Video_to_gif_uses_the_settings_frame_rate_and_size()
    {
        var input = await Fixtures.GetAsync("video.mp4");
        var output = Path.Combine(Fixtures.NewDir(), "small.gif");
        var settings = new ConversionSettings { GifFps = 8, GifMaxWidth = 80 };
        await Converter.ConvertAsync(new ConversionJob(input, F.Mp4, F.Gif, output, settings), null, CancellationToken.None);

        var info = await MediaAssert.ValidAsync(output, 1.0, 0.3);
        Assert.Equal("gif", info.VideoCodec);
        Assert.Equal(80, info.Width);
        Assert.Equal(60, info.Height);
        Assert.InRange(info.FrameRate, 6, 9);
    }

    [Fact]
    public async Task Odd_sized_gif_becomes_even_yuv420p_video()
    {
        var input = await Fixtures.GetAsync("odd.gif");
        var output = Path.Combine(Fixtures.NewDir(), "odd.mp4");
        await Converter.ConvertAsync(new ConversionJob(input, F.Gif, F.Mp4, output, new ConversionSettings()), null, CancellationToken.None);

        var info = await MediaAssert.ValidAsync(output, 1.0, 0.3);
        Assert.Equal((160, 120), (info.Width, info.Height));
        Assert.Equal("yuv420p", info.Video!.PixelFormat);
        Assert.False(info.HasAudio);
    }

    [Fact]
    public async Task Odd_dimensions_are_made_even_for_h264()
    {
        var input = await Fixtures.GetAsync("odd.mkv");
        var output = Path.Combine(Fixtures.NewDir(), "odd.mp4");
        await Converter.ConvertAsync(new ConversionJob(input, F.Mkv, F.Mp4, output, new ConversionSettings()), null, CancellationToken.None);
        var info = await MediaAssert.ValidAsync(output, 1.0);
        Assert.Equal((160, 120), (info.Width, info.Height));
    }

    [Fact]
    public async Task Rotation_is_kept_when_copying_and_applied_when_reencoding()
    {
        var input = await Fixtures.GetAsync("rotated.mp4");
        var source = await MediaProbe.ProbeAsync(input);
        Assert.Equal(270, source.Rotation);
        Assert.Equal((120, 160), (source.Width, source.Height));

        var mov = Path.Combine(Fixtures.NewDir(), "rot.mov");
        await Converter.ConvertAsync(new ConversionJob(input, F.Mp4, F.Mov, mov, new ConversionSettings()), null, CancellationToken.None);
        var copied = await MediaAssert.ValidAsync(mov);
        Assert.Equal(270, copied.Rotation);
        Assert.Equal((120, 160), (copied.Width, copied.Height));

        var webm = Path.Combine(Fixtures.NewDir(), "rot.webm");
        await Converter.ConvertAsync(new ConversionJob(input, F.Mp4, F.Webm, webm, new ConversionSettings()), null, CancellationToken.None);
        var encoded = await MediaAssert.ValidAsync(webm);
        Assert.Equal(0, encoded.Rotation);
        Assert.Equal((120, 160), (encoded.Width, encoded.Height));
    }

    [Fact]
    public async Task Video_without_audio_converts_but_cannot_become_mp3()
    {
        var input = await Fixtures.GetAsync("silent.mp4");
        var dir = Fixtures.NewDir();
        var webm = Path.Combine(dir, "silent.webm");
        await Converter.ConvertAsync(new ConversionJob(input, F.Mp4, F.Webm, webm, new ConversionSettings()), null, CancellationToken.None);
        var info = await MediaAssert.ValidAsync(webm, 1.0);
        Assert.False(info.HasAudio);

        var mp3 = Path.Combine(dir, "silent.mp3");
        await Assert.ThrowsAsync<MediaException>(() =>
            Converter.ConvertAsync(new ConversionJob(input, F.Mp4, F.Mp3, mp3, new ConversionSettings()), null, CancellationToken.None));
        Assert.False(File.Exists(mp3));
    }

    [Fact]
    public async Task Metadata_and_cover_art_survive_audio_conversion()
    {
        var input = await Fixtures.GetAsync("tagged.mp3");
        var output = Path.Combine(Fixtures.NewDir(), "tagged.flac");
        await Converter.ConvertAsync(new ConversionJob(input, F.Mp3, F.Flac, output, new ConversionSettings()), null, CancellationToken.None);
        var info = await MediaAssert.ValidAsync(output, 1.0);
        Assert.Equal("Petal Test", info.Tags["title"]);
        Assert.NotNull(info.CoverArt);
    }

    [Fact]
    public async Task Hardware_setting_falls_back_to_software_when_needed()
    {
        var input = await Fixtures.GetAsync("video.webm");
        var output = Path.Combine(Fixtures.NewDir(), "hw.mp4");
        var settings = new ConversionSettings { HardwareVideoEncoding = true };
        await Converter.ConvertAsync(new ConversionJob(input, F.Webm, F.Mp4, output, settings), null, CancellationToken.None);
        var info = await MediaAssert.ValidAsync(output, 1.0);
        Assert.Equal("h264", info.VideoCodec);
    }

    [Fact]
    public async Task Progress_reports_intermediate_values_and_ends_at_one()
    {
        var input = await Fixtures.GetAsync("hq.mp4");
        var output = Path.Combine(Fixtures.NewDir(), "progress.webm");
        var progress = new RecordingProgress();
        await Converter.ConvertAsync(new ConversionJob(input, F.Mp4, F.Webm, output, new ConversionSettings()), progress, CancellationToken.None);
        progress.AssertCompleteAndMonotonic();
        Assert.Contains(progress.Values, v => v is > 0 and < 1);
    }

    [Fact]
    public async Task Cancellation_kills_ffmpeg_and_leaves_no_files()
    {
        var input = await Fixtures.GetAsync("long.mp4");
        var dir = Fixtures.NewDir();
        var output = Path.Combine(dir, "cancelled.webm");
        using var cts = new CancellationTokenSource();
        var progress = new RecordingProgress();
        progress.Reported += v =>
        {
            if (v > 0.01)
                cts.Cancel();
        };
        cts.CancelAfter(TimeSpan.FromSeconds(8));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Converter.ConvertAsync(new ConversionJob(input, F.Mp4, F.Webm, output, new ConversionSettings()), progress, cts.Token));

        Assert.False(File.Exists(output));
        Assert.Empty(Directory.EnumerateFileSystemEntries(dir));
    }

    [Fact]
    public async Task Never_overwrites_the_input()
    {
        var input = await Fixtures.GetAsync("video.mp4");
        await Assert.ThrowsAsync<ArgumentException>(() =>
            Converter.ConvertAsync(new ConversionJob(input, F.Mp4, F.Mov, input, new ConversionSettings()), null, CancellationToken.None));
    }

    [Fact]
    public async Task Probe_reports_streams_and_dimensions()
    {
        var info = await MediaProbe.ProbeAsync(await Fixtures.GetAsync("video.mp4"));
        Assert.Contains("mp4", info.Container);
        Assert.InRange(info.DurationSeconds, 0.9, 1.1);
        Assert.Equal((160, 120), (info.Width, info.Height));
        Assert.Equal(15, info.FrameRate, 2);
        Assert.True(info.HasVideo);
        Assert.True(info.HasAudio);
        Assert.Equal("h264", info.VideoCodec);
        Assert.Equal("aac", info.AudioCodec);
        Assert.Equal(2, info.AudioChannels);
        Assert.Equal(44100, info.SampleRate);
        Assert.True(info.BitRate > 0);
        Assert.True(info.FileSize > 0);

        var audio = await MediaProbe.ProbeAsync(await Fixtures.GetAsync("tagged.mp3"));
        Assert.False(audio.HasVideo);
        Assert.NotNull(audio.CoverArt);
        Assert.Equal("Petal Artist", audio.Tags["artist"]);
    }

    /// <summary>
    /// FLAC with odd block sizes (FFmpeg 9 copies the source's frame size: 47, 576, 1152…) is valid
    /// but Windows can't play it; Petal always writes 4608-sample blocks.
    /// </summary>
    [Theory]
    [InlineData("mp3")]
    [InlineData("opus")]
    [InlineData("wma")]
    [InlineData("m4a")]
    public async Task Flac_uses_the_standard_block_size_whatever_the_source(string sourceId)
    {
        var from = MediaAssert.Format(sourceId);
        var input = await Fixtures.ForFormatAsync(from);
        var output = Path.Combine(Fixtures.NewDir(), "out.flac");
        await Converter.ConvertAsync(new ConversionJob(input, from, F.Flac, output, new ConversionSettings()), null, CancellationToken.None);
        var header = File.ReadAllBytes(output)[..18];
        Assert.Equal("fLaC", System.Text.Encoding.ASCII.GetString(header, 0, 4));
        int min = (header[8] << 8) | header[9], max = (header[10] << 8) | header[11];
        Assert.Equal((4608, 4608), (min, max));
    }

    [Fact]
    public async Task Probe_rejects_files_that_are_not_media()
    {
        var path = Path.Combine(Fixtures.NewDir(), "notes.mp4");
        await File.WriteAllTextAsync(path, "not a video");
        await Assert.ThrowsAsync<MediaException>(() => MediaProbe.ProbeAsync(path));
    }
}

public class FavouriteMediaTests
{
    private static readonly MediaConverter Converter = new();

    [Fact]
    public async Task Plans_scaling_and_re_encoding_only_when_asked()
    {
        var big = await Fixtures.GetAsync("big.mov");
        var info = await MediaProbe.ProbeAsync(big);

        // A plain MOV to MP4 copies the H.264 stream.
        var plain = MediaConverter.Plan(new ConversionJob(big, F.Mov, F.Mp4, "out.mp4", new ConversionSettings()), info);
        Assert.Equal(StreamMode.Copy, plain.Video);

        // With a size limit it is encoded and scaled, keeping 16:9 with even sides.
        var small = MediaConverter.Plan(new ConversionJob(big, F.Mov, F.Mp4, "out.mp4", new ConversionSettings { MaxLongEdge = 160 }), info);
        Assert.Equal(StreamMode.Encode, small.Video);
        Assert.Equal("scale=160:90:flags=lanczos", small.VideoFilter);

        // A limit the video already fits does nothing.
        var roomy = MediaConverter.Plan(new ConversionJob(big, F.Mov, F.Mp4, "out.mp4", new ConversionSettings { MaxLongEdge = 1280 }), info);
        Assert.Equal(StreamMode.Copy, roomy.Video);

        // Saving in its own format always re-encodes, or nothing would change.
        var mp3 = await Fixtures.GetAsync("audio.mp3");
        var resave = MediaConverter.Plan(new ConversionJob(mp3, F.Mp3, F.Mp3, "out.mp3", new ConversionSettings { AudioBitrateKbps = 96 }), await MediaProbe.ProbeAsync(mp3));
        Assert.Equal(StreamMode.Encode, resave.Audio);
        Assert.True(Converter.CanResave(F.Mp4));
        Assert.True(Converter.CanResave(F.Mp3));
        Assert.False(Converter.CanResave(F.Png));
    }

    [Fact]
    public async Task Makes_a_smaller_copy_in_the_same_format()
    {
        var input = await Fixtures.GetAsync("hq.mp4");
        var dir = Fixtures.NewDir();
        var output = Path.Combine(dir, "hq 160.mp4");

        await Converter.ConvertAsync(new ConversionJob(input, F.Mp4, F.Mp4, output, new ConversionSettings { MaxLongEdge = 160, VideoCrf = 28 }), null, CancellationToken.None);

        var info = await MediaAssert.ValidAsync(output, duration: 5.0, tolerance: 0.3);
        Assert.Equal((160, 120), (info.Video!.Width, info.Video.Height));
        Assert.True(info.HasAudio);
        Assert.True(new FileInfo(output).Length < new FileInfo(input).Length);
        MediaAssert.NoTempFiles(dir);
    }
}
