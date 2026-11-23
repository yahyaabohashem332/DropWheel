using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Petal.Engines.Documents.IO;

/// <summary>
/// Kobo's EPUB ("kepub"): the same book, each sentence in a &lt;span class="koboSpan" id="kobo.P.S"&gt; and the body
/// in two divs, which Kobo's reader needs for its page counts, reading statistics and highlights. Made from an EPUB
/// and taken back to one by changing only its XHTML pages; style sheets, pictures and the package stay as they are.
/// </summary>
internal static partial class Kepub
{
    private static readonly XNamespace X = "http://www.w3.org/1999/xhtml";

    /// <summary>Elements whose words are no prose to count: code, drawings, scripts.</summary>
    private static readonly HashSet<string> Skip = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "style", "pre", "code", "svg", "math", "textarea", "rt", "rp", "head", "title",
    };

    private static readonly HashSet<string> BlockElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "p", "h1", "h2", "h3", "h4", "h5", "h6", "li", "div", "td", "th", "dt", "dd", "blockquote", "figcaption", "caption",
        "section", "article", "aside", "header", "footer", "address", "body",
    };

    public static void FromEpub(string epub, string output) => Rewrite(epub, output, AddSpans);

    public static void ToEpub(string kepub, string output) => Rewrite(kepub, output, RemoveSpans);

    /// <summary>Copies the book entry by entry, "mimetype" first and stored, passing its XHTML pages through <paramref name="change"/>.</summary>
    private static void Rewrite(string input, string output, Action<XDocument> change)
    {
        using var source = ZipFile.OpenRead(input);
        if (source.GetEntry("META-INF/container.xml") is null)
            throw new InvalidDataException("This is not an EPUB.");
        using var fs = new FileStream(output, FileMode.CreateNew, FileAccess.Write);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        var mime = zip.CreateEntry("mimetype", CompressionLevel.NoCompression);
        using (var s = mime.Open())
            s.Write("application/epub+zip"u8);
        foreach (var entry in source.Entries)
        {
            if (entry.FullName == "mimetype" || entry.FullName.EndsWith('/'))
                continue;
            using var read = entry.Open();
            using var buffer = new MemoryStream();
            read.CopyTo(buffer);
            var bytes = buffer.ToArray();
            var extension = Path.GetExtension(entry.FullName).ToLowerInvariant();
            if (extension is ".xhtml" or ".html" or ".htm" && Transform(bytes, change) is { } changed)
                bytes = changed;
            var stored = entry.CompressedLength == entry.Length;
            var target = zip.CreateEntry(entry.FullName, stored ? CompressionLevel.NoCompression : CompressionLevel.Optimal);
            using var write = target.Open();
            write.Write(bytes);
        }
    }

    /// <summary>The page changed, or null when it isn't well-formed XHTML (it is then copied as it is).</summary>
    private static byte[]? Transform(byte[] bytes, Action<XDocument> change)
    {
        var text = Text.TextFile.Decode(bytes);
        // The DOCTYPE is kept as written; HTML's named entities (which only the DTD defines) become numbers.
        var doctype = Doctype().Match(text);
        var body = NamedEntity().Replace(text, m => m.Groups[1].Value is "amp" or "lt" or "gt" or "quot" or "apos"
            ? m.Value
            : WebUtility.HtmlDecode(m.Value) is var decoded && decoded != m.Value ? "&#" + char.ConvertToUtf32(decoded, 0) + ";" : m.Value);
        XDocument document;
        try
        {
            using var reader = XmlReader.Create(new StringReader(body), new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null });
            document = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        }
        catch (XmlException)
        {
            return null;
        }
        if (document.Root?.Name.LocalName != "html")
            return null;
        change(document);
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n");
        if (doctype.Success)
            sb.Append(doctype.Value).Append('\n');
        sb.Append(document.Root.ToString(SaveOptions.DisableFormatting));
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static void AddSpans(XDocument document)
    {
        var root = document.Root!;
        var ns = root.Name.Namespace;
        var body = root.Element(ns + "body");
        if (body is null || body.Descendants().Any(e => (string?)e.Attribute("class") == "koboSpan"))
            return;
        var paragraph = 0;
        var sentence = 0;
        XElement? lastBlock = null;
        foreach (var node in body.DescendantNodes().OfType<XText>().ToList())
        {
            if (string.IsNullOrWhiteSpace(node.Value) || node.Ancestors().Any(a => Skip.Contains(a.Name.LocalName)))
                continue;
            var block = node.Ancestors().First(a => BlockElements.Contains(a.Name.LocalName));
            if (block != lastBlock)
            {
                paragraph++;
                sentence = 0;
                lastBlock = block;
            }
            var replacement = new List<XNode>();
            var at = 0;
            foreach (Match m in Sentence().Matches(node.Value))
            {
                if (m.Index > at)
                    replacement.Add(new XText(node.Value[at..m.Index]));
                replacement.Add(new XElement(ns + "span", new XAttribute("class", "koboSpan"),
                    new XAttribute("id", $"kobo.{paragraph}.{++sentence}"), m.Value));
                at = m.Index + m.Length;
            }
            if (at < node.Value.Length)
                replacement.Add(new XText(node.Value[at..]));
            node.ReplaceWith(replacement);
        }
        var inner = new XElement(ns + "div", new XAttribute("id", "book-inner"), body.Nodes().ToList());
        body.RemoveNodes();
        body.Add(new XElement(ns + "div", new XAttribute("id", "book-columns"), inner));
        root.Element(ns + "head")?.Add(new XElement(ns + "style", new XAttribute("type", "text/css"), new XAttribute("id", "kobostylehacks"),
            "div#book-inner { margin-top: 0; margin-bottom: 0; }"));
    }

    private static void RemoveSpans(XDocument document)
    {
        var root = document.Root!;
        foreach (var span in root.Descendants().Where(e => e.Name.LocalName == "span" && (string?)e.Attribute("class") == "koboSpan").ToList())
            span.ReplaceWith(span.Nodes().ToList());
        foreach (var id in new[] { "book-inner", "book-columns" })
        {
            foreach (var div in root.Descendants().Where(e => e.Name.LocalName == "div" && (string?)e.Attribute("id") == id).ToList())
                div.ReplaceWith(div.Nodes().ToList());
        }
        root.Descendants().Where(e => e.Name.LocalName == "style" && (string?)e.Attribute("id") == "kobostylehacks").Remove();
        // Adjacent pieces of text join again.
        foreach (var element in root.DescendantsAndSelf())
        {
            var texts = element.Nodes().OfType<XText>().ToList();
            for (var i = texts.Count - 1; i > 0; i--)
            {
                if (texts[i].PreviousNode == texts[i - 1])
                {
                    texts[i - 1].Value += texts[i].Value;
                    texts[i].Remove();
                }
            }
        }
    }

    /// <summary>A sentence: from its first letter to the marks that end it (and the quotes or brackets after them).</summary>
    [GeneratedRegex(@"\S.*?(?:[.!?…؟。！？]+[""'”’»)\]]*(?=\s|$)|(?=\s*$))", RegexOptions.Singleline)]
    private static partial Regex Sentence();

    [GeneratedRegex(@"<!DOCTYPE[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex Doctype();

    [GeneratedRegex(@"&([A-Za-z][A-Za-z0-9]*);")]
    private static partial Regex NamedEntity();
}
