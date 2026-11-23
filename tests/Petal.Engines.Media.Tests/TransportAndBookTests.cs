using Petal.Core.Formats;
using Petal.Core.Jobs;
using Petal.Engines.Media.Internal;
using F = Petal.Core.Formats.Formats;

namespace Petal.Engines.Media.Tests;

/// <summary>1.24's video and audio: transport streams, audiobooks with their chapters, 3GP and FLV read.</summary>
public sealed class TransportAndBookTests
{
    private static readonly MediaConverter Converter = new();

    [Fact]
    public async Task Mp4_into_a_transport_stream_copies_its_streams()
    {
        var input = await Fixtures.GetAsync("video.mp4");
        var info = await MediaProbe.ProbeAsync(input);
        var job = new ConversionJob(input, F.Mp4, F.Ts, Path.Combine(Fixtures.NewDir(), "tv.ts"), new ConversionSettings());
        var plan = MediaConverter.Plan(job, info);
        Assert.Equal(StreamMode.Copy, plan.Video);
        Assert.Equal(StreamMode.Copy, plan.Audio);
        await Converter.ConvertAsync(job, null, CancellationToken.None);
        var output = await MediaAssert.ValidAsync(job.OutputPath, 1.0);
        Assert.Equal("h264", output.VideoCodec);
        Assert.Equal("aac", output.AudioCodec);
        Assert.Equal(F.Ts, F.FromPath(job.OutputPath));
    }

    [Fact]
    public async Task An_audiobook_keeps_its_chapters()
    {
        var dir = Fixtures.NewDir();
        var chapters = Path.Combine(dir, "chapters.txt");
        File.WriteAllText(chapters, ";FFMETADATA1\ntitle=Story\n[CHAPTER]\nTIMEBASE=1/1000\nSTART=0\nEND=1500\ntitle=Chapter One\n[CHAPTER]\nTIMEBASE=1/1000\nSTART=1500\nEND=3000\ntitle=الفصل الثاني\n");
        var m4a = Path.Combine(dir, "story.m4a");
        var made = await Petal.Core.Util.ProcessRunner.RunAsync(Fixtures.Ffmpeg,
            ["-hide_banner", "-nostdin", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "sine=f=440:r=44100:d=3", "-i", chapters, "-map_metadata", "1", "-map_chapters", "1",
             "-c:a", "aac_mf", "-b:a", "128k", "-ar", "44100", "-sample_fmt", "s16", m4a]);
        Assert.Equal(0, made.ExitCode);

        var job = new ConversionJob(m4a, F.M4a, F.M4b, Path.Combine(dir, "story.m4b"), new ConversionSettings());
        await Converter.ConvertAsync(job, null, CancellationToken.None);
        var book = await MediaProbe.ProbeAsync(job.OutputPath);
        Assert.Equal(["Chapter One", "الفصل الثاني"], book.Chapters.Select(c => c.Tags.GetValueOrDefault("title")));
        Assert.Equal("aac", book.AudioCodec);

        // And out of the book, as MP3, the sound comes through.
        var mp3 = new ConversionJob(job.OutputPath, F.M4b, F.Mp3, Path.Combine(dir, "story.mp3"), new ConversionSettings());
        await Converter.ConvertAsync(mp3, null, CancellationToken.None);
        Assert.Equal("mp3", (await MediaAssert.ValidAsync(mp3.OutputPath, 3.0)).AudioCodec);
    }

    [Theory]
    [InlineData("video.3gp", "h263")]
    [InlineData("video.flv", "h264")]
    public async Task Older_phone_and_flash_videos_become_mp4(string fixture, string codec)
    {
        var input = await Fixtures.GetAsync(fixture);
        Assert.Equal(codec, (await MediaProbe.ProbeAsync(input)).VideoCodec);
        var from = F.FromPath(input)!;
        Assert.False(from.CanWrite);
        var job = new ConversionJob(input, from, F.Mp4, Path.Combine(Fixtures.NewDir(), "phone.mp4"), new ConversionSettings());
        await Converter.ConvertAsync(job, null, CancellationToken.None);
        var output = await MediaAssert.ValidAsync(job.OutputPath, 1.0);
        Assert.Equal("h264", output.VideoCodec);
        Assert.True(output.HasAudio);
    }

    [Fact]
    public void TypeScript_source_code_is_not_a_video()
    {
        var dir = Fixtures.NewDir();
        var code = Path.Combine(dir, "app.ts");
        File.WriteAllText(code, "export const answer: number = 42;\n");
        Assert.Null(F.FromPath(code));
        var stream = Path.Combine(dir, "recording.ts");
        var packets = new byte[188 * 3];
        packets[0] = packets[188] = packets[376] = 0x47;
        File.WriteAllBytes(stream, packets);
        Assert.Equal(F.Ts, F.FromPath(stream));
        // A name for a result that isn't written yet is a transport stream; .m2ts and .mts always are.
        Assert.Equal(F.Ts, F.FromPath(Path.Combine(dir, "not yet.ts")));
        Assert.Equal(F.Ts, F.FromPath(Path.Combine(dir, "camcorder.MTS")));
    }
}
