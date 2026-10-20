using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Petal.Engines.Documents.IO;

/// <summary>OpenDocument namespaces and zip packaging shared by ODT and ODS.</summary>
internal static class Odf
{
    public static readonly XNamespace OfficeNs = "urn:oasis:names:tc:opendocument:xmlns:office:1.0";
    public static readonly XNamespace StyleNs = "urn:oasis:names:tc:opendocument:xmlns:style:1.0";
    public static readonly XNamespace TextNs = "urn:oasis:names:tc:opendocument:xmlns:text:1.0";
    public static readonly XNamespace TableNs = "urn:oasis:names:tc:opendocument:xmlns:table:1.0";
    public static readonly XNamespace DrawNs = "urn:oasis:names:tc:opendocument:xmlns:drawing:1.0";
    public static readonly XNamespace FoNs = "urn:oasis:names:tc:opendocument:xmlns:xsl-fo-compatible:1.0";
    public static readonly XNamespace XLinkNs = "http://www.w3.org/1999/xlink";
    public static readonly XNamespace DcNs = "http://purl.org/dc/elements/1.1/";
    public static readonly XNamespace MetaNs = "urn:oasis:names:tc:opendocument:xmlns:meta:1.0";
    public static readonly XNamespace NumberNs = "urn:oasis:names:tc:opendocument:xmlns:datastyle:1.0";
    public static readonly XNamespace SvgNs = "urn:oasis:names:tc:opendocument:xmlns:svg-compatible:1.0";
    public static readonly XNamespace ManifestNs = "urn:oasis:names:tc:opendocument:xmlns:manifest:1.0";

    public const string Version = "1.3";

    /// <summary>Namespace declarations for a document root, with the conventional prefixes.</summary>
    public static IEnumerable<XAttribute> Declarations() =>
    [
        new(XNamespace.Xmlns + "office", OfficeNs),
        new(XNamespace.Xmlns + "style", StyleNs),
        new(XNamespace.Xmlns + "text", TextNs),
        new(XNamespace.Xmlns + "table", TableNs),
        new(XNamespace.Xmlns + "draw", DrawNs),
        new(XNamespace.Xmlns + "fo", FoNs),
        new(XNamespace.Xmlns + "xlink", XLinkNs),
        new(XNamespace.Xmlns + "dc", DcNs),
        new(XNamespace.Xmlns + "meta", MetaNs),
        new(XNamespace.Xmlns + "number", NumberNs),
        new(XNamespace.Xmlns + "svg", SvgNs),
        new(OfficeNs + "version", Version),
    ];

    /// <summary>Writes an ODF zip: "mimetype" first and uncompressed, then the parts and a manifest.</summary>
    public static void WritePackage(string path, string mimeType, IReadOnlyList<(string Name, byte[] Data, string MediaType)> parts)
    {
        using var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        var mime = zip.CreateEntry("mimetype", CompressionLevel.NoCompression);
        using (var s = mime.Open())
            s.Write(Encoding.ASCII.GetBytes(mimeType));

        foreach (var (name, data, _) in parts)
        {
            var entry = zip.CreateEntry(name, IsCompressed(name) ? CompressionLevel.NoCompression : CompressionLevel.Optimal);
            using var s = entry.Open();
            s.Write(data);
        }

        var manifest = new XElement(ManifestNs + "manifest",
            new XAttribute(XNamespace.Xmlns + "manifest", ManifestNs),
            new XAttribute(ManifestNs + "version", Version),
            new XElement(ManifestNs + "file-entry",
                new XAttribute(ManifestNs + "full-path", "/"),
                new XAttribute(ManifestNs + "version", Version),
                new XAttribute(ManifestNs + "media-type", mimeType)),
            parts.Select(p => new XElement(ManifestNs + "file-entry",
                new XAttribute(ManifestNs + "full-path", p.Name),
                new XAttribute(ManifestNs + "media-type", p.MediaType))));
        var manifestEntry = zip.CreateEntry("META-INF/manifest.xml", CompressionLevel.Optimal);
        using (var s = manifestEntry.Open())
            s.Write(Serialize(new XDocument(manifest)));
    }

