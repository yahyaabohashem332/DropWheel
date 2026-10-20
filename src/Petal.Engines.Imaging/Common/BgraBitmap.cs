namespace Petal.Engines.Imaging;

/// <summary>
/// A tightly packed 32-bit BGRA pixel buffer with straight (non-premultiplied) alpha,
/// matching WPF's <c>PixelFormats.Bgra32</c>. Row stride is always <c>Width * 4</c>.
/// </summary>
public sealed class BgraBitmap
{
    public BgraBitmap(int width, int height, byte[] pixels)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        if (pixels.Length < (long)width * height * 4)
            throw new ArgumentException("Pixel buffer is smaller than width × height × 4.", nameof(pixels));
        Width = width;
        Height = height;
        Pixels = pixels;
    }

    public BgraBitmap(int width, int height) : this(width, height, new byte[checked(width * height * 4)])
    {
    }

    public int Width { get; }

    public int Height { get; }

    public int Stride => Width * 4;

    public byte[] Pixels { get; }
}

/// <summary>An axis-aligned rectangle in image pixels; X/Y is the top-left corner.</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;

    public int Bottom => Y + Height;

    public bool IsEmpty => Width <= 0 || Height <= 0;

    /// <summary>This rectangle clipped to a <paramref name="width"/> × <paramref name="height"/> image.</summary>
    public PixelRect ClampTo(int width, int height)
    {
        int x0 = Math.Clamp(X, 0, width), y0 = Math.Clamp(Y, 0, height);
        int x1 = Math.Clamp(Right, 0, width), y1 = Math.Clamp(Bottom, 0, height);
        return new PixelRect(x0, y0, Math.Max(0, x1 - x0), Math.Max(0, y1 - y0));
    }

    public PixelRect Union(PixelRect other)
    {
        if (IsEmpty) return other;
        if (other.IsEmpty) return this;
        int x0 = Math.Min(X, other.X), y0 = Math.Min(Y, other.Y);
        return new PixelRect(x0, y0, Math.Max(Right, other.Right) - x0, Math.Max(Bottom, other.Bottom) - y0);
    }

    public PixelRect Inflate(int dx, int dy) => new(X - dx, Y - dy, Width + 2 * dx, Height + 2 * dy);
}
