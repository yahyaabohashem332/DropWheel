using System.Globalization;
using System.Numerics;
using System.Text;

namespace Petal.Engines.Files.Models;

/// <summary>
/// Wavefront OBJ: positions (with colours, as many programs add them), texture coordinates, normals and polygons
/// (made into triangles), parts ("o", "g") and materials from its .mtl (colour, opacity and picture). Y is up.
/// Written with its .mtl and pictures beside it.
/// </summary>
internal static class Obj
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static Model3D Read(string path)
    {
        var model = new Model3D { Name = Path.GetFileNameWithoutExtension(path) };
        var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var positions = new List<Vector3>();
        var colors = new List<Vector4?>();
        var uvs = new List<Vector2>();
        var normals = new List<Vector3>();
        var corners = new Dictionary<(int V, int T, int N), int>();
        var materials = new Dictionary<string, int>(StringComparer.Ordinal);
        var current = -1;
        var partName = model.Name ?? "model";
        var partStart = 0;
        bool anyUv = false, anyNormal = false, anyColor = false;

        void EndPart()
        {
            if (model.Triangles.Count > partStart)
                model.Parts.Add(new Part3D(partName, partStart, model.Triangles.Count - partStart));
            partStart = model.Triangles.Count;
        }

        int Corner(string token)
        {
            var bits = token.Split('/');
            int Index(int i, int count)
            {
                if (i >= bits.Length || bits[i].Length == 0)
                    return -1;
                // Counted from 1, or back from the last one when negative.
                var n = int.Parse(bits[i], Inv);
                return n < 0 ? count + n : n - 1;
            }
            var key = (Index(0, positions.Count), Index(1, uvs.Count), Index(2, normals.Count));
            if ((uint)key.Item1 >= positions.Count)
                throw new InvalidDataException("A face of this OBJ points at a corner it doesn't have.");
            if (!corners.TryGetValue(key, out var index))
            {
                index = model.Positions.Count;
                model.Positions.Add(positions[key.Item1]);
                model.Uvs!.Add(key.Item2 >= 0 && key.Item2 < uvs.Count ? uvs[key.Item2] : Vector2.Zero);
                model.Normals!.Add(key.Item3 >= 0 && key.Item3 < normals.Count ? normals[key.Item3] : Vector3.Zero);
                model.Colors!.Add(colors[key.Item1] ?? Vector4.One);
                anyUv |= key.Item2 >= 0;
                anyNormal |= key.Item3 >= 0;
                corners[key] = index;
            }
            return index;
        }

        model.Uvs = [];
        model.Normals = [];
        model.Colors = [];
        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#')
                continue;
            var space = line.IndexOfAny([' ', '\t']);
            var keyword = space < 0 ? line : line[..space];
            var rest = space < 0 ? "" : line[(space + 1)..].Trim();
            var p = rest.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            switch (keyword)
            {
                case "v":
                    positions.Add(new Vector3(F(p, 0), F(p, 1), F(p, 2)));
                    if (p.Length >= 6)
                    {
                        // Colours 0–1, or 0–255 from some scanners.
                        var c = new Vector3(F(p, 3), F(p, 4), F(p, 5));
                        if (c.X > 1 || c.Y > 1 || c.Z > 1) c /= 255f;
                        colors.Add(new Vector4(c, 1));
                        anyColor = true;
                    }
                    else
                        colors.Add(null);
                    break;
                case "vt":
                    uvs.Add(new Vector2(F(p, 0), 1 - (p.Length > 1 ? F(p, 1) : 0)));
                    break;
                case "vn":
                    normals.Add(new Vector3(F(p, 0), F(p, 1), F(p, 2)));
                    break;
                case "f":
                {
                    if (p.Length < 3)
                        break;
                    if (current < 0)
                        current = model.DefaultMaterial();
                    var ids = p.Select(Corner).ToList();
                    for (var k = 1; k + 1 < ids.Count; k++)
                        model.Triangles.Add((ids[0], ids[k], ids[k + 1], current));
                    break;
                }
                case "o":
                case "g":
                    EndPart();
                    partName = rest.Length > 0 ? rest : partName;
                    break;
                case "usemtl":
                    current = materials.TryGetValue(rest, out var m) ? m : Missing(rest);
                    break;
                case "mtllib":
                    foreach (var library in Libraries(rest))
                        ReadMaterials(Path.Combine(dir, library), model, materials);
                    break;
            }
        }
        EndPart();
        if (!anyUv) model.Uvs = null;
        if (!anyNormal) model.Normals = null;
        if (!anyColor) model.Colors = null;
        model.Check();
        model.FromYUp();
        return model;

        int Missing(string name)
        {
            model.Materials.Add(new Material3D(name, Vector4.One));
            materials[name] = model.Materials.Count - 1;
            return model.Materials.Count - 1;
        }
    }

    /// <summary>"mtllib" may name several files; a name with spaces is one when that file is there.</summary>
    private static IEnumerable<string> Libraries(string rest) =>
        rest.EndsWith(".mtl", StringComparison.OrdinalIgnoreCase) && rest.Count(c => c == '.') == 1 ? [rest] : rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    private static float F(string[] parts, int i) =>
        i < parts.Length && float.TryParse(parts[i], NumberStyles.Float, Inv, out var v) ? v : 0f;

    private static void ReadMaterials(string path, Model3D model, Dictionary<string, int> materials)
    {
        if (!File.Exists(path))
            return;
        var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        string? name = null;
        var color = Vector4.One;
        byte[]? texture = null;

        void Flush()
        {
            if (name is null)
                return;
            model.Materials.Add(new Material3D(name, color, texture));
            materials[name] = model.Materials.Count - 1;
        }

        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#')
                continue;
            var space = line.IndexOfAny([' ', '\t']);
            var keyword = space < 0 ? line : line[..space];
            var rest = space < 0 ? "" : line[(space + 1)..].Trim();
            var p = rest.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            switch (keyword)
            {
                case "newmtl":
                    Flush();
                    (name, color, texture) = (rest, Vector4.One, null);
                    break;
                case "Kd":
                    color = new Vector4(F(p, 0), F(p, 1), F(p, 2), color.W);
                    break;
                case "d":
                    color.W = F(p, 0);
                    break;
                case "Tr":
                    color.W = 1 - F(p, 0);
                    break;
                case "map_Kd":
                {
                    // Options ("-s 1 1 1") come first; the file is what follows them.
                    var file = p.Length > 0 ? rest[rest.IndexOf(p[^1], StringComparison.Ordinal)..] : "";
                    for (var i = 0; i < p.Length; i++)
                    {
                        var candidate = string.Join(' ', p[i..]);
                        if (File.Exists(Path.Combine(dir, candidate)))
                        {
                            file = candidate;
                            break;
                        }
                    }
                    var full = Path.Combine(dir, file.Replace('/', Path.DirectorySeparatorChar));
                    texture = File.Exists(full) ? File.ReadAllBytes(full) : null;
                    break;
                }
            }
        }
        Flush();
    }

    /// <summary>
    /// Writes the OBJ, its .mtl and its pictures beside each other in <paramref name="folder"/>, named after
    /// <paramref name="stem"/>; returns the files written, the OBJ first.
    /// </summary>
    public static List<string> Write(Model3D model, string folder, string stem)
    {
        var obj = Path.Combine(folder, stem + ".obj");
        var mtl = Path.Combine(folder, stem + ".mtl");
        var written = new List<string> { obj, mtl };
        var sb = new StringBuilder();
        sb.Append("# DropWheel\n").Append("mtllib ").Append(Path.GetFileName(mtl)).Append('\n');
        for (var i = 0; i < model.Positions.Count; i++)
        {
            var v = Model3D.ToYUp(model.Positions[i]);
            sb.Append("v ").Append(N(v.X)).Append(' ').Append(N(v.Y)).Append(' ').Append(N(v.Z));
            if (model.Colors is { } colors)
                sb.Append(' ').Append(N(colors[i].X)).Append(' ').Append(N(colors[i].Y)).Append(' ').Append(N(colors[i].Z));
            sb.Append('\n');
        }
        if (model.Uvs is { } uvs)
            foreach (var t in uvs)
                sb.Append("vt ").Append(N(t.X)).Append(' ').Append(N(1 - t.Y)).Append('\n');
        if (model.Normals is { } normals)
            foreach (var n in normals)
            {
                var y = Model3D.ToYUp(n);
                sb.Append("vn ").Append(N(y.X)).Append(' ').Append(N(y.Y)).Append(' ').Append(N(y.Z)).Append('\n');
            }
        var names = MaterialNames(model);
        var parts = model.Parts.Count > 0 ? model.Parts : [new Part3D(model.Name ?? "model", 0, model.Triangles.Count)];
        foreach (var part in parts)
        {
            sb.Append("o ").Append(part.Name.Replace('\n', ' ')).Append('\n');
            var material = -1;
            for (var t = part.FirstTriangle; t < part.FirstTriangle + part.TriangleCount; t++)
            {
                var (a, b, c, m) = model.Triangles[t];
                if (m != material)
                {
                    sb.Append("usemtl ").Append(m >= 0 && m < names.Length ? names[m] : "default").Append('\n');
                    material = m;
                }
                sb.Append('f');
                foreach (var v in new[] { a, b, c })
                {
                    var i = v + 1;
                    sb.Append(' ').Append(i);
                    if (model.Uvs is not null || model.Normals is not null)
                        sb.Append('/').Append(model.Uvs is not null ? i.ToString(Inv) : "");
                    if (model.Normals is not null)
                        sb.Append('/').Append(i);
                }
                sb.Append('\n');
            }
        }
        File.WriteAllText(obj, sb.ToString(), new UTF8Encoding(false));

        var lib = new StringBuilder("# DropWheel\n");
        for (var m = 0; m < model.Materials.Count; m++)
        {
            var material = model.Materials[m];
            lib.Append("newmtl ").Append(names[m]).Append('\n')
               .Append("Kd ").Append(N(material.Color.X)).Append(' ').Append(N(material.Color.Y)).Append(' ').Append(N(material.Color.Z)).Append('\n')
               .Append("d ").Append(N(material.Color.W)).Append('\n');
            if (material.Texture is { } texture)
            {
                var file = $"{stem}_{m + 1}.{(Pictures.IsJpeg(texture) ? "jpg" : "png")}";
                File.WriteAllBytes(Path.Combine(folder, file), texture);
                written.Add(Path.Combine(folder, file));
                lib.Append("map_Kd ").Append(file).Append('\n');
            }
        }
        File.WriteAllText(mtl, lib.ToString(), new UTF8Encoding(false));
        return written;
    }

    /// <summary>
    /// Each material's name as an OBJ can hold it: one word (many programs stop at a space), and each its own, as two
    /// materials of a glTF may share a name.
    /// </summary>
    internal static string[] MaterialNames(Model3D model)
    {
        var taken = new HashSet<string>(StringComparer.Ordinal);
        var names = new string[model.Materials.Count];
        for (var m = 0; m < names.Length; m++)
        {
            var name = string.Join('_', model.Materials[m].Name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            if (name.Length == 0)
                name = "material";
            var unique = name;
            for (var n = 2; !taken.Add(unique); n++)
                unique = $"{name}_{n}";
            names[m] = unique;
        }
        return names;
    }

    private static string N(float v) => v.ToString("0.######", Inv);
}
