namespace Petal.Engines.Imaging;

/// <summary>256-bin red, green, blue and luminance (Rec. 709) histograms of a BGRA buffer.</summary>
public sealed class ImageHistogram
{
    private ImageHistogram(int[] red, int[] green, int[] blue, int[] luminance)
    {
        Red = red;
        Green = green;
        Blue = blue;
        Luminance = luminance;
        Max = Math.Max(Math.Max(red.Max(), green.Max()), Math.Max(blue.Max(), luminance.Max()));
    }

    public int[] Red { get; }

    public int[] Green { get; }

    public int[] Blue { get; }

    public int[] Luminance { get; }

    /// <summary>The tallest bin across all channels, for scaling the plot.</summary>
    public int Max { get; }

    public static ImageHistogram Compute(BgraBitmap bitmap) => Compute(bitmap.Pixels, bitmap.Width, bitmap.Height, bitmap.Stride);

    public static ImageHistogram Compute(byte[] bgra, int width, int height, int stride)
    {
        var red = new int[256];
        var green = new int[256];
        var blue = new int[256];
        var luma = new int[256];
        var gate = new object();
        Parallel.For(0, (height + 63) / 64,
            () => new int[4 * 256],
            (block, _, local) =>
            {
                int y1 = Math.Min(height, (block + 1) * 64);
                for (int y = block * 64; y < y1; y++)
                {
                    int i = y * stride;
                    for (int x = 0; x < width; x++, i += 4)
                    {
                        int b = bgra[i], g = bgra[i + 1], r = bgra[i + 2];
                        local[r]++;
                        local[256 + g]++;
                        local[512 + b]++;
                        local[768 + ((54 * r + 183 * g + 19 * b) >> 8)]++;
                    }
                }
                return local;
            },
            local =>
            {
                lock (gate)
                {
                    for (int k = 0; k < 256; k++)
                    {
                        red[k] += local[k];
                        green[k] += local[256 + k];
                        blue[k] += local[512 + k];
                        luma[k] += local[768 + k];
                    }
                }
            });
        return new ImageHistogram(red, green, blue, luma);
    }
}
