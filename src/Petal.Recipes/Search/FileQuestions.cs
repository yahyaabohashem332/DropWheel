using System.Text;
using System.Text.RegularExpressions;

namespace Petal.Recipes.Search;

/// <summary>A passage of a file that may hold the answer, numbered as the answer cites it.</summary>
public sealed record FileSource(int Number, string Path, string Passage);

/// <summary>An answer from the files: the words (empty when the files don't say), and the passages it came from.</summary>
public sealed record FileAnswer(string Text, IReadOnlyList<FileSource> Sources, bool UsedModel)
{
    public bool Found => Text.Length > 0;
}

/// <summary>
/// Ask your files: a question ("when does the car insurance end?", "كم كانت فاتورة الكهرباء؟") answered from
/// what the indexed documents say. The search index finds the files that mention it, the passages that match
/// best are read again from the files themselves, and the language model on this PC answers from those
/// passages only, citing each one. Nothing leaves the PC.
/// </summary>
public static partial class FileQuestions
{
    /// <summary>The most text handed to the model; small models answer best from a little, well chosen.</summary>
    public const int MostContext = 3200;

    private const int PassageLength = 650;

    /// <summary>Whether what was typed reads as a question rather than search words.</summary>
    public static bool LooksLikeQuestion(string text)
    {
        var t = text.Trim();
        if (t.Length < 6 || !t.Contains(' '))
            return false;
        if (t.EndsWith('?') || t.EndsWith('؟'))
            return true;
        return QuestionStart().IsMatch(AskPetal.Fold(t));
    }

    /// <summary>The words of a question that say what it is about, without "what", "the", "هل", "في"…</summary>
    public static IReadOnlyList<string> Terms(string question)
    {
        var words = SearchIndex.Words(question).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var kept = words.Where(w => w.Length > 1 && !Stop.Contains(w)).Distinct().ToList();
        return kept.Count > 0 ? kept : words.Distinct().ToList();
    }

