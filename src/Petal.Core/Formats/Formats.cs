using System.Collections.Frozen;

namespace Petal.Core.Formats;

/// <summary>Every format Petal supports, plus lookup by file extension.</summary>
public static class Formats
{
    // Raster images
    public static readonly FileFormat Jpg = new("jpg", "JPG", FormatFamily.Image, ["jpg", "jpeg", "jfif", "jpe"]);
    public static readonly FileFormat Png = new("png", "PNG", FormatFamily.Image, ["png"]);
    public static readonly FileFormat Webp = new("webp", "WEBP", FormatFamily.Image, ["webp"]);
    public static readonly FileFormat Heic = new("heic", "HEIC", FormatFamily.Image, ["heic", "heif"]);
    public static readonly FileFormat Tiff = new("tiff", "TIFF", FormatFamily.Image, ["tiff", "tif"]);
    public static readonly FileFormat Avif = new("avif", "AVIF", FormatFamily.Image, ["avif"]);
    public static readonly FileFormat Bmp = new("bmp", "BMP", FormatFamily.Image, ["bmp", "dib"]);
    public static readonly FileFormat Ico = new("ico", "ICO", FormatFamily.Image, ["ico"]);
    public static readonly FileFormat Jxl = new("jxl", "JXL", FormatFamily.Image, ["jxl"]);
    public static readonly FileFormat Svg = new("svg", "SVG", FormatFamily.Vector, ["svg"], CanWrite: false);

    // Pictures for games, print and older programs: read, and written from PNG and TIFF. (EXR waits for a build of
    // ImageMagick with more than 8 bits a channel: its light can't be kept in 8.)
    public static readonly FileFormat Tga = new("tga", "TGA", FormatFamily.Image, ["tga", "icb", "vda", "vst"]);
    public static readonly FileFormat Dds = new("dds", "DDS", FormatFamily.Image, ["dds"]);
    public static readonly FileFormat Jp2 = new("jp2", "JP2", FormatFamily.Image, ["jp2", "j2k", "j2c", "jpf", "jpx"]);
    public static readonly FileFormat Qoi = new("qoi", "QOI", FormatFamily.Image, ["qoi"]);
    public static readonly FileFormat Pcx = new("pcx", "PCX", FormatFamily.Image, ["pcx"]);
    // GIMP's own files: their layers, as one picture; never written.
    public static readonly FileFormat Xcf = new("xcf", "XCF", FormatFamily.Vector, ["xcf"], CanWrite: false);

    // Design files and camera originals: read like pictures, never written.
    public static readonly FileFormat Psd = new("psd", "PSD", FormatFamily.Vector, ["psd"], CanWrite: false);
    public static readonly FileFormat Psb = new("psb", "PSB", FormatFamily.Vector, ["psb"], CanWrite: false);
    public static readonly FileFormat Raw = new("raw", "RAW", FormatFamily.Vector,
        ["dng", "cr2", "cr3", "crw", "nef", "nrw", "arw", "srf", "sr2", "raf", "orf", "rw2", "rwl", "pef", "srw", "3fr", "iiq", "erf", "kdc", "mrw", "mos", "x3f"], CanWrite: false);
    public static readonly FileFormat Ai = new("ai", "AI", FormatFamily.Vector, ["ai"], CanWrite: false);
    public static readonly FileFormat Eps = new("eps", "EPS", FormatFamily.Vector, ["eps", "epsf", "epsi"], CanWrite: false);

    // Projects
    public static readonly FileFormat Indd = new("indd", "INDD", FormatFamily.Project, ["indd", "indt"], CanWrite: false);
    public static readonly FileFormat Idml = new("idml", "IDML", FormatFamily.Project, ["idml"], CanWrite: false);
    public static readonly FileFormat Xd = new("xd", "XD", FormatFamily.Project, ["xd"], CanWrite: false);
    public static readonly FileFormat Prproj = new("prproj", "PRPROJ", FormatFamily.Project, ["prproj"], CanWrite: false);
    public static readonly FileFormat Aep = new("aep", "AEP", FormatFamily.Project, ["aep"], CanWrite: false);

