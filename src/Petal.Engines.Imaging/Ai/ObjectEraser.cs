using ImageMagick;
using Microsoft.ML.OnnxRuntime;
using Petal.Core.Models;

namespace Petal.Engines.Imaging;

/// <summary>A brush stroke over a picture, in the picture's own pixels.</summary>
public sealed record BrushStroke(IReadOnlyList<(double X, double Y)> Points, double Radius);

/// <summary>
/// Erase Object: LaMa (Apache-2.0) fills in what was brushed over so it looks as if it was never
/// there. The model sees a 512 × 512 square around the brushed area; only the brushed pixels (with
/// a soft edge) change, so the rest of a large photo keeps its full detail.
/// </summary>
public static class ObjectEraser
{
    private const int Side = 512;

    public static IReadOnlyList<ModelInfo> Models { get; } = [ModelCatalog.Lama];

    public static bool IsReady => ModelStore.IsAvailable(ModelCatalog.Lama);

    private static readonly Lock Gate = new();
    private static InferenceSession? _session;

    /// <summary>A picture upright, as Erase works on it.</summary>
    public static MagickImage Load(string path)
    {
        var image = ImageLoader.Load(path);
        image.AutoOrient();
        return image;
    }

    /// <summary>The strokes as a mask the size of the picture: 255 where they cover it.</summary>
    public static byte[] Mask(int width, int height, IEnumerable<BrushStroke> strokes)
    {
        var mask = new byte[width * height];
        foreach (var stroke in strokes)
        {
            var points = stroke.Points;
            if (points.Count == 0)
                continue;
            double r = Math.Max(1, stroke.Radius);
            Stamp(points[0].X, points[0].Y);
            for (int i = 1; i < points.Count; i++)
            {
                var (ax, ay) = points[i - 1];
                var (bx, by) = points[i];
                double distance = Math.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay));
                int steps = Math.Max(1, (int)Math.Ceiling(distance / Math.Max(1, r / 3)));
                for (int s = 1; s <= steps; s++)
                    Stamp(ax + (bx - ax) * s / steps, ay + (by - ay) * s / steps);
            }

