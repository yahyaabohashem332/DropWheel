using System.Diagnostics;
using System.Text;
using Petal.Core.Audio;
using Petal.Engines.Media;

namespace Petal.Tests;

/// <summary>
/// Measures live captions on real speech fed in real time, the near-live engine against the pieces of 1.8.2: a
/// benchmark, run on purpose. PETAL_BENCH is a folder with &lt;name&gt;.wav (16 kHz) and &lt;name&gt;.txt (what is
/// said); PETAL_BENCH_MODEL a Whisper model; optional PETAL_BENCH_AUDIO (name), PETAL_BENCH_LANG, PETAL_BENCH_CPU=1
/// (the processor only), PETAL_BENCH_DTW=0 (no aligned times), PETAL_BENCH_ONLY_NEW=1. Results go to bench.txt.
/// </summary>
public sealed class CaptionBench
{
    private static readonly string Folder = Environment.GetEnvironmentVariable("PETAL_BENCH") ?? "";

    private static float[] ReadWav(string path)
    {
        var bytes = File.ReadAllBytes(path);
        int data = 12;
        while (data + 8 < bytes.Length && Encoding.ASCII.GetString(bytes, data, 4) != "data")
            data += 8 + BitConverter.ToInt32(bytes, data + 4);
        int start = data + 8, count = (bytes.Length - start) / 2;
        var samples = new float[count];
        for (int i = 0; i < count; i++)
            samples[i] = BitConverter.ToInt16(bytes, start + i * 2) / 32768f;
        return samples;
    }

