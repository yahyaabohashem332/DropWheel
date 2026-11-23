using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using Petal.Engines.Documents.Model;
using Petal.Engines.Documents.Pdf;
using Petal.Engines.Documents.Util;
using SharpCompress.Archives;
using SharpCompress.Readers;
using SkiaSharp;

namespace Petal.Engines.Documents.IO;

/// <summary>A comic book's page: its picture as a PNG or JPEG, and its size in pixels.</summary>
internal sealed record ComicPage(byte[] Image, bool IsJpeg, int Width, int Height);

/// <summary>A comic book: its pages in reading order and what its ComicInfo.xml says.</summary>
internal sealed record Comic(List<ComicPage> Pages, string? Title, string? Author, string? Language, bool RightToLeft);

/// <summary>
/// Comic books: CBZ, CBR and CB7 are ZIP, RAR and 7z archives of page pictures, read in the order of their names
/// with numbers counted as numbers ("page 2" before "page 10"). They become a PDF of one picture a page, a
/// fixed-layout EPUB, or a CBZ; a PDF becomes a CBZ of its pages drawn. Manga (ComicInfo's "YesAndRightToLeft")
/// is read right to left in both.
/// </summary>
internal static class Comics
{
    private static readonly HashSet<string> Pictures = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp", ".gif", ".bmp", ".tif", ".tiff",
    };

    public static Comic Read(string path, CancellationToken ct = default)
    {
        var files = new List<(string Name, byte[] Bytes)>();
        string? comicInfo = null;
        // The archive's kind from its bytes, not its name: a "CBR" is often a ZIP, and the other way round.
        using (var archive = ArchiveFactory.OpenArchive(new FileInfo(path), new SharpCompress.Readers.ReaderOptions()))
        {
            if (archive.IsEncrypted || archive.Entries.Any(e => e.IsEncrypted))
                throw new NotSupportedException("This comic book is protected by a password.");

            // Pages and the ComicInfo; folders, the Mac's leftovers and hidden files are skipped.
            void Take(string? key, bool isDirectory, Func<Stream> open)
            {
                ct.ThrowIfCancellationRequested();
                if (isDirectory || key is null)
                    return;
                var name = key.Replace('\\', '/');
                var leaf = name[(name.LastIndexOf('/') + 1)..];
                if (name.StartsWith("__MACOSX/", StringComparison.Ordinal) || leaf.StartsWith('.'))
                    return;
                var info = leaf.Equals("ComicInfo.xml", StringComparison.OrdinalIgnoreCase);
                if (!info && !Pictures.Contains(Path.GetExtension(leaf)))
                    return;
                using var stream = open();
                using var buffer = new MemoryStream();
                stream.CopyTo(buffer);
                if (info)
                    comicInfo = Text.TextFile.Decode(buffer.ToArray());
                else
                    files.Add((name, buffer.ToArray()));
            }

            // A solid archive (and any 7z) is read from start to end once; the others entry by entry.
            if (archive.IsSolid || archive.Type == SharpCompress.Common.ArchiveType.SevenZip)
            {
                using var reader = archive.ExtractAllEntries();
                while (reader.MoveToNextEntry())
                    Take(reader.Entry.Key, reader.Entry.IsDirectory, reader.OpenEntryStream);
            }
            else
            {
                foreach (var entry in archive.Entries)
                    Take(entry.Key, entry.IsDirectory, entry.OpenEntryStream);
            }
        }
        var info = comicInfo;
        files.Sort((a, b) => NaturalCompare(a.Name, b.Name));
        var pages = new List<ComicPage>();
        foreach (var (_, bytes) in files)
        {
            if (ImageTools.Normalize(bytes) is { } picture)
                pages.Add(new ComicPage(picture.Bytes, picture.MediaType == "image/jpeg", picture.Width, picture.Height));
        }
        if (pages.Count == 0)
            throw new InvalidDataException("This comic book holds no pages.");

        string? title = null, author = null, language = null;
        var rightToLeft = false;
        if (info is not null)
        {
            try
            {
                var root = XDocument.Parse(info).Root;
                string? Field(string name) => root?.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value.Trim() is { Length: > 0 } v ? v : null;
                var series = Field("Series");
                var number = Field("Number");
                title = Field("Title") ?? (series is null ? null : number is null ? series : $"{series} {number}");
                if (title is not null && series is not null && Field("Title") is not null && !title.Contains(series, StringComparison.OrdinalIgnoreCase))
                    title = number is null ? $"{series}: {title}" : $"{series} {number}: {title}";
                author = Field("Writer");
                language = Field("LanguageISO");
                rightToLeft = string.Equals(Field("Manga"), "YesAndRightToLeft", StringComparison.OrdinalIgnoreCase);
            }
            catch (System.Xml.XmlException)
            {
                // A broken ComicInfo says nothing; the pages are still the book.
            }
        }
        return new Comic(pages, title, author, language, rightToLeft);
    }

    /// <summary>Names compared as people read them: runs of digits by their value, letters without case.</summary>
    internal static int NaturalCompare(string a, string b)
    {
        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            if (char.IsAsciiDigit(a[i]) && char.IsAsciiDigit(b[j]))
            {
                int si = i, sj = j;
                while (i < a.Length && char.IsAsciiDigit(a[i])) i++;
                while (j < b.Length && char.IsAsciiDigit(b[j])) j++;
                var x = a[si..i].TrimStart('0');
                var y = b[sj..j].TrimStart('0');
                if (x.Length != y.Length)
                    return x.Length.CompareTo(y.Length);
                var c = string.CompareOrdinal(x, y);
                if (c != 0)
                    return c;
                continue;
            }
            var d = char.ToLowerInvariant(a[i]).CompareTo(char.ToLowerInvariant(b[j]));
            if (d != 0)
                return d;
            i++;
            j++;
        }
        return (a.Length - i).CompareTo(b.Length - j);
    }

    /// <summary>A PDF of one picture a page, each page the picture's size (at 96 pixels an inch).</summary>
    public static void WritePdf(Comic comic, string path, string fallbackTitle, IProgress<double>? progress, CancellationToken ct)
    {
        using var document = new PdfDocument();
        document.Info.Title = comic.Title ?? fallbackTitle;
        if (comic.Author is not null)
            document.Info.Author = comic.Author;
        if (comic.RightToLeft)
            document.ViewerPreferences.Direction = PdfReadingDirection.RightToLeft;
        for (var i = 0; i < comic.Pages.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var page = comic.Pages[i];
            using var stream = new MemoryStream(page.Image);
            using var image = XImage.FromStream(stream);
            var pdfPage = document.AddPage();
            double width = page.Width * 0.75, height = page.Height * 0.75;
            pdfPage.Width = XUnit.FromPoint(width);
            pdfPage.Height = XUnit.FromPoint(height);
            using (var graphics = XGraphics.FromPdfPage(pdfPage))
                graphics.DrawImage(image, 0, 0, width, height);
            progress?.Report((i + 1.0) / comic.Pages.Count);
        }
        document.Save(path);
    }

    /// <summary>A fixed-layout EPUB, one page a picture, read right to left for manga.</summary>
    public static void WriteEpub(Comic comic, string path, string fallbackTitle)
    {
        var metadata = new DocMetadata { Title = comic.Title ?? fallbackTitle, Author = comic.Author, Language = comic.Language };
        Epub.WriteFixedLayout([.. comic.Pages.Select(p => new EpubPage(p.Image, p.IsJpeg, p.Width, p.Height))], path, metadata.Title, metadata, comic.RightToLeft);
    }

    /// <summary>A CBZ: the pages as "page001.jpg" and on, stored (pictures don't compress), with a ComicInfo.xml.</summary>
    public static void WriteCbz(Comic comic, string path) =>
        WriteCbz(comic.Pages.Select(p => (p.Image, p.IsJpeg)), comic.Pages.Count, path, comic.Title, comic.Author, comic.Language, comic.RightToLeft);

    private static void WriteCbz(IEnumerable<(byte[] Image, bool IsJpeg)> pages, int count, string path, string? title, string? author, string? language, bool rightToLeft)
    {
        using var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        var digits = Math.Max(3, count.ToString(System.Globalization.CultureInfo.InvariantCulture).Length);
        var n = 0;
        foreach (var (image, isJpeg) in pages)
        {
            var entry = zip.CreateEntry($"page{(++n).ToString(System.Globalization.CultureInfo.InvariantCulture).PadLeft(digits, '0')}.{(isJpeg ? "jpg" : "png")}", CompressionLevel.NoCompression);
            using var s = entry.Open();
            s.Write(image);
        }
        var info = new XElement("ComicInfo",
            new XAttribute(XNamespace.Xmlns + "xsd", "http://www.w3.org/2001/XMLSchema"),
            new XAttribute(XNamespace.Xmlns + "xsi", "http://www.w3.org/2001/XMLSchema-instance"));
        if (title is not null) info.Add(new XElement("Title", title));
        if (author is not null) info.Add(new XElement("Writer", author));
        info.Add(new XElement("PageCount", n));
        if (language is not null) info.Add(new XElement("LanguageISO", language));
        if (rightToLeft) info.Add(new XElement("Manga", "YesAndRightToLeft"));
        var xml = zip.CreateEntry("ComicInfo.xml", CompressionLevel.Optimal);
        using var w = xml.Open();
        w.Write(Encoding.UTF8.GetBytes(new XDocument(new XDeclaration("1.0", "utf-8", null), info).Declaration + "\n" + info));
    }

    /// <summary>A PDF's pages drawn as JPEG pictures into a CBZ, one at a time.</summary>
    public static void PdfToCbz(byte[] pdf, string path, string? title, int dpi, int quality, IProgress<double>? progress, CancellationToken ct)
    {
        var count = PdfRaster.PageSizes(pdf).Count;
        if (count == 0)
            throw new InvalidDataException("This PDF has no pages.");
        var done = 0;
        IEnumerable<(byte[], bool)> Pages()
        {
            foreach (var (_, bitmap) in PdfRaster.RenderAll(pdf, dpi))
            {
                using (bitmap)
                {
                    ct.ThrowIfCancellationRequested();
                    using var image = SKImage.FromBitmap(bitmap);
                    using var data = image.Encode(SKEncodedImageFormat.Jpeg, Math.Clamp(quality, 1, 100));
                    progress?.Report(++done / (double)count);
                    yield return (data.ToArray(), true);
                }
            }
        }
        WriteCbz(Pages(), count, path, title, null, null, false);
    }
}
