using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Petal.Core.Text;

/// <summary>How much text there is.</summary>
public sealed record TextCount(int Words, int Characters, int CharactersWithoutSpaces, int Lines, int Sentences);

/// <summary>Small things done to selected text: capitals, Arabic marks, digits, spaces, counting.</summary>
public static partial class TextTools
{
    public static string Upper(string text) => text.ToUpper(CultureInfo.CurrentCulture);

    public static string Lower(string text) => text.ToLower(CultureInfo.CurrentCulture);

    /// <summary>Every Word Starts With A Capital (Arabic has no capitals and stays as it is).</summary>
    public static string Title(string text)
    {
        var sb = new StringBuilder(text.Length);
        bool start = true;
        foreach (char c in text)
        {
            sb.Append(start ? char.ToUpper(c, CultureInfo.CurrentCulture) : char.ToLower(c, CultureInfo.CurrentCulture));
            start = char.IsWhiteSpace(c) || c is '-' or '(' or '"' or '“' or '/';
        }
        return sb.ToString();
    }

    /// <summary>Sentence case: the first letter of each sentence a capital, the rest small.</summary>
    public static string Sentence(string text)
    {
        var sb = new StringBuilder(text.Length);
        bool start = true;
        foreach (char c in text)
        {
            if (char.IsLetter(c))
            {
                sb.Append(start ? char.ToUpper(c, CultureInfo.CurrentCulture) : char.ToLower(c, CultureInfo.CurrentCulture));
                start = false;
            }
            else
            {
                sb.Append(c);
                if (c is '.' or '!' or '?' or '؟' or '\n')
                    start = true;
            }
        }
        return sb.ToString();
    }

    /// <summary>Arabic without its short-vowel marks (tashkeel), Quranic marks or the stretching tatweel.</summary>
    public static string RemoveDiacritics(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (char c in text)
            if (!IsArabicMark(c))
                sb.Append(c);
        return sb.ToString();
    }

    private static bool IsArabicMark(char c) =>
        c is >= 'ً' and <= 'ٟ' or 'ٰ' or >= 'ۖ' and <= 'ۭ' or 'ـ' or >= 'ؐ' and <= 'ؚ';

    /// <summary>
    /// The other kind of digits: Arabic-Indic (٠١٢٣) become 0123 when there are any, otherwise 0123 become ٠١٢٣.
    /// Persian digits (۰۱۲) count as Arabic-Indic.
    /// </summary>
    public static string SwapDigits(string text)
    {
        bool hasIndic = text.Any(c => c is >= '٠' and <= '٩' or >= '۰' and <= '۹');
        var sb = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            if (hasIndic)
                sb.Append(c switch
                {
                    >= '٠' and <= '٩' => (char)('0' + c - '٠'),
                    >= '۰' and <= '۹' => (char)('0' + c - '۰'),
                    '٫' => '.',
                    '٬' => ',',
                    _ => c,
                });
            else
                sb.Append(c is >= '0' and <= '9' ? (char)('٠' + c - '0') : c);
        }
        return sb.ToString();
    }

    /// <summary>One space between words, none at the ends of lines, at most one empty line in a row.</summary>
    public static string TidySpaces(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n').Select(l => Spaces().Replace(l, " ").Trim()).ToList();
        var kept = new List<string>();
        foreach (var line in lines)
        {
            if (line.Length == 0 && (kept.Count == 0 || kept[^1].Length == 0))
                continue;
            kept.Add(line);
        }
        while (kept.Count > 0 && kept[^1].Length == 0)
            kept.RemoveAt(kept.Count - 1);
        return string.Join(Environment.NewLine, kept);
    }

    public static TextCount Count(string text)
    {
        int words = Words().Matches(text).Count;
        int chars = new StringInfo(text.Replace("\r\n", "\n")).LengthInTextElements;
        int noSpaces = text.Count(c => !char.IsWhiteSpace(c));
        int lines = text.Length == 0 ? 0 : text.Replace("\r\n", "\n").TrimEnd('\n').Split('\n').Length;
        int sentences = Sentences().Matches(text).Count;
        if (sentences == 0 && words > 0)
            sentences = 1;
        return new TextCount(words, chars, noSpaces, lines, sentences);
    }

    [GeneratedRegex(@"[ \t ]+")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"[\p{L}\p{N}][\p{L}\p{N}\p{Mn}'’\-]*")]
    private static partial Regex Words();

    [GeneratedRegex(@"[^.!?؟\n]*[\p{L}\p{N}][^.!?؟\n]*[.!?؟]+")]
    private static partial Regex Sentences();
}
