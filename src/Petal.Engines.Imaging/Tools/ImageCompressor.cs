using ImageMagick;
using Petal.Core.Formats;
using F = Petal.Core.Formats.Formats;

namespace Petal.Engines.Imaging;

public enum CompressionPreset
{
    /// <summary>Smaller files with little visible quality loss.</summary>
    Balanced,

    /// <summary>Much smaller files; PNGs are reduced to a 256-colour palette.</summary>
    Strong,
}

public enum ImageResizeMode
{
    /// <summary>Keep original dimensions.</summary>
    Original,

    /// <summary>Shrink so the longer side is at most <see cref="ImageResize.Value"/> pixels (never enlarges).</summary>
    LongEdge,

    /// <summary>Scale both sides to <see cref="ImageResize.Value"/> percent.</summary>
    Percent,
}

/// <summary>The "Image size" choice of the Compress window.</summary>
public sealed record ImageResize(ImageResizeMode Mode, double Value = 0)
{
    public static ImageResize Original { get; } = new(ImageResizeMode.Original);

    public static ImageResize LongEdge(int pixels) => new(ImageResizeMode.LongEdge, pixels);

    public static ImageResize Percent(double percent) => new(ImageResizeMode.Percent, percent);
}

public sealed record ImageCompressOptions
{
    public CompressionPreset Preset { get; init; } = CompressionPreset.Balanced;

    /// <summary>Compress until the file is at most this many bytes (quality first, then dimensions).</summary>
    public long? TargetBytes { get; init; }

    /// <summary>Alternative to <see cref="TargetBytes"/>: make the file this many percent smaller (1–99).</summary>
    public double? ReductionPercent { get; init; }

    public ImageResize Resize { get; init; } = ImageResize.Original;

    /// <summary>Keep EXIF/XMP/IPTC. The colour profile is always kept; EXIF thumbnails are always dropped.</summary>
    public bool KeepMetadata { get; init; } = true;
}

/// <param name="Quality">The lossy quality used, or null for lossless/palette output.</param>
/// <param name="TargetReached">False when a size target could not be met even at minimum quality and size.</param>
/// <param name="KeptOriginal">Compression could not beat the original, so its bytes were copied unchanged.</param>
public sealed record ImageCompressResult(
    string OutputPath,
    long OriginalBytes,
    long OutputBytes,
    int Width,
    int Height,
    int? Quality,
    bool TargetReached,
    bool KeptOriginal)
{
    /// <summary>Fraction of the original size saved, e.g. 0.4 for 40 % smaller.</summary>
    public double Savings => OriginalBytes == 0 ? 0 : 1 - (double)OutputBytes / OriginalBytes;
}

public sealed record ImageCompressBatchResult(ImageJobItem Item, ImageCompressResult? Result, string? Error);

/// <summary>Compress tool for images.</summary>
public static class ImageCompressor
{
    private const int MinSearchQuality = 20;
    private const int MaxSearchQuality = 92;
    private const int MinSide = 16;

    public static Task<ImageCompressResult> CompressAsync(string inputPath, string outputPath, ImageCompressOptions? options = null,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default) =>
        Task.Run(() => RunAsync(inputPath, outputPath, options ?? new ImageCompressOptions(), progress, cancellationToken), cancellationToken);

    public static async Task<IReadOnlyList<ImageCompressBatchResult>> CompressBatchAsync(IReadOnlyList<ImageJobItem> items,
        ImageCompressOptions? options = null, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var results = new ImageCompressResult?[items.Count];
        var outcome = await ToolSupport.RunBatchAsync(Enumerable.Range(0, items.Count).ToList(),
            i => (items[i].InputPath, items[i].OutputPath),
            async (i, p, ct) => results[i] = await CompressAsync(items[i].InputPath, items[i].OutputPath, options, p, ct),
            progress, cancellationToken);
        return outcome.Select((r, i) => new ImageCompressBatchResult(items[i], results[i], r.Error)).ToList();
    }

