using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using Petal.Engines.Documents.Model;

namespace Petal.Engines.Documents.IO;

/// <summary>A page picture for fixed-layout EPUBs, already encoded as JPEG or PNG.</summary>
internal sealed record EpubPage(byte[] Image, bool IsJpeg, int Width, int Height);

/// <summary>EPUB 3 reading and writing (with an NCX table of contents for older readers).</summary>
internal static class Epub
{
    private static readonly XNamespace Opf = "http://www.idpf.org/2007/opf";
    private static readonly XNamespace Dc = "http://purl.org/dc/elements/1.1/";
    private static readonly XNamespace Container = "urn:oasis:names:tc:opendocument:xmlns:container";
    private static readonly XNamespace Ncx = "http://www.daisy.org/z3986/2005/ncx/";

    public static DocModel Read(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var container = Odf.LoadPart(zip, "META-INF/container.xml") ?? throw new InvalidDataException("Not an EPUB: META-INF/container.xml is missing.");
        var opfPath = container.Descendants(Container + "rootfile").Select(r => (string?)r.Attribute("full-path")).FirstOrDefault(p => p is not null)
                      ?? throw new InvalidDataException("The EPUB does not name its package document.");
        var opf = Odf.LoadPart(zip, opfPath) ?? throw new InvalidDataException("The EPUB package document is missing.");
        var opfDir = DirectoryOf(opfPath);

        var doc = new DocModel();
        var metadata = opf.Root?.Element(Opf + "metadata");
        if (metadata is not null)
        {
            doc.Metadata.Title = NullIfBlank(metadata.Element(Dc + "title")?.Value);
            doc.Metadata.Author = NullIfBlank(string.Join(", ", metadata.Elements(Dc + "creator").Select(c => c.Value.Trim()).Where(c => c.Length > 0)));
            doc.Metadata.Language = NullIfBlank(metadata.Element(Dc + "language")?.Value);
            doc.Metadata.Subject = NullIfBlank(metadata.Element(Dc + "description")?.Value);
            var subjects = metadata.Elements(Dc + "subject").Select(s => s.Value.Trim()).Where(s => s.Length > 0).ToList();
            if (subjects.Count > 0)
                doc.Metadata.Keywords = string.Join(", ", subjects);
        }

        var manifest = opf.Root?.Element(Opf + "manifest")?.Elements(Opf + "item")
            .Where(i => i.Attribute("id") is not null && i.Attribute("href") is not null)
            .GroupBy(i => (string)i.Attribute("id")!)
            .ToDictionary(g => g.Key, g => g.First()) ?? [];
        var spine = opf.Root?.Element(Opf + "spine")?.Elements(Opf + "itemref")
            .Where(r => (string?)r.Attribute("linear") != "no")
            .Select(r => (string?)r.Attribute("idref"))
            .Where(id => id is not null && manifest.ContainsKey(id))
            .Select(id => manifest[id!])
            .ToList() ?? [];

        var first = true;
        foreach (var item in spine)
        {
            var mediaType = (string?)item.Attribute("media-type") ?? "";
            if (!mediaType.Contains("html", StringComparison.OrdinalIgnoreCase))
                continue;
            var itemPath = Combine(opfDir, Uri.UnescapeDataString((string)item.Attribute("href")!));
            var bytes = Odf.ReadEntry(zip, itemPath);
            if (bytes is null)
                continue;
            var html = Text.TextFile.Decode(bytes);
            var itemDir = DirectoryOf(itemPath);
            var chapter = new DocModel();
            HtmlReader.ParseInto(html, chapter, src => Odf.ReadEntry(zip, Combine(itemDir, Uri.UnescapeDataString(src.Split('#')[0]))));
            if (chapter.Blocks.Count == 0)
                continue;
            if (!first)
                doc.Blocks.Add(new PageBreak());
            first = false;
            doc.Blocks.AddRange(chapter.Blocks);
            doc.Metadata.Title ??= chapter.Metadata.Title;
        }
        return doc;
    }

