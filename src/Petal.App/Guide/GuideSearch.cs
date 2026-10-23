using System.Globalization;
using System.Text;
using Petal.App.Localization;

namespace Petal.App.Guide;

/// <summary>One entry found for a request, with how well it matched.</summary>
public sealed record GuideHit(string Key, double Score);

/// <summary>
/// Finds the tools and actions a request is about ("a countdown", "أريد أداة تقوم بمهمة العداد", "make a PDF
/// smaller"), on this PC and at once: the words of the request against each entry's name, keywords, summary,
/// how-to and examples, in the language Petal speaks and in English. Letters are folded so spellings meet
/// (Arabic hamzas, Persian ی and ک, digits), little words are left out, and a word matches the start of a
/// longer one ("countdown" finds "count down"; "العداد" finds "عداد").
/// </summary>
public static class GuideSearch
{
    /// <summary>The entries that match, best first; empty for a request with nothing to go on.</summary>
    public static IReadOnlyList<GuideHit> Find(string request, int most = 8)
    {
        var words = Words(request).Where(w => !Stop.Contains(w)).Distinct().ToList();
        if (words.Count == 0)
            return [];
        var folded = " " + Phrase(request) + " ";
        // The language Petal speaks and English; a request in Arabic letters reads the tables written in them too.
        var codes = new List<string> { L.Code, "en" };
        if (request.Any(c => c is >= (char)0x0600 and <= (char)0x06FF))
            codes.AddRange(["ar", "fa", "ur"]);
        codes = codes.Distinct().ToList();
        var hits = new List<GuideHit>();
        foreach (var key in GuideBook.Keys)
        {
            double score = Score(key, words, folded, codes);
            if (score > 0)
                hits.Add(new GuideHit(key, score));
        }
        return hits.OrderByDescending(h => h.Score).ThenBy(h => h.Key, StringComparer.Ordinal).Take(most).ToList();
    }

