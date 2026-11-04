using System.IO;
using System.Text;
using System.Text.Json;
using Petal.Core.Models;
using Petal.Core.Text;
using Petal.Engines.Documents;
using Petal.Recipes.Questions;
using Petal.Recipes.Translation;

namespace Petal.Recipes.Study;

/// <summary>A card to learn from: what it asks, what it answers, and where in the file that is.</summary>
public sealed record StudyCard(string Front, string Back, string Where);

/// <summary>A question with its choices, which one is right and why.</summary>
public sealed record StudyQuestion(string Question, IReadOnlyList<string> Options, int Correct, string Why, string Where);

/// <summary>What a lecture or a document comes to.</summary>
public sealed record StudyPack(string Summary, IReadOnlyList<StudyCard> Cards, IReadOnlyList<StudyQuestion> Quiz);

/// <summary>How much to make from each stretch of the file.</summary>
public enum StudyAmount
{
    Fewer,
    Normal,
    More,
}

/// <summary>
/// Study notes from a lecture or a document: a summary, cards and a quiz, written from the file's own words and each
/// saying where in the file it comes from. A long file is read a stretch at a time, as the model holds only a few pages.
/// </summary>
public static class StudyMaker
{
    /// <summary>The most characters of a file given to the model at once, with room for what it writes.</summary>
    private const int Stretch = 3500;

    /// <summary>What it needs on this PC: the program and the model here already, or else the one that does well on any PC.</summary>
    public static IReadOnlyList<ModelInfo> Models =>
        [ModelCatalog.LlamaCpp, new[] { ModelCatalog.GemmaTranslator, ModelCatalog.GemmaQuick }.FirstOrDefault(ModelStore.IsAvailable) ?? ModelCatalog.GemmaQuick];

    public static bool Ready => Cloud.CloudAi.CanWrite || Models.All(ModelStore.IsAvailable);

    /// <summary>Stretches of the text, cut between passages, each knowing the first and last place it holds.</summary>
    internal static List<(string Text, PlacedText First, PlacedText Last)> Stretches(IReadOnlyList<PlacedText> places, int most = Stretch)
    {
        var stretches = new List<(string Text, PlacedText First, PlacedText Last)>();
        var text = new StringBuilder();
        PlacedText? first = null, last = null;
        void Close()
        {
            if (first is not null && text.Length > 0)
                stretches.Add((text.ToString().Trim(), first, last!));
            text.Clear();
            first = null;
        }
        foreach (var place in places)
        {
            var words = place.Text.Trim();
            if (words.Length == 0)
                continue;
            // A place longer than a stretch is cut, at a sentence where it can be.
            foreach (var piece in GemmaTranslation.Parts(words, most))
            {
                if (text.Length > 0 && text.Length + piece.Length + 1 > most)
                    Close();
                first ??= place;
                last = place;
                text.Append(piece).Append('\n');
            }
        }
        Close();
        return stretches;
    }

    private static string Writing(string into) => Translator.EnglishName(into);

    private static (int Cards, int Questions) PerStretch(StudyAmount amount) => amount switch
    {
        StudyAmount.Fewer => (3, 1),
        StudyAmount.More => (8, 4),
        _ => (5, 2),
    };

    internal static string Instruction(string into, StudyAmount amount)
    {
        var (cards, questions) = PerStretch(amount);
        return "You make study material from the user's text, for a student preparing for an exam. Use only what the text says: invent nothing, and add no facts of your own. "
            + $"Write everything in {Writing(into)}. Reply with one JSON object and nothing else, with these keys: "
            + "\"notes\": a list of the main points, in short sentences; "
            + $"\"cards\": a list of up to {cards} objects with \"q\" (a question or a term worth remembering) and \"a\" (its short answer); "
            + $"\"quiz\": a list of up to {questions} objects with \"q\" (a question), \"options\" (exactly four different answers, only one of them right, the others believable), \"answer\" (the number of the right one, counting from 0) and \"why\" (one sentence saying why, from the text). "
            + "Ask about what matters, not about small details, and never ask about the text itself. If the text has nothing to learn from, reply with empty lists.";
    }

    private static string SummaryInstruction(string into) =>
        $"Below are notes from a lecture or a document, in order. Write them up as study notes in {Writing(into)}: a short overview first, then the main points under short headings starting with \"## \", as bullet points starting with \"- \". Keep to what the notes say. Reply with the study notes only.";

