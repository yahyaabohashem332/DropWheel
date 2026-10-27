using System.Globalization;
using System.Text.RegularExpressions;

namespace Petal.Core.Text;

/// <summary>An amount with its unit, as a text writes it: where it is, the amount, and the unit's short name.</summary>
public readonly record struct MeasureMatch(int Start, int Length, double Value, string Unit);

public static partial class Calculator
{
    /// <summary>
    /// The units a measurement in running text is recognised by: the names that can't be taken for something else. Times
    /// and file sizes are left out (nothing worth converting), and so are single letters that are words ("in", "م").
    /// </summary>
    private static readonly string[] RunningUnits =
    [
        "mm", "millimetre", "millimeter", "millimetres", "millimeters", "cm", "centimetre", "centimeter", "centimetres", "centimeters",
        "metre", "meter", "metres", "meters", "km", "kilometre", "kilometer", "kilometres", "kilometers",
        "inch", "inches", "ft", "foot", "feet", "yd", "yard", "yards", "mi", "mile", "miles", "nmi", "nautical mile", "nautical miles",
        "mg", "milligram", "milligrams", "gram", "grams", "kg", "kilo", "kilos", "kilogram", "kilograms", "tonne", "tonnes",
        "oz", "ounce", "ounces", "lb", "lbs",
        "ml", "millilitre", "milliliter", "millilitres", "milliliters", "litre", "liter", "litres", "liters", "gal", "gallon", "gallons",
        "cup", "cups", "fl oz", "fluid ounce", "fluid ounces",
        "m²", "m2", "sq m", "square metre", "square meter", "square metres", "square meters", "km²", "km2", "sq km",
        "ha", "hectare", "hectares", "acre", "acres", "ft²", "ft2", "sq ft", "square foot", "square feet",
        "mi²", "mi2", "sq mi", "square mile", "square miles", "feddan", "feddans",
        "km/h", "kmh", "kph", "mph", "m/s", "knot", "knots",
        "celsius", "fahrenheit", "kelvin",
        "مم", "ملم", "مليمتر", "سم", "سنتيمتر", "متر", "أمتار", "كم", "كيلومتر", "كيلومترات", "إنش", "انش", "بوصة", "قدم", "أقدام",
        "ياردة", "ميل", "أميال", "ميل بحري", "ملغ", "ملغم", "مليغرام", "غرام", "جرام", "كغ", "كجم", "كيلو", "كيلوغرام", "كيلوجرام",
        "طن", "أونصة", "رطل", "مل", "ملليلتر", "مليلتر", "لتر", "لترات", "غالون", "جالون", "كوب",
        "متر مربع", "كيلومتر مربع", "ميل مربع", "هكتار", "فدان", "أفدنة", "قدم مربع", "كم/س", "كم/ساعة", "ميل/س", "عقدة",
        "درجة مئوية", "مئوية", "سيليزية", "فهرنهايت", "درجة فهرنهايت",
    ];

