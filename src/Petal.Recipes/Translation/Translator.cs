using System.IO;
using System.Text;
using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Petal.Core.Models;
using Petal.Core.Text;

namespace Petal.Recipes.Translation;

/// <summary>
/// Translation on this PC, by <see cref="GemmaTranslation"/> for any two languages. Where only older models are here:
/// Opus-MT (Helsinki-NLP, CC-BY 4.0), made for each pair, then two of them through English, or M2M100 (Meta, MIT), with beam
/// search over a few candidates. With a cloud model chosen (<see cref="Cloud.CloudAi.Translating"/>), <see cref="TranslateAsync"/>
/// uses that instead.
/// </summary>
public static class Translator
{
    /// <summary>English to Arabic.</summary>
    public static IReadOnlyList<ModelInfo> Models { get; } =
        [ModelCatalog.OpusEnArEncoder, ModelCatalog.OpusEnArDecoder, ModelCatalog.OpusEnArSource, ModelCatalog.OpusEnArVocabulary];

    /// <summary>Arabic to English.</summary>
    public static IReadOnlyList<ModelInfo> EnglishModels { get; } =
        [ModelCatalog.OpusArEnEncoder, ModelCatalog.OpusArEnDecoder, ModelCatalog.OpusArEnSource, ModelCatalog.OpusArEnVocabulary];

    public static bool IsReady => Models.All(ModelStore.IsAvailable);

    public static bool EnglishReady => EnglishModels.All(ModelStore.IsAvailable);

    // English to Arabic writes ">>ara<<" first: its model knows several kinds of Arabic.
    private static readonly PairModel IntoArabic = new(Models, pad: 62801, mark: ">>ara<<");
    private static readonly PairModel IntoEnglish = new(EnglishModels, pad: 62833, mark: null);

    /// <summary>Each line in Arabic; empty lines stay empty.</summary>
    public static IReadOnlyList<string> ToArabic(IReadOnlyList<string> lines, IProgress<double>? progress = null, CancellationToken cancellationToken = default) =>
        IntoArabic.Lines(lines, progress, cancellationToken).Select(Tidy).ToList();

    /// <summary>
    /// One line in Arabic. Quick follows only the likeliest word at each step (about four times less work): for
    /// a sentence still being said, whose words may change before it is translated properly.
    /// </summary>
    public static string ToArabic(string line, bool quick) => Tidy(IntoArabic.Line(line, quick));

    /// <summary>
    /// What the English to Arabic model leaves behind now and then, put right: an English "a" or "the" left among the Arabic
    /// words, every letter given its vowel marks (a style it picked up from some of what it learnt from, which nobody writes
    /// today; the marks a writer would keep, the doubling mark and the "an" ending as in جداً, stay), and the thousands of a
    /// number parted by an Arabic comma (10،000).
    /// </summary>
    public static string Tidy(string arabic)
    {
        if (arabic.Length == 0)
            return arabic;
        var text = System.Text.RegularExpressions.Regex.Replace(arabic, @"(?<=\p{IsArabic}[\p{P}]*) (?:a|an|the)(?= \p{IsArabic})", "");
        // Fatha, damma, kasra, sukun and the "un" and "in" endings: kept when there are only one or two, put there on purpose.
        static bool Vowel(char c) => c is >= (char)0x064C and <= (char)0x0650 or (char)0x0652;
        if (text.Count(Vowel) >= 3)
            text = new string(text.Where(c => !Vowel(c)).ToArray());
        return System.Text.RegularExpressions.Regex.Replace(text, @"(?<=\d)،(?=\d{3}(?!\d))", ",");
    }

    /// <summary>Each line (in Arabic) in English; empty lines stay empty.</summary>
    public static IReadOnlyList<string> ToEnglish(IReadOnlyList<string> lines, IProgress<double>? progress = null, CancellationToken cancellationToken = default) =>
        IntoEnglish.Lines(lines, progress, cancellationToken);

    /// <summary>One line (in Arabic) in English; quick as for <see cref="ToArabic(string, bool)"/>.</summary>
    public static string ToEnglish(string line, bool quick) => IntoEnglish.Line(line, quick);

    // ---------- any language into any other ----------

    /// <summary>The languages of the interface, each translated into and from all the others: code, English name, its own name.</summary>
    public static IReadOnlyList<TranslationLanguage> Languages { get; } =
    [
        new("en", "English", "English"), new("ar", "Arabic", "العربية"), new("tr", "Turkish", "Türkçe"), new("fa", "Persian", "فارسی"),
        new("ur", "Urdu", "اردو"), new("id", "Indonesian", "Bahasa Indonesia"), new("fr", "French", "Français"), new("es", "Spanish", "Español"),
    ];

