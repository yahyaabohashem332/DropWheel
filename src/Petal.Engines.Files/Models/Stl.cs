using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace Petal.Engines.Files.Models;

/// <summary>
/// STL, 3D printing's format: triangles alone, in millimetres. Read binary or as text (corners that meet are joined
/// into shared vertices); written binary.
/// </summary>
internal static class Stl
{
    public static Model3D Read(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var model = new Model3D { MetresPerUnit = Model3D.Millimetre, Name = Path.GetFileNameWithoutExtension(path) };
        var material = model.DefaultMaterial();
        var vertices = new Dictionary<Vector3, int>();

        int Vertex(Vector3 p)
        {
            if (!vertices.TryGetValue(p, out var index))
            {
                index = model.Positions.Count;
                model.Positions.Add(p);
                vertices[p] = index;
            }
            return index;
        }

        // Binary when its triangle count says exactly how long it is (a text STL begins "solid", but so do some binary ones).
        var binary = bytes.Length >= 84 && 84L + 50L * BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(80)) == bytes.Length;
        if (binary)
        {
            var count = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(80));
            for (var i = 0; i < count; i++)
            {
                var at = 84 + 50 * i + 12;
                Vector3 P(int k) => new(
                    BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(at + 12 * k)),
                    BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(at + 12 * k + 4)),
                    BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(at + 12 * k + 8)));
                model.Triangles.Add((Vertex(P(0)), Vertex(P(1)), Vertex(P(2)), material));
            }
        }
        else
        {
            var corners = new List<Vector3>(3);
            foreach (var raw in Encoding.ASCII.GetString(bytes).Split('\n'))
            {
                var line = raw.Trim();
                if (line.StartsWith("vertex", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 4)
                        throw new InvalidDataException("A corner of this STL is missing a number.");
                    corners.Add(new Vector3(Number(parts[1]), Number(parts[2]), Number(parts[3])));
                }
                else if (line.StartsWith("endloop", StringComparison.OrdinalIgnoreCase))
                {
                    // A loop of more than three corners is a fan of triangles.
                    for (var k = 1; k + 1 < corners.Count; k++)
                        model.Triangles.Add((Vertex(corners[0]), Vertex(corners[k]), Vertex(corners[k + 1]), material));
                    corners.Clear();
                }
            }
            if (model.Triangles.Count == 0)
                throw new InvalidDataException("This is not an STL file.");
        }
        model.Parts.Add(new Part3D(model.Name ?? "model", 0, model.Triangles.Count));
        return model;
    }

    private static float Number(string s) =>
        float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : throw new InvalidDataException($"\"{s}\" is not a number.");

    /// <summary>Binary STL in millimetres: each triangle with its own normal.</summary>
    public static void Write(Model3D model, string path)
    {
        var factor = model.FactorTo(Model3D.Millimetre);
        using var output = new BufferedStream(File.Create(path), 1 << 16);
        var header = new byte[84];
        Encoding.ASCII.GetBytes("DropWheel STL, millimetres").CopyTo(header, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(80), (uint)model.Triangles.Count);
        output.Write(header);
        var record = new byte[50];
        for (var t = 0; t < model.Triangles.Count; t++)
        {
            var (a, b, c, _) = model.Triangles[t];
            var n = model.FaceNormal(t);
            var values = new[] { n, model.Positions[a] * factor, model.Positions[b] * factor, model.Positions[c] * factor };
            for (var k = 0; k < 4; k++)
            {
                BinaryPrimitives.WriteSingleLittleEndian(record.AsSpan(12 * k), values[k].X);
                BinaryPrimitives.WriteSingleLittleEndian(record.AsSpan(12 * k + 4), values[k].Y);
                BinaryPrimitives.WriteSingleLittleEndian(record.AsSpan(12 * k + 8), values[k].Z);
            }
            output.Write(record);
        }
    }
}