    private static readonly Regex Running = new(
        @"(?<![\p{L}\d.,])(?<num>\d{1,3}(?:,\d{3})+(?:\.\d+)?|\d+(?:[.,]\d+)?)\s?(?<unit>" + string.Join("|", RunningUnits.OrderByDescending(u => u.Length).Select(Regex.Escape)) + @")(?![\p{L}\d²/])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// Metres, grams and litres by their letter: "5 m" (with its space, as "5m" is as often five minutes or five
    /// million), "500 g" or "500g" (lower case: "5G" is a network), and "2 L".
    /// </summary>
    private static readonly Regex Letters = new(@"(?<![\p{L}\d.,])(?<num>\d+(?:[.,]\d+)?)(?:(?<sp>\s)(?<unit>m)|\s?(?<unit>g|L|l))(?![\p{L}\d²/'])",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex Degrees = new(@"(?<![\p{L}\d.,])(?<num>-?\d+(?:[.,]\d+)?)\s?(?:[°º]\s?(?<unit>[CFcf])|(?:degrees?|deg)\s(?<unit>celsius|fahrenheit|C|F))(?![\p{L}\d])",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex FeetInches = new(@"(?<![\p{L}\d.,])(?<ft>\d)\s?(?:'|′|ft|feet|foot)\s?(?<in>\d{1,2}(?:\.\d+)?)\s?(?:""|″|in|inches)?(?![\p{L}\d])",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// Measurements in running text: "5 km", "70 kg", "45°F", "5′11″", "١٢٠ كم/ساعة", "3 أفدنة". Expects Western digits.
    /// </summary>
    public static IReadOnlyList<MeasureMatch> FindMeasures(string text)
    {
        var found = new List<MeasureMatch>();
        void Add(Match m, double value, string unit)
        {
            if (!found.Any(f => m.Index < f.Start + f.Length && f.Start < m.Index + m.Length))
                found.Add(new MeasureMatch(m.Index, m.Length, value, unit));
        }
        foreach (Match m in FeetInches.Matches(text))
        {
            double inches = double.Parse(m.Groups["ft"].Value, CultureInfo.InvariantCulture) * 12 + double.Parse(m.Groups["in"].Value, CultureInfo.InvariantCulture);
            if (double.Parse(m.Groups["in"].Value, CultureInfo.InvariantCulture) < 12)
                Add(m, inches, "in");
        }
        foreach (Match m in Degrees.Matches(text))
            if (Number(m.Groups["num"].Value) is { } value)
                Add(m, value, m.Groups["unit"].Value.ToLowerInvariant() is "c" or "celsius" ? "°C" : "°F");
        foreach (Match m in Running.Matches(text))
            if (Number(m.Groups["num"].Value) is { } value && ByName.TryGetValue(m.Groups["unit"].Value, out var unit))
                Add(m, value, unit.Name);
        foreach (Match m in Letters.Matches(text))
            if (Number(m.Groups["num"].Value) is { } value && ByName.TryGetValue(m.Groups["unit"].Value, out var unit))
                Add(m, value, unit.Name);
        return found.OrderBy(f => f.Start).ToList();
    }

    /// <summary>"1,500.5" with thousands, "1,5" with a decimal comma.</summary>
    private static double? Number(string text)
    {
        var s = ThousandsComma().IsMatch(text) ? text.Replace(",", "") : text.Replace(',', '.');
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    /// <summary>Where each unit goes for someone used to the other system: kilometres to miles, pounds to kilograms.</summary>
    private static readonly Dictionary<string, string[]> Readers = new()
    {
        ["mm"] = ["in"], ["cm"] = ["in"], ["m"] = ["ft"], ["km"] = ["mi"],
        ["in"] = ["cm"], ["ft"] = ["m"], ["yd"] = ["m"], ["mi"] = ["km"], ["nmi"] = ["km"],
        ["mg"] = ["oz"], ["g"] = ["oz"], ["kg"] = ["lb"], ["t"] = ["lb"], ["oz"] = ["g"], ["lb"] = ["kg"],
        ["ml"] = ["fl oz"], ["l"] = ["gal"], ["gal"] = ["l"], ["cup"] = ["ml"], ["fl oz"] = ["ml"],
        ["m²"] = ["ft²"], ["km²"] = ["mi²"], ["ha"] = ["acre"], ["acre"] = ["ha", "m²"], ["feddan"] = ["m²", "acre"],
        ["ft²"] = ["m²"], ["mi²"] = ["km²"],
        ["km/h"] = ["mph"], ["mph"] = ["km/h"], ["m/s"] = ["km/h"], ["kn"] = ["km/h"],
        ["°C"] = ["°F"], ["°F"] = ["°C"], ["K"] = ["°C"],
    };

    /// <summary>
    /// The measurement in the other system, as people read it: "5 km" is "3.11 mi", "45 °F" is "7.2 °C", and a person's
    /// height in centimetres is also given in feet and inches. Empty for a unit with nothing to convert to.
    /// </summary>
    public static IReadOnlyList<Calculation> ForReader(double value, string unit)
    {
        if (!ByName.TryGetValue(unit, out var from) || !Readers.TryGetValue(from.Name, out var targets))
            return [];
        var answers = new List<Calculation>();
        foreach (var name in targets)
        {
            var to = ByName[name];
            double converted = Convert(value, from, to);
            // Temperatures are read to a tenth of a degree.
            var shown = to.Kind == "temperature" ? Math.Round(converted, 1).ToString("0.#", CultureInfo.InvariantCulture) : Rounded(converted);
            answers.Add(new Calculation(converted, shown + " " + to.Name, to.Name));
        }
        // 180 cm, 1.8 m, 70 in: a height, also in feet and inches.
        double inches = Convert(value, from, ByName["in"]);
        if (from.Kind == "length" && from.Name is "cm" or "m" or "in" && inches is >= 36 and < 100)
        {
            int feet = (int)(inches / 12);
            double rest = Math.Round(inches - feet * 12, 1);
            if (rest >= 12)
            {
                feet++;
                rest -= 12;
            }
            answers.Add(new Calculation(inches, $"{feet}′ {rest.ToString("0.#", CultureInfo.InvariantCulture)}″", "ft+in"));
        }
        return answers;
    }

    /// <summary>A converted amount to the digits that mean something: 3.11, 74.6, 154.3, 2,205, 0.53, 0.035.</summary>
    public static string Rounded(double value)
    {
        double abs = Math.Abs(value);
        if (abs == 0)
            return "0";
        int decimals = abs >= 1000 ? 0 : abs >= 10 ? 1 : abs >= 1 ? 2 : 1 - (int)Math.Floor(Math.Log10(abs));
        return Math.Round(value, Math.Min(decimals, 12)).ToString(abs >= 1000 ? "#,0" : "0." + new string('#', Math.Max(decimals, 1)), CultureInfo.InvariantCulture);
    }

    [GeneratedRegex(@"^\d{1,3}(?:,\d{3})+(?:\.\d+)?$")]
    private static partial Regex ThousandsComma();
}