    // Colour swatches
    public static readonly FileFormat Ase = new("ase", "ASE", FormatFamily.Palette, ["ase"]);
    public static readonly FileFormat Aco = new("aco", "ACO", FormatFamily.Palette, ["aco"]);

    // Apple's documents, from an iPhone or a Mac: read, never written.
    public static readonly FileFormat Pages = new("pages", "PAGES", FormatFamily.Project, ["pages"], CanWrite: false);
    public static readonly FileFormat Numbers = new("numbers", "NUMBERS", FormatFamily.Project, ["numbers"], CanWrite: false);
    public static readonly FileFormat Keynote = new("key", "KEYNOTE", FormatFamily.Project, ["key"], CanWrite: false);

    public static readonly FileFormat Ttf = new("ttf", "TTF", FormatFamily.Font, ["ttf"]);
    public static readonly FileFormat Otf = new("otf", "OTF", FormatFamily.Font, ["otf"]);
    public static readonly FileFormat Woff = new("woff", "WOFF", FormatFamily.Font, ["woff"]);
    public static readonly FileFormat Woff2 = new("woff2", "WOFF2", FormatFamily.Font, ["woff2"]);

    // Audio
    public static readonly FileFormat Mp3 = new("mp3", "MP3", FormatFamily.Audio, ["mp3"]);
    public static readonly FileFormat M4a = new("m4a", "M4A", FormatFamily.Audio, ["m4a", "aac"]);
    public static readonly FileFormat Wav = new("wav", "WAV", FormatFamily.Audio, ["wav", "wave"]);
    public static readonly FileFormat Flac = new("flac", "FLAC", FormatFamily.Audio, ["flac"]);
    public static readonly FileFormat Ogg = new("ogg", "OGG", FormatFamily.Audio, ["ogg", "oga"]);
    public static readonly FileFormat Opus = new("opus", "OPUS", FormatFamily.Audio, ["opus"]);
    public static readonly FileFormat Aiff = new("aiff", "AIFF", FormatFamily.Audio, ["aiff", "aif", "aifc"]);
    // Read through Windows' own decoders; not written, as their encoders aren't free to ship.
    public static readonly FileFormat Wma = new("wma", "WMA", FormatFamily.Audio, ["wma"], CanWrite: false);

    // Video (GIF behaves like a video: it converts to and from video formats)
    public static readonly FileFormat Mp4 = new("mp4", "MP4", FormatFamily.Video, ["mp4", "m4v"]);
    public static readonly FileFormat Mov = new("mov", "MOV", FormatFamily.Video, ["mov", "qt"]);
    public static readonly FileFormat Mkv = new("mkv", "MKV", FormatFamily.Video, ["mkv"]);
    public static readonly FileFormat Webm = new("webm", "WEBM", FormatFamily.Video, ["webm"]);
    public static readonly FileFormat Avi = new("avi", "AVI", FormatFamily.Video, ["avi"]);
    public static readonly FileFormat Wmv = new("wmv", "WMV", FormatFamily.Video, ["wmv"], CanWrite: false);
    public static readonly FileFormat Gif = new("gif", "GIF", FormatFamily.Video, ["gif"]);

    // Documents
    public static readonly FileFormat Pdf = new("pdf", "PDF", FormatFamily.Pdf, ["pdf"]);

    // A PDF as pictures in two more ways: every page one under another in one long picture, or the first page
    // alone. They write PNG or JPG files but are targets of their own on the wheel; with no extensions of their
    // own a file on disk is always PNG or JPG, never one of these.
    public static readonly FileFormat PdfLongPng = new("pdflongpng", "PNG (long)", FormatFamily.Image, [], CanRead: false, Writes: "png");
    public static readonly FileFormat PdfFirstPng = new("pdffirstpng", "PNG (first page)", FormatFamily.Image, [], CanRead: false, Writes: "png");
    public static readonly FileFormat PdfLongJpg = new("pdflongjpg", "JPG (long)", FormatFamily.Image, [], CanRead: false, Writes: "jpg");
    public static readonly FileFormat PdfFirstJpg = new("pdffirstjpg", "JPG (first page)", FormatFamily.Image, [], CanRead: false, Writes: "jpg");

