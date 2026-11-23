using System.Numerics;

namespace Petal.Engines.Files.Models;

/// <summary>A material: its colour (RGBA as shown, sRGB, 0–1) and, if it has one, its picture (PNG or JPEG bytes).</summary>
internal sealed record Material3D(string Name, Vector4 Color, byte[]? Texture = null);

/// <summary>A named part of a model (an OBJ object, a 3MF object, a glTF node's mesh): a run of its triangles.</summary>
internal sealed record Part3D(string Name, int FirstTriangle, int TriangleCount);

/// <summary>
/// A 3D model as every format here is read into and written from: triangles over shared vertices, each vertex with
/// its position and, if the file had them, a normal, a texture coordinate (top-left origin, as glTF has it) and a
/// colour; each triangle with its material. Z is up, as in STL, 3MF and PLY (OBJ and glTF are turned on the way in
/// and out). <see cref="MetresPerUnit"/> is the unit when the file says it (STL is millimetres by convention, glTF
/// metres, 3MF what it declares); OBJ and PLY don't, and their numbers are kept as they are.
/// </summary>
internal sealed class Model3D
{
    public List<Vector3> Positions { get; } = [];
    public List<Vector3>? Normals { get; set; }
    public List<Vector2>? Uvs { get; set; }
    public List<Vector4>? Colors { get; set; }
    public List<(int A, int B, int C, int Material)> Triangles { get; } = [];
    public List<Material3D> Materials { get; } = [];
    public List<Part3D> Parts { get; } = [];
    public double? MetresPerUnit { get; set; }
    public string? Name { get; set; }

    public const double Millimetre = 0.001;

    /// <summary>The material triangles without one use: white.</summary>
    public int DefaultMaterial()
    {
        var index = Materials.FindIndex(m => m.Name == "default");
        if (index >= 0)
            return index;
        Materials.Add(new Material3D("default", Vector4.One));
        return Materials.Count - 1;
    }

    public (Vector3 Min, Vector3 Max) Bounds()
    {
        if (Positions.Count == 0)
            return (Vector3.Zero, Vector3.Zero);
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var p in Positions)
        {
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
        }
        return (min, max);
    }

    /// <summary>Scales every position, as a change of unit does.</summary>
    public void Scale(float factor)
    {
        if (factor == 1)
            return;
        for (var i = 0; i < Positions.Count; i++)
            Positions[i] *= factor;
    }

    /// <summary>Turns a Y-up model (OBJ, glTF) to Z-up: what was up is up again.</summary>
    public void FromYUp()
    {
        for (var i = 0; i < Positions.Count; i++)
            Positions[i] = new Vector3(Positions[i].X, -Positions[i].Z, Positions[i].Y);
        if (Normals is { } normals)
            for (var i = 0; i < normals.Count; i++)
                normals[i] = new Vector3(normals[i].X, -normals[i].Z, normals[i].Y);
    }

    /// <summary>The positions and normals as a Y-up file has them.</summary>
    public static Vector3 ToYUp(Vector3 v) => new(v.X, v.Z, -v.Y);

    /// <summary>The factor that puts this model's numbers in the given unit; 1 when this model's own unit is unknown.</summary>
    public float FactorTo(double metresPerUnit) => MetresPerUnit is { } unit ? (float)(unit / metresPerUnit) : 1f;

    /// <summary>Each triangle's own normal, its corners counter-clockwise seen from outside.</summary>
    public Vector3 FaceNormal(int triangle)
    {
        var (a, b, c, _) = Triangles[triangle];
        var n = Vector3.Cross(Positions[b] - Positions[a], Positions[c] - Positions[a]);
        var length = n.Length();
        return length > 0 ? n / length : Vector3.UnitZ;
    }

    /// <summary>A copy where no vertex is shared between triangles, so each corner can have its own colour or coordinate.</summary>
    public Model3D Unshared()
    {
        var copy = new Model3D { MetresPerUnit = MetresPerUnit, Name = Name };
        copy.Materials.AddRange(Materials);
        copy.Parts.AddRange(Parts);
        if (Normals is not null) copy.Normals = [];
        if (Uvs is not null) copy.Uvs = [];
        if (Colors is not null) copy.Colors = [];
        foreach (var (a, b, c, m) in Triangles)
        {
            var first = copy.Positions.Count;
            foreach (var v in new[] { a, b, c })
            {
                copy.Positions.Add(Positions[v]);
                copy.Normals?.Add(Normals![v]);
                copy.Uvs?.Add(Uvs![v]);
                copy.Colors?.Add(Colors![v]);
            }
            copy.Triangles.Add((first, first + 1, first + 2, m));
        }
        return copy;
    }

    /// <summary>The colour each corner shows: its own colour times its material's (the picture isn't counted).</summary>
    public Vector4 CornerColor(int vertex, int material)
    {
        var color = material >= 0 && material < Materials.Count ? Materials[material].Color : Vector4.One;
        return Colors is { } colors ? colors[vertex] * color : color;
    }

    /// <summary>Whether anything here has a colour other than white.</summary>
    public bool HasColor => Colors is not null || Materials.Any(m => m.Color != Vector4.One);

    public void Check()
    {
        if (Triangles.Count == 0)
            throw new InvalidDataException("This model has no triangles.");
        foreach (var (a, b, c, _) in Triangles)
        {
            if ((uint)a >= Positions.Count || (uint)b >= Positions.Count || (uint)c >= Positions.Count)
                throw new InvalidDataException("This model's triangles point at corners it doesn't have.");
        }
    }
}