    /// <summary>A language's English name, as models are told it; the code when it isn't one of these.</summary>
    public static string EnglishName(string code) => Languages.FirstOrDefault(l => l.Code == code)?.EnglishName ?? code;

    // Where each language's mark sits in M2M100's list, after its 128,004 pieces.
    private static readonly Dictionary<string, long> UniversalMarks = new()
    {
        ["ar"] = 128006,
        ["en"] = 128022,
        ["es"] = 128023,
        ["fr"] = 128028,
        ["de"] = 128020,
        ["it"] = 128045,
        ["pt"] = 128075,
        ["ru"] = 128077,
        ["tr"] = 128093,
        ["fa"] = 128025,
        ["ur"] = 128095,
        ["id"] = 128041,
        ["zh"] = 128102,
        ["hi"] = 128036,
        ["ja"] = 128046,
        ["ko"] = 128052,
    };

    private static readonly Dictionary<(string, string), PairModel> Made = new()
    {
        [("en", "ar")] = IntoArabic,
        [("ar", "en")] = IntoEnglish,
    };

    private static PairModel? Direct(string from, string to)
    {
        lock (Made)
        {
            if (Made.TryGetValue((from, to), out var pair))
                return pair;
            if (TranslationCatalog.Pairs.FirstOrDefault(p => p.From == from && p.To == to) is not { } entry)
                return null;
            return Made[(from, to)] = new PairModel(entry.Files, entry.Start, entry.Mark, entry.End);
        }
    }

    private static PairModel Universal(string from, string to)
    {
        lock (Made)
        {
            var key = ("m2m:" + from, to);
            return Made.TryGetValue(key, out var pair) ? pair
                : Made[key] = new PairModel(TranslationCatalog.Universal, 2, null, eos: 2, ban: 1, sourceMark: UniversalMarks[from], targetMark: UniversalMarks[to], width: 2);
        }
    }

    /// <summary>
    /// The steps from one language to another on this PC, quickest first: a pair's own model, two through English, or M2M100.
    /// Models already here come before a new download, so once M2M100 is here no pair needs anything more.
    /// </summary>
    private static List<PairModel> Route(string from, string to)
    {
        bool universal = UniversalMarks.ContainsKey(from) && UniversalMarks.ContainsKey(to);
        if (Direct(from, to) is { } direct && (direct.Ready || !universal || !TranslationCatalog.Universal.All(ModelStore.IsAvailable)))
            return [direct];
        var through = Direct(from, "en") is { } first && Direct("en", to) is { } second ? new List<PairModel> { first, second } : null;
        if (through is not null && (through.All(p => p.Ready) || !universal || !TranslationCatalog.Universal.All(ModelStore.IsAvailable)))
            return through;
        if (universal)
            return [Universal(from, to)];
        return through ?? throw new InvalidOperationException($"No translation from {from} into {to}.");
    }

    /// <summary>The models translating from one language to another needs, downloaded or not.</summary>
    public static IReadOnlyList<ModelInfo> ModelsFor(string from, string to) =>
        from == to ? [] : GemmaTranslation.Models;

    /// <summary>The older models a pair would use, for Opus-MT's and M2M100's own route.</summary>
    internal static IReadOnlyList<ModelInfo> PairModelsFor(string from, string to) =>
        from == to ? [] : Route(from, to).SelectMany(p => p.Files).ToList();

    /// <summary>Whether this translation can run now: the model is here, or a cloud model translates.</summary>
    public static bool ReadyFor(string from, string to) =>
        from == to || Cloud.CloudAi.CanTranslate || GemmaTranslation.Ready;

    /// <summary>Whether a line can be put into another language to search by: any model does for that.</summary>
    public static bool CanSearchIn(string from, string to) =>
        GemmaTranslation.Ready || PairModelsFor(from, to).All(ModelStore.IsAvailable);

    /// <summary>Each line from one language into another, on this PC; empty lines stay empty.</summary>
    public static IReadOnlyList<string> Lines(IReadOnlyList<string> lines, string from, string to, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        if (from == to)
            return lines;
        if (GemmaTranslation.Ready)
            return ByMeaning(lines, from, to, progress, cancellationToken);
        IReadOnlyList<string> current = lines;
        var route = Route(from, to);
        for (int i = 0; i < route.Count; i++)
            current = route[i].Lines(current, progress is null ? null : new Step(progress, i, route.Count), cancellationToken);
        return to == "ar" ? current.Select(Tidy).ToList() : current;
    }

