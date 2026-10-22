using System.Globalization;
using System.Text;

namespace Petal.Core.Text;

/// <summary>An answer: the number, how to read it, and the unit it is in (empty for a plain number).</summary>
public sealed record Calculation(double Value, string Text, string Unit);

/// <summary>
/// Sums and conversions typed the way people write them, in English or Arabic: "12% of 340", "(3+4)*2^3",
/// "sqrt 2", "5 km in mi", "٧٠ كغ بالرطل", "98.6 f to c", "1.5 GB in MB". Arabic-Indic digits work too.
/// </summary>
public static class Calculator
{
    private sealed record Unit(string Name, string Kind, double Factor, double Offset = 0);

    // Factors to the kind's base unit (metre, kilogram, litre, square metre, metre a second, byte, second, kelvin).
    private static readonly (string[] Names, Unit Unit)[] Units =
    [
        (["mm", "millimetre", "millimeter", "millimetres", "millimeters", "مم", "ملم", "مليمتر"], new("mm", "length", 0.001)),
        (["cm", "centimetre", "centimeter", "centimetres", "centimeters", "سم", "سنتيمتر"], new("cm", "length", 0.01)),
        (["m", "metre", "meter", "metres", "meters", "م", "متر", "أمتار"], new("m", "length", 1)),
        (["km", "kilometre", "kilometer", "kilometres", "kilometers", "كم", "كيلومتر", "كيلومترات"], new("km", "length", 1000)),
        (["in", "inch", "inches", "\"", "إنش", "انش", "بوصة"], new("in", "length", 0.0254)),
        (["ft", "foot", "feet", "'", "قدم", "أقدام"], new("ft", "length", 0.3048)),
        (["yd", "yard", "yards", "ياردة"], new("yd", "length", 0.9144)),
        (["mi", "mile", "miles", "ميل", "أميال"], new("mi", "length", 1609.344)),
        (["nmi", "nautical mile", "nautical miles", "ميل بحري"], new("nmi", "length", 1852)),
        (["mg", "milligram", "milligrams", "ملغ", "ملغم", "مليغرام"], new("mg", "mass", 1e-6)),
        (["g", "gram", "grams", "غ", "غم", "جم", "غرام", "جرام"], new("g", "mass", 0.001)),
        (["kg", "kilo", "kilos", "kilogram", "kilograms", "كغ", "كجم", "كيلو", "كيلوغرام", "كيلوجرام"], new("kg", "mass", 1)),
        (["t", "tonne", "tonnes", "ton", "tons", "طن"], new("t", "mass", 1000)),
        (["oz", "ounce", "ounces", "أونصة"], new("oz", "mass", 0.028349523125)),
        (["lb", "lbs", "pound", "pounds", "رطل", "باوند"], new("lb", "mass", 0.45359237)),
        (["ml", "millilitre", "milliliter", "millilitres", "milliliters", "مل", "ملليلتر", "مليلتر"], new("ml", "volume", 0.001)),
        (["l", "litre", "liter", "litres", "liters", "ل", "لتر", "لترات"], new("l", "volume", 1)),
        (["gal", "gallon", "gallons", "غالون", "جالون"], new("gal", "volume", 3.785411784)),
        (["cup", "cups", "كوب"], new("cup", "volume", 0.2365882365)),
        (["floz", "fl oz", "fluid ounce", "fluid ounces"], new("fl oz", "volume", 0.0295735295625)),
        (["m2", "m²", "sq m", "square metre", "square meter", "متر مربع"], new("m²", "area", 1)),
        (["km2", "km²", "sq km", "square kilometre", "square kilometer", "كيلومتر مربع"], new("km²", "area", 1e6)),
        (["ha", "hectare", "hectares", "هكتار"], new("ha", "area", 1e4)),
        (["acre", "acres", "فدان"], new("acre", "area", 4046.8564224)),
        (["ft2", "ft²", "sq ft", "square foot", "square feet", "قدم مربع"], new("ft²", "area", 0.09290304)),
        (["m/s", "مت/ث"], new("m/s", "speed", 1)),
        (["km/h", "kmh", "kph", "كم/س", "كم/ساعة"], new("km/h", "speed", 1 / 3.6)),
        (["mph", "ميل/س"], new("mph", "speed", 0.44704)),
        (["knot", "knots", "kn", "عقدة"], new("kn", "speed", 0.514444)),
        (["b", "byte", "bytes", "بايت"], new("B", "data", 1)),
        (["kb", "kilobyte", "kilobytes", "كيلوبايت"], new("KB", "data", 1e3)),
        (["mb", "megabyte", "megabytes", "ميغابايت", "ميجابايت"], new("MB", "data", 1e6)),
        (["gb", "gigabyte", "gigabytes", "غيغابايت", "جيجابايت"], new("GB", "data", 1e9)),
        (["tb", "terabyte", "terabytes", "تيرابايت"], new("TB", "data", 1e12)),
        (["kib"], new("KiB", "data", 1024)),
        (["mib"], new("MiB", "data", 1048576)),
        (["gib"], new("GiB", "data", 1073741824)),
        (["s", "sec", "secs", "second", "seconds", "ث", "ثانية", "ثوان", "ثواني"], new("s", "time", 1)),
        (["min", "mins", "minute", "minutes", "د", "دقيقة", "دقائق"], new("min", "time", 60)),
        (["h", "hr", "hrs", "hour", "hours", "س", "ساعة", "ساعات"], new("h", "time", 3600)),
        (["day", "days", "يوم", "أيام"], new("day", "time", 86400)),
        (["week", "weeks", "أسبوع", "أسابيع"], new("week", "time", 604800)),
        (["c", "°c", "celsius", "مئوية", "سيليزية", "درجة مئوية"], new("°C", "temperature", 1, 273.15)),
        (["f", "°f", "fahrenheit", "فهرنهايت"], new("°F", "temperature", 5.0 / 9, 459.67)),
        (["k", "kelvin", "كلفن"], new("K", "temperature", 1)),
    ];