    /// <summary>Reflowable EPUB: chapters split at top-level headings (or page breaks), pictures as separate items.</summary>
    public static void Write(DocModel doc, string path)
    {
        var title = doc.EffectiveTitle() ?? Path.GetFileNameWithoutExtension(path).Replace(".epub", "", StringComparison.OrdinalIgnoreCase);
        var images = new Dictionary<ImageData, string>(ReferenceEqualityComparer.Instance);
        var parts = new List<(string Name, byte[] Data, string MediaType, string Id, string? Properties)>();
        var htmlWriter = new HtmlWriter
        {
            Xhtml = true,
            StylesheetHref = "style.css",
            ImageSource = image =>
            {
                if (!images.TryGetValue(image, out var name))
                {
                    name = $"images/image{images.Count + 1}.{image.Extension}";
                    images[image] = name;
                    parts.Add(($"OEBPS/{name}", image.Bytes, image.MediaType, $"img{images.Count}", null));
                }
                return name;
            },
        };

        var chapters = SplitChapters(doc.Blocks);
        var toc = new List<(string File, string Title)>();
        for (var i = 0; i < chapters.Count; i++)
        {
            var blocks = chapters[i];
            var chapterTitle = blocks.OfType<Heading>().Select(h => h.Inlines.PlainText().Trim()).FirstOrDefault(t => t.Length > 0)
                               ?? (chapters.Count == 1 ? title : $"Part {i + 1}");
            var file = $"chapter{i + 1}.xhtml";
            var xhtml = htmlWriter.Render(blocks, chapterTitle, doc.Metadata);
            parts.Add(($"OEBPS/{file}", Encoding.UTF8.GetBytes(xhtml), "application/xhtml+xml", $"chapter{i + 1}", null));
            toc.Add((file, chapterTitle));
        }

        parts.Add(("OEBPS/style.css", Encoding.UTF8.GetBytes(HtmlWriter.Stylesheet + "\nbody { margin: 0 5%; max-width: none; }\n"), "text/css", "css", null));
        WritePackage(path, title, doc.Metadata, toc, parts, fixedLayout: false, coverId: null);
    }

    /// <summary>Fixed-layout EPUB: one page picture per page, keeping the original look exactly.</summary>
    public static void WriteFixedLayout(IReadOnlyList<EpubPage> pages, string path, string title, DocMetadata metadata)
    {
        var parts = new List<(string Name, byte[] Data, string MediaType, string Id, string? Properties)>();
        var toc = new List<(string File, string Title)>();
        for (var i = 0; i < pages.Count; i++)
        {
            var page = pages[i];
            var ext = page.IsJpeg ? "jpg" : "png";
            var imageName = $"images/page{i + 1}.{ext}";
            parts.Add(($"OEBPS/{imageName}", page.Image, page.IsJpeg ? "image/jpeg" : "image/png", $"img{i + 1}", i == 0 ? "cover-image" : null));
            var xhtml = $"""
                <?xml version="1.0" encoding="utf-8"?>
                <!DOCTYPE html>
                <html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops">
                <head>
                <meta charset="utf-8" />
                <meta name="viewport" content="width={page.Width}, height={page.Height}" />
                <title>Page {i + 1}</title>
                <style>html, body {"{"} margin: 0; padding: 0; width: {page.Width}px; height: {page.Height}px; overflow: hidden; {"}"} img {"{"} display: block; width: {page.Width}px; height: {page.Height}px; {"}"}</style>
                </head>
                <body><img src="{imageName}" alt="Page {i + 1}" /></body>
                </html>
                """;
            var file = $"page{i + 1}.xhtml";
            parts.Add(($"OEBPS/{file}", Encoding.UTF8.GetBytes(xhtml), "application/xhtml+xml", $"page{i + 1}", null));
            toc.Add((file, $"Page {i + 1}"));
        }
        WritePackage(path, title, metadata, toc, parts, fixedLayout: true, coverId: "img1");
    }

    private static List<List<Block>> SplitChapters(List<Block> blocks)
    {
        var chapters = new List<List<Block>>();
        var h1Count = blocks.Count(b => b is Heading { Level: 1 });
        var current = new List<Block>();
        foreach (var block in blocks)
        {
            var split = h1Count >= 2 ? block is Heading { Level: 1 } : block is PageBreak;
            if (split && current.Any(b => b is not PageBreak))
            {
                chapters.Add(current);
                current = [];
            }
            if (block is not PageBreak)
                current.Add(block);
        }
        if (current.Count > 0 || chapters.Count == 0)
            chapters.Add(current);
        return chapters;
    }

    private static void WritePackage(string path, string title, DocMetadata metadata, List<(string File, string Title)> toc,
        List<(string Name, byte[] Data, string MediaType, string Id, string? Properties)> parts, bool fixedLayout, string? coverId)
    {
        var uid = "urn:uuid:" + Guid.NewGuid();
        var language = string.IsNullOrWhiteSpace(metadata.Language) ? "en" : metadata.Language!;
        var modified = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");

        var nav = new StringBuilder();
        nav.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<!DOCTYPE html>\n<html xmlns=\"http://www.w3.org/1999/xhtml\" xmlns:epub=\"http://www.idpf.org/2007/ops\">\n");
        nav.Append("<head><meta charset=\"utf-8\" /><title>").Append(HtmlWriter.Text(title)).Append("</title></head>\n<body>\n");
        nav.Append("<nav epub:type=\"toc\" id=\"toc\"><h1>Contents</h1><ol>\n");
        foreach (var (file, t) in toc)
            nav.Append("<li><a href=\"").Append(file).Append("\">").Append(HtmlWriter.Text(t)).Append("</a></li>\n");
        nav.Append("</ol></nav>\n</body>\n</html>\n");

