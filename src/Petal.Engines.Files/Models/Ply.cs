using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace Petal.Engines.Files.Models;

/// <summary>
/// PLY (Stanford), as 3D scanners and point-cloud tools write it: vertices with normals, colours and texture
/// coordinates, and faces of any number of corners; text or binary either way round. Written binary
/// little-endian, colours as bytes.
/// </summary>
internal static class Ply
{
    private sealed record Property(string Name, string Type, string? CountType);

    private sealed record Element(string Name, int Count, List<Property> Properties);

    public static Model3D Read(string path)
    {
        using var stream = new BufferedStream(File.OpenRead(path), 1 << 16);
        // The header, line by line, in ASCII.
        var elements = new List<Element>();
        string format = "ascii";
        string? line;
        if (ReadLine(stream) != "ply")
            throw new InvalidDataException("This is not a PLY file.");
        while ((line = ReadLine(stream)) is not null && line != "end_header")
        {
            var p = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (p.Length == 0) continue;
            switch (p[0])
            {
                case "format": format = p[1]; break;
                case "element": elements.Add(new Element(p[1], int.Parse(p[2], CultureInfo.InvariantCulture), [])); break;
                case "property" when elements.Count > 0:
                    elements[^1].Properties.Add(p[1] == "list" ? new Property(p[4], p[3], p[2]) : new Property(p[2], p[1], null));
                    break;
            }
        }
        if (line is null)
            throw new InvalidDataException("This PLY file's header has no end.");

        var reader = format switch
        {
            "ascii" => new Values(stream, ascii: true, bigEndian: false),
            "binary_little_endian" => new Values(stream, ascii: false, bigEndian: false),
            "binary_big_endian" => new Values(stream, ascii: false, bigEndian: true),
            _ => throw new NotSupportedException($"PLY's \"{format}\" format is not one DropWheel knows."),
        };

        var model = new Model3D { Name = Path.GetFileNameWithoutExtension(path) };
        var material = model.DefaultMaterial();
        foreach (var element in elements)
        {
            var names = element.Properties.Select(p => p.Name).ToList();
            bool Has(params string[] any) => any.Any(names.Contains);
            if (element.Name == "vertex")
            {
                if (Has("nx")) model.Normals = new(element.Count);
                if (Has("red", "r", "diffuse_red")) model.Colors = new(element.Count);
                if (Has("s", "u", "texture_u", "texture_s")) model.Uvs = new(element.Count);
            }
            for (var i = 0; i < element.Count; i++)
            {
                var values = new Dictionary<string, double>(StringComparer.Ordinal);
                List<int>? corners = null;
                foreach (var property in element.Properties)
                {
                    if (property.CountType is { } countType)
                    {
                        var n = (int)reader.Read(countType);
                        var list = new List<int>(n);
                        for (var k = 0; k < n; k++)
                            list.Add((int)reader.Read(property.Type));
                        if (property.Name is "vertex_indices" or "vertex_index")
                            corners = list;
                    }
                    else
                        values[property.Name] = reader.Read(property.Type);
                }
                if (element.Name == "vertex")
                {
                    double V(params string[] keys) => keys.Select(k => values.TryGetValue(k, out var v) ? v : (double?)null).FirstOrDefault(v => v is not null) ?? 0;
                    model.Positions.Add(new Vector3((float)V("x"), (float)V("y"), (float)V("z")));
                    model.Normals?.Add(new Vector3((float)V("nx"), (float)V("ny"), (float)V("nz")));
                    if (model.Colors is not null)
                    {
                        var bytes = element.Properties.First(p => p.Name is "red" or "r" or "diffuse_red").Type is "float" or "float32" or "double" or "float64" ? 1f : 255f;
                        var alpha = values.ContainsKey("alpha") || values.ContainsKey("a") ? (float)V("alpha", "a") / bytes : 1f;
                        model.Colors.Add(new Vector4((float)V("red", "r", "diffuse_red") / bytes, (float)V("green", "g", "diffuse_green") / bytes, (float)V("blue", "b", "diffuse_blue") / bytes, alpha));
                    }
                    model.Uvs?.Add(new Vector2((float)V("s", "u", "texture_u", "texture_s"), 1 - (float)V("t", "v", "texture_v", "texture_t")));
                }
                else if (element.Name == "face" && corners is { Count: >= 3 })
                {
                    for (var k = 1; k + 1 < corners.Count; k++)
                        model.Triangles.Add((corners[0], corners[k], corners[k + 1], material));
                }
            }
        }
        model.Check();
        model.Parts.Add(new Part3D(model.Name ?? "model", 0, model.Triangles.Count));
        return model;
    }

    private static string? ReadLine(Stream stream)
    {
        var sb = new StringBuilder();
        int b;
        while ((b = stream.ReadByte()) >= 0 && b != '\n')
            if (b != '\r') sb.Append((char)b);
        return b < 0 && sb.Length == 0 ? null : sb.ToString().Trim();
    }

    /// <summary>The values after the header, of PLY's types, as text or in either byte order.</summary>
    private sealed class Values(Stream stream, bool ascii, bool bigEndian)
    {
        private readonly byte[] _buffer = new byte[8];

