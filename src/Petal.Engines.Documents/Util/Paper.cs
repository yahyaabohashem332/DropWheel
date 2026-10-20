using System.Globalization;

namespace Petal.Engines.Documents.Util;

/// <summary>Paper for documents Petal lays out itself: Letter in the US and Canada, A4 elsewhere.</summary>
internal static class Paper
{
    public static bool UseLetter
    {
        get
        {
            try
            {
                var region = RegionInfo.CurrentRegion.TwoLetterISORegionName;
                return region is "US" or "CA" or "MX" or "PH" or "CL" or "CO" or "VE";
            }
            catch (ArgumentException)
            {
                return false;
            }
        }
    }

    /// <summary>Page size in points.</summary>
    public static (double Width, double Height) PageSizePt => UseLetter ? (612, 792) : (595.28, 841.89);

    /// <summary>Margins in points (one inch).</summary>
    public const double MarginPt = 72;

    public static double ContentWidthPt => PageSizePt.Width - 2 * MarginPt;

    public static double ContentHeightPt => PageSizePt.Height - 2 * MarginPt;
}