    private static async Task<string> WriteAsync(string instruction, string text, int most, CancellationToken cancellationToken)
    {
        if (Cloud.CloudAi.CanWrite)
            return await AskPetal.WriteAsync(instruction, text, most, cancellationToken, 0.2).ConfigureAwait(false);
        var model = Models[1];
        var clean = text.Replace("<|", "< |").Replace("|>", "| >").Trim();
        var prompt = $"<|turn>system\n{instruction}<turn|>\n<|turn>user\n{clean}<turn|>\n<|turn>model\n";
        return GemmaTranslation.Reply(await GemmaServer.CompleteAsync(model, prompt, most, 0.2, 1.05, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>What one stretch of the file came to; null when the reply couldn't be read.</summary>
    internal static (List<string> Notes, List<StudyCard> Cards, List<StudyQuestion> Quiz)? Parse(string reply, string where)
    {
        int open = reply.IndexOf('{'), close = reply.LastIndexOf('}');
        if (open < 0 || close <= open)
            return null;
        try
        {
            using var json = JsonDocument.Parse(reply[open..(close + 1)], new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            var root = json.RootElement;
            var notes = new List<string>();
            var cards = new List<StudyCard>();
            var quiz = new List<StudyQuestion>();
            if (root.TryGetProperty("notes", out var n) && n.ValueKind == JsonValueKind.Array)
                notes.AddRange(n.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!.Trim()).Where(s => s.Length > 0));
            if (root.TryGetProperty("cards", out var c) && c.ValueKind == JsonValueKind.Array)
                foreach (var card in c.EnumerateArray())
                    if (Str(card, "q") is { Length: > 0 } q && Str(card, "a") is { Length: > 0 } a)
                        cards.Add(new StudyCard(q, a, where));
            if (root.TryGetProperty("quiz", out var z) && z.ValueKind == JsonValueKind.Array)
                foreach (var item in z.EnumerateArray())
                    if (Question(item, where) is { } question)
                        quiz.Add(question);
            return (notes, cards, quiz);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()!.Trim() : "";

    /// <summary>A question only when it is one: four different answers and a right one among them.</summary>
    private static StudyQuestion? Question(JsonElement e, string where)
    {
        var q = Str(e, "q");
        if (q.Length == 0 || !e.TryGetProperty("options", out var o) || o.ValueKind != JsonValueKind.Array)
            return null;
        var options = o.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!.Trim()).ToList();
        if (options.Count != 4 || options.Any(s => s.Length == 0) || options.Distinct(StringComparer.OrdinalIgnoreCase).Count() != 4)
            return null;
        if (!e.TryGetProperty("answer", out var a))
            return null;
        int correct = a.ValueKind == JsonValueKind.Number && a.TryGetInt32(out var number) ? number
            : a.ValueKind == JsonValueKind.String ? options.FindIndex(s => string.Equals(s, a.GetString()!.Trim(), StringComparison.OrdinalIgnoreCase)) : -1;
        return correct is < 0 or > 3 ? null : new StudyQuestion(q, options, correct, Str(e, "why"), where);
    }

    /// <summary>The same thing asked twice in different stretches is one card.</summary>
    internal static List<T> Distinct<T>(IEnumerable<T> items, Func<T, string> key)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var kept = new List<T>();
        foreach (var item in items)
            if (seen.Add(new string(key(item).Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray())))
                kept.Add(item);
        return kept;
    }

    /// <summary>The language a file's words are in, for writing back in it.</summary>
    public static string LanguageOf(IReadOnlyList<PlacedText> places) =>
        LanguageGuess.Of(string.Join(' ', places.Take(30).Select(p => p.Text))) is { } code && Translator.Languages.Any(l => l.Code == code) ? code : "en";

    /// <summary>Makes the pack. <paramref name="placeName"/> says a place in the interface's language ("Page 3").</summary>
    public static async Task<StudyPack> MakeAsync(IReadOnlyList<PlacedText> places, string into, StudyAmount amount, Func<PlacedText, PlacedText, string> placeName, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var stretches = Stretches(places);
        if (stretches.Count == 0)
            throw new InvalidOperationException("There are no words to read in this file.");
        var instruction = Instruction(into, amount);
        var notes = new List<string>();
        var cards = new List<StudyCard>();
        var quiz = new List<StudyQuestion>();
        try
        {
            for (int i = 0; i < stretches.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (text, first, last) = stretches[i];
                var where = placeName(first, last);
                var made = Parse(await WriteAsync(instruction, text, 1100, cancellationToken).ConfigureAwait(false), where)
                    // Once more, when it didn't come back as asked.
                    ?? Parse(await WriteAsync(instruction, text, 1100, cancellationToken).ConfigureAwait(false), where);
                if (made is var (n, c, q))
                {
                    notes.AddRange(n);
                    cards.AddRange(c);
                    quiz.AddRange(q);
                }
                progress?.Report((i + 1.0) / (stretches.Count + 1));
            }
            if (notes.Count == 0 && cards.Count == 0 && quiz.Count == 0)
                throw new InvalidOperationException("The model didn't find anything to make notes from in this file.");
            var material = string.Join('\n', notes.Select(n => "- " + n));
            if (material.Length > Stretch * 2)
                material = material[..(Stretch * 2)];
            var summary = await WriteAsync(SummaryInstruction(into), material, 1300, cancellationToken).ConfigureAwait(false);
            progress?.Report(1);
            return new StudyPack(summary.Trim(), Distinct(cards, c => c.Front), Distinct(quiz, q => q.Question));
        }
        finally
        {
            GemmaTranslation.Finished();
        }
    }

    /// <summary>The notes as a text file: the summary, the cards, then the quiz with its answers at the end of each question.</summary>
    public static string Markdown(string title, StudyPack pack, string cardsHeading, string quizHeading, string answerWord)
    {
        var sb = new StringBuilder();
        sb.Append("# ").AppendLine(title).AppendLine();
        sb.AppendLine(pack.Summary).AppendLine();
        if (pack.Cards.Count > 0)
        {
            sb.Append("## ").AppendLine(cardsHeading).AppendLine();
            foreach (var c in pack.Cards)
                sb.Append("- **").Append(c.Front).Append("** ").Append(c.Back).Append(" (").Append(c.Where).AppendLine(")");
            sb.AppendLine();
        }
        if (pack.Quiz.Count > 0)
        {
            sb.Append("## ").AppendLine(quizHeading).AppendLine();
            int number = 1;
            foreach (var q in pack.Quiz)
            {
                sb.Append(number++).Append(". ").AppendLine(q.Question);
                for (int i = 0; i < q.Options.Count; i++)
                    sb.Append("   - ").Append((char)('A' + i)).Append(") ").AppendLine(q.Options[i]);
                sb.Append("   - **").Append(answerWord).Append("** ").Append((char)('A' + q.Correct)).Append(") ").Append(q.Options[q.Correct]);
                if (q.Why.Length > 0)
                    sb.Append(": ").Append(q.Why);
                sb.Append(" (").Append(q.Where).AppendLine(")").AppendLine();
            }
        }
        return sb.ToString();
    }
}