    private static double Score(string key, List<string> words, string folded, List<string> codes)
    {
        var fields = new List<(HashSet<string> Words, double Weight)>(Names(key));
        var phrases = new List<string>();
        foreach (var code in codes)
            if (Fields(code, key) is { } table)
            {
                fields.AddRange(table.Fields);
                phrases.AddRange(table.Phrases);
            }
        double score = 0;
        int matched = 0;
        foreach (var word in words)
        {
            double best = 0;
            foreach (var (set, weight) in fields)
            {
                double how = Match(word, set);
                if (how > 0)
                    best = Math.Max(best, weight * how);
            }
            if (best > 0)
                matched++;
            score += best;
        }
        if (matched == 0)
            return 0;
        // A keyword said whole ("count down", "make pdf smaller") is worth more than its words apart.
        foreach (var phrase in phrases.Distinct())
            if (folded.Contains(" " + phrase + " ", StringComparison.Ordinal))
                score += 4;
        // Requests that match in most of their words before those that match one word strongly.
        return score * (0.5 + 0.5 * matched / words.Count);
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(string Code, string Key), (List<(HashSet<string> Words, double Weight)> Fields, List<string> Phrases)?> Tables = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(string Code, string Key), List<(HashSet<string> Words, double Weight)>> NameWords = new();

    /// <summary>An entry's name in the language Petal speaks and in English, the words worth most.</summary>
    private static List<(HashSet<string> Words, double Weight)> Names(string key) => NameWords.GetOrAdd((L.Code, key), k =>
    [
        (Words(GuideBook.Name(k.Key)).ToHashSet(), 5),
        (Words(GuideBook.EnglishName(k.Key)).ToHashSet(), 5),
    ]);

    /// <summary>One table's words for an entry, by field with what a match in each is worth, and its keywords of several words; worked out once.</summary>
    private static (List<(HashSet<string> Words, double Weight)> Fields, List<string> Phrases)? Fields(string code, string key) => Tables.GetOrAdd((code, key), k =>
    {
        if (!GuideBook.Table(k.Code).TryGetValue(k.Key, out var e))
            return null;
        var fields = new List<(HashSet<string> Words, double Weight)>
        {
            (e.Keywords.SelectMany(Words).ToHashSet(), 4),
            (Words(e.Summary).ToHashSet(), 2),
            (Words(e.How + " " + e.WorksOn).ToHashSet(), 1),
            (Words(e.Details + " " + string.Join(' ', e.Examples)).ToHashSet(), 0.8),
        };
        var phrases = e.Keywords.Select(Phrase).Where(p => p.Contains(' ')).Distinct().ToList();
        return (fields, phrases);
    });

    /// <summary>
    /// 1 for the same word, 0.7 when one starts the other (both long enough to mean something), 0.6 when the request's
    /// word is the entry's with a letter or two in front: an Arabic verb's form ("احذف", "يحذف" for "حذف").
    /// </summary>
    private static double Match(string word, HashSet<string> set)
    {
        if (set.Contains(word))
            return 1;
        if (word.Length < 3)
            return 0;
        double best = 0;
        foreach (var w in set)
        {
            if (w.Length < 3)
                continue;
            if (w.StartsWith(word, StringComparison.Ordinal) || word.StartsWith(w, StringComparison.Ordinal) && w.Length >= 4)
                return 0.7;
            if (word.Length - w.Length is 1 or 2 && word.EndsWith(w, StringComparison.Ordinal) && IsArabicLetter(word[0]))
                best = 0.6;
        }
        return best;
    }

    private static bool IsArabicLetter(char c) => c is >= (char)0x0621 and <= (char)0x064A || c is >= (char)0x0671 and <= (char)0x06D3;

    /// <summary>The words of a text, folded, with Arabic's attached "ال", "و", "ب"… taken off long enough words.</summary>
    internal static IEnumerable<string> Words(string text)
    {
        var folded = Fold(text);
        var word = new StringBuilder();
        foreach (var c in folded + " ")
        {
            if (char.IsLetterOrDigit(c))
            {
                word.Append(c);
                continue;
            }
            if (word.Length > 0)
            {
                yield return Bare(word.ToString());
                word.Clear();
            }
        }
    }

    private static string Bare(string word)
    {
        if (word.Length >= 5 && word[0] is 'ا' or 'و' or 'ب' or 'ل' or 'ف' or 'ك')
        {
            foreach (var prefix in new[] { "وال", "بال", "فال", "كال", "لل", "ال" })
                if (word.StartsWith(prefix, StringComparison.Ordinal) && word.Length - prefix.Length >= 3)
                    return word[prefix.Length..];
        }
        return word;
    }

    /// <summary>Lower case, without diacritics, with the letters that are written several ways made one.</summary>
    internal static string Fold(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text.Normalize(NormalizationForm.FormD))
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(c);
            if (category == UnicodeCategory.NonSpacingMark || c is 'ـ' or (char)0x200C or (char)0x200D)
                continue;
            sb.Append(c switch
            {
                'أ' or 'إ' or 'آ' or 'ٱ' => 'ا',
                'ة' or 'ۀ' or 'ہ' or 'ھ' => 'ه',
                'ى' or 'ی' or 'ې' or 'ے' => 'ي',
                'ک' => 'ك',
                >= '٠' and <= '٩' => (char)('0' + (c - '٠')),
                >= '۰' and <= '۹' => (char)('0' + (c - '۰')),
                _ => char.ToLowerInvariant(c),
            });
        }
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    /// <summary>A text's words as a phrase, without the little words that change nothing ("what is in this picture" = "what is in a picture").</summary>
    private static string Phrase(string text) => string.Join(' ', Words(text).Where(w => !Fillers.Contains(w)));

    // "This", "the", "a" and their kind: left out when phrases are compared.
    private static readonly HashSet<string> Fillers = Build([.. Petal.Recipes.GuidePrompts.FillerWords]);

    // Little words in the languages Petal speaks, folded as above: they say nothing about the task.
    private static readonly HashSet<string> Stop = Build([.. Petal.Recipes.GuidePrompts.StopWords]);

    private static HashSet<string> Build(params string[] lists) =>
        lists.SelectMany(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Select(w => string.Concat(Words(w))).ToHashSet(StringComparer.Ordinal);
}