    /// <summary>
    /// The passages most likely to hold the answer: from the files that mention the question's words most,
    /// re-read (when <paramref name="readOriginal"/> can) so they keep their punctuation and letters.
    /// </summary>
    public static async Task<IReadOnlyList<FileSource>> FindAsync(string question, IReadOnlyList<(string Path, string Body)> candidates,
        Func<string, CancellationToken, Task<string?>> readOriginal, CancellationToken cancellationToken)
    {
        var terms = Terms(question);
        var scored = new List<(string Path, string Passage, double Score)>();
        foreach (var (path, body) in candidates.Take(5))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? text = null;
            try
            {
                text = await readOriginal(path, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Too slow to read again (a scan read with OCR): the words kept in the index will do.
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
            {
                // The words kept in the index will do.
            }
            foreach (var passage in Passages(string.IsNullOrWhiteSpace(text) ? body : text))
            {
                double score = Score(passage, terms);
                if (score > 0)
                    scored.Add((path, passage, score));
            }
        }
        var chosen = new List<FileSource>();
        int length = 0;
        // At most two passages from one file, so one long document doesn't crowd out the rest.
        foreach (var (path, passage, _) in scored.OrderByDescending(s => s.Score))
        {
            if (chosen.Count(c => c.Path == path) >= 2 || length + passage.Length > MostContext)
                continue;
            chosen.Add(new FileSource(chosen.Count + 1, path, passage));
            length += passage.Length;
            if (chosen.Count == 6)
                break;
        }
        return chosen;
    }

    /// <summary>The text cut into passages of about <see cref="PassageLength"/> letters, at paragraph or sentence ends.</summary>
    internal static List<string> Passages(string text)
    {
        var passages = new List<string>();
        var current = new StringBuilder();
        foreach (var piece in Sentences().Split(text.Replace("\r\n", "\n")))
        {
            var clean = Space().Replace(piece, " ").Trim();
            if (clean.Length == 0)
                continue;
            if (current.Length > 0 && current.Length + clean.Length > PassageLength)
            {
                passages.Add(current.ToString());
                current.Clear();
            }
            if (current.Length > 0)
                current.Append(' ');
            current.Append(clean.Length > PassageLength * 2 ? clean[..(PassageLength * 2)] : clean);
        }
        if (current.Length > 0)
            passages.Add(current.ToString());
        return passages;
    }

    /// <summary>How well a passage matches: each question word found counts, more for rarer (longer) words and for finding several.</summary>
    internal static double Score(string passage, IReadOnlyList<string> terms)
    {
        var words = SearchIndex.Words(passage).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int found = 0;
        double score = 0;
        foreach (var term in terms)
        {
            int hits = words.Count(w => w.StartsWith(term, StringComparison.Ordinal) || (term.Length > 4 && w.StartsWith(term[..^1], StringComparison.Ordinal)));
            if (hits == 0)
                continue;
            found++;
            score += Math.Min(3, hits) * Math.Min(8, term.Length);
        }
        return found == 0 ? 0 : score * found;
    }

    /// <summary>
    /// The answer from the passages with the model on this PC, or, without the model, no words (the passages
    /// themselves are then the answer to read).
    /// </summary>
    public static async Task<FileAnswer> AnswerAsync(string question, IReadOnlyList<FileSource> sources, CancellationToken cancellationToken)
    {
        if (sources.Count == 0 || !AskPetal.ModelReady)
            return new FileAnswer("", sources, false);
        var context = new StringBuilder();
        foreach (var source in sources)
            context.Append('[').Append(source.Number).Append("] (").Append(System.IO.Path.GetFileName(source.Path)).Append(")\n").Append(source.Passage).Append("\n\n");
        context.Append("Question: ").Append(question.Trim());
        bool arabic = question.Any(c => c is >= '؀' and <= 'ۿ');
        var instruction = "You answer questions about the user's own files. The user gives numbered passages from the files, then a question. " +
            "Reply with a complete sentence that answers the question with the facts from the passages (dates, amounts, names), " +
            "followed by the number of the passage it came from in square brackets. Example: The rent is 500 dollars a month [2]. " +
            "Write " + (arabic ? "in Arabic" : "in the language of the question") + ", at most three sentences. " +
            "If the passages don't contain the answer, reply with exactly: NOT FOUND";
        var answer = await AskPetal.WriteAsync(instruction, context.ToString(), 220, cancellationToken).ConfigureAwait(false);
        answer = answer.Trim();
        if (answer.Length == 0 || answer.Contains("NOT FOUND", StringComparison.OrdinalIgnoreCase))
            return new FileAnswer("", sources, true);
        // Only citations of passages that exist.
        answer = Citation().Replace(answer, m => int.TryParse(m.Groups[1].Value, out int n) && n >= 1 && n <= sources.Count ? m.Value : "");
        return new FileAnswer(answer.Trim(), sources, true);
    }

    /// <summary>The numbers an answer cites, in order, each once.</summary>
    public static IReadOnlyList<int> Cited(string answer) =>
        Citation().Matches(answer).Select(m => int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)).Distinct().ToList();

    private static readonly HashSet<string> Stop =
    [
        "what", "when", "where", "which", "who", "whom", "whose", "why", "how", "much", "many", "is", "are", "was", "were", "the", "a", "an",
        "of", "in", "on", "at", "to", "for", "from", "by", "with", "and", "or", "do", "does", "did", "my", "me", "i", "it", "its", "this", "that",
        "there", "be", "been", "can", "could", "should", "would", "will", "about", "have", "has", "had", "you", "your", "our", "we", "any",
        "ما", "ماذا", "متي", "اين", "كيف", "كم", "من", "هل", "لماذا", "اي", "في", "علي", "الي", "عن", "مع", "هو", "هي", "كان", "كانت",
        "يكون", "تكون", "ان", "او", "ثم", "هذا", "هذه", "ذلك", "تلك", "التي", "الذي", "لي", "عندي", "لدي",
    ];

    [GeneratedRegex(@"^(what|when|where|which|who|whose|why|how|is|are|was|were|do|does|did|can|could|should|ما|ماذا|متي|اين|كيف|كم|من|هل|لماذا|اي)\s")]
    private static partial Regex QuestionStart();

    [GeneratedRegex(@"(?<=[.!?؟。\n])\s+|\n{2,}")]
    private static partial Regex Sentences();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Space();

    [GeneratedRegex(@"\[(\d{1,2})\]")]
    private static partial Regex Citation();
}
