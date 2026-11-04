using System.Diagnostics;
using System.Globalization;
using System.Text;
using Petal.Core.Models;
using Petal.Recipes;
using Petal.Recipes.Translation;
using Whisper.net;

namespace Petal.Tests;

/// <summary>
/// English from Arabic speech, measured on FLEURS (read sentences with a human English translation each): Whisper's
/// own translation with each model, and Whisper writing the Arabic, then a text translation of it (Opus-MT, or the
/// AI model). A benchmark, run on purpose: PETAL_TBENCH is a folder with ar_dev.tsv, en_dev.tsv and audio\dev\*.wav;
/// PETAL_TBENCH_WHISPER the folder of the ggml models; PETAL_TBENCH_OPUS the folder of the Arabic to English Opus-MT
/// files; optional PETAL_TBENCH_N (clips, 30), PETAL_TBENCH_QWEN=1 (the AI model too, slow). Results: tbench.txt.
/// </summary>
public sealed class TranslationBench
{
    private static readonly string Folder = Environment.GetEnvironmentVariable("PETAL_TBENCH") ?? "";

    private sealed record Clip(string Wav, string Arabic, string English);

    private static List<Clip> Clips(int count)
    {
        var english = new Dictionary<string, string>();
        foreach (var line in File.ReadLines(Path.Combine(Folder, "en_dev.tsv")))
        {
            var c = line.Split('\t');
            if (c.Length > 2)
                english.TryAdd(c[0], c[2]);
        }
        var clips = new List<Clip>();
        var seen = new HashSet<string>();
        foreach (var line in File.ReadLines(Path.Combine(Folder, "ar_dev.tsv")))
        {
            var c = line.Split('\t');
            if (c.Length < 3 || !english.TryGetValue(c[0], out var en) || !seen.Add(c[0]))
                continue;
            var wav = Path.Combine(Folder, "audio", "dev", c[1]);
            if (!File.Exists(wav) || ReadWav(wav) is not { Length: > 16000 })
                continue;
            clips.Add(new Clip(wav, c[2], en));
            if (clips.Count == count)
                break;
        }
        return clips;
    }

    /// <summary>
    /// The samples of a 16 kHz mono WAV, 16-bit or floating point (FLEURS's); null when the file was cut short
    /// (the archive was only partly fetched).
    /// </summary>
    private static float[]? ReadWav(string path)
    {
        var bytes = File.ReadAllBytes(path);
        int at = 12, format = 1, bits = 16;
        while (at + 8 < bytes.Length && Encoding.ASCII.GetString(bytes, at, 4) != "data")
        {
            if (Encoding.ASCII.GetString(bytes, at, 4) == "fmt ")
            {
                format = BitConverter.ToInt16(bytes, at + 8);
                bits = BitConverter.ToInt16(bytes, at + 22);
            }
            at += 8 + BitConverter.ToInt32(bytes, at + 4);
        }
        if (at + 8 >= bytes.Length)
            return null;
        int size = BitConverter.ToInt32(bytes, at + 4), start = at + 8;
        if (start + size > bytes.Length)
            return null;
        bool floats = format == 3 && bits == 32;
        var samples = new float[size / (floats ? 4 : 2)];
        for (int i = 0; i < samples.Length; i++)
            samples[i] = floats ? BitConverter.ToSingle(bytes, start + i * 4) : BitConverter.ToInt16(bytes, start + i * 2) / 32768f;
        return samples;
    }

    // ---- scores ----

    private static string[] Words(string text) =>
        new string(text.ToLowerInvariant().Select(ch => char.IsLetterOrDigit(ch) || ch == '\'' ? ch : ' ').ToArray())
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Corpus BLEU (four-word phrases, brevity penalty), lower case, punctuation left out.</summary>
    private static double Bleu(IReadOnlyList<string> hypotheses, IReadOnlyList<string> references)
    {
        var matched = new double[4];
        var total = new double[4];
        double hypothesisLength = 0, referenceLength = 0;
        for (int s = 0; s < hypotheses.Count; s++)
        {
            var h = Words(hypotheses[s]);
            var r = Words(references[s]);
            hypothesisLength += h.Length;
            referenceLength += r.Length;
            for (int n = 1; n <= 4; n++)
            {
                var counts = Grams(r, n);
                foreach (var (gram, count) in Grams(h, n))
                {
                    matched[n - 1] += Math.Min(count, counts.GetValueOrDefault(gram));
                    total[n - 1] += count;
                }
            }
        }
        if (matched.Any(m => m == 0))
            return 0;
        double log = Enumerable.Range(0, 4).Sum(i => Math.Log(matched[i] / total[i])) / 4;
        double brevity = hypothesisLength >= referenceLength ? 1 : Math.Exp(1 - referenceLength / Math.Max(1, hypothesisLength));
        return 100 * brevity * Math.Exp(log);
    }

