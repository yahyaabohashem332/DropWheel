namespace Petal.Core.Formats;

/// <summary>The extensions of the content types web servers give their files, for naming a downloaded file.</summary>
public static class ContentTypes
{
    private static readonly Dictionary<string, string> Types = new(StringComparer.Ordinal)
    {
        ["text/html"] = "html", ["application/xhtml+xml"] = "html", ["application/pdf"] = "pdf",
        ["image/jpeg"] = "jpg", ["image/pjpeg"] = "jpg", ["image/png"] = "png", ["image/gif"] = "gif", ["image/webp"] = "webp",
        ["image/avif"] = "avif", ["image/svg+xml"] = "svg", ["image/heic"] = "heic", ["image/heif"] = "heic", ["image/bmp"] = "bmp",
        ["image/tiff"] = "tiff", ["image/x-icon"] = "ico", ["image/vnd.microsoft.icon"] = "ico", ["image/jxl"] = "jxl",
        ["video/mp4"] = "mp4", ["video/webm"] = "webm", ["video/quicktime"] = "mov", ["video/x-matroska"] = "mkv",
        ["audio/mpeg"] = "mp3", ["audio/mp3"] = "mp3", ["audio/mp4"] = "m4a", ["audio/x-m4a"] = "m4a", ["audio/aac"] = "aac",
        ["audio/ogg"] = "ogg", ["audio/opus"] = "opus", ["audio/wav"] = "wav", ["audio/x-wav"] = "wav", ["audio/wave"] = "wav",
        ["audio/flac"] = "flac", ["audio/x-flac"] = "flac", ["audio/webm"] = "webm",
        ["text/plain"] = "txt", ["text/csv"] = "csv", ["text/markdown"] = "md", ["application/json"] = "json",
        ["application/xml"] = "xml", ["text/xml"] = "xml", ["application/rtf"] = "rtf", ["text/rtf"] = "rtf",
        ["application/zip"] = "zip", ["application/x-zip-compressed"] = "zip", ["application/x-7z-compressed"] = "7z",
        ["application/gzip"] = "gz", ["application/x-tar"] = "tar", ["application/epub+zip"] = "epub",
        ["application/msword"] = "doc", ["application/vnd.ms-excel"] = "xls", ["application/vnd.ms-powerpoint"] = "ppt",
        ["application/vnd.openxmlformats-officedocument.wordprocessingml.document"] = "docx",
        ["application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"] = "xlsx",
        ["application/vnd.openxmlformats-officedocument.presentationml.presentation"] = "pptx",
        ["application/vnd.oasis.opendocument.text"] = "odt", ["application/vnd.oasis.opendocument.spreadsheet"] = "ods",
        ["application/vnd.oasis.opendocument.presentation"] = "odp", ["font/ttf"] = "ttf", ["font/otf"] = "otf",
        ["font/woff"] = "woff", ["font/woff2"] = "woff2",
    };

    /// <summary>The extension (without its dot) for a content type such as "image/webp", or null for one not here.</summary>
    public static string? Extension(string type) => Types.GetValueOrDefault(type.Split(';')[0].Trim().ToLowerInvariant());
}