    /// <summary>Default quality of each preset for a lossy format.</summary>
    public static int PresetQuality(FileFormat format, CompressionPreset preset) => (format.Id, preset) switch
    {
        ("jpg", CompressionPreset.Balanced) => 80,
        ("jpg", _) => 62,
        ("webp", CompressionPreset.Balanced) => 80,
        ("webp", _) => 60,
        ("heic", CompressionPreset.Balanced) => 70,
        ("heic", _) => 50,
        ("avif", CompressionPreset.Balanced) => 65,
        ("avif", _) => 45,
        _ => preset == CompressionPreset.Balanced ? 85 : 70,
    };

    private sealed record Encoded(byte[] Bytes, int? Quality, int Width, int Height);

    private static async Task<ImageCompressResult> RunAsync(string inputPath, string outputPath, ImageCompressOptions options,
        IProgress<double>? progress, CancellationToken ct)
    {
        var source = ImageFormats.Detect(inputPath) ?? throw new NotSupportedException("Unknown image format");
        if (source == F.Svg || source == F.Gif || source == F.Pdf)
            throw new NotSupportedException($"{source.Label} files can't be compressed as images");
        long originalBytes = new FileInfo(inputPath).Length;
        var target = ToolSupport.OutputFormat(outputPath, source);

        using var image = ImageLoader.Load(inputPath, source);
        progress?.Report(0.1);
        int sourceQuality = source == F.Jpg && image.Quality is >= 1 and <= 100 ? (int)image.Quality : 0;
        bool resized = ApplyResize(image, options.Resize);
        RemoveExifThumbnail(image);

        long? targetBytes = options.TargetBytes
            ?? (options.ReductionPercent is double pct ? (long)(originalBytes * (1 - Math.Clamp(pct, 1, 99) / 100)) : null);
        // Copying the original is only an option when its metadata may stay.
        bool sameFile = !resized && target == source && options.KeepMetadata;

        Encoded result;
        bool reached = true;
        if (targetBytes is long limit && sameFile && originalBytes <= limit)
        {
            result = new Encoded(await File.ReadAllBytesAsync(inputPath, ct), null, (int)image.Width, (int)image.Height);
        }
        else if (targetBytes is long t)
        {
            (result, reached) = await FitAsync(image, target, options, Math.Max(1, t), progress, ct);
        }
        else
        {
            int quality = PresetQuality(target, options.Preset);
            if (target == source && sourceQuality >= 30)
                quality = Math.Min(quality, sourceQuality - 2);
            result = await EncodePresetAsync(image, target, options, quality, ct);
        }
        progress?.Report(0.9);

        bool keptOriginal = false;
        if (sameFile && result.Bytes.LongLength >= originalBytes)
        {
            result = new Encoded(await File.ReadAllBytesAsync(inputPath, ct), null, result.Width, result.Height);
            keptOriginal = true;
        }

        ct.ThrowIfCancellationRequested();
        AtomicFile.WriteAllBytes(outputPath, result.Bytes);
        progress?.Report(1);
        return new ImageCompressResult(outputPath, originalBytes, result.Bytes.LongLength, result.Width, result.Height,
            keptOriginal ? null : result.Quality, reached && (targetBytes is null || result.Bytes.LongLength <= targetBytes), keptOriginal);
    }

