using System.Globalization;
using System.Text;
using ImageMagick;

namespace Petal.Engines.Imaging;

/// <summary>How a picture is traced.</summary>
/// <param name="Colours">How many flat colours; 0 finds how many it takes.</param>
/// <param name="Smoothness">How far a curve may stray from the edge, in pixels of the picture.</param>
/// <param name="DropBackground">The colour around the picture's edge becomes transparent.</param>
public sealed record TraceOptions(int Colours = 0, double Smoothness = 0.6, bool DropBackground = false);

/// <summary>What came out, to say so.</summary>
public sealed record TraceResult(string Svg, int Colours, int Shapes);

/// <summary>
/// Turns a picture into an SVG: a logo, an icon, a signature, a flat drawing. The picture comes down to a few flat colours;
/// each colour's outline is followed around its pixels, its stair-steps smoothed, and straight runs and cubic curves fitted
/// between its corners. Each layer also covers those above it, so no hairline gaps show between colours.
/// Written here rather than taken from a tracing library: the well-known one, potrace, is under the GPL.
/// </summary>
public static class ImageTracer
{
    /// <summary>Above this a picture is reduced, as a logo gains nothing by more pixels and tracing takes much longer.</summary>
    private const int LargestSide = 1600;

    /// <summary>Below this a picture is enlarged first, so its soft edges tell where the true edge lies between pixels.</summary>
    private const int SmallestSide = 700;

    /// <summary>Patches smaller than this share of the picture are specks, and take the colour around them.</summary>
    private const double SpeckShare = 0.00004;

