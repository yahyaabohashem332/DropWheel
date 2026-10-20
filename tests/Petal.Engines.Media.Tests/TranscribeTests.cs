using System.Diagnostics;
using Petal.Core.Models;
using Xunit.Abstractions;

namespace Petal.Engines.Media.Tests;

/// <summary>
/// Speech to text against the real Whisper model (downloaded once into %LOCALAPPDATA%\Petal\models)
/// and subtitles written into videos.
/// </summary>
public class TranscribeTests(ITestOutputHelper output)
{
    /// <summary>A WAV of English speech from Windows' built-in voice.</summary>
    private static string Speech(string text)
    {
        var path = Path.Combine(Fixtures.NewDir(), "speech.wav");
        var script = "Add-Type -AssemblyName System.Speech; $s = New-Object System.Speech.Synthesis.SpeechSynthesizer; " +
                     $"$s.Rate = -1; $s.SetOutputToWaveFile('{path.Replace("'", "''")}'); $s.Speak('{text.Replace("'", "''")}'); $s.Dispose()";
        using var process = Process.Start(new ProcessStartInfo("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", script]) { CreateNoWindow = true })!;
        process.WaitForExit(60_000);
        Assert.True(File.Exists(path), "Windows speech synthesis didn't produce a file");
        return path;
    }

    [Fact]
    public async Task Transcribes_speech_with_timings()
    {
        var model = ModelStore.Find(ModelCatalog.WhisperFast);
        if (model is null)
        {
            output.WriteLine($"Skipped: {ModelCatalog.WhisperFast.FileName} isn't in {ModelStore.Folder}.");
            return;
        }
        var wav = Speech("Hello from Petal. This sentence checks that transcription works on this computer.");
        var watch = Stopwatch.StartNew();
        var transcript = await Transcriber.TranscribeAsync(wav, new TranscribeOptions(model, "en"));
        output.WriteLine($"{watch.ElapsedMilliseconds} ms: {transcript.ToText()}");

        var text = transcript.ToText().ToLowerInvariant();
        Assert.Contains("hello", text);
        Assert.Contains("transcription", text);
        Assert.Equal("en", transcript.Language);
        Assert.All(transcript.Segments, s => Assert.True(s.End >= s.Start));
        var srt = transcript.ToSrt();
        Assert.StartsWith("1\n00:00:0", srt);
        Assert.Contains(" --> ", srt);
    }

    [Fact]
    public async Task Detects_the_language_when_asked_to()
    {
        var model = ModelStore.Find(ModelCatalog.WhisperFast);
        if (model is null)
            return;
        var wav = Speech("Good morning. The weather is lovely today and the meeting starts at ten.");
        var transcript = await Transcriber.TranscribeAsync(wav, new TranscribeOptions(model));
        Assert.Equal("en", transcript.Language);
    }

    [Fact]
    public async Task Silence_is_reported_clearly()
    {
        var model = ModelStore.Find(ModelCatalog.WhisperFast) ?? Path.Combine(Fixtures.NewDir(), "missing.bin");
        await Assert.ThrowsAnyAsync<Exception>(() => Transcriber.TranscribeAsync(Path.Combine(Fixtures.NewDir(), "nothing.wav"), new TranscribeOptions(model)));
    }

    [Fact]
    public void Formats_subtitles_and_text()
    {
        var t = new Transcript(
        [
            new(TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(2.25), "مرحبًا بكم."),
            new(TimeSpan.FromSeconds(2.5), TimeSpan.FromSeconds(4), "Second line."),
            new(TimeSpan.FromSeconds(7), TimeSpan.FromSeconds(3725.5), "After a pause."),
        ], "ar");
        Assert.Equal("1\n00:00:00,500 --> 00:00:02,250\nمرحبًا بكم.\n\n2\n00:00:02,500 --> 00:00:04,000\nSecond line.\n\n3\n00:00:07,000 --> 01:02:05,500\nAfter a pause.\n\n", t.ToSrt());
        Assert.StartsWith("WEBVTT\n\n00:00:00.500 --> 00:00:02.250\n", t.ToVtt());
        Assert.Equal("مرحبًا بكم. Second line.\n\nAfter a pause.\n", t.ToText());
    }

    [Fact]
    public async Task Adds_a_subtitle_track_without_re_encoding()
    {
        var video = await Fixtures.GetAsync("hq.mp4");
        var srt = Path.Combine(Fixtures.NewDir(), "subs.srt");
        await File.WriteAllTextAsync(srt, "1\n00:00:00,500 --> 00:00:04,000\nHello there.\n\n");
        var subbed = Path.Combine(Fixtures.NewDir(), "subbed.mp4");
        await Transcriber.AddSubtitlesAsync(video, srt, subbed, burnIn: false, language: "en");

        var info = await MediaProbe.ProbeAsync(subbed);
        Assert.Single(info.SubtitleStreams);
        Assert.True(info.HasAudio);
        var original = await MediaProbe.ProbeAsync(video);
        Assert.Equal(original.Video!.Codec, info.Video!.Codec);
    }

    [Fact]
    public async Task Burns_arabic_subtitles_into_the_picture()
    {
        var video = await Fixtures.GetAsync("hq.mp4");
        var srt = Path.Combine(Fixtures.NewDir(), "عربي.srt");
        await File.WriteAllTextAsync(srt, "1\n00:00:00,500 --> 00:00:04,500\nمرحبًا من Petal\n\n");
        var burned = Path.Combine(Fixtures.NewDir(), "burned.mp4");
        await Transcriber.AddSubtitlesAsync(video, srt, burned, burnIn: true, language: "ar");

        var before = await Fixtures.RgbFrameAsync(video, 2.0);
        var after = await Fixtures.RgbFrameAsync(burned, 2.0);
        double bottom = 0, top = 0;
        int nb = 0, nt = 0;
        for (int y = 0; y < before.Height; y++)
            for (int x = 0; x < before.Width; x++)
            {
                int i = (y * before.Width + x) * 3;
                double d = Math.Abs(before.Rgb[i] - after.Rgb[i]) + Math.Abs(before.Rgb[i + 1] - after.Rgb[i + 1]) + Math.Abs(before.Rgb[i + 2] - after.Rgb[i + 2]);
                if (y > before.Height * 0.72) { bottom += d; nb++; }
                else if (y < before.Height * 0.5) { top += d; nt++; }
            }
        output.WriteLine($"mean change: bottom {bottom / nb:0.0}, top {top / nt:0.0}");
        Assert.True(bottom / nb > 4 * (top / nt) && bottom / nb > 3, "text appears at the bottom only");
        // Kept for a visual check of the Arabic shaping.
        File.Copy(burned, Path.Combine(Path.GetTempPath(), "petal-burned-arabic.mp4"), overwrite: true);
    }
}
