using System.Globalization;
using System.Text;
using System.Xml.Linq;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;
using Petal.Engines.Documents.Util;

namespace Petal.Engines.Documents.Pdf;

public enum PdfMetadataGroup
{
    /// <summary>The document information dictionary (Title, Author…).</summary>
    Document,

    /// <summary>The XMP packet; shown for reference and kept in step with edits.</summary>
    Xmp,
}

/// <summary>One metadata row for the Metadata window.</summary>
/// <param name="Key">"Title", "Author"… or a custom key; XMP rows use "prefix:Name".</param>
/// <param name="Value">Dates as "yyyy-MM-dd HH:mm:ss zzz".</param>
public sealed record PdfMetadataField(string Key, string Label, string Value, bool Editable, PdfMetadataGroup Group);

/// <summary>Reads, edits and removes PDF metadata (document information plus XMP).</summary>
public static class PdfMetadataEditor
{
    /// <summary>The standard keys, in display order.</summary>
    public static readonly IReadOnlyList<string> StandardKeys = ["Title", "Author", "Subject", "Keywords", "Creator", "Producer", "CreationDate", "ModDate"];

    private static readonly Dictionary<string, string> Labels = new()
    {
        ["Title"] = "Title", ["Author"] = "Author", ["Subject"] = "Subject", ["Keywords"] = "Keywords",
        ["Creator"] = "Creator", ["Producer"] = "Producer", ["CreationDate"] = "Created", ["ModDate"] = "Modified",
    };

    private static readonly XNamespace Rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
    private static readonly XNamespace Dc = "http://purl.org/dc/elements/1.1/";
    private static readonly XNamespace PdfNs = "http://ns.adobe.com/pdf/1.3/";
    private static readonly XNamespace XmpNs = "http://ns.adobe.com/xap/1.0/";
    private static readonly XNamespace Xml = XNamespace.Xml;

    public static Task<IReadOnlyList<PdfMetadataField>> ReadAsync(string path, CancellationToken cancellationToken = default, string? password = null) =>
        Task.Run<IReadOnlyList<PdfMetadataField>>(() =>
        {
            using var document = PdfSharpIO.Open(path, PdfDocumentOpenMode.Import, password);
            var fields = new List<PdfMetadataField>();
            var info = document.Info.Elements;
            foreach (var key in StandardKeys)
                fields.Add(new PdfMetadataField(key, Labels[key], InfoValue(info, "/" + key) ?? "", true, PdfMetadataGroup.Document));
            foreach (var key in info.Keys.Select(k => k.TrimStart('/')).Where(k => !StandardKeys.Contains(k)).Order())
                fields.Add(new PdfMetadataField(key, key, InfoValue(info, "/" + key) ?? "", true, PdfMetadataGroup.Document));

            if (Xmp(document) is { } xmp)
                foreach (var (key, value) in XmpProperties(xmp))
                    fields.Add(new PdfMetadataField(key, key, value, false, PdfMetadataGroup.Xmp));
            return fields;
        }, cancellationToken);

