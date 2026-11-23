using System.Globalization;
using System.IO.Compression;
using System.Numerics;
using System.Text;
using System.Xml.Linq;

namespace Petal.Engines.Files.Models;

/// <summary>
/// 3MF, the 3D Manufacturing Format slicers and Windows use: a ZIP whose model part lists objects (meshes, or
/// components of other objects, each with its transform), the build's items, the unit, and colours (base materials,
/// and the Materials extension's colour groups). Read whole; written in millimetres, a part an object, colours kept.
/// </summary>
internal static class ThreeMf
{
    private static readonly XNamespace Core = "http://schemas.microsoft.com/3dmanufacturing/core/2015/02";
    private static readonly XNamespace Mat = "http://schemas.microsoft.com/3dmanufacturing/material/2015/02";
    private static readonly XNamespace Production = "http://schemas.microsoft.com/3dmanufacturing/production/2015/06";
    private const string ModelRelationship = "http://schemas.microsoft.com/3dmanufacturing/2013/01/3dmodel";
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>One model part of the package (the root, or another the Production extension points at).</summary>
    private sealed class ModelFile(string name, XElement root)
    {
        public string Name { get; } = name;
        public XElement Root { get; } = root;
        public XNamespace Ns => Root.Name.Namespace;
        public Dictionary<string, XElement> Objects { get; } = new(StringComparer.Ordinal);
        public Dictionary<(string Group, int Index), int> MaterialOf { get; } = [];
        public Dictionary<string, List<Vector4>> ColorGroups { get; } = new(StringComparer.Ordinal);
    }

    public static Model3D Read(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var main = ModelPart(zip) ?? throw new InvalidDataException("This 3MF file holds no model.");
        var model = new Model3D { Name = Path.GetFileNameWithoutExtension(path) };
        var white = model.DefaultMaterial();
        var files = new Dictionary<string, ModelFile>(StringComparer.OrdinalIgnoreCase);

        // A model part, read once: its objects, base materials (a material each) and colour groups (a colour each).
        ModelFile FileOf(string name)
        {
            if (files.TryGetValue(name, out var known))
                return known;
            var entry = zip.GetEntry(name) ?? throw new InvalidDataException("This 3MF file points at a part it doesn't hold.");
            XDocument xml;
            using (var stream = entry.Open())
                xml = XDocument.Load(stream);
            var file = new ModelFile(name, xml.Root ?? throw new InvalidDataException("This 3MF file's model is empty."));
            files[name] = file;
            var resources = file.Root.Element(file.Ns + "resources");
            if (resources is null)
                return file;
            foreach (var group in resources.Elements(file.Ns + "basematerials"))
            {
                var id = (string?)group.Attribute("id") ?? "";
                var index = 0;
                foreach (var b in group.Elements(file.Ns + "base"))
                {
                    model.Materials.Add(new Material3D((string?)b.Attribute("name") ?? $"material {model.Materials.Count}", Color((string?)b.Attribute("displaycolor"))));
                    file.MaterialOf[(id, index++)] = model.Materials.Count - 1;
                }
            }
            foreach (var group in resources.Elements(Mat + "colorgroup"))
                file.ColorGroups[(string?)group.Attribute("id") ?? ""] = [.. group.Elements(Mat + "color").Select(c => Color((string?)c.Attribute("color")))];
            foreach (var o in resources.Elements(file.Ns + "object"))
                file.Objects[(string?)o.Attribute("id") ?? ""] = o;
            return file;
        }

        // Where an item or component's object is: its own part when it names one (Bambu Studio keeps each object's
        // mesh in a part of its own), else the part it is in.
        string Where(XElement e, string current) =>
            (string?)e.Attribute(Production + "path") is { Length: > 0 } other ? Uri.UnescapeDataString(other.TrimStart('/')) : current;

        var rootFile = FileOf(main.FullName);
        model.MetresPerUnit = ((string?)rootFile.Root.Attribute("unit"))?.ToLowerInvariant() switch
        {
            "micron" => 1e-6, "centimeter" => 0.01, "inch" => 0.0254, "foot" => 0.3048, "meter" => 1, _ => Model3D.Millimetre,
        };
        var anyColor = false;
        var colors = new List<Vector4>();
        var vertexOf = new Dictionary<(int Instance, int Vertex, Vector4 Color), int>();
        var instance = 0;

        void Add(ModelFile file, XElement obj, Matrix4x4 transform, int depth, string? itemName)
        {
            if (depth > 32)
                throw new InvalidDataException("This 3MF file's objects contain each other.");
            var ns = file.Ns;
            var name = (string?)obj.Attribute("name") is { Length: > 0 } own ? own : itemName ?? model.Name ?? "model";
            if (obj.Element(ns + "components") is { } components)
            {
                foreach (var component in components.Elements(ns + "component"))
                {
                    var inner = FileOf(Where(component, file.Name));
                    if (inner.Objects.TryGetValue((string?)component.Attribute("objectid") ?? "", out var part))
                        Add(inner, part, Transform((string?)component.Attribute("transform")) * transform, depth + 1, name);
                }
                return;
            }
            if (obj.Element(ns + "mesh") is not { } mesh)
                return;
            var vertices = mesh.Element(ns + "vertices")?.Elements(ns + "vertex")
                .Select(v => Vector3.Transform(new Vector3(F(v, "x"), F(v, "y"), F(v, "z")), transform)).ToList() ?? [];
            var objectGroup = (string?)obj.Attribute("pid");
            var objectIndex = (int?)obj.Attribute("pindex") ?? 0;
            var me = instance++;
            var first = model.Triangles.Count;
            foreach (var t in mesh.Element(ns + "triangles")?.Elements(ns + "triangle") ?? [])
            {
                int[] corners = [(int)t.Attribute("v1")!, (int)t.Attribute("v2")!, (int)t.Attribute("v3")!];
                if (corners.Any(c => (uint)c >= vertices.Count))
                    throw new InvalidDataException("A triangle of this 3MF file points at a corner it doesn't have.");
                var group = (string?)t.Attribute("pid") ?? objectGroup;
                var p1 = (int?)t.Attribute("p1") ?? objectIndex;
                int[] properties = [p1, (int?)t.Attribute("p2") ?? p1, (int?)t.Attribute("p3") ?? p1];
                var material = white;
                var cornerColors = new[] { Vector4.One, Vector4.One, Vector4.One };
                if (group is not null && file.MaterialOf.TryGetValue((group, p1), out var m))
                    material = m;
                else if (group is not null && file.ColorGroups.TryGetValue(group, out var list))
                {
                    for (var k = 0; k < 3; k++)
                        cornerColors[k] = properties[k] < list.Count ? list[properties[k]] : Vector4.One;
                    anyColor = true;
                }
                var ids = new int[3];
                for (var k = 0; k < 3; k++)
                {
                    var key = (me, corners[k], cornerColors[k]);
                    if (!vertexOf.TryGetValue(key, out var id))
                    {
                        id = model.Positions.Count;
                        model.Positions.Add(vertices[corners[k]]);
                        colors.Add(cornerColors[k]);
                        vertexOf[key] = id;
                    }
                    ids[k] = id;
                }
                model.Triangles.Add((ids[0], ids[1], ids[2], material));
            }
            if (model.Triangles.Count > first)
                model.Parts.Add(new Part3D(name, first, model.Triangles.Count - first));
        }

        foreach (var item in rootFile.Root.Element(rootFile.Ns + "build")?.Elements(rootFile.Ns + "item") ?? [])
        {
            var file = FileOf(Where(item, main.FullName));
            if (file.Objects.TryGetValue((string?)item.Attribute("objectid") ?? "", out var obj))
                Add(file, obj, Transform((string?)item.Attribute("transform")), 0, null);
        }
        if (anyColor)
            model.Colors = colors;
        model.Check();
        return model;
    }

