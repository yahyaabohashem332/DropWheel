using System.Globalization;
using System.Text.RegularExpressions;

namespace Petal.Core.Text;

/// <summary>A currency: its code, the decimals it is written with, and the names texts give it.</summary>
public sealed record Currency(string Code, int Decimals, string[] Names);

/// <summary>An amount of money found in a text.</summary>
public readonly record struct MoneyMatch(int Start, int Length, double Amount, string Code);

/// <summary>What a dollar buys in each currency on a day, and amounts converted with it.</summary>
public sealed record ExchangeRates(DateTime Updated, IReadOnlyDictionary<string, double> PerDollar)
{
    /// <summary>The amount in the other currency, or null when either isn't in the table.</summary>
    public double? Convert(double amount, string from, string to) =>
        PerDollar.TryGetValue(from, out var f) && PerDollar.TryGetValue(to, out var t) && f > 0 ? amount / f * t : null;
}

/// <summary>
/// Amounts of money as texts write them: "$1,299.99", "€ 45", "250 SAR", "١٥٠ ريال", "3.5 مليون جنيه", "$5m". A name several
/// currencies share ("ريال", "$", "دينار") is the reader's own currency when it is one of them, otherwise the most used one.
/// </summary>
public static class Money
{
    /// <summary>The currency of the reader's region ("SAR"), or null when Windows doesn't say.</summary>
    public static string? HomeCurrency { get; } = Home();

