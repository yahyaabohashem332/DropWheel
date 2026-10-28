namespace Petal.Recipes.Questions;

/// <summary>
/// Passages found by their words (BM25), alongside <see cref="PassageModel"/>'s meanings: a name, a number or a rare term
/// is found by its word even where the meaning is unsure. Words are compared folded (Arabic hamzas, ta marbuta, vowel
/// marks; case) and lightly stemmed ("الكتاب" and "كتابها" are one word, as are "ticket" and "tickets"), the little
/// words of English and Arabic left out.
/// </summary>
internal sealed class WordIndex
{
    private const double K1 = 1.2, B = 0.75;

    private readonly Dictionary<string, List<(int Passage, int Count)>> _postings = new(StringComparer.Ordinal);
    private readonly int[] _lengths;
    private readonly double _average;

    public WordIndex(IReadOnlyList<string> passages)
    {
        _lengths = new int[passages.Count];
        for (int i = 0; i < passages.Count; i++)
        {
            var words = Terms(passages[i]).ToList();
            _lengths[i] = words.Count;
            foreach (var group in words.GroupBy(w => w, StringComparer.Ordinal))
            {
                if (!_postings.TryGetValue(group.Key, out var list))
                    _postings[group.Key] = list = [];
                list.Add((i, group.Count()));
            }
        }
        _average = _lengths.Length == 0 ? 1 : Math.Max(1, _lengths.Average());
    }

    /// <summary>Each passage's score for the words of <paramref name="question"/> (0 for none of them).</summary>
    public double[] Scores(string question)
    {
        var scores = new double[_lengths.Length];
        int n = _lengths.Length;
        foreach (var term in Terms(question).Distinct(StringComparer.Ordinal))
        {
            if (!_postings.TryGetValue(term, out var list))
                continue;
            double idf = Math.Log(1 + (n - list.Count + 0.5) / (list.Count + 0.5));
            foreach (var (passage, count) in list)
                scores[passage] += idf * count * (K1 + 1) / (count + K1 * (1 - B + B * _lengths[passage] / _average));
        }
        return scores;
    }

    /// <summary>A text's words as compared: folded, stemmed, the little ones left out.</summary>
    internal static IEnumerable<string> Terms(string text)
    {
        var folded = AskPetal.Fold(text);
        int start = -1;
        for (int i = 0; i <= folded.Length; i++)
        {
            bool letter = i < folded.Length && char.IsLetterOrDigit(folded[i]);
            if (letter && start < 0)
                start = i;
            else if (!letter && start >= 0)
            {
                var word = folded[start..i];
                start = -1;
                if (Little.Contains(word))
                    continue;
                word = Stem(word);
                if (word.Length > 1)
                    yield return word;
            }
        }
    }

    private static string Stem(string word)
    {
        if (word.Any(c => c is >= (char)0x0600 and <= (char)0x06FF))
        {
            foreach (var prefix in ArabicPrefixes)
            {
                if (word.Length - prefix.Length >= 3 && word.StartsWith(prefix, StringComparison.Ordinal))
                {
                    word = word[prefix.Length..];
                    break;
                }
            }
            foreach (var suffix in ArabicSuffixes)
            {
                if (word.Length - suffix.Length >= 3 && word.EndsWith(suffix, StringComparison.Ordinal))
                {
                    word = word[..^suffix.Length];
                    break;
                }
            }
            return word;
        }
        if (word.Length > 4 && word.EndsWith("ies", StringComparison.Ordinal))
            return word[..^3] + "y";
        if (word.Length > 5 && word.EndsWith("ing", StringComparison.Ordinal))
            return word[..^3];
        if (word.Length > 4 && word.EndsWith("ed", StringComparison.Ordinal))
            return word[..^2];
        if (word.Length > 3 && word.EndsWith('s') && !word.EndsWith("ss", StringComparison.Ordinal) && !word.EndsWith("us", StringComparison.Ordinal) && !word.EndsWith("is", StringComparison.Ordinal))
            return word[..^1];
        return word;
    }

    // Folded spellings (ة as ه, أ إ آ as ا), longest first.
    private static readonly string[] ArabicPrefixes = ["وال", "بال", "كال", "فال", "لل", "ال", "و", "ف", "ب"];
    private static readonly string[] ArabicSuffixes = ["هما", "كما", "ات", "ان", "ون", "ين", "ها", "هم", "كم", "نا", "يه", "ه", "ي"];

    private static readonly HashSet<string> Little = new(StringComparer.Ordinal)
    {
        "the", "a", "an", "of", "to", "in", "on", "at", "for", "and", "or", "is", "are", "was", "were", "be", "been", "it", "its", "this", "that", "these",
        "those", "with", "as", "by", "from", "do", "does", "did", "what", "which", "who", "whom", "how", "when", "where", "why", "there", "their", "they",
        "we", "you", "your", "i", "me", "my", "he", "she", "his", "her", "about", "can", "could", "would", "should", "will", "not", "no", "yes", "if",
        "so", "than", "then", "into", "any", "all", "some", "say", "says", "said", "tell", "file", "document",
        "في", "من", "علي", "الي", "عن", "ما", "ماذا", "هل", "هو", "هي", "ان", "او", "ثم", "كيف", "متي", "اين", "لماذا", "هذا", "هذه", "ذلك", "تلك", "التي",
        "الذي", "الذين", "كان", "كانت", "يكون", "مع", "كل", "بعض", "قد", "لا", "لم", "لن", "انا", "انت", "نحن", "هم", "يقول", "قال", "الملف",
    };
}
