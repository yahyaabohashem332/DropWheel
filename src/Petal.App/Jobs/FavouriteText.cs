using Petal.App.Localization;
using Petal.Core.Formats;
using Petal.Core.Jobs;

namespace Petal.App.Jobs;

/// <summary>How favourites are named on the wheel, in Settings and in file names.</summary>
public static class FavouriteText
{
    private static readonly int[] PictureSizes = [0, 3840, 2560, 2048, 1600, 1280, 1024, 800, 512];

    private static readonly int[] VideoSizes = [0, 3840, 2560, 1920, 1280, 854];

    public static readonly (int Value, string Label)[] PictureQualities =
        [(0, "As in Settings"), (95, "95"), (90, "90"), (85, "85"), (80, "80"), (70, "70"), (60, "60"), (50, "50")];

    public static readonly (int Value, string Label)[] VideoQualities =
        [(0, "As in Settings"), (17, "Best"), (20, "High"), (24, "Balanced"), (28, "Small")];

    private static readonly int[] AudioBitrates = [320, 256, 192, 128, 96];

    /// <summary>Formats a favourite can save to, in the order Petal lists formats.</summary>
    public static IReadOnlyList<FileFormat> Targets { get; } =
        Formats.All.Where(f => f.CanWrite && ConversionMatrix.AllPaths().Any(p => p.To == f)).ToList();

    public static (int Value, string Label)[] SizesFor(FileFormat target) =>
        (target.Family == FormatFamily.Video ? VideoSizes : PictureSizes)
            .Select(v => (v, v == 0 ? L.T("Keep the size") : SizeName(target, v)))
            .ToArray();

    public static (int Value, string Label)[] QualitiesFor(FileFormat target) => Favourite.QualityKindOf(target) switch
    {
        QualityKind.Picture => PictureQualities,
        QualityKind.Video => VideoQualities,
        QualityKind.Audio => [(0, "As in Settings"), .. AudioBitrates.Select(b => (b, L.F("{0} kbps", b)))],
        _ => [],
    };

    /// <summary>What the petal says: the user's name, or e.g. "WEBP 1600", "MP4 720p", "MP3 128k".</summary>
    public static string Label(Favourite f)
    {
        if (!string.IsNullOrWhiteSpace(f.Name))
            return f.Name.Trim();
        var label = f.Target?.Label ?? f.Format.ToUpperInvariant();
        return Short(f) is { Length: > 0 } detail ? $"{label} {detail}" : label;
    }

    /// <summary>
    /// Between the parts of a description. In Arabic a right-to-left mark keeps "WEBP" and "1600"
    /// from joining into one Latin run that would read backwards.
    /// </summary>
    public static string Separator => L.IsRightToLeft ? "\u200F · " : " · ";

    /// <summary>The settings in words: "WEBP · 1600 px · quality 80".</summary>
    public static string Describe(Favourite f)
    {
        var parts = new List<string> { f.Target?.Label ?? f.Format.ToUpperInvariant() };
        if (f.Target is { } target)
        {
            if (f.SizeApplies && f.LongestSide > 0)
                parts.Add(SizeName(target, f.LongestSide));
            if (f.QualityApplies && f.Quality > 0)
                parts.Add(QualityName(target, f.Quality));
        }
        return string.Join(Separator, parts);
    }

    /// <summary>Added to the file name when a file is saved again in its own format ("photo 1600px.jpg").</summary>
    public static string Suffix(Favourite f)
    {
        var text = !string.IsNullOrWhiteSpace(f.Name) ? f.Name.Trim() : Short(f).Replace(" ", "");
        if (text.Length == 0)
            text = L.T("Favourite");
        foreach (var c in System.IO.Path.GetInvalidFileNameChars())
            text = text.Replace(c, '-');
        return text;
    }

    private static string Short(Favourite f)
    {
        if (f.Target is not { } target)
            return "";
        if (f.SizeApplies && f.LongestSide > 0)
            return target.Family == FormatFamily.Video ? VideoSize(f.LongestSide) ?? $"{f.LongestSide}px" : $"{f.LongestSide}";
        if (f.QualityApplies && f.Quality > 0)
        {
            return Favourite.QualityKindOf(target) switch
            {
                QualityKind.Picture => $"{f.Quality}%",
                QualityKind.Audio => $"{f.Quality}k",
                _ => L.T(Named(VideoQualities, f.Quality) ?? "Custom").ToLowerInvariant(),
            };
        }
        return "";
    }

    private static string SizeName(FileFormat target, int longestSide) =>
        target.Family == FormatFamily.Video && VideoSize(longestSide) is { } name ? name : L.F("{0} px", longestSide);

    private static string QualityName(FileFormat target, int quality) => Favourite.QualityKindOf(target) switch
    {
        QualityKind.Picture => L.F("quality {0}", quality),
        QualityKind.Audio => L.F("{0} kbps", quality),
        _ => L.F("{0} quality", L.T(Named(VideoQualities, quality) ?? "Custom")),
    };

    private static string? VideoSize(int longestSide) => longestSide switch
    {
        3840 => "4K",
        2560 => "1440p",
        1920 => "1080p",
        1280 => "720p",
        854 => "480p",
        _ => null,
    };

    private static string? Named((int Value, string Label)[] options, int value) =>
        options.FirstOrDefault(o => o.Value == value).Label;
}
