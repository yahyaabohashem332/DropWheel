using ImageMagick;

namespace Petal.Engines.Imaging;

/// <summary>How a scanned page is cleaned up.</summary>
public enum ScanLook
{
    /// <summary>Colour kept, shadows and the paper's tint evened out to white.</summary>
    Document,
    /// <summary>The photo's own colours, only levelled.</summary>
    Color,
    /// <summary>Like <see cref="Document"/>, in shades of grey.</summary>
    Gray,
    /// <summary>Pure black on white, like a fax or a photocopy.</summary>
    BlackAndWhite,
}

/// <summary>A page's four corners as fractions of the (upright) photo: top-left, top-right, bottom-right, bottom-left.</summary>
public sealed record PageCorners(double[] Points)
{
    public static PageCorners Inset(double margin) =>
        new([margin, margin, 1 - margin, margin, 1 - margin, 1 - margin, margin, 1 - margin]);

    public (double X, double Y) this[int i] => (Points[2 * i], Points[2 * i + 1]);

    public bool IsValid => Points.Length == 8 && Points.All(v => double.IsFinite(v) && v is >= -0.01 and <= 1.01);
}

/// <summary>
/// Turns a phone photo of a page into a flat scan: finds the sheet of paper, straightens it with a
/// perspective correction, and evens out shadows so the paper is white.
/// </summary>
public static class DocumentScanner
{
    /// <summary>Longest side of the finished page; 3508 px is A4 at 300 dpi.</summary>
    private const int MaxSide = 3508;
    private const int WorkSide = 480;
    /// <summary>A4's width in inches: finished pages are given the density that makes them A4 wide.</summary>
    private const double PageWidthInches = 8.27;

