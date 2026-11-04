using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Petal.Core.Models;
using Petal.Engines.Media;

namespace Petal.Recipes.Translation;

/// <summary>
/// A dub's lines translated the way a dubbing translator works, by the AI model on this PC (Qwen3 4B): the meaning
/// rather than the words, as someone would say it in the other language, with the lines before in sight (so a pronoun,
/// a name or a misheard word is understood from what came before), religious and common phrases in their usual form,
/// and each line kept about as long as the time it has to be said in. A few lines at a time; a line the model leaves
/// out or answers in the wrong language is translated by Opus-MT instead.
/// </summary>
public static partial class DubTranslation
{
    /// <summary>Lines translated together, and lines before them shown for what they mean.</summary>
    public const int Batch = 8, Context = 4;

    /// <summary>The model that writes the translations.</summary>
    public static ModelInfo Writer { get; set; } = ModelCatalog.GemmaTranslator;

    public static IReadOnlyList<ModelInfo> Models => [ModelCatalog.LlamaCpp, Writer];

    public static bool Ready => Cloud.CloudAi.CanWrite || Models.All(ModelStore.IsAvailable);

    /// <summary>
    /// Each line's translation, in order. <paramref name="partial"/> hears how many lines are done; <paramref name="fallback"/>
    /// translates the lines the model couldn't (Opus-MT, when not given).
    /// </summary>
    public static async Task<List<string>> TranslateAsync(IReadOnlyList<DubLine> lines, string from, string into, IProgress<double>? progress = null,
        CancellationToken cancellationToken = default, Func<IReadOnlyList<string>, IReadOnlyList<string>>? fallback = null)
    {
        var result = new string?[lines.Count];
        // Read through once first, as a translator would: what it is about, who speaks, the words that matter.
        string brief = "";
        try
        {
            brief = await AskPetal.WriteAsync(BriefInstruction(from, into), string.Join('\n', lines.Select(l => l.Original)), null, 260,
                cancellationToken, temperature: 0, model: Writer).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
        }
        progress?.Report(0.1);
        for (int start = 0; start < lines.Count; start += Batch)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int count = Math.Min(Batch, lines.Count - start);
            var earlier = Enumerable.Range(Math.Max(0, start - Context), Math.Min(Context, start))
                .Select(i => (lines[i].Original, result[i] ?? "")).ToList();
            var batch = lines.Skip(start).Take(count).ToList();
            int words = batch.Sum(l => Target(l, from, into));
            string reply;
            try
            {
                reply = await AskPetal.WriteAsync(Instruction(from, into), Prompt(brief, earlier, batch, from, into), null, Math.Clamp(80 + words * 5, 200, 1400),
                    cancellationToken, temperature: 0, model: Writer, begun: "1.").ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                reply = "";
            }
            var read = Parse(reply, count);
            for (int k = 0; k < count; k++)
                if (read.TryGetValue(k + 1, out var text) && Fits(text, batch[k], from, into))
                    result[start + k] = into == "ar" ? Translator.Tidy(text) : text;
            // A line that says again what another one says took that one's words (said twice in the dub): asked alone.
            foreach (int k in Repeated(result, start, count, lines))
            {
                var alone = Enumerable.Range(Math.Max(0, start + k - Context), Math.Min(Context, start + k))
                    .Select(i => (lines[i].Original, result[i] ?? "")).ToList();
                try
                {
                    var again = Parse(await AskPetal.WriteAsync(Instruction(from, into), Prompt(brief, alone, [batch[k]], from, into), null,
                        Math.Clamp(80 + Target(batch[k], from, into) * 5, 120, 600), cancellationToken, temperature: 0, model: Writer, begun: "1.").ConfigureAwait(false), 1);
                    result[start + k] = again.TryGetValue(1, out var text) && Fits(text, batch[k], from, into) ? (into == "ar" ? Translator.Tidy(text) : text) : null;
                }
                catch (InvalidOperationException)
                {
                    result[start + k] = null;
                }
            }
            progress?.Report(0.1 + 0.9 * (start + count) / lines.Count);
        }
        // What the model couldn't give: the word-for-word translation rather than nothing.
        var missing = Enumerable.Range(0, lines.Count).Where(i => result[i] is null && lines[i].Original.Trim().Length > 0).ToList();
        if (missing.Count > 0)
        {
            var originals = missing.Select(i => lines[i].Original).ToList();
            // A key translating has them; on a key that writes with no translation model here, the company's model is asked for each alone.
            var plain = fallback is null && Cloud.CloudAi.CanTranslate ? await Translator.TranslateAsync(originals, from, into, null, cancellationToken).ConfigureAwait(false)
                : fallback is null && Cloud.CloudAi.CanWrite && !Translator.ReadyFor(from, into) ? await EachAloneAsync(originals, into, cancellationToken).ConfigureAwait(false)
                : fallback?.Invoke(originals) ?? Translator.Lines(originals, from, into, null, cancellationToken);
            for (int k = 0; k < missing.Count; k++)
                result[missing[k]] = plain[k];
        }
        return result.Select(r => (r ?? "").Trim()).ToList();
    }

    private static async Task<IReadOnlyList<string>> EachAloneAsync(IReadOnlyList<string> originals, string into, CancellationToken cancellationToken)
    {
        var translated = new List<string>(originals.Count);
        foreach (var original in originals)
            translated.Add(await AskPetal.WriteAsync(AskPetal.Instructions.TranslateInto(Translator.EnglishName(into)), original, Math.Clamp(original.Length * 2, 200, 800), cancellationToken).ConfigureAwait(false));
        return translated;
    }

    /// <summary>
    /// The lines of a batch (by place in it) whose translation is mostly the same words as an earlier one's — in the batch or
    /// just before it — when their originals are not alike: the later of the two took the other's words.
    /// </summary>
    internal static List<int> Repeated(IReadOnlyList<string?> result, int start, int count, IReadOnlyList<DubLine> lines)
    {
        var found = new List<int>();
        for (int k = 0; k < count; k++)
        {
            int i = start + k;
            if (result[i] is not { } text)
                continue;
            for (int j = Math.Max(0, start - 1); j < i; j++)
            {
                if (result[j] is not { } other)
                    continue;
                if (Overlap(text, other) > 0.6 && Overlap(lines[i].Original, lines[j].Original) < 0.4)
                {
                    found.Add(k);
                    break;
                }
            }
        }
        return found;
    }

    /// <summary>How much of the shorter text's words the other has too (0 to 1).</summary>
    private static double Overlap(string a, string b)
    {
        static HashSet<string> Set(string t) => t.ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.Trim('.', ',', ';', ':', '!', '?', '"', '\'', '…', '،', '؛', '؟')).Where(w => w.Length > 2).ToHashSet();
        var x = Set(a);
        var y = Set(b);
        if (x.Count < 3 || y.Count < 3)
            return 0;
        return x.Intersect(y).Count() / (double)Math.Min(x.Count, y.Count);
    }

    private static string Name(string language) => Translator.EnglishName(language);

    /// <summary>What the model is told: the dubbing translator's job.</summary>
    internal static string Instruction(string from, string into)
    {
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"You are a professional dubbing translator. You translate the spoken lines of a video from {Name(from)} into {Name(into)}; a voice actor will say your lines in place of the original speaker.\n");
        sb.Append(CultureInfo.InvariantCulture, $"- Translate the meaning, not word for word: say it the way a native {Name(into)} speaker naturally would, keeping the tone (casual, formal, religious, poetic) and who is speaking to whom.\n");
        sb.Append("- The lines come from speech recognition and may have misheard or broken words; use the lines around them to understand what was meant, and translate that.\n");
        sb.Append(CultureInfo.InvariantCulture, $"- Keep names and numbers. Say set phrases (greetings, blessings, sayings) the way they are usually said in {Name(into)}.\n");
        sb.Append("- Each line must fit the time of the original: aim for about the number of words given for it, never much more.\n");
        sb.Append("- Give exactly one translation for each numbered line, in the same order: do not merge, split, skip or add lines, and do not explain.\n");
        sb.Append("- Translate each line on its own: never move words or meaning from one line into another, and never say a line's content again in another line, even when a sentence runs over two lines.\n");
        if (into == "ar")
            sb.Append("- Write clear Modern Standard Arabic, as in professional dubbing, without diacritics.\n");
        sb.Append("Reply only with the numbered translations, one per line:\n1. ...\n2. ...");
        return sb.ToString();
    }

    /// <summary>What the model is told to write after reading the whole video: a translator's notes.</summary>
    internal static string BriefInstruction(string from, string into) =>
        $"You are preparing to translate a video from {Name(from)} into {Name(into)} for dubbing. The user gives you its transcript, made by speech recognition, " +
        "so some words are misheard. Write short notes in English for the translator: what the video is (for example a religious lesson, a poem, an interview, " +
        "a tutorial) and its tone; who is speaking to whom; and up to ten key names, terms and set phrases, each with what was most likely really said where " +
        $"the transcript is garbled, and how to say it in {Name(into)}. At most 120 words. Reply with the notes only.";

    /// <summary>What the model is given: the notes on the whole video, the lines just before (for their meaning) and the numbered lines to translate.</summary>
    internal static string Prompt(string brief, IReadOnlyList<(string Original, string Translation)> earlier, IReadOnlyList<DubLine> lines, string from, string into)
    {
        var sb = new StringBuilder();
        if (brief.Trim().Length > 0)
            sb.Append("About the video (notes made after reading all of it):\n").Append(brief.Trim()).Append("\n\n");
        if (earlier.Count > 0)
        {
            sb.Append("Just before, for context (already translated):\n");
            foreach (var (original, translation) in earlier)
                sb.Append("- ").Append(original).Append(translation.Length > 0 ? " => " + translation : "").Append('\n');
            sb.Append('\n');
        }
        sb.Append("Translate these lines (the number in brackets is about how many words each should have):\n");
        for (int i = 0; i < lines.Count; i++)
            sb.Append(CultureInfo.InvariantCulture, $"{i + 1}. [{Target(lines[i], from, into)}] {lines[i].Original}\n");
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// About how many words a line's translation should have: from the time it has (Arabic is said at about 2.1 words a
    /// second, English at 2.6) and from how many words the original has (Arabic says in fewer words what English says in
    /// more), the two taken together.
    /// </summary>
    internal static int Target(DubLine line, string from, string into)
    {
        int original = Words(line.Original);
        double rate = into == "ar" ? 2.1 : 2.6;
        double ratio = from == into ? 1 : into == "ar" ? 0.8 : 1.25;
        return Math.Max(1, (int)Math.Round((line.Length * rate + original * ratio) / 2));
    }

    private static int Words(string text) => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    /// <summary>The numbered translations in a reply, by number (a number given twice keeps its first).</summary>
    internal static Dictionary<int, string> Parse(string reply, int count)
    {
        var found = new Dictionary<int, string>();
        foreach (var raw in reply.Split('\n'))
        {
            var m = Numbered().Match(raw.Trim());
            if (!m.Success || !int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int n) || n < 1 || n > count)
                continue;
            // The word count it was given, said back.
            var text = Bracket().Replace(m.Groups[2].Value, "").Trim().Trim('"', '“', '”', '«', '»').Trim();
            if (text.Length > 0)
                found.TryAdd(n, text);
        }
        return found;
    }

    /// <summary>A translation in the right language, not wildly longer than the line has time for.</summary>
    internal static bool Fits(string text, DubLine line, string from, string into)
    {
        int arabic = text.Count(c => c is >= (char)0x0600 and <= (char)0x06FF), latin = text.Count(char.IsAsciiLetter);
        bool language = into == "ar" ? arabic > latin : into == "en" ? latin > arabic : arabic <= latin;
        return language && Words(text) <= Target(line, from, into) * 2.5 + 3;
    }

    [GeneratedRegex(@"^(\d{1,2})\s*[.)\-:]\s*(.+)$")]
    private static partial Regex Numbered();

    [GeneratedRegex(@"^\[\s*\d+\s*\]\s*")]
    private static partial Regex Bracket();
}