    public static readonly FileFormat Docx = new("docx", "DOCX", FormatFamily.Document, ["docx"]);
    public static readonly FileFormat Odt = new("odt", "ODT", FormatFamily.Document, ["odt"]);
    public static readonly FileFormat Rtf = new("rtf", "RTF", FormatFamily.Document, ["rtf"]);
    public static readonly FileFormat Md = new("md", "MD", FormatFamily.Document, ["md", "markdown"]);
    public static readonly FileFormat Html = new("html", "HTML", FormatFamily.Document, ["html", "htm", "xhtml"]);
    public static readonly FileFormat Txt = new("txt", "TXT", FormatFamily.Text, ["txt", "text", "log"]);
    public static readonly FileFormat Epub = new("epub", "EPUB", FormatFamily.Ebook, ["epub"]);

    // Slides
    public static readonly FileFormat Pptx = new("pptx", "PPTX", FormatFamily.Slides, ["pptx"]);
    public static readonly FileFormat Ppt = new("ppt", "PPT", FormatFamily.Slides, ["ppt"], CanWrite: false);

    // Spreadsheets
    public static readonly FileFormat Xlsx = new("xlsx", "XLSX", FormatFamily.Spreadsheet, ["xlsx", "xlsm"]);
    public static readonly FileFormat Xls = new("xls", "XLS", FormatFamily.Spreadsheet, ["xls"], CanWrite: false);
    public static readonly FileFormat Ods = new("ods", "ODS", FormatFamily.Spreadsheet, ["ods"]);
    public static readonly FileFormat Csv = new("csv", "CSV", FormatFamily.Spreadsheet, ["csv"]);
    public static readonly FileFormat Tsv = new("tsv", "TSV", FormatFamily.Spreadsheet, ["tsv", "tab"]);

    // Data: converted to each other through one tree of values, and to and from tables.
    public static readonly FileFormat Json = new("json", "JSON", FormatFamily.Data, ["json"]);
    public static readonly FileFormat Jsonl = new("jsonl", "JSONL", FormatFamily.Data, ["jsonl", "ndjson"]);
    public static readonly FileFormat Yaml = new("yaml", "YAML", FormatFamily.Data, ["yaml", "yml"]);
    public static readonly FileFormat Toml = new("toml", "TOML", FormatFamily.Data, ["toml"]);
    public static readonly FileFormat Xml = new("xml", "XML", FormatFamily.Data, ["xml"]);
    // Contacts from a phone or an address book: read, and made into tables and JSON.
    public static readonly FileFormat Vcf = new("vcf", "VCF", FormatFamily.Data, ["vcf", "vcard"], CanWrite: false);

    // Subtitles
    public static readonly FileFormat Srt = new("srt", "SRT", FormatFamily.Subtitle, ["srt"]);
    public static readonly FileFormat Vtt = new("vtt", "VTT", FormatFamily.Subtitle, ["vtt"]);
    public static readonly FileFormat Ass = new("ass", "ASS", FormatFamily.Subtitle, ["ass"]);
    public static readonly FileFormat Ssa = new("ssa", "SSA", FormatFamily.Subtitle, ["ssa"]);
    public static readonly FileFormat Ttml = new("ttml", "TTML", FormatFamily.Subtitle, ["ttml", "dfxp"]);
    public static readonly FileFormat Sami = new("smi", "SAMI", FormatFamily.Subtitle, ["smi", "sami"]);
    // MicroDVD's text subtitles. A VobSub .sub (pictures, with an .idx) is told apart when it is read, and refused.
    public static readonly FileFormat MicroDvd = new("sub", "SUB", FormatFamily.Subtitle, ["sub"]);
    // Timed lyrics.
    public static readonly FileFormat Lrc = new("lrc", "LRC", FormatFamily.Subtitle, ["lrc"]);

