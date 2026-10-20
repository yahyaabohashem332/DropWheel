using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Petal.Engines.Imaging.Metadata;

internal enum XmpKind
{
    Simple,
    Alt,
    Seq,
    Bag,
    Struct,
}

/// <summary>A top-level XMP property, flattened for display.</summary>
internal sealed record XmpProperty(string Namespace, string Prefix, string Local, string Value, XmpKind Kind)
{
    public string Id => $"xmp:{Namespace}#{Local}";

    public string Key => string.IsNullOrEmpty(Prefix) ? Local : $"{Prefix}:{Local}";

    public bool IsEditable => Kind is XmpKind.Simple or XmpKind.Alt or XmpKind.Seq or XmpKind.Bag;
}

/// <summary>Reads and edits the RDF properties of an XMP packet.</summary>
internal sealed class XmpDocument
{
    public static readonly XNamespace Rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
    public static readonly XNamespace Meta = "adobe:ns:meta/";
    public const string DublinCore = "http://purl.org/dc/elements/1.1/";
    public const string ExifNs = "http://ns.adobe.com/exif/1.0/";
    private static readonly XNamespace XmlNs = "http://www.w3.org/XML/1998/namespace";

    private XmpDocument(XDocument document) => Document = document;

    public XDocument Document { get; }

    public static XmpDocument? Parse(byte[] bytes)
    {
        try
        {
            var text = Encoding.UTF8.GetString(bytes).Trim('\0', ' ', '\r', '\n', '﻿');
            var doc = XDocument.Parse(text, System.Xml.Linq.LoadOptions.PreserveWhitespace);
            return doc.Descendants(Rdf + "RDF").Any() ? new XmpDocument(doc) : null;
        }
        catch (XmlException)
        {
            return null;
        }
    }

    public static XmpDocument CreateEmpty() => new(new XDocument(
        new XElement(Meta + "xmpmeta", new XAttribute(XNamespace.Xmlns + "x", Meta.NamespaceName),
            new XElement(Rdf + "RDF", new XAttribute(XNamespace.Xmlns + "rdf", Rdf.NamespaceName),
                new XElement(Rdf + "Description", new XAttribute(Rdf + "about", ""))))));

    public byte[] ToBytes()
    {
        var hasPacket = Document.Nodes().OfType<XProcessingInstruction>().Any(p => p.Target == "xpacket");
        var body = Document.ToString(SaveOptions.DisableFormatting);
        if (!hasPacket)
            body = "<?xpacket begin=\"﻿\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?>" + body + "<?xpacket end=\"w\"?>";
        return new UTF8Encoding(false).GetBytes(body);
    }

    private IEnumerable<XElement> Descriptions => Document.Descendants(Rdf + "Description").Where(d => d.Parent?.Name == Rdf + "RDF");

    public IReadOnlyList<XmpProperty> Properties()
    {
        var list = new List<XmpProperty>();
        foreach (var description in Descriptions)
        {
            foreach (var attribute in description.Attributes())
            {
                if (attribute.IsNamespaceDeclaration || attribute.Name.Namespace == Rdf || attribute.Name.Namespace == XNamespace.None)
                    continue;
                list.Add(new XmpProperty(attribute.Name.NamespaceName, PrefixOf(description, attribute.Name.Namespace), attribute.Name.LocalName,
                    attribute.Value, XmpKind.Simple));
            }
            foreach (var element in description.Elements())
            {
                var (kind, value) = Describe(element);
                list.Add(new XmpProperty(element.Name.NamespaceName, PrefixOf(element, element.Name.Namespace), element.Name.LocalName, value, kind));
            }
        }
        return list;
    }

    public XmpProperty? Find(string ns, string local) => Properties().FirstOrDefault(p => p.Namespace == ns && p.Local == local);

    /// <summary>Changes an existing property; arrays take "a; b; c".</summary>
    public bool Set(string ns, string local, string value)
    {
        XName name = XName.Get(local, ns);
        foreach (var description in Descriptions)
        {
            if (description.Attribute(name) is { } attribute)
            {
                attribute.Value = value;
                return true;
            }
            if (description.Element(name) is { } element)
            {
                var (kind, _) = Describe(element);
                switch (kind)
                {
                    case XmpKind.Simple:
                        element.Value = value;
                        return true;
                    case XmpKind.Alt:
                        var items = element.Element(Rdf + "Alt")!.Elements(Rdf + "li").ToList();
                        var target = items.FirstOrDefault(li => (string?)li.Attribute(XmlNs + "lang") == "x-default") ?? items.FirstOrDefault();
                        if (target is null)
                            element.Element(Rdf + "Alt")!.Add(target = new XElement(Rdf + "li", new XAttribute(XmlNs + "lang", "x-default")));
                        target.Value = value;
                        return true;
                    case XmpKind.Seq or XmpKind.Bag:
                        var container = element.Element(Rdf + (kind == XmpKind.Seq ? "Seq" : "Bag"))!;
                        container.RemoveNodes();
                        foreach (var part in SplitList(value))
                            container.Add(new XElement(Rdf + "li", part));
                        return true;
                    default:
                        return false;
                }
            }
        }
        return false;
    }

