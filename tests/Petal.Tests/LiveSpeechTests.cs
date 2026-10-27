using System.Diagnostics;
using System.Text.RegularExpressions;
using Petal.Core.Audio;
using Petal.Core.Models;
using Petal.Engines.Media;

namespace Petal.Tests;

/// <summary>Live captions and meeting notes: cutting speech at pauses, resampling, and Whisper on a stream.</summary>
[Collection("Timing")]
public sealed class LiveSpeechTests
{
    private static float[] Tone(double seconds, double amplitude, int rate = SpeechChunker.Rate) =>
        Enumerable.Range(0, (int)(seconds * rate)).Select(i => (float)(amplitude * Math.Sin(2 * Math.PI * 220 * i / rate))).ToArray();

    [Fact]
    public void Speech_is_cut_at_pauses_and_quiet_is_dropped()
    {
        var chunker = new SpeechChunker();
        var pieces = new List<float[]>();
        pieces.AddRange(chunker.Add(new float[SpeechChunker.Rate]));
        Assert.Empty(pieces);
        pieces.AddRange(chunker.Add(Tone(2, 0.3)));
        pieces.AddRange(chunker.Add(new float[SpeechChunker.Rate]));
        var piece = Assert.Single(pieces);
        // The speech, the pause that ended it, and a little from before.
        Assert.InRange(piece.Length / (double)SpeechChunker.Rate, 2.4, 2.9);
        // A click on its own is not speech.
        pieces.AddRange(chunker.Add(Tone(0.1, 0.4)));
        pieces.AddRange(chunker.Add(new float[SpeechChunker.Rate * 2]));
        Assert.Single(pieces);
    }

    [Fact]
    public void Talking_without_a_break_is_cut_into_pieces_that_keep_up()
    {
        var chunker = new SpeechChunker(longest: 4);
        var pieces = chunker.Add(Tone(10, 0.3));
        Assert.Equal(2, pieces.Count);
        Assert.All(pieces, p => Assert.InRange(p.Length / (double)SpeechChunker.Rate, 3.9, 4.5));
        Assert.NotNull(chunker.Flush());
    }

    [Fact]
    public void Sound_of_any_rate_becomes_16_kHz_mono()
    {
        var stereo48 = new float[48000 * 2];
        for (int i = 0; i < 48000; i++)
            stereo48[i * 2] = stereo48[i * 2 + 1] = (float)Math.Sin(2 * Math.PI * 100 * i / 48000.0);
        var resampler = new SpeechResampler(48000, 2);
        // In pieces, as a sound card delivers it.
        var output = new List<float>();
        for (int at = 0; at < stereo48.Length; at += 960 * 2)
            output.AddRange(resampler.Convert(stereo48.AsSpan(at, Math.Min(960 * 2, stereo48.Length - at))));
        Assert.InRange(output.Count, 15990, 16001);
        // Still the same 100 Hz wave, without jumps where the pieces meet.
        for (int i = 1; i < output.Count; i++)
            Assert.True(Math.Abs(output[i] - output[i - 1]) < 0.05, $"jump at {i}");
        Assert.Equal(Math.Sin(2 * Math.PI * 100 * 4000 / 16000.0), output[4000], 2);
    }

    [Fact]
    public async Task Speech_is_written_down_live_when_the_model_is_here()
    {
        if (ModelStore.Find(ModelCatalog.WhisperFast) is not { } model)
            return;
        var wav = Path.Combine(Path.GetTempPath(), $"petal-speech-{Guid.NewGuid():N}.wav");
        try
        {
            // Windows' own voice says a sentence.
            var say = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in new[] { "-NoProfile", "-Command", $"Add-Type -AssemblyName System.Speech; $s = New-Object System.Speech.Synthesis.SpeechSynthesizer; $s.SetOutputToWaveFile('{wav}'); $s.Speak('Good morning everyone, the meeting starts at ten o clock.'); $s.Dispose()" })
                say.ArgumentList.Add(a);
            using (var p = Process.Start(say)!)
                await p.WaitForExitAsync();
            if (!File.Exists(wav))
                return;
            using var reader = new NAudio.Wave.WaveFileReader(wav);
            var bytes = new byte[reader.Length];
            int read = reader.Read(bytes, 0, bytes.Length);
            var floats = new float[read / 2];
            for (int i = 0; i < floats.Length; i++)
                floats[i] = BitConverter.ToInt16(bytes, i * 2) / 32768f;
            var samples = new SpeechResampler(reader.WaveFormat.SampleRate, reader.WaveFormat.Channels).Convert(floats);
            using var live = new LiveTranscriber(model, "en");
            var text = await live.TranscribeAsync(samples);
            Assert.Contains("meeting", text.Text, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("en", text.Language);
        }
        finally
        {
            File.Delete(wav);
        }
    }

