using Petal.Core.Models;

namespace Petal.Recipes.Translation;

/// <summary>
/// Translation by small models made for it, for where the large one can't keep up: live captions on a PC without a
/// graphics card, where <see cref="GemmaTranslation"/> takes four seconds a sentence and these a fifth of one.
/// <para>
/// Measured on the same sixteen FLEURS sentences in all eight languages (chrF, the large model's beside it): a pair's
/// own Opus-MT model is as good or nearly, English to Arabic 61 (66), Arabic to English 71 (74), English to Turkish
/// 69 (68), Turkish to English 75 (77), English to Indonesian 78 (82), to French 78 (81), to Spanish 65 (65). Two of
/// them through English do for the pairs without a model: Turkish to Arabic 59 (59), Arabic to Turkish 57 (58),
/// French to Arabic 61 (60). Opus-MT is poor at Persian and Urdu (42 to 55), so M2M100 translates those: English to
/// Persian 64 (68), Persian to English 68 (70), English to Urdu 56 (63), Urdu to English 63 (74).
/// </para>
/// </summary>
public static class SmallTranslation
{
    private static readonly string[] Known = ["en", "ar", "tr", "fa", "ur", "id", "fr", "es"];

    // The languages Opus-MT has no good model for.
    private static readonly string[] ByUniversal = ["fa", "ur"];

    // Where each language's mark sits in M2M100's list, after its 128,004 pieces.
    private static readonly Dictionary<string, long> Marks = new()
    {
        ["ar"] = 128006, ["en"] = 128022, ["es"] = 128023, ["fr"] = 128028, ["tr"] = 128093, ["fa"] = 128025, ["ur"] = 128095, ["id"] = 128041,
    };

    private static readonly Dictionary<(string, string), PairModel?> Made = [];

    private static PairModel? Pair(string from, string to)
    {
        lock (Made)
        {
            if (Made.TryGetValue((from, to), out var made))
                return made;
            var entry = TranslationCatalog.Pairs.FirstOrDefault(p => p.From == from && p.To == to);
            return Made[(from, to)] = entry is null ? null : new PairModel(entry.Files, entry.Start, entry.Mark, entry.End);
        }
    }

    private static PairModel Universal(string from, string to)
    {
        lock (Made)
        {
            var key = ("all:" + from, to);
            return Made.TryGetValue(key, out var made) && made is not null ? made
                : (Made[key] = new PairModel(TranslationCatalog.Universal, 2, null, eos: 2, ban: 1, sourceMark: Marks[from], targetMark: Marks[to], width: 2))!;
        }
    }

    /// <summary>The best small models from one language to another, in the order they translate; none when there are none for the two.</summary>
    private static IReadOnlyList<PairModel> Best(string from, string to)
    {
        if (from == to || !Known.Contains(from) || !Known.Contains(to))
            return [];
        if (ByUniversal.Contains(from) || ByUniversal.Contains(to))
            return [Universal(from, to)];
        if (Pair(from, to) is { } own)
            return [own];
        return Pair(from, "en") is { } first && Pair("en", to) is { } second ? [first, second] : [Universal(from, to)];
    }

    /// <summary>The models that translate now: the best, or the one for every pair while that is here and they aren't.</summary>
    internal static IReadOnlyList<PairModel> Route(string from, string to)
    {
        var best = Best(from, to);
        return best.Count == 0 || best.All(m => m.Ready) || !TranslationCatalog.Universal.All(ModelStore.IsAvailable) ? best : [Universal(from, to)];
    }

    /// <summary>Whether small models have these two languages at all.</summary>
    public static bool Knows(string from, string to) => Best(from, to).Count > 0;

    /// <summary>The files the best small models for a pair are made of, here or not.</summary>
    public static IReadOnlyList<ModelInfo> ModelsFor(string from, string to) =>
        Best(from, to).SelectMany(m => m.Files).Distinct().ToList();

    /// <summary>Whether small models can translate this pair now.</summary>
    public static bool ReadyFor(string from, string to) =>
        Route(from, to) is { Count: > 0 } route && route.All(m => m.Ready);

    /// <summary>Loads a pair's models, so the first sentence doesn't wait for them.</summary>
    public static void Prepare(string from, string to)
    {
        if (!ReadyFor(from, to))
            return;
        foreach (var model in Route(from, to))
            model.Prepare();
    }