    /// <summary>Sets an ordered list property, creating it (and its namespace) when missing.</summary>
    public void SetList(string ns, string prefix, string local, IEnumerable<string> values, XmpKind kind = XmpKind.Seq)
    {
        var joined = string.Join("; ", values);
        if (Find(ns, local) is not null && Set(ns, local, joined))
            return;
        var description = Descriptions.FirstOrDefault() ?? CreateDescription();
        if (description.GetPrefixOfNamespace(ns) is null && description.Document?.Root?.GetPrefixOfNamespace(ns) is null)
            description.Add(new XAttribute(XNamespace.Xmlns + prefix, ns));
        var container = new XElement(Rdf + (kind == XmpKind.Bag ? "Bag" : "Seq"), values.Select(v => new XElement(Rdf + "li", v)));
        description.Add(new XElement(XName.Get(local, ns), container));
    }

    public bool Remove(string ns, string local)
    {
        XName name = XName.Get(local, ns);
        bool removed = false;
        foreach (var description in Descriptions.ToList())
        {
            if (description.Attribute(name) is { } attribute)
            {
                attribute.Remove();
                removed = true;
            }
            foreach (var element in description.Elements(name).ToList())
            {
                element.Remove();
                removed = true;
            }
        }
        return removed;
    }

    public void RemoveWhere(Func<XmpProperty, bool> predicate)
    {
        foreach (var p in Properties().Where(predicate).ToList())
            Remove(p.Namespace, p.Local);
    }

    /// <summary>Sets a simple text property, creating it when missing.</summary>
    public void SetSimple(string ns, string prefix, string local, string value)
    {
        if (Set(ns, local, value))
            return;
        var description = Descriptions.FirstOrDefault() ?? CreateDescription();
        if (description.GetPrefixOfNamespace(ns) is null)
            description.Add(new XAttribute(XNamespace.Xmlns + prefix, ns));
        description.Add(new XAttribute(XName.Get(local, ns), value));
    }

    private XElement CreateDescription()
    {
        var rdf = Document.Descendants(Rdf + "RDF").First();
        var description = new XElement(Rdf + "Description", new XAttribute(Rdf + "about", ""));
        rdf.Add(description);
        return description;
    }

    private static (XmpKind Kind, string Value) Describe(XElement element)
    {
        if (element.Element(Rdf + "Alt") is { } alt)
        {
            var items = alt.Elements(Rdf + "li").ToList();
            var preferred = items.FirstOrDefault(li => (string?)li.Attribute(XmlNs + "lang") == "x-default") ?? items.FirstOrDefault();
            return (XmpKind.Alt, preferred?.Value.Trim() ?? "");
        }
        if (element.Element(Rdf + "Seq") is { } seq)
            return ListOf(XmpKind.Seq, seq);
        if (element.Element(Rdf + "Bag") is { } bag)
            return ListOf(XmpKind.Bag, bag);
        if (element.HasElements || (string?)element.Attribute(Rdf + "parseType") == "Resource")
        {
            // Structures: show "field=value" pairs, edit only by removing the whole property.
            var parts = element.Descendants()
                .Where(d => !d.HasElements && d.Name.Namespace != Rdf && !string.IsNullOrWhiteSpace(d.Value))
                .Select(d => $"{d.Name.LocalName}={d.Value.Trim()}")
                .Concat(element.Descendants(Rdf + "Description").SelectMany(d => d.Attributes())
                    .Where(a => !a.IsNamespaceDeclaration && a.Name.Namespace != Rdf)
                    .Select(a => $"{a.Name.LocalName}={a.Value}"));
            return (XmpKind.Struct, string.Join(", ", parts.Take(12)));
        }
        return (XmpKind.Simple, element.Value.Trim());
    }

    private static (XmpKind, string) ListOf(XmpKind kind, XElement container)
    {
        var values = container.Elements(Rdf + "li").Select(li => li.HasElements ? string.Join(" ", li.Descendants().Where(d => !d.HasElements).Select(d => d.Value.Trim())) : li.Value.Trim());
        return (kind, string.Join("; ", values));
    }

    private static string PrefixOf(XElement context, XNamespace ns) => context.GetPrefixOfNamespace(ns) ?? "";

    private static IEnumerable<string> SplitList(string value) =>
        value.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
}
