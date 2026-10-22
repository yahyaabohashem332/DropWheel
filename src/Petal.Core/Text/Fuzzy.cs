namespace Petal.Core.Text;

/// <summary>
/// How well a few typed letters match a name, the way a command palette wants it: the whole name, its
/// start, the start of a word, then anywhere, then letters in order ("rcs" finds "Record the Screen").
/// Arabic is folded so أ إ آ match ا, ة matches ه, ى matches ي, and diacritics don't count; Latin
/// letters ignore case and accents.
/// </summary>
public static class Fuzzy
{
    /// <summary>0 for no match; higher is better (up to 1000).</summary>
    public static int Score(string query, string name)
    {
        var q = Fold(query);
        var n = Fold(name);
        if (q.Length == 0 || n.Length == 0)
            return 0;
        if (n == q)
            return 1000;
        if (n.StartsWith(q, StringComparison.Ordinal))
            return 900 - Math.Min(100, n.Length - q.Length);
        // Every typed word at the start of a word of the name ("rec scr").
        var words = n.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var typed = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (typed.All(t => words.Any(w => w.StartsWith(t, StringComparison.Ordinal))))
            return 800 - Math.Min(100, n.Length - q.Length) - (words.Length > 0 && words[0].StartsWith(typed[0], StringComparison.Ordinal) ? 0 : 20);
        int at = n.IndexOf(q, StringComparison.Ordinal);
        if (at >= 0)
            return 600 - Math.Min(150, at * 5);
        // Initials: "rts" for "record the screen".
        var initials = string.Concat(words.Select(w => w[0]));
        var compact = q.Replace(" ", "");
        if (compact.Length >= 2 && initials.StartsWith(compact, StringComparison.Ordinal))
            return 550;
        // Letters in order, closer together is better.
        int pos = -1, gaps = 0;
        foreach (var c in compact)
        {
            int next = n.IndexOf(c, pos + 1);
            if (next < 0)
                return 0;
            if (pos >= 0)
                gaps += next - pos - 1;
            pos = next;
        }
        return compact.Length < 3 ? 0 : Math.Max(1, 400 - gaps * 8);
    }

    /// <summary>The best score against several names for the same thing (English, Arabic, a short label).</summary>
    public static int Best(string query, params string?[] names)
    {
        int best = 0;
        foreach (var name in names)
            if (!string.IsNullOrEmpty(name))
                best = Math.Max(best, Score(query, name));
        return best;
    }

    /// <summary>Lower case, no accents or diacritics, Arabic letters folded, single spaces.</summary>
    public static string Fold(string text)
    {
        var sb = new System.Text.StringBuilder(text.Length);
        bool space = true;
        foreach (var raw in text.Normalize(System.Text.NormalizationForm.FormD))
        {
            var category = char.GetUnicodeCategory(raw);
            if (category == System.Globalization.UnicodeCategory.NonSpacingMark || raw is >= 'ً' and <= 'ٟ' || raw is 'ٰ' or 'ـ')
                continue;
            char c = raw switch
            {
                'أ' or 'إ' or 'آ' or 'ٱ' => 'ا',
                'ة' => 'ه',
                'ى' => 'ي',
                'ؤ' => 'و',
                'ئ' => 'ي',
                _ => char.ToLowerInvariant(raw),
            };
            if (char.IsWhiteSpace(c) || c is '-' or '_' or '.' or '/' or ',' or '،')
            {
                if (!space)
                    sb.Append(' ');
                space = true;
                continue;
            }
            sb.Append(c);
            space = false;
        }
        return sb.ToString().TrimEnd();
    }
}
