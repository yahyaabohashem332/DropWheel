namespace Petal.Engines.Media.Tests;

public class AudioToolTests
{
    [Fact]
    public void Wav_and_aiff_compress_to_flac()
    {
        Assert.Equal("flac", MediaCompressor.CompressedExtension(@"C:\a\song.wav"));
        Assert.Equal("flac", MediaCompressor.CompressedExtension(@"C:\a\song.aif"));
        Assert.Equal("mp3", MediaCompressor.CompressedExtension(@"C:\a\song.mp3"));
        Assert.Equal("MOV", MediaCompressor.CompressedExtension(@"C:\a\clip.MOV"));
    }

    [Fact]
    public async Task Compress_wav_is_lossless_flac()
    {
        var input = await Fixtures.GetAsync("audio.wav");
        var output = Path.Combine(Fixtures.NewDir(), "audio Compressed." + MediaCompressor.CompressedExtension(input));
        var result = await MediaCompressor.CompressAsync(input, output);
        var info = await MediaAssert.ValidAsync(output, 1.0);
        Assert.Equal("flac", info.AudioCodec);
        Assert.Equal(2, info.AudioChannels);
        Assert.True(result.OutputBytes < result.OriginalBytes / 2);
    }

    [Theory]
    [InlineData("music.mp3", CompressPreset.Balanced)]
    [InlineData("music.mp3", CompressPreset.Strong)]
    [InlineData("audio.m4a", CompressPreset.Balanced)]
    [InlineData("audio.opus", CompressPreset.Strong)]
    [InlineData("audio.ogg", CompressPreset.Balanced)]
    [InlineData("audio.wma", CompressPreset.Strong)]
    [InlineData("audio.flac", CompressPreset.Balanced)]
    public async Task Compress_lossy_audio_keeps_the_format_and_shrinks(string fixture, CompressPreset preset)
    {
        var input = await Fixtures.GetAsync(fixture);
        var source = await MediaProbe.ProbeAsync(input);
        var output = Path.Combine(Fixtures.NewDir(), "c" + Path.GetExtension(input));
        var progress = new RecordingProgress();
        var result = await MediaCompressor.CompressAsync(input, output, new CompressOptions(preset), progress);
        var info = await MediaAssert.ValidAsync(output, source.DurationSeconds);
        Assert.Equal(source.AudioCodec, info.AudioCodec);
        Assert.True(result.OutputBytes <= result.OriginalBytes, $"{result.OutputBytes} > {result.OriginalBytes}");
        progress.AssertCompleteAndMonotonic();
    }

    [Fact]
    public async Task Compress_audio_to_a_target_size()
    {
        var input = await Fixtures.GetAsync("music.mp3");
        var target = new FileInfo(input).Length / 4;
        var result = await MediaCompressor.CompressAsync(input, Path.Combine(Fixtures.NewDir(), "t.mp3"), new CompressOptions(TargetSizeBytes: target));
        Assert.InRange(result.OutputBytes, target * 0.6, target * 1.15);
    }

    [Fact]
    public async Task Normalize_reaches_the_target_loudness_and_reports_levels()
    {
        var input = await Fixtures.GetAsync("quiet.wav");
        var output = Path.Combine(Fixtures.NewDir(), "normalized.wav");
        var progress = new RecordingProgress();
        var result = await AudioTools.NormalizeAsync(input, output, new NormalizeOptions(), progress);

        Assert.True(result.Input.IntegratedLufs < -30, $"input {result.Input.IntegratedLufs}");
        Assert.InRange(result.Output.IntegratedLufs, -17.5, -14.5);
        Assert.True(result.Output.TruePeakDbtp <= -1.0, $"tp {result.Output.TruePeakDbtp}");
        var info = await MediaAssert.ValidAsync(output, 6.0);
        Assert.Equal(44100, info.SampleRate);
        Assert.Equal("pcm_s16le", info.AudioCodec);
        progress.AssertCompleteAndMonotonic();

        var measured = await AudioTools.MeasureLoudnessAsync(output);
        Assert.InRange(measured.IntegratedLufs, -17.5, -14.5);
    }

    [Fact]
    public async Task Normalize_the_audio_track_of_a_video()
    {
        var input = await Fixtures.GetAsync("hq.mp4");
        var output = Path.Combine(Fixtures.NewDir(), "normalized.mp4");
        var result = await AudioTools.NormalizeAsync(input, output, new NormalizeOptions(TargetLufs: -20));
        Assert.InRange(result.Output.IntegratedLufs, -21.5, -18.5);
        var info = await MediaAssert.ValidAsync(output, 5.0);
        Assert.True(info.HasVideo);
        Assert.Equal("h264", info.VideoCodec);
    }

