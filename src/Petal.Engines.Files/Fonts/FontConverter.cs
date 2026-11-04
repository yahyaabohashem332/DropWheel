using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Petal.Core.Formats;
using Petal.Core.Jobs;
using F = Petal.Core.Formats.Formats;

namespace Petal.Engines.Files.Fonts;

/// <summary>
/// Converts fonts between TTF, OTF, WOFF and WOFF2. Between the web's packings and their source nothing of the font
/// changes. Between TTF and OTF the outlines themselves are rewritten, from quadratic curves to cubic ones (exactly) or back
/// (within half a unit), and hinting is left out, since it doesn't carry over; letters, spacing, kerning and shaping stay.
/// </summary>
public sealed class FontConverter : IConverter
{
    /// <summary>How far a quadratic curve may stray from the cubic it replaces, in thousandths of the font's height.</summary>
    private const double Tolerance = 0.5;

    /// <summary>Tables that belong to the outlines they were made for, or to variations a converted font no longer has.</summary>
    private static readonly string[] OutlineTables =
        ["glyf", "loca", "cvt ", "fpgm", "prep", "hdmx", "LTSH", "VDMX", "gasp", "gvar", "fvar", "avar", "cvar", "HVAR", "VVAR", "MVAR", "STAT", "DSIG", "CFF ", "CFF2", "VORG"];

    public string Name => "Fonts";

    public bool CanConvert(FileFormat from, FileFormat to) =>
        from.Family == FormatFamily.Font && to.Family == FormatFamily.Font && from != to;

    public Task<JobResult> ConvertAsync(ConversionJob job, IProgress<double>? progress, CancellationToken cancellationToken) =>
        Task.Run(async () =>
        {
            var bytes = await File.ReadAllBytesAsync(job.InputPath, cancellationToken).ConfigureAwait(false);
            progress?.Report(0.1);
            var output = Convert(bytes, job.To);
            cancellationToken.ThrowIfCancellationRequested();
            var temporary = job.OutputPath + ".petal-tmp";
            await File.WriteAllBytesAsync(temporary, output, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, job.OutputPath, true);
            progress?.Report(1);
            return JobResult.Single(job.OutputPath);
        }, cancellationToken);

    /// <summary>A font file in another format.</summary>
    internal static byte[] Convert(byte[] input, FileFormat to)
    {
        var font = SfntFont.Read(input);
        if (to == F.Ttf)
            return (font.HasCffOutlines || font.Tables.ContainsKey("CFF2") ? ToTrueType(font) : font).ToSfnt();
        if (to == F.Otf)
            return (font.HasGlyfOutlines ? ToCff(font) : font).ToSfnt();
        if (to == F.Woff)
            return font.ToWoff();
        if (to == F.Woff2)
            return Woff2.Write(font);
        throw new NotSupportedException($"DropWheel can't write {to.Label} fonts.");
    }

    // ---- TrueType to CFF.

    internal static SfntFont ToCff(SfntFont source)
    {
        var glyphs = GlyfTable.Read(source);
        var advance = Advances(source);
        var names = GlyphNames(source, glyphs.Length);
        var strings = new List<byte[]>(glyphs.Length);
        for (int g = 0; g < glyphs.Length; g++)
            strings.Add(CffTable.CharStringOf(Cubic(glyphs[g]), g < advance.Length ? advance[g] : 0));

        var head = source.Table("head");
        var box = (BinaryPrimitives.ReadInt16BigEndian(head.AsSpan(36)), BinaryPrimitives.ReadInt16BigEndian(head.AsSpan(38)),
            BinaryPrimitives.ReadInt16BigEndian(head.AsSpan(40)), BinaryPrimitives.ReadInt16BigEndian(head.AsSpan(42)));
        var result = new SfntFont { Flavor = SfntFont.Cff };
        foreach (var (tag, table) in source.Tables)
            if (!OutlineTables.Contains(tag))
                result.Tables[tag] = table;
        result.Tables["CFF "] = CffTable.Write(PostScriptName(source), source.UnitsPerEm, box, names, strings);
        var maxp = new byte[6];
        BinaryPrimitives.WriteUInt32BigEndian(maxp, 0x00005000);
        BinaryPrimitives.WriteUInt16BigEndian(maxp.AsSpan(4), (ushort)glyphs.Length);
        result.Tables["maxp"] = maxp;
        ShortPost(result);
        return result;
    }

