using System.Globalization;
using System.Text;
using System.Xml.Linq;

namespace Petal.Engines.Files.Design;

/// <summary>A font as a document records it.</summary>
/// <param name="FileName">The font file on the PC that made the document ("arialbd.ttf"), when recorded.</param>
public sealed record DesignFont(string Name, string? Family, string? Style, string? PostScriptName, string? FileName, string? Type = null);

/// <summary>
/// The XMP metadata Adobe applications embed in their files: the application, page count and
/// size, fonts, linked files and a thumbnail. Found by scanning for the packet, so it works for
/// InDesign, Illustrator, EPS and PDF files alike without reading their formats.
/// </summary>
public sealed class XmpPacket
{
    private static readonly XNamespace Rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
    private static readonly XNamespace Xmp = "http://ns.adobe.com/xap/1.0/";
    private static readonly XNamespace XmpTPg = "http://ns.adobe.com/xap/1.0/t/pg/";
    private static readonly XNamespace StDim = "http://ns.adobe.com/xap/1.0/sType/Dimensions#";
    private static readonly XNamespace StFnt = "http://ns.adobe.com/xap/1.0/sType/Font#";
    private static readonly XNamespace XmpMM = "http://ns.adobe.com/xap/1.0/mm/";
    private static readonly XNamespace StMfs = "http://ns.adobe.com/xap/1.0/sType/ManifestItem#";
    private static readonly XNamespace StRef = "http://ns.adobe.com/xap/1.0/sType/ResourceRef#";
    private static readonly XNamespace XmpGImg = "http://ns.adobe.com/xap/1.0/g/img/";
    private static readonly XNamespace XmpG = "http://ns.adobe.com/xap/1.0/g/";
    private static readonly XNamespace Dc = "http://purl.org/dc/elements/1.1/";

    private readonly XDocument _xml;

    private XmpPacket(XDocument xml) => _xml = xml;

    /// <summary>The program that saved the file ("Adobe InDesign 19.0 (Windows)").</summary>
    public string? CreatorTool => Value(Xmp + "CreatorTool");

    public string? Title => _xml.Descendants(Dc + "title").Descendants(Rdf + "li").Select(e => e.Value).FirstOrDefault(v => v.Length > 0);