    private static string? Home()
    {
        try
        {
            return RegionInfo.CurrentRegion.ISOCurrencySymbol;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    public static IReadOnlyList<Currency> All { get; } =
    [
        new("USD", 2, ["US$", "USD", "dollars", "dollar", "دولار أمريكي", "دولار امريكي", "دولارات أمريكية"]),
        new("EUR", 2, ["€", "EUR", "euros", "euro", "يورو"]),
        new("GBP", 2, ["£", "GBP", "جنيه إسترليني", "جنيه استرليني", "جنيهات إسترلينية", "باوند"]),
        new("JPY", 0, ["JPY", "yen", "ين ياباني"]),
        new("CNY", 2, ["CN¥", "CNY", "RMB", "yuan", "يوان"]),
        new("INR", 2, ["₹", "INR", "روبية هندية"]),
        new("PKR", 2, ["PKR", "روپے", "روبية باكستانية"]),
        new("TRY", 2, ["₺", "TRY", "TL", "ليرة تركية", "ليرات تركية"]),
        new("SAR", 2, ["SAR", "SR", "ر.س", "ريال سعودي", "ريالات سعودية"]),
        new("AED", 2, ["AED", "Dhs", "د.إ", "درهم إماراتي", "درهم اماراتي", "دراهم إماراتية"]),
        new("EGP", 2, ["EGP", "LE", "E£", "ج.م", "جنيه مصري", "جنيهات مصرية"]),
        new("KWD", 3, ["KWD", "KD", "د.ك", "دينار كويتي", "دنانير كويتية"]),
        new("QAR", 2, ["QAR", "QR", "ر.ق", "ريال قطري"]),
        new("BHD", 3, ["BHD", "BD", "د.ب", "دينار بحريني"]),
        new("OMR", 3, ["OMR", "RO", "ر.ع", "ريال عماني", "ريال عُماني"]),
        new("JOD", 3, ["JOD", "JD", "د.أ", "دينار أردني", "دينار اردني"]),
        new("MAD", 2, ["MAD", "د.م", "درهم مغربي"]),
        new("IQD", 0, ["IQD", "د.ع", "دينار عراقي"]),
        new("LBP", 0, ["LBP", "ل.ل", "ليرة لبنانية"]),
        new("SYP", 0, ["SYP", "ل.س", "ليرة سورية"]),
        new("TND", 3, ["TND", "د.ت", "دينار تونسي"]),
        new("DZD", 2, ["DZD", "د.ج", "دينار جزائري"]),
        new("LYD", 3, ["LYD", "ل.د", "دينار ليبي"]),
        new("SDG", 2, ["SDG", "جنيه سوداني"]),
        new("YER", 0, ["YER", "ريال يمني"]),
        new("IRR", 0, ["IRR", "ریال"]),
        new("IDR", 0, ["Rp", "IDR", "rupiah"]),
        new("MYR", 2, ["RM", "MYR", "ringgit"]),
        new("KRW", 0, ["₩", "KRW"]),
        new("CAD", 2, ["C$", "CA$", "CAD"]),
        new("AUD", 2, ["A$", "AU$", "AUD"]),
        new("CHF", 2, ["CHF"]),
        new("RUB", 2, ["₽", "RUB", "روبل"]),
        new("BDT", 2, ["৳", "BDT"]),
    ];

    /// <summary>Names several currencies share: the reader's own when it is among them, otherwise the first.</summary>
    private static readonly (string[] Names, string[] Codes)[] Shared =
    [
        (["$"], ["USD", "CAD", "AUD"]),
        (["¥"], ["JPY", "CNY"]),
        (["Rs", "Rs."], ["INR", "PKR"]),
        (["﷼", "ريال", "ريالا", "ريالًا", "ريالات", "riyal", "riyals", "rial", "rials"], ["SAR", "QAR", "OMR", "YER", "IRR"]),
        (["درهم", "درهمًا", "درهما", "دراهم", "dirham", "dirhams"], ["AED", "MAD"]),
        (["جنيه", "جنيهًا", "جنيها", "جنيهات", "pound", "pounds"], ["EGP", "SDG", "GBP"]),
        (["دينار", "دينارًا", "دينارا", "دنانير", "dinar", "dinars"], ["KWD", "JOD", "BHD", "IQD", "TND", "DZD", "LYD"]),
        (["ليرة", "ليرات", "lira", "liras"], ["TRY", "LBP", "SYP"]),
        (["دولار", "دولارًا", "دولارا", "دولارات"], ["USD", "CAD", "AUD"]),
    ];

    private static readonly string[] Multipliers = ["k", "K", "m", "M", "bn", "B", "ألف", "آلاف", "الف", "مليون", "ملايين", "مليار", "million", "millions", "billion", "billions", "thousand", "mille", "milyon", "milyar", "bin", "juta", "ribu", "millones", "mil"];

    public static Currency? Get(string code) => All.FirstOrDefault(c => c.Code == code);

    public static IReadOnlyList<MoneyMatch> Find(string text, string? home)
    {
        var found = new List<MoneyMatch>();
        foreach (var regex in new[] { Before, After })
        {
            foreach (Match m in regex.Matches(text))
            {
                if (found.Any(f => m.Index < f.Start + f.Length && f.Start < m.Index + m.Length))
                    continue;
                var code = CodeOf(m.Groups["cur"].Value, home);
                if (code is null || Amount(m.Groups["num"].Value, code) is not { } amount)
                    continue;
                if (m.Groups["mul"].Success)
                    amount *= Multiplier(m.Groups["mul"].Value.Trim());
                found.Add(new MoneyMatch(m.Index, m.Length, amount, code));
            }
        }
        return found.OrderBy(f => f.Start).ToList();
    }

    /// <summary>"1,299.99 USD", with as many decimals as the currency uses (and none when the amount is whole).</summary>
    public static string Format(double amount, string code)
    {
        int decimals = Get(code)?.Decimals ?? 2;
        bool whole = Math.Abs(amount - Math.Round(amount)) < 0.005;
        var number = amount.ToString(whole && Math.Abs(amount) >= 100 ? "#,0" : "#,0." + new string('0', decimals), CultureInfo.InvariantCulture);
        return number + " " + code;
    }

    private static string? CodeOf(string name, string? home)
    {
        var trimmed = name.Trim();
        foreach (var (names, codes) in Shared)
            if (names.Any(n => string.Equals(n, trimmed, StringComparison.OrdinalIgnoreCase)))
                return home is not null && codes.Contains(home) ? home : codes[0];
        foreach (var currency in All)
            if (currency.Names.Any(n => string.Equals(n, trimmed, n.Length <= 3 && n.All(char.IsLetter) ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase)))
                return currency.Code;
        return null;
    }

    /// <summary>
    /// The number as the currency writes it: "1,299.99", "1.299,99", "1 299,99", "12,5". A comma or dot before exactly three
    /// digits separates thousands; before one or two, it is the decimal mark.
    /// </summary>
    internal static double? Amount(string number, string code)
    {
        var s = new string(number.Where(c => c is not ' ' and not (char)0xA0 and not (char)0x202F).ToArray());
        if (s.Length == 0)
            return null;
        int comma = s.LastIndexOf(','), dot = s.LastIndexOf('.');
        if (comma >= 0 && dot >= 0)
            s = comma > dot ? s.Replace(".", "").Replace(',', '.') : s.Replace(",", "");
        else if (comma >= 0)
            s = s.Length - comma - 1 == 3 ? s.Replace(",", "") : s.Replace(',', '.');
        else if (dot >= 0 && (s.Count(c => c == '.') > 1 || (s.Length - dot - 1 == 3 && (Get(code)?.Decimals ?? 2) < 3 && s[..dot].Length <= 3 && DotThousands.Contains(code))))
            s = s.Replace(".", "");
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    /// <summary>Currencies usually written with a dot between the thousands ("1.299 €").</summary>
    private static readonly HashSet<string> DotThousands = ["EUR", "TRY", "IDR", "IRR", "CHF"];

    private static double Multiplier(string word) => word switch
    {
        "k" or "K" or "ألف" or "آلاف" or "الف" or "thousand" or "mille" or "bin" or "ribu" or "mil" => 1e3,
        "m" or "M" or "مليون" or "ملايين" or "million" or "millions" or "milyon" or "juta" or "millones" => 1e6,
        _ => 1e9,
    };

    private static readonly string Names = string.Join("|", All.SelectMany(c => c.Names).Concat(Shared.SelectMany(s => s.Names))
        .Distinct().OrderByDescending(n => n.Length).Select(Regex.Escape));

    private static readonly string Spaces = " " + (char)0xA0 + (char)0x202F;

    private static readonly string Number = @"(?<num>\d{1,3}(?:[,." + Spaces + @"]\d{3})+(?:[.,]\d{1,3})?|\d+(?:[.,]\d{1,3})?)";

    private static readonly string Multiplied = @"(?<mul>[" + Spaces + @"]?(?:" + string.Join("|", Multipliers.OrderByDescending(m => m.Length).Select(Regex.Escape)) + @"))?";

    // "$1,299.99", "US$ 45", "SAR 250": the currency first.
    private static readonly Regex Before = new(@"(?<![\p{L}\d])(?<cur>" + Names + @")[" + Spaces + @"]?" + Number + Multiplied + @"(?![\p{L}\d])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    // "250 SAR", "١٥٠ ريال", "45 €": the amount first.
    private static readonly Regex After = new(@"(?<![\p{L}\d.,])" + Number + Multiplied + @"[" + Spaces + @"]?(?<cur>" + Names + @")(?![\p{L}\d])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
}
