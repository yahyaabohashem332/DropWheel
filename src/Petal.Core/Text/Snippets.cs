using System.Globalization;

namespace Petal.Core.Text;

/// <summary>A short text that grows into a long one as you type it: ";addr" becomes your address.</summary>
public sealed class Snippet
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..10];
    public string Abbreviation { get; set; } = "";
    public string Text { get; set; } = "";
}

/// <summary>What was typed matched a snippet: which one, and how many typed characters it takes back.</summary>
public sealed record SnippetMatch(Snippet Snippet, int Typed);

/// <summary>A snippet's text made ready: the words to type, and how far back the cursor goes after them.</summary>
public sealed record SnippetText(string Text, int CursorBack);

/// <summary>
/// Matching typed text to snippets, and filling in their placeholders. An abbreviation matches only as a
/// word of its own, and also when it was typed with the other keyboard language on (";شييق" is ";addr"
/// typed in Arabic), so switching languages first is never needed.
/// </summary>
public static class Snippets
{
    /// <summary>The snippet the typed text now ends with, if any (the longest wins).</summary>
    public static SnippetMatch? Match(string typed, IReadOnlyList<Snippet> snippets)
    {
        SnippetMatch? best = null;
        foreach (var snippet in snippets)
        {
            var abbreviation = snippet.Abbreviation;
            if (abbreviation.Length < 2)
                continue;
            foreach (var form in new[] { abbreviation, KeyboardLayout.ToArabic(abbreviation), KeyboardLayout.ToEnglish(abbreviation) }.Distinct())
            {
                if (form.Length == 0 || !typed.EndsWith(form, StringComparison.Ordinal))
                    continue;
                // A word of its own: nothing but a space or punctuation right before it.
                int before = typed.Length - form.Length - 1;
                if (before >= 0 && char.IsLetterOrDigit(typed[before]))
                    continue;
                if (best is null || form.Length > best.Typed)
                    best = new SnippetMatch(snippet, form.Length);
            }
        }
        return best;
    }

    /// <summary>
    /// The text with its placeholders filled in: {date}, {time}, {day}, {hijri} (the Hijri date in Arabic),
    /// {clipboard}, and {cursor} for where the cursor should end up.
    /// </summary>
    public static SnippetText Fill(string template, DateTime now, Func<string>? clipboard = null)
    {
        var ar = CultureInfo.GetCultureInfo("ar-SA");
        var hijri = (CultureInfo)ar.Clone();
        hijri.DateTimeFormat.Calendar = new UmAlQuraCalendar();
        var text = template
            .Replace("{date}", now.ToString("d", CultureInfo.CurrentCulture), StringComparison.OrdinalIgnoreCase)
            .Replace("{time}", now.ToString("t", CultureInfo.CurrentCulture), StringComparison.OrdinalIgnoreCase)
            .Replace("{day}", now.ToString("dddd", CultureInfo.CurrentCulture), StringComparison.OrdinalIgnoreCase)
            .Replace("{hijri}", now.ToString("d MMMM yyyy", hijri), StringComparison.OrdinalIgnoreCase);
        if (text.Contains("{clipboard}", StringComparison.OrdinalIgnoreCase))
            text = text.Replace("{clipboard}", clipboard?.Invoke() ?? "", StringComparison.OrdinalIgnoreCase);
        int cursor = text.IndexOf("{cursor}", StringComparison.OrdinalIgnoreCase);
        if (cursor < 0)
            return new SnippetText(text, 0);
        text = text.Remove(cursor, "{cursor}".Length);
        return new SnippetText(text, text.Length - cursor);
    }

    /// <summary>A few to start with, which the user can change or delete.</summary>
    public static List<Snippet> Starters() =>
    [
        new() { Abbreviation = ";date", Text = "{date}" },
        new() { Abbreviation = ";time", Text = "{time}" },
        new() { Abbreviation = ";hijri", Text = "{hijri}" },
    ];
}
