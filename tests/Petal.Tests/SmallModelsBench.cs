using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Petal.Core.Models;
using Petal.Recipes.Translation;

namespace Petal.Tests;

/// <summary>
/// Measures small translation models against each other, for choosing what translates live captions on a PC without
/// a graphics card: how good (chrF against the same sentences as FLEURS has them in the language wanted) and how
/// quick (seconds a sentence on the processor). A benchmark, run on purpose.
/// <para>
/// PETAL_SMALL is a folder of models, each as &lt;name&gt;-encoder.onnx, &lt;name&gt;-decoder.onnx, &lt;name&gt;-source.spm,
/// &lt;name&gt;-vocab.json and &lt;name&gt;-config.json (Hugging Face's, for the ids the decoder starts and ends with);
/// PETAL_LIVE the folder of talks (&lt;set&gt;_&lt;language&gt;.json, lists of {"text"}); PETAL_SMALL_SET the set ("talk");
/// PETAL_SMALL_ONLY the systems to measure ("opus", "opus+mul", "through", "m2m", "small100", with commas);
/// PETAL_SMALL_FROM and PETAL_SMALL_INTO the languages ("fa,ur"); PETAL_SMALL_QUICK for the quick translation alone;
/// PETAL_SMALL_PLAIN for a pair's ordinary model where there is a larger one too; PETAL_SMALL_THREADS the threads a
/// model works with. Results are added to small.txt in the models' folder.
/// </para>
/// </summary>
public sealed class SmallModelsBench
{
    private static readonly string Folder = Environment.GetEnvironmentVariable("PETAL_SMALL") ?? "";
    private static readonly string[] Languages = ["en", "ar", "tr", "fa", "ur", "id", "fr", "es"];

    // Where each language's mark sits in M2M100's list (SMaLL-100 shares it).
    private static readonly Dictionary<string, long> Marks = new()
    {
        ["ar"] = 128006, ["en"] = 128022, ["es"] = 128023, ["fr"] = 128028, ["tr"] = 128093, ["fa"] = 128025, ["ur"] = 128095, ["id"] = 128041,
    };

    // The marks Opus-MT's many-language models know these languages by.
    private static readonly Dictionary<string, string[]> OpusMarks = new()
    {
        ["ar"] = [">>ara<<", ">>arb<<"], ["tr"] = [">>tur<<"], ["fa"] = [">>fas<<", ">>pes<<"], ["ur"] = [">>urd<<"], ["id"] = [">>ind<<"],
        ["fr"] = [">>fra<<"], ["es"] = [">>spa<<"], ["en"] = [">>eng<<"],
    };

    private static ModelInfo Part(string name) =>
        new("bench-" + name, name, name, "", "", new FileInfo(Path.Combine(Folder, name)).Length, "");

    private static bool Here(string name) => File.Exists(Path.Combine(Folder, name + "-encoder.onnx")) && File.Exists(Path.Combine(Folder, name + "-decoder.onnx"));

    /// <summary>
    /// A Marian model of the folder, with the ids its config.json gives, and the mark of the language wanted when its
    /// vocabulary has one (a model that knows several languages can't do without).
    /// </summary>
    private static PairModel? Marian(string name, string into, bool several = false)
    {
        if (!Here(name))
            return null;
        using var config = JsonDocument.Parse(File.ReadAllText(Path.Combine(Folder, name + "-config.json")));
        long start = config.RootElement.GetProperty("decoder_start_token_id").GetInt64(), eos = config.RootElement.GetProperty("eos_token_id").GetInt64();
        string? mark;
        using (var vocabulary = JsonDocument.Parse(File.ReadAllText(Path.Combine(Folder, name + "-vocab.json"))))
            mark = OpusMarks[into].FirstOrDefault(m => vocabulary.RootElement.TryGetProperty(m, out _));
        if (mark is null && several)
            return null;
        return new PairModel([Part(name + "-encoder.onnx"), Part(name + "-decoder.onnx"), Part(name + "-source.spm"), Part(name + "-vocab.json")], start, mark, eos);
    }

    private static readonly Dictionary<string, PairModel?> Made = [];