    /// <summary>A "post" table without names: the outlines' own table or nobody needs them.</summary>
    private static void ShortPost(SfntFont font)
    {
        var post = new byte[32];
        if (font.Tables.TryGetValue("post", out var old))
            old.AsSpan(0, Math.Min(32, old.Length)).CopyTo(post);
        BinaryPrimitives.WriteUInt32BigEndian(post, 0x00030000);
        font.Tables["post"] = post;
    }

    private static int[] Advances(SfntFont font)
    {
        int glyphs = font.GlyphCount;
        var result = new int[glyphs];
        var hmtx = font.Table("hmtx");
        int metrics = BinaryPrimitives.ReadUInt16BigEndian(font.Table("hhea").AsSpan(34));
        for (int g = 0, last = 0; g < glyphs; g++)
        {
            if (g < metrics && g * 4 + 2 <= hmtx.Length)
                last = BinaryPrimitives.ReadUInt16BigEndian(hmtx.AsSpan(g * 4));
            result[g] = last;
        }
        return result;
    }

    /// <summary>Names as Adobe's convention builds them from the characters the glyphs show, which is what they are for.</summary>
    private static List<string> GlyphNames(SfntFont font, int glyphs)
    {
        var characters = font.CharacterOfGlyph();
        var names = new List<string>(glyphs) { ".notdef" };
        for (int g = 1; g < glyphs; g++)
            names.Add(!characters.TryGetValue(g, out int c) ? "glyph" + g.ToString(CultureInfo.InvariantCulture)
                : c <= 0xFFFF ? "uni" + c.ToString("X4", CultureInfo.InvariantCulture)
                : "u" + c.ToString("X", CultureInfo.InvariantCulture));
        return names;
    }

    private static string PostScriptName(SfntFont font)
    {
        var name = font.NameOf(6) ?? font.NameOf(4) ?? "Font";
        var clean = new StringBuilder();
        foreach (char c in name)
            if (c is > ' ' and < (char)127 and not ('[' or ']' or '(' or ')' or '{' or '}' or '<' or '>' or '/' or '%'))
                clean.Append(c);
        return clean.Length == 0 ? "Font" : clean.ToString();
    }

    /// <summary>
    /// Quadratic contours as the cubic paths that draw exactly the same; the direction turns round, since TrueType
    /// fills to the right of a contour and CFF to the left.
    /// </summary>
    internal static List<PathStep> Cubic(IReadOnlyList<List<OutlinePoint>> contours)
    {
        var path = new List<PathStep>();
        foreach (var contour in contours)
        {
            if (contour.Count < 2)
                continue;
            var points = new List<OutlinePoint>(contour);
            points.Reverse();
            // Start on the curve; when no point is, halfway between the first two is.
            int first = points.FindIndex(p => p.On);
            OutlinePoint start;
            List<OutlinePoint> order;
            if (first >= 0)
            {
                start = points[first];
                order = [.. points.Skip(first + 1), .. points.Take(first + 1)];
            }
            else
            {
                start = new OutlinePoint((points[^1].X + points[0].X) / 2, (points[^1].Y + points[0].Y) / 2, true);
                order = [.. points, start];
            }
            path.Add(new PathStep('M', 0, 0, 0, 0, start.X, start.Y));
            var at = start;
            OutlinePoint? control = null;
            void To(OutlinePoint end)
            {
                if (control is { } q)
                    path.Add(new PathStep('C', at.X + 2.0 / 3 * (q.X - at.X), at.Y + 2.0 / 3 * (q.Y - at.Y), end.X + 2.0 / 3 * (q.X - end.X), end.Y + 2.0 / 3 * (q.Y - end.Y), end.X, end.Y));
                else
                    path.Add(new PathStep('L', 0, 0, 0, 0, end.X, end.Y));
                at = end;
                control = null;
            }
            foreach (var p in order)
            {
                if (p.On)
                    To(p);
                else
                {
                    if (control is { } c)
                        To(new OutlinePoint((c.X + p.X) / 2, (c.Y + p.Y) / 2, true));
                    control = p;
                }
            }
            if (control is not null)
                To(start);
            // A closing line back to the start is what closing does anyway.
            if (path[^1] is { Kind: 'L' } last && last.X == start.X && last.Y == start.Y)
                path.RemoveAt(path.Count - 1);
        }
        return path;
    }

