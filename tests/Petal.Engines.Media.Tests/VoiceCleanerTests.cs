using Petal.Core.Models;
using Xunit.Abstractions;

namespace Petal.Engines.Media.Tests;

[Collection("Models")]
public sealed class VoiceCleanerTests(ITestOutputHelper output)
{
    private const int Rate = 16000;

    /// <summary>
    /// With PETAL_DEEPFILTER pointing at a folder holding the DeepFilterNet program, hiss under a
    /// voice goes away, the voice stays in step, and a video keeps its picture.
    /// </summary>
    [Fact]
    public async Task Noise_under_a_voice_goes_away()
    {
        var folder = Environment.GetEnvironmentVariable("PETAL_DEEPFILTER");
        if (folder is null)
        {
            output.WriteLine("Skipped: PETAL_DEEPFILTER isn't set.");
            return;
        }
        var saved = ModelStore.Folder;
        ModelStore.Folder = folder;
        try
        {
            var voice = Fixtures.Speech("Petal takes the noise out of speech. The hiss goes, and the voice stays clear.");
            var dir = Fixtures.NewDir();
            // The voice, then two and a half seconds of hiss on its own; once as a recording, once in a video.
            // The same hiss every run: how far it drops varies by several dB from one random hiss to the next.
            var noisy = Path.Combine(dir, "noisy.wav");
            var video = Path.Combine(dir, "noisy.mp4");
            await RunFfmpegAsync(["-i", voice, "-f", "lavfi", "-i", "anoisesrc=color=pink:a=0.3:d=60:seed=1",
                "-filter_complex", "[0:a]aresample=48000,apad=pad_dur=2.5[v];[1:a]aresample=48000[n];[v][n]amix=inputs=2:duration=first:normalize=0",
                "-c:a", "pcm_s16le", noisy]);
            var length = (await MediaProbe.ProbeAsync(noisy)).DurationSeconds;
            await RunFfmpegAsync(["-f", "lavfi", "-i", $"testsrc2=s=320x240:r=15:d={length.ToString(System.Globalization.CultureInfo.InvariantCulture)}", "-i", noisy,
                "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "192k", "-shortest", video]);

            var clean = await VoiceCleaner.CleanAsync(noisy, Path.Combine(dir, "clean.wav"));
            var truth = (await MediaPreview.DecodePcmAsync(voice, new PcmDecodeOptions(Rate, 1))).Samples;
            var before = (await MediaPreview.DecodePcmAsync(noisy, new PcmDecodeOptions(Rate, 1))).Samples;
            var after = (await MediaPreview.DecodePcmAsync(clean, new PcmDecodeOptions(Rate, 1))).Samples;

            double hissBefore = TailDb(before), hissAfter = TailDb(after);
            var (lagBefore, matchBefore) = BestMatch(before, truth);
            var (lagAfter, matchAfter) = BestMatch(after, truth);
            output.WriteLine($"hiss {hissBefore:0.0} dB → {hissAfter:0.0} dB; voice match {matchBefore:0.000} → {matchAfter:0.000} (lag {lagAfter} samples)");
            Assert.True(hissBefore - hissAfter > 20, $"the hiss only dropped {hissBefore - hissAfter:0.0} dB");
            Assert.True(matchAfter > matchBefore, $"the voice matched {matchBefore:0.000} before and {matchAfter:0.000} after");
            Assert.True(matchAfter > 0.8, $"the cleaned voice matches {matchAfter:0.000}");
            // In step with the original: no more than a millisecond off.
            Assert.InRange(lagAfter, -Rate / 1000, Rate / 1000);
            Assert.InRange(lagBefore, -1, 1);
            Assert.InRange(after.Length, before.Length - Rate / 10, before.Length + Rate / 10);

            var cleanVideo = await VoiceCleaner.CleanAsync(video, Path.Combine(dir, "clean.mp4"));
            var source = await MediaProbe.ProbeAsync(video);
            var result = await MediaAssert.ValidAsync(cleanVideo, source.DurationSeconds);
            Assert.True(result.HasVideo, "the video lost its picture");
            Assert.Equal(source.VideoCodec, result.VideoCodec);
            Assert.Equal((source.Width, source.Height), (result.Width, result.Height));
            var videoSound = (await MediaPreview.DecodePcmAsync(cleanVideo, new PcmDecodeOptions(Rate, 1))).Samples;
            output.WriteLine($"video hiss → {TailDb(videoSound):0.0} dB");
            Assert.True(hissBefore - TailDb(videoSound) > 20, "the video's hiss stayed");
        }
        finally
        {
            ModelStore.Folder = saved;
        }
    }

    /// <summary>The level of the last 1.5 seconds (hiss only), in dB.</summary>
    private static double TailDb(float[] samples)
    {
        int from = samples.Length - Rate * 3 / 2;
        double sum = 0;
        for (int i = from; i < samples.Length - Rate / 10; i++)
            sum += (double)samples[i] * samples[i];
        return 10 * Math.Log10(sum / (samples.Length - Rate / 10 - from) + 1e-12);
    }

    /// <summary>The shift (within 25 ms) at which <paramref name="sound"/> best matches the clean voice, and how well.</summary>
    private static (int Lag, double Correlation) BestMatch(float[] sound, float[] voice)
    {
        int n = Math.Min(sound.Length, voice.Length) - 800;
        (int, double) best = (0, double.MinValue);
        for (int lag = -400; lag <= 400; lag++)
        {
            double ab = 0, aa = 0, bb = 0;
            for (int i = 400; i < n; i++)
            {
                double a = sound[i + lag], b = voice[i];
                ab += a * b;
                aa += a * a;
                bb += b * b;
            }
            double c = ab / Math.Sqrt(aa * bb + 1e-12);
            if (c > best.Item2)
                best = (lag, c);
        }
        return best;
    }

    private static async Task RunFfmpegAsync(string[] args)
    {
        var result = await Petal.Core.Util.ProcessRunner.RunAsync(Fixtures.Ffmpeg, ["-hide_banner", "-nostdin", "-loglevel", "error", "-y", .. args]);
        Assert.True(result.Succeeded, result.StandardError);
    }
}