    private static Dictionary<string, int> Grams(string[] words, int n)
    {
        var grams = new Dictionary<string, int>();
        for (int i = 0; i + n <= words.Length; i++)
        {
            var key = string.Join(' ', words, i, n);
            grams[key] = grams.GetValueOrDefault(key) + 1;
        }
        return grams;
    }

    /// <summary>Corpus chrF (letter sequences up to six, recall weighted twice), spaces left out.</summary>
    private static double ChrF(IReadOnlyList<string> hypotheses, IReadOnlyList<string> references)
    {
        double precision = 0, recall = 0;
        for (int n = 1; n <= 6; n++)
        {
            double matched = 0, inHypothesis = 0, inReference = 0;
            for (int s = 0; s < hypotheses.Count; s++)
            {
                var h = Letters(hypotheses[s], n);
                var r = Letters(references[s], n);
                foreach (var (gram, count) in h)
                    matched += Math.Min(count, r.GetValueOrDefault(gram));
                inHypothesis += h.Values.Sum();
                inReference += r.Values.Sum();
            }
            precision += inHypothesis > 0 ? matched / inHypothesis : 0;
            recall += inReference > 0 ? matched / inReference : 0;
        }
        precision /= 6;
        recall /= 6;
        return precision + recall == 0 ? 0 : 100 * 5 * precision * recall / (4 * precision + recall);
    }

    private static Dictionary<string, int> Letters(string text, int n)
    {
        var letters = new string(text.Where(ch => !char.IsWhiteSpace(ch)).ToArray());
        var grams = new Dictionary<string, int>();
        for (int i = 0; i + n <= letters.Length; i++)
        {
            var key = letters.Substring(i, n);
            grams[key] = grams.GetValueOrDefault(key) + 1;
        }
        return grams;
    }

