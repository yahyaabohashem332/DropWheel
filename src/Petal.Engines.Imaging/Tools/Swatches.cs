using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json;
using ImageMagick;
using ImageMagick.Drawing;

namespace Petal.Engines.Imaging;

/// <summary>A colour swatch: its name, its colour as stored (RGB, CMYK, Lab or Gray) and as sRGB for screens.</summary>
/// <param name="Values">As stored: RGB and Gray 0–1, CMYK 0–1 ink, Lab L 0–100 with a and b around 0.</param>
public sealed record Swatch(string Name, string Model, float[] Values, string? Group = null, bool Spot = false)
{
    public (byte R, byte G, byte B) Rgb => Swatches.ToRgb(Model, Values);

    public string Hex => $"#{Rgb.R:X2}{Rgb.G:X2}{Rgb.B:X2}";

    public static Swatch FromRgb(string name, byte r, byte g, byte b, string? group = null) => new(name, "RGB", [r / 255f, g / 255f, b / 255f], group);
}

/// <summary>
/// Colour swatch files: Adobe Swatch Exchange (.ase, shared by Photoshop, Illustrator and
/// InDesign) and Photoshop swatches (.aco). Read, write, show as a picture, export for code, and
/// extract the main colours of a picture.
/// </summary>
public static class Swatches
{
    public static IReadOnlyList<Swatch> Read(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length >= 4 && bytes.AsSpan(0, 4).SequenceEqual("ASEF"u8))
            return ReadAse(bytes);
        if (bytes.Length >= 4 && BinaryPrimitives.ReadUInt16BigEndian(bytes) is 1 or 2)
            return ReadAco(bytes);
        throw new InvalidDataException("This isn't a swatch file.");
    }

    // ─────────────── ASE ───────────────

    private static List<Swatch> ReadAse(byte[] data)
    {
        var swatches = new List<Swatch>();
        int count = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(8));
        int at = 12;
        string? group = null;
        for (int i = 0; i < count && at + 6 <= data.Length; i++)
        {
            int type = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(at));
            int length = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(at + 2));
            int body = at + 6;
            if (body + length > data.Length)
                break;
            switch (type)
            {
                case 0xC001:
                    group = ReadName(data, body, out _);
                    break;
                case 0xC002:
                    group = null;
                    break;
                case 0x0001:
                    var name = ReadName(data, body, out int p);
                    var model = Encoding.ASCII.GetString(data, p, 4).Trim();
                    p += 4;
                    int n = model switch { "CMYK" => 4, "Gray" => 1, _ => 3 };
                    var values = new float[n];
                    for (int v = 0; v < n; v++)
                        values[v] = BinaryPrimitives.ReadSingleBigEndian(data.AsSpan(p + v * 4));
                    p += n * 4;
                    int kind = p + 2 <= body + length ? BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(p)) : 2;
                    if (model == "LAB")
                        values[0] *= 100; // stored as 0–1
                    swatches.Add(new Swatch(name, model == "LAB" ? "Lab" : model, values, group, kind == 1));
                    break;
            }
            at = body + length;
        }
        return swatches;
    }

    private static string ReadName(byte[] data, int at, out int after)
    {
        int chars = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(at));
        after = at + 2 + chars * 2;
        return Encoding.BigEndianUnicode.GetString(data, at + 2, chars * 2).TrimEnd('\0');
    }

    public static void WriteAse(IReadOnlyList<Swatch> swatches, string path)
    {
        var blocks = new List<byte[]>();
        // In the order the colours come; each group's colours together.
        foreach (var group in swatches.GroupBy(s => s.Group))
        {
            if (group.Key is not null)
                blocks.Add(Block(0xC001, Name(group.Key)));
            foreach (var swatch in group)
            {
                var model = swatch.Model == "Lab" ? "LAB " : swatch.Model.PadRight(4)[..4];
                var values = swatch.Model == "Lab" ? [swatch.Values[0] / 100, swatch.Values[1], swatch.Values[2]] : swatch.Values;
                var body = new MemoryStream();
                body.Write(Name(swatch.Name));
                body.Write(Encoding.ASCII.GetBytes(model));
                foreach (var v in values)
                    WriteBig(body, v);
                WriteBig(body, (ushort)(swatch.Spot ? 1 : 2));
                blocks.Add(Block(0x0001, body.ToArray()));
            }
            if (group.Key is not null)
                blocks.Add(Block(0xC002, []));
        }
        var o = new MemoryStream();
        o.Write("ASEF"u8);
        WriteBig(o, (ushort)1);
        WriteBig(o, (ushort)0);
        WriteBig(o, (uint)blocks.Count);
        foreach (var block in blocks)
            o.Write(block);
        File.WriteAllBytes(path, o.ToArray());
    }

    private static byte[] Name(string name)
    {
        var o = new MemoryStream();
        WriteBig(o, (ushort)(name.Length + 1));
        o.Write(Encoding.BigEndianUnicode.GetBytes(name + "\0"));
        return o.ToArray();
    }

    private static byte[] Block(ushort type, byte[] body)
    {
        var o = new MemoryStream();
        WriteBig(o, type);
        WriteBig(o, (uint)body.Length);
        o.Write(body);
        return o.ToArray();
    }

    // ─────────────── ACO ───────────────

    private static List<Swatch> ReadAco(byte[] data)
    {
        // Version 1 (no names) usually comes first, then version 2 with names: prefer the second.
        int at = 0;
        List<Swatch>? best = null;
        while (at + 4 <= data.Length)
        {
            int version = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(at));
            int count = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(at + 2));
            if (version is not (1 or 2))
                break;
            at += 4;
            var list = new List<Swatch>();
            for (int i = 0; i < count && at + 10 <= data.Length; i++)
            {
                int space = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(at));
                ushort w(int k) => BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(at + 2 + k * 2));
                short sw(int k) => BinaryPrimitives.ReadInt16BigEndian(data.AsSpan(at + 2 + k * 2));
                (string model, float[] values) = space switch
                {
                    0 => ("RGB", new[] { w(0) / 65535f, w(1) / 65535f, w(2) / 65535f }),
                    2 => ("CMYK", new[] { 1 - w(0) / 65535f, 1 - w(1) / 65535f, 1 - w(2) / 65535f, 1 - w(3) / 65535f }),
                    7 => ("Lab", new[] { w(0) / 100f, sw(1) / 100f, sw(2) / 100f }),
                    8 => ("Gray", new[] { 1 - w(0) / 10000f }),
                    9 => ("CMYK", new[] { w(0) / 10000f, w(1) / 10000f, w(2) / 10000f, w(3) / 10000f }),
                    1 => ("RGB", Hsb(w(0) / 65535f * 360, w(1) / 65535f, w(2) / 65535f)),
                    _ => ("RGB", new[] { 0f, 0f, 0f }),
                };
                at += 10;
                string name = $"Colour {i + 1}";
                if (version == 2 && at + 4 <= data.Length)
                {
                    int chars = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(at));
                    at += 4;
                    if (at + chars * 2 <= data.Length)
                        name = Encoding.BigEndianUnicode.GetString(data, at, chars * 2).TrimEnd('\0');
                    at += chars * 2;
                }
                list.Add(new Swatch(name, model, values));
            }
            best = list;
            if (version == 2)
                break;
        }
        return best ?? [];
    }

    private static float[] Hsb(float h, float s, float v)
    {
        float c = v * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - c;
        var (r, g, b) = (int)(h / 60) switch { 0 => (c, x, 0f), 1 => (x, c, 0f), 2 => (0f, c, x), 3 => (0f, x, c), 4 => (x, 0f, c), _ => (c, 0f, x) };
        return [r + m, g + m, b + m];
    }

    public static void WriteAco(IReadOnlyList<Swatch> swatches, string path)
    {
        var o = new MemoryStream();
        foreach (int version in new[] { 1, 2 })
        {
            WriteBig(o, (ushort)version);
            WriteBig(o, (ushort)swatches.Count);
            foreach (var swatch in swatches)
            {
                var v = swatch.Values;
                switch (swatch.Model)
                {
                    case "CMYK":
                        WriteBig(o, (ushort)2);
                        for (int k = 0; k < 4; k++)
                            WriteBig(o, (ushort)Math.Round((1 - Math.Clamp((double)v[k], 0, 1)) * 65535));
                        break;
                    case "Lab":
                        WriteBig(o, (ushort)7);
                        WriteBig(o, (ushort)Math.Round(Math.Clamp((double)v[0], 0, 100) * 100));
                        WriteBig(o, (ushort)(short)Math.Round(Math.Clamp((double)v[1], -128, 127) * 100));
                        WriteBig(o, (ushort)(short)Math.Round(Math.Clamp((double)v[2], -128, 127) * 100));
                        WriteBig(o, (ushort)0);
                        break;
                    case "Gray":
                        WriteBig(o, (ushort)8);
                        WriteBig(o, (ushort)Math.Round((1 - Math.Clamp((double)v[0], 0, 1)) * 10000));
                        WriteBig(o, (ushort)0);
                        WriteBig(o, (ushort)0);
                        WriteBig(o, (ushort)0);
                        break;
                    default:
                        WriteBig(o, (ushort)0);
                        // In double precision: in float, 0.3 × 65535 lands exactly on .5 and rounds the wrong way.
                        for (int k = 0; k < 3; k++)
                            WriteBig(o, (ushort)Math.Round(Math.Clamp((double)v[k], 0, 1) * 65535));
                        WriteBig(o, (ushort)0);
                        break;
                }
                if (version == 2)
                {
                    WriteBig(o, (uint)(swatch.Name.Length + 1));
                    o.Write(Encoding.BigEndianUnicode.GetBytes(swatch.Name + "\0"));
                }
            }
        }
        File.WriteAllBytes(path, o.ToArray());
    }

    // ─────────────── Colour ───────────────

    /// <summary>sRGB for a swatch: CMYK by simple ink arithmetic, Lab (D50, as Adobe stores it) exactly.</summary>
    public static (byte R, byte G, byte B) ToRgb(string model, float[] v)
    {
        static byte B8(double x) => (byte)Math.Clamp(Math.Round(x * 255), 0, 255);
        switch (model)
        {
            case "CMYK":
                return (B8((1 - v[0]) * (1 - v[3])), B8((1 - v[1]) * (1 - v[3])), B8((1 - v[2]) * (1 - v[3])));
            case "Gray":
                return (B8(v[0]), B8(v[0]), B8(v[0]));
            case "Lab":
                var (r, g, b) = LabToSrgb(v[0], v[1], v[2]);
                return (B8(r), B8(g), B8(b));
            default:
                return (B8(v[0]), B8(v[1]), B8(v[2]));
        }
    }

    internal static (double R, double G, double B) LabToSrgb(double l, double a, double b)
    {
        // Lab (D50) → XYZ (D50) → Bradford to D65 → linear sRGB → sRGB.
        double fy = (l + 16) / 116, fx = fy + a / 500, fz = fy - b / 200;
        static double F(double t) => t * t * t > 0.008856 ? t * t * t : (t - 16.0 / 116) / 7.787;
        double x = 0.9642 * F(fx), y = 1.0 * F(fy), z = 0.8251 * F(fz);
        double x65 = 0.9555766 * x - 0.0230393 * y + 0.0631636 * z;
        double y65 = -0.0282895 * x + 1.0099416 * y + 0.0210077 * z;
        double z65 = 0.0122982 * x - 0.0204830 * y + 1.3299098 * z;
        double rl = 3.2404542 * x65 - 1.5371385 * y65 - 0.4985314 * z65;
        double gl = -0.9692660 * x65 + 1.8760108 * y65 + 0.0415560 * z65;
        double bl = 0.0556434 * x65 - 0.2040259 * y65 + 1.0572252 * z65;
        static double Gamma(double c) => c <= 0.0031308 ? 12.92 * c : 1.055 * Math.Pow(Math.Max(c, 0), 1 / 2.4) - 0.055;
        return (Gamma(rl), Gamma(gl), Gamma(bl));
    }

    // ─────────────── Exports ───────────────

    public static string ToCss(IReadOnlyList<Swatch> swatches)
    {
        var sb = new StringBuilder(":root {\n");
        var used = new HashSet<string>();
        foreach (var s in swatches)
        {
            var slug = Slug(s.Name);
            var name = slug;
            for (int n = 2; !used.Add(name); n++)
                name = $"{slug}-{n}";
            sb.Append(CultureInfo.InvariantCulture, $"  --{name}: {s.Hex.ToLowerInvariant()};\n");
        }
        return sb.Append("}\n").ToString();
    }

    public static string ToJson(IReadOnlyList<Swatch> swatches) =>
        JsonSerializer.Serialize(swatches.Select(s => new { name = s.Name, hex = s.Hex, model = s.Model, values = s.Values, group = s.Group, spot = s.Spot }),
            new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

    /// <summary>A GIMP (and Inkscape, Krita) palette.</summary>
    public static string ToGpl(IReadOnlyList<Swatch> swatches, string title)
    {
        var sb = new StringBuilder($"GIMP Palette\nName: {title}\nColumns: 8\n#\n");
        foreach (var s in swatches)
            sb.Append(CultureInfo.InvariantCulture, $"{s.Rgb.R,3} {s.Rgb.G,3} {s.Rgb.B,3}\t{s.Name}\n");
        return sb.ToString();
    }

    private static string Slug(string name)
    {
        var sb = new StringBuilder();
        foreach (char c in name.ToLowerInvariant())
            sb.Append(char.IsLetterOrDigit(c) ? c : '-');
        var slug = string.Join('-', sb.ToString().Split('-', StringSplitOptions.RemoveEmptyEntries));
        return slug.Length == 0 ? "colour" : char.IsDigit(slug[0]) ? "c-" + slug : slug;
    }

    /// <summary>A picture of the swatches: a grid of colour chips, each with its name and hex value.</summary>
    public static void WriteSheet(IReadOnlyList<Swatch> swatches, string path)
    {
        const int chip = 160, label = 54, gap = 16, columns = 6;
        int count = Math.Max(1, swatches.Count);
        int cols = Math.Min(columns, count), rows = (count + cols - 1) / cols;
        int width = gap + cols * (chip + gap), height = gap + rows * (chip + label + gap);
        using var sheet = new MagickImage(MagickColors.White, (uint)width, (uint)height);
        var font = FontFor(swatches.Select(s => s.Name));
        for (int i = 0; i < swatches.Count; i++)
        {
            var s = swatches[i];
            int x = gap + i % cols * (chip + gap), y = gap + i / cols * (chip + label + gap);
            var (r, g, b) = s.Rgb;
            new Drawables()
                .FillColor(new MagickColor(r, g, b))
                .StrokeColor(new MagickColor(0, 0, 0, 40))
                .StrokeWidth(1)
                .RoundRectangle(x, y, x + chip, y + chip, 12, 12)
                .Draw(sheet);
            var name = s.Name.Length > 22 ? s.Name[..21] + "…" : s.Name;
            new Drawables()
                .Font(font)
                .FontPointSize(15)
                .FillColor(new MagickColor(30, 30, 30))
                .TextAlignment(TextAlignment.Left)
                .Text(x + 2, y + chip + 22, name)
                .FontPointSize(13)
                .FillColor(new MagickColor(110, 110, 110))
                .Text(x + 2, y + chip + 42, s.Hex)
                .Draw(sheet);
        }
        sheet.Write(path, MagickFormat.Png);
    }

    /// <summary>A Windows font that has the names' script (Arabic names need one with Arabic letters).</summary>
    private static string FontFor(IEnumerable<string> names)
    {
        var fonts = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts");
        foreach (var file in new[] { "segoeui.ttf", "tahoma.ttf", "arial.ttf" })
        {
            var path = Path.Combine(fonts, file);
            if (File.Exists(path))
                return path;
        }
        return "Arial";
    }

    // ─────────────── Extraction ───────────────

    /// <summary>
    /// The main colours of a picture, most used first: k-means in Lab space on a small copy,
    /// then near-identical colours merged. Transparent pixels don't count.
    /// </summary>
    public static IReadOnlyList<Swatch> Extract(string picture, int count = 8)
    {
        using var image = ImageLoader.Load(picture, new LoadOptions { DecodeSizeHint = 400, AllowEpsPreview = true, DrawingDpi = 72 });
        return Extract(image, count);
    }

    internal static IReadOnlyList<Swatch> Extract(MagickImage source, int count)
    {
        using var image = (MagickImage)source.Clone();
        image.FilterType = FilterType.Box;
        image.Resize(new MagickGeometry(120, 120) { Greater = true });
        var bytes = image.GetPixels().ToByteArray(PixelMapping.RGBA)!;
        var points = new List<(double L, double A, double B, byte R, byte G, byte Bl)>();
        for (int i = 0; i + 3 < bytes.Length; i += 4)
        {
            if (bytes[i + 3] < 128)
                continue;
            var (l, a, b) = SrgbToLab(bytes[i], bytes[i + 1], bytes[i + 2]);
            points.Add((l, a, b, bytes[i], bytes[i + 1], bytes[i + 2]));
        }
        if (points.Count == 0)
            return [];
        int k = Math.Min(count, points.Count);
        // k-means++ start, made repeatable: the first centre is the colour nearest the average.
        var centres = new List<(double L, double A, double B)>();
        var mean = (points.Average(p => p.L), points.Average(p => p.A), points.Average(p => p.B));
        centres.Add(points.MinBy(p => Distance((p.L, p.A, p.B), mean)) is var first ? (first.L, first.A, first.B) : mean);
        while (centres.Count < k)
        {
            var far = points.MaxBy(p => centres.Min(c => Distance((p.L, p.A, p.B), c)));
            centres.Add((far.L, far.A, far.B));
        }
        var assignment = new int[points.Count];
        for (int iteration = 0; iteration < 20; iteration++)
        {
            bool moved = false;
            for (int i = 0; i < points.Count; i++)
            {
                int best = 0;
                double bestDistance = double.MaxValue;
                for (int c = 0; c < centres.Count; c++)
                {
                    double d = Distance((points[i].L, points[i].A, points[i].B), centres[c]);
                    if (d < bestDistance)
                    {
                        bestDistance = d;
                        best = c;
                    }
                }
                if (assignment[i] != best)
                {
                    assignment[i] = best;
                    moved = true;
                }
            }
            for (int c = 0; c < centres.Count; c++)
            {
                var members = Enumerable.Range(0, points.Count).Where(i => assignment[i] == c).ToList();
                if (members.Count > 0)
                    centres[c] = (members.Average(i => points[i].L), members.Average(i => points[i].A), members.Average(i => points[i].B));
            }
            if (!moved && iteration > 0)
                break;
        }
        // Each colour is the average of its pixels (in sRGB), weighted by how many there are.
        var clusters = Enumerable.Range(0, centres.Count)
            .Select(c => (Members: Enumerable.Range(0, points.Count).Where(i => assignment[i] == c).ToList(), Centre: centres[c]))
            .Where(c => c.Members.Count > 0)
            .OrderByDescending(c => c.Members.Count)
            .ToList();
        var merged = new List<(double L, double A, double B, int Weight, double R, double G, double Bl)>();
        foreach (var cluster in clusters)
        {
            double r = cluster.Members.Average(i => (double)points[i].R), g = cluster.Members.Average(i => (double)points[i].G), b = cluster.Members.Average(i => (double)points[i].Bl);
            int near = merged.FindIndex(m => Distance((m.L, m.A, m.B), cluster.Centre) < 6);
            if (near >= 0)
            {
                var m = merged[near];
                int w = m.Weight + cluster.Members.Count;
                merged[near] = (m.L, m.A, m.B, w, (m.R * m.Weight + r * cluster.Members.Count) / w, (m.G * m.Weight + g * cluster.Members.Count) / w, (m.Bl * m.Weight + b * cluster.Members.Count) / w);
            }
            else
            {
                merged.Add((cluster.Centre.L, cluster.Centre.A, cluster.Centre.B, cluster.Members.Count, r, g, b));
            }
        }
        return merged.OrderByDescending(m => m.Weight).Select(m =>
        {
            var (r, g, b) = ((byte)Math.Round(m.R), (byte)Math.Round(m.G), (byte)Math.Round(m.Bl));
            return Swatch.FromRgb($"#{r:X2}{g:X2}{b:X2}", r, g, b);
        }).ToList();
    }

    private static double Distance((double L, double A, double B) p, (double L, double A, double B) q) =>
        Math.Sqrt((p.L - q.L) * (p.L - q.L) + (p.A - q.A) * (p.A - q.A) + (p.B - q.B) * (p.B - q.B));

    internal static (double L, double A, double B) SrgbToLab(byte r8, byte g8, byte b8)
    {
        static double Linear(byte c) => c / 255.0 is var v && v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        double r = Linear(r8), g = Linear(g8), b = Linear(b8);
        // sRGB (D65) → XYZ → Lab (D65 white; only distances matter here).
        double x = (0.4124 * r + 0.3576 * g + 0.1805 * b) / 0.95047, y = 0.2126 * r + 0.7152 * g + 0.0722 * b, z = (0.0193 * r + 0.1192 * g + 0.9505 * b) / 1.08883;
        static double F(double t) => t > 0.008856 ? Math.Cbrt(t) : 7.787 * t + 16.0 / 116;
        double fx = F(x), fy = F(y), fz = F(z);
        return (116 * fy - 16, 500 * (fx - fy), 200 * (fy - fz));
    }

    private static void WriteBig(Stream s, ushort value)
    {
        Span<byte> b = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(b, value);
        s.Write(b);
    }

    private static void WriteBig(Stream s, uint value)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(b, value);
        s.Write(b);
    }

    private static void WriteBig(Stream s, float value)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteSingleBigEndian(b, value);
        s.Write(b);
    }
}