    public int? Pages => int.TryParse(Value(XmpTPg + "NPages"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : null;

    /// <summary>The largest page (artboard) size and its unit ("Points", "Millimeters", "Pixels"…).</summary>
    public (double Width, double Height, string Unit)? PageSize
    {
        get
        {
            var size = _xml.Descendants(XmpTPg + "MaxPageSize").FirstOrDefault();
            if (size is null)
                return null;
            double Read(string name) => double.TryParse(Child(size, StDim + name), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
            return (Read("w"), Read("h"), Child(size, StDim + "unit") ?? "");
        }
    }

    public IReadOnlyList<DesignFont> Fonts =>
        _xml.Descendants(XmpTPg + "Fonts").Descendants(Rdf + "li").Select(li =>
        {
            string? Read(string name) => Child(li, StFnt + name);
            var family = Read("fontFamily");
            var face = Read("fontFace");
            var postScript = Read("fontName");
            var name = family is { Length: > 0 } ? (face is { Length: > 0 } && face != "Regular" ? $"{family} {face}" : family) : postScript ?? "";
            return new DesignFont(name, family, face, postScript, Read("fontFileName"), Read("fontType"));
        }).Where(f => f.Name.Length > 0).DistinctBy(f => (f.PostScriptName ?? f.Name).ToLowerInvariant()).ToList();

    /// <summary>Files the document links to (placed pictures), as recorded on the PC that saved it.</summary>
    public IReadOnlyList<string> LinkedFiles =>
        _xml.Descendants(XmpMM + "Manifest").Descendants(StMfs + "reference")
            .Select(r => Child(r, StRef + "filePath"))
            .OfType<string>()
            .Select(ToPath)
            .Where(p => p.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>Colour swatch names (Illustrator and InDesign list the swatches in use).</summary>
    public IReadOnlyList<string> Swatches =>
        _xml.Descendants(XmpG + "swatchName").Select(e => e.Value).Where(v => v.Length > 0).Distinct().ToList();

    /// <summary>Separation plates (Cyan, Magenta, Yellow, Black and any spot colours).</summary>
    public IReadOnlyList<string> Plates =>
        _xml.Descendants(XmpTPg + "PlateNames").Descendants(Rdf + "li").Select(e => e.Value).Where(v => v.Length > 0).ToList();

    /// <summary>The thumbnail the application stored (usually a JPEG), or null.</summary>
    public byte[]? Thumbnail
    {
        get
        {
            var image = _xml.Descendants(XmpGImg + "image").Select(e => e.Value)
                .Concat(_xml.Descendants().Where(e => e.Name.LocalName == "li" && e.Attribute(XmpGImg + "image") is not null).Select(e => e.Attribute(XmpGImg + "image")!.Value))
                .FirstOrDefault(v => v.Length > 100);
            if (image is null)
                return null;
            try
            {
                return Convert.FromBase64String(image.Replace("&#xA;", "").Replace("\n", "").Replace("\r", "").Replace(" ", ""));
            }
            catch (FormatException)
            {
                return null;
            }
        }
    }

    private string? Value(XName name)
    {
        var element = _xml.Descendants(name).FirstOrDefault();
        if (element is not null)
            return element.Value.Trim() is { Length: > 0 } v ? v : null;
        // XMP also writes simple values as attributes of rdf:Description.
        return _xml.Descendants(Rdf + "Description").Select(d => d.Attribute(name)?.Value).FirstOrDefault(v => v is { Length: > 0 });
    }

    private static string? Child(XElement parent, XName name) =>
        parent.Element(name)?.Value ?? parent.Attribute(name)?.Value ?? parent.Descendants(name).FirstOrDefault()?.Value;

    /// <summary>"file:///C:/Images/a.jpg" and the like to "C:\Images\a.jpg".</summary>
    internal static string ToPath(string reference)
    {
        var text = reference.Trim();
        if (text.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            if (Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.IsFile)
                return uri.LocalPath;
            text = Uri.UnescapeDataString(text[5..].TrimStart('/'));
            if (text.Length > 1 && text[1] == ':')
                return text.Replace('/', '\\');
        }
        return text;
    }

    /// <summary>The last XMP packet in the file (an application's own, which follows any it embedded from placed files), or null.</summary>
    public static XmpPacket? Read(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
        foreach (var packet in Enumerable.Reverse(Packets(stream)))
        {
            if (Parse(packet) is { } xmp && (xmp.CreatorTool is not null || xmp.Pages is not null || xmp.Fonts.Count > 0))
                return xmp;
        }
        return null;
    }

    public static XmpPacket? Parse(byte[] packet)
    {
        try
        {
            var text = Encoding.UTF8.GetString(packet);
            int start = text.IndexOf("<x:xmpmeta", StringComparison.Ordinal);
            if (start < 0)
                start = text.IndexOf("<rdf:RDF", StringComparison.Ordinal);
            if (start < 0)
                return null;
            return new XmpPacket(XDocument.Parse(text[start..]));
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
    }

    /// <summary>Every "&lt;x:xmpmeta …&lt;/x:xmpmeta&gt;" in the file, scanned in pieces so large files stay cheap.</summary>
    private static List<byte[]> Packets(Stream stream)
    {
        var open = "<x:xmpmeta"u8.ToArray();
        var close = "</x:xmpmeta>"u8.ToArray();
        var found = new List<byte[]>();
        const int chunk = 4 << 20;
        var buffer = new byte[chunk + open.Length];
        long position = 0;
        int carried = 0;
        while (true)
        {
            int read = stream.ReadAtLeast(buffer.AsSpan(carried, chunk), chunk, throwOnEndOfStream: false);
            int length = carried + read;
            if (length == 0)
                break;
            int at = 0;
            while (true)
            {
                int i = buffer.AsSpan(at, length - at).IndexOf(open);
                if (i < 0)
                    break;
                long start = position - carried + at + i;
                var packet = ReadPacket(stream, start, close);
                if (packet is not null)
                    found.Add(packet);
                at += i + open.Length;
            }
            if (read == 0)
                break;
            // Keep the tail in case the marker straddles two pieces.
            carried = Math.Min(open.Length - 1, length);
            Array.Copy(buffer, length - carried, buffer, 0, carried);
            position += read;
            stream.Position = position;
        }
        return found;
    }

    private static byte[]? ReadPacket(Stream stream, long start, byte[] close)
    {
        long saved = stream.Position;
        try
        {
            stream.Position = start;
            // Packets are small; a few megabytes covers thumbnails and long manifests.
            var buffer = new byte[(int)Math.Min(16 << 20, stream.Length - start)];
            int n = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            int end = buffer.AsSpan(0, n).IndexOf(close);
            return end < 0 ? null : buffer[..(end + close.Length)];
        }
        finally
        {
            stream.Position = saved;
        }
    }
}