    private static double Wer(string reference, string hypothesis)
    {
        var r = reference.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(StreamWord.Normalize).Where(w => w.Length > 0).ToArray();
        var h = hypothesis.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(StreamWord.Normalize).Where(w => w.Length > 0).ToArray();
        var d = new int[r.Length + 1, h.Length + 1];
        for (int i = 0; i <= r.Length; i++) d[i, 0] = i;
        for (int j = 0; j <= h.Length; j++) d[0, j] = j;
        for (int i = 1; i <= r.Length; i++)
            for (int j = 1; j <= h.Length; j++)
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (r[i - 1] == h[j - 1] ? 0 : 1));
        return (double)d[r.Length, h.Length] / Math.Max(1, r.Length);
    }

    [Fact]
    public async Task Streaming_versus_pieces()
    {
        if (Folder.Length == 0)
            return;
        var model = Environment.GetEnvironmentVariable("PETAL_BENCH_MODEL")!;
        var name = Environment.GetEnvironmentVariable("PETAL_BENCH_AUDIO") ?? "lecture_en";
        var audio = ReadWav(Path.Combine(Folder, name + ".wav"));
        var reference = File.ReadAllText(Path.Combine(Folder, name + ".txt"));
        // A PC without a graphics card Whisper can use: the processor only.
        if (Environment.GetEnvironmentVariable("PETAL_BENCH_CPU") == "1")
            Whisper.net.LibraryLoader.RuntimeOptions.RuntimeLibraryOrder = [Whisper.net.LibraryLoader.RuntimeLibrary.Cpu];
        var report = new StringBuilder();
        report.AppendLine($"# {Path.GetFileName(model)} on {name} ({audio.Length / 16000.0:0.0} s)");

        // ---- the new engine ----
        var stage = Path.Combine(Folder, "stage.txt");
        File.AppendAllText(stage, $"start {name} {Path.GetFileName(model)}{Environment.NewLine}");
        using (var engine = new StreamingTranscriber(model, Environment.GetEnvironmentVariable("PETAL_BENCH_LANG") ?? "auto", Environment.GetEnvironmentVariable("PETAL_BENCH_DTW") != "0"))
        {
            var clock = Stopwatch.StartNew();
            var firstSeen = new Dictionary<string, double>();
            var finals = new List<(string Text, double End, double At)>();
            var sentences = new List<StreamSentence>();
            double speedSum = 0; int passes = 0;
            engine.Changed += s =>
            {
                speedSum += engine.Speed; passes++;
            };
            engine.Sentence += s => { lock (sentences) sentences.Add(s); };
            var lags = new List<double>();
            var finalLags = new List<double>();
            engine.Changed += s =>
            {
                if (s.Newest > 0)
                    lock (lags)
                        lags.Add(clock.Elapsed.TotalSeconds - s.Newest);
            };
            engine.Sentence += s => { lock (finalLags) finalLags.Add(clock.Elapsed.TotalSeconds - s.End); };
            using var stop = new CancellationTokenSource();
            var run = engine.RunAsync(stop.Token);
            int chunk = 1600;
            for (int i = 0; i < audio.Length; i += chunk)
            {
                engine.Add(audio.AsSpan(i, Math.Min(chunk, audio.Length - i)));
                double due = (double)(i + chunk) / 16000;
                double wait = due - clock.Elapsed.TotalSeconds;
                if (wait > 0)
                    await Task.Delay(TimeSpan.FromSeconds(wait));
            }
            // A little quiet at the end, as after a lesson.
            await Task.Delay(2500);
            stop.Cancel();
            try { await run; } catch (OperationCanceledException) { }
            var text = string.Join(' ', sentences.Select(s => s.Text));
            // Latency of each sentence: when it became final after its last word was said.
            report.AppendLine($"runtime {engine.Runtime}, average speed {(passes > 0 ? speedSum / passes : 0):0.00}, language {engine.Language}");
            report.AppendLine($"WER {Wer(reference, text):P1}");
            report.AppendLine($"sentences {sentences.Count}; newest word on screen behind the talk: average {lags.DefaultIfEmpty().Average():0.00} s, worst {lags.DefaultIfEmpty().Max():0.00} s; a sentence final after its end: average {finalLags.DefaultIfEmpty().Average():0.00} s");
            report.AppendLine("final text: " + text);
        }

        File.AppendAllText(stage, "new engine done" + Environment.NewLine);
        if (Environment.GetEnvironmentVariable("PETAL_BENCH_ONLY_NEW") == "1")
        {
            File.AppendAllText(Path.Combine(Folder, "bench.txt"), report + Environment.NewLine);
            return;
        }
        // ---- the pieces (as 1.8.2) ----
        using (var old = new LiveTranscriber(model, Environment.GetEnvironmentVariable("PETAL_BENCH_LANG") ?? "auto"))
        {
            var chunker = new SpeechChunker();
            var clock = Stopwatch.StartNew();
            var queue = System.Threading.Channels.Channel.CreateBounded<(float[] Piece, double End)>(new System.Threading.Channels.BoundedChannelOptions(3) { FullMode = System.Threading.Channels.BoundedChannelFullMode.DropOldest });
            var shown = new List<(string Text, double Latency, double Length)>();
            int made = 0;
            var worker = Task.Run(async () =>
            {
                await foreach (var (piece, end) in queue.Reader.ReadAllAsync())
                {
                    var heard = await old.TranscribeAsync(piece);
                    if (heard.Text.Length > 0)
                        shown.Add((heard.Text, clock.Elapsed.TotalSeconds - end, piece.Length / 16000.0));
                }
            });
            int chunk = 1600;
            for (int i = 0; i < audio.Length; i += chunk)
            {
                foreach (var piece in chunker.Add(audio.AsSpan(i, Math.Min(chunk, audio.Length - i))))
                {
                    made++;
                    queue.Writer.TryWrite((piece, (double)(i + chunk) / 16000));
                }
                double wait = (double)(i + chunk) / 16000 - clock.Elapsed.TotalSeconds;
                if (wait > 0)
                    await Task.Delay(TimeSpan.FromSeconds(wait));
            }
            if (chunker.Flush() is { } rest)
            {
                made++;
                queue.Writer.TryWrite((rest, (double)audio.Length / 16000));
            }
            queue.Writer.Complete();
            await worker;
            var text = string.Join(' ', shown.Select(s => s.Text));
            report.AppendLine($"## pieces: {made} made, {shown.Count} shown");
            report.AppendLine($"WER {Wer(reference, text):P1}");
            report.AppendLine($"latency after the piece ended: average {shown.Average(s => s.Latency):0.00} s, worst {shown.Max(s => s.Latency):0.00} s");
            // A word waits for the rest of its piece: on average half the piece, at worst all of it.
            report.AppendLine($"a word on screen after it was said: average {shown.Average(s => s.Latency + s.Length / 2):0.00} s, worst {shown.Max(s => s.Latency + s.Length):0.00} s");
            report.AppendLine("text: " + text);
        }
        File.AppendAllText(stage, "old pieces done" + Environment.NewLine);
        File.AppendAllText(Path.Combine(Folder, "bench.txt"), report + Environment.NewLine);
    }
}
