using System.Globalization;
using System.Text.RegularExpressions;

namespace Petal.Engines.Media.Internal;

/// <summary>Culture-independent number and colour formatting for FFmpeg arguments.</summary>
internal static partial class Inv
{
    public static string N(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);

    public static string I(long value) => value.ToString(CultureInfo.InvariantCulture);

    public static double ParseDouble(string? text, double fallback = 0) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v) ? v : fallback;

    public static long ParseLong(string? text, long fallback = 0) =>
        long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    /// <summary>Parses "30000/1001" or "25".</summary>
    public static double ParseRational(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return 0;
        var slash = text.IndexOf('/');
        if (slash < 0)
            return ParseDouble(text);
        var num = ParseDouble(text[..slash]);
        var den = ParseDouble(text[(slash + 1)..]);
        return den == 0 ? 0 : num / den;
    }

    /// <summary>Turns "#RRGGBB" or "#RRGGBBAA" into FFmpeg's "0xRRGGBB@alpha".</summary>
    public static string Color(string hex)
    {
        var m = HexColor().Match(hex.Trim());
        if (!m.Success)
            throw new ArgumentException($"'{hex}' is not a #RRGGBB colour", nameof(hex));
        var rgb = m.Groups[1].Value;
        if (m.Groups[2].Success)
        {
            var alpha = int.Parse(m.Groups[2].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255.0;
            return $"0x{rgb}@{N(Math.Round(alpha, 3))}";
        }
        return $"0x{rgb}";
    }

    [GeneratedRegex("^#?([0-9A-Fa-f]{6})([0-9A-Fa-f]{2})?$")]
    private static partial Regex HexColor();
}
