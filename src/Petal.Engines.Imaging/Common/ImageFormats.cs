using ImageMagick;
using Petal.Core.Formats;
using F = Petal.Core.Formats.Formats;

namespace Petal.Engines.Imaging;

/// <summary>Mapping between Petal formats and ImageMagick formats, plus content sniffing.</summary>
internal static class ImageFormats
{
    /// <summary>Formats this engine can decode (GIF is decoded here for tools even though it converts as video).</summary>
    public static bool CanRead(FileFormat format) =>
        format.Family is FormatFamily.Image or FormatFamily.Vector || format == F.Gif;

    /// <summary>Formats this engine can encode.</summary>
    public static bool CanWrite(FileFormat format) =>
        format.Family == FormatFamily.Image || format == F.Pdf || format == F.Gif;

    public static MagickFormat ToMagick(FileFormat format) => format.Id switch
    {
        "jpg" => MagickFormat.Jpeg,
        "png" => MagickFormat.Png,
        "webp" => MagickFormat.WebP,
        "heic" => MagickFormat.Heic,
        "tiff" => MagickFormat.Tiff,
        "avif" => MagickFormat.Avif,
        "bmp" => MagickFormat.Bmp,
        "ico" => MagickFormat.Ico,
        "svg" => MagickFormat.Svg,
        "psd" => MagickFormat.Psd,
        "psb" => MagickFormat.Psb,
        "raw" => MagickFormat.Dng,
        "gif" => MagickFormat.Gif,
        "pdf" => MagickFormat.Pdf,
        "jxl" => MagickFormat.Jxl,
        "tga" => MagickFormat.Tga,
        "dds" => MagickFormat.Dds,
        "jp2" => MagickFormat.Jp2,
        "qoi" => MagickFormat.Qoi,
        "pcx" => MagickFormat.Pcx,
        "xcf" => MagickFormat.Xcf,
        _ => throw new NotSupportedException($"{format.Label} is not an image format"),
    };

    public static FileFormat? FromMagick(MagickFormat format) => format switch
    {
        MagickFormat.Jpeg or MagickFormat.Jpg or MagickFormat.Jpe or MagickFormat.Pjpeg => F.Jpg,
        MagickFormat.Png or MagickFormat.Png8 or MagickFormat.Png24 or MagickFormat.Png32 or MagickFormat.Png48 or MagickFormat.Png64 or MagickFormat.Png00 => F.Png,
        MagickFormat.WebP => F.Webp,
        MagickFormat.Heic or MagickFormat.Heif => F.Heic,
        MagickFormat.Avif => F.Avif,
        MagickFormat.Tiff or MagickFormat.Tif or MagickFormat.Tiff64 or MagickFormat.Ptif => F.Tiff,
        MagickFormat.Bmp or MagickFormat.Bmp2 or MagickFormat.Bmp3 => F.Bmp,
        MagickFormat.Ico or MagickFormat.Icon => F.Ico,
        MagickFormat.Svg or MagickFormat.Svgz or MagickFormat.Msvg or MagickFormat.Rsvg => F.Svg,
        MagickFormat.Gif or MagickFormat.Gif87 => F.Gif,
        MagickFormat.Pdf => F.Pdf,
        MagickFormat.Jxl => F.Jxl,
        MagickFormat.Tga or MagickFormat.Icb or MagickFormat.Vda or MagickFormat.Vst => F.Tga,
        MagickFormat.Dds or MagickFormat.Dxt1 or MagickFormat.Dxt5 => F.Dds,
        MagickFormat.Jp2 or MagickFormat.J2k or MagickFormat.J2c or MagickFormat.Jpc or MagickFormat.Jpt or MagickFormat.Jpm => F.Jp2,
        MagickFormat.Qoi => F.Qoi,
        MagickFormat.Pcx => F.Pcx,
        MagickFormat.Xcf => F.Xcf,
        _ => null,
    };