    /// <summary>Finds the sheet in the photo; falls back to a slightly inset frame when there is no clear page.</summary>
    public static Task<PageCorners> FindPageAsync(string photo, CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        using var image = ImageLoader.Load(photo);
        image.AutoOrient();
        return FindPage(image, cancellationToken) ?? PageCorners.Inset(0.04);
    }, cancellationToken);

    internal static PageCorners? FindPage(MagickImage image, CancellationToken cancellationToken)
    {
        using var small = (MagickImage)image.Clone();
        ImageWriter.FlattenOnWhite(small);
        small.Resize(new MagickGeometry(WorkSide, WorkSide) { Greater = true });
        int w = (int)small.Width, h = (int)small.Height;
        var rgb = small.GetPixels().ToByteArray(PixelMapping.RGB) ?? [];
        // Paper is light and nearly colourless; tables, hands and carpets are darker or coloured.
        var score = new byte[w * h];
        for (int i = 0, p = 0; i < score.Length; i++, p += 3)
        {
            int r = rgb[p], g = rgb[p + 1], b = rgb[p + 2];
            int light = (r + g + b) / 3, colour = Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b));
            score[i] = (byte)Math.Clamp(light - 3 * colour / 2, 0, 255);
        }
        score = BoxBlur(BoxBlur(score, w, h), w, h);
        cancellationToken.ThrowIfCancellationRequested();

        int threshold = Otsu(score);
        var mask = new bool[w * h];
        for (int i = 0; i < mask.Length; i++)
            mask[i] = score[i] > threshold;
        mask = Open(mask, w, h);

        // The page: a large bright region that doesn't fill the whole frame; if the paper is the
        // darker part (a dark page on a white desk), the other side of the threshold.
        var best = BestRegion(mask, w, h);
        if (best is null)
        {
            for (int i = 0; i < mask.Length; i++)
                mask[i] = !mask[i];
            best = BestRegion(mask, w, h);
        }
        if (best is null)
            return null;

        var hull = ConvexHull(best);
        var quad = LargestQuad(Simplify(hull, Math.Max(w, h) * 0.006));
        if (quad is null || Area(quad) < 0.15 * w * h)
            return null;
        var ordered = Order(quad);
        // A pixel too far out shows a strip of table along the edge; one percent in never does.
        double cx = ordered.Average(p => p.X), cy = ordered.Average(p => p.Y);
        var points = new double[8];
        for (int i = 0; i < 4; i++)
        {
            points[2 * i] = Math.Clamp((cx + (ordered[i].X - cx) * 0.99) / (w - 1.0), 0, 1);
            points[2 * i + 1] = Math.Clamp((cy + (ordered[i].Y - cy) * 0.99) / (h - 1.0), 0, 1);
        }
        return new PageCorners(points);
    }

    /// <summary>Straightens the page inside <paramref name="corners"/> and saves it as a JPEG sized for an A4-wide page.</summary>
    public static Task FlattenAsync(string photo, PageCorners corners, ScanLook look, string output, CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        if (!corners.IsValid)
            throw new ArgumentException("The page's corners are not inside the photo.", nameof(corners));
        using var image = ImageLoader.Load(photo);
        image.AutoOrient();
        ImageWriter.FlattenOnWhite(image);
        double w = image.Width - 1, h = image.Height - 1;
        var c = Enumerable.Range(0, 4).Select(i => (X: corners[i].X * w, Y: corners[i].Y * h)).ToArray();
        static double Distance((double X, double Y) a, (double X, double Y) b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
        double width = Math.Max(Distance(c[0], c[1]), Distance(c[3], c[2]));
        double height = Math.Max(Distance(c[0], c[3]), Distance(c[1], c[2]));
        // Seen at an angle, the far edge looks shorter: the page's real proportions come from the
        // perspective itself. Keep the longer measured side and size the other from them.
        if (AspectRatio(c, image.Width / 2.0, image.Height / 2.0) is { } ratio)
        {
            if (height * ratio >= width)
                width = height * ratio;
            else
                height = width / ratio;
        }
        double fit = Math.Min(1, MaxSide / Math.Max(width, height));
        int outW = Math.Max(16, (int)Math.Round(width * fit)), outH = Math.Max(16, (int)Math.Round(height * fit));

        int srcW = (int)image.Width, srcH = (int)image.Height;
        var source = image.GetPixels().ToByteArray(PixelMapping.RGB) ?? throw new InvalidOperationException("The photo has no pixels.");
        cancellationToken.ThrowIfCancellationRequested();
        var page = Warp(source, srcW, srcH, c, outW, outH);
        cancellationToken.ThrowIfCancellationRequested();
        bool gray = look is ScanLook.Gray or ScanLook.BlackAndWhite;
        Clean(page, outW, outH, look);

        using var result = new MagickImage();
        result.ReadPixels(page, new PixelReadSettings((uint)outW, (uint)outH, StorageType.Char, PixelMapping.RGB));
        if (gray)
            result.ColorType = ColorType.Grayscale;
        double dpi = outW / PageWidthInches;
        result.Format = MagickFormat.Jpeg;
        result.Quality = look == ScanLook.BlackAndWhite ? 80u : 85u;
        result.Density = new Density(dpi, dpi, DensityUnit.PixelsPerInch);
        result.Write(output);
    }, cancellationToken);

    /// <summary>
    /// Width over height of the real rectangle behind a photographed quadrilateral (top-left,
    /// top-right, bottom-right, bottom-left), assuming an ordinary camera centred on the photo
    /// (Zhang &amp; He, "Whiteboard scanning and image enhancement"). Null for a shape no page
    /// could have, where the measured sides are kept.
    /// </summary>
    internal static double? AspectRatio(IReadOnlyList<(double X, double Y)> corners, double centerX, double centerY)
    {
        // The paper's m1..m4 are top-left, top-right, bottom-left, bottom-right.
        var m1 = (corners[0].X - centerX, corners[0].Y - centerY, 1.0);
        var m2 = (corners[1].X - centerX, corners[1].Y - centerY, 1.0);
        var m3 = (corners[3].X - centerX, corners[3].Y - centerY, 1.0);
        var m4 = (corners[2].X - centerX, corners[2].Y - centerY, 1.0);
        static (double, double, double) Cross((double X, double Y, double Z) a, (double X, double Y, double Z) b) =>
            (a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
        static double Dot((double X, double Y, double Z) a, (double X, double Y, double Z) b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

        double k2 = Dot(Cross(m1, m4), m3) / Dot(Cross(m2, m4), m3);
        double k3 = Dot(Cross(m1, m4), m2) / Dot(Cross(m3, m4), m2);
        var n2 = (k2 * m2.Item1 - m1.Item1, k2 * m2.Item2 - m1.Item2, k2 - 1);
        var n3 = (k3 * m3.Item1 - m1.Item1, k3 * m3.Item2 - m1.Item2, k3 - 1);
        // The focal length follows when both pairs of edges converge; a photo tilted straight
        // towards one edge (a symmetric trapezoid) doesn't say, so a phone camera's usual one is
        // assumed: about 0.65 of the photo's diagonal (a 26 mm equivalent lens).
        double diagonal = 2 * Math.Sqrt(centerX * centerX + centerY * centerY);
        double denominator = n2.Item3 * n3.Item3;
        double f2 = Math.Abs(denominator) < 1e-9 ? double.NaN : -(n2.Item1 * n3.Item1 + n2.Item2 * n3.Item2) / denominator;
        double f = f2 > 0 && double.IsFinite(f2) && Math.Sqrt(f2) is var fit && fit > 0.3 * diagonal && fit < 5 * diagonal
            ? fit
            : 0.65 * diagonal;
        double width = Math.Sqrt((n2.Item1 * n2.Item1 + n2.Item2 * n2.Item2) / (f * f) + n2.Item3 * n2.Item3);
        double height = Math.Sqrt((n3.Item1 * n3.Item1 + n3.Item2 * n3.Item2) / (f * f) + n3.Item3 * n3.Item3);
        double ratio = width / height;
        return double.IsFinite(ratio) && ratio is > 0.2 and < 5 ? ratio : null;
    }

    /// <summary>
    /// The page as an upright rectangle: each output pixel is looked up in the photo through the
    /// perspective that maps the rectangle onto the four corners, with bilinear interpolation.
    /// </summary>
    internal static byte[] Warp(byte[] source, int srcW, int srcH, (double X, double Y)[] corners, int outW, int outH)
    {
        var h = Homography([(0, 0), (outW - 1, 0), (outW - 1, outH - 1), (0, outH - 1)], corners);
        var page = new byte[outW * outH * 3];
        Parallel.For(0, outH, v =>
        {
            int row = v * outW * 3;
            for (int u = 0; u < outW; u++)
            {
                double d = h[6] * u + h[7] * v + 1;
                double x = Math.Clamp((h[0] * u + h[1] * v + h[2]) / d, 0, srcW - 1);
                double y = Math.Clamp((h[3] * u + h[4] * v + h[5]) / d, 0, srcH - 1);
                int x0 = (int)x, y0 = (int)y, x1 = Math.Min(x0 + 1, srcW - 1), y1 = Math.Min(y0 + 1, srcH - 1);
                double fx = x - x0, fy = y - y0;
                int a = (y0 * srcW + x0) * 3, b = (y0 * srcW + x1) * 3, c = (y1 * srcW + x0) * 3, e = (y1 * srcW + x1) * 3;
                for (int k = 0; k < 3; k++)
                {
                    double top = source[a + k] + (source[b + k] - source[a + k]) * fx;
                    double bottom = source[c + k] + (source[e + k] - source[c + k]) * fx;
                    page[row + u * 3 + k] = (byte)(top + (bottom - top) * fy + 0.5);
                }
            }
        });
        return page;
    }

    /// <summary>The 3 × 3 projective map (its last entry 1) taking each <paramref name="from"/> point to its <paramref name="to"/> point.</summary>
    internal static double[] Homography((double X, double Y)[] from, (double X, double Y)[] to)
    {
        var m = new double[8, 9];
        for (int i = 0; i < 4; i++)
        {
            var (x, y) = from[i];
            var (X, Y) = to[i];
            double[] r1 = [x, y, 1, 0, 0, 0, -x * X, -y * X, X];
            double[] r2 = [0, 0, 0, x, y, 1, -x * Y, -y * Y, Y];
            for (int j = 0; j < 9; j++)
            {
                m[2 * i, j] = r1[j];
                m[2 * i + 1, j] = r2[j];
            }
        }
        // Gauss-Jordan elimination with partial pivoting.
        for (int col = 0; col < 8; col++)
        {
            int pivot = col;
            for (int r = col + 1; r < 8; r++)
            {
                if (Math.Abs(m[r, col]) > Math.Abs(m[pivot, col]))
                    pivot = r;
            }
            if (Math.Abs(m[pivot, col]) < 1e-12)
                throw new ArgumentException("The page's corners don't make a quadrilateral.");
            for (int j = 0; j < 9; j++)
                (m[col, j], m[pivot, j]) = (m[pivot, j], m[col, j]);
            for (int r = 0; r < 8; r++)
            {
                if (r == col)
                    continue;
                double f = m[r, col] / m[col, col];
                for (int j = col; j < 9; j++)
                    m[r, j] -= f * m[col, j];
            }
        }
        var h = new double[8];
        for (int i = 0; i < 8; i++)
            h[i] = m[i, 8] / m[i, i];
        return h;
    }

    /// <summary>Evens out the lighting (and greys, thresholds or levels the colours), in place.</summary>
    private static void Clean(byte[] rgb, int w, int h, ScanLook look)
    {
        int n = w * h;
        if (look == ScanLook.Color)
        {
            // Stretch each channel between its darkest and lightest half-percent.
            for (int k = 0; k < 3; k++)
            {
                var histogram = new int[256];
                for (int i = k; i < rgb.Length; i += 3)
                    histogram[rgb[i]]++;
                int lo = Percentile(histogram, n, 0.005), hi = Percentile(histogram, n, 0.995);
                if (hi - lo < 16)
                    continue;
                var map = new byte[256];
                for (int v = 0; v < 256; v++)
                    map[v] = (byte)Math.Clamp((v - lo) * 255 / (hi - lo), 0, 255);
                for (int i = k; i < rgb.Length; i += 3)
                    rgb[i] = map[rgb[i]];
            }
            return;
        }

        bool gray = look is ScanLook.Gray or ScanLook.BlackAndWhite;
        if (gray)
        {
            Parallel.For(0, h, y =>
            {
                for (int i = y * w * 3, end = i + w * 3; i < end; i += 3)
                {
                    byte l = (byte)((rgb[i] * 77 + rgb[i + 1] * 150 + rgb[i + 2] * 29) >> 8);
                    rgb[i] = rgb[i + 1] = rgb[i + 2] = l;
                }
            });
        }

        // The page's lighting: averages of 12 × 12 blocks with the ink closed over (thin dark
        // strokes vanish under a maximum then a minimum filter), blurred, then read back
        // bilinearly. Dividing by it leaves white paper and the ink in its own colour.
        const int block = 12;
        int sw = Math.Max(2, (w + block - 1) / block), sh = Math.Max(2, (h + block - 1) / block);
        int channels = gray ? 1 : 3;
        var light = new double[channels][];
        for (int k = 0; k < channels; k++)
        {
            var small = new double[sw * sh];
            var counts = new int[sw * sh];
            for (int y = 0; y < h; y++)
            {
                int sy = y / block;
                for (int x = 0, i = y * w * 3 + k; x < w; x++, i += 3)
                {
                    int cell = sy * sw + x / block;
                    small[cell] += rgb[i];
                    counts[cell]++;
                }
            }
            for (int cell = 0; cell < small.Length; cell++)
                small[cell] /= Math.Max(1, counts[cell]);
            small = Filter(Filter(small, sw, sh, 3, Math.Max), sw, sh, 3, Math.Min);
            light[k] = Box(Box(small, sw, sh, 2), sw, sh, 2);
        }
        double low = (look == ScanLook.BlackAndWhite ? 0.35 : 0.12) * 255, high = 0.96 * 255;
        Parallel.For(0, h, y =>
        {
            double gy = Math.Clamp((y + 0.5) / block - 0.5, 0, sh - 1);
            int y0 = (int)gy, y1 = Math.Min(y0 + 1, sh - 1);
            double fy = gy - y0;
            for (int x = 0; x < w; x++)
            {
                double gx = Math.Clamp((x + 0.5) / block - 0.5, 0, sw - 1);
                int x0 = (int)gx, x1 = Math.Min(x0 + 1, sw - 1);
                double fx = gx - x0;
                int i = (y * w + x) * 3;
                for (int k = 0; k < channels; k++)
                {
                    var l = light[k];
                    double top = l[y0 * sw + x0] + (l[y0 * sw + x1] - l[y0 * sw + x0]) * fx;
                    double bottom = l[y1 * sw + x0] + (l[y1 * sw + x1] - l[y1 * sw + x0]) * fx;
                    double paper = Math.Max(24, top + (bottom - top) * fy);
                    double v = (rgb[i + k] * 255 / paper - low) * 255 / (high - low);
                    if (look == ScanLook.BlackAndWhite)
                        v = v >= 0.62 * 255 ? 255 : 0;
                    rgb[i + k] = (byte)Math.Clamp(v + 0.5, 0, 255);
                }
                if (gray)
                    rgb[i + 1] = rgb[i + 2] = rgb[i];
            }
        });
    }

    private static int Percentile(int[] histogram, int total, double fraction)
    {
        long target = (long)(total * fraction), sum = 0;
        for (int v = 0; v < 256; v++)
        {
            sum += histogram[v];
            if (sum > target)
                return v;
        }
        return 255;
    }

    /// <summary>A square maximum or minimum filter (<paramref name="pick"/>) of the given radius, rows then columns.</summary>
    private static double[] Filter(double[] src, int w, int h, int radius, Func<double, double, double> pick)
    {
        var rows = new double[src.Length];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                double v = src[y * w + x];
                for (int d = Math.Max(0, x - radius); d <= Math.Min(w - 1, x + radius); d++)
                    v = pick(v, src[y * w + d]);
                rows[y * w + x] = v;
            }
        }
        var result = new double[src.Length];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                double v = rows[y * w + x];
                for (int d = Math.Max(0, y - radius); d <= Math.Min(h - 1, y + radius); d++)
                    v = pick(v, rows[d * w + x]);
                result[y * w + x] = v;
            }
        }
        return result;
    }

    private static double[] Box(double[] src, int w, int h, int radius)
    {
        var result = new double[src.Length];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                double sum = 0;
                int count = 0;
                for (int dy = Math.Max(0, y - radius); dy <= Math.Min(h - 1, y + radius); dy++)
                {
                    for (int dx = Math.Max(0, x - radius); dx <= Math.Min(w - 1, x + radius); dx++)
                    {
                        sum += src[dy * w + dx];
                        count++;
                    }
                }
                result[y * w + x] = sum / count;
            }
        }
        return result;
    }

    // ---- Finding the page ----

    private readonly record struct Pt(double X, double Y);

    private static byte[] BoxBlur(byte[] src, int w, int h)
    {
        var dst = new byte[src.Length];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int sum = 0, n = 0;
                for (int dy = -1; dy <= 1; dy++)
                {
                    int yy = y + dy;
                    if ((uint)yy >= (uint)h)
                        continue;
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int xx = x + dx;
                        if ((uint)xx >= (uint)w)
                            continue;
                        sum += src[yy * w + xx];
                        n++;
                    }
                }
                dst[y * w + x] = (byte)(sum / n);
            }
        }
        return dst;
    }

    private static int Otsu(byte[] values)
    {
        var histogram = new long[256];
        foreach (var v in values)
            histogram[v]++;
        long total = values.Length;
        double sumAll = 0;
        for (int i = 0; i < 256; i++)
            sumAll += i * (double)histogram[i];
        double sumBelow = 0, best = -1;
        long below = 0;
        int threshold = 128;
        for (int t = 0; t < 256; t++)
        {
            below += histogram[t];
            if (below == 0)
                continue;
            long above = total - below;
            if (above == 0)
                break;
            sumBelow += t * (double)histogram[t];
            double meanBelow = sumBelow / below, meanAbove = (sumAll - sumBelow) / above;
            double between = (double)below * above * (meanBelow - meanAbove) * (meanBelow - meanAbove);
            if (between > best)
            {
                best = between;
                threshold = t;
            }
        }
        return threshold;
    }

    /// <summary>Erosion then dilation (3 × 3): specks and thin bridges to the background go.</summary>
    private static bool[] Open(bool[] mask, int w, int h) => Morph(Morph(mask, w, h, erode: true), w, h, erode: false);

    private static bool[] Morph(bool[] mask, int w, int h, bool erode)
    {
        var result = new bool[mask.Length];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                bool value = erode;
                for (int dy = -1; dy <= 1 && value == erode; dy++)
                {
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int xx = Math.Clamp(x + dx, 0, w - 1), yy = Math.Clamp(y + dy, 0, h - 1);
                        if (mask[yy * w + xx] != erode)
                        {
                            value = !erode;
                            break;
                        }
                    }
                }
                result[y * w + x] = value;
            }
        }
        return result;
    }

    /// <summary>
    /// The pixels of the most page-like region: big, and not the backdrop around everything (a
    /// region touching three or four edges of the photo counts for less).
    /// </summary>
    private static List<Pt>? BestRegion(bool[] mask, int w, int h)
    {
        var label = new int[mask.Length];
        var queue = new Queue<int>();
        List<Pt>? best = null;
        double bestScore = 0;
        int next = 0;
        for (int start = 0; start < mask.Length; start++)
        {
            if (!mask[start] || label[start] != 0)
                continue;
            next++;
            var edges = 0;
            bool left = false, right = false, top = false, bottom = false;
            // Each row's leftmost and rightmost pixel is all the hull needs.
            var minX = new int[h];
            var maxX = new int[h];
            Array.Fill(minX, int.MaxValue);
            Array.Fill(maxX, -1);
            int area = 0;
            label[start] = next;
            queue.Enqueue(start);
            while (queue.Count > 0)
            {
                int i = queue.Dequeue(), x = i % w, y = i / w;
                area++;
                minX[y] = Math.Min(minX[y], x);
                maxX[y] = Math.Max(maxX[y], x);
                left |= x == 0;
                right |= x == w - 1;
                top |= y == 0;
                bottom |= y == h - 1;
                if (x > 0) Visit(i - 1);
                if (x < w - 1) Visit(i + 1);
                if (y > 0) Visit(i - w);
                if (y < h - 1) Visit(i + w);
            }
            edges = (left ? 1 : 0) + (right ? 1 : 0) + (top ? 1 : 0) + (bottom ? 1 : 0);
            // Touching three or four sides of the photo, it is the backdrop (or a page too close
            // to tell apart): the inset frame is the better guess.
            if (area < 0.12 * w * h || edges >= 3)
                continue;
            double score = area;
            if (score <= bestScore)
                continue;
            bestScore = score;
            best = [];
            for (int y = 0; y < h; y++)
            {
                if (maxX[y] < 0)
                    continue;
                best.Add(new Pt(minX[y], y));
                best.Add(new Pt(maxX[y], y));
            }

            void Visit(int j)
            {
                if (mask[j] && label[j] == 0)
                {
                    label[j] = next;
                    queue.Enqueue(j);
                }
            }
        }
        // A "page" that is the whole frame tells nothing.
        return bestScore > 0 && best is not null && bestScore < 0.97 * w * h ? best : null;
    }

    private static List<Pt> ConvexHull(List<Pt> points)
    {
        var sorted = points.Distinct().OrderBy(p => p.X).ThenBy(p => p.Y).ToList();
        if (sorted.Count < 3)
            return sorted;
        static double Cross(Pt o, Pt a, Pt b) => (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);
        var hull = new List<Pt>();
        foreach (var pass in new[] { sorted, Enumerable.Reverse(sorted).ToList() })
        {
            int start = hull.Count;
            foreach (var p in pass)
            {
                while (hull.Count >= start + 2 && Cross(hull[^2], hull[^1], p) <= 0)
                    hull.RemoveAt(hull.Count - 1);
                hull.Add(p);
            }
            hull.RemoveAt(hull.Count - 1);
        }
        return hull;
    }

    /// <summary>Drops hull points that hardly bend the outline, so the corner search stays small.</summary>
    private static List<Pt> Simplify(List<Pt> hull, double tolerance)
    {
        var points = hull.ToList();
        bool removed = true;
        while (removed && points.Count > 4)
        {
            removed = false;
            int weakest = -1;
            double least = double.MaxValue;
            for (int i = 0; i < points.Count; i++)
            {
                var a = points[(i + points.Count - 1) % points.Count];
                var b = points[i];
                var c = points[(i + 1) % points.Count];
                double length = Math.Sqrt((c.X - a.X) * (c.X - a.X) + (c.Y - a.Y) * (c.Y - a.Y));
                double distance = length < 1e-9 ? 0 : Math.Abs((c.X - a.X) * (a.Y - b.Y) - (a.X - b.X) * (c.Y - a.Y)) / length;
                if (distance < least)
                {
                    least = distance;
                    weakest = i;
                }
            }
            if (least < tolerance || points.Count > 48)
            {
                points.RemoveAt(weakest);
                removed = true;
            }
        }
        return points;
    }

    /// <summary>The four hull points that enclose the most area.</summary>
    private static Pt[]? LargestQuad(List<Pt> hull)
    {
        int n = hull.Count;
        if (n < 4)
            return null;
        Pt[]? best = null;
        double bestArea = 0;
        for (int a = 0; a < n; a++)
            for (int b = a + 1; b < n; b++)
                for (int c = b + 1; c < n; c++)
                    for (int d = c + 1; d < n; d++)
                    {
                        var quad = new[] { hull[a], hull[b], hull[c], hull[d] };
                        double area = Area(quad);
                        if (area > bestArea)
                        {
                            bestArea = area;
                            best = quad;
                        }
                    }
        return best;
    }

    private static double Area(Pt[] polygon)
    {
        double sum = 0;
        for (int i = 0; i < polygon.Length; i++)
        {
            var p = polygon[i];
            var q = polygon[(i + 1) % polygon.Length];
            sum += p.X * q.Y - q.X * p.Y;
        }
        return Math.Abs(sum) / 2;
    }

    /// <summary>Clockwise from the top-left corner (the one nearest the photo's top-left).</summary>
    private static Pt[] Order(Pt[] quad)
    {
        double cx = quad.Average(p => p.X), cy = quad.Average(p => p.Y);
        // Image y points down, so increasing atan2 goes clockwise on screen.
        var around = quad.OrderBy(p => Math.Atan2(p.Y - cy, p.X - cx)).ToList();
        int first = around.IndexOf(around.MinBy(p => p.X + p.Y));
        return Enumerable.Range(0, 4).Select(i => around[(first + i) % 4]).ToArray();
    }
}