    private static ZipArchiveEntry? ModelPart(ZipArchive zip)
    {
        if (zip.GetEntry("_rels/.rels") is { } rels)
        {
            using var stream = rels.Open();
            var target = XDocument.Load(stream).Root?.Elements()
                .FirstOrDefault(r => (string?)r.Attribute("Type") == ModelRelationship)?.Attribute("Target")?.Value;
            if (target is not null && zip.GetEntry(Uri.UnescapeDataString(target.TrimStart('/'))) is { } entry)
                return entry;
        }
        return zip.GetEntry("3D/3dmodel.model") ?? zip.Entries.FirstOrDefault(e => e.FullName.EndsWith(".model", StringComparison.OrdinalIgnoreCase));
    }

    private static float F(XElement e, string name) =>
        float.TryParse((string?)e.Attribute(name), NumberStyles.Float, Inv, out var v) ? v : 0f;

    /// <summary>3MF's transform: twelve numbers, a 3×4 matrix applied to points as rows.</summary>
    private static Matrix4x4 Transform(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return Matrix4x4.Identity;
        var m = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(s => float.Parse(s, NumberStyles.Float, Inv)).ToArray();
        if (m.Length != 12)
            return Matrix4x4.Identity;
        return new Matrix4x4(m[0], m[1], m[2], 0, m[3], m[4], m[5], 0, m[6], m[7], m[8], 0, m[9], m[10], m[11], 1);
    }

    /// <summary>"#RRGGBB" or "#RRGGBBAA", as shown (sRGB), 0–1.</summary>
    internal static Vector4 Color(string? hex)
    {
        if (hex is null || hex.Length < 7 || hex[0] != '#')
            return Vector4.One;
        float Byte(int at) => int.Parse(hex.AsSpan(at, 2), NumberStyles.HexNumber, Inv) / 255f;
        return new Vector4(Byte(1), Byte(3), Byte(5), hex.Length >= 9 ? Byte(7) : 1);
    }

