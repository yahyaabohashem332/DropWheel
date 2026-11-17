using System;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace Petal.Shell;

/// <summary>
/// What a file looks like, for File Explorer, told by what the file holds (its name isn't given): a design file's
/// own preview, a PDF's first page, a 3D model drawn, an EPUB's cover, a comic book's first page, an SVG drawn, an
/// Office or 3MF file's thumbnail.
/// </summary>
public static class Previews
{
    /// <summary>The picture, about <paramref name="size"/> pixels on its longer side; null when there is none to give.</summary>
    public static Bitmap? Read(Stream stream, int size)
    {
        if (!stream.CanSeek || stream.Length < 8)
            return null;
        var head = new byte[1024];
        stream.Position = 0;
        int n = stream.Read(head, 0, head.Length);
        stream.Position = 0;
        if (PdfPages.Looks(head, n))
        {
            if (PdfPages.Bytes(stream) is { } pdf && PdfPages.Render(pdf, 0, size) is { } page)
                return page;
            return EmbeddedPreview.Read(stream, size);
        }
        bool zip = n >= 4 && head[0] == 'P' && head[1] == 'K' && head[2] == 3 && head[3] == 4;
        if (zip)
            return EmbeddedPreview.Read(stream, size) ?? Archive(stream, size);
        if (Mesh.LooksLikeModel(head, n, stream.Length) && Bytes(stream, 256L * 1024 * 1024) is { } bytes && Mesh.Read(bytes) is { } mesh)
            return mesh.Render(size);
        if (SvgPicture.Looks(head, n))
            return SvgPicture.Render(stream, size);
        return EmbeddedPreview.Read(stream, size);
    }

    /// <summary>A ZIP without a design file's preview: an EPUB's cover, a comic book's first page, or a 3MF model drawn.</summary>
    private static Bitmap? Archive(Stream stream, int size)
    {
        try
        {
            stream.Position = 0;
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
            if (Entry(zip, "mimetype") is { } mime && Text(mime).Trim() == "application/epub+zip")
                return Cover(zip) is { } cover ? EmbeddedPreview.Fit(cover, size) : null;
            if (zip.Entries.Any(e => e.FullName.StartsWith("3D/", StringComparison.OrdinalIgnoreCase) && e.FullName.EndsWith(".model", StringComparison.OrdinalIgnoreCase)))
            {
                stream.Position = 0;
                return Bytes(stream, 256L * 1024 * 1024) is { } bytes && Mesh.Read(bytes) is { } mesh ? mesh.Render(size) : null;
            }
            // A comic book: pictures only, read in their names' order (as numbered pages are).
            var pages = zip.Entries.Where(e => e.Length > 0 && IsPicture(e.FullName) && !e.FullName.StartsWith("__MACOSX", StringComparison.OrdinalIgnoreCase))
                .OrderBy(e => e.FullName, NaturalOrder.Instance).ToList();
            if (pages.Count > 0 && zip.Entries.Count(e => e.Length > 0 && !IsPicture(e.FullName)) <= 3)
                return Picture(pages[0]) is { } first ? EmbeddedPreview.Fit(first, size) : null;
        }
        catch (InvalidDataException)
        {
        }
        return null;
    }

    /// <summary>An EPUB's cover: the picture its package names as the cover (EPUB 3 or 2), or the first one called "cover".</summary>
    internal static Bitmap? Cover(ZipArchive zip)
    {
        var container = Entry(zip, "META-INF/container.xml");
        var opfPath = container is null ? null : Regex.Match(Text(container), "full-path\\s*=\\s*\"([^\"]+)\"").Groups[1].Value;
        if (!string.IsNullOrEmpty(opfPath) && Entry(zip, opfPath!) is { } opf)
        {
            var package = new XmlDocument { XmlResolver = null };
            using (var reader = XmlReader.Create(new StringReader(Text(opf)), new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null }))
                package.Load(reader);
            var items = package.GetElementsByTagName("item").OfType<XmlElement>().ToList();
            var coverId = package.GetElementsByTagName("meta").OfType<XmlElement>().FirstOrDefault(m => m.GetAttribute("name") == "cover")?.GetAttribute("content");
            var item = items.FirstOrDefault(i => (" " + i.GetAttribute("properties") + " ").Contains(" cover-image "))
                ?? items.FirstOrDefault(i => coverId is { Length: > 0 } && i.GetAttribute("id") == coverId);
            if (item is not null)
            {
                var folder = opfPath!.Contains('/') ? opfPath.Substring(0, opfPath.LastIndexOf('/') + 1) : "";
                var href = Uri.UnescapeDataString(item.GetAttribute("href"));
                if (Entry(zip, Normal(folder + href)) is { } picture && Picture(picture) is { } found)
                    return found;
            }
        }
        var named = zip.Entries.FirstOrDefault(e => IsPicture(e.FullName) && Path.GetFileName(e.FullName).StartsWith("cover", StringComparison.OrdinalIgnoreCase));
        return named is null ? null : Picture(named);
    }

    /// <summary>"OEBPS/text/../images/a.jpg" as "OEBPS/images/a.jpg".</summary>
    private static string Normal(string path)
    {
        var parts = new System.Collections.Generic.List<string>();
        foreach (var part in path.Split('/'))
        {
            if (part == "..")
            {
                if (parts.Count > 0)
                    parts.RemoveAt(parts.Count - 1);
            }
            else if (part.Length > 0 && part != ".")
                parts.Add(part);
        }
        return string.Join("/", parts);
    }

    private static bool IsPicture(string name) =>
        Path.GetExtension(name).ToLowerInvariant() is ".jpg" or ".jpeg" or ".png" or ".gif" or ".bmp" or ".webp" or ".tif" or ".tiff";

    private static ZipArchiveEntry? Entry(ZipArchive zip, string name) =>
        zip.Entries.FirstOrDefault(e => e.FullName.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static string Text(ZipArchiveEntry entry)
    {
        using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static Bitmap? Picture(ZipArchiveEntry entry)
    {
        if (entry.Length > 128 * 1024 * 1024)
            return null;
        using var inner = entry.Open();
        using var copy = new MemoryStream();
        inner.CopyTo(copy);
        return EmbeddedPreview.Decode(copy.ToArray());
    }

    private static byte[]? Bytes(Stream stream, long most) => PdfPages.Bytes(stream, most);

    /// <summary>Names in the order people number pages: "page 2" before "page 10".</summary>
    private sealed class NaturalOrder : System.Collections.Generic.IComparer<string>
    {
        public static readonly NaturalOrder Instance = new();

        public int Compare(string? x, string? y)
        {
            x ??= "";
            y ??= "";
            int i = 0, j = 0;
            while (i < x.Length && j < y.Length)
            {
                if (char.IsDigit(x[i]) && char.IsDigit(y[j]))
                {
                    int si = i, sj = j;
                    while (i < x.Length && char.IsDigit(x[i]))
                        i++;
                    while (j < y.Length && char.IsDigit(y[j]))
                        j++;
                    var a = x.Substring(si, i - si).TrimStart('0');
                    var b = y.Substring(sj, j - sj).TrimStart('0');
                    int c = a.Length != b.Length ? a.Length.CompareTo(b.Length) : string.CompareOrdinal(a, b);
                    if (c != 0)
                        return c;
                }
                else
                {
                    int c = char.ToLowerInvariant(x[i]).CompareTo(char.ToLowerInvariant(y[j]));
                    if (c != 0)
                        return c;
                    i++;
                    j++;
                }
            }
            return (x.Length - i).CompareTo(y.Length - j);
        }
    }
}