    [Fact]
    public async Task Trim_audio_keeps_the_range()
    {
        var input = await Fixtures.GetAsync("padded.wav");
        var output = Path.Combine(Fixtures.NewDir(), "trim.wav");
        await AudioTools.TrimAsync(input, output, new TrimAudioOptions(0.5, 2.0));
        await MediaAssert.ValidAsync(output, 1.5, 0.02);
    }

    [Fact]
    public async Task Trim_silent_ends_keeps_the_pause_inside()
    {
        var input = await Fixtures.GetAsync("padded.wav");
        var range = await AudioTools.DetectSilentEndsAsync(input);
        Assert.InRange(range.Start, 0.95, 1.05);
        Assert.InRange(range.End, 3.45, 3.55);

        var output = Path.Combine(Fixtures.NewDir(), "auto.wav");
        var progress = new RecordingProgress();
        await AudioTools.TrimAsync(input, output, new TrimAudioOptions(0, 4.5, TrimSilentEnds: true), progress);
        await MediaAssert.ValidAsync(output, 2.5, 0.08);
        progress.AssertCompleteAndMonotonic();

        // The 0.5 s pause between the tones is still there.
        var pause = await MediaPreview.GetWaveformAsync(output, 10, new WaveformOptions(Start: TimeSpan.FromSeconds(1.1), Length: TimeSpan.FromSeconds(0.3)));
        Assert.True(pause.Peak < 0.01);
    }

    [Fact]
    public async Task Trim_mp3_keeps_cover_art()
    {
        var input = await Fixtures.GetAsync("tagged.mp3");
        var output = Path.Combine(Fixtures.NewDir(), "t.mp3");
        await AudioTools.TrimAsync(input, output, new TrimAudioOptions(0.2, 0.7));
        var info = await MediaAssert.ValidAsync(output, 0.5, 0.06);
        Assert.NotNull(info.CoverArt);
        Assert.Equal("Petal Test", info.Tags["title"]);
    }

    [Fact]
    public async Task Mono_becomes_stereo_with_independent_volumes()
    {
        var input = await Fixtures.GetAsync("mono.wav");
        var output = Path.Combine(Fixtures.NewDir(), "stereo.wav");
        await AudioTools.ConvertChannelsAsync(input, output, new AudioChannelsOptions(AudioChannelMode.Stereo, LeftVolume: 0, RightVolume: 2));
        var info = await MediaAssert.ValidAsync(output, 1.0);
        Assert.Equal(2, info.AudioChannels);

        var left = await MediaPreview.GetWaveformAsync(output, 20, new WaveformOptions(WaveformChannel.Left));
        var right = await MediaPreview.GetWaveformAsync(output, 20, new WaveformOptions(WaveformChannel.Right));
        Assert.True(left.Peak < 0.01);
        Assert.InRange(right.Peak, 0.22, 0.28);
    }

    [Fact]
    public async Task Stereo_becomes_mono()
    {
        var input = await Fixtures.GetAsync("audio.mp3");
        var output = Path.Combine(Fixtures.NewDir(), "mono.mp3");
        await AudioTools.ConvertChannelsAsync(input, output, new AudioChannelsOptions(AudioChannelMode.Mono));
        var info = await MediaAssert.ValidAsync(output, 1.0, 0.1);
        Assert.Equal(1, info.AudioChannels);
    }