    internal static string Hex(Vector4 c)
    {
        var b = Vector4.Clamp(c, Vector4.Zero, Vector4.One) * 255f;
        return $"#{(int)MathF.Round(b.X):X2}{(int)MathF.Round(b.Y):X2}{(int)MathF.Round(b.Z):X2}{(int)MathF.Round(b.W):X2}";
    }

    public static void Write(Model3D model, string path)
    {
        var factor = model.FactorTo(Model3D.Millimetre);
        var resources = new XElement(Core + "resources");
        var perVertex = model.Colors is not null;
        var materials = model.Materials.Count > 0 ? model.Materials : [new Material3D("default", Vector4.One)];
        // Colours: each material as a base material; with corners' own colours, a colour group of what each corner
        // shows (its colour times its triangle's material's), filled as the triangles are written.
        resources.Add(new XElement(Core + "basematerials", new XAttribute("id", 1),
            materials.Select((m, i) => new XElement(Core + "base", new XAttribute("name", m.Name.Length > 0 ? m.Name : $"material {i + 1}"), new XAttribute("displaycolor", Hex(m.Color))))));
        var colorGroup = new XElement(Mat + "colorgroup", new XAttribute("id", 2));
        var colorIndex = new Dictionary<(int Vertex, int Material), int>();
        int ColorOf(int v, int m)
        {
            if (!colorIndex.TryGetValue((v, m), out var i))
            {
                i = colorIndex.Count;
                colorIndex[(v, m)] = i;
                colorGroup.Add(new XElement(Mat + "color", new XAttribute("color", Hex(model.CornerColor(v, m)))));
            }
            return i;
        }
        if (perVertex)
            resources.Add(colorGroup);

        var parts = model.Parts.Count > 0 ? model.Parts : [new Part3D(model.Name ?? "model", 0, model.Triangles.Count)];
        var build = new XElement(Core + "build");
        var id = 3;
        foreach (var part in parts)
        {
            var local = new Dictionary<int, int>();
            var vertices = new XElement(Core + "vertices");
            var triangles = new XElement(Core + "triangles");
            int Local(int v)
            {
                if (!local.TryGetValue(v, out var i))
                {
                    i = local.Count;
                    local[v] = i;
                    var p = model.Positions[v] * factor;
                    vertices.Add(new XElement(Core + "vertex", new XAttribute("x", N(p.X)), new XAttribute("y", N(p.Y)), new XAttribute("z", N(p.Z))));
                }
                return i;
            }
            for (var t = part.FirstTriangle; t < part.FirstTriangle + part.TriangleCount; t++)
            {
                var (a, b, c, m) = model.Triangles[t];
                // 3MF allows no triangle with a corner twice.
                if (a == b || b == c || a == c)
                    continue;
                var triangle = new XElement(Core + "triangle", new XAttribute("v1", Local(a)), new XAttribute("v2", Local(b)), new XAttribute("v3", Local(c)));
                if (perVertex)
                {
                    triangle.Add(new XAttribute("pid", 2), new XAttribute("p1", ColorOf(a, m)), new XAttribute("p2", ColorOf(b, m)), new XAttribute("p3", ColorOf(c, m)));
                }
                else
                    triangle.Add(new XAttribute("pid", 1), new XAttribute("p1", Math.Clamp(m, 0, materials.Count - 1)));
                triangles.Add(triangle);
            }
            if (local.Count == 0)
                continue;
            resources.Add(new XElement(Core + "object", new XAttribute("id", id), new XAttribute("type", "model"), new XAttribute("name", part.Name),
                new XElement(Core + "mesh", vertices, triangles)));
            build.Add(new XElement(Core + "item", new XAttribute("objectid", id)));
            id++;
        }

        var xml = new XDocument(new XDeclaration("1.0", "UTF-8", null), new XElement(Core + "model",
            new XAttribute("unit", "millimeter"), new XAttribute(XNamespace.Xml + "lang", "en-US"),
            new XAttribute(XNamespace.Xmlns + "m", Mat),
            new XElement(Core + "metadata", new XAttribute("name", "Application"), "DropWheel"),
            new XElement(Core + "metadata", new XAttribute("name", "Title"), model.Name ?? ""),
            resources, build));

        using var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        void Text(string name, string content)
        {
            using var w = new StreamWriter(zip.CreateEntry(name, CompressionLevel.Optimal).Open(), new UTF8Encoding(false));
            w.Write(content);
        }
        Text("[Content_Types].xml", """
            <?xml version="1.0" encoding="UTF-8"?>
            <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="model" ContentType="application/vnd.ms-package.3dmanufacturing-3dmodel+xml"/></Types>
            """);
        Text("_rels/.rels", $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Target="/3D/3dmodel.model" Id="rel0" Type="{ModelRelationship}"/></Relationships>
            """);
        using (var w = new StreamWriter(zip.CreateEntry("3D/3dmodel.model", CompressionLevel.Optimal).Open(), new UTF8Encoding(false)))
            xml.Save(w, SaveOptions.DisableFormatting);
    }

    private static string N(float v) => v.ToString("0.######", Inv);
}