        var ncx = new XDocument(new XElement(Ncx + "ncx", new XAttribute("version", "2005-1"),
            new XElement(Ncx + "head",
                new XElement(Ncx + "meta", new XAttribute("name", "dtb:uid"), new XAttribute("content", uid)),
                new XElement(Ncx + "meta", new XAttribute("name", "dtb:depth"), new XAttribute("content", "1"))),
            new XElement(Ncx + "docTitle", new XElement(Ncx + "text", title)),
            new XElement(Ncx + "navMap", toc.Select((t, i) => new XElement(Ncx + "navPoint",
                new XAttribute("id", $"np{i + 1}"), new XAttribute("playOrder", i + 1),
                new XElement(Ncx + "navLabel", new XElement(Ncx + "text", t.Title)),
                new XElement(Ncx + "content", new XAttribute("src", t.File)))))));

        var meta = new XElement(Opf + "metadata",
            new XAttribute(XNamespace.Xmlns + "dc", Dc),
            new XElement(Dc + "identifier", new XAttribute("id", "bookid"), uid),
            new XElement(Dc + "title", title),
            new XElement(Dc + "language", language),
            new XElement(Opf + "meta", new XAttribute("property", "dcterms:modified"), modified));
        if (!string.IsNullOrWhiteSpace(metadata.Author))
            meta.Add(new XElement(Dc + "creator", metadata.Author));
        if (!string.IsNullOrWhiteSpace(metadata.Subject))
            meta.Add(new XElement(Dc + "description", metadata.Subject));
        if (fixedLayout)
        {
            meta.Add(new XElement(Opf + "meta", new XAttribute("property", "rendition:layout"), "pre-paginated"));
            meta.Add(new XElement(Opf + "meta", new XAttribute("property", "rendition:orientation"), "auto"));
            meta.Add(new XElement(Opf + "meta", new XAttribute("property", "rendition:spread"), "none"));
        }
        if (coverId is not null)
            meta.Add(new XElement(Opf + "meta", new XAttribute("name", "cover"), new XAttribute("content", coverId)));

        var manifest = new XElement(Opf + "manifest",
            new XElement(Opf + "item", new XAttribute("id", "nav"), new XAttribute("href", "nav.xhtml"), new XAttribute("media-type", "application/xhtml+xml"), new XAttribute("properties", "nav")),
            new XElement(Opf + "item", new XAttribute("id", "ncx"), new XAttribute("href", "toc.ncx"), new XAttribute("media-type", "application/x-dtbncx+xml")));
        foreach (var p in parts)
        {
            var item = new XElement(Opf + "item",
                new XAttribute("id", p.Id),
                new XAttribute("href", p.Name["OEBPS/".Length..]),
                new XAttribute("media-type", p.MediaType));
            if (p.Properties is not null)
                item.Add(new XAttribute("properties", p.Properties));
            manifest.Add(item);
        }

        var spine = new XElement(Opf + "spine", new XAttribute("toc", "ncx"),
            toc.Select(t => new XElement(Opf + "itemref", new XAttribute("idref", parts.First(p => p.Name == "OEBPS/" + t.File).Id))));

        var package = new XDocument(new XElement(Opf + "package",
            new XAttribute("version", "3.0"),
            new XAttribute("unique-identifier", "bookid"),
            new XAttribute(XNamespace.Xml + "lang", language),
            meta, manifest, spine));

        var container = """
            <?xml version="1.0" encoding="UTF-8"?>
            <container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container">
              <rootfiles>
                <rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml"/>
              </rootfiles>
            </container>
            """;

        using var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        // The OCF spec requires "mimetype" first and stored without compression.
        var mime = zip.CreateEntry("mimetype", CompressionLevel.NoCompression);
        using (var s = mime.Open())
            s.Write(Encoding.ASCII.GetBytes("application/epub+zip"));
        void Add(string name, byte[] data, bool compress = true)
        {
            var entry = zip.CreateEntry(name, compress ? CompressionLevel.Optimal : CompressionLevel.NoCompression);
            using var s = entry.Open();
            s.Write(data);
        }
        Add("META-INF/container.xml", Encoding.UTF8.GetBytes(container));
        Add("OEBPS/content.opf", Odf.Serialize(package));
        Add("OEBPS/nav.xhtml", Encoding.UTF8.GetBytes(nav.ToString()));
        Add("OEBPS/toc.ncx", Odf.Serialize(ncx));
        foreach (var p in parts)
            Add(p.Name, p.Data, !p.MediaType.StartsWith("image/", StringComparison.Ordinal));
    }

    private static string DirectoryOf(string zipPath)
    {
        var i = zipPath.LastIndexOf('/');
        return i < 0 ? "" : zipPath[..i];
    }

    /// <summary>Resolves a relative href inside the zip, handling "./" and "../".</summary>
    internal static string Combine(string dir, string href)
    {
        if (href.StartsWith('/'))
            return href.TrimStart('/');
        var segments = new List<string>(dir.Length == 0 ? [] : dir.Split('/'));
        foreach (var part in href.Split('/'))
        {
            if (part is "." or "")
                continue;
            if (part == "..")
            {
                if (segments.Count > 0) segments.RemoveAt(segments.Count - 1);
                continue;
            }
            segments.Add(part);
        }
        return string.Join('/', segments);
    }

    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
