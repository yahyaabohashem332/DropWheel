using System.Text;
using System.Text.RegularExpressions;
using Petal.App.Localization;
using Petal.Recipes;

namespace Petal.App.Guide;

/// <summary>The tools the AI model chose for a request (from the notes it was given), and how to use the first.</summary>
/// <param name="Text">How to use the first chosen tool for the request, or the answer to a question about one.</param>
/// <param name="Notes">The entries the model was given, in order.</param>
/// <param name="Cited">The entries it chose, best first.</param>
/// <param name="Found">False when none of the notes fits the request (or the model wasn't asked).</param>
public sealed record GuideReply(string Text, IReadOnlyList<string> Notes, IReadOnlyList<string> Cited, bool Found);

/// <summary>
/// Answers about Petal's tools with the AI model on this PC (Qwen2.5 1.5B), from the guide's own entries only. A
/// model this small is good at a small, checkable task, so the work is split: first it is shown the entries the
/// search found, as numbered notes, and replies with the numbers of those that do what was asked (anything else is
/// left out); then, given only the first one's note, it says in a few sentences how to use it for the request.
/// What a tool is and does is shown from the guide itself, never from what the model writes. Greedy decoding gives
/// the same answer every time.
/// </summary>
public static partial class GuideAnswer
{
    /// <summary>The most notes handed to the model: enough to choose from, short enough for its context.</summary>
    public const int MostNotes = 6;

    /// <summary>
    /// Which tools fit what the user wants to do (best first), and how to use the first for it. The search's own
    /// order leads; the model's choice lifts a tool a little, enough to settle close calls but not to overturn a
    /// clear match.
    /// </summary>
    public static async Task<GuideReply> FindAsync(string request, IReadOnlyList<GuideHit> hits, CancellationToken cancellationToken)
    {
        var top = hits.Where(h => GuideBook.Get(h.Key) is not null).DistinctBy(h => h.Key).Take(MostNotes).ToList();
        var notes = top.Select(h => h.Key).ToList();
        if (notes.Count == 0 || !AskPetal.ModelReady)
            return new GuideReply("", notes, [], false);
        var choices = new StringBuilder();
        for (int i = 0; i < notes.Count; i++)
            choices.Append(Brief(i + 1, notes[i])).AppendLine();
        choices.Append(GuidePrompts.RequestLabel).Append(request.Trim());
        var picked = await AskPetal.WriteAsync(GuidePrompts.Choose, choices.ToString(), 16, cancellationToken, temperature: 0).ConfigureAwait(false);
        var chosen = Numbers(picked, notes);
        double best = top[0].Score;
        double[] lift = [0.15, 0.1, 0.05];
        // Only a tool the search already found close gains from being chosen.
        var ranked = top.Select(h => (h.Key, Score: h.Score / best + (chosen.IndexOf(h.Key) is var at and >= 0 && h.Score / best >= 0.5 ? lift[at] : 0)))
            .OrderByDescending(r => r.Score).ToList();
        // The best, and the next ones when they come close.
        var named = ranked.Take(1).Concat(ranked.Skip(1).Where(r => r.Score >= 0.7).Take(2)).Select(r => r.Key).ToList();
        var tip = await TipAsync(named[0], request, GuidePrompts.Tip, cancellationToken).ConfigureAwait(false);
        return new GuideReply(tip, notes, named, true);
    }

    /// <summary>A question about one entry, answered from its note alone.</summary>
    public static async Task<GuideReply> AboutAsync(string key, string question, CancellationToken cancellationToken)
    {
        if (GuideBook.Get(key) is null || !AskPetal.ModelReady)
            return new GuideReply("", [key], [], false);
        var answer = await TipAsync(key, question, GuidePrompts.About, cancellationToken).ConfigureAwait(false);
        return new GuideReply(answer, [key], answer.Length > 0 ? [key] : [], answer.Length > 0);
    }

    private static async Task<string> TipAsync(string key, string request, string instruction, CancellationToken cancellationToken)
    {
        var context = Notes(key) + Environment.NewLine + Environment.NewLine + GuidePrompts.RequestLabel + request.Trim();
        var told = string.Format(System.Globalization.CultureInfo.InvariantCulture, instruction, ReplyLanguage(request));
        var answer = (await AskPetal.WriteAsync(told, context, 200, cancellationToken, temperature: 0).ConfigureAwait(false)).Trim();
        // The note's number means nothing to the reader.
        return Citation().Replace(answer, "").Replace("  ", " ").Trim();
    }

    /// <summary>
    /// The language to answer in, by name: the one Petal speaks for a request in its script; Arabic for a request in
    /// Arabic letters when Petal speaks a language in Latin ones; English for one in Latin letters when it speaks one in Arabic.
    /// </summary>
    internal static string ReplyLanguage(string request)
    {
        bool arabicScript = request.Any(c => c is >= (char)0x0600 and <= (char)0x06FF);
        return arabicScript == L.IsRightToLeft ? L.EnglishName : arabicScript ? "Arabic" : "English";
    }

    /// <summary>The notes the model chose, by the numbers in its reply (those that were given, each once, at most three).</summary>
    internal static List<string> Numbers(string reply, IReadOnlyList<string> notes)
    {
        var chosen = new List<string>();
        foreach (Match m in Regex.Matches(reply, @"\d+"))
            if (int.TryParse(m.Value, out int n) && n >= 1 && n <= notes.Count && !chosen.Contains(notes[n - 1]) && chosen.Count < 3)
                chosen.Add(notes[n - 1]);
        return chosen;
    }

    /// <summary>An entry in a line, for choosing, in English (which the model reads best): number, name, what it does and takes.</summary>
    internal static string Brief(int number, string key)
    {
        var e = GuideBook.English(key) ?? GuideBook.Get(key)!;
        var name = GuideBook.EnglishName(key) is { Length: > 0 } english ? english : GuideBook.Name(key);
        return $"[{number}] {name}: {e.Summary}" + (e.WorksOn.Length > 0 ? $" ({e.WorksOn})" : "");
    }

    /// <summary>
    /// One entry for the model: in English, which it reads best, then in the language Petal speaks, so its answer
    /// can use the names and buttons as the reader sees them.
    /// </summary>
    internal static string Notes(string key)
    {
        var english = GuideBook.English(key);
        var shown = GuideBook.Get(key)!;
        if (english is null || ReferenceEquals(english, shown) || L.Code == "en")
            return Note(GuideBook.Name(key), shown);
        return Note(GuideBook.EnglishName(key) is { Length: > 0 } name ? name : GuideBook.Name(key), english)
            + Environment.NewLine + Environment.NewLine + string.Format(System.Globalization.CultureInfo.InvariantCulture, GuidePrompts.SameNoteIn, L.EnglishName) + Environment.NewLine + Note(GuideBook.Name(key), shown);
    }

    private static string Note(string name, GuideEntry e)
    {
        var note = new StringBuilder();
        note.Append(name).Append(": ").Append(e.Summary).AppendLine();
        if (e.WorksOn.Length > 0)
            note.AppendLine(e.WorksOn);
        note.AppendLine(e.How).AppendLine(e.Details);
        foreach (var example in e.Examples)
            note.Append("- ").AppendLine(example);
        return note.ToString().TrimEnd();
    }

    [GeneratedRegex(@"\[(\d{1,2})\]")]
    private static partial Regex Citation();
}