    public static Task<TraceResult> TraceFileAsync(string input, TraceOptions? options = null, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            using var image = ImageLoader.Load(input);
            image.AutoOrient();
            return Trace(image, options ?? new TraceOptions(), cancellationToken);
        }, cancellationToken);

    public static TraceResult Trace(MagickImage source, TraceOptions options, CancellationToken cancellationToken = default)
    {
        int originalWidth = (int)source.Width, originalHeight = (int)source.Height;
        using var image = (MagickImage)source.Clone();
        image.ColorSpace = ColorSpace.sRGB;
        double scale = 1;
        int longest = Math.Max(originalWidth, originalHeight);
        if (longest > LargestSide)
            scale = (double)LargestSide / longest;
        else if (longest < SmallestSide)
            scale = Math.Min(4, Math.Ceiling((double)SmallestSide / longest));
        if (scale != 1)
        {
            image.FilterType = scale > 1 ? FilterType.Mitchell : FilterType.Lanczos;
            image.Resize(new MagickGeometry((uint)Math.Round(originalWidth * scale), (uint)Math.Round(originalHeight * scale)) { IgnoreAspectRatio = true });
        }
        int width = (int)image.Width, height = (int)image.Height;
        var rgba = image.GetPixels().ToByteArray(PixelMapping.RGBA) ?? throw new InvalidOperationException("The picture has no pixels.");
        cancellationToken.ThrowIfCancellationRequested();

        var palette = Palette(rgba, options.Colours);
        var labels = Assign(rgba, palette);
        RemoveSpecks(labels, width, height, palette.Count, (int)Math.Max(4, width * height * SpeckShare));
        cancellationToken.ThrowIfCancellationRequested();

        var area = new long[palette.Count];
        foreach (var label in labels)
            if (label >= 0)
                area[label]++;
        int dropped = options.DropBackground ? BorderColour(labels, width, height, palette.Count) : -1;
        // Largest underneath: smaller shapes lie on top, and each layer also covers what lies above it.
        var order = Enumerable.Range(0, palette.Count).Where(i => area[i] > 0).OrderByDescending(i => i == dropped ? long.MaxValue : area[i]).ToArray();
        var rank = new int[palette.Count];
        for (int i = 0; i < order.Length; i++)
            rank[order[i]] = i;

        var sb = new StringBuilder();
        string Number(double v) => Math.Round(v, 2).ToString("0.##", CultureInfo.InvariantCulture);
        sb.Append($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{originalWidth}\" height=\"{originalHeight}\" viewBox=\"0 0 {originalWidth} {originalHeight}\">\n");
        int shapes = 0, colours = 0;
        double tolerance = Math.Max(0.1, options.Smoothness) * scale;
        for (int r = 0; r < order.Length; r++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int colour = order[r];
            if (colour == dropped)
                continue;
            var mask = new bool[width * height];
            for (int i = 0; i < mask.Length; i++)
                mask[i] = labels[i] >= 0 && labels[i] != dropped && rank[labels[i]] >= r;
            var loops = Outlines(mask, width, height);
            if (loops.Count == 0)
                continue;
            var path = new StringBuilder();
            foreach (var loop in loops)
                AppendLoop(path, loop, tolerance, 1 / scale, Number);
            var (red, green, blue) = palette[colour];
            sb.Append($"  <path fill=\"#{red:x2}{green:x2}{blue:x2}\" fill-rule=\"evenodd\" d=\"{path}\"/>\n");
            shapes += loops.Count;
            colours++;
        }
        sb.Append("</svg>\n");
        return new TraceResult(sb.ToString(), colours, shapes);
    }

    // ---- Colours.

    private static (double L, double A, double B) Lab(int r, int g, int b)
    {
        static double Linear(int v)
        {
            double c = v / 255.0;
            return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }
        double rl = Linear(r), gl = Linear(g), bl = Linear(b);
        double x = (0.4124 * rl + 0.3576 * gl + 0.1805 * bl) / 0.95047, y = 0.2126 * rl + 0.7152 * gl + 0.0722 * bl, z = (0.0193 * rl + 0.1192 * gl + 0.9505 * bl) / 1.08883;
        static double F(double t) => t > 0.008856 ? Math.Cbrt(t) : 7.787 * t + 16.0 / 116;
        return (116 * F(y) - 16, 500 * (F(x) - F(y)), 200 * (F(y) - F(z)));
    }

    /// <summary>The colours of the picture, found by k-means in a colour space that matches the eye; with no count given, as few as stay close to it.</summary>
    internal static List<(byte R, byte G, byte B)> Palette(byte[] rgba, int wanted)
    {
        var samples = new List<(double L, double A, double B, byte R, byte G, byte Bl)>();
        int count = rgba.Length / 4, step = Math.Max(1, count / 40000);
        for (int i = 0; i < count; i += step)
            if (rgba[i * 4 + 3] >= 128)
            {
                var (l, a, b) = Lab(rgba[i * 4], rgba[i * 4 + 1], rgba[i * 4 + 2]);
                samples.Add((l, a, b, rgba[i * 4], rgba[i * 4 + 1], rgba[i * 4 + 2]));
            }
        if (samples.Count == 0)
            return [(0, 0, 0)];
        if (wanted > 0)
            return KMeans(samples, wanted).Palette;
        List<(byte, byte, byte)> best = [];
        double before = double.MaxValue;
        for (int k = 2; k <= 16; k++)
        {
            var (palette, error) = KMeans(samples, k);
            best = palette;
            // Close enough to the picture, or a further colour barely helps: soft edges would only become rings of their own.
            if (error < 2.2 || before - error < before * 0.12 && k > 2 && error < 7)
                break;
            before = error;
        }
        return best;
    }

    private static (List<(byte R, byte G, byte B)> Palette, double Error) KMeans(List<(double L, double A, double B, byte R, byte G, byte Bl)> samples, int k)
    {
        // Spread out from the first: each next centre is the sample furthest from those chosen, so small bright marks get a colour too.
        var centres = new List<(double L, double A, double B)> { (samples[0].L, samples[0].A, samples[0].B) };
        var nearest = samples.Select(s => Far(s, centres[0])).ToArray();
        while (centres.Count < k)
        {
            int at = Array.IndexOf(nearest, nearest.Max());
            if (nearest[at] <= 0)
                break;
            centres.Add((samples[at].L, samples[at].A, samples[at].B));
            for (int i = 0; i < samples.Count; i++)
                nearest[i] = Math.Min(nearest[i], Far(samples[i], centres[^1]));
        }
        var belongs = new int[samples.Count];
        for (int round = 0; round < 12; round++)
        {
            var sum = new double[centres.Count, 3];
            var size = new int[centres.Count];
            for (int i = 0; i < samples.Count; i++)
            {
                int best = 0;
                double bestDistance = double.MaxValue;
                for (int c = 0; c < centres.Count; c++)
                {
                    double d = Far(samples[i], centres[c]);
                    if (d < bestDistance)
                        (best, bestDistance) = (c, d);
                }
                belongs[i] = best;
                sum[best, 0] += samples[i].L;
                sum[best, 1] += samples[i].A;
                sum[best, 2] += samples[i].B;
                size[best]++;
            }
            for (int c = 0; c < centres.Count; c++)
                if (size[c] > 0)
                    centres[c] = (sum[c, 0] / size[c], sum[c, 1] / size[c], sum[c, 2] / size[c]);
        }
        // Each colour is the middle one of what it holds, the middle being a colour really in the picture: soft edges don't dull it.
        double error = 0;
        var palette = new List<(byte, byte, byte)>();
        for (int c = 0; c < centres.Count; c++)
        {
            var members = Enumerable.Range(0, samples.Count).Where(i => belongs[i] == c).Select(i => samples[i]).ToList();
            if (members.Count == 0)
                continue;
            var r = members.Select(m => m.R).Order().ToList();
            var g = members.Select(m => m.G).Order().ToList();
            var b = members.Select(m => m.Bl).Order().ToList();
            palette.Add((r[r.Count / 2], g[g.Count / 2], b[b.Count / 2]));
            error += members.Sum(m => Math.Sqrt(Far(m, centres[c])));
        }
        return (palette, error / samples.Count);
    }

    private static double Far((double L, double A, double B, byte R, byte G, byte Bl) s, (double L, double A, double B) c) =>
        (s.L - c.L) * (s.L - c.L) + (s.A - c.A) * (s.A - c.A) + (s.B - c.B) * (s.B - c.B);

    private static int[] Assign(byte[] rgba, List<(byte R, byte G, byte B)> palette)
    {
        var labs = palette.Select(p => Lab(p.R, p.G, p.B)).ToArray();
        var labels = new int[rgba.Length / 4];
        var known = new Dictionary<int, int>();
        for (int i = 0; i < labels.Length; i++)
        {
            if (rgba[i * 4 + 3] < 128)
            {
                labels[i] = -1;
                continue;
            }
            int key = rgba[i * 4] << 16 | rgba[i * 4 + 1] << 8 | rgba[i * 4 + 2];
            if (!known.TryGetValue(key, out int label))
            {
                var (l, a, b) = Lab(rgba[i * 4], rgba[i * 4 + 1], rgba[i * 4 + 2]);
                double best = double.MaxValue;
                for (int c = 0; c < labs.Length; c++)
                {
                    double d = (l - labs[c].L) * (l - labs[c].L) + (a - labs[c].A) * (a - labs[c].A) + (b - labs[c].B) * (b - labs[c].B);
                    if (d < best)
                        (best, label) = (d, c);
                }
                known[key] = label;
            }
            labels[i] = label;
        }
        return labels;
    }

    /// <summary>Small patches take the colour most of what surrounds them has.</summary>
    internal static void RemoveSpecks(int[] labels, int width, int height, int colours, int smallest)
    {
        var seen = new bool[labels.Length];
        var stack = new Stack<int>();
        var patch = new List<int>();
        var around = new int[colours + 1];
        for (int start = 0; start < labels.Length; start++)
        {
            if (seen[start])
                continue;
            int label = labels[start];
            patch.Clear();
            Array.Clear(around);
            stack.Push(start);
            seen[start] = true;
            while (stack.Count > 0)
            {
                int p = stack.Pop();
                patch.Add(p);
                int x = p % width, y = p / width;
                void Visit(int q)
                {
                    if (labels[q] == label)
                    {
                        if (!seen[q])
                        {
                            seen[q] = true;
                            stack.Push(q);
                        }
                    }
                    else
                        around[labels[q] + 1]++;
                }
                if (x > 0)
                    Visit(p - 1);
                if (x < width - 1)
                    Visit(p + 1);
                if (y > 0)
                    Visit(p - width);
                if (y < height - 1)
                    Visit(p + width);
            }
            if (patch.Count >= smallest || label < 0)
                continue;
            int best = Array.IndexOf(around, around.Max());
            if (around[best] == 0)
                continue;
            foreach (int p in patch)
                labels[p] = best - 1;
        }
    }

    /// <summary>The colour that holds most of the picture's edge.</summary>
    private static int BorderColour(int[] labels, int width, int height, int colours)
    {
        var count = new int[colours];
        void Add(int p)
        {
            if (labels[p] >= 0)
                count[labels[p]]++;
        }
        for (int x = 0; x < width; x++)
        {
            Add(x);
            Add((height - 1) * width + x);
        }
        for (int y = 0; y < height; y++)
        {
            Add(y * width);
            Add(y * width + width - 1);
        }
        return count.Max() == 0 ? -1 : Array.IndexOf(count, count.Max());
    }

    // ---- Outlines.

    /// <summary>The closed loops around a mask, along the edges between its pixels, each from pixel corner to pixel corner.</summary>
    internal static List<List<(int X, int Y)>> Outlines(bool[] mask, int width, int height)
    {
        bool In(int x, int y) => x >= 0 && y >= 0 && x < width && y < height && mask[y * width + x];
        // Each pixel edge with the mask on its right, going around clockwise on screen: from corner to corner.
        var next = new Dictionary<long, List<(int X, int Y)>>();
        long Key(int x, int y) => (long)y * (width + 2) + x;
        void Edge(int x0, int y0, int x1, int y1)
        {
            if (!next.TryGetValue(Key(x0, y0), out var list))
                next[Key(x0, y0)] = list = new List<(int, int)>(1);
            list.Add((x1, y1));
        }
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                if (!In(x, y))
                    continue;
                if (!In(x, y - 1))
                    Edge(x, y, x + 1, y);
                if (!In(x + 1, y))
                    Edge(x + 1, y, x + 1, y + 1);
                if (!In(x, y + 1))
                    Edge(x + 1, y + 1, x, y + 1);
                if (!In(x - 1, y))
                    Edge(x, y + 1, x, y);
            }
        var loops = new List<List<(int, int)>>();
        while (next.Count > 0)
        {
            long startKey = next.Keys.First();
            int sx = (int)(startKey % (width + 2)), sy = (int)(startKey / (width + 2));
            var loop = new List<(int, int)> { (sx, sy) };
            var (x, y) = (sx, sy);
            int dx = 0, dy = 0;
            while (true)
            {
                var options = next[Key(x, y)];
                // Where two corners touch, keep turning the same way, so the loop stays around one shape.
                int pick = 0;
                if (options.Count > 1)
                    for (int i = 0; i < options.Count; i++)
                        if ((options[i].X - x) * dy - (options[i].Y - y) * dx < 0)
                            pick = i;
                var (nx, ny) = options[pick];
                options.RemoveAt(pick);
                if (options.Count == 0)
                    next.Remove(Key(x, y));
                (dx, dy) = (nx - x, ny - y);
                (x, y) = (nx, ny);
                if (x == sx && y == sy)
                    break;
                loop.Add((x, y));
            }
            if (loop.Count >= 4)
                loops.Add(loop);
        }
        return loops;
    }

    private readonly record struct Pt(double X, double Y)
    {
        public static Pt operator +(Pt a, Pt b) => new(a.X + b.X, a.Y + b.Y);

        public static Pt operator -(Pt a, Pt b) => new(a.X - b.X, a.Y - b.Y);

        public static Pt operator *(Pt a, double s) => new(a.X * s, a.Y * s);

        public double Dot(Pt b) => X * b.X + Y * b.Y;

        public double Length => Math.Sqrt(X * X + Y * Y);

        public Pt Unit => Length < 1e-9 ? new Pt(0, 0) : this * (1 / Length);
    }

    /// <summary>A loop as path commands: the stair-steps ironed out, corners kept sharp, straight and curved stretches fitted between them.</summary>
    private static void AppendLoop(StringBuilder path, List<(int X, int Y)> loop, double tolerance, double scale, Func<double, string> number)
    {
        // Between pixel corners lie the true edge's stair-steps: the middles of their treads lie on it.
        int n = loop.Count;
        var middle = new Pt[n];
        for (int i = 0; i < n; i++)
        {
            var (ax, ay) = loop[i];
            var (bx, by) = loop[(i + 1) % n];
            middle[i] = new Pt((ax + bx) / 2.0, (ay + by) / 2.0);
        }
        var corners = Corners(middle);
        string Out(Pt p) => number(p.X * scale) + " " + number(p.Y * scale);
        void Stretch(Pt[] part)
        {
            Fit(part, tolerance, curve =>
            {
                if (curve.Line)
                    path.Append('L').Append(Out(curve.P3));
                else
                    path.Append('C').Append(Out(curve.P1)).Append(' ').Append(Out(curve.P2)).Append(' ').Append(Out(curve.P3));
            });
        }
        if (corners.Count == 0)
        {
            // Round all the way: any place can start it.
            var round = new Pt[n + 1];
            Array.Copy(middle, round, n);
            round[n] = middle[0];
            path.Append('M').Append(Out(round[0]));
            Stretch(round);
            path.Append('Z');
            return;
        }
        path.Append('M').Append(Out(middle[corners[0]]));
        for (int c = 0; c < corners.Count; c++)
        {
            int from = corners[c], to = corners[(c + 1) % corners.Count];
            int length = ((to - from + n - 1) % n) + 1;
            var part = new Pt[length + 1];
            for (int i = 0; i <= length; i++)
                part[i] = middle[(from + i) % n];
            Stretch(part);
        }
        path.Append('Z');
    }

    /// <summary>Where the outline turns sharply: seen from a few steps either way, by more than a curve would over that distance.</summary>
    private static List<int> Corners(Pt[] p)
    {
        int n = p.Length;
        var corners = new List<int>();
        if (n < 8)
            return Enumerable.Range(0, n).ToList();
        const int Reach = 4;
        var turn = new double[n];
        for (int i = 0; i < n; i++)
        {
            var a = (p[i] - p[(i - Reach + n) % n]).Unit;
            var b = (p[(i + Reach) % n] - p[i]).Unit;
            turn[i] = Math.Acos(Math.Clamp(a.Dot(b), -1, 1));
        }
        const double Sharp = 0.75;
        for (int i = 0; i < n; i++)
        {
            if (turn[i] < Sharp)
                continue;
            bool most = true;
            for (int d = 1; d <= Reach && most; d++)
                most = turn[i] >= turn[(i + d) % n] && turn[i] > turn[(i - d + n) % n];
            if (most)
                corners.Add(i);
        }
        return corners;
    }

    private readonly record struct Curve(bool Line, Pt P1, Pt P2, Pt P3);

    /// <summary>Fits a stretch with a straight line when it is one, otherwise with cubic curves, as Philip Schneider's method does.</summary>
    private static void Fit(Pt[] points, double tolerance, Action<Curve> emit)
    {
        var distinct = new List<Pt>(points.Length);
        foreach (var p in points)
            if (distinct.Count == 0 || (distinct[^1] - p).Length > 1e-6)
                distinct.Add(p);
        var d = distinct.ToArray();
        if (d.Length < 2)
            return;
        var startTangent = Tangent(d, 0, 1);
        var endTangent = Tangent(d, d.Length - 1, -1);
        FitRange(d, 0, d.Length - 1, startTangent, endTangent, tolerance, emit, 0);
    }

    private static Pt Tangent(Pt[] d, int at, int direction)
    {
        int other = Math.Clamp(at + direction * 2, 0, d.Length - 1);
        return (d[other] - d[at]).Unit;
    }

    private static void FitRange(Pt[] d, int first, int last, Pt t1, Pt t2, double tolerance, Action<Curve> emit, int depth)
    {
        // A straight line, when all of it lies close to one.
        var chord = d[last] - d[first];
        double chordLength = chord.Length;
        double worst = 0;
        if (chordLength > 1e-6)
        {
            var unit = chord * (1 / chordLength);
            for (int i = first + 1; i < last; i++)
            {
                var v = d[i] - d[first];
                worst = Math.Max(worst, Math.Abs(v.X * unit.Y - v.Y * unit.X));
            }
        }
        if (last - first == 1 || (chordLength > 1e-6 && worst <= Math.Min(tolerance, 0.55)))
        {
            emit(new Curve(true, d[first], d[last], d[last]));
            return;
        }
        var u = ChordLengths(d, first, last);
        var bez = Generate(d, first, last, u, t1, t2);
        var (error, split) = MaxError(d, first, last, bez, u);
        if (error <= tolerance * tolerance)
        {
            emit(new Curve(false, bez[1], bez[2], bez[3]));
            return;
        }
        if (error <= tolerance * tolerance * 16)
            for (int round = 0; round < 4; round++)
            {
                u = Reparameterize(d, first, last, u, bez);
                bez = Generate(d, first, last, u, t1, t2);
                (error, split) = MaxError(d, first, last, bez, u);
                if (error <= tolerance * tolerance)
                {
                    emit(new Curve(false, bez[1], bez[2], bez[3]));
                    return;
                }
            }
        split = Math.Clamp(split, first + 1, last - 1);
        var centre = (d[Math.Max(split - 2, first)] - d[Math.Min(split + 2, last)]).Unit;
        FitRange(d, first, split, t1, centre, tolerance, emit, depth + 1);
        FitRange(d, split, last, centre * -1, t2, tolerance, emit, depth + 1);
    }

    private static double[] ChordLengths(Pt[] d, int first, int last)
    {
        var u = new double[last - first + 1];
        for (int i = first + 1; i <= last; i++)
            u[i - first] = u[i - first - 1] + (d[i] - d[i - 1]).Length;
        double total = Math.Max(1e-9, u[^1]);
        for (int i = 0; i < u.Length; i++)
            u[i] /= total;
        return u;
    }

    private static Pt[] Generate(Pt[] d, int first, int last, double[] u, Pt t1, Pt t2)
    {
        double c00 = 0, c01 = 0, c11 = 0, x0 = 0, x1 = 0;
        Pt p0 = d[first], p3 = d[last];
        for (int i = 0; i < u.Length; i++)
        {
            double t = u[i], s = 1 - t;
            double b0 = s * s * s, b1 = 3 * t * s * s, b2 = 3 * t * t * s, b3 = t * t * t;
            var a1 = t1 * b1;
            var a2 = t2 * b2;
            c00 += a1.Dot(a1);
            c01 += a1.Dot(a2);
            c11 += a2.Dot(a2);
            var shortfall = d[first + i] - (p0 * (b0 + b1) + p3 * (b2 + b3));
            x0 += a1.Dot(shortfall);
            x1 += a2.Dot(shortfall);
        }
        double det = c00 * c11 - c01 * c01;
        double alpha1 = Math.Abs(det) < 1e-12 ? 0 : (x0 * c11 - x1 * c01) / det;
        double alpha2 = Math.Abs(det) < 1e-12 ? 0 : (c00 * x1 - c01 * x0) / det;
        double segment = (p3 - p0).Length;
        double small = 1e-6 * segment;
        if (alpha1 < small || alpha2 < small)
            alpha1 = alpha2 = segment / 3;
        return [p0, p0 + t1 * alpha1, p3 + t2 * alpha2, p3];
    }

    private static Pt Point(Pt[] b, double t)
    {
        double s = 1 - t;
        return b[0] * (s * s * s) + b[1] * (3 * s * s * t) + b[2] * (3 * s * t * t) + b[3] * (t * t * t);
    }

    private static (double Error, int Split) MaxError(Pt[] d, int first, int last, Pt[] bez, double[] u)
    {
        double worst = 0;
        int at = (first + last) / 2;
        for (int i = first + 1; i < last; i++)
        {
            var v = Point(bez, u[i - first]) - d[i];
            double e = v.Dot(v);
            if (e > worst)
                (worst, at) = (e, i);
        }
        return (worst, at);
    }

    private static double[] Reparameterize(Pt[] d, int first, int last, double[] u, Pt[] b)
    {
        var result = new double[u.Length];
        Pt[] q1 = [(b[1] - b[0]) * 3, (b[2] - b[1]) * 3, (b[3] - b[2]) * 3];
        Pt[] q2 = [(q1[1] - q1[0]) * 2, (q1[2] - q1[1]) * 2];
        for (int i = 0; i < u.Length; i++)
        {
            double t = u[i], s = 1 - t;
            var p = Point(b, t) - d[first + i];
            var d1 = q1[0] * (s * s) + q1[1] * (2 * s * t) + q1[2] * (t * t);
            var d2 = q2[0] * s + q2[1] * t;
            double denominator = d1.Dot(d1) + p.Dot(d2);
            result[i] = Math.Abs(denominator) < 1e-12 ? t : Math.Clamp(t - p.Dot(d1) / denominator, 0, 1);
        }
        return result;
    }
}