    // PETAL_SMALL_PLAIN: a pair's ordinary model even where the folder has a larger one.
    private static readonly bool Plain = Environment.GetEnvironmentVariable("PETAL_SMALL_PLAIN") is { Length: > 0 };

    private static string[] Chosen(string variable) =>
        Environment.GetEnvironmentVariable(variable) is { Length: > 0 } list ? list.Split(',') : Languages;

    /// <summary>The model made for a pair, when the folder has one: the pair's own, a larger one, or a many-language one.</summary>
    private static PairModel? Direct(string from, string to, bool many)
    {
        string key = $"{from}-{to}-{many}";
        if (Made.TryGetValue(key, out var made))
            return made;
        PairModel? model = (Plain ? null : Marian($"big-{from}-{to}", to)) ?? Marian($"{from}-{to}", to);
        if (model is null && many)
            model = to == "en" ? Marian("mul-en", to) : from == "en" ? Marian("en-mul", to, several: true) : null;
        return Made[key] = model;
    }

    private sealed record Candidate(string Name, Func<string, string, Func<string, int, string>?> For);

    private static IEnumerable<Candidate> Systems()
    {
        // A pair's own model, where there is one.
        yield return new Candidate("opus", (from, to) => Direct(from, to, many: false) is { } model ? (text, width) => model.Translate(text, width, null, default).Text : null);
        // The same, and Opus-MT's many-language models where a pair has none of its own.
        yield return new Candidate("opus+mul", (from, to) => Direct(from, to, many: true) is { } model && Direct(from, to, many: false) is null ? (text, width) => model.Translate(text, width, null, default).Text : null);
        // Through English, by the best models there are for each half.
        yield return new Candidate("through", (from, to) =>
            from != "en" && to != "en" && Direct(from, "en", many: true) is { } first && Direct("en", to, many: true) is { } second
                ? (text, width) => second.Translate(first.Translate(text, width, null, default).Text, width, null, default).Text : null);
        if (Here("m2m100"))
        {
            yield return new Candidate("m2m", (from, to) =>
            {
                var model = new PairModel([Part("m2m100-encoder.onnx"), Part("m2m100-decoder.onnx"), Part("m2m100-source.spm"), Part("m2m100-vocab.json")], 2, null, eos: 2, ban: 1, sourceMark: Marks[from], targetMark: Marks[to], width: 2);
                return (text, width) => model.Translate(text, Math.Min(width, 2), null, default).Text;
            });
        }
        if (Here("small100"))
        {
            yield return new Candidate("small100", (from, to) =>
            {
                var model = new PairModel([Part("small100-encoder.onnx"), Part("small100-decoder.onnx"), Part("small100-source.spm"), Part("small100-vocab.json")], 2, null, eos: 2, ban: 1, sourceMark: Marks[to], width: 2);
                return (text, width) => model.Translate(text, Math.Min(width, 2), null, default).Text;
            });
        }
    }

