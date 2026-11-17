using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml;

namespace Petal.Shell;

/// <summary>
/// A 3D model's triangles: from STL (text or binary), OBJ, PLY (text or binary), 3MF and GLB (binary glTF), drawn
/// lit and shaded from a corner above, with nothing but .NET Framework. Read from the file only: a model's textures
/// and the files it points to are never opened.
/// </summary>
public sealed class Mesh
{
    /// <summary>The corners, three numbers each.</summary>
    public float[] Points { get; }

    /// <summary>The triangles, three corners each.</summary>
    public int[] Triangles { get; }

    public Mesh(float[] points, int[] triangles)
    {
        Points = points;
        Triangles = triangles;
    }

    public int TriangleCount => Triangles.Length / 3;

    /// <summary>True for the formats made with Z up (STL, PLY, 3MF); OBJ and glTF have Y up.</summary>
    public bool ZUp { get; private set; }

    /// <summary>The most read of a model: beyond this a thumbnail would take too long, and the rest is left out.</summary>
    private const int MostTriangles = 4_000_000;

    // ───────────── reading ─────────────

    /// <summary>
    /// Whether a file's start says it is a model: a GLB's or PLY's mark, a binary STL's size (its count of triangles
    /// at byte 80 and 50 bytes each), a text STL's "solid … facet", or an OBJ's corners.
    /// </summary>
    public static bool LooksLikeModel(byte[] head, int count, long length)
    {
        if (count >= 4 && head[0] == 'g' && head[1] == 'l' && head[2] == 'T' && head[3] == 'F')
            return true;
        if (count >= 4 && head[0] == 'p' && head[1] == 'l' && head[2] == 'y' && (head[3] == 10 || head[3] == 13))
            return true;
        if (count >= 84 && 84 + 50L * BitConverter.ToUInt32(head, 80) == length && BitConverter.ToUInt32(head, 80) > 0)
            return true;
        var text = Encoding.ASCII.GetString(head, 0, count).TrimStart();
        if (text.StartsWith("solid", StringComparison.OrdinalIgnoreCase) && text.IndexOf("facet", StringComparison.OrdinalIgnoreCase) >= 0)
            return true;
        var start = new byte[count];
        Array.Copy(head, start, count);
        return LooksLikeObj(start);
    }

    /// <summary>A model from a file's bytes, the kind told by what they hold; null when they aren't one of a model.</summary>
    public static Mesh? Read(byte[] bytes)
    {
        try
        {
            if (bytes.Length >= 12 && bytes[0] == 'g' && bytes[1] == 'l' && bytes[2] == 'T' && bytes[3] == 'F')
                return Glb(bytes);
            if (bytes.Length >= 4 && bytes[0] == 'p' && bytes[1] == 'l' && bytes[2] == 'y' && (bytes[3] == '\n' || bytes[3] == '\r'))
                return Ply(bytes);
            if (bytes.Length >= 4 && bytes[0] == 'P' && bytes[1] == 'K' && bytes[2] == 3 && bytes[3] == 4)
                return ThreeMf(bytes);
            if (BinaryStl(bytes) is { } stl)
                return stl;
            var head = Encoding.ASCII.GetString(bytes, 0, Math.Min(bytes.Length, 512)).TrimStart();
            if (head.StartsWith("solid", StringComparison.OrdinalIgnoreCase))
                return TextStl(Encoding.ASCII.GetString(bytes));
            if (LooksLikeObj(bytes))
                return Obj(Encoding.UTF8.GetString(bytes));
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or IndexOutOfRangeException or ArgumentException or InvalidDataException or XmlException or EndOfStreamException)
        {
        }
        return null;
    }

    private static Mesh? Made(List<float> points, List<int> triangles, bool zUp)
    {
        int corners = points.Count / 3;
        if (triangles.Count < 3 || corners < 3 || triangles.Any(i => i < 0 || i >= corners))
            return null;
        return new Mesh(points.ToArray(), triangles.ToArray()) { ZUp = zUp };
    }