    private static async Task<Encoded> EncodePresetAsync(MagickImage image, FileFormat format, ImageCompressOptions options, int quality,
        CancellationToken ct)
    {
        bool strong = options.Preset == CompressionPreset.Strong;
        switch (format.Id)
        {
            case "png":
                var lossless = await BestLosslessPngAsync(image, options, ct);
                if (!strong)
                    return lossless;
                var palette = await EncodeAsync(image, format, Base(options) with { Palette = true, PngCompressionLevel = 9 }, null, ct);
                return palette.Bytes.Length < lossless.Bytes.Length ? palette : lossless;

            case "tiff":
                return strong && !image.HasAlpha
                    ? await EncodeAsync(image, format, Base(options) with { Tiff = TiffMode.Jpeg, Quality = quality }, quality, ct)
                    : await EncodeAsync(image, format, Base(options) with { Tiff = TiffMode.Zip }, null, ct);

            case "bmp":
                return await EncodeAsync(image, format, Base(options) with { Palette = strong || image.TotalColors <= 256 }, null, ct);

            case "ico":
                return await EncodeAsync(image, format, Base(options), null, ct);

            default: // jpg, webp, heic, avif
                // One encode, so spend effort on size: slower libwebp/aom settings.
                return await EncodeAsync(image, format, Base(options) with { Quality = quality, WebpMethod = 5, AvifSpeed = 6 }, quality, ct);
        }
    }

    /// <summary>Searches quality, then dimensions, for the best result within <paramref name="limit"/> bytes.</summary>
    private static async Task<(Encoded, bool)> FitAsync(MagickImage original, FileFormat format, ImageCompressOptions options, long limit,
        IProgress<double>? progress, CancellationToken ct)
    {
        Encoded? smallest = null;
        double scale = 1;
        MagickImage? scaled = null;
        try
        {
            for (int attempt = 0; attempt < 6; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                var image = scaled ?? original;
                var (fit, floor) = await FitAtSizeAsync(image, format, options, limit, ct);
                progress?.Report(0.1 + 0.8 * (attempt + 1) / 6);
                if (fit is not null)
                    return (fit, true);
                if (smallest is null || floor.Bytes.Length < smallest.Bytes.Length)
                    smallest = floor;

                // Pixels scale roughly with file size; aim a little under the target.
                scale *= Math.Clamp(Math.Sqrt((double)limit / floor.Bytes.Length) * 0.92, 0.1, 0.95);
                uint w = (uint)Math.Max(MinSide, Math.Round(original.Width * scale));
                uint h = (uint)Math.Max(MinSide, Math.Round(original.Height * scale));
                if (scaled is not null && w >= scaled.Width)
                    break;
                scaled?.Dispose();
                scaled = (MagickImage)original.Clone();
                scaled.Resize(new MagickGeometry(w, h) { IgnoreAspectRatio = true });
                MetadataHygiene.AfterGeometryChange(scaled);
                if (w == MinSide || h == MinSide)
                {
                    var (last, lastFloor) = await FitAtSizeAsync(scaled, format, options, limit, ct);
                    return last is not null ? (last, true) : (lastFloor.Bytes.Length < smallest.Bytes.Length ? lastFloor : smallest, false);
                }
            }
            return (smallest!, false);
        }
        finally
        {
            scaled?.Dispose();
        }
    }

    /// <summary>Best encoding within the limit at the current size, or null plus the smallest attempt.</summary>
    private static async Task<(Encoded? Fit, Encoded Floor)> FitAtSizeAsync(MagickImage image, FileFormat format, ImageCompressOptions options,
        long limit, CancellationToken ct)
    {
        bool lossyQuality = format == F.Jpg || format == F.Webp || format == F.Heic || format == F.Avif ||
                            (format == F.Tiff && !image.HasAlpha);
        if (lossyQuality)
        {
            var opts = Base(options) with { Tiff = TiffMode.Jpeg, WebpMethod = 4 };
            var top = await EncodeAsync(image, format, opts with { Quality = MaxSearchQuality }, MaxSearchQuality, ct);
            if (top.Bytes.Length <= limit)
                return (top, top);
            int lo = MinSearchQuality, hi = MaxSearchQuality;
            var bottom = await EncodeAsync(image, format, opts with { Quality = lo }, lo, ct);
            if (bottom.Bytes.Length > limit)
                return (null, bottom);
            var best = bottom;
            // Invariant: quality lo fits, quality hi doesn't.
            while (hi - lo > 2)
            {
                int mid = (lo + hi) / 2;
                var attempt = await EncodeAsync(image, format, opts with { Quality = mid }, mid, ct);
                if (attempt.Bytes.Length <= limit)
                {
                    lo = mid;
                    best = attempt;
                }
                else
                {
                    hi = mid;
                }
            }
            return (best, bottom);
        }

        if (format == F.Png || format == F.Bmp)
        {
            var lossless = format == F.Png
                ? await BestLosslessPngAsync(image, options, ct)
                : await EncodeAsync(image, format, Base(options), null, ct);
            if (lossless.Bytes.Length <= limit)
                return (lossless, lossless);
            Encoded floor = lossless;
            foreach (var colors in new[] { 256, 128, 64, 32 })
            {
                var attempt = await EncodeAsync(image, format, Base(options) with { Palette = true, PaletteColors = colors, PngCompressionLevel = 9 }, null, ct);
                if (attempt.Bytes.Length <= limit)
                    return (attempt, attempt);
                if (attempt.Bytes.Length < floor.Bytes.Length)
                    floor = attempt;
            }
            return (null, floor);
        }

        var plain = await EncodeAsync(image, format, Base(options) with { Tiff = TiffMode.Zip }, null, ct);
        return plain.Bytes.Length <= limit ? (plain, plain) : (null, plain);
    }

