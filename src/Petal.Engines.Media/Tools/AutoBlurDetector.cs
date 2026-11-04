using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Petal.Core.Models;

namespace Petal.Engines.Media;

/// <summary>What is to be hidden.</summary>
public enum BlurKind
{
    Face,
    Plate,
}

/// <summary>Something found in a frame: its box as shares of the frame (0 … 1), and how sure the model is.</summary>
public readonly record struct BlurSighting(BlurKind Kind, double X, double Y, double Width, double Height, double Score)
{
    public double Right => X + Width;

    public double Bottom => Y + Height;

    public double Area => Width * Height;

    /// <summary>How much two boxes cover the same place, 0 to 1.</summary>
    public double Overlap(in BlurSighting other)
    {
        double w = Math.Min(Right, other.Right) - Math.Max(X, other.X), h = Math.Min(Bottom, other.Bottom) - Math.Max(Y, other.Y);
        if (w <= 0 || h <= 0)
            return 0;
        double shared = w * h;
        return shared / (Area + other.Area - shared);
    }
}

/// <summary>
/// Finds faces and number plates in a picture with OpenCV's YuNet models: small, quick on a processor, and free for
/// commercial use (faces MIT, plates Apache-2.0). The plate model was trained on Chinese plates and is less sure of
/// others, so a lower score is accepted for it and the result is for checking.
/// </summary>
internal sealed class AutoBlurDetector : IDisposable
{
    private const double FaceScore = 0.6;
    private const double PlateScore = 0.5;
    private const double SameThing = 0.3;
    private static readonly int[] FaceStrides = [8, 16, 32];

    private readonly InferenceSession? _face;
    private readonly InferenceSession? _plate;
    private readonly int _plateWidth, _plateHeight;

