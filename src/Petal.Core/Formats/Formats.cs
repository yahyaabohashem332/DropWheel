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
    public static readonly FileFormat Svg = new("svg", "SVG", FormatFamily.Vector, ["svg"], CanWrite: false);

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
    public static readonly FileFormat Wma = new("wma", "WMA", FormatFamily.Audio, ["wma"]);

    // Video (GIF behaves like a video: it converts to and from video formats)
    public static readonly FileFormat Mp4 = new("mp4", "MP4", FormatFamily.Video, ["mp4", "m4v"]);
    public static readonly FileFormat Mov = new("mov", "MOV", FormatFamily.Video, ["mov", "qt"]);
    public static readonly FileFormat Mkv = new("mkv", "MKV", FormatFamily.Video, ["mkv"]);
    public static readonly FileFormat Webm = new("webm", "WEBM", FormatFamily.Video, ["webm"]);
    public static readonly FileFormat Avi = new("avi", "AVI", FormatFamily.Video, ["avi"]);
    public static readonly FileFormat Wmv = new("wmv", "WMV", FormatFamily.Video, ["wmv"]);
    public static readonly FileFormat Gif = new("gif", "GIF", FormatFamily.Video, ["gif"]);

    // Documents
    public static readonly FileFormat Pdf = new("pdf", "PDF", FormatFamily.Pdf, ["pdf"]);
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

    // Subtitles
    public static readonly FileFormat Srt = new("srt", "SRT", FormatFamily.Subtitle, ["srt"]);
    public static readonly FileFormat Vtt = new("vtt", "VTT", FormatFamily.Subtitle, ["vtt"]);

    // Archives (".tar.gz" and ".tgz" are both the TGZ format)
    public static readonly FileFormat Zip = new("zip", "ZIP", FormatFamily.Archive, ["zip"]);
    public static readonly FileFormat Tar = new("tar", "TAR", FormatFamily.Archive, ["tar"]);
    public static readonly FileFormat Tgz = new("tgz", "TAR.GZ", FormatFamily.Archive, ["tar.gz", "tgz", "gz"]);
    public static readonly FileFormat Rar = new("rar", "RAR", FormatFamily.Archive, ["rar"], CanWrite: false);
    public static readonly FileFormat SevenZip = new("7z", "7Z", FormatFamily.Archive, ["7z"], CanWrite: false);

    public static readonly IReadOnlyList<FileFormat> All =
    [
        Jpg, Png, Webp, Heic, Tiff, Avif, Bmp, Ico, Svg,
        Psd, Psb, Raw, Ai, Eps,
        Indd, Idml, Xd, Prproj, Aep,
        Pages, Numbers, Keynote,
        Ase, Aco,
        Ttf, Otf, Woff, Woff2,
        Mp3, M4a, Wav, Flac, Ogg, Opus, Aiff, Wma,
        Mp4, Mov, Mkv, Webm, Avi, Wmv, Gif,
        Pdf, Docx, Odt, Rtf, Md, Html, Txt, Epub,
        Pptx, Ppt,
        Xlsx, Xls, Ods, Csv, Tsv,
        Srt, Vtt,
        Zip, Tar, Tgz, Rar, SevenZip,
    ];

    public static readonly IReadOnlyList<FileFormat> RasterImages = [Jpg, Png, Webp, Heic, Tiff, Avif, Bmp, Ico];
    public static readonly IReadOnlyList<FileFormat> AudioFormats = [Mp3, M4a, Wav, Flac, Ogg, Opus, Aiff, Wma];
    public static readonly IReadOnlyList<FileFormat> VideoFormats = [Mp4, Mov, Mkv, Webm, Avi, Wmv];

    private static readonly FrozenDictionary<string, FileFormat> ById = All.ToFrozenDictionary(f => f.Id, StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenDictionary<string, FileFormat> ByExtension = All
        .SelectMany(f => f.Extensions.Select(e => (e, f)))
        .ToFrozenDictionary(p => p.e, p => p.f, StringComparer.OrdinalIgnoreCase);

    public static FileFormat? FromId(string id) => ById.GetValueOrDefault(id);

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
