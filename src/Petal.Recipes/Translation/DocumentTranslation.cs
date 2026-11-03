using System.Text.RegularExpressions;
using Petal.Engines.Documents.Translate;

namespace Petal.Recipes.Translation;

/// <summary>
/// A document translated with <see cref="Translator"/>: on this PC, or by the cloud model chosen. The model is given
/// sentences, which is what it was made for and all it can read without cutting them off, each only once however often it
/// is repeated (a heading on every page, the cells of a column).
/// </summary>
public static partial class DocumentTranslation
{
    /// <summary>The model reads about 250 pieces of a word; this many letters are comfortably fewer.</summary>
    private const int LongestPiece = 400;

    /// <inheritdoc cref="DocumentTranslator.TranslateAsync"/>
    public static Task TranslateAsync(string input, string output, string from, string to, IProgress<double>? progress = null, CancellationToken cancellationToken = default) =>
        DocumentTranslator.TranslateAsync(input, output, from, to,
            (lines, p, ct) => BySentenceAsync(lines, (pieces, pp, c) => Translator.TranslateAsync(pieces, from, to, pp, c), p, ct),
            progress, cancellationToken);

    /// <summary>
    /// Each line translated a sentence at a time and put together again, its line breaks and tabs kept. Pieces that are
    /// the same are translated once.
    /// </summary>
    internal static async Task<IReadOnlyList<string>> BySentenceAsync(IReadOnlyList<string> lines,
        Func<IReadOnlyList<string>, IProgress<double>?, CancellationToken, Task<IReadOnlyList<string>>> translate,
        IProgress<double>? progress, CancellationToken cancellationToken)
    {
        // Odd places hold what parts a line's stretches (a line break, a tab); even places the stretches, each as its pieces.
        var cut = lines.Select(line => Parted().Split(line).Select((part, i) => (Part: part, Pieces: i % 2 == 0 ? Pieces(part) : [])).ToList()).ToList();
        var unique = cut.SelectMany(l => l).SelectMany(p => p.Pieces).Where(p => p.Any(char.IsLetter)).Distinct(StringComparer.Ordinal).ToList();
        var translated = await translate(unique, progress, cancellationToken).ConfigureAwait(false);
        if (translated.Count != unique.Count)
            throw new InvalidOperationException("The translation came back incomplete.");
        var done = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < unique.Count; i++)
            done[unique[i]] = translated[i];
        string One(string piece) => done.TryGetValue(piece, out var words) && words.Trim().Length > 0 ? words.Trim() : piece;
        return cut.Select(line => string.Concat(line.Select(p => p.Pieces.Count == 0 ? p.Part : string.Join(' ', p.Pieces.Select(One))))).ToList();
    }

    /// <summary>A stretch of text in pieces the model can read: its sentences, and a sentence too long at its clauses and then its words.</summary>
    internal static List<string> Pieces(string text)
    {
        var pieces = new List<string>();
        foreach (var sentence in Sentence().Split(text.Trim()).Where(s => s.Length > 0))
        {
            if (sentence.Length <= LongestPiece)
            {
                pieces.Add(sentence);
                continue;
            }
            foreach (var clause in Clause().Split(sentence).Where(s => s.Length > 0))
                AddWords(clause, pieces);
        }
        return pieces;
    }

    private static void AddWords(string clause, List<string> pieces)
    {
        var current = "";
        foreach (var word in clause.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (current.Length > 0 && current.Length + 1 + word.Length > LongestPiece)
            {
                pieces.Add(current);
                current = word;
            }
            else
                current = current.Length == 0 ? word : current + " " + word;
        }
        if (current.Length > 0)
            pieces.Add(current);
    }

    [GeneratedRegex(@"(\s*[\n\t]\s*)")]
    private static partial Regex Parted();

    [GeneratedRegex(@"(?<=[.!?؟۔。])\s+")]
    private static partial Regex Sentence();

    [GeneratedRegex(@"(?<=[,;:،؛])\s+")]
    private static partial Regex Clause();
}