    // ---- CFF to TrueType.

    internal static SfntFont ToTrueType(SfntFont source)
    {
        if (!source.HasCffOutlines)
            throw new InvalidDataException("This font uses CFF2 variable outlines, which DropWheel can't convert.");
        var paths = CffTable.Read(source.Table("CFF "), source.UnitsPerEm);
        double tolerance = Tolerance * source.UnitsPerEm / 1000.0;
        var glyphs = paths.Select(p => Quadratic(p, tolerance)).ToList();
        var result = new SfntFont { Flavor = SfntFont.TrueType };
        foreach (var (tag, table) in source.Tables)
            if (!OutlineTables.Contains(tag))
                result.Tables[tag] = table;
        GlyfTable.Write(result, glyphs);
        ShortPost(result);
        return result;
    }

    /// <summary>Cubic paths as quadratic contours, a curve split until a single quadratic comes close enough; the direction turns round.</summary>
    internal static List<List<OutlinePoint>> Quadratic(IReadOnlyList<PathStep> path, double tolerance)
    {
        var contours = new List<List<OutlinePoint>>();
        List<OutlinePoint>? contour = null;
        double x = 0, y = 0;
        foreach (var step in path)
        {
            switch (step.Kind)
            {
                case 'M':
                    if (contour is not null)
                        contours.Add(Finish(contour));
                    contour = [new OutlinePoint(step.X, step.Y, true)];
                    break;
                case 'L':
                    contour?.Add(new OutlinePoint(step.X, step.Y, true));
                    break;
                default:
                    if (contour is not null)
                        Split(contour, x, y, step.C1X, step.C1Y, step.C2X, step.C2Y, step.X, step.Y, tolerance, 0);
                    break;
            }
            (x, y) = (step.X, step.Y);
        }
        if (contour is not null)
            contours.Add(Finish(contour));
        return contours;
    }

    private static List<OutlinePoint> Finish(List<OutlinePoint> contour)
    {
        // The last point repeating the first adds nothing; rounded points that repeat add nothing either.
        var rounded = contour.Select(p => new OutlinePoint(Math.Round(p.X), Math.Round(p.Y), p.On)).ToList();
        var result = new List<OutlinePoint>(rounded.Count);
        foreach (var p in rounded)
            if (result.Count == 0 || result[^1] != p)
                result.Add(p);
        if (result.Count > 1 && result[^1] == result[0])
            result.RemoveAt(result.Count - 1);
        result.Reverse();
        return result;
    }

    private static void Split(List<OutlinePoint> to, double x0, double y0, double x1, double y1, double x2, double y2, double x3, double y3, double tolerance, int depth)
    {
        // How far the best single quadratic can be from this cubic: √3/36 of the third difference's length.
        double ex = x3 - 3 * x2 + 3 * x1 - x0, ey = y3 - 3 * y2 + 3 * y1 - y0;
        double error = Math.Sqrt(ex * ex + ey * ey) * Math.Sqrt(3) / 36;
        if (error <= tolerance || depth >= 8)
        {
            to.Add(new OutlinePoint((3 * x1 - x0 + 3 * x2 - x3) / 4, (3 * y1 - y0 + 3 * y2 - y3) / 4, false));
            to.Add(new OutlinePoint(x3, y3, true));
            return;
        }
        double ax = (x0 + x1) / 2, ay = (y0 + y1) / 2, bx = (x1 + x2) / 2, by = (y1 + y2) / 2, cx = (x2 + x3) / 2, cy = (y2 + y3) / 2;
        double dx = (ax + bx) / 2, dy = (ay + by) / 2, fx = (bx + cx) / 2, fy = (by + cy) / 2, mx = (dx + fx) / 2, my = (dy + fy) / 2;
        Split(to, x0, y0, ax, ay, dx, dy, mx, my, tolerance, depth + 1);
        Split(to, mx, my, fx, fy, cx, cy, x3, y3, tolerance, depth + 1);
    }
}
