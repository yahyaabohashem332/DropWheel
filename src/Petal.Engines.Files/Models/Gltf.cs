using System.Numerics;
using System.Text.Json.Nodes;
using SharpGLTF.Geometry;
using SharpGLTF.Geometry.VertexTypes;
using SharpGLTF.Materials;
using SharpGLTF.Memory;
using SharpGLTF.Scenes;
using SharpGLTF.Schema2;

namespace Petal.Engines.Files.Models;

/// <summary>
/// glTF 2.0, as .glb (one binary file) or .gltf (JSON), through SharpGLTF: the scene's meshes in their places,
/// base colours, pictures and vertex colours. Y is up, metres the unit, colours linear (made sRGB here and back).
/// A .gltf is written as one file, its buffer and pictures inside it.
/// </summary>
internal static class Gltf
{
    private const double Metre = 1;

    public static Model3D Read(string path)
    {
        var root = ModelRoot.Load(path, new ReadSettings { Validation = SharpGLTF.Validation.ValidationMode.TryFix });
        var scene = root.DefaultScene ?? root.LogicalScenes.FirstOrDefault() ?? throw new InvalidDataException("This glTF file has no scene.");
        var model = new Model3D { Name = Path.GetFileNameWithoutExtension(path), MetresPerUnit = Metre, Normals = [], Uvs = [], Colors = [] };
        var materials = new Dictionary<Material, int>();
        var vertices = new Dictionary<(Vector3, Vector3, Vector4, Vector2), int>();
        var anyColor = false;
        var anyUv = false;

        int MaterialOf(Material? material)
        {
            if (material is null)
                return model.DefaultMaterial();
            if (materials.TryGetValue(material, out var index))
                return index;
            var color = Srgb(material.GetDiffuseColor(Vector4.One));
            byte[]? texture = null;
            if (material.FindChannel("BaseColor")?.Texture?.PrimaryImage?.Content is { } image && (image.IsPng || image.IsJpg))
                texture = image.Content.ToArray();
            model.Materials.Add(new Material3D(material.Name ?? $"material {model.Materials.Count + 1}", color, texture));
            materials[material] = model.Materials.Count - 1;
            return model.Materials.Count - 1;
        }

        int Vertex(IVertexBuilder v)
        {
            var geometry = v.GetGeometry();
            var position = geometry.GetPosition();
            geometry.TryGetNormal(out var normal);
            var paint = v.GetMaterial();
            var color = paint.MaxColors > 0 ? Srgb(paint.GetColor(0)) : Vector4.One;
            var uv = paint.MaxTextCoords > 0 ? paint.GetTexCoord(0) : Vector2.Zero;
            anyColor |= color != Vector4.One;
            anyUv |= paint.MaxTextCoords > 0;
            var key = (position, normal, color, uv);
            if (!vertices.TryGetValue(key, out var index))
            {
                index = model.Positions.Count;
                model.Positions.Add(position);
                model.Normals!.Add(normal);
                model.Colors!.Add(color);
                model.Uvs!.Add(uv);
                vertices[key] = index;
            }
            return index;
        }

        var nodes = Node.Flatten(scene).ToList();
        if (nodes.Any(n => n.Skin is not null))
        {
            // A skinned model (a character) is posed by its skeleton: the scene as a whole, one part.
            foreach (var t in scene.EvaluateTriangles<VertexPositionNormal, VertexColor1Texture1>(null, null, 0))
                model.Triangles.Add((Vertex(t.A), Vertex(t.B), Vertex(t.C), MaterialOf(t.Material)));
            if (model.Triangles.Count > 0)
                model.Parts.Add(new Part3D(model.Name ?? "model", 0, model.Triangles.Count));
        }
        else
        {
            // Each node's mesh where the node puts it, a part of its own.
            foreach (var node in nodes.Where(n => n.Mesh is not null))
            {
                var first = model.Triangles.Count;
                foreach (var t in node.Mesh.EvaluateTriangles<VertexPositionNormal, VertexColor1Texture1, VertexEmpty>(new SharpGLTF.Transforms.RigidTransform(node.WorldMatrix)))
                    model.Triangles.Add((Vertex(t.A), Vertex(t.B), Vertex(t.C), MaterialOf(t.Material)));
                if (model.Triangles.Count > first)
                    model.Parts.Add(new Part3D(node.Name ?? node.Mesh.Name ?? $"part {model.Parts.Count + 1}", first, model.Triangles.Count - first));
            }
        }
        if (!anyColor) model.Colors = null;
        if (!anyUv) model.Uvs = null;
        if (model.Normals!.All(n => n == Vector3.Zero)) model.Normals = null;
        model.Check();
        model.FromYUp();
        return model;
    }