    /// <summary>
    /// How many threads the models work with, for before they are first used. Beside a speech model that keeps every
    /// core busy, a few do better than many.
    /// </summary>
    public static int Threads
    {
        get => PairModel.Threads;
        set => PairModel.Threads = Math.Clamp(value, 1, 16);
    }

    /// <summary>
    /// A whole sentence, the likeliest word at each step. (Following a few candidates at once, as a careful translation
    /// of a document does, was measured on six pairs: it changed chrF by between -1.5 and +1.3, on average nothing,
    /// for a third more time.)
    /// </summary>
    public static string Sentence(string text, string from, string to, CancellationToken cancellationToken = default)
    {
        text = text.Trim();
        if (text.Length == 0 || !text.Any(char.IsLetter))
            return text;
        var route = Route(from, to);
        if (route.Count == 0)
            throw new InvalidOperationException($"No small model translates {from} into {to}.");
        foreach (var model in route)
            text = model.Translate(text, 1, null, cancellationToken).Text;
        return to == "ar" ? Translator.Tidy(text) : text;
    }
}

/// <summary>
/// The words so far of a sentence being said, by small models. A model made for whole sentences ends whatever it is
/// given, so each new translation carries on from the words the listener has already been shown as settled
/// (<c>begun</c>): those go into the model's answer as its start, and it writes only what follows. They can't change
/// on screen, and only the new words cost time. Through English, the English in the middle is held the same way: what
/// two readings in a row agree on stays as it is for the next.
/// </summary>
internal sealed class SmallSoFar
{
    private sealed class Step
    {
        public IReadOnlyList<long> Last = [];
        public string[] Words = [];
        public int Steady;
    }

    private Step[] _steps = [];
    private string _pair = "";

    public string Translate(string text, string from, string to, string begun, CancellationToken cancellationToken)
    {
        var route = SmallTranslation.Route(from, to);
        if (route.Count == 0)
            return "";
        string pair = from + ">" + to + ">" + route.Count;
        // Nothing settled: a new sentence, or one heard differently now. Nothing is kept from before.
        if (begun.Length == 0 || pair != _pair || _steps.Length != route.Count)
        {
            _steps = route.Select(_ => new Step()).ToArray();
            _pair = pair;
        }
        bool ended = Ends(text);
        string current = text.Trim();
        for (int i = 0; i < route.Count; i++)
        {
            var (model, step) = (route[i], _steps[i]);
            bool last = i == route.Count - 1;
            var start = last ? Settled(model, step.Last, begun, to) : step.Steady > 0 ? model.Start(step.Last, step.Steady) : null;
            var answer = model.Translate(current, 1, start, cancellationToken, most: current.Length / 2 + 24);
            var words = answer.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (last && start is null && begun.Length > 0)
            {
                // The settled words couldn't be given to the model as its start, so it wrote the sentence afresh.
                // What the listener was shown as settled stays all the same: the new words follow it.
                step.Words = words;
                step.Last = answer.Pieces;
                var settled = begun.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                var shown = Shown(answer.Text, to).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                return string.Join(' ', settled.Concat(shown.Skip(settled.Length)));
            }
            if (!last)
            {
                int same = 0;
                while (same < words.Length - 1 && same < step.Words.Length && words[same] == step.Words[same])
                    same++;
                step.Steady = same;
            }
            step.Words = words;
            step.Last = answer.Pieces;
            // The model put a full stop to words that aren't a sentence yet: the next one would take it for their end.
            current = last || ended ? answer.Text : LiveInterpreter.Unfinished(answer.Text);
        }
        return Shown(current, to);
    }

    /// <summary>
    /// How much less likely (in the mean log chance of a piece) the sentence carried on from the words on screen may be
    /// than the one written afresh and still be kept. Measured on the sixteen FLEURS sentences heard three words more
    /// at a time (SmallModelsBench), chrF and the share of the first line's words that change when it comes, written
    /// afresh / kept within 0.2: en→ar 62.3, 27 % / 62.7, 10 %; en→tr 68.4, 10 % / 68.3, 4 %; ar→en 70.9, 12 % /
    /// 71.5, 0 %; ar→tr 57.5, 15 % / 57.0, 13 %; tr→en 74.6, 18 % / 74.8, 16 %; tr→ar 58.2, 28 % / 58.0, 23 %. As right
    /// on average (65.4 against 65.3), with four words in ten fewer changing. Within 0.4 it was half a point worse.
    /// </summary>
    internal static double Margin { get; set; } = 0.2;