    /// <summary>Windows' own voice saying some sentences, as 16 kHz samples (null when it can't).</summary>
    private static async Task<float[]?> SayAsync(string text)
    {
        var wav = Path.Combine(Path.GetTempPath(), $"petal-speech-{Guid.NewGuid():N}.wav");
        try
        {
            var say = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in new[] { "-NoProfile", "-Command", $"Add-Type -AssemblyName System.Speech; $s = New-Object System.Speech.Synthesis.SpeechSynthesizer; $s.SetOutputToWaveFile('{wav}'); $s.Speak('{text}'); $s.Dispose()" })
                say.ArgumentList.Add(a);
            using (var p = Process.Start(say)!)
                await p.WaitForExitAsync();
            if (!File.Exists(wav))
                return null;
            using var reader = new NAudio.Wave.WaveFileReader(wav);
            var bytes = new byte[reader.Length];
            int read = reader.Read(bytes, 0, bytes.Length);
            var floats = new float[read / 2];
            for (int i = 0; i < floats.Length; i++)
                floats[i] = BitConverter.ToInt16(bytes, i * 2) / 32768f;
            return new SpeechResampler(reader.WaveFormat.SampleRate, reader.WaveFormat.Channels).Convert(floats);
        }
        finally
        {
            File.Delete(wav);
        }
    }

    [Fact]
    public async Task Words_come_while_the_sentence_is_still_being_said()
    {
        if (ModelStore.Find(ModelCatalog.WhisperFast) is not { } model)
            return;
        var samples = await SayAsync("The first clocks used water that dripped slowly from one bowl into another, and the level showed how much time had passed. Later, mechanical clocks appeared in the towers of European cities.");
        if (samples is null)
            return;
        using var engine = new StreamingTranscriber(model, "en");
        // As the captions do before they listen: the graphics card prepares its work on the first pass.
        await engine.WarmUpAsync(CancellationToken.None);
        var clock = Stopwatch.StartNew();
        double firstWords = -1;
        var sentences = new List<StreamSentence>();
        engine.Changed += s =>
        {
            if (firstWords < 0 && (s.Current + s.Draft).Length > 0)
                firstWords = clock.Elapsed.TotalSeconds;
        };
        engine.Sentence += s => { lock (sentences) sentences.Add(s); };
        using var stop = new CancellationTokenSource();
        var run = engine.RunAsync(stop.Token);
        // Fed as it would be said, a tenth of a second at a time.
        for (int at = 0; at < samples.Length; at += 1600)
        {
            engine.Add(samples.AsSpan(at, Math.Min(1600, samples.Length - at)));
            double wait = (at + 1600) / 16000.0 - clock.Elapsed.TotalSeconds;
            if (wait > 0)
                await Task.Delay(TimeSpan.FromSeconds(wait));
        }
        double spoken = samples.Length / 16000.0;
        engine.Add(new float[16000 * 2]);
        await Task.Delay(3000);
        stop.Cancel();
        await run;
        var text = string.Join(' ', sentences.Select(s => s.Text));
        // The first words are up long before the first sentence is over (the old pieces waited for a pause).
        Assert.InRange(firstWords, 0.1, spoken / 2);
        Assert.Contains("water", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("towers", text, StringComparison.OrdinalIgnoreCase);
        Assert.True(sentences.Count >= 2, text);
        // Nothing written twice where the passes meet.
        var words = text.Split(' ').Select(StreamWord.Normalize).ToArray();
        for (int i = 0; i + 3 < words.Length; i++)
            Assert.False(words[i] == words[i + 2] && words[i + 1] == words[i + 3], $"repeated at {i}: {text}");
        Assert.Equal(1, Regex.Count(text, "dripped", RegexOptions.IgnoreCase));
    }

    [Theory]
    [InlineData("[BLANK_AUDIO]", true)]
    [InlineData("(music)", true)]
    [InlineData("♪ ♪", true)]
    [InlineData("Hello there.", false)]
    public void Noise_is_not_words(string text, bool noise) => Assert.Equal(noise, LiveTranscriber.IsNoise(text));
}
