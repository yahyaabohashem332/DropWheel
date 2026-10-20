namespace Petal.Engines.Media.Tests;

public class PreviewTests
{
    [Fact]
    public async Task Extracts_a_scaled_frame_as_png()
    {
        var input = await Fixtures.GetAsync("hq.mp4");
        var png = await MediaPreview.ExtractFrameAsync(input, TimeSpan.FromSeconds(2.5), maxWidth: 100);
        Assert.Equal((100, 76), Fixtures.PngSize(png));

        var full = await MediaPreview.ExtractFrameAsync(input, TimeSpan.FromSeconds(1));
        Assert.Equal((320, 240), Fixtures.PngSize(full));
    }

    [Fact]
    public async Task Frame_past_the_end_falls_back_to_the_last_frame()
    {
        var input = await Fixtures.GetAsync("video.mp4");
        var png = await MediaPreview.ExtractFrameAsync(input, TimeSpan.FromSeconds(30), maxWidth: 80);
        Assert.Equal(80, Fixtures.PngSize(png).Width);
    }

    [Fact]
    public async Task Frames_are_display_oriented()
    {
        var input = await Fixtures.GetAsync("rotated.mp4");
        var png = await MediaPreview.ExtractFrameAsync(input, TimeSpan.FromSeconds(0.5));
        Assert.Equal((120, 160), Fixtures.PngSize(png));
    }

    [Fact]
    public async Task Filmstrip_has_the_requested_number_of_thumbnails()
    {
        var input = await Fixtures.GetAsync("hq.mp4");
        var strip = await MediaPreview.ExtractFilmstripAsync(input, 6, maxWidth: 64);
        Assert.Equal(6, strip.Count);
        Assert.All(strip, png => Assert.Equal(64, Fixtures.PngSize(png).Width));
        // Different moments of a moving test pattern give different pictures.
        Assert.NotEqual(Convert.ToBase64String(strip[0]), Convert.ToBase64String(strip[5]));
    }

    [Fact]
    public async Task Waveform_buckets_follow_the_signal()
    {
        var input = await Fixtures.GetAsync("audio.wav");
        var wave = await MediaPreview.GetWaveformAsync(input, 200);
        Assert.Equal(200, wave.Buckets);
        Assert.Equal(200, wave.Min.Length);
        Assert.Equal(200, wave.Rms.Length);
        // lavfi's sine has an amplitude of 1/8.
        Assert.InRange(wave.Peak, 0.11, 0.14);
        Assert.InRange(wave.Rms.Average(), 0.075, 0.1);
        Assert.InRange(wave.Duration.TotalSeconds, 0.95, 1.05);
        Assert.All(wave.Max, m => Assert.True(m > 0.05f));
    }

    [Fact]
    public async Task Waveform_can_show_a_single_channel_and_a_range()
    {
        var input = await Fixtures.GetAsync("left_only.wav");
        var left = await MediaPreview.GetWaveformAsync(input, 50, new WaveformOptions(WaveformChannel.Left));
        var right = await MediaPreview.GetWaveformAsync(input, 50, new WaveformOptions(WaveformChannel.Right));
        Assert.InRange(left.Peak, 0.45, 0.55);
        Assert.True(right.Peak < 0.01);

        var padded = await Fixtures.GetAsync("padded.wav");
        var silentPart = await MediaPreview.GetWaveformAsync(padded, 20, new WaveformOptions(Start: TimeSpan.FromSeconds(0.1), Length: TimeSpan.FromSeconds(0.7)));
        var loudPart = await MediaPreview.GetWaveformAsync(padded, 20, new WaveformOptions(Start: TimeSpan.FromSeconds(1.2), Length: TimeSpan.FromSeconds(0.6)));
        Assert.True(silentPart.Peak < 0.01);
        Assert.InRange(loudPart.Peak, 0.27, 0.33);
    }

    [Fact]
    public async Task Decodes_pcm_at_the_requested_format()
    {
        var input = await Fixtures.GetAsync("audio.mp3");
        var pcm = await MediaPreview.DecodePcmAsync(input, new PcmDecodeOptions(SampleRate: 22050, Channels: 1));
        Assert.Equal(1, pcm.Channels);
        Assert.Equal(22050, pcm.SampleRate);
        Assert.InRange(pcm.FrameCount, 21800, 23000);
        Assert.InRange(pcm.Samples.Max(), 0.1f, 0.15f);

        var stereo = await MediaPreview.DecodePcmAsync(input, new PcmDecodeOptions(Start: TimeSpan.FromSeconds(0.5), Length: TimeSpan.FromSeconds(0.25)));
        Assert.Equal(2, stereo.Channels);
        Assert.InRange(stereo.Duration.TotalSeconds, 0.24, 0.26);
        Assert.Equal(stereo.FrameCount * 2, stereo.Samples.Length);
    }

    [Fact]
    public async Task Pcm_preview_can_apply_a_tool_filter()
    {
        var input = await Fixtures.GetAsync("padded.wav");
        var filter = AudioTools.BleepPreviewFilter(new BleepOptions([new MediaTimeRange(0.2, 0.8)], ToneVolume: 0.5));
        var pcm = await MediaPreview.DecodePcmAsync(input, new PcmDecodeOptions(SampleRate: 44100, Channels: 1, Length: TimeSpan.FromSeconds(0.9), AudioFilter: filter));
        var bleeped = pcm.Samples.Skip(44100 * 3 / 10).Take(44100 * 4 / 10);
        Assert.InRange(bleeped.Max(), 0.45f, 0.55f);
        Assert.True(pcm.Samples.Take(44100 / 10).Max() < 0.01f);
    }
}
