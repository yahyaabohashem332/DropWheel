using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Petal.Core.Models;
using Petal.Engines.Media;
using Petal.Recipes.Translation;

namespace Petal.Tests;

/// <summary>
/// Measures live captions whole, as they run: real speech fed in real time through the speech engine and the
/// interpreter, with the translation model on this PC. A benchmark, run on purpose.
/// <para>
/// PETAL_LIVE is a folder of talks: &lt;name&gt;.wav (16 kHz mono, 16-bit) and &lt;name&gt;.json, a list of
/// {"start", "end", "text"} for each sentence said. A talk named &lt;kind&gt;_&lt;language&gt; ("talk_en") is scored
/// against the same sentences in the language wanted (&lt;kind&gt;_&lt;into&gt;.json), when that file is there: FLEURS
/// has the same sentences read aloud in every language. PETAL_LIVE_SPEECH is a Whisper model; PETAL_LIVE_TALK the
/// talk's name; PETAL_LIVE_FROM its language or "auto"; PETAL_LIVE_INTO the language wanted; PETAL_LIVE_MODELS the
/// folder of downloaded models, when it isn't the usual one; PETAL_LIVE_GAPS=1 sends no sound at all between the
/// sentences, as the PC's sound does while nothing plays; PETAL_LIVE_SHORT=1 listens as a PC without a graphics card
/// does (a short encoder context, no aligned word times). Each run writes &lt;talk&gt;-&lt;into&gt;.log, everything
/// that happened and when, and adds a line to live.txt.
/// </para>
/// </summary>
public sealed class LiveCaptionsBench
{
    private static readonly string Folder = Environment.GetEnvironmentVariable("PETAL_LIVE") ?? "";

    private sealed record Said(double Start, double End, string Text);

    private static List<Said> Marks(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        return doc.RootElement.EnumerateArray().Select(e => new Said(e.GetProperty("start").GetDouble(), e.GetProperty("end").GetDouble(), e.GetProperty("text").GetString()!)).ToList();
    }

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

    /// <summary>Words as they are compared: lower case, without marks or punctuation.</summary>
    internal static string[] Plain(string text)
    {
        var sb = new StringBuilder();
        foreach (var c in text.Normalize(NormalizationForm.FormKC))
            sb.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : char.IsWhiteSpace(c) || c is '-' or '‌' ? ' ' : '\0');
        return sb.ToString().Replace("\0", "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>The share of words wrong (changed, missing or added) against what was said.</summary>
    internal static double WordErrors(string said, string heard)
    {
        var r = Plain(said);
        var h = Plain(heard);
        var d = new int[r.Length + 1, h.Length + 1];
        for (int i = 0; i <= r.Length; i++) d[i, 0] = i;
        for (int j = 0; j <= h.Length; j++) d[0, j] = j;
        for (int i = 1; i <= r.Length; i++)
            for (int j = 1; j <= h.Length; j++)
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (r[i - 1] == h[j - 1] ? 0 : 1));
        return (double)d[r.Length, h.Length] / Math.Max(1, r.Length);
    }

    /// <summary>chrF (Popović 2015): how alike two texts are by their runs of one to six letters, recall counting double; 0 to 100.</summary>
    internal static double ChrF(string translation, string reference)
    {
        static Dictionary<string, int> Runs(string text, int n)
        {
            text = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            var runs = new Dictionary<string, int>();
            for (int i = 0; i + n <= text.Length; i++)
                runs[text.Substring(i, n)] = runs.GetValueOrDefault(text.Substring(i, n)) + 1;
            return runs;
        }
        double precision = 0, recall = 0;
        int orders = 0;
        for (int n = 1; n <= 6; n++)
        {
            var t = Runs(translation, n);
            var r = Runs(reference, n);
            if (t.Count == 0 || r.Count == 0)
                continue;
            int match = t.Sum(run => Math.Min(run.Value, r.GetValueOrDefault(run.Key)));
            precision += (double)match / t.Values.Sum();
            recall += (double)match / r.Values.Sum();
            orders++;
        }
        if (orders == 0 || precision + recall == 0)
            return 0;
        precision /= orders;
        recall /= orders;
        return 100 * 5 * precision * recall / (4 * precision + recall);
    }

