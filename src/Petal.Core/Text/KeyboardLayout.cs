using System.Text;

namespace Petal.Core.Text;

/// <summary>
/// Text typed with the wrong keyboard language, put right: "ؤشف" typed on the Arabic layout while meaning
/// "cat", or "hgsghl" typed on the English one while meaning "السلام". The keys are those of the US English
/// and the Arabic (101) layouts that Windows installs.
/// </summary>
public static class KeyboardLayout
{
    // US key → Arabic (101) character, unshifted and shifted.
    private static readonly (char Key, string Arabic)[] Keys =
    [
        ('`', "ذ"), ('q', "ض"), ('w', "ص"), ('e', "ث"), ('r', "ق"), ('t', "ف"), ('y', "غ"), ('u', "ع"), ('i', "ه"), ('o', "خ"), ('p', "ح"),
        ('[', "ج"), (']', "د"), ('a', "ش"), ('s', "س"), ('d', "ي"), ('f', "ب"), ('g', "ل"), ('h', "ا"), ('j', "ت"), ('k', "ن"), ('l', "م"),
        (';', "ك"), ('\'', "ط"), ('z', "ئ"), ('x', "ء"), ('c', "ؤ"), ('v', "ر"), ('b', "لا"), ('n', "ى"), ('m', "ة"), (',', "و"), ('.', "ز"), ('/', "ظ"),
        ('~', "ّ"), ('Q', "َ"), ('W', "ً"), ('E', "ُ"), ('R', "ٌ"), ('T', "لإ"), ('Y', "إ"), ('U', "‘"), ('I', "÷"), ('O', "×"), ('P', "؛"),
        ('{', "<"), ('}', ">"), ('A', "ِ"), ('S', "ٍ"), ('D', "]"), ('F', "["), ('G', "لأ"), ('H', "أ"), ('J', "ـ"), ('K', "،"), ('L', "/"),
        ('Z', "~"), ('X', "ْ"), ('C', "}"), ('V', "{"), ('B', "لآ"), ('N', "آ"), ('M', "’"), ('<', ","), ('>', "."), ('?', "؟"),
    ];

    private static readonly Dictionary<char, string> ToArabicMap = Keys.ToDictionary(k => k.Key, k => k.Arabic);

    // Arabic → the key, single characters only; "لا" and its kin are read as two characters below.
    private static readonly Dictionary<char, char> ToEnglishMap = Keys
        .Where(k => k.Arabic.Length == 1 && !"<>[]{}/~,.".Contains(k.Arabic[0]))
        .GroupBy(k => k.Arabic[0])
        .ToDictionary(g => g.Key, g => g.First().Key);

    /// <summary>Which way the text needs turning: true when it is (mostly) Arabic letters typed for English.</summary>
    public static bool LooksArabic(string text)
    {
        int arabic = 0, latin = 0;
        foreach (char c in text)
        {
            if (c is >= '؀' and <= 'ۿ')
                arabic++;
            else if (c is >= 'a' and <= 'z' or >= 'A' and <= 'Z')
                latin++;
        }
        return arabic > latin;
    }

    /// <summary>Turns the text the other way round: Arabic letters into the English keys under them, or back.</summary>
    public static string Fix(string text) => LooksArabic(text) ? ToEnglish(text) : ToArabic(text);

    /// <summary>English typed on the Arabic layout's keys, as the Arabic it meant.</summary>
    public static string ToArabic(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            // Caps Lock on: capitals mean the plain key.
            if (ToArabicMap.TryGetValue(c, out var arabic))
                sb.Append(arabic);
            else
                sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>Arabic typed on the English layout's keys, as the English it meant.</summary>
    public static string ToEnglish(string text)
    {
        var sb = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            // ل followed by an alef is one key (B, T, G, b) or two (g then h). After a vowel and before the end of a word
            // or a t, as in "light", "though", it was the two; otherwise the one.
            if (c == 'ل' && i + 1 < text.Length && text[i + 1] is 'ا' or 'إ' or 'أ' or 'آ')
            {
                char alef = text[i + 1];
                bool gh = alef == 'ا' && sb.Length > 0 && "aeiouAEIOU".Contains(sb[^1])
                    && (i + 2 >= text.Length || !IsArabicLetter(text[i + 2]) || text[i + 2] == 'ف');
                sb.Append(alef switch
                {
                    'ا' => gh ? "gh" : "b",
                    'إ' => "T",
                    'أ' => "G",
                    _ => "B",
                });
                i++;
                continue;
            }
            sb.Append(ToEnglishMap.TryGetValue(c, out var key) ? key : c);
        }
        return sb.ToString();
    }

    private static bool IsArabicLetter(char c) => c is >= 'ء' and <= 'ي';
}