    /// <summary>
    /// Applies edits (key → new value; null or empty removes the key) and writes the result to
    /// <paramref name="output"/>. Dates accept ISO forms such as "2024-05-01 13:45" or "2024-05-01T13:45:00+02:00".
    /// The XMP packet, when present, is updated to match.
    /// </summary>
    public static Task WriteAsync(string input, string output, IReadOnlyDictionary<string, string?> changes,
        CancellationToken cancellationToken = default, string? password = null) =>
        Task.Run(() =>
        {
            SafeOutput.EnsureNotInput(input, output);
            using var document = PdfSharpIO.Open(input, PdfDocumentOpenMode.Modify, password);
            var info = document.Info.Elements;
            var final = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var key in info.Keys.ToList())
                if (InfoValue(info, key) is { } existing)
                    final[key.TrimStart('/')] = existing;
            foreach (var (rawKey, value) in changes)
            {
                var key = rawKey.TrimStart('/');
                if (string.IsNullOrWhiteSpace(value))
                    final.Remove(key);
                else
                    final[key] = value.Trim();
            }
            cancellationToken.ThrowIfCancellationRequested();

            // PDFsharp regenerates XMP from its own Info on save, so the packet is built here and put back afterwards.
            var xmp = Xmp(document) is { } xmpStream ? UpdatedXmp(xmpStream, final) : null;

            var bytes = PdfSharpIO.SaveToBytes(document);
            bytes = PdfSharpIO.Finish(bytes, Encode(final),
                xmp is null ? PdfSharpIO.XmpAction.Remove : PdfSharpIO.XmpAction.Replace, xmp);
            SafeOutput.WriteBytes(output, bytes);
        }, cancellationToken);

    /// <summary>Writes a copy without document information, XMP packets or private application data.</summary>
    public static Task RemoveAllAsync(string input, string output, CancellationToken cancellationToken = default, string? password = null) =>
        Task.Run(() =>
        {
            SafeOutput.EnsureNotInput(input, output);
            using var document = PdfSharpIO.Open(input, PdfDocumentOpenMode.Modify, password);
            document.Info.Elements.Clear();
            var catalog = document.Internals.Catalog.Elements;
            catalog.Remove("/Metadata");
            catalog.Remove("/PieceInfo");
            foreach (PdfPage page in document.Pages)
            {
                page.Elements.Remove("/Metadata");
                page.Elements.Remove("/PieceInfo");
            }
            foreach (var obj in document.Internals.GetAllObjects())
                if (obj is PdfDictionary dict && dict.Elements.GetName("/Subtype") is "/Image" or "/Form")
                    dict.Elements.Remove("/Metadata");
            cancellationToken.ThrowIfCancellationRequested();
            var bytes = PdfSharpIO.SaveToBytes(document);
            bytes = PdfSharpIO.Finish(bytes, [], PdfSharpIO.XmpAction.Remove);
            SafeOutput.WriteBytes(output, PdfSharpIO.BlankWriterComments(bytes));
        }, cancellationToken);

    /// <summary>The information dictionary as encoded PDF values, for restoring it after a PDFsharp save.</summary>
    internal static List<(string Key, string Value)> InfoEntries(PdfDocumentInformation info)
    {
        var values = new Dictionary<string, string>();
        foreach (var key in info.Elements.Keys)
            if (InfoValue(info.Elements, key) is { } v)
                values[key.TrimStart('/')] = v;
        return Encode(values);
    }

    private static List<(string Key, string Value)> Encode(Dictionary<string, string> values)
    {
        var result = new List<(string, string)>();
        foreach (var (key, value) in values)
        {
            if (!IsValidName(key))
                continue;
            var encoded = key is "CreationDate" or "ModDate" && ParseDate(value) is { } date
                ? PdfSharpIO.EncodeDate(date)
                : PdfSharpIO.EncodeString(value);
            result.Add((key, encoded));
        }
        return result;
    }

    private static bool IsValidName(string key) =>
        key.Length > 0 && key.All(c => c > ' ' && c < 127 && "()<>[]{}/%#".IndexOf(c) < 0);

    private static string? InfoValue(PdfDictionary.DictionaryElements elements, string key)
    {
        var item = elements[key];
        if (item is PdfReference r)
            item = r.Value;
        switch (item)
        {
            case PdfDate d:
                return Format(d.Value);
            case PdfString s:
                if (key is "/CreationDate" or "/ModDate" && ParsePdfDate(s.Value) is { } parsed)
                    return parsed.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture);
                return s.Value;
            case PdfName n:
                return n.Value.TrimStart('/');
            case null:
                return null;
            default:
                return item.ToString();
        }
    }

    private static string Format(DateTime dt) =>
        new DateTimeOffset(dt).ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture);

    /// <summary>Parses "D:YYYYMMDDHHmmSSOHH'mm'" (any trailing parts optional).</summary>
    internal static DateTimeOffset? ParsePdfDate(string value)
    {
        var s = value.Trim();
        if (s.StartsWith("D:", StringComparison.Ordinal))
            s = s[2..];
        if (s.Length < 4 || !s[..4].All(char.IsAsciiDigit))
            return null;
        int Part(int start, int length, int fallback) =>
            s.Length >= start + length && int.TryParse(s.AsSpan(start, length), out var v) ? v : fallback;
        try
        {
            var dt = new DateTime(Part(0, 4, 1), Math.Max(1, Part(4, 2, 1)), Math.Max(1, Part(6, 2, 1)), Part(8, 2, 0), Part(10, 2, 0), Part(12, 2, 0));
            var offset = TimeSpan.Zero;
            var tz = s.Length > 14 ? s[14..] : "";
            if (tz.Length > 0 && tz[0] is '+' or '-')
            {
                var digits = new string(tz.Where(char.IsAsciiDigit).ToArray());
                var hours = digits.Length >= 2 ? int.Parse(digits[..2], CultureInfo.InvariantCulture) : 0;
                var minutes = digits.Length >= 4 ? int.Parse(digits[2..4], CultureInfo.InvariantCulture) : 0;
                offset = new TimeSpan(hours, minutes, 0) * (tz[0] == '-' ? -1 : 1);
            }
            return new DateTimeOffset(dt, offset);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    internal static DateTimeOffset? ParseDate(string value)
    {
        if (ParsePdfDate(value) is { } pdf && value.TrimStart().StartsWith("D:", StringComparison.Ordinal))
            return pdf;
        string[] formats = ["yyyy-MM-dd HH:mm:ss zzz", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm", "yyyy-MM-dd", "yyyy-MM-ddTHH:mm:ssK", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-ddTHH:mm"];
        if (DateTimeOffset.TryParseExact(value.Trim(), formats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var exact))
            return exact;
        if (DateTimeOffset.TryParse(value, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out var loose))
            return loose;
        return null;
    }

    // ─────────────── XMP ───────────────

    private static PdfDictionary? Xmp(PdfDocument document)
    {
        var item = document.Internals.Catalog.Elements["/Metadata"];
        if (item is PdfReference r)
            item = r.Value;
        return item is PdfDictionary { Stream: not null } dict ? dict : null;
    }

    private static XDocument? LoadXmp(PdfDictionary stream)
    {
        try
        {
            var bytes = stream.Stream!.UnfilteredValue;
            var text = Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF');
            var start = text.IndexOf("<x:xmpmeta", StringComparison.Ordinal);
            if (start < 0) start = text.IndexOf("<rdf:RDF", StringComparison.Ordinal);
            if (start < 0) return null;
            var endTag = text.Contains("</x:xmpmeta>", StringComparison.Ordinal) ? "</x:xmpmeta>" : "</rdf:RDF>";
            var end = text.IndexOf(endTag, start, StringComparison.Ordinal);
            if (end < 0) return null;
            return XDocument.Parse(text[start..(end + endTag.Length)]);
        }
        catch (Exception ex) when (ex is System.Xml.XmlException or DecoderFallbackException or InvalidOperationException)
        {
            return null;
        }
    }

    private static IEnumerable<(string Key, string Value)> XmpProperties(PdfDictionary stream)
    {
        var xmp = LoadXmp(stream);
        if (xmp is null)
            yield break;
        foreach (var description in xmp.Descendants(Rdf + "Description"))
        {
            foreach (var attr in description.Attributes().Where(a => !a.IsNamespaceDeclaration && a.Name.Namespace != Rdf))
                yield return (Qualified(description, attr.Name), attr.Value);
            foreach (var element in description.Elements())
            {
                var items = element.Descendants(Rdf + "li").Select(li => li.Value.Trim()).Where(v => v.Length > 0).ToList();
                var value = items.Count > 0 ? string.Join("; ", items) : element.HasElements ? "" : element.Value.Trim();
                if (value.Length > 0)
                    yield return (Qualified(description, element.Name), value);
            }
        }
    }

    private static string Qualified(XElement context, XName name)
    {
        var prefix = context.GetPrefixOfNamespace(name.Namespace);
        return prefix is null ? name.LocalName : $"{prefix}:{name.LocalName}";
    }

    /// <summary>The XMP packet with its standard properties brought in line with the information dictionary; everything else is kept.</summary>
    private static byte[]? UpdatedXmp(PdfDictionary stream, Dictionary<string, string> info)
    {
        var xmp = LoadXmp(stream);
        if (xmp?.Descendants(Rdf + "RDF").FirstOrDefault() is not { } rdf)
            return null;
        var description = rdf.Elements(Rdf + "Description").FirstOrDefault();
        if (description is null)
        {
            description = new XElement(Rdf + "Description", new XAttribute(Rdf + "about", ""));
            rdf.Add(description);
        }

        void Set(XName name, Func<string, XElement>? build, string? value)
        {
            foreach (var d in rdf.Elements(Rdf + "Description"))
            {
                d.Elements(name).Remove();
                d.Attribute(name)?.Remove();
            }
            if (string.IsNullOrEmpty(value))
                return;
            description.Add(build is null ? new XElement(name, value) : build(value));
        }
        XElement Alt(XName name, string v) => new(name, new XElement(Rdf + "Alt", new XElement(Rdf + "li", new XAttribute(Xml + "lang", "x-default"), v)));
        XElement Seq(XName name, string v) => new(name, new XElement(Rdf + "Seq", v.Split(';', ',').Select(s => s.Trim()).Where(s => s.Length > 0).Select(s => new XElement(Rdf + "li", s))));
        string? Date(string key) => info.TryGetValue(key, out var v) && ParseDate(v) is { } d ? d.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture) : null;

        Set(Dc + "title", v => Alt(Dc + "title", v), info.GetValueOrDefault("Title"));
        Set(Dc + "creator", v => Seq(Dc + "creator", v), info.GetValueOrDefault("Author"));
        Set(Dc + "description", v => Alt(Dc + "description", v), info.GetValueOrDefault("Subject"));
        Set(PdfNs + "Keywords", null, info.GetValueOrDefault("Keywords"));
        Set(PdfNs + "Producer", null, info.GetValueOrDefault("Producer"));
        Set(XmpNs + "CreatorTool", null, info.GetValueOrDefault("Creator"));
        Set(XmpNs + "CreateDate", null, Date("CreationDate"));
        Set(XmpNs + "ModifyDate", null, Date("ModDate"));
        foreach (var (ns, prefix) in new[] { (Dc, "dc"), (PdfNs, "pdf"), (XmpNs, "xmp") })
            if (description.GetPrefixOfNamespace(ns) is null && rdf.GetPrefixOfNamespace(ns) is null)
                description.Add(new XAttribute(XNamespace.Xmlns + prefix, ns));

        var packet = "<?xpacket begin=\"\uFEFF\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?>\n" +
                     xmp.Root!.ToString(SaveOptions.DisableFormatting) +
                     "\n<?xpacket end=\"w\"?>";
        return Encoding.UTF8.GetBytes(packet);
    }

    /// <summary>The document's XMP packet as stored, or null.</summary>
    internal static byte[]? RawXmp(PdfDocument document)
    {
        try
        {
            return Xmp(document)?.Stream?.UnfilteredValue;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }
}