    // Archives (".tar.gz" and ".tgz" are both the TGZ format)
    public static readonly FileFormat Zip = new("zip", "ZIP", FormatFamily.Archive, ["zip"]);
    public static readonly FileFormat Tar = new("tar", "TAR", FormatFamily.Archive, ["tar"]);
    public static readonly FileFormat Tgz = new("tgz", "TAR.GZ", FormatFamily.Archive, ["tar.gz", "tgz", "gz"]);
    public static readonly FileFormat Rar = new("rar", "RAR", FormatFamily.Archive, ["rar"], CanWrite: false);
    public static readonly FileFormat SevenZip = new("7z", "7Z", FormatFamily.Archive, ["7z"], CanWrite: false);

    public static readonly IReadOnlyList<FileFormat> All =
    [
        Jpg, Png, Webp, Heic, Tiff, Avif, Bmp, Ico, Jxl, Svg,
        Tga, Dds, Jp2, Qoi, Pcx,
        Psd, Psb, Raw, Ai, Eps, Xcf,
        Indd, Idml, Xd, Prproj, Aep,
        Pages, Numbers, Keynote,
        Ase, Aco,
        Ttf, Otf, Woff, Woff2,
        Mp3, M4a, Wav, Flac, Ogg, Opus, Aiff, Wma,
        Mp4, Mov, Mkv, Webm, Avi, Wmv, Gif,
        Pdf, PdfLongPng, PdfFirstPng, PdfLongJpg, PdfFirstJpg,
        Docx, Odt, Rtf, Md, Html, Txt, Epub,
        Pptx, Ppt,
        Xlsx, Xls, Ods, Csv, Tsv,
        Json, Jsonl, Yaml, Toml, Xml, Vcf,
        Srt, Vtt, Ass, Ssa, Ttml, Sami, MicroDvd, Lrc,
        Zip, Tar, Tgz, Rar, SevenZip,
    ];

    public static readonly IReadOnlyList<FileFormat> RasterImages = [Jpg, Png, Webp, Heic, Tiff, Avif, Bmp, Ico, Jxl];

    /// <summary>Pictures for particular uses (games, print, older programs): written only from PNG and TIFF.</summary>
    public static readonly IReadOnlyList<FileFormat> SpecialImages = [Tga, Dds, Jp2, Qoi, Pcx];
    public static readonly IReadOnlyList<FileFormat> AudioFormats = [Mp3, M4a, Wav, Flac, Ogg, Opus, Aiff, Wma];
    public static readonly IReadOnlyList<FileFormat> VideoFormats = [Mp4, Mov, Mkv, Webm, Avi, Wmv];

    private static readonly FrozenDictionary<string, FileFormat> ById = All.ToFrozenDictionary(f => f.Id, StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenDictionary<string, FileFormat> ByExtension = All
        .SelectMany(f => f.Extensions.Select(e => (e, f)))
        .ToFrozenDictionary(p => p.e, p => p.f, StringComparer.OrdinalIgnoreCase);

    public static FileFormat? FromId(string id) => ById.GetValueOrDefault(id);

    /// <summary>The format of the file a target writes: PNG for "PNG (long)", the target itself otherwise.</summary>
    public static FileFormat Written(FileFormat target) =>
        target.Writes is { } extension ? ByExtension.GetValueOrDefault(extension) ?? target : target;

    /// <summary>Detects the format from a path, understanding double extensions like ".tar.gz".</summary>
    public static FileFormat? FromPath(string path)
    {
        if (Directory.Exists(path))
            return null;
        var name = Path.GetFileName(path);
        if (name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
            return Tgz;
        var ext = Path.GetExtension(name);
        return ext.Length > 1 ? ByExtension.GetValueOrDefault(ext[1..]) : null;
    }

    /// <summary>The file name without its format extension (handles ".tar.gz").</summary>
    public static string BaseName(string path)
    {
        var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
            return name[..^7];
        return Path.GetFileNameWithoutExtension(name);
    }
}