    [Fact]
    public void The_scores_measure_what_they_say()
    {
        Assert.Equal(0, WordErrors("The first clocks used water.", "the first clocks used water"));
        Assert.Equal(0.2, WordErrors("The first clocks used water.", "The first clocks use water."), 3);
        Assert.Equal(100, ChrF("سننقل الإطلاق", "سننقل الإطلاق"), 3);
        Assert.InRange(ChrF("We will move the launch.", "We'll move the launch to Thursday."), 40, 80);
        Assert.True(ChrF("Something else entirely.", "We'll move the launch to Thursday.") < 20);
    }

    [Fact]
    public async Task Captions_on_a_talk_said_in_real_time()
    {
        if (Folder.Length == 0)
            return;
        string name = Environment.GetEnvironmentVariable("PETAL_LIVE_TALK") ?? "talk_en";
        string from = Environment.GetEnvironmentVariable("PETAL_LIVE_FROM") ?? "auto";
        string into = Environment.GetEnvironmentVariable("PETAL_LIVE_INTO") ?? "ar";
        string speech = Environment.GetEnvironmentVariable("PETAL_LIVE_SPEECH")!;
        bool gaps = Environment.GetEnvironmentVariable("PETAL_LIVE_GAPS") == "1";
        if (Environment.GetEnvironmentVariable("PETAL_LIVE_MODELS") is { Length: > 0 } models)
            ModelStore.Folder = models;
        var audio = ReadWav(Path.Combine(Folder, name + ".wav"));
        var marks = Marks(Path.Combine(Folder, name + ".json"));

        var load = Stopwatch.StartNew();
        await GemmaTranslation.PrepareAsync();
        double prepared = load.Elapsed.TotalSeconds;
        // As on a PC without a graphics card: Whisper reads only as much as there is to hear.
        bool brief = Environment.GetEnvironmentVariable("PETAL_LIVE_SHORT") == "1";
        using var engine = new StreamingTranscriber(speech, from, alignWords: !brief, ModelStore.Find(ModelCatalog.VoiceFinder), Transcriber.Languages);
        await engine.WarmUpAsync(CancellationToken.None);
        var interpreter = new LiveInterpreter(new LiveTranslator(), () => into);
        var clock = Stopwatch.StartNew();
        var log = new StringBuilder();
        var sentences = new List<(StreamSentence Sentence, double At)>();
        var lines = new List<(LiveLine Line, double At)>();
        var passes = new List<double>();
        var behind = new List<double>();
        void Note(string kind, string text)
        {
            lock (log)
                log.AppendLine($"{clock.Elapsed.TotalSeconds,7:0.00} {kind} {text}");
        }
        engine.Passed += p => Note("p ", $"[{p.From:0.00}-{p.To:0.00}]{(p.Closing ? " closing" : "")} {p.Took:0.00}s {p.Words.Count} words");
        engine.Changed += s =>
        {
            lock (passes)
            {
                passes.Add(engine.LastPass);
                if (s.Newest > 0 && !gaps)
                    behind.Add(clock.Elapsed.TotalSeconds - s.Newest);
            }
            Note("d ", $"{s.Number} {s.Current} | {s.Draft}");
            interpreter.Heard(s);
        };
        engine.Sentence += s =>
        {
            lock (sentences)
                sentences.Add((s, clock.Elapsed.TotalSeconds));
            Note("S ", $"{s.Number} [{s.Start:0.00}-{s.End:0.00}] {s.Text}");
            interpreter.Finished(s);
        };
        engine.Failed += e => Note("!!", "speech: " + e.Message);
        interpreter.Failed += e => Note("!!", "translation: " + e.Message);
        interpreter.Line += l =>
        {
            lock (lines)
                lines.Add((l, clock.Elapsed.TotalSeconds));
            Note(l.Final ? l.Proper ? "1=" : "1~" : "2 ", l.Final ? $"{l.Number} {l.Text}" : $"{l.Number} {l.Text[..l.Steady]} | {l.Text[l.Steady..]}");
        };
        using var stop = new CancellationTokenSource();
        var listening = engine.RunAsync(stop.Token);
        var translating = Task.Run(() => interpreter.RunAsync(stop.Token));
        for (int i = 0; i < audio.Length; i += 1600)
        {
            double at = (double)i / StreamingTranscriber.Rate;
            if (!gaps || marks.Any(m => at > m.Start - 0.15 && at < m.End + 0.15))
                engine.Add(audio.AsSpan(i, Math.Min(1600, audio.Length - i)));
            double wait = (double)(i + 1600) / StreamingTranscriber.Rate - clock.Elapsed.TotalSeconds;
            if (wait > 0)
                await Task.Delay(TimeSpan.FromSeconds(wait));
        }
        await Task.Delay(6000);
        stop.Cancel();
        await listening;
        await translating;
        GemmaTranslation.Finished();

        var heard = string.Join(' ', sentences.Select(s => s.Sentence.Text));
        var proper = lines.Where(l => l.Line.Final && l.Line.Proper).ToList();
        // How long after a sentence was over its translation stood on the first line: as it stood, and properly.
        var moved = new List<double>();
        var right = new List<double>();
        foreach (var (sentence, at) in sentences)
        {
            var first = lines.Where(l => l.Line.Final && l.Line.Number >= sentence.Number).Select(l => l.At).DefaultIfEmpty(-1).Min();
            var properly = proper.Where(l => l.Line.Number >= sentence.Number).Select(l => l.At).DefaultIfEmpty(-1).Min();
            if (first >= 0)
                moved.Add(first - at);
            if (properly >= 0)
                right.Add(properly - at);
        }
        // The second line: whether anything it had called settled ever changed while its sentence went on.
        var second = lines.Where(l => !l.Line.Final).ToList();
        int unsettled = 0;
        for (int i = 1; i < second.Count; i++)
        {
            var (a, b) = (second[i - 1].Line, second[i].Line);
            if (a.Number == b.Number && a.Steady > 0 && !b.Text.StartsWith(a.Text[..a.Steady], StringComparison.Ordinal))
                unsettled++;
        }
        // The same words final twice running.
        int twice = 0;
        for (int i = 1; i < sentences.Count; i++)
        {
            var a = Plain(sentences[i - 1].Sentence.Text);
            var b = Plain(sentences[i].Sentence.Text);
            if (a.Length >= 2 && b.Length >= 2 && b.Count(a.Contains) >= b.Length * 0.8)
                twice++;
        }
        var report = new StringBuilder();
        report.Append($"{name} ({from} as {engine.Language}) into {into}{(gaps ? ", no sound between sentences" : "")}: ");
        report.Append($"words wrong {WordErrors(string.Join(' ', marks.Select(m => m.Text)), heard):P1}; {sentences.Count} sentences for {marks.Count} said, {twice} said twice; ");
        report.Append($"a pass {passes.DefaultIfEmpty().Average():0.00} s (worst {passes.DefaultIfEmpty().Max():0.00}), newest word {behind.DefaultIfEmpty().Average():0.00} s behind the talk; ");
        report.Append($"translation ready in {prepared:0.0} s, on a graphics card {GemmaTranslation.OnCard}; first line as it stood {moved.DefaultIfEmpty().Average():0.00} s after the sentence, properly {right.DefaultIfEmpty().Average():0.00} s (worst {right.DefaultIfEmpty().Max():0.00}); ");
        report.Append($"second line changed {second.Count} times, settled words changed {unsettled} times");
        // The same sentences as FLEURS has them in the language wanted, when they are here.
        int cut = name.IndexOf('_');
        var reference = cut > 0 ? Path.Combine(Folder, $"{name[..cut]}_{into}.json") : "";
        if (File.Exists(reference) && proper.Count > 0)
            report.Append($"; chrF {ChrF(string.Join(' ', proper.Select(l => l.Line.Text)), string.Join(' ', Marks(reference).Select(m => m.Text))):0.0}");
        File.WriteAllText(Path.Combine(Folder, $"{name}-{into}.log"), log.ToString());
        File.AppendAllText(Path.Combine(Folder, "live.txt"), report + Environment.NewLine);
        Assert.NotEmpty(sentences);
    }
}