    /// <summary>A binary STL: an 80-byte header, a count, and 50 bytes a triangle; its size says it is one.</summary>
    private static Mesh? BinaryStl(byte[] b)
    {
        if (b.Length < 84)
            return null;
        long count = BitConverter.ToUInt32(b, 80);
        if (84 + 50 * count != b.Length || count == 0)
            return null;
        int n = (int)Math.Min(count, MostTriangles);
        var points = new float[n * 9];
        var triangles = new int[n * 3];
        for (int t = 0; t < n; t++)
        {
            int at = 84 + t * 50 + 12;
            for (int k = 0; k < 9; k++)
                points[t * 9 + k] = BitConverter.ToSingle(b, at + k * 4);
            triangles[t * 3] = t * 3;
            triangles[t * 3 + 1] = t * 3 + 1;
            triangles[t * 3 + 2] = t * 3 + 2;
        }
        return new Mesh(points, triangles) { ZUp = true };
    }

    private static Mesh? TextStl(string text)
    {
        var points = new List<float>();
        var triangles = new List<int>();
        foreach (var raw in Lines(text))
        {
            var line = raw.Trim();
            if (!line.StartsWith("vertex", StringComparison.OrdinalIgnoreCase))
                continue;
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 4)
                continue;
            points.Add(Number(parts[1]));
            points.Add(Number(parts[2]));
            points.Add(Number(parts[3]));
            if (points.Count / 3 % 3 == 0)
            {
                int c = points.Count / 3;
                triangles.Add(c - 3);
                triangles.Add(c - 2);
                triangles.Add(c - 1);
                if (triangles.Count / 3 >= MostTriangles)
                    break;
            }
        }
        return Made(points, triangles, zUp: true);
    }

    /// <summary>An OBJ is text whose lines start with "v ", "f ", "#", "o "…: at least a few corners near the start, nothing binary.</summary>
    private static bool LooksLikeObj(byte[] b)
    {
        int n = Math.Min(b.Length, 4096), vertices = 0;
        for (int i = 0; i < n; i++)
            if (b[i] == 0)
                return false;
        foreach (var line in Lines(Encoding.ASCII.GetString(b, 0, n)))
            if (line.StartsWith("v ", StringComparison.Ordinal))
                vertices++;
        return vertices >= 3;
    }

    private static Mesh? Obj(string text)
    {
        var points = new List<float>();
        var triangles = new List<int>();
        foreach (var raw in Lines(text))
        {
            var line = raw.Trim();
            if (line.StartsWith("v ", StringComparison.Ordinal))
            {
                var p = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (p.Length >= 4)
                {
                    points.Add(Number(p[1]));
                    points.Add(Number(p[2]));
                    points.Add(Number(p[3]));
                }
            }
            else if (line.StartsWith("f ", StringComparison.Ordinal))
            {
                // Corners as "a", "a/b" or "a/b/c", counted from 1, or back from the latest when negative; a polygon as a fan.
                var corners = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Skip(1)
                    .Select(c => int.Parse(c.Split('/')[0], CultureInfo.InvariantCulture))
                    .Select(i => i < 0 ? points.Count / 3 + i : i - 1).ToList();
                for (int k = 1; k + 1 < corners.Count; k++)
                {
                    triangles.Add(corners[0]);
                    triangles.Add(corners[k]);
                    triangles.Add(corners[k + 1]);
                }
                if (triangles.Count / 3 >= MostTriangles)
                    break;
            }
        }
        return Made(points, triangles, zUp: false);
    }

    /// <summary>A PLY's corners and faces, in text or little- or big-endian binary; other properties skipped by their sizes.</summary>
    private static Mesh? Ply(byte[] b)
    {
        int end = IndexOf(b, Encoding.ASCII.GetBytes("end_header"));
        if (end < 0)
            return null;
        int bodyStart = end + "end_header".Length;
        while (bodyStart < b.Length && b[bodyStart] != '\n')
            bodyStart++;
        bodyStart++;
        var header = Encoding.ASCII.GetString(b, 0, end);
        string format = "";
        var elements = new List<(string Name, int Count, List<(string Type, string? ListCount, string Name)> Properties)>();
        foreach (var raw in Lines(header))
        {
            var p = raw.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (p.Length == 0)
                continue;
            if (p[0] == "format" && p.Length > 1)
                format = p[1];
            else if (p[0] == "element" && p.Length >= 3)
                elements.Add((p[1], int.Parse(p[2], CultureInfo.InvariantCulture), []));
            else if (p[0] == "property" && elements.Count > 0)
                elements[elements.Count - 1].Properties.Add(p.Length >= 5 && p[1] == "list" ? (p[3], p[2], p[4]) : (p[1], null, p[p.Length - 1]));
        }
        var points = new List<float>();
        var triangles = new List<int>();
        if (format == "ascii")
        {
            var words = Encoding.ASCII.GetString(b, bodyStart, b.Length - bodyStart).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            int w = 0;
            foreach (var (name, count, properties) in elements)
                for (int i = 0; i < count; i++)
                    foreach (var (type, listCount, prop) in properties)
                    {
                        if (listCount is not null)
                        {
                            int k = int.Parse(words[w++], CultureInfo.InvariantCulture);
                            var corners = Enumerable.Range(0, k).Select(_ => int.Parse(words[w++], CultureInfo.InvariantCulture)).ToList();
                            if (name == "face")
                                Fan(triangles, corners);
                        }
                        else
                        {
                            var value = Number(words[w++]);
                            if (name == "vertex" && prop is "x" or "y" or "z")
                                points.Add(value);
                        }
                    }
        }
        else
        {
            bool big = format == "binary_big_endian";
            int at = bodyStart;
            double Read(string type)
            {
                int size = type switch { "char" or "uchar" or "int8" or "uint8" => 1, "short" or "ushort" or "int16" or "uint16" => 2, "double" or "float64" => 8, _ => 4 };
                var bytes = new byte[size];
                Array.Copy(b, at, bytes, 0, size);
                at += size;
                if (big != !BitConverter.IsLittleEndian)
                    Array.Reverse(bytes);
                return type switch
                {
                    "char" or "int8" => (sbyte)bytes[0],
                    "uchar" or "uint8" => bytes[0],
                    "short" or "int16" => BitConverter.ToInt16(bytes, 0),
                    "ushort" or "uint16" => BitConverter.ToUInt16(bytes, 0),
                    "int" or "int32" => BitConverter.ToInt32(bytes, 0),
                    "uint" or "uint32" => BitConverter.ToUInt32(bytes, 0),
                    "double" or "float64" => BitConverter.ToDouble(bytes, 0),
                    _ => BitConverter.ToSingle(bytes, 0),
                };
            }
            foreach (var (name, count, properties) in elements)
                for (int i = 0; i < count; i++)
                    foreach (var (type, listCount, prop) in properties)
                    {
                        if (listCount is not null)
                        {
                            int k = (int)Read(listCount);
                            var corners = new List<int>(k);
                            for (int c = 0; c < k; c++)
                                corners.Add((int)Read(type));
                            if (name == "face")
                                Fan(triangles, corners);
                        }
                        else
                        {
                            var value = (float)Read(type);
                            if (name == "vertex" && prop is "x" or "y" or "z")
                                points.Add(value);
                        }
                    }
        }
        return Made(points, triangles, zUp: true);
    }

    private static void Fan(List<int> triangles, List<int> corners)
    {
        for (int k = 1; k + 1 < corners.Count; k++)
        {
            triangles.Add(corners[0]);
            triangles.Add(corners[k]);
            triangles.Add(corners[k + 1]);
        }
    }

    /// <summary>A 3MF: the model's XML inside the ZIP, every object's corners and triangles.</summary>
    private static Mesh? ThreeMf(byte[] bytes)
    {
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        var entry = zip.Entries.FirstOrDefault(e => e.FullName.EndsWith(".model", StringComparison.OrdinalIgnoreCase) && e.FullName.StartsWith("3D/", StringComparison.OrdinalIgnoreCase));
        if (entry is null)
            return null;
        var points = new List<float>();
        var triangles = new List<int>();
        using var reader = XmlReader.Create(entry.Open(), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        int objectStart = 0;
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element)
                continue;
            switch (reader.LocalName)
            {
                case "mesh":
                    objectStart = points.Count / 3;
                    break;
                case "vertex":
                    points.Add(Number(reader.GetAttribute("x") ?? "0"));
                    points.Add(Number(reader.GetAttribute("y") ?? "0"));
                    points.Add(Number(reader.GetAttribute("z") ?? "0"));
                    break;
                case "triangle":
                    triangles.Add(objectStart + int.Parse(reader.GetAttribute("v1") ?? "0", CultureInfo.InvariantCulture));
                    triangles.Add(objectStart + int.Parse(reader.GetAttribute("v2") ?? "0", CultureInfo.InvariantCulture));
                    triangles.Add(objectStart + int.Parse(reader.GetAttribute("v3") ?? "0", CultureInfo.InvariantCulture));
                    break;
            }
            if (triangles.Count / 3 >= MostTriangles)
                break;
        }
        return Made(points, triangles, zUp: true);
    }

    /// <summary>
    /// A GLB: its JSON and its binary chunk; each node's meshes placed by the node's transform down the scene, their
    /// positions (floats) and indices (bytes, shorts or ints, or none for a plain list of triangles).
    /// </summary>
    private static Mesh? Glb(byte[] b)
    {
        if (BitConverter.ToUInt32(b, 4) != 2)
            return null;
        int at = 12;
        string? json = null;
        int binStart = -1;
        while (at + 8 <= b.Length)
        {
            int length = BitConverter.ToInt32(b, at);
            uint type = BitConverter.ToUInt32(b, at + 4);
            if (type == 0x4E4F534A)
                json = Encoding.UTF8.GetString(b, at + 8, length);
            else if (type == 0x004E4942)
                binStart = at + 8;
            at += 8 + length;
        }
        if (json is null || binStart < 0 || Json.Parse(json) is not Dictionary<string, object?> root)
            return null;
        var accessors = List(root, "accessors");
        var views = List(root, "bufferViews");
        var meshes = List(root, "meshes");
        var nodes = List(root, "nodes");
        var points = new List<float>();
        var triangles = new List<int>();

        float[] Floats(int accessor, out int count)
        {
            var a = (Dictionary<string, object?>)accessors[accessor]!;
            var view = (Dictionary<string, object?>)views[Int(a, "bufferView")]!;
            count = Int(a, "count");
            int start = binStart + Int(view, "byteOffset") + Int(a, "byteOffset");
            int stride = Int(view, "byteStride") is > 0 and var s ? s : 12;
            var values = new float[count * 3];
            for (int i = 0; i < count; i++)
                for (int k = 0; k < 3; k++)
                    values[i * 3 + k] = BitConverter.ToSingle(b, start + i * stride + k * 4);
            return values;
        }

        int[] Indices(int accessor)
        {
            var a = (Dictionary<string, object?>)accessors[accessor]!;
            var view = (Dictionary<string, object?>)views[Int(a, "bufferView")]!;
            int count = Int(a, "count"), start = binStart + Int(view, "byteOffset") + Int(a, "byteOffset"), kind = Int(a, "componentType");
            var values = new int[count];
            for (int i = 0; i < count; i++)
                values[i] = kind switch
                {
                    5121 => b[start + i],
                    5123 => BitConverter.ToUInt16(b, start + i * 2),
                    _ => (int)BitConverter.ToUInt32(b, start + i * 4),
                };
            return values;
        }

        void Add(int meshIndex, double[] matrix)
        {
            var mesh = (Dictionary<string, object?>)meshes[meshIndex]!;
            foreach (var p in List(mesh, "primitives").OfType<Dictionary<string, object?>>())
            {
                if (p.TryGetValue("mode", out var mode) && mode is double m && (int)m != 4)
                    continue;
                if (p["attributes"] is not Dictionary<string, object?> attributes || !attributes.ContainsKey("POSITION"))
                    continue;
                var positions = Floats(Int(attributes, "POSITION"), out int count);
                int first = points.Count / 3;
                for (int i = 0; i < count; i++)
                {
                    double x = positions[i * 3], y = positions[i * 3 + 1], z = positions[i * 3 + 2];
                    points.Add((float)(matrix[0] * x + matrix[4] * y + matrix[8] * z + matrix[12]));
                    points.Add((float)(matrix[1] * x + matrix[5] * y + matrix[9] * z + matrix[13]));
                    points.Add((float)(matrix[2] * x + matrix[6] * y + matrix[10] * z + matrix[14]));
                }
                var indices = p.ContainsKey("indices") ? Indices(Int(p, "indices")) : Enumerable.Range(0, count).ToArray();
                foreach (var i in indices)
                    triangles.Add(first + i);
            }
        }

        void Walk(int nodeIndex, double[] parent, int depth)
        {
            if (depth > 64)
                return;
            var node = (Dictionary<string, object?>)nodes[nodeIndex]!;
            var matrix = Multiply(parent, Local(node));
            if (node.ContainsKey("mesh"))
                Add(Int(node, "mesh"), matrix);
            foreach (var child in List(node, "children").OfType<double>())
                Walk((int)child, matrix, depth + 1);
        }

        var identity = new double[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 };
        var scenes = List(root, "scenes");
        int scene = root.ContainsKey("scene") ? Int(root, "scene") : 0;
        if (scenes.Count > scene && scenes[scene] is Dictionary<string, object?> sc)
            foreach (var n in List(sc, "nodes").OfType<double>())
                Walk((int)n, identity, 0);
        else
            for (int i = 0; i < meshes.Count; i++)
                Add(i, identity);
        return Made(points, triangles, zUp: false);
    }

    /// <summary>A node's own transform, column-major as glTF writes it: its matrix, or its translation, rotation and scale.</summary>
    private static double[] Local(Dictionary<string, object?> node)
    {
        if (List(node, "matrix") is { Count: 16 } m)
            return m.Select(v => (double)v!).ToArray();
        var t = List(node, "translation") is { Count: 3 } tl ? tl.Select(v => (double)v!).ToArray() : [0, 0, 0];
        var r = List(node, "rotation") is { Count: 4 } rl ? rl.Select(v => (double)v!).ToArray() : [0, 0, 0, 1];
        var s = List(node, "scale") is { Count: 3 } sl ? sl.Select(v => (double)v!).ToArray() : [1, 1, 1];
        double x = r[0], y = r[1], z = r[2], w = r[3];
        return
        [
            (1 - 2 * (y * y + z * z)) * s[0], 2 * (x * y + z * w) * s[0], 2 * (x * z - y * w) * s[0], 0,
            2 * (x * y - z * w) * s[1], (1 - 2 * (x * x + z * z)) * s[1], 2 * (y * z + x * w) * s[1], 0,
            2 * (x * z + y * w) * s[2], 2 * (y * z - x * w) * s[2], (1 - 2 * (x * x + y * y)) * s[2], 0,
            t[0], t[1], t[2], 1,
        ];
    }

    private static double[] Multiply(double[] a, double[] b)
    {
        var r = new double[16];
        for (int c = 0; c < 4; c++)
            for (int row = 0; row < 4; row++)
                r[c * 4 + row] = a[row] * b[c * 4] + a[4 + row] * b[c * 4 + 1] + a[8 + row] * b[c * 4 + 2] + a[12 + row] * b[c * 4 + 3];
        return r;
    }

    private static List<object?> List(Dictionary<string, object?> d, string key) => d.TryGetValue(key, out var v) && v is List<object?> l ? l : [];

    private static int Int(Dictionary<string, object?> d, string key) => d.TryGetValue(key, out var v) && v is double n ? (int)n : 0;

    private static float Number(string text) => float.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);

    private static IEnumerable<string> Lines(string text) => text.Split('\n');

    private static int IndexOf(byte[] b, byte[] sought)
    {
        for (int i = 0; i + sought.Length <= Math.Min(b.Length, 65536); i++)
        {
            int k = 0;
            while (k < sought.Length && b[i + k] == sought[k])
                k++;
            if (k == sought.Length)
                return i;
        }
        return -1;
    }

    // ───────────── drawing ─────────────

    /// <summary>
    /// The model seen from a corner above (turned by <paramref name="yaw"/> and tilted by <paramref name="pitch"/>,
    /// in degrees), filling about <paramref name="size"/> pixels, lit from the viewer's upper left, on a clear
    /// background. Drawn twice the size and made smaller, so its edges are smooth.
    /// </summary>
    public Bitmap Render(int size, double yaw = -35, double pitch = 25)
    {
        int big = Math.Max(16, size * 2);
        // STL, 3MF and PLY are made with Z up, OBJ and glTF with Y up: either is turned to stand upright.
        float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;
        for (int i = 0; i < Points.Length; i += 3)
        {
            minX = Math.Min(minX, Points[i]); maxX = Math.Max(maxX, Points[i]);
            minY = Math.Min(minY, Points[i + 1]); maxY = Math.Max(maxY, Points[i + 1]);
            minZ = Math.Min(minZ, Points[i + 2]); maxZ = Math.Max(maxZ, Points[i + 2]);
        }
        bool zUp = ZUp;
        double cx = (minX + maxX) / 2, cy = (minY + maxY) / 2, cz = (minZ + maxZ) / 2;
        double a = yaw * Math.PI / 180, e = pitch * Math.PI / 180;
        double ca = Math.Cos(a), sa = Math.Sin(a), ce = Math.Cos(e), se = Math.Sin(e);
        int n = Points.Length / 3;
        var sx = new double[n];
        var sy = new double[n];
        var sz = new double[n];
        double lx = double.MaxValue, ly = double.MaxValue, hx = double.MinValue, hy = double.MinValue;
        for (int i = 0; i < n; i++)
        {
            double x = Points[i * 3] - cx, y = Points[i * 3 + 1] - cy, z = Points[i * 3 + 2] - cz;
            // Up is +Y in the view.
            if (zUp)
                (y, z) = (z, -y);
            double x1 = x * ca + z * sa, z1 = -x * sa + z * ca;
            double y2 = y * ce - z1 * se, z2 = y * se + z1 * ce;
            sx[i] = x1;
            sy[i] = -y2;
            sz[i] = z2;
            lx = Math.Min(lx, x1); hx = Math.Max(hx, x1);
            ly = Math.Min(ly, -y2); hy = Math.Max(hy, -y2);
        }
        double span = Math.Max(hx - lx, hy - ly);
        double scale = span > 0 ? big * 0.86 / span : 1;
        double ox = big / 2.0 - (lx + hx) / 2 * scale, oy = big / 2.0 - (ly + hy) / 2 * scale;
        for (int i = 0; i < n; i++)
        {
            sx[i] = sx[i] * scale + ox;
            sy[i] = sy[i] * scale + oy;
            sz[i] *= scale;
        }
        var depth = new float[big * big];
        for (int i = 0; i < depth.Length; i++)
            depth[i] = float.MaxValue;
        var colour = new int[big * big];
        // A light from the viewer's upper left, a little in front.
        double lightX = -0.45, lightY = -0.6, lightZ = -0.66;
        for (int t = 0; t + 2 < Triangles.Length; t += 3)
        {
            int i0 = Triangles[t], i1 = Triangles[t + 1], i2 = Triangles[t + 2];
            double ux = sx[i1] - sx[i0], uy = sy[i1] - sy[i0], uz = sz[i1] - sz[i0];
            double vx = sx[i2] - sx[i0], vy = sy[i2] - sy[i0], vz = sz[i2] - sz[i0];
            double nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;
            double length = Math.Sqrt(nx * nx + ny * ny + nz * nz);
            if (length == 0)
                continue;
            // Lit on whichever side faces the viewer: models aren't always wound the same way.
            double light = Math.Abs(nx * lightX + ny * lightY + nz * lightZ) / length;
            double shade = 0.32 + 0.68 * light;
            int rgb = unchecked((int)0xFF000000) | (int)(0x6E * shade + 40) << 16 | (int)(0x8C * shade + 40) << 8 | (int)(0xB4 * shade + 40);
            Fill(sx[i0], sy[i0], sz[i0], sx[i1], sy[i1], sz[i1], sx[i2], sy[i2], sz[i2], big, depth, colour, rgb);
        }
        var picture = new Bitmap(big, big, PixelFormat.Format32bppArgb);
        var data = picture.LockBits(new Rectangle(0, 0, big, big), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (int y = 0; y < big; y++)
                Marshal.Copy(colour, y * big, data.Scan0 + y * data.Stride, big);
        }
        finally
        {
            picture.UnlockBits(data);
        }
        return EmbeddedPreview.Fit(picture, size);
    }

    /// <summary>One triangle into the picture, nearer pixels over farther ones.</summary>
    private static void Fill(double x0, double y0, double z0, double x1, double y1, double z1, double x2, double y2, double z2, int size, float[] depth, int[] colour, int rgb)
    {
        int left = Math.Max(0, (int)Math.Floor(Math.Min(x0, Math.Min(x1, x2)))), right = Math.Min(size - 1, (int)Math.Ceiling(Math.Max(x0, Math.Max(x1, x2))));
        int top = Math.Max(0, (int)Math.Floor(Math.Min(y0, Math.Min(y1, y2)))), bottom = Math.Min(size - 1, (int)Math.Ceiling(Math.Max(y0, Math.Max(y1, y2))));
        double area = (x1 - x0) * (y2 - y0) - (x2 - x0) * (y1 - y0);
        if (Math.Abs(area) < 1e-9 || left > right || top > bottom)
            return;
        for (int y = top; y <= bottom; y++)
        {
            double py = y + 0.5;
            for (int x = left; x <= right; x++)
            {
                double px = x + 0.5;
                double w0 = ((x1 - px) * (y2 - py) - (x2 - px) * (y1 - py)) / area;
                double w1 = ((x2 - px) * (y0 - py) - (x0 - px) * (y2 - py)) / area;
                double w2 = 1 - w0 - w1;
                if (w0 < 0 || w1 < 0 || w2 < 0)
                    continue;
                float z = (float)(w0 * z0 + w1 * z1 + w2 * z2);
                int i = y * size + x;
                if (z < depth[i])
                {
                    depth[i] = z;
                    colour[i] = rgb;
                }
            }
        }
    }
}