    private static bool IsCompressed(string name) =>
        name.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase);

    public static byte[] Serialize(XDocument doc)
    {
        using var ms = new MemoryStream();
        var settings = new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = false, OmitXmlDeclaration = false };
        using (var writer = XmlWriter.Create(ms, settings))
            doc.Save(writer);
        return ms.ToArray();
    }

    public static XDocument? LoadPart(ZipArchive zip, string name)
    {
        var entry = zip.GetEntry(name);
        if (entry is null)
            return null;
        using var s = entry.Open();
        return XDocument.Load(s, LoadOptions.PreserveWhitespace);
    }

    public static byte[]? ReadEntry(ZipArchive zip, string name)
    {
        var entry = zip.GetEntry(name.TrimStart('/'));
        if (entry is null)
            return null;
        using var s = entry.Open();
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>Converts an ODF length ("2.5cm", "10mm", "1in", "12pt", "96px") to points.</summary>
    public static double? ParseLength(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        value = value.Trim();
        var i = 0;
        while (i < value.Length && (char.IsDigit(value[i]) || value[i] is '.' or '-' or '+'))
            i++;
        if (!double.TryParse(value[..i], NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            return null;
        return value[i..].ToLowerInvariant() switch
        {
            "cm" => number * 72 / 2.54,
            "mm" => number * 72 / 25.4,
            "in" or "inch" => number * 72,
            "pt" or "" => number,
            "pc" => number * 12,
            "px" => number * 0.75,
            _ => null,
        };
    }

    public static string Cm(double points) => (points * 2.54 / 72).ToString("0.###", CultureInfo.InvariantCulture) + "cm";

    /// <summary>The text content of an element, interpreting ODF's space, tab and line-break elements.</summary>
    public static string PlainText(XElement element)
    {
        var sb = new StringBuilder();
        AppendText(sb, element);
        return sb.ToString();
    }

    private static void AppendText(StringBuilder sb, XElement element)
    {
        foreach (var node in element.Nodes())
        {
            if (node is XText t)
                sb.Append(t.Value);
            else if (node is XElement e)
            {
                if (e.Name == TextNs + "s")
                    sb.Append(' ', int.TryParse((string?)e.Attribute(TextNs + "c"), out var c) ? c : 1);
                else if (e.Name == TextNs + "tab")
                    sb.Append('\t');
                else if (e.Name == TextNs + "line-break")
                    sb.Append('\n');
                else if (e.Name == TextNs + "note" || e.Name == OfficeNs + "annotation")
                    continue;
                else
                    AppendText(sb, e);
            }
        }
    }

    /// <summary>Adds text to an ODF paragraph, encoding runs of spaces, tabs and newlines the ODF way.</summary>
    public static void AppendEncodedText(XElement parent, string text)
    {
        var sb = new StringBuilder();
        void FlushText()
        {
            if (sb.Length == 0) return;
            parent.Add(new XText(sb.ToString()));
            sb.Clear();
        }
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch == '\t')
            {
                FlushText();
                parent.Add(new XElement(TextNs + "tab"));
            }
            else if (ch == '\n')
            {
                FlushText();
                parent.Add(new XElement(TextNs + "line-break"));
            }
            else if (ch == ' ')
            {
                var run = 1;
                while (i + run < text.Length && text[i + run] == ' ')
                    run++;
                // A space straight after literal text survives; leading and repeated spaces would collapse.
                var literal = sb.Length > 0 ? 1 : 0;
                if (literal == 1)
                    sb.Append(' ');
                var remaining = run - literal;
                if (remaining > 0)
                {
                    FlushText();
                    parent.Add(remaining == 1 ? new XElement(TextNs + "s") : new XElement(TextNs + "s", new XAttribute(TextNs + "c", remaining)));
                }
                i += run - 1;
            }
            else if (ch >= 0x20 || ch == '\r')
            {
                if (ch != '\r')
                    sb.Append(ch);
            }
        }
        FlushText();
    }
}
