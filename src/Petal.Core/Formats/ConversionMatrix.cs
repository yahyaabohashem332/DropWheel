using System.Collections.Frozen;
using static Petal.Core.Formats.Formats;

namespace Petal.Core.Formats;

/// <summary>
/// Which formats each format can be converted into. The order of each list is the
/// clockwise order of the petals on the conversion wheel, starting at the top.
/// </summary>
public static class ConversionMatrix
{
    private static readonly FileFormat[] DocumentOutputs = [Docx, Odt, Rtf, Pdf, Txt, Md, Html, Epub, Png, Jpg];
    private static readonly FileFormat[] SpreadsheetOutputs = [Xlsx, Ods, Csv, Tsv, Pdf];
    private static readonly FileFormat[] ArchiveOutputs = [Zip, Tar, Tgz];

    private static readonly FrozenDictionary<FileFormat, FileFormat[]> Targets = Build();

    private static FrozenDictionary<FileFormat, FileFormat[]> Build()
    {
        var map = new Dictionary<FileFormat, FileFormat[]>();

        foreach (var image in RasterImages)
            map[image] = [.. RasterImages.Where(f => f != image), Pdf];
        map[Svg] = [.. RasterImages, Pdf];
        map[Psd] = [Png, Jpg, Webp, Tiff, Avif, Heic, Bmp, Ico, Pdf];
        map[Psb] = [Png, Jpg, Webp, Tiff, Avif, Heic, Bmp, Ico, Pdf];
        map[Raw] = [Jpg, Heic, Tiff, Png, Webp, Avif, Pdf];
        map[Ai] = [Pdf, Png, Jpg, Webp, Tiff, Avif, Heic, Bmp, Ico];
        map[Eps] = [Pdf, Png, Jpg, Webp, Tiff, Avif, Heic, Bmp, Ico];

        // InDesign's interchange format: its text, as a document.
        map[Idml] = [Docx, Odt, Txt, Md, Html];

        map[Ase] = [Aco, Png];
        map[Aco] = [Ase, Png];

        foreach (var apple in new[] { Pages, Numbers, Keynote })
            map[apple] = [Pdf, Txt, Jpg];

        map[Ttf] = [Woff2, Otf, Woff];
        map[Otf] = [Woff2, Ttf, Woff];
        map[Woff] = [Ttf, Otf, Woff2];
        map[Woff2] = [Ttf, Otf, Woff];

        foreach (var audio in AudioFormats)
            map[audio] = [.. AudioFormats.Where(f => f != audio)];

        foreach (var video in VideoFormats)
            map[video] = [.. VideoFormats.Where(f => f != video), Gif, Mp3];
        map[Gif] = [.. VideoFormats];

        map[Pdf] = [Docx, Odt, Pptx, Xlsx, Jpg, Png, Epub, Txt, Md, Html];

        map[Pptx] = [Pdf, Jpg, Png];
        map[Ppt] = [Pdf, Jpg, Png];

        foreach (var sheet in new[] { Xlsx, Xls, Ods, Csv, Tsv })
            map[sheet] = [.. SpreadsheetOutputs.Where(f => f != sheet)];

        foreach (var doc in new[] { Docx, Odt, Rtf, Md, Html })
            map[doc] = [.. DocumentOutputs.Where(f => f != doc)];
        map[Txt] = [Docx, Odt, Rtf, Pdf, Md, Html, Epub, Jpg, Png];
        map[Epub] = [Pdf, Docx, Odt, Txt, Md, Html];

        map[Srt] = [Vtt, Txt];
        map[Vtt] = [Srt, Txt];

        foreach (var archive in new[] { Zip, Tar, Tgz, Rar, SevenZip })
            map[archive] = [.. ArchiveOutputs.Where(f => f != archive)];

        return map.ToFrozenDictionary();
    }

    public static IReadOnlyList<FileFormat> TargetsFor(FileFormat source) =>
        Targets.TryGetValue(source, out var list) ? list : [];

    /// <summary>
    /// Targets valid for every file in a selection. Mixed formats only share targets
    /// when they belong to compatible families, e.g. JPG and PNG can both become WEBP.
    /// </summary>
    public static IReadOnlyList<FileFormat> TargetsFor(IReadOnlyCollection<FileFormat> sources)
    {
        if (sources.Count == 0)
            return [];
        var distinct = sources.Distinct().ToList();
        if (distinct.Count == 1)
            return TargetsFor(distinct[0]);

        // For mixed selections keep the order of the first format's list but only shared items.
        // A target equal to one of the sources is fine for the others, so build the union order first.
        var order = distinct.SelectMany(TargetsFor).Concat(distinct).Distinct().ToList();
        return order
            .Where(t => distinct.All(s => s == t || TargetsFor(s).Contains(t)))
            .Where(t => !distinct.All(s => s == t))
            .ToList();
    }

    public static bool CanConvert(FileFormat from, FileFormat to) => TargetsFor(from).Contains(to);

    /// <summary>Every source → target pair.</summary>
    public static IEnumerable<(FileFormat From, FileFormat To)> AllPaths() =>
        Targets.SelectMany(kv => kv.Value.Select(t => (kv.Key, t)));

    public static int TotalPaths => Targets.Sum(kv => kv.Value.Length);
}