    /// <summary>Letters wrong in the Arabic, without marks, punctuation or spaces.</summary>
    private static double Cer(IReadOnlyList<string> hypotheses, IReadOnlyList<string> references)
    {
        static string Plain(string text) => new(text.Where(ch => char.IsLetterOrDigit(ch) && CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark).ToArray());
        double errors = 0, length = 0;
        for (int s = 0; s < hypotheses.Count; s++)
        {
            var h = Plain(hypotheses[s]);
            var r = Plain(references[s]);
            var d = new int[r.Length + 1, h.Length + 1];
            for (int i = 0; i <= r.Length; i++) d[i, 0] = i;
            for (int j = 0; j <= h.Length; j++) d[0, j] = j;
            for (int i = 1; i <= r.Length; i++)
                for (int j = 1; j <= h.Length; j++)
                    d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (r[i - 1] == h[j - 1] ? 0 : 1));
            errors += d[r.Length, h.Length];
            length += r.Length;
        }
        return 100 * errors / Math.Max(1, length);
    }

    // ---- the ways ----

    private static async Task<List<string>> WhisperAsync(string model, IReadOnlyList<float[]> sounds, bool translate)
    {
        using var factory = WhisperFactory.FromPath(model);
        var texts = new List<string>();
        foreach (var sound in sounds)
        {
            var builder = factory.CreateBuilder().WithLanguage("ar");
            if (translate)
                builder = builder.WithTranslate();
            await using var processor = builder.Build();
            var parts = new List<string>();
            await foreach (var segment in processor.ProcessAsync(sound))
                parts.Add(segment.Text.Trim());
            texts.Add(string.Join(' ', parts));
        }
        return texts;
    }

    /// <summary>
    /// The tools' own route (<see cref="SpeechText"/>) on a video of Arabic speech (PETAL_TBENCH_VIDEO), with each
    /// accuracy and each language wanted, then English captions burned in with the Accurate model; the words go to
    /// e2e.txt and frames of the captioned video beside it.
    /// </summary>
    [Fact]
    public async Task Captions_of_Arabic_speech_in_each_language()
    {
        var video = Environment.GetEnvironmentVariable("PETAL_TBENCH_VIDEO");
        if (Folder.Length == 0 || video is null)
            return;
        var whisper = Environment.GetEnvironmentVariable("PETAL_TBENCH_WHISPER")!;
        var saved = ModelStore.Folder;
        ModelStore.Folder = Environment.GetEnvironmentVariable("PETAL_TBENCH_OPUS")!;
        try
        {
            var report = new StringBuilder();
            var small = Path.Combine(whisper, "ggml-small-q5_1.bin");
            Petal.Engines.Media.Transcript? english = null;
            foreach (var file in new[] { "ggml-base-q5_1.bin", "ggml-small-q5_1.bin", "ggml-large-v3-turbo-q5_0.bin" })
            {
                foreach (var into in new[] { SpeechInto.Spoken, SpeechInto.Arabic, SpeechInto.English })
                {
                    var clock = Stopwatch.StartNew();
                    var transcript = await SpeechText.TranscribeAsync(video, new SpeechTextOptions(Path.Combine(whisper, file), "auto", into));
                    report.AppendLine($"## {file} · {into} · {transcript.Language} · {clock.Elapsed.TotalSeconds:0.0} s");
                    foreach (var segment in transcript.Segments)
                        report.AppendLine($"{segment.Start:mm\\:ss\\.f}-{segment.End:mm\\:ss\\.f} {segment.Text}");
                    if (file.Contains("turbo") && into == SpeechInto.English)
                        english = transcript;
                }
            }
            File.WriteAllText(Path.Combine(Folder, "e2e.txt"), report.ToString());
            var output = Path.Combine(Folder, "captioned_en.mp4");
            await Petal.Engines.Media.AnimatedCaptions.BurnAsync(video, english!.Segments, output, new Petal.Engines.Media.AnimatedCaptionOptions());
            var info = await Petal.Engines.Media.MediaProbe.ProbeAsync(output);
            for (int i = 1; i <= 3; i++)
                File.WriteAllBytes(Path.Combine(Folder, $"captioned_en_{i}.jpg"), await Petal.Engines.Media.MediaPreview.ExtractFrameAsync(output, info.Duration * i / 4, 960));
        }
        finally
        {
            ModelStore.Folder = saved;
        }
    }

    [Fact]
    public async Task English_from_Arabic_speech()
    {
        if (Folder.Length == 0)
            return;
        var whisper = Environment.GetEnvironmentVariable("PETAL_TBENCH_WHISPER")!;
        var opus = Environment.GetEnvironmentVariable("PETAL_TBENCH_OPUS");
        int count = int.TryParse(Environment.GetEnvironmentVariable("PETAL_TBENCH_N"), out var n) ? n : 30;
        var clips = Clips(count);
        var sounds = clips.Select(c => ReadWav(c.Wav)!).ToList();
        var english = clips.Select(c => c.English).ToList();
        var arabic = clips.Select(c => c.Arabic).ToList();
        var report = new StringBuilder();
        var detail = new StringBuilder();
        report.AppendLine($"# {clips.Count} FLEURS clips, {sounds.Sum(s => s.Length) / 16000.0:0} s of Arabic speech");
        var results = new Dictionary<string, List<string>>();

        void Score(string name, List<string> texts, double seconds)
        {
            results[name] = texts;
            report.AppendLine($"{name,-34} BLEU {Bleu(texts, english),5:0.0}  chrF {ChrF(texts, english),5:0.0}  {seconds,6:0.0} s");
        }

        foreach (var file in new[] { "ggml-base-q5_1.bin", "ggml-small-q5_1.bin", "ggml-large-v3-turbo-q5_0.bin" })
        {
            var path = Path.Combine(whisper, file);
            if (!File.Exists(path))
                continue;
            var name = file.Split('-')[1] == "large" ? "turbo" : file.Split('-')[1];
            var clock = Stopwatch.StartNew();
            var written = await WhisperAsync(path, sounds, translate: false);
            report.AppendLine($"{name} writes the Arabic: {Cer(written, arabic):0.0}% letters wrong, {clock.Elapsed.TotalSeconds:0.0} s");
            results[name + " arabic"] = written;
            double writeSeconds = clock.Elapsed.TotalSeconds;
            clock.Restart();
            Score($"{name}: Whisper translates", await WhisperAsync(path, sounds, translate: true), clock.Elapsed.TotalSeconds);
            if (opus is not null && name != "base")
            {
                var saved = ModelStore.Folder;
                ModelStore.Folder = opus;
                try
                {
                    clock.Restart();
                    var translated = Translator.ToEnglish(written).ToList();
                    Score($"{name} Arabic + Opus-MT", translated, writeSeconds + clock.Elapsed.TotalSeconds);
                }
                finally
                {
                    ModelStore.Folder = saved;
                }
            }
            if (Environment.GetEnvironmentVariable("PETAL_TBENCH_QWEN") == "1" && name != "base")
            {
                clock.Restart();
                var translated = new List<string>();
                foreach (var text in written)
                    translated.Add(await AskPetal.WriteAsync(AskPetal.Instructions.Translate(intoEnglish: true), text, 200, CancellationToken.None, temperature: 0));
                Score($"{name} Arabic + AI model", translated, writeSeconds + clock.Elapsed.TotalSeconds);
            }
        }
        for (int i = 0; i < clips.Count; i++)
        {
            detail.AppendLine($"## {Path.GetFileName(clips[i].Wav)}");
            detail.AppendLine($"reference ar: {clips[i].Arabic}");
            detail.AppendLine($"reference en: {clips[i].English}");
            foreach (var (name, texts) in results)
                detail.AppendLine($"{name}: {texts[i]}");
            detail.AppendLine();
        }
        File.AppendAllText(Path.Combine(Folder, "tbench.txt"), report + Environment.NewLine);
        File.WriteAllText(Path.Combine(Folder, "tbench_detail.txt"), detail.ToString());
    }
}
