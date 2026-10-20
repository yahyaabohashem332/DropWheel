using System.Numerics;
using System.Runtime.InteropServices;

namespace Petal.Engines.Imaging.Editing;

/// <summary>
/// Repeated box blur on a float plane: three passes approximate a Gaussian, and the cost per
/// pixel does not depend on the radius, so large "base layer" blurs stay cheap at full resolution.
/// </summary>
internal static class BoxBlur
{
    private const int ColumnBlock = 64;

    /// <summary>Blurs <paramref name="data"/> in place.</summary>
    public static void Apply(float[] data, int width, int height, int radius, int passes = 3)
    {
        if (radius < 1)
            return;
        radius = Math.Min(radius, Math.Max(width, height));
        var temp = new float[data.Length];
        for (int p = 0; p < passes; p++)
        {
            Horizontal(data, temp, width, height, radius);
            Vertical(temp, data, width, height, radius);
        }
    }

    /// <summary>Box radius whose three-pass blur matches a Gaussian of <paramref name="sigma"/>.</summary>
    public static int RadiusForSigma(double sigma) =>
        Math.Max(1, (int)Math.Round((Math.Sqrt(4 * sigma * sigma + 1) - 1) / 2));

    private static void Horizontal(float[] src, float[] dst, int width, int height, int r)
    {
        float inv = 1f / (2 * r + 1);
        Parallel.For(0, height, y =>
        {
            var row = src.AsSpan(y * width, width);
            var output = dst.AsSpan(y * width, width);
            float sum = 0;
            for (int i = -r; i <= r; i++)
                sum += row[Math.Clamp(i, 0, width - 1)];
            int last = width - 1;
            for (int x = 0; x < width; x++)
            {
                output[x] = sum * inv;
                int add = x + r + 1, sub = x - r;
                sum += row[add > last ? last : add] - row[sub < 0 ? 0 : sub];
            }
        });
    }

    private static void Vertical(float[] src, float[] dst, int width, int height, int r)
    {
        float inv = 1f / (2 * r + 1);
        int blocks = (width + ColumnBlock - 1) / ColumnBlock;
        Parallel.For(0, blocks, block =>
        {
            int x0 = block * ColumnBlock, count = Math.Min(ColumnBlock, width - x0);
            Span<float> sums = stackalloc float[ColumnBlock];
            sums = sums[..count];
            sums.Clear();
            for (int i = -r; i <= r; i++)
                Add(sums, src.AsSpan(Math.Clamp(i, 0, height - 1) * width + x0, count), 1);

            var vInv = new Vector<float>(inv);
            for (int y = 0; y < height; y++)
            {
                var output = dst.AsSpan(y * width + x0, count);
                int x = 0;
                if (Vector.IsHardwareAccelerated)
                {
                    var sumVectors = MemoryMarshal.Cast<float, Vector<float>>(sums);
                    var outVectors = MemoryMarshal.Cast<float, Vector<float>>(output);
                    for (int v = 0; v < sumVectors.Length; v++)
                        outVectors[v] = sumVectors[v] * vInv;
                    x = sumVectors.Length * Vector<float>.Count;
                }
                for (; x < count; x++)
                    output[x] = sums[x] * inv;

                int add = Math.Min(y + r + 1, height - 1), sub = Math.Max(y - r, 0);
                Add(sums, src.AsSpan(add * width + x0, count), 1);
                Add(sums, src.AsSpan(sub * width + x0, count), -1);
            }
        });
    }

    private static void Add(Span<float> sums, ReadOnlySpan<float> row, float sign)
    {
        int x = 0;
        if (Vector.IsHardwareAccelerated)
        {
            var s = MemoryMarshal.Cast<float, Vector<float>>(sums);
            var rv = MemoryMarshal.Cast<float, Vector<float>>(row);
            var signV = new Vector<float>(sign);
            for (int v = 0; v < s.Length; v++)
                s[v] += rv[v] * signV;
            x = s.Length * Vector<float>.Count;
        }
        for (; x < sums.Length; x++)
            sums[x] += row[x] * sign;
    }
}