            void Stamp(double cx, double cy)
            {
                int x0 = Math.Max(0, (int)Math.Floor(cx - r)), x1 = Math.Min(width - 1, (int)Math.Ceiling(cx + r));
                int y0 = Math.Max(0, (int)Math.Floor(cy - r)), y1 = Math.Min(height - 1, (int)Math.Ceiling(cy + r));
                for (int y = y0; y <= y1; y++)
                    for (int x = x0; x <= x1; x++)
                        if ((x - cx) * (x - cx) + (y - cy) * (y - cy) <= r * r)
                            mask[y * width + x] = 255;
            }
        }
        return mask;
    }

    /// <summary>A copy of <paramref name="image"/> with the masked area filled in.</summary>
    public static MagickImage Erase(MagickImage image, byte[] mask)
    {
        int width = (int)image.Width, height = (int)image.Height;
        if (mask.Length != width * height)
            throw new ArgumentException("The mask isn't the size of the picture.", nameof(mask));
        int minX = width, minY = height, maxX = -1, maxY = -1;
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                if (mask[y * width + x] != 0)
                {
                    minX = Math.Min(minX, x);
                    maxX = Math.Max(maxX, x);
                    minY = Math.Min(minY, y);
                    maxY = Math.Max(maxY, y);
                }
        var result = (MagickImage)image.Clone();
        if (maxX < 0)
            return result;

        // A square around the brushed area with as much context again around it.
        int boxW = maxX - minX + 1, boxH = maxY - minY + 1;
        int side = Math.Min(Math.Max(width, height), Math.Max(Math.Max(boxW, boxH) * 2 + 64, 256));
        side = Math.Min(side, Math.Min(width, height) < side ? Math.Max(width, height) : side);
        int cx = (minX + maxX) / 2, cy = (minY + maxY) / 2;
        int cropW = Math.Min(side, width), cropH = Math.Min(side, height);
        int left = Math.Clamp(cx - cropW / 2, 0, width - cropW), top = Math.Clamp(cy - cropH / 2, 0, height - cropH);

        using var crop = (MagickImage)image.Clone();
        ImageWriter.FlattenOnWhite(crop);
        crop.Crop(new MagickGeometry(left, top, (uint)cropW, (uint)cropH));
        crop.ResetPage();
        crop.FilterType = FilterType.Lanczos;
        crop.Resize(new MagickGeometry(Side, Side) { IgnoreAspectRatio = true });
        var rgb = crop.GetPixels().ToByteArray(PixelMapping.RGB) ?? throw new InvalidOperationException("The picture has no pixels.");

        // The mask at 512, a little wider so the object's outline goes too.
        var small = new float[Side * Side];
        for (int y = 0; y < Side; y++)
            for (int x = 0; x < Side; x++)
            {
                int sx = left + (int)((x + 0.5) * cropW / Side), sy = top + (int)((y + 0.5) * cropH / Side);
                small[y * Side + x] = mask[Math.Min(height - 1, sy) * width + Math.Min(width - 1, sx)] != 0 ? 1 : 0;
            }
        small = Dilate(small, Math.Max(2, Side / 128));

        var input = new float[3 * Side * Side];
        for (int i = 0; i < Side * Side; i++)
            for (int c = 0; c < 3; c++)
                input[c * Side * Side + i] = rgb[i * 3 + c] / 255f;
        InferenceSession session;
        lock (Gate)
            session = _session ??= new InferenceSession(ModelStore.Find(ModelCatalog.Lama) ?? throw new FileNotFoundException("The eraser model isn't downloaded yet."),
                new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL, IntraOpNumThreads = Environment.ProcessorCount });
        using var imageTensor = OrtValue.CreateTensorValueFromMemory(input, [1, 3, Side, Side]);
        using var maskTensor = OrtValue.CreateTensorValueFromMemory(small, [1, 1, Side, Side]);
        using var run = new RunOptions();
        var names = session.InputMetadata.Keys.ToList();
        var values = names.Select(n => n.Contains("mask", StringComparison.OrdinalIgnoreCase) ? maskTensor : imageTensor).ToList();
        using var outputs = session.Run(run, names, values, [session.OutputMetadata.Keys.First()]);
        var filled = outputs[0].GetTensorDataAsSpan<float>();

        var bytes = new byte[Side * Side * 3];
        for (int i = 0; i < Side * Side; i++)
            for (int c = 0; c < 3; c++)
                bytes[i * 3 + c] = (byte)Math.Clamp(filled[c * Side * Side + i] + 0.5f, 0, 255);
        using var patch = new MagickImage();
        patch.ReadPixels(bytes, new PixelReadSettings(Side, Side, StorageType.Char, PixelMapping.RGB));
        patch.FilterType = FilterType.Lanczos;
        patch.Resize(new MagickGeometry((uint)cropW, (uint)cropH) { IgnoreAspectRatio = true });
        var patchRgb = patch.GetPixels().ToByteArray(PixelMapping.RGB)!;

        // The filled pixels go over the picture where the mask is, with a feathered edge: the
        // patch carries the soft mask as its transparency.
        var soft = Feather(mask, width, height, left, top, cropW, cropH, Math.Max(2, Math.Max(cropW, cropH) / 170));
        var rgba = new byte[cropW * cropH * 4];
        for (int i = 0; i < cropW * cropH; i++)
        {
            rgba[i * 4] = patchRgb[i * 3];
            rgba[i * 4 + 1] = patchRgb[i * 3 + 1];
            rgba[i * 4 + 2] = patchRgb[i * 3 + 2];
            rgba[i * 4 + 3] = (byte)Math.Clamp(soft[i] * 255 + 0.5f, 0, 255);
        }
        using var blended = new MagickImage();
        blended.ReadPixels(rgba, new PixelReadSettings((uint)cropW, (uint)cropH, StorageType.Char, PixelMapping.RGBA));
        result.Composite(blended, left, top, CompositeOperator.Over);
        return result;
    }

    private static float[] Dilate(float[] mask, int radius)
    {
        var result = new float[mask.Length];
        for (int y = 0; y < Side; y++)
            for (int x = 0; x < Side; x++)
            {
                if (mask[y * Side + x] == 0)
                    continue;
                for (int dy = -radius; dy <= radius; dy++)
                    for (int dx = -radius; dx <= radius; dx++)
                    {
                        int xx = x + dx, yy = y + dy;
                        if (xx >= 0 && yy >= 0 && xx < Side && yy < Side && dx * dx + dy * dy <= radius * radius)
                            result[yy * Side + xx] = 1;
                    }
            }
        return result;
    }

    /// <summary>The crop's share of the mask, widened by <paramref name="grow"/> and softened over as much again.</summary>
    private static float[] Feather(byte[] mask, int width, int height, int left, int top, int w, int h, int grow)
    {
        var distance = new float[w * h];
        Array.Fill(distance, float.MaxValue);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                if (mask[(top + y) * width + left + x] != 0)
                    distance[y * w + x] = 0;
        // Two passes of a chamfer distance transform.
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                ref float d = ref distance[y * w + x];
                if (x > 0) d = Math.Min(d, distance[y * w + x - 1] + 1);
                if (y > 0) d = Math.Min(d, distance[(y - 1) * w + x] + 1);
                if (x > 0 && y > 0) d = Math.Min(d, distance[(y - 1) * w + x - 1] + 1.414f);
                if (x < w - 1 && y > 0) d = Math.Min(d, distance[(y - 1) * w + x + 1] + 1.414f);
            }
        for (int y = h - 1; y >= 0; y--)
            for (int x = w - 1; x >= 0; x--)
            {
                ref float d = ref distance[y * w + x];
                if (x < w - 1) d = Math.Min(d, distance[y * w + x + 1] + 1);
                if (y < h - 1) d = Math.Min(d, distance[(y + 1) * w + x] + 1);
                if (x < w - 1 && y < h - 1) d = Math.Min(d, distance[(y + 1) * w + x + 1] + 1.414f);
                if (x > 0 && y < h - 1) d = Math.Min(d, distance[(y + 1) * w + x - 1] + 1.414f);
            }
        var alpha = new float[w * h];
        for (int i = 0; i < alpha.Length; i++)
            alpha[i] = distance[i] <= grow ? 1 : Math.Max(0, 1 - (distance[i] - grow) / grow);
        return alpha;
    }

    /// <summary>Erases the strokes from the picture file and saves the result in the output's format.</summary>
    public static async Task EraseAsync(string input, IReadOnlyList<BrushStroke> strokes, string output, CancellationToken cancellationToken = default)
    {
        using var image = await Task.Run(() => ImageLoader.Load(input), cancellationToken);
        image.AutoOrient();
        var mask = Mask((int)image.Width, (int)image.Height, strokes);
        using var erased = await Task.Run(() => Erase(image, mask), cancellationToken);
        await SaveAsync(erased, output, cancellationToken);
    }

    /// <summary>Saves an erased picture in the output's format (PNG when that can't be written).</summary>
    public static Task SaveAsync(MagickImage image, string output, CancellationToken cancellationToken = default) =>
        ImageWriter.WriteAsync(image, output, Petal.Core.Formats.Formats.FromPath(output) is { CanWrite: true } format ? format : Petal.Core.Formats.Formats.Png, null, cancellationToken);
}
