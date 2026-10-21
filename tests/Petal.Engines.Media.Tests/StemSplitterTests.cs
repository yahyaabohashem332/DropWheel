using System.Diagnostics;
using Petal.Core.Models;
using Xunit.Abstractions;

namespace Petal.Engines.Media.Tests;

[Collection("Models")]
public sealed class StemSplitterTests(ITestOutputHelper output)
{
    [Fact]
    public void The_spectrogram_turns_back_into_the_same_sound()
    {
        var rng = new Random(7);
        var x = new float[343980];
        double[] freqs = [110, 233.1, 440, 1234.5, 3520, 9876];
        for (int i = 0; i < x.Length; i++)
        {
            double t = i / 44100.0;
            x[i] = (float)(freqs.Sum(f => Math.Sin(2 * Math.PI * f * t) * 0.1) + (rng.NextDouble() - 0.5) * 0.01);
        }
        var back = StemSplitter.InverseSpec(StemSplitter.Spec(x));
        double Snr(int from, int to)
        {
            double signal = 0, error = 0;
            for (int i = from; i < to; i++)
            {
                signal += (double)x[i] * x[i];
                error += (double)(x[i] - back[i]) * (x[i] - back[i]);
            }
            return 10 * Math.Log10(signal / error);
        }
        // Demucs drops the first and last two frames, so the very ends are rougher; the overlap
        // between pieces covers them. Everywhere else the sound must come back intact.
        double whole = Snr(0, x.Length), middle = Snr(4096, x.Length - 4096);
        output.WriteLine($"round trip: {whole:0.0} dB overall, {middle:0.0} dB away from the ends");
        Assert.True(whole > 30, $"{whole:0.0} dB");
        Assert.True(middle > 60, $"{middle:0.0} dB");
    }

    /// <summary>With PETAL_DEMUCS pointing at a folder holding htdemucs_embedded.onnx, a voice over music comes back out.</summary>
    [Fact]
    public async Task A_voice_over_music_is_separated()
    {
        var folder = Environment.GetEnvironmentVariable("PETAL_DEMUCS");
        if (folder is null)
        {
            output.WriteLine("Skipped: PETAL_DEMUCS isn't set.");
            return;
        }
        var saved = ModelStore.Folder;
        ModelStore.Folder = folder;
        try
        {
            var voice = Fixtures.Speech("Petal separates the singer from the band. The voice comes out on its own, and the music stays together.");
            var dir = Fixtures.NewDir();
            var song = Path.Combine(dir, "song.mp3");
            // Chords and drums under the voice, about 11 seconds: two overlapping pieces.
            using (var p = Process.Start(new ProcessStartInfo(Fixtures.Ffmpeg, ["-hide_banner", "-loglevel", "error", "-y", "-i", voice,
                "-f", "lavfi", "-i", "sine=f=220:d=11,volume=0.12", "-f", "lavfi", "-i", "sine=f=329.6:d=11,volume=0.1",
                "-f", "lavfi", "-i", "anoisesrc=color=white:a=0.3:d=11,volume='if(lt(mod(t,0.5),0.06),1,0)':eval=frame",
                "-filter_complex", "[0:a]aresample=44100,apad=whole_dur=11[v];[1:a][2:a][3:a]amix=inputs=3:normalize=0[m];[v][m]amix=inputs=2:normalize=0,aformat=channel_layouts=stereo",
                "-c:a", "libmp3lame", "-q:a", "2", song]) { CreateNoWindow = true })!)
                p.WaitForExit();

            var clock = Stopwatch.StartNew();
            var stems = await StemSplitter.SplitAsync(song, Path.Combine(dir, "stems"), "wav", new SplitStemsOptions());
            output.WriteLine($"separated in {clock.Elapsed.TotalSeconds:0.0} s: {string.Join(", ", stems.Select(Path.GetFileName))}");
            Assert.Equal(["Vocals.wav", "Music.wav"], stems.Select(Path.GetFileName));

            var truth = await MediaPreview.DecodePcmAsync(voice, new PcmDecodeOptions(44100, 1));
            var vocals = await MediaPreview.DecodePcmAsync(stems[0], new PcmDecodeOptions(44100, 1));
            var music = await MediaPreview.DecodePcmAsync(stems[1], new PcmDecodeOptions(44100, 1));
            double Correlation(float[] a, float[] b)
            {
                int n = Math.Min(a.Length, b.Length);
                double ab = 0, aa = 0, bb = 0;
                for (int i = 0; i < n; i++)
                {
                    ab += a[i] * b[i];
                    aa += a[i] * a[i];
                    bb += b[i] * b[i];
                }
                return ab / Math.Sqrt(aa * bb + 1e-12);
            }
            double withVoice = Correlation(vocals.Samples, truth.Samples), musicWithVoice = Correlation(music.Samples, truth.Samples);
            output.WriteLine($"vocals ~ voice {withVoice:0.000}, music ~ voice {musicWithVoice:0.000}");
            Assert.True(withVoice > 0.8, $"vocals correlate {withVoice:0.000} with the voice");
            Assert.True(musicWithVoice < 0.3, $"music correlates {musicWithVoice:0.000} with the voice");
        }
        finally
        {
            ModelStore.Folder = saved;
        }
    }
}
