using System.IO.Compression;
using System.Numerics;
using System.Text;
using Petal.Core.Formats;
using Petal.Core.Jobs;
using Petal.Engines.Files.Models;
using SharpGLTF.Schema2;
using SkiaSharp;
using F = Petal.Core.Formats.Formats;

namespace Petal.Tests;

public class ModelTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("petal-model-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static readonly Vector4 Red = new(0.9f, 0.1f, 0.1f, 1);
    private static readonly Vector4 Blue = new(0.1f, 0.2f, 0.9f, 1);

    /// <summary>A box's twelve triangles, each counter-clockwise seen from outside.</summary>
    private static void Box(Model3D model, Vector3 min, Vector3 max, int material, string part)
    {
        var first = model.Triangles.Count;
        var at = model.Positions.Count;
        for (var i = 0; i < 8; i++)
            model.Positions.Add(new Vector3((i & 1) == 0 ? min.X : max.X, (i & 2) == 0 ? min.Y : max.Y, (i & 4) == 0 ? min.Z : max.Z));
        int[][] faces = [[0, 2, 3, 1], [4, 5, 7, 6], [0, 1, 5, 4], [2, 6, 7, 3], [0, 4, 6, 2], [1, 3, 7, 5]];
        var center = (min + max) / 2;
        foreach (var f in faces)
        {
            var q = f.Select(i => at + i).ToArray();
            var faceCenter = q.Aggregate(Vector3.Zero, (s, i) => s + model.Positions[i]) / 4;
            var normal = Vector3.Cross(model.Positions[q[1]] - model.Positions[q[0]], model.Positions[q[2]] - model.Positions[q[0]]);
            if (Vector3.Dot(normal, faceCenter - center) < 0)
                Array.Reverse(q);
            model.Triangles.Add((q[0], q[1], q[2], material));
            model.Triangles.Add((q[0], q[2], q[3], material));
        }
        model.Parts.Add(new Part3D(part, first, model.Triangles.Count - first));
    }

    /// <summary>
    /// A bracket in millimetres, Z up: a red base 10 × 20 × 5 and a blue post on it to 30 high. Both its
    /// materials are called "Paint", as a glTF's may be.
    /// </summary>
    private static Model3D Bracket()
    {
        var model = new Model3D { Name = "bracket", MetresPerUnit = Model3D.Millimetre };
        model.Materials.Add(new Material3D("Paint", Red));
        model.Materials.Add(new Material3D("Paint", Blue));
        Box(model, new Vector3(0, 0, 0), new Vector3(10, 20, 5), 0, "base");
        Box(model, new Vector3(0, 0, 5), new Vector3(10, 4, 30), 1, "post");
        return model;
    }

    private static Vector3 Size(Model3D model, double metresPerUnit)
    {
        var (min, max) = model.Bounds();
        return (max - min) * model.FactorTo(metresPerUnit);
    }

    private static void Near(Vector3 expected, Vector3 actual, float tolerance = 1e-3f) =>
        Assert.True(Vector3.Distance(expected, actual) < tolerance, $"expected {expected}, got {actual}");

    private static bool Close(Vector4 a, Vector4 b) => Vector4.Distance(a, b) < 0.01f;

    private async Task<string> Convert(string input, FileFormat from, FileFormat to, string? output = null)
    {
        output ??= OutputNaming.ForConversion(input, to);
        var result = await new ModelConverter().ConvertAsync(new ConversionJob(input, from, to, output, ConversionSettings.Default), null, default);
        Assert.True(File.Exists(result.OutputPaths[0]));
        return result.OutputPaths[0];
    }

    private string Source()
    {
        var path = Path.Combine(_dir, "bracket.3mf");
        ThreeMf.Write(Bracket(), path);
        return path;
    }

    [Theory]
    [InlineData("stl")]
    [InlineData("obj")]
    [InlineData("ply")]
    [InlineData("glb")]
    [InlineData("gltf")]
    public async Task A_model_goes_through_every_format_and_comes_back(string id)
    {
        var format = F.FromId(id)!;
        var written = await Convert(Source(), F.ThreeMf, format);
        var model = ModelConverter.Read(written, format);
        Assert.Equal(24, model.Triangles.Count);
        // Up stays up, and the size stays the size: millimetres where the format has a unit, the same numbers where it hasn't.
        Near(new Vector3(10, 20, 30), Size(model, model.MetresPerUnit is null ? 1 : Model3D.Millimetre));

        var back = await Convert(written, format, F.ThreeMf, Path.Combine(_dir, "back.3mf"));
        var again = ModelConverter.Read(back, F.ThreeMf);
        Assert.Equal(24, again.Triangles.Count);
        Near(new Vector3(10, 20, 30), Size(again, Model3D.Millimetre));

        // What the format keeps of the colours and the parts.
        var shown = again.Triangles.Select(t => again.CornerColor(t.A, t.Material)).ToList();
        if (id == "stl")
            Assert.All(shown, c => Assert.Equal(Vector4.One, c));
        else
        {
            Assert.Equal(12, shown.Count(c => Close(c, Red)));
            Assert.Equal(12, shown.Count(c => Close(c, Blue)));
        }
        if (id is "obj" or "glb" or "gltf")
            Assert.Equal(["base", "post"], model.Parts.Select(p => p.Name));
    }

    [Fact]
    public async Task Obj_writes_its_materials_beside_it_each_with_its_own_name()
    {
        var obj = await Convert(Source(), F.ThreeMf, F.Obj);
        var mtl = Path.ChangeExtension(obj, ".mtl");
        Assert.True(File.Exists(mtl));
        var names = File.ReadAllLines(mtl).Where(l => l.StartsWith("newmtl ")).Select(l => l[7..]).ToList();
        Assert.Equal(["Paint", "Paint_2", "default"], names.Order(StringComparer.Ordinal));
        Assert.Contains("mtllib bracket.mtl", File.ReadAllText(obj));
    }

    [Theory]
    [InlineData("glb")]
    [InlineData("gltf")]
    public async Task Gltf_is_valid_and_a_gltf_is_one_file(string id)
    {
        var written = await Convert(Source(), F.ThreeMf, F.FromId(id)!);
        // SharpGLTF's strict reading checks it against the glTF 2.0 schema and its rules.
        var root = ModelRoot.Load(written, new ReadSettings { Validation = SharpGLTF.Validation.ValidationMode.Strict });
        Assert.Equal(2, root.LogicalMeshes.Count);
        Assert.Equal(2, root.Asset.Version.Major);
        if (id == "gltf")
        {
            Assert.Equal([written], Directory.GetFiles(_dir, "bracket.*").Where(f => !f.EndsWith(".3mf")).ToArray());
            Assert.Contains("data:application/octet-stream;base64,", File.ReadAllText(written));
        }
        // glTF is in metres, Y up: the post is 0.03 m high along Y.
        var (min, max) = (root.LogicalMeshes.SelectMany(m => m.Primitives).Select(p => p.GetVertexAccessor("POSITION").AsVector3Array()).SelectMany(a => a)
            .Aggregate((Min: new Vector3(float.MaxValue), Max: new Vector3(float.MinValue)), (s, v) => (Vector3.Min(s.Min, v), Vector3.Max(s.Max, v))));
        Near(new Vector3(0.01f, 0.03f, 0.02f), max - min, 1e-6f);
    }

    [Fact]
    public async Task Windows_reads_the_3mf_DropWheel_writes()
    {
        var glb = await Convert(Source(), F.ThreeMf, F.Glb);
        var written = await Convert(glb, F.Glb, F.ThreeMf, Path.Combine(_dir, "from glb.3mf"));
        // Windows' own 3MF reader (as its Print 3D and slicers' drivers use) checks the package against the specification.
        using var stream = await Windows.Storage.Streams.FileRandomAccessStream.OpenAsync(written, Windows.Storage.FileAccessMode.Read);
        var package = new Windows.Graphics.Printing3D.Printing3D3MFPackage();
        var model = await package.LoadModelFromPackageAsync(stream);
        Assert.Equal(Windows.Graphics.Printing3D.Printing3DModelUnit.Millimeter, model.Unit);
        Assert.Equal(2, model.Meshes.Count);
        Assert.Equal(24L, model.Meshes.Sum(m => (long)m.IndexCount));
        Assert.Contains(model.Material.BaseGroups.SelectMany(g => g.Bases), b => b.Color.Color.R > 200 && b.Color.Color.B < 60);
    }

    [Fact]
    public async Task Colours_of_corners_go_through_3mf_and_ply()
    {
        var model = Bracket();
        model.Colors = [.. model.Positions.Select(p => new Vector4(p.Z / 30f, 1 - p.Z / 30f, 0.5f, 1))];
        model.Materials[0] = model.Materials[0] with { Color = Vector4.One };
        model.Materials[1] = model.Materials[1] with { Color = Vector4.One };
        var path = Path.Combine(_dir, "shaded.3mf");
        ThreeMf.Write(model, path);

        using (var stream = await Windows.Storage.Streams.FileRandomAccessStream.OpenAsync(path, Windows.Storage.FileAccessMode.Read))
        {
            var windows = await new Windows.Graphics.Printing3D.Printing3D3MFPackage().LoadModelFromPackageAsync(stream);
            Assert.NotEmpty(windows.Material.ColorGroups);
        }
        var read = ThreeMf.Read(path);
        Assert.NotNull(read.Colors);
        var top = read.Positions.Select((p, i) => (p, i)).First(x => x.p.Z > 29.9f).i;
        Assert.True(Close(new Vector4(1, 0, 0.5f, 1), read.Colors![top]), $"{read.Colors[top]}");

        var ply = await Convert(path, F.ThreeMf, F.Ply);
        var fromPly = Ply.Read(ply);
        var plyTop = fromPly.Positions.Select((p, i) => (p, i)).First(x => x.p.Z > 29.9f).i;
        Assert.True(Vector4.Distance(new Vector4(1, 0, 0.5f, 1), fromPly.Colors![plyTop]) < 0.01f);
    }

    [Fact]
    public void Text_stl_and_text_and_big_endian_ply_are_read()
    {
        var stl = Path.Combine(_dir, "pyramid.stl");
        File.WriteAllText(stl, """
            solid pyramid
              facet normal 0 0 -1
                outer loop
                  vertex 0 0 0
                  vertex 0 10 0
                  vertex 10 10 0
                  vertex 10 0 0
                endloop
              endfacet
              facet normal 0 -1 1
                outer loop
                  vertex 0 0 0
                  vertex 10 0 0
                  vertex 5 5 8
                endloop
              endfacet
            endsolid pyramid
            """);
        var pyramid = Stl.Read(stl);
        Assert.Equal(3, pyramid.Triangles.Count);
        Assert.Equal(5, pyramid.Positions.Count);
        Near(new Vector3(10, 10, 8), Size(pyramid, Model3D.Millimetre));

        var ascii = Path.Combine(_dir, "quad.ply");
        File.WriteAllText(ascii, "ply\nformat ascii 1.0\nelement vertex 4\nproperty float x\nproperty float y\nproperty float z\n" +
            "property uchar red\nproperty uchar green\nproperty uchar blue\nelement face 1\nproperty list uchar int vertex_indices\nend_header\n" +
            "0 0 0 255 0 0\n1 0 0 255 0 0\n1 1 0 0 255 0\n0 1 0 0 255 0\n4 0 1 2 3\n");
        var quad = Ply.Read(ascii);
        Assert.Equal(2, quad.Triangles.Count);
        Assert.Equal(new Vector4(1, 0, 0, 1), quad.Colors![0]);

        // The same, binary, the bytes the other way round.
        var big = Path.Combine(_dir, "quad big.ply");
        using (var w = new BinaryWriter(File.Create(big)))
        {
            w.Write(Encoding.ASCII.GetBytes("ply\nformat binary_big_endian 1.0\nelement vertex 4\nproperty float x\nproperty float y\nproperty float z\n" +
                "element face 1\nproperty list uchar int vertex_indices\nend_header\n"));
            void Big(Span<byte> b) { b.Reverse(); w.Write(b); }
            foreach (var p in new[] { (0f, 0f, 0f), (2f, 0f, 0f), (2f, 3f, 0f), (0f, 3f, 4f) })
                foreach (var f in new[] { p.Item1, p.Item2, p.Item3 })
                    Big(BitConverter.GetBytes(f));
            w.Write((byte)4);
            foreach (var i in new[] { 0, 1, 2, 3 })
                Big(BitConverter.GetBytes(i));
        }
        var quadBig = Ply.Read(big);
        Assert.Equal(2, quadBig.Triangles.Count);
        Near(new Vector3(2, 3, 4), Size(quadBig, 1));
    }

    [Fact]
    public async Task An_obj_with_quads_negative_corners_and_a_picture_becomes_a_glb_with_its_picture()
    {
        var png = new SKBitmap(2, 2);
        png.SetPixel(0, 0, SKColors.Orange); png.SetPixel(1, 0, SKColors.Orange); png.SetPixel(0, 1, SKColors.Teal); png.SetPixel(1, 1, SKColors.Teal);
        File.WriteAllBytes(Path.Combine(_dir, "wood grain.png"), png.Encode(SKEncodedImageFormat.Png, 100).ToArray());
        File.WriteAllText(Path.Combine(_dir, "table top.mtl"), "newmtl oak finish\nKd 0.8 0.8 0.8\nmap_Kd -s 1 1 1 wood grain.png\n");
        var obj = Path.Combine(_dir, "table.obj");
        File.WriteAllText(obj, """
            mtllib table top.mtl
            o top
            v 0 1 0
            v 2 1 0
            v 2 1 1
            v 0 1 1
            vt 0 0
            vt 1 0
            vt 1 1
            vt 0 1
            vn 0 1 0
            usemtl oak finish
            f -4/-4/-1 -3/-3/-1 -2/-2/-1 -1/-1/-1
            o leg
            v 0 0 0
            v 0.1 0 0
            v 0 1 0
            f 5 6 7
            """);
        var model = Obj.Read(obj);
        Assert.Equal(3, model.Triangles.Count);
        Assert.Equal(["top", "leg"], model.Parts.Select(p => p.Name));
        // Y up in the OBJ, Z up here: the top lies at height 1.
        Assert.All(model.Triangles.Take(2), t => Assert.Equal(1, model.Positions[t.A].Z, 5));

        var glb = await Convert(obj, F.Obj, F.Glb);
        var back = Gltf.Read(glb);
        var oak = back.Materials.Single(m => m.Name == "oak finish");
        Assert.NotNull(oak.Texture);
        Assert.True(Pictures.IsPng(oak.Texture));
        Assert.True(Close(new Vector4(0.8f, 0.8f, 0.8f, 1), oak.Color), $"{oak.Color}");
        Assert.NotNull(back.Uvs);

        // And back to an OBJ: its picture goes beside it.
        var again = await Convert(glb, F.Glb, F.Obj, Path.Combine(_dir, "again.obj"));
        Assert.Single(Directory.GetFiles(_dir, "again_*.png"));
        Assert.Contains("map_Kd again_", File.ReadAllText(Path.ChangeExtension(again, ".mtl")));
    }

    [Fact]
    public void A_3mf_whose_objects_are_in_parts_of_their_own_is_read_whole()
    {
        // As Bambu Studio writes them: the build's object has components in another model part (3MF's Production extension).
        var path = Path.Combine(_dir, "plate.3mf");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            void Text(string name, string content)
            {
                using var w = new StreamWriter(zip.CreateEntry(name).Open());
                w.Write(content);
            }
            Text("_rels/.rels", """<?xml version="1.0" encoding="UTF-8"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Target="/3D/3dmodel.model" Id="rel-1" Type="http://schemas.microsoft.com/3dmanufacturing/2013/01/3dmodel"/></Relationships>""");
            Text("3D/3dmodel.model", """
                <?xml version="1.0" encoding="UTF-8"?>
                <model unit="millimeter" xmlns="http://schemas.microsoft.com/3dmanufacturing/core/2015/02" xmlns:p="http://schemas.microsoft.com/3dmanufacturing/production/2015/06" requiredextensions="p">
                 <resources>
                  <object id="2" type="model" name="Cube"><components>
                   <component p:path="/3D/Objects/object_1.model" objectid="1" transform="1 0 0 0 1 0 0 0 1 0 0 0"/>
                   <component p:path="/3D/Objects/object_1.model" objectid="1" transform="1 0 0 0 1 0 0 0 1 20 0 0"/>
                  </components></object>
                 </resources>
                 <build><item objectid="2" transform="1 0 0 0 1 0 0 0 1 100 100 0"/></build>
                </model>
                """);
            Text("3D/Objects/object_1.model", """
                <?xml version="1.0" encoding="UTF-8"?>
                <model unit="millimeter" xmlns="http://schemas.microsoft.com/3dmanufacturing/core/2015/02">
                 <resources><object id="1" type="model"><mesh>
                  <vertices><vertex x="0" y="0" z="0"/><vertex x="5" y="0" z="0"/><vertex x="0" y="5" z="0"/><vertex x="0" y="0" z="5"/></vertices>
                  <triangles><triangle v1="0" v2="2" v3="1"/><triangle v1="0" v2="1" v3="3"/><triangle v1="0" v2="3" v3="2"/><triangle v1="1" v2="2" v3="3"/></triangles>
                 </mesh></object></resources>
                </model>
                """);
        }
        var model = ThreeMf.Read(path);
        Assert.Equal(8, model.Triangles.Count);
        Assert.Equal(2, model.Parts.Count);
        Assert.All(model.Parts, p => Assert.Equal("Cube", p.Name));
        var (min, max) = model.Bounds();
        Near(new Vector3(100, 100, 0), min);
        Near(new Vector3(125, 105, 5), max);
    }

    [Fact]
    public async Task A_model_becomes_a_picture_of_it_in_its_colours()
    {
        var png = await Convert(Source(), F.ThreeMf, F.Png);
        using var picture = SKBitmap.Decode(png);
        Assert.InRange(Math.Max(picture.Width, picture.Height), 1500, 1600);
        Assert.Equal(0, picture.GetPixel(0, 0).Alpha);
        int red = 0, blue = 0;
        for (var y = 0; y < picture.Height; y += 4)
            for (var x = 0; x < picture.Width; x += 4)
            {
                var c = picture.GetPixel(x, y);
                if (c.Alpha < 255) continue;
                if (c.Red > 2 * c.Blue && c.Red > 60) red++;
                if (c.Blue > 2 * c.Red && c.Blue > 60) blue++;
            }
        Assert.True(red > 1000 && blue > 1000, $"red {red}, blue {blue}");
        // Lit from one side: the faces don't all show the same shade.
        var shades = new HashSet<byte>();
        for (var y = 0; y < picture.Height; y += 8)
            for (var x = 0; x < picture.Width; x += 8)
                if (picture.GetPixel(x, y) is { Alpha: 255 } c && c.Red > 2 * c.Blue)
                    shades.Add((byte)(c.Red / 16));
        Assert.True(shades.Count >= 2);
    }

    /// <summary>
    /// Files another program wrote (PETAL_MODELS: a folder of them, such as Blender's in D:\.dropwheel-sdk\models3d),
    /// each read and written to every format beside a copy in PETAL_MODELS_OUT, to open there in that program.
    /// </summary>
    [Fact]
    public async Task Real_models_convert_to_every_format()
    {
        var folder = Environment.GetEnvironmentVariable("PETAL_MODELS");
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
            return;
        var outFolder = Environment.GetEnvironmentVariable("PETAL_MODELS_OUT") ?? Path.Combine(_dir, "out");
        Directory.CreateDirectory(outFolder);
        var report = new StringBuilder();
        foreach (var file in Directory.GetFiles(folder).Order())
        {
            if (F.FromPath(file) is not { Family: FormatFamily.Model } format)
                continue;
            var model = ModelConverter.Read(file, format);
            report.AppendLine($"{Path.GetFileName(file)}: {model.Triangles.Count} triangles, {model.Positions.Count} corners, parts [{string.Join(", ", model.Parts.Select(p => $"{p.Name} {p.TriangleCount}"))}], " +
                $"materials [{string.Join(", ", model.Materials.Select(m => $"{m.Name} {m.Color:F2}{(m.Texture is null ? "" : " picture")}"))}], colours {(model.Colors is null ? "no" : "yes")}, " +
                $"uv {(model.Uvs is null ? "no" : "yes")}, normals {(model.Normals is null ? "no" : "yes")}, unit {model.MetresPerUnit}, size {Size(model, model.MetresPerUnit ?? 1):F3}");
            var each = Path.Combine(outFolder, Path.GetFileName(file).Replace('.', '_').Replace(' ', '_'));
            if (Directory.Exists(each))
                Directory.Delete(each, recursive: true);
            Directory.CreateDirectory(each);
            foreach (var to in ConversionMatrix.TargetsFor(format))
            {
                var written = await Convert(file, format, to, Path.Combine(each, "model." + to.WriteExtension));
                if (to.Family == FormatFamily.Model)
                {
                    var again = ModelConverter.Read(written, to);
                    Assert.True(again.Triangles.Count >= model.Triangles.Count * 0.99, $"{written}: {again.Triangles.Count} of {model.Triangles.Count}");
                }
            }
        }
        File.WriteAllText(Path.Combine(outFolder, "report.txt"), report.ToString());
    }

    [Fact]
    public void What_isnt_a_model_says_so()
    {
        var empty = Path.Combine(_dir, "empty.stl");
        File.WriteAllText(empty, "solid nothing\nendsolid nothing\n");
        Assert.Throws<InvalidDataException>(() => Stl.Read(empty));

        var cloud = Path.Combine(_dir, "scan.ply");
        File.WriteAllText(cloud, "ply\nformat ascii 1.0\nelement vertex 2\nproperty float x\nproperty float y\nproperty float z\nend_header\n0 0 0\n1 1 1\n");
        Assert.Throws<InvalidDataException>(() => Ply.Read(cloud));

        var broken = Path.Combine(_dir, "broken.obj");
        File.WriteAllText(broken, "v 0 0 0\nf 1 2 3\n");
        Assert.Throws<InvalidDataException>(() => Obj.Read(broken));
    }
}