        public double Read(string type)
        {
            if (ascii)
                return double.Parse(Token(), NumberStyles.Float, CultureInfo.InvariantCulture);
            var size = type switch
            {
                "char" or "uchar" or "int8" or "uint8" => 1,
                "short" or "ushort" or "int16" or "uint16" => 2,
                "int" or "uint" or "int32" or "uint32" or "float" or "float32" => 4,
                "double" or "float64" => 8,
                _ => throw new InvalidDataException($"PLY's type \"{type}\" is not one DropWheel knows."),
            };
            if (stream.ReadAtLeast(_buffer.AsSpan(0, size), size, throwOnEndOfStream: false) < size)
                throw new InvalidDataException("This PLY file is cut short.");
            var span = _buffer.AsSpan(0, size);
            if (bigEndian != !BitConverter.IsLittleEndian)
                span.Reverse();
            return type switch
            {
                "char" or "int8" => (sbyte)span[0],
                "uchar" or "uint8" => span[0],
                "short" or "int16" => BitConverter.ToInt16(span),
                "ushort" or "uint16" => BitConverter.ToUInt16(span),
                "int" or "int32" => BitConverter.ToInt32(span),
                "uint" or "uint32" => BitConverter.ToUInt32(span),
                "float" or "float32" => BitConverter.ToSingle(span),
                _ => BitConverter.ToDouble(span),
            };
        }

        private string Token()
        {
            var sb = new StringBuilder();
            int b;
            while ((b = stream.ReadByte()) >= 0 && char.IsWhiteSpace((char)b)) { }
            if (b < 0)
                throw new InvalidDataException("This PLY file is cut short.");
            do sb.Append((char)b); while ((b = stream.ReadByte()) >= 0 && !char.IsWhiteSpace((char)b));
            return sb.ToString();
        }
    }

    /// <summary>
    /// Binary little-endian PLY: positions, normals and texture coordinates as the model has them, and colours as
    /// bytes, the materials' own colours worked into each corner when they differ.
    /// </summary>
    public static void Write(Model3D source, string path)
    {
        var colored = source.HasColor;
        // A material's colour belongs to its triangles, not to corners they share with others: each triangle gets its own corners.
        var used = source.Triangles.Select(t => t.Material).Distinct()
            .Select(m => m >= 0 && m < source.Materials.Count ? source.Materials[m].Color : Vector4.One).Distinct().Count();
        var model = colored && used > 1 ? source.Unshared() : source;
        var header = new StringBuilder("ply\nformat binary_little_endian 1.0\ncomment DropWheel\n")
            .Append("element vertex ").Append(model.Positions.Count).Append('\n')
            .Append("property float x\nproperty float y\nproperty float z\n");
        if (model.Normals is not null) header.Append("property float nx\nproperty float ny\nproperty float nz\n");
        if (model.Uvs is not null) header.Append("property float s\nproperty float t\n");
        if (colored) header.Append("property uchar red\nproperty uchar green\nproperty uchar blue\nproperty uchar alpha\n");
        header.Append("element face ").Append(model.Triangles.Count).Append('\n').Append("property list uchar int vertex_indices\nend_header\n");

        // Each corner's colour: its own times its first triangle's material's.
        var cornerColor = new Vector4[colored ? model.Positions.Count : 0];
        if (colored)
        {
            Array.Fill(cornerColor, new Vector4(-1));
            foreach (var (a, b, c, m) in model.Triangles)
                foreach (var v in new[] { a, b, c })
                    if (cornerColor[v].X < 0) cornerColor[v] = model.CornerColor(v, m);
            for (var v = 0; v < cornerColor.Length; v++)
                if (cornerColor[v].X < 0) cornerColor[v] = model.Colors?[v] ?? Vector4.One;
        }

        using var output = new BufferedStream(File.Create(path), 1 << 16);
        output.Write(Encoding.ASCII.GetBytes(header.ToString()));
        var four = new byte[4];
        void Float(float f)
        {
            BinaryPrimitives.WriteSingleLittleEndian(four, f);
            output.Write(four);
        }
        for (var i = 0; i < model.Positions.Count; i++)
        {
            Float(model.Positions[i].X); Float(model.Positions[i].Y); Float(model.Positions[i].Z);
            if (model.Normals is { } n) { Float(n[i].X); Float(n[i].Y); Float(n[i].Z); }
            if (model.Uvs is { } t) { Float(t[i].X); Float(1 - t[i].Y); }
            if (colored)
            {
                var c = Vector4.Clamp(cornerColor[i], Vector4.Zero, Vector4.One) * 255f;
                output.WriteByte((byte)MathF.Round(c.X)); output.WriteByte((byte)MathF.Round(c.Y));
                output.WriteByte((byte)MathF.Round(c.Z)); output.WriteByte((byte)MathF.Round(c.W));
            }
        }
        foreach (var (a, b, c, _) in model.Triangles)
        {
            output.WriteByte(3);
            BinaryPrimitives.WriteInt32LittleEndian(four, a); output.Write(four);
            BinaryPrimitives.WriteInt32LittleEndian(four, b); output.Write(four);
            BinaryPrimitives.WriteInt32LittleEndian(four, c); output.Write(four);
        }
    }
}