    /// <summary>
    /// The real format of a file: magic bytes first (a ".jpg" that is really a PNG is common),
    /// then the extension.
    /// </summary>
    public static FileFormat? Detect(string path)
    {
        Span<byte> head = stackalloc byte[32];
        int n;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            n = fs.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        }
        catch (IOException)
        {
            return F.FromPath(path);
        }
        var sniffed = Sniff(head[..n]);
        var named = F.FromPath(path);
        // Camera RAW files are TIFF (or ISO) containers, and Illustrator files are PDF or PostScript
        // inside: the extension says what they really are.
        if (named == F.Raw && sniffed is null or { Id: "tiff" })
            return F.Raw;
        if (named == F.Ai && sniffed is null or { Id: "pdf" } or { Id: "eps" })
            return F.Ai;
        return sniffed ?? named;
    }

    internal static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public static FileFormat? Sniff(ReadOnlySpan<byte> h)
    {
        if (h.Length >= 3 && h[0] == 0xFF && h[1] == 0xD8 && h[2] == 0xFF) return F.Jpg;
        if (h.Length >= 8 && h[..8].SequenceEqual(PngSignature)) return F.Png;
        if (h.Length >= 12 && h[..4].SequenceEqual("RIFF"u8) && h[8..12].SequenceEqual("WEBP"u8)) return F.Webp;
        if (h.Length >= 6 && (h[..6].SequenceEqual("GIF87a"u8) || h[..6].SequenceEqual("GIF89a"u8))) return F.Gif;
        if (h.Length >= 4 && (h[..4].SequenceEqual("II*\0"u8) || h[..4].SequenceEqual("MM\0*"u8))) return F.Tiff;
        if (h.Length >= 2 && h[0] == (byte)'B' && h[1] == (byte)'M') return F.Bmp;
        if (h.Length >= 4 && h[0] == 0 && h[1] == 0 && h[2] == 1 && h[3] == 0) return F.Ico;
        if (h.Length >= 5 && h[..5].SequenceEqual("%PDF-"u8)) return F.Pdf;
        if (h.Length >= 2 && h[0] == 0xFF && h[1] == 0x0A) return F.Jxl; // JPEG XL code stream
        if (h.Length >= 12 && h[..12].SequenceEqual((ReadOnlySpan<byte>)[0, 0, 0, 0x0C, (byte)'J', (byte)'X', (byte)'L', (byte)' ', 0x0D, 0x0A, 0x87, 0x0A])) return F.Jxl;
        if (h.Length >= 12 && h[..12].SequenceEqual((ReadOnlySpan<byte>)[0, 0, 0, 0x0C, (byte)'j', (byte)'P', (byte)' ', (byte)' ', 0x0D, 0x0A, 0x87, 0x0A])) return F.Jp2;
        if (h.Length >= 4 && h[0] == 0xFF && h[1] == 0x4F && h[2] == 0xFF && h[3] == 0x51) return F.Jp2; // JPEG 2000 code stream
        if (h.Length >= 4 && h[..4].SequenceEqual("qoif"u8)) return F.Qoi;
        if (h.Length >= 4 && h[..4].SequenceEqual("DDS "u8)) return F.Dds;
        if (h.Length >= 9 && h[..9].SequenceEqual("gimp xcf "u8)) return F.Xcf;
        if (PsdFile.IsPsd(h)) return h[5] == 2 ? F.Psb : F.Psd;
        if (EpsFile.IsDosEps(h) || h.StartsWith("%!PS-Adobe"u8)) return F.Eps;
        if (h.Length >= 12 && h[4..8].SequenceEqual("ftyp"u8))
        {
            var brand = h[8..12];
            if (brand.SequenceEqual("avif"u8) || brand.SequenceEqual("avis"u8)) return F.Avif;
            if (brand.SequenceEqual("heic"u8) || brand.SequenceEqual("heix"u8) || brand.SequenceEqual("mif1"u8) ||
                brand.SequenceEqual("msf1"u8) || brand.SequenceEqual("heim"u8) || brand.SequenceEqual("heis"u8) ||
                brand.SequenceEqual("hevc"u8) || brand.SequenceEqual("hevx"u8))
                return F.Heic;
        }
        // SVG is text; a BOM or XML prolog can precede the root element.
        var text = h.Length >= 3 && h[0] == 0xEF && h[1] == 0xBB && h[2] == 0xBF ? h[3..] : h;
        if (text.Length >= 4 && (text.StartsWith("<svg"u8) || text.StartsWith("<?xml"u8)))
            return F.Svg;
        return null;
    }

    /// <summary>True for formats that store pixels with lossy compression, so re-encoding should reuse their quality.</summary>
    public static bool IsLossy(FileFormat format) =>
        format == F.Jpg || format == F.Webp || format == F.Heic || format == F.Avif || format == F.Jxl || format == F.Jp2 || format == F.Dds;

    public static bool SupportsAlpha(FileFormat format) =>
        format == F.Png || format == F.Webp || format == F.Tiff || format == F.Avif || format == F.Ico || format == F.Gif
        || format == F.Jxl || format == F.Tga || format == F.Dds || format == F.Jp2 || format == F.Qoi;
}