    private static List<string> Sentences(string talks, string set, string language)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(talks, $"{set}_{language}.json")));
        return doc.RootElement.EnumerateArray().Select(e => e.GetProperty("text").GetString()!).ToList();
    }

    /// <summary>
    /// The small models as the app has them (<see cref="SmallTranslation"/>), on the pairs chosen: whole sentences
    /// quickly and carefully, and each sentence as captions hear it, a few words more at a time, with what the second
    /// line would have settled given back as the start. PETAL_SMALL_ROUTES is the folder of downloaded models;
    /// PETAL_SMALL_MEMORY the memory of the PC to measure as, in GB.
    /// </summary>
    [Fact]
    public void Small_routes_as_the_app_has_them()
    {
        if (Environment.GetEnvironmentVariable("PETAL_SMALL_ROUTES") is not { Length: > 0 } models)
            return;
        ModelStore.Folder = models;
        string talks = Environment.GetEnvironmentVariable("PETAL_LIVE")!;
        string set = Environment.GetEnvironmentVariable("PETAL_SMALL_SET") ?? "talk";
        if (int.TryParse(Environment.GetEnvironmentVariable("PETAL_SMALL_THREADS"), out int threads))
            SmallTranslation.Threads = threads;
        // PETAL_SMALL_MEMORY: as on a PC with that many GB of memory.
        if (double.TryParse(Environment.GetEnvironmentVariable("PETAL_SMALL_MEMORY"), System.Globalization.CultureInfo.InvariantCulture, out double memory))
            PairModel.Memory = (long)(memory * (1L << 30));
        var texts = Languages.ToDictionary(l => l, l => Sentences(talks, set, l));
        var report = Path.Combine(talks, "routes.txt");
        File.AppendAllText(report, $"# {set}, {texts["en"].Count} sentences, {SmallTranslation.Threads} threads, {PairModel.Memory / (double)(1L << 30):0} GB, {DateTime.Now:HH:mm}{Environment.NewLine}");
        foreach (var from in Chosen("PETAL_SMALL_FROM"))
        {
            foreach (var to in Chosen("PETAL_SMALL_INTO"))
            {
                if (to == from || !SmallTranslation.ReadyFor(from, to))
                    continue;
                SmallTranslation.Sentence(texts[from][0], from, to);
                var timer = Stopwatch.StartNew();
                var quick = texts[from].Select(s => SmallTranslation.Sentence(s, from, to)).ToList();
                double quickSeconds = timer.Elapsed.TotalSeconds / quick.Count;

                // As captions hear it: three words more each time, and what two translations in a row agree on is settled.
                var soFar = new SmallSoFar();
                var grown = new List<string>();
                // The first line by each margin: never keep what was shown, keep it within the margin, always keep it.
                double[] margins = [-1e9, 0, 0.1, 0.2, 0.4, 1e9];
                var finals = margins.Select(_ => new List<string>()).ToArray();
                var finalTimes = new List<double>();
                var times = new List<double>();
                int changed = 0, forced = 0, calls = 0;
                foreach (var sentence in texts[from])
                {
                    var words = sentence.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    var shown = new List<string>();
                    int steady = 0;
                    for (int count = Math.Min(3, words.Length); ; count = Math.Min(words.Length, count + 3))
                    {
                        string begun = string.Join(' ', shown.Take(steady));
                        timer.Restart();
                        var now = LiveInterpreter.Unfinished(soFar.Translate(string.Join(' ', words.Take(count)), from, to, begun, default)).Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
                        times.Add(timer.Elapsed.TotalSeconds);
                        calls++;
                        if (begun.Length > 0)
                        {
                            forced++;
                            if (!string.Join(' ', now).StartsWith(begun, StringComparison.Ordinal))
                                changed++;
                        }
                        int same = 0;
                        while (same < now.Count - 1 && same < shown.Count && now[same] == shown[same])
                            same++;
                        steady = Math.Min(Math.Max(same, Math.Min(steady, now.Count - 1)), now.Count);
                        shown = now;
                        if (count == words.Length)
                            break;
                    }
                    grown.Add(string.Join(' ', shown));
                    for (int m = 0; m < margins.Length; m++)
                    {
                        timer.Restart();
                        finals[m].Add(soFar.Final(sentence, from, to, string.Join(' ', shown), default, margins[m]));
                        if (margins[m] == SmallSoFar.Margin)
                            finalTimes.Add(timer.Elapsed.TotalSeconds);
                    }
                }
                var reference = string.Join(' ', texts[to]);
                File.AppendAllText(report, $"{from} > {to}: whole {LiveCaptionsBench.ChrF(string.Join(' ', quick), reference):0.0} ({quickSeconds:0.00}s)"
                    + $"  word by word {LiveCaptionsBench.ChrF(string.Join(' ', grown), reference):0.0} ({times.Average():0.00}s a time, worst {times.Max():0.00}s; settled words changed {changed} of {forced} times){Environment.NewLine}");
                File.WriteAllLines(Path.Combine(talks, $"route-{from}-{to}.txt"), quick.Zip(grown, (a, b) => a + Environment.NewLine + "    " + b));
                var row = new StringBuilder($"    first line by margin ({finalTimes.Average():0.00}s, worst {finalTimes.Max():0.00}s):");
                for (int m = 0; m < margins.Length; m++)
                {
                    double renewed = finals[m].Zip(grown, (f, g) => New(g, f)).Average();
                    row.Append($"  {(margins[m] < -1e8 ? "fresh" : margins[m] > 1e8 ? "kept" : margins[m].ToString("0.00", System.Globalization.CultureInfo.InvariantCulture))} {LiveCaptionsBench.ChrF(string.Join(' ', finals[m]), reference):0.0}/{renewed:P0}");
                }
                File.AppendAllText(report, row + Environment.NewLine);
            }
        }
    }

    /// <summary>The share of the words of <paramref name="now"/> that weren't in <paramref name="before"/>, in order (a full stop gained doesn't count).</summary>
    private static double New(string before, string now)
    {
        static string[] Words(string text) => text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(w => w.TrimEnd('.', ',', '،', '؟', '?', '!', '۔')).ToArray();
        var (a, b) = (Words(before), Words(now));
        if (b.Length == 0)
            return 0;
        var run = new int[a.Length + 1, b.Length + 1];
        for (int i = a.Length - 1; i >= 0; i--)
            for (int j = b.Length - 1; j >= 0; j--)
                run[i, j] = a[i] == b[j] ? run[i + 1, j + 1] + 1 : Math.Max(run[i + 1, j], run[i, j + 1]);
        return 1 - (double)run[0, 0] / b.Length;
    }

    [Fact]
    public void Small_models_measured_on_every_pair()
    {
        if (Folder.Length == 0)
            return;
        ModelStore.Folder = Folder;
        string talks = Environment.GetEnvironmentVariable("PETAL_LIVE")!;
        string set = Environment.GetEnvironmentVariable("PETAL_SMALL_SET") ?? "talk";
        string? only = Environment.GetEnvironmentVariable("PETAL_SMALL_ONLY");
        bool quickOnly = Environment.GetEnvironmentVariable("PETAL_SMALL_QUICK") is { Length: > 0 };
        if (int.TryParse(Environment.GetEnvironmentVariable("PETAL_SMALL_THREADS"), out int threads))
            PairModel.Threads = threads;
        var texts = Languages.ToDictionary(l => l, l => Sentences(talks, set, l));
        var report = Path.Combine(Folder, "small.txt");
        File.AppendAllText(report, $"# {set}, {texts["en"].Count} sentences, {PairModel.Threads} threads, {DateTime.Now:HH:mm}{Environment.NewLine}");
        foreach (var system in Systems())
        {
            if (only is not null && !only.Split(',').Contains(system.Name))
                continue;
            foreach (var from in Chosen("PETAL_SMALL_FROM"))
            {
                var row = new StringBuilder($"{system.Name,-9} {from} >");
                foreach (var to in Chosen("PETAL_SMALL_INTO"))
                {
                    if (to == from || system.For(from, to) is not { } translate)
                        continue;
                    // The first sentence loads the model: it isn't timed.
                    translate(texts[from][0], 1);
                    var quick = new List<string>();
                    var timer = Stopwatch.StartNew();
                    foreach (var sentence in texts[from])
                        quick.Add(translate(sentence, 1));
                    double quickSeconds = timer.Elapsed.TotalSeconds / texts[from].Count;
                    File.WriteAllLines(Path.Combine(Folder, $"quick-{system.Name}-{from}-{to}.txt"), quick);
                    if (quickOnly)
                    {
                        row.Append($"  {to} quick {LiveCaptionsBench.ChrF(string.Join(' ', quick), string.Join(' ', texts[to])):0.0} ({quickSeconds:0.00}s)");
                        continue;
                    }
                    var careful = new List<string>();
                    timer.Restart();
                    foreach (var sentence in texts[from])
                        careful.Add(translate(sentence, 4));
                    double carefulSeconds = timer.Elapsed.TotalSeconds / texts[from].Count;
                    var reference = string.Join(' ', texts[to]);
                    row.Append($"  {to} {LiveCaptionsBench.ChrF(string.Join(' ', careful), reference):0.0} ({carefulSeconds:0.00}s) quick {LiveCaptionsBench.ChrF(string.Join(' ', quick), reference):0.0} ({quickSeconds:0.00}s)");
                    File.WriteAllLines(Path.Combine(Folder, $"out-{system.Name}-{from}-{to}.txt"), careful);
                }
                if (row.ToString().Contains("  "))
                    File.AppendAllText(report, row + Environment.NewLine);
            }
        }
    }
}
