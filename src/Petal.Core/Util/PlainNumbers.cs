using System.Globalization;

namespace Petal.Core.Util;

/// <summary>
/// Numbers written the same way on every PC. Petal builds WPF drawings, FFmpeg arguments and file contents
/// out of numbers, and reads numbers back from programs' output; a region that writes 3.4 as "3,4" (German,
/// French, Turkish, Indonesian, Spanish and many more), or -1 with a Unicode minus sign (Swedish, Finnish,
/// Persian…), breaks all of them, the wheel included: on such a PC DropWheel didn't start. The region's
/// dates, times, sorting, letter case and code page stay as they are; only how numbers are written changes.
/// </summary>
public static class PlainNumbers
{
    /// <summary>
    /// A copy of <paramref name="culture"/> that writes and reads numbers with a full stop for the decimal
    /// point, a comma between thousands and the ASCII plus and minus signs.
    /// </summary>
    public static CultureInfo Of(CultureInfo culture)
    {
        var copy = (CultureInfo)culture.Clone();
        var n = copy.NumberFormat;
        n.NumberDecimalSeparator = n.CurrencyDecimalSeparator = n.PercentDecimalSeparator = ".";
        n.NumberGroupSeparator = n.CurrencyGroupSeparator = n.PercentGroupSeparator = ",";
        n.NegativeSign = "-";
        n.PositiveSign = "+";
        return copy;
    }

    /// <summary>
    /// Makes the PC's culture write plain numbers for this thread and every thread started after it.
    /// Called first thing at start-up.
    /// </summary>
    public static void Apply()
    {
        var plain = Of(CultureInfo.CurrentCulture);
        CultureInfo.DefaultThreadCurrentCulture = plain;
        CultureInfo.CurrentCulture = plain;
    }
}
