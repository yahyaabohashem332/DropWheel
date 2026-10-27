using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Petal.Engines.Media;

/// <summary>
/// A line made ready to be said by a voice model, which reads words, not figures or marks: numbers written out as they are
/// said (in Arabic, 340 is ثلاثمئة وأربعين; in English, three hundred forty, and 2026 a year, twenty twenty-six), a share
/// in percent said as one, brackets and quotation marks taken away (a name in brackets is said, not skipped), and the
/// spaces tidied.
/// </summary>
public static partial class SpokenText
{
    /// <summary>The text as a voice should read it, in "ar" or "en" (other languages keep their figures).</summary>
    public static string Of(string text, string language)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";
        var plain = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            plain.Append(c switch
            {
                // Arabic and Persian figures as the figures they are.
                >= (char)0x0660 and <= (char)0x0669 => (char)('0' + (c - 0x0660)),
                >= (char)0x06F0 and <= (char)0x06F9 => (char)('0' + (c - 0x06F0)),
                (char)0x066A => '%',
                (char)0x0640 => '\0',
                '(' or ')' or '[' or ']' or '{' or '}' or '«' or '»' or '"' or '“' or '”' or '„' or '*' or '_' or '#' => ' ',
                _ => c,
            });
        }
        var result = plain.Replace("\0", "").ToString();
        if (language is "ar" or "en")
        {
            bool arabic = language == "ar";
            var figures = result;
            // Words where the figure was, parted from a word that follows it (4K is four K).
            result = Figures().Replace(figures, m => Say(m, arabic) + (m.Index + m.Length < figures.Length && char.IsLetter(figures[m.Index + m.Length]) ? " " : ""));
            result = result.Replace(" & ", arabic ? " و" : " and ");
        }
        return Spaces().Replace(result, " ").Trim();
    }

    private static string Say(Match m, bool arabic)
    {
        string currency = m.Groups["currency"].Value, whole = m.Groups["whole"].Value.Replace(",", "").Replace("٬", "");
        string part = m.Groups["part"].Value;
        bool percent = m.Groups["percent"].Success;
        // A telephone number, a code or a figure too long to be a quantity: one figure at a time.
        if (whole.Length > 12 || whole.Length > 1 && whole[0] == '0' && part.Length == 0 && !m.Groups["whole"].Value.Contains(','))
            return m.Groups["prefix"].Value + string.Join(' ', whole.Select(d => Digit(d - '0', arabic)));
        long number = long.Parse(whole, CultureInfo.InvariantCulture);
        string said;
        if (!arabic && part.Length == 0 && !percent && currency.Length == 0 && whole.Length == 4 && number is >= 1100 and <= 2099)
            said = EnglishYear((int)number);
        else
            said = arabic ? Arabic(number) : English(number);
        if (part.Length > 0)
            said += arabic ? " فاصلة " + Arabic(long.Parse(part.Length > 9 ? part[..9] : part, CultureInfo.InvariantCulture))
                : " point " + string.Join(' ', part.Select(d => Digit(d - '0', false)));
        if (percent)
            said += arabic ? " بالمئة" : " percent";
        if (currency.Length > 0 && !arabic)
            said += " " + (currency switch { "$" => "dollar", "€" => "euro", _ => "pound" }) + (number == 1 && part.Length == 0 ? "" : "s");
        else if (currency.Length > 0)
            said += currency switch { "$" => " دولار", "€" => " يورو", _ => " جنيه" };
        return m.Groups["prefix"].Value + said;
    }

    private static string Digit(int d, bool arabic) => arabic ? (d == 2 ? "اثنين" : ArabicOnes[d]) : EnglishOnes[d];

    // ---------- Arabic ----------

    // Said as numbers are said aloud (عشرين, اثنين: the form after a preposition or a verb, the most heard), counting forms.
    private static readonly string[] ArabicOnes = ["صفر", "واحد", "اثنين", "ثلاثة", "أربعة", "خمسة", "ستة", "سبعة", "ثمانية", "تسعة", "عشرة",
        "أحد عشر", "اثني عشر", "ثلاثة عشر", "أربعة عشر", "خمسة عشر", "ستة عشر", "سبعة عشر", "ثمانية عشر", "تسعة عشر"];
    private static readonly string[] ArabicTens = ["", "", "عشرين", "ثلاثين", "أربعين", "خمسين", "ستين", "سبعين", "ثمانين", "تسعين"];
    private static readonly string[] ArabicHundreds = ["", "مئة", "مئتين", "ثلاثمئة", "أربعمئة", "خمسمئة", "ستمئة", "سبعمئة", "ثمانمئة", "تسعمئة"];

    /// <summary>A whole number in Arabic words: 1234 is ألف ومئتين وأربعة وثلاثين.</summary>
    public static string Arabic(long number)
    {
        if (number < 0)
            return "سالب " + Arabic(-number);
        if (number == 0)
            return ArabicOnes[0];
        // Each scale: one, two, three to ten, eleven to ninety-nine (and more) of it.
        (long Size, string One, string Two, string Few, string Many)[] scales =
        [
            (1_000_000_000, "مليار", "مليارين", "مليارات", "مليارًا"),
            (1_000_000, "مليون", "مليونين", "ملايين", "مليونًا"),
            (1_000, "ألف", "ألفين", "آلاف", "ألفًا"),
        ];
        var parts = new List<string>();
        foreach (var (size, one, two, few, many) in scales)
        {
            long count = number / size;
            number %= size;
            if (count == 0)
                continue;
            long rest = count % 100;
            parts.Add(count switch
            {
                1 => one,
                2 => two,
                <= 10 => Under1000((int)count) + " " + few,
                < 100 => Under1000((int)count) + " " + many,
                // Past a hundred, the scale follows the last part of the count: مئة وثلاثة آلاف, مئتي ألف.
                _ when rest == 0 => Under1000((int)count).Replace("مئتين", "مئتي") + " " + one,
                _ when rest == 1 => Under1000((int)(count - 1)) + " و" + one,
                _ when rest == 2 => Under1000((int)(count - 2)) + " و" + two,
                _ when rest <= 10 => Under1000((int)count) + " " + few,
                _ => Under1000((int)count) + " " + many,
            });
        }
        if (number > 0)
            parts.Add(Under1000((int)number));
        return string.Join(" و", parts);
    }

    private static string Under1000(int n)
    {
        var parts = new List<string>();
        if (n >= 100)
            parts.Add(ArabicHundreds[n / 100]);
        n %= 100;
        if (n is > 0 and < 20)
            parts.Add(ArabicOnes[n]);
        else if (n >= 20)
            parts.Add(n % 10 == 0 ? ArabicTens[n / 10] : ArabicOnes[n % 10] + " و" + ArabicTens[n / 10]);
        return string.Join(" و", parts);
    }

    // ---------- English ----------

    private static readonly string[] EnglishOnes = ["zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten",
        "eleven", "twelve", "thirteen", "fourteen", "fifteen", "sixteen", "seventeen", "eighteen", "nineteen"];
    private static readonly string[] EnglishTens = ["", "", "twenty", "thirty", "forty", "fifty", "sixty", "seventy", "eighty", "ninety"];

    /// <summary>A whole number in English words: 1234 is one thousand two hundred thirty-four.</summary>
    public static string English(long number)
    {
        if (number < 0)
            return "minus " + English(-number);
        if (number == 0)
            return EnglishOnes[0];
        var parts = new List<string>();
        foreach (var (size, name) in new[] { (1_000_000_000L, "billion"), (1_000_000L, "million"), (1_000L, "thousand") })
        {
            if (number < size)
                continue;
            parts.Add(EnglishUnder1000((int)(number / size)) + " " + name);
            number %= size;
        }
        if (number > 0)
            parts.Add(EnglishUnder1000((int)number));
        return string.Join(' ', parts);
    }

    private static string EnglishUnder1000(int n)
    {
        var parts = new List<string>();
        if (n >= 100)
            parts.Add(EnglishOnes[n / 100] + " hundred");
        n %= 100;
        if (n is > 0 and < 20)
            parts.Add(EnglishOnes[n]);
        else if (n >= 20)
            parts.Add(n % 10 == 0 ? EnglishTens[n / 10] : EnglishTens[n / 10] + "-" + EnglishOnes[n % 10]);
        return string.Join(' ', parts);
    }

    /// <summary>A year as it is said: nineteen ninety-nine, two thousand five, twenty twenty-six, nineteen oh five.</summary>
    private static string EnglishYear(int year)
    {
        if (year is >= 2000 and < 2010)
            return English(year);
        int first = year / 100, last = year % 100;
        return last == 0 ? English(first) + " hundred" : English(first) + " " + (last < 10 ? "oh " + EnglishOnes[last] : English(last));
    }

    /// <summary>A figure: a currency sign perhaps, the whole part (its thousands parted by commas or not), a decimal part, a percent sign.</summary>
    // An Arabic word of one letter (و, ب, ل, ك, ف) may lead into the figure: و3 is وثلاثة.
    [GeneratedRegex(@"(?<![\p{L}\d.,])(?<prefix>[وبلكف])?(?<currency>[$€£])?(?<whole>\d{1,3}(?:[,٬]\d{3})+|\d+)(?:[.٫](?<part>\d+))?(?<percent>\s?%)?(?![\d])")]
    private static partial Regex Figures();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex Spaces();
}