    public AutoBlurDetector(bool faces, bool plates)
    {
        var options = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL, IntraOpNumThreads = Math.Max(1, Environment.ProcessorCount / 2) };
        if (faces)
            _face = new InferenceSession(ModelStore.Find(ModelCatalog.FaceFinder) ?? throw new FileNotFoundException("The face model isn't downloaded yet."), options);
        if (plates)
        {
            _plate = new InferenceSession(ModelStore.Find(ModelCatalog.PlateFinder) ?? throw new FileNotFoundException("The number plate model isn't downloaded yet."), options);
            var shape = _plate.InputMetadata.First().Value.Dimensions;
            // Fixed by the model if it says so; otherwise this size holds a plate at a reasonable distance.
            (_plateHeight, _plateWidth) = shape.Length == 4 && shape[2] > 0 && shape[3] > 0 ? (shape[2], shape[3]) : (480, 640);
        }
    }

    public void Dispose()
    {
        _face?.Dispose();
        _plate?.Dispose();
    }

    /// <summary>What is in a picture given as BGR bytes.</summary>
    public List<BlurSighting> Find(byte[] bgr, int width, int height)
    {
        var found = new List<BlurSighting>();
        if (_face is not null)
            found.AddRange(Faces(bgr, width, height));
        if (_plate is not null)
            found.AddRange(Plates(bgr, width, height));
        return found;
    }

    /// <summary>The picture as the model wants it: planes of 0–255 values in BGR order, stretched to its size.</summary>
    private static float[] Planes(byte[] bgr, int width, int height, int w, int h)
    {
        var input = new float[3 * w * h];
        for (int y = 0; y < h; y++)
        {
            int sy = Math.Min(height - 1, y * height / h);
            for (int x = 0; x < w; x++)
            {
                int i = (sy * width + Math.Min(width - 1, x * width / w)) * 3, o = y * w + x;
                input[o] = bgr[i];
                input[w * h + o] = bgr[i + 1];
                input[2 * w * h + o] = bgr[i + 2];
            }
        }
        return input;
    }

    private static int Multiple32(int size) => Math.Max(32, (size + 31) / 32 * 32);

    private List<BlurSighting> Faces(byte[] bgr, int width, int height)
    {
        int w = Multiple32(width), h = Multiple32(height);
        var input = Planes(bgr, width, height, w, h);
        var name = _face!.InputMetadata.Keys.First();
        using var outputs = _face.Run([NamedOnnxValue.CreateFromTensor(name, new DenseTensor<float>(input, [1, 3, h, w]))]);
        var byName = outputs.ToDictionary(o => o.Name, o => o.AsTensor<float>());
        var candidates = new List<BlurSighting>();
        foreach (int stride in FaceStrides)
        {
            int rows = h / stride, columns = w / stride;
            var cls = byName[$"cls_{stride}"].ToArray();
            var obj = byName[$"obj_{stride}"].ToArray();
            var box = byName[$"bbox_{stride}"].ToArray();
            for (int r = 0; r < rows; r++)
                for (int c = 0; c < columns; c++)
                {
                    int i = r * columns + c;
                    double score = Math.Sqrt(Math.Clamp(cls[i], 0f, 1f) * Math.Clamp(obj[i], 0f, 1f));
                    if (score < FaceScore)
                        continue;
                    double cx = (c + box[i * 4]) * stride, cy = (r + box[i * 4 + 1]) * stride;
                    double bw = Math.Exp(box[i * 4 + 2]) * stride, bh = Math.Exp(box[i * 4 + 3]) * stride;
                    candidates.Add(new BlurSighting(BlurKind.Face, (cx - bw / 2) / w, (cy - bh / 2) / h, bw / w, bh / h, score));
                }
        }
        return Best(candidates);
    }

    private List<BlurSighting> Plates(byte[] bgr, int width, int height)
    {
        int w = _plateWidth, h = _plateHeight;
        var input = Planes(bgr, width, height, w, h);
        var name = _plate!.InputMetadata.Keys.First();
        using var outputs = _plate.Run([NamedOnnxValue.CreateFromTensor(name, new DenseTensor<float>(input, [1, 3, h, w]))]);
        var byName = outputs.ToDictionary(o => o.Name, o => o.AsTensor<float>());
        var loc = byName["loc"].ToArray();
        var conf = byName["conf"].ToArray();
        var iou = byName["iou"].ToArray();
        var priors = Priors(w, h);
        var candidates = new List<BlurSighting>();
        for (int i = 0; i < priors.Count; i++)
        {
            double score = Math.Sqrt(Math.Max(0, conf[i * 2 + 1]) * Math.Clamp(iou[i], 0f, 1f));
            if (score < PlateScore)
                continue;
            var (cx, cy, sw, sh) = priors[i];
            // Four corners, in shares of the picture; the box is around them.
            double left = 1, top = 1, right = 0, bottom = 0;
            foreach (int a in new[] { 4, 6, 10, 12 })
            {
                double x = cx + loc[i * 14 + a] * 0.1 * sw, y = cy + loc[i * 14 + a + 1] * 0.1 * sh;
                left = Math.Min(left, x);
                right = Math.Max(right, x);
                top = Math.Min(top, y);
                bottom = Math.Max(bottom, y);
            }
            if (right > left && bottom > top)
                candidates.Add(new BlurSighting(BlurKind.Plate, left, top, right - left, bottom - top, score));
        }
        return Best(candidates);
    }

    /// <summary>Where the plate model looks: at each of its four scales, a few box sizes in each cell.</summary>
    private static List<(double X, double Y, double W, double H)> Priors(int w, int h)
    {
        int rows = ((h + 1) / 2) / 2, columns = ((w + 1) / 2) / 2;
        int[] steps = [8, 16, 32, 64];
        int[][] sizes = [[10, 16, 24], [32, 48], [64, 96], [128, 192, 256]];
        var priors = new List<(double, double, double, double)>();
        for (int k = 0; k < 4; k++)
        {
            rows /= 2;
            columns /= 2;
            for (int i = 0; i < rows; i++)
                for (int j = 0; j < columns; j++)
                    foreach (int m in sizes[k])
                        priors.Add(((j + 0.5) * steps[k] / w, (i + 0.5) * steps[k] / h, (double)m / w, (double)m / h));
        }
        return priors;
    }

    /// <summary>Of boxes on the same place, the surest.</summary>
    internal static List<BlurSighting> Best(List<BlurSighting> candidates)
    {
        var kept = new List<BlurSighting>();
        foreach (var candidate in candidates.OrderByDescending(c => c.Score))
            if (!kept.Any(k => k.Overlap(candidate) > SameThing))
                kept.Add(candidate);
        return kept;
    }
}
