using ImageMagick;

namespace Petal.Engines.Imaging;

/// <summary>Windows icons: a square, transparent-padded image at every standard size up to 256 px.</summary>
internal static class IcoWriter
{
    internal static readonly int[] StandardSizes = [256, 128, 64, 48, 32, 24, 16];

    public static void Write(MagickImage image, Stream output, ImageWriteOptions options)
    {
        int side = (int)Math.Max(image.Width, image.Height);
        var sizes = SizesFor(side);
        int largest = sizes[0];

        using var square = (MagickImage)image.Clone();
        MetadataHygiene.Strip(square, keepColorProfile: false);
        // Shrink a big source to the largest icon size before padding it square, so a photo
        // never becomes a huge square canvas; the smaller sizes come from that master.
        if (side > largest)
        {
            square.Thumbnail(new MagickGeometry((uint)largest, (uint)largest));
            square.ResetPage();
        }
        if (!square.HasAlpha)
            square.Alpha(AlphaAction.Set);
        square.BackgroundColor = MagickColors.Transparent;
        uint masterSide = Math.Max(square.Width, square.Height);
        square.Extent(masterSide, masterSide, Gravity.Center);

        using var frames = new MagickImageCollection();
        foreach (var size in sizes)
        {
            var frame = (MagickImage)square.Clone();
            if (frame.Width != size)
                frame.Resize((uint)size, (uint)size, FilterType.Lanczos);
            frames.Add(frame);
        }
        // ImageMagick stores the 256 px frame as PNG (Vista-style icons), smaller ones as bitmaps.
        frames.Write(output, MagickFormat.Ico);
    }

    internal static IReadOnlyList<int> SizesFor(int side)
    {
        var sizes = StandardSizes.Where(s => s <= side).ToList();
        if (side < 256 && !sizes.Contains(side))
            sizes.Insert(0, side);
        return sizes;
    }
}