    [Theory]
    [InlineData(VisualizerShape.Landscape, 1920, 1080)]
    [InlineData(VisualizerShape.Portrait, 1080, 1920)]
    [InlineData(VisualizerShape.Square, 1080, 1080)]
    public async Task Visualizer_makes_an_mp4_of_the_requested_shape(VisualizerShape shape, int width, int height)
    {
        var input = await Fixtures.GetAsync("audio.mp3");
        var output = Path.Combine(Fixtures.NewDir(), "viz.mp4");
        var progress = new RecordingProgress();
        await AudioTools.CreateVisualizerAsync(input, output, new AudioVisualizerOptions(shape), progress);
        var info = await MediaAssert.ValidAsync(output, 1.0, 0.15);
        Assert.Equal((width, height), (info.Width, info.Height));
        Assert.Equal("h264", info.VideoCodec);
        Assert.Equal("aac", info.AudioCodec);
        progress.AssertCompleteAndMonotonic();

        // Accent-coloured bars on black: the middle row has both.
        var frame = await Fixtures.RgbFrameAsync(output, 0.5);
        var row = height / 2;
        var colored = 0;
        var black = 0;
        for (var x = 0; x < width; x++)
        {
            var i = (row * width + x) * 3;
            if (frame.Rgb[i] < 20 && frame.Rgb[i + 1] < 20 && frame.Rgb[i + 2] < 20)
                black++;
            else if (frame.Rgb[i + 2] > 180 && frame.Rgb[i] > 80 && frame.Rgb[i] < 160)
                colored++;
        }
        Assert.True(colored > width / 4, $"colored {colored}");
        Assert.True(black > width / 10, $"black {black}");
    }

    [Fact]
    public async Task Visualizer_with_a_still_image()
    {
        var input = await Fixtures.GetAsync("audio.wav");
        var image = await Fixtures.GetAsync("image.png");
        var output = Path.Combine(Fixtures.NewDir(), "still.mp4");
        await AudioTools.CreateVisualizerAsync(input, output, new AudioVisualizerOptions(VisualizerShape.Square, VisualizerStyle.StillImage, ImagePath: image));
        var info = await MediaAssert.ValidAsync(output, 1.0, 0.15);
        Assert.Equal((1080, 1080), (info.Width, info.Height));
        Assert.True(info.HasAudio);
    }

    [Fact]
    public async Task Bleep_replaces_ranges_with_a_tone()
    {
        var input = await Fixtures.GetAsync("padded.wav");
        var output = Path.Combine(Fixtures.NewDir(), "bleeped.wav");
        var options = new BleepOptions([new MediaTimeRange(0.2, 0.8), new MediaTimeRange(1.3, 1.6)], ToneVolume: 0.5);
        await AudioTools.BleepAsync(input, output, options);
        await MediaAssert.ValidAsync(output, 4.5, 0.02);

        var bleep1 = await MediaPreview.GetWaveformAsync(output, 10, new WaveformOptions(Start: TimeSpan.FromSeconds(0.3), Length: TimeSpan.FromSeconds(0.4)));
        var bleep2 = await MediaPreview.GetWaveformAsync(output, 10, new WaveformOptions(Start: TimeSpan.FromSeconds(1.35), Length: TimeSpan.FromSeconds(0.2)));
        var untouchedTone = await MediaPreview.GetWaveformAsync(output, 10, new WaveformOptions(Start: TimeSpan.FromSeconds(1.7), Length: TimeSpan.FromSeconds(0.2)));
        var untouchedSilence = await MediaPreview.GetWaveformAsync(output, 10, new WaveformOptions(Start: TimeSpan.FromSeconds(4.0), Length: TimeSpan.FromSeconds(0.4)));
        Assert.InRange(bleep1.Peak, 0.47, 0.53);
        Assert.InRange(bleep2.Peak, 0.47, 0.53);
        Assert.InRange(untouchedTone.Peak, 0.27, 0.33);
        Assert.True(untouchedSilence.Peak < 0.01);
    }

    [Fact]
    public async Task Raw_aac_files_stay_adts()
    {
        var input = await Fixtures.GetAsync("audio.aac");
        var output = Path.Combine(Fixtures.NewDir(), "bleeped.aac");
        await AudioTools.BleepAsync(input, output, new BleepOptions([new MediaTimeRange(0.2, 0.4)]));
        var info = await MediaAssert.ValidAsync(output, 1.0, 0.15);
        Assert.Equal("aac", info.Container);
        Assert.Equal("aac", info.AudioCodec);
    }

    [Fact]
    public async Task Bleep_the_audio_track_of_a_video()
    {
        var input = await Fixtures.GetAsync("video.mp4");
        var output = Path.Combine(Fixtures.NewDir(), "bleeped.mp4");
        await AudioTools.BleepAsync(input, output, new BleepOptions([new MediaTimeRange(0.2, 0.6)]));
        var info = await MediaAssert.ValidAsync(output, 1.0);
        Assert.True(info.HasVideo);
        Assert.True(info.HasAudio);
        var source = await MediaProbe.ProbeAsync(input);
        // The picture was copied, not re-encoded.
        Assert.Equal(source.VideoBitRate, info.VideoBitRate);
    }
}