    /// <summary>Writes .glb, or a .gltf with everything inside it.</summary>
    public static void Write(Model3D source, string path, bool binary)
    {
        // Without normals, each triangle shows its own face: a box keeps its edges.
        var model = source.Normals is null ? source.Unshared() : source;
        var factor = model.FactorTo(Metre);
        var materials = model.Materials.Select(m =>
        {
            var builder = new MaterialBuilder(m.Name).WithDoubleSide(true).WithMetallicRoughnessShader().WithMetallicRoughness(0, 0.8f).WithBaseColor(Linear(m.Color));
            if (m.Texture is { } texture)
                builder.WithChannelImage(KnownChannel.BaseColor, new MemoryImage(texture));
            if (m.Color.W < 1)
                builder.WithAlpha(SharpGLTF.Materials.AlphaMode.BLEND);
            return builder;
        }).ToList();
        if (materials.Count == 0)
            materials.Add(new MaterialBuilder("default").WithDoubleSide(true));

        // Only what the model has: corners' colours and picture coordinates when it has them.
        var scene = new SceneBuilder(model.Name ?? "model");
        if (model.Colors is { } colors && model.Uvs is { } uvs)
            Meshes(model, factor, materials, scene, v => new VertexColor1Texture1(Linear(colors[v]), uvs[v]));
        else if (model.Colors is { } only)
            Meshes(model, factor, materials, scene, v => new VertexColor1(Linear(only[v])));
        else if (model.Uvs is { } coordinates)
            Meshes(model, factor, materials, scene, v => new VertexTexture1(coordinates[v]));
        else
            Meshes(model, factor, materials, scene, _ => default(VertexEmpty));
        var gltf = scene.ToGltf2();
        if (binary)
        {
            gltf.SaveGLB(path);
            return;
        }
        // SharpGLTF writes a .gltf's buffer beside it: written in a folder of its own, then put inside the JSON.
        var folder = Path.Combine(Path.GetTempPath(), "petal-gltf-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var json = Path.Combine(folder, "model.gltf");
            gltf.SaveGLTF(json, new WriteSettings { ImageWriting = ResourceWriteMode.EmbeddedAsBase64, MergeBuffers = true, JsonIndented = true });
            var document = JsonNode.Parse(File.ReadAllText(json))!;
            foreach (var buffer in document["buffers"]?.AsArray() ?? [])
            {
                if (buffer?["uri"]?.GetValue<string>() is { } uri && !uri.StartsWith("data:", StringComparison.Ordinal))
                    buffer["uri"] = "data:application/octet-stream;base64," + Convert.ToBase64String(File.ReadAllBytes(Path.Combine(folder, uri)));
            }
            File.WriteAllText(path, document.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
        finally
        {
            try { Directory.Delete(folder, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>A mesh for each part, in metres and Y up, its corners painted as <paramref name="paint"/> says.</summary>
    private static void Meshes<TPaint>(Model3D model, float factor, List<MaterialBuilder> materials, SceneBuilder scene, Func<int, TPaint> paint)
        where TPaint : struct, IVertexMaterial
    {
        VertexBuilder<VertexPositionNormal, TPaint, VertexEmpty> Corner(int v, int triangle)
        {
            var normal = model.Normals is { } n ? n[v] : model.FaceNormal(triangle);
            if (normal != Vector3.Zero)
                normal = Vector3.Normalize(normal);
            var geometry = new VertexPositionNormal(Model3D.ToYUp(model.Positions[v] * factor), Model3D.ToYUp(normal));
            return new VertexBuilder<VertexPositionNormal, TPaint, VertexEmpty>(geometry, paint(v));
        }

        var parts = model.Parts.Count > 0 ? model.Parts : [new Part3D(model.Name ?? "model", 0, model.Triangles.Count)];
        foreach (var part in parts)
        {
            var mesh = new MeshBuilder<VertexPositionNormal, TPaint, VertexEmpty>(part.Name);
            for (var t = part.FirstTriangle; t < part.FirstTriangle + part.TriangleCount; t++)
            {
                var (a, b, c, m) = model.Triangles[t];
                mesh.UsePrimitive(materials[Math.Clamp(m, 0, materials.Count - 1)]).AddTriangle(Corner(a, t), Corner(b, t), Corner(c, t));
            }
            scene.AddRigidMesh(mesh, Matrix4x4.Identity).WithName(part.Name);
        }
    }

    private static float ToSrgb(float c) => c <= 0.0031308f ? c * 12.92f : 1.055f * MathF.Pow(c, 1 / 2.4f) - 0.055f;

    private static float ToLinear(float c) => c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);

    private static Vector4 Srgb(Vector4 c) => new(ToSrgb(c.X), ToSrgb(c.Y), ToSrgb(c.Z), c.W);

    private static Vector4 Linear(Vector4 c) => new(ToLinear(c.X), ToLinear(c.Y), ToLinear(c.Z), c.W);
}