    private static readonly Dictionary<string, Unit> ByName = Units
        .SelectMany(u => u.Names.Select(n => (n, u.Unit)))
        .GroupBy(x => x.n, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(g => g.Key, g => g.First().Unit, StringComparer.OrdinalIgnoreCase);

    private static readonly string[] Joins = [" in ", " to ", " into ", " as ", " إلى ", " الى ", " بال", " ب", " = "];

    /// <summary>The answer, or null when the text isn't a sum or a conversion Petal can read.</summary>
    public static Calculation? Evaluate(string input)
    {
        var text = Normalise(input);
        if (text.Length == 0)
            return null;
        try
        {
            if (Conversion(text) is { } converted)
                return converted;
            var parser = new Parser(text);
            double value = parser.ParseAll();
            if (double.IsNaN(value) || double.IsInfinity(value))
                return null;
            return new Calculation(value, Format(value), "");
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>A number the way the answer shows it: up to ten significant digits, no trailing zeros, thousands apart.</summary>
    public static string Format(double value)
    {
        if (value == 0)
            return "0";
        double abs = Math.Abs(value);
        if (abs >= 1e15 || abs < 1e-6)
            return value.ToString("0.#########E+0", CultureInfo.InvariantCulture);
        var rounded = Math.Round(value, Math.Max(0, 9 - (int)Math.Floor(Math.Log10(abs))));
        return rounded.ToString(abs >= 1000 ? "#,0.##########" : "0.##########", CultureInfo.InvariantCulture);
    }

    private static string Normalise(string input)
    {
        var sb = new StringBuilder(input.Length);
        foreach (char c in input.Trim())
            sb.Append(c switch
            {
                >= '٠' and <= '٩' => (char)('0' + c - '٠'),
                >= '۰' and <= '۹' => (char)('0' + c - '۰'),
                '٫' => '.',
                '٬' or '،' => ',',
                '×' or '✕' => '*',
                '÷' => '/',
                '−' or '–' => '-',
                _ => c,
            });
        var text = sb.ToString().TrimEnd('=', ' ');
        // "12,500.75": thousands commas between digits go; a lone comma is a decimal point ("3,5").
        if (text.Contains('.'))
            text = text.Replace(",", "");
        else if (System.Text.RegularExpressions.Regex.IsMatch(text, @"\d,\d{3}(?!\d)"))
            text = System.Text.RegularExpressions.Regex.Replace(text, @"(?<=\d),(?=\d{3}(?!\d))", "");
        else
            text = System.Text.RegularExpressions.Regex.Replace(text, @"(?<=\d),(?=\d)", ".");
        return text;
    }

    /// <summary>"5 km in mi": an amount (itself a sum), its unit, a joining word and the unit wanted.</summary>
    private static Calculation? Conversion(string text)
    {
        string lower = " " + text.ToLowerInvariant() + " ";
        foreach (var join in Joins)
        {
            int at = lower.LastIndexOf(join, StringComparison.Ordinal);
            if (at <= 0)
                continue;
            string left = lower[1..at].Trim(), right = lower[(at + join.Length)..].Trim();
            if (!ByName.TryGetValue(right, out var to))
                continue;
            if (SplitUnit(left) is not { } amount || amount.Unit.Kind != to.Kind)
                continue;
            double value = Convert(amount.Value, amount.Unit, to);
            return new Calculation(value, Format(value) + " " + to.Name, to.Name);
        }
        return null;
    }

    private static (double Value, Unit Unit)? SplitUnit(string left)
    {
        // The longest unit name that ends the text, after a number or a closing bracket.
        foreach (var name in ByName.Keys.OrderByDescending(n => n.Length))
        {
            if (!left.EndsWith(name, StringComparison.OrdinalIgnoreCase))
                continue;
            var number = left[..^name.Length].Trim();
            if (number.Length == 0 || !(char.IsDigit(number[^1]) || number[^1] is ')' or '.'))
                continue;
            if (number.EndsWith('°'))
                number = number[..^1];
            try
            {
                return (new Parser(number).ParseAll(), ByName[name]);
            }
            catch (FormatException)
            {
                return null;
            }
        }
        return null;
    }

    private static double Convert(double value, Unit from, Unit to)
    {
        if (from.Kind == "temperature")
        {
            double kelvin = (value + from.Offset) * from.Factor;
            return kelvin / to.Factor - to.Offset;
        }
        return value * from.Factor / to.Factor;
    }

    /// <summary>
    /// + − × ÷ ^ with brackets, % ("12% of 340", "340 + 12%"), functions (sqrt, sin, cos, tan, log, ln, abs,
    /// round, floor, ceil) in degrees, and pi and e.
    /// </summary>
    private sealed class Parser(string text)
    {
        private int _i;

        public double ParseAll()
        {
            double v = Sum();
            Skip();
            if (_i != text.Length)
                throw new FormatException();
            return v;
        }

        private double Sum()
        {
            // A percentage on its own is a fraction: "50%" is 0.5.
            double v = Product(out bool alone);
            if (alone)
                v /= 100;
            while (true)
            {
                Skip();
                if (Take('+')) v = AddOrPercent(v, +1);
                else if (Take('-')) v = AddOrPercent(v, -1);
                else return v;
            }
        }

        // "340 + 12%" is 340 and 12 % of it.
        private double AddOrPercent(double left, int sign)
        {
            double right = Product(out bool percent);
            return percent ? left + sign * left * right / 100 : left + sign * right;
        }

        private double Product() => Product(out _);

        private double Product(out bool percent)
        {
            double v = Power(out percent);
            while (true)
            {
                Skip();
                if (Take('*')) { v *= Power(out _); percent = false; }
                else if (Take('/')) { v /= Power(out _); percent = false; }
                else if (Word("of") || Word("من"))
                {
                    double of = Power(out _);
                    v = percent ? v * of / 100 : v * of;
                    percent = false;
                }
                else return v;
            }
        }

        private double Power(out bool percent)
        {
            double v = Unary(out percent);
            Skip();
            if (Take('^'))
            {
                v = Math.Pow(v, Power(out _));
                percent = false;
            }
            return v;
        }

        private double Unary(out bool percent)
        {
            Skip();
            if (Take('-')) return -Unary(out percent);
            if (Take('+')) return Unary(out percent);
            double v = Atom();
            Skip();
            percent = Take('%');
            return v;
        }

        private double Atom()
        {
            Skip();
            if (Take('('))
            {
                double v = Sum();
                Skip();
                if (!Take(')'))
                    throw new FormatException();
                return v;
            }
            if (char.IsDigit(Peek) || Peek == '.')
            {
                int start = _i;
                while (_i < text.Length && (char.IsDigit(text[_i]) || text[_i] == '.'))
                    _i++;
                if (_i < text.Length && text[_i] is 'e' or 'E' && _i + 1 < text.Length && (char.IsDigit(text[_i + 1]) || text[_i + 1] is '-' or '+'))
                {
                    _i += 2;
                    while (_i < text.Length && char.IsDigit(text[_i]))
                        _i++;
                }
                if (!double.TryParse(text[start.._i], NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                    throw new FormatException();
                return number;
            }
            int wordStart = _i;
            while (_i < text.Length && char.IsLetter(text[_i]))
                _i++;
            var name = text[wordStart.._i].ToLowerInvariant();
            switch (name)
            {
                case "pi" or "π": return Math.PI;
                case "e": return Math.E;
            }
            if (name.Length == 0 && Take('π'))
                return Math.PI;
            Func<double, double>? f = name switch
            {
                "sqrt" or "جذر" => Math.Sqrt,
                "sin" => d => Math.Sin(d * Math.PI / 180),
                "cos" => d => Math.Cos(d * Math.PI / 180),
                "tan" => d => Math.Tan(d * Math.PI / 180),
                "log" => Math.Log10,
                "ln" => Math.Log,
                "abs" => Math.Abs,
                "round" => d => Math.Round(d, MidpointRounding.AwayFromZero),
                "floor" => Math.Floor,
                "ceil" => Math.Ceiling,
                _ => null,
            };
            if (f is null)
                throw new FormatException();
            return f(Power(out _));
        }

        private char Peek => _i < text.Length ? text[_i] : '\0';

        private void Skip()
        {
            while (_i < text.Length && char.IsWhiteSpace(text[_i]))
                _i++;
        }

        private bool Take(char c)
        {
            if (Peek != c)
                return false;
            _i++;
            return true;
        }

        private bool Word(string word)
        {
            Skip();
            if (string.Compare(text, _i, word, 0, word.Length, StringComparison.OrdinalIgnoreCase) != 0)
                return false;
            int end = _i + word.Length;
            if (end < text.Length && char.IsLetter(text[end]))
                return false;
            _i = end;
            return true;
        }
    }
}
