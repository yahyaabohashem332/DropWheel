using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Tomlyn;
using Tomlyn.Model;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Petal.Engines.Documents.Data;

/// <summary>Data that can't take the shape asked for; the message says why, in words for people.</summary>
public sealed class DataShapeException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// JSON, JSON Lines, YAML, TOML and XML read into one tree of values (System.Text.Json's nodes) and written back
/// out of it. Numbers keep the digits they were written with; YAML's plain words become numbers, true, false or
/// null only as YAML 1.2 says, and quoted ones stay text.
/// </summary>
internal static partial class DataTree
{
    private static readonly JsonDocumentOptions ReadOptions = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip, MaxDepth = 256 };

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static readonly JsonSerializerOptions LineOptions = new() { WriteIndented = false, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    // ---------- JSON ----------

    public static JsonNode? ReadJson(string text)
    {
        try
        {
            return JsonNode.Parse(text, documentOptions: ReadOptions);
        }
        catch (JsonException ex)
        {
            throw new DataShapeException($"This isn't valid JSON (line {ex.LineNumber + 1}).", ex);
        }
    }

    public static string WriteJson(JsonNode? node) => (node?.ToJsonString(WriteOptions) ?? "null") + "\n";

    /// <summary>JSON Lines: one value a line; blank lines are skipped.</summary>
    public static JsonArray ReadJsonLines(string text)
    {
        var array = new JsonArray();
        var lines = text.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0)
                continue;
            try
            {
                array.Add(JsonNode.Parse(line, documentOptions: ReadOptions));
            }
            catch (JsonException ex)
            {
                throw new DataShapeException($"Line {i + 1} isn't valid JSON.", ex);
            }
        }
        return array;
    }

    public static string WriteJsonLines(JsonNode? node)
    {
        if (node is not JsonArray array)
            throw new DataShapeException("Only a list can become JSON Lines: one item a line.");
        var text = new StringBuilder();
        foreach (var item in array)
            text.Append(item?.ToJsonString(LineOptions) ?? "null").Append('\n');
        return text.ToString();
    }

    // ---------- YAML ----------

    /// <summary>A YAML file; several documents in one file (separated by ---) become a list.</summary>
    public static JsonNode? ReadYaml(string text)
    {
        var stream = new YamlStream();
        try
        {
            stream.Load(new StringReader(text));
        }
        catch (YamlException ex)
        {
            throw new DataShapeException($"This isn't valid YAML (line {ex.Start.Line}).", ex);
        }
        catch (InvalidOperationException ex)
        {
            // YamlDotNet's scanner gives up this way on some broken files.
            throw new DataShapeException("This isn't valid YAML.", ex);
        }
        if (stream.Documents.Count == 0)
            return null;
        if (stream.Documents.Count == 1)
            return FromYaml(stream.Documents[0].RootNode, 0);
        return new JsonArray(stream.Documents.Select(d => FromYaml(d.RootNode, 0)).ToArray());
    }

    private static JsonNode? FromYaml(YamlNode node, int depth)
    {
        if (depth > 256)
            throw new DataShapeException("This YAML is nested too deeply.");
        switch (node)
        {
            case YamlMappingNode map:
            {
                var obj = new JsonObject();
                foreach (var (key, value) in map.Children)
                {
                    var name = key is YamlScalarNode s ? s.Value ?? "" : key.ToString();
                    // "<<" merges another mapping in (YAML 1.1); its own keys win.
                    if (name == "<<" && value is YamlMappingNode or YamlSequenceNode)
                    {
                        foreach (var merged in value is YamlSequenceNode many ? many.Children : [value])
                            if (FromYaml(merged, depth + 1) is JsonObject from)
                                foreach (var (k, v) in from)
                                    if (!obj.ContainsKey(k) && !map.Children.Keys.Any(x => x is YamlScalarNode xs && xs.Value == k))
                                        obj[k] = v?.DeepClone();
                        continue;
                    }
                    obj[name] = FromYaml(value, depth + 1);
                }
                return obj;
            }
            case YamlSequenceNode sequence:
                return new JsonArray(sequence.Children.Select(c => FromYaml(c, depth + 1)).ToArray());
            case YamlScalarNode scalar:
                return Scalar(scalar);
            default:
                return null;
        }
    }

    /// <summary>YAML 1.2's core schema: plain null, true and false, and numbers; anything quoted is text.</summary>
    private static JsonNode? Scalar(YamlScalarNode scalar)
    {
        var text = scalar.Value ?? "";
        var tag = scalar.Tag.IsEmpty ? "" : scalar.Tag.Value;
        if (tag is "tag:yaml.org,2002:str")
            return JsonValue.Create(text);
        if (scalar.Style != ScalarStyle.Plain && !tag.StartsWith("tag:yaml.org,2002:", StringComparison.Ordinal))
            return JsonValue.Create(text);
        if (text is "" or "~" or "null" or "Null" or "NULL")
            return null;
        if (text is "true" or "True" or "TRUE")
            return JsonValue.Create(true);
        if (text is "false" or "False" or "FALSE")
            return JsonValue.Create(false);
        if (Number(text) is { } number)
            return number;
        return JsonValue.Create(text);
    }

    /// <summary>A number as YAML or TOML writes it, kept with its own digits; hex, octal and binary become decimal.</summary>
    private static JsonNode? Number(string text)
    {
        var t = text.Replace("_", "");
        if (YamlInteger().IsMatch(t))
        {
            if (long.TryParse(t, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _))
                return JsonNode.Parse(t.TrimStart('+'));
            return null;
        }
        if (t.Length > 2 && t[0] == '0' && t[1] is 'x' or 'o' or 'b')
        {
            try
            {
                return JsonValue.Create(Convert.ToInt64(t[2..], t[1] switch { 'x' => 16, 'o' => 8, _ => 2 }));
            }
            catch (Exception ex) when (ex is FormatException or OverflowException)
            {
                return null;
            }
        }
        if (YamlFloat().IsMatch(t) && double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && double.IsFinite(d))
            return JsonNode.Parse(Normalise(t));
        return null;
    }

    /// <summary>A number in JSON's own form: "+1.5" → "1.5", ".5" → "0.5", "5." → "5.0".</summary>
    private static string Normalise(string t)
    {
        t = t.TrimStart('+');
        if (t.StartsWith('.')) t = "0" + t;
        if (t.StartsWith("-.", StringComparison.Ordinal)) t = "-0" + t[1..];
        t = t.Replace(".e", ".0e").Replace(".E", ".0E");
        return t.EndsWith('.') ? t + "0" : t;
    }

    [GeneratedRegex(@"^[-+]?(0|[1-9][0-9]*)$")]
    private static partial Regex YamlInteger();

    [GeneratedRegex(@"^[-+]?(\.[0-9]+|[0-9]+(\.[0-9]*)?)([eE][-+]?[0-9]+)?$")]
    private static partial Regex YamlFloat();

    public static string WriteYaml(JsonNode? node)
    {
        var writer = new StringWriter();
        new YamlStream(new YamlDocument(ToYaml(node))).Save(writer, assignAnchors: false);
        var text = writer.ToString().Replace("\r\n", "\n");
        // The document's end marker adds nothing for one document.
        if (text.EndsWith("...\n", StringComparison.Ordinal))
            text = text[..^4];
        return text.TrimEnd('\n') + "\n";
    }

    private static YamlNode ToYaml(JsonNode? node) => node switch
    {
        JsonObject obj => new YamlMappingNode(obj.Select(p => new KeyValuePair<YamlNode, YamlNode>(Text(p.Key), ToYaml(p.Value)))),
        JsonArray array => new YamlSequenceNode(array.Select(ToYaml)),
        JsonValue value => value.GetValueKind() switch
        {
            JsonValueKind.String => Text(value.GetValue<string>()),
            JsonValueKind.Number => new YamlScalarNode(value.ToJsonString()) { Style = ScalarStyle.Plain },
            JsonValueKind.True => new YamlScalarNode("true") { Style = ScalarStyle.Plain },
            JsonValueKind.False => new YamlScalarNode("false") { Style = ScalarStyle.Plain },
            _ => new YamlScalarNode("null") { Style = ScalarStyle.Plain },
        },
        _ => new YamlScalarNode("null") { Style = ScalarStyle.Plain },
    };

    /// <summary>Text, quoted whenever YAML would read it as something else (a number, true, null) or not at all.</summary>
    private static YamlScalarNode Text(string text)
    {
        bool ambiguous = Scalar(new YamlScalarNode(text) { Style = ScalarStyle.Plain }) is not JsonValue v || v.GetValueKind() != JsonValueKind.String
            || text.Length == 0 || text != text.Trim() || text.IndexOfAny(['\n', '\r', '\t', '#', ':', '\'', '"', '{', '}', '[', ']', ',', '&', '*', '!', '|', '>', '%', '@', '`']) >= 0
            || text[0] is '-' or '?' || text is "yes" or "no" or "on" or "off" or "Yes" or "No" or "On" or "Off" or "y" or "n" or "Y" or "N";
        return new YamlScalarNode(text) { Style = ambiguous ? ScalarStyle.DoubleQuoted : ScalarStyle.Plain };
    }

    // ---------- TOML ----------

    public static JsonNode? ReadToml(string text)
    {
        TomlTable table;
        try
        {
            table = TomlSerializer.Deserialize<TomlTable>(text) ?? new TomlTable();
        }
        catch (TomlException ex)
        {
            throw new DataShapeException("This isn't valid TOML: " + FirstLine(ex.Message), ex);
        }
        return FromToml(table);
    }

    private static string FirstLine(string text) => text.Split('\n')[0].Trim();

    private static JsonNode? FromToml(object? value) => value switch
    {
        null => null,
        TomlTable table => new JsonObject(table.Select(p => new KeyValuePair<string, JsonNode?>(p.Key, FromToml(p.Value)))),
        TomlTableArray tables => new JsonArray(tables.Select(t => FromToml(t)).ToArray()),
        TomlArray array => new JsonArray(array.Select(FromToml).ToArray()),
        string s => JsonValue.Create(s),
        bool b => JsonValue.Create(b),
        long l => JsonValue.Create(l),
        int i => JsonValue.Create(i),
        double d => double.IsFinite(d) ? JsonValue.Create(d) : JsonValue.Create(d.ToString(CultureInfo.InvariantCulture)),
        float f => JsonValue.Create((double)f),
        TomlDateTime dt => JsonValue.Create(dt.ToString()),
        DateTime dt => JsonValue.Create(dt.ToString("o", CultureInfo.InvariantCulture)),
        DateTimeOffset dto => JsonValue.Create(dto.ToString("o", CultureInfo.InvariantCulture)),
        TimeOnly t => JsonValue.Create(t.ToString("HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture)),
        DateOnly d => JsonValue.Create(d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
        _ => JsonValue.Create(Convert.ToString(value, CultureInfo.InvariantCulture)),
    };

    /// <summary>TOML holds named values at its top and has no null: such values are left out (and counted).</summary>
    public static string WriteToml(JsonNode? node, out int nullsLeftOut)
    {
        if (node is not JsonObject obj)
            throw new DataShapeException("TOML needs named values at the top, not a list or a single value.");
        int nulls = 0;
        var table = (TomlTable)ToToml(obj, ref nulls)!;
        nullsLeftOut = nulls;
        return TomlSerializer.Serialize(table).Replace("\r\n", "\n");
    }

    private static object? ToToml(JsonNode? node, ref int nulls)
    {
        switch (node)
        {
            case JsonObject obj:
            {
                var table = new TomlTable();
                foreach (var (key, value) in obj)
                {
                    var converted = ToToml(value, ref nulls);
                    if (converted is null)
                    {
                        nulls++;
                        continue;
                    }
                    table[key] = converted;
                }
                return table;
            }
            case JsonArray array when array.Count > 0 && array.All(i => i is JsonObject):
            {
                var tables = new TomlTableArray();
                foreach (var item in array)
                    tables.Add((TomlTable)ToToml(item, ref nulls)!);
                return tables;
            }
            case JsonArray array:
            {
                var list = new TomlArray();
                foreach (var item in array)
                {
                    var converted = ToToml(item, ref nulls);
                    if (converted is null)
                        nulls++;
                    else
                        list.Add(converted);
                }
                return list;
            }
            case JsonValue value:
                return value.GetValueKind() switch
                {
                    JsonValueKind.String => value.GetValue<string>(),
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.Number => value.TryGetValue<long>(out var l) ? l
                        : long.TryParse(value.ToJsonString(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out l) ? l
                        : double.Parse(value.ToJsonString(), CultureInfo.InvariantCulture),
                    _ => null,
                };
            default:
                return null;
        }
    }

    // ---------- XML ----------

    /// <summary>
    /// XML as a tree: the root element by its name; an element with only text is that text; otherwise attributes
    /// are "@name", the element's own text "#text", and children that repeat a name a list.
    /// </summary>
    public static JsonNode ReadXml(string text)
    {
        XDocument document;
        try
        {
            document = XDocument.Parse(text, LoadOptions.None);
        }
        catch (XmlException ex)
        {
            throw new DataShapeException($"This isn't valid XML (line {ex.LineNumber}).", ex);
        }
        var root = document.Root ?? throw new DataShapeException("This XML has no elements.");
        return new JsonObject { [Name(root)] = FromXml(root) };
    }

    private static string Name(XElement element) =>
        element.GetPrefixOfNamespace(element.Name.Namespace) is { Length: > 0 } prefix ? $"{prefix}:{element.Name.LocalName}" : element.Name.LocalName;

    private static string Name(XAttribute attribute) =>
        attribute.Parent?.GetPrefixOfNamespace(attribute.Name.Namespace) is { Length: > 0 } prefix ? $"{prefix}:{attribute.Name.LocalName}" : attribute.Name.LocalName;

    private static JsonNode? FromXml(XElement element)
    {
        var attributes = element.Attributes().Where(a => !a.IsNamespaceDeclaration).ToList();
        var children = element.Elements().ToList();
        var ownText = string.Concat(element.Nodes().OfType<XText>().Select(t => t.Value)).Trim();
        if (attributes.Count == 0 && children.Count == 0)
            return JsonValue.Create(element.Value);
        var obj = new JsonObject();
        foreach (var attribute in attributes)
            obj["@" + Name(attribute)] = attribute.Value;
        foreach (var group in children.GroupBy(Name))
        {
            var items = group.Select(FromXml).ToArray();
            obj[group.Key] = items.Length == 1 ? items[0] : new JsonArray(items);
        }
        if (ownText.Length > 0)
            obj["#text"] = ownText;
        return obj;
    }

    /// <summary>The tree as XML; read back by <see cref="ReadXml"/> it gives the same tree.</summary>
    public static string WriteXml(JsonNode? node)
    {
        XElement root;
        if (node is JsonObject { Count: 1 } single && single.First().Value is JsonObject or JsonArray or JsonValue)
        {
            var (name, value) = single.First();
            root = value is JsonArray list ? new XElement(XmlName("root"), list.Select(i => Element(name, i))) : Element(name, value);
        }
        else
        {
            root = Element("root", node);
        }
        var document = new XDocument(new XDeclaration("1.0", "utf-8", null), root);
        var text = new StringBuilder();
        using (var writer = XmlWriter.Create(text, new XmlWriterSettings { Indent = true, IndentChars = "  ", OmitXmlDeclaration = false, NewLineChars = "\n" }))
            document.Save(writer);
        return text.ToString().Replace("encoding=\"utf-16\"", "encoding=\"utf-8\"") + "\n";
    }

    private static XName XmlName(string name)
    {
        if (name.Length == 0)
            name = "item";
        // "prefix:name" keeps its prefix only as part of the name: no namespaces are made up.
        return XmlConvert.EncodeLocalName(name.Replace(':', '_'))!;
    }

    private static XElement Element(string name, JsonNode? node)
    {
        var element = new XElement(XmlName(name));
        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, value) in obj)
                {
                    if (key.StartsWith('@') && key.Length > 1 && value is JsonValue attribute)
                        element.SetAttributeValue(XmlName(key[1..]), Plain(attribute));
                    else if (key == "#text" && value is JsonValue text)
                        element.Add(new XText(Plain(text)));
                    else if (value is JsonArray items)
                        foreach (var item in items)
                            element.Add(Element(key, item));
                    else
                        element.Add(Element(key, value));
                }
                break;
            case JsonArray array:
                foreach (var item in array)
                    element.Add(Element("item", item));
                break;
            case JsonValue value:
                element.Value = Plain(value);
                break;
        }
        return element;
    }

    /// <summary>A single value as plain text: strings as they are, numbers with their own digits.</summary>
    public static string Plain(JsonValue value) => value.GetValueKind() switch
    {
        JsonValueKind.String => value.GetValue<string>(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Null => "",
        _ => value.ToJsonString(),
    };
}