    /// <summary>
    /// A finished sentence, for the first line. It is written afresh, as a whole sentence translates best; and carried
    /// on from the words the second line showed for it (<paramref name="shown"/>), which the listener has been reading.
    /// The one carried on is kept unless the model finds it less likely by more than <see cref="Margin"/>: then the
    /// fresh one is clearly better and takes its place; otherwise the first line changes only where it must.
    /// Through English, both are weighed as translations of the English written afresh. Nothing here is kept for later.
    /// </summary>
    public string Final(string text, string from, string to, string shown, CancellationToken cancellationToken, double? margin = null)
    {
        text = text.Trim();
        if (text.Length == 0 || !text.Any(char.IsLetter))
            return text;
        var route = SmallTranslation.Route(from, to);
        if (route.Count == 0)
            throw new InvalidOperationException($"No small model translates {from} into {to}.");
        string middle = text;
        for (int i = 0; i < route.Count - 1; i++)
            middle = route[i].Translate(middle, 1, null, cancellationToken).Text;
        var model = route[^1];
        var fresh = model.Translate(middle, 1, null, cancellationToken);
        var chosen = fresh;
        string pair = from + ">" + to + ">" + route.Count;
        if (shown.Trim().Length > 0 && pair == _pair && _steps.Length == route.Count
            && Settled(model, _steps[^1].Last, shown, to) is { Count: > 0 } begun && !begun.SequenceEqual(fresh.Pieces.Take(begun.Count)))
        {
            var kept = model.Translate(middle, 1, begun, cancellationToken);
            double keptLikely = model.Likelihood(middle, kept.Pieces, cancellationToken);
            double freshLikely = model.Likelihood(middle, fresh.Pieces, cancellationToken);
            if (keptLikely >= freshLikely - (margin ?? Margin))
                chosen = kept;
        }
        return to == "ar" ? Translator.Tidy(chosen.Text) : chosen.Text;
    }

    /// <summary>
    /// The words as the listener is shown them. Arabic is tidied, and loses its vowel marks whatever their number:
    /// tidying takes them off only once there are three, which would change words already settled on screen when a
    /// sentence's third mark came.
    /// </summary>
    private static string Shown(string text, string to) =>
        to == "ar" ? string.Concat(Translator.Tidy(text).Where(c => c is not (>= (char)0x064C and <= (char)0x0650) and not (char)0x0652)) : text;

    private static bool Ends(string text) => text.TrimEnd() is { Length: > 0 } t && t[^1] is '.' or '?' or '!' or '؟' or '۔' or '。';

    /// <summary>
    /// The pieces of the last answer that spell the settled words; null when they aren't its start (they were tidied
    /// into something else, or are of another answer), and then the translation starts afresh.
    /// </summary>
    internal static IReadOnlyList<long>? Settled(PairModel model, IReadOnlyList<long> last, string begun, string to)
    {
        string wanted = Plain(begun);
        if (wanted.Length == 0 || last.Count == 0)
            return null;
        // The shortest start of the answer that, shown as the listener saw it, spells the settled words and stops
        // where a word ends. Counting words wouldn't do: tidying takes a stray word out now and then, and a number
        // may be written in pieces with a space of their own.
        for (int count = 1; count <= last.Count; count++)
        {
            // Cut where a word begins, or before a full stop or a comma the model joined to the word before: the words
            // shown for a sentence still being said have none at their end.
            if (count < last.Count && !model.Begins(last[count]) && !model.Mark(last[count]))
                continue;
            var spelt = model.Words(last.Take(count));
            var shown = Plain(Shown(spelt, to));
            if (shown == wanted)
                return last.Take(count).ToList();
            if (shown.Length > wanted.Length + 8)
                break;
        }
        return null;
    }

    /// <summary>
    /// Words as they are compared: one space between them, and without the vowel marks tidying may have taken off.
    /// </summary>
    private static string Plain(string words) =>
        string.Join(' ', string.Concat(words.Where(c => c is < (char)0x064B or > (char)0x0652)).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
