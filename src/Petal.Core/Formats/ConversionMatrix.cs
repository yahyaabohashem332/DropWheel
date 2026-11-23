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
    private static readonly FileFormat[] SpreadsheetOutputs = [Xlsx, Ods, Csv, Tsv, Pdf, Json];
    private static readonly FileFormat[] ArchiveOutputs = [Zip, SevenZip, Tgz, Txz, Tbz2, Tar];

    private static readonly FrozenDictionary<FileFormat, FileFormat[]> Targets = Build();

    private static FrozenDictionary<FileFormat, FileFormat[]> Build()
    {
        var map = new Dictionary<FileFormat, FileFormat[]>();

        foreach (var image in RasterImages)
            map[image] = [.. RasterImages.Where(f => f != image), Pdf];
        // Textures, archives and older formats: made from the pictures people keep them as.
        map[Png] = [.. map[Png], .. SpecialImages];
        map[Tiff] = [.. map[Tiff], .. SpecialImages];
        foreach (var special in SpecialImages)
            map[special] = [Png, Jpg, Webp, Jxl, Tiff, Pdf];
        map[Xcf] = [Png, Jpg, Webp, Tiff, Avif, Jxl, Pdf];
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

        // 3D models to each other and to a picture.
        FileFormat[] models = [Glb, Stl, Obj, ThreeMf, Gltf, Ply];
        foreach (var model in models)
            map[model] = [.. models.Where(f => f != model), Png];

        // Drawings on paper (vector PDF and SVG, and PNG); a DWG also to DXF, which free CAD programs open.
        map[Dxf] = [Pdf, Svg, Png];
        map[Dwg] = [Pdf, Dxf, Svg, Png];

        map[Ttf] = [Woff2, Otf, Woff];
        map[Otf] = [Woff2, Ttf, Woff];
        map[Woff] = [Ttf, Otf, Woff2];
        map[Woff2] = [Ttf, Otf, Woff];

        // WMV and WMA are read (Windows decodes them) but not written: only formats DropWheel can write are targets.
        foreach (var audio in AudioFormats)
            map[audio] = [.. AudioFormats.Where(f => f != audio && f.CanWrite)];

        foreach (var video in VideoFormats)
            map[video] = [.. VideoFormats.Where(f => f != video && f.CanWrite), Gif, Mp3];
        map[Gif] = [.. VideoFormats.Where(f => f.CanWrite)];

        map[Pdf] = [Docx, Odt, Pptx, Xlsx, Jpg, Png, PdfLongJpg, PdfFirstJpg, PdfLongPng, PdfFirstPng, Epub, Txt, Md, Html, Cbz];

        // A presentation becomes a show or a template, and they and presentations with macros a plain one again.
        map[Pptx] = [Pdf, Jpg, Png, Ppsx, Potx];
        map[Ppt] = [Pdf, Jpg, Png];
        foreach (var slides in new[] { Ppsx, Potx, Pptm })
            map[slides] = [Pptx, Pdf, Jpg, Png];

        foreach (var sheet in new[] { Xlsx, Xls, Ods, Csv, Tsv, Xlsm, Xltx })
            map[sheet] = [.. SpreadsheetOutputs.Where(f => f != sheet)];
        map[Xlsx] = [.. map[Xlsx], Xltx];

        foreach (var doc in new[] { Docx, Odt, Rtf, Md, Html, Doc, Docm, Dotx, Mhtml, Rst })
            map[doc] = [.. DocumentOutputs.Where(f => f != doc)];
        // A document becomes a template; Markdown, web pages and Word documents become reStructuredText.
        map[Docx] = [.. map[Docx], Dotx, Rst];
        map[Md] = [.. map[Md], Rst];
        map[Html] = [.. map[Html], Rst];
        map[Txt] = [Docx, Odt, Rtf, Pdf, Md, Html, Epub, Jpg, Png];
        map[Epub] = [Pdf, Docx, Odt, Txt, Md, Html, Kepub];
        // Kindle and Kobo books: to EPUB first, and to the document formats.
        foreach (var book in new[] { Mobi, Azw3, Kepub })
            map[book] = [Epub, Pdf, Docx, Odt, Txt, Md, Html];
        // Comic books: to a PDF or a fixed-layout EPUB of their pages; RAR's and 7z's to ZIP's.
        map[Cbz] = [Pdf, Epub];
        map[Cbr] = [Cbz, Pdf, Epub];
        map[Cb7] = [Cbz, Pdf, Epub];

        // Data: a tree of values in another notation, or a table of records.
        map[Json] = [Yaml, Toml, Xml, Csv, Xlsx, Jsonl];
        map[Jsonl] = [Json, Csv, Xlsx];
        map[Yaml] = [Json, Toml, Xml, Csv, Xlsx];
        map[Toml] = [Json, Yaml, Xml];
        map[Xml] = [Json, Yaml];
        map[Vcf] = [Csv, Xlsx, Json];

        // Every subtitle format to every other, and to the words alone.
        FileFormat[] subtitles = [Srt, Vtt, Ass, Ttml, Sami, Ssa, MicroDvd, Lrc];
        foreach (var subtitle in subtitles)
            map[subtitle] = [.. subtitles.Where(f => f != subtitle), Txt];

        foreach (var archive in new[] { Zip, Tar, Tgz, Rar, SevenZip, Tbz2, Txz, Cab, Iso })
            map[archive] = [.. ArchiveOutputs.Where(f => f != archive)];
        // A disc image or a Windows cabinet is made from a ZIP.
        map[Zip] = [.. map[Zip], Iso, Cab];

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