    /// <summary>One line from one language into another on this PC; quick follows only the likeliest word at each step.</summary>
    public static string Line(string line, string from, string to, bool quick = false)
    {
        if (from == to)
            return line;
        // Quick is for searching: a small model for the pair saves loading a large one.
        if (GemmaTranslation.Ready && !(quick && PairModelsFor(from, to).All(ModelStore.IsAvailable)))
            return Tidied(GemmaTranslation.TranslateAsync(line, from, to, live: quick).GetAwaiter().GetResult(), to);
        foreach (var pair in Route(from, to))
            line = pair.Line(line, quick);
        return to == "ar" ? Tidy(line) : line;
    }

    private static string Tidied(string text, string to) => to == "ar" ? Tidy(text) : text;

    /// <summary>Each line in turn, with the two before it to read it by: a line of subtitles or of a document is often half a sentence.</summary>
    private static List<string> ByMeaning(IReadOnlyList<string> lines, string from, string to, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var result = new List<string>(lines.Count);
        var earlier = new List<string>();
        try
        {
            for (int i = 0; i < lines.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var line = lines[i].Trim();
                result.Add(line.Length == 0 || !line.Any(char.IsLetter) ? line
                    : Tidied(GemmaTranslation.TranslateAsync(line, from, to, earlier.TakeLast(2).ToList(), false, cancellationToken).GetAwaiter().GetResult(), to));
                if (line.Length > 0)
                    earlier.Add(line);
                progress?.Report((i + 1.0) / lines.Count);
            }
        }
        finally
        {
            GemmaTranslation.Finished();
        }
        return result;
    }

    /// <summary>Each line translated by the cloud model chosen, or else on this PC; <paramref name="from"/> may be "auto" with a cloud model.</summary>
    public static async Task<IReadOnlyList<string>> TranslateAsync(IReadOnlyList<string> lines, string from, string to, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        if (Cloud.CloudAi.CanTranslate)
            return await Cloud.CloudAi.TranslateAsync(lines, to, progress, cancellationToken).ConfigureAwait(false);
        // What isn't known stays so for the model that reads any language: a wrong guess would mislead it.
        if (from is "auto" or "" && !GemmaTranslation.Ready)
            from = LanguageGuess.Of(string.Join(' ', lines.Take(40))) ?? "en";
        var source = from;
        return await Task.Run(() => Lines(lines, source, to, progress, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>One step's share of the progress of a route of a few.</summary>
    private sealed class Step(IProgress<double> inner, int index, int count) : IProgress<double>
    {
        public void Report(double value) => inner.Report((index + Math.Clamp(value, 0, 1)) / count);
    }

    /// <summary>The pieces' ids for English to Arabic: the target language mark, the text, end of sentence.</summary>
    internal static long[] Encode(string text) => IntoArabic.Encode(text);

    /// <summary>The pieces' ids for Arabic to English: the text, end of sentence.</summary>
    internal static long[] EncodeArabic(string text) => IntoEnglish.Encode(text);

    /// <summary>Subtitles in another language: the same cues and timing, the words translated.</summary>
    /// <param name="from">The language they are in; "auto" tells it from the words.</param>
    public static async Task<string> TranslateSubtitlesAsync(string text, bool vtt, string from, string to, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var cues = Petal.Engines.Files.Subtitles.SubtitleConverter.Parse(text);
        // A cue's lines are one sentence more often than not: translated together.
        var words = await TranslateAsync(cues.Select(c => string.Join(' ', c.Text.Split('\n').Select(StripTags))).ToList(), from, to, progress, cancellationToken).ConfigureAwait(false);
        var translated = cues.Select((c, i) => c with { Text = words[i] }).ToList();
        return vtt ? Petal.Engines.Files.Subtitles.SubtitleConverter.WriteVtt(translated) : Petal.Engines.Files.Subtitles.SubtitleConverter.WriteSrt(translated);
    }

    /// <summary>Formatting tags (&lt;i&gt;, {\an8}) don't go through translation.</summary>
    private static string StripTags(string line) =>
        System.Text.RegularExpressions.Regex.Replace(line, @"<[^>]{1,20}>|\{\\[^}]{1,20}\}", "").Trim();
}

/// <summary>A language translated into and from.</summary>
public sealed record TranslationLanguage(string Code, string EnglishName, string NativeName);