    /// <summary>
    /// Lossless PNG: graphics (few colours) try the unfiltered and adaptive filters; photos use
    /// adaptive filtering, at maximum zlib effort when the image is small enough to stay quick.
    /// </summary>
    private static async Task<Encoded> BestLosslessPngAsync(MagickImage image, ImageCompressOptions options, CancellationToken ct)
    {
        long pixels = (long)image.Width * image.Height;
        bool graphic = image.TotalColors <= 4096;
        int level = pixels <= 4_000_000 || graphic ? 9 : 7;
        var variants = graphic ? new[] { (0, 0), (5, 1) } : [(5, 1)];
        Encoded? best = null;
        foreach (var (filter, strategy) in variants)
        {
            var attempt = await EncodeAsync(image, F.Png, Base(options) with { PngCompressionLevel = level, PngFilter = filter, PngStrategy = strategy }, null, ct);
            if (best is null || attempt.Bytes.Length < best.Bytes.Length)
                best = attempt;
        }
        return best!;
    }

    // AVIF speed 8 keeps the quality search quick; the final file is encoded with the same speed so the size holds.
    private static ImageWriteOptions Base(ImageCompressOptions options) => new() { KeepMetadata = options.KeepMetadata, AvifSpeed = 8 };

    private static async Task<Encoded> EncodeAsync(MagickImage image, FileFormat format, ImageWriteOptions options, int? quality, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var bytes = await ImageWriter.EncodeToBytesAsync(image, format, options, ct);
        return new Encoded(bytes, quality, (int)image.Width, (int)image.Height);
    }

    private static bool ApplyResize(MagickImage image, ImageResize resize)
    {
        uint w = image.Width, h = image.Height;
        switch (resize.Mode)
        {
            case ImageResizeMode.LongEdge when resize.Value >= 1 && Math.Max(w, h) > resize.Value:
                double s = resize.Value / Math.Max(w, h);
                w = (uint)Math.Max(1, Math.Round(w * s));
                h = (uint)Math.Max(1, Math.Round(h * s));
                break;
            case ImageResizeMode.Percent when resize.Value > 0 && Math.Abs(resize.Value - 100) > 0.01:
                w = (uint)Math.Max(1, Math.Round(w * resize.Value / 100));
                h = (uint)Math.Max(1, Math.Round(h * resize.Value / 100));
                break;
            default:
                return false;
        }
        image.Resize(new MagickGeometry(w, h) { IgnoreAspectRatio = true });
        MetadataHygiene.AfterGeometryChange(image);
        return true;
    }

    private static void RemoveExifThumbnail(MagickImage image)
    {
        var exif = image.GetExifProfile();
        if (exif is null || exif.ThumbnailLength == 0)
            return;
        exif.RemoveThumbnail();
        image.SetProfile(exif);
    }
}