/// <summary>A small JSON reader, for a GLB's description: objects, lists, numbers, strings, true, false and null.</summary>
internal static class Json
{
    public static object? Parse(string text)
    {
        int at = 0;
        var value = Value(text, ref at);
        return value;
    }

    private static void Space(string s, ref int at)
    {
        while (at < s.Length && char.IsWhiteSpace(s[at]))
            at++;
    }

    private static object? Value(string s, ref int at)
    {
        Space(s, ref at);
        if (at >= s.Length)
            throw new FormatException("JSON ended early");
        switch (s[at])
        {
            case '{':
                var obj = new Dictionary<string, object?>();
                at++;
                Space(s, ref at);
                if (s[at] == '}')
                {
                    at++;
                    return obj;
                }
                while (true)
                {
                    Space(s, ref at);
                    var key = (string)Value(s, ref at)!;
                    Space(s, ref at);
                    if (s[at++] != ':')
                        throw new FormatException("Expected :");
                    obj[key] = Value(s, ref at);
                    Space(s, ref at);
                    if (s[at] == ',')
                    {
                        at++;
                        continue;
                    }
                    if (s[at++] != '}')
                        throw new FormatException("Expected }");
                    return obj;
                }
            case '[':
                var list = new List<object?>();
                at++;
                Space(s, ref at);
                if (s[at] == ']')
                {
                    at++;
                    return list;
                }
                while (true)
                {
                    list.Add(Value(s, ref at));
                    Space(s, ref at);
                    if (s[at] == ',')
                    {
                        at++;
                        continue;
                    }
                    if (s[at++] != ']')
                        throw new FormatException("Expected ]");
                    return list;
                }
            case '"':
                var sb = new StringBuilder();
                at++;
                while (s[at] != '"')
                {
                    if (s[at] == '\\')
                    {
                        at++;
                        char c = s[at];
                        sb.Append(c switch
                        {
                            'n' => '\n',
                            't' => '\t',
                            'r' => '\r',
                            'b' => '\b',
                            'f' => '\f',
                            'u' => (char)int.Parse(s.Substring(at + 1, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                            _ => c,
                        });
                        if (c == 'u')
                            at += 4;
                        at++;
                    }
                    else
                        sb.Append(s[at++]);
                }
                at++;
                return sb.ToString();
            case 't':
                at += 4;
                return true;
            case 'f':
                at += 5;
                return false;
            case 'n':
                at += 4;
                return null;
            default:
                int start = at;
                while (at < s.Length && (char.IsDigit(s[at]) || s[at] is '-' or '+' or '.' or 'e' or 'E'))
                    at++;
                return double.Parse(s.Substring(start, at - start), NumberStyles.Float, CultureInfo.InvariantCulture);
        }
    }
}
