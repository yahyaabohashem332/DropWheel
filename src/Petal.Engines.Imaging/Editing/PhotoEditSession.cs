using ImageMagick;
using Petal.Core.Formats;
using Petal.Engines.Imaging.Editing;

namespace Petal.Engines.Imaging;

/// <summary>
/// Edit Photo: loads an image once, keeps a downscaled working copy for live preview, and exports
/// at full resolution with exactly the same adjustment math (radii, vignette and grain scale with
/// the image, so the export looks like the preview). Rendering is thread-safe.
/// </summary>
public sealed class PhotoEditSession : IDisposable
{
    private readonly PhotoFrame _preview;
    private readonly Lock _renderGate = new();
    private bool _disposed;

    private PhotoEditSession(string path, FileFormat? format, int width, int height, PhotoFrame preview)
    {
        SourcePath = path;
        SourceFormat = format;
        SourceWidth = width;
        SourceHeight = height;
        _preview = preview;
        OriginalPreview = new BgraBitmap(preview.Width, preview.Height, (byte[])preview.Pixels.Clone());
    }

    public string SourcePath { get; }

    public FileFormat? SourceFormat { get; }

    /// <summary>Full-resolution size, upright.</summary>
    public int SourceWidth { get; }

    public int SourceHeight { get; }

    public int PreviewWidth => _preview.Width;

    public int PreviewHeight => _preview.Height;

    /// <summary>The unedited preview (for "Original" comparison).</summary>
    public BgraBitmap OriginalPreview { get; }

    /// <summary>Opens <paramref name="path"/> with a preview at most <paramref name="previewMaxSize"/> px on the long edge.</summary>
    public static Task<PhotoEditSession> OpenAsync(string path, int previewMaxSize = 1600, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            var format = ImageFormats.Detect(path);
            var info = ImageInspector.Read(path);
            using var image = ImageLoader.Load(path, format, new LoadOptions { DecodeSizeHint = previewMaxSize });
            cancellationToken.ThrowIfCancellationRequested();
            if (image.Width > previewMaxSize || image.Height > previewMaxSize)
                image.Resize(new MagickGeometry((uint)previewMaxSize, (uint)previewMaxSize));
            var pixels = ImagePreview.ToBgra(image).Pixels;
            var frame = new PhotoFrame((int)image.Width, (int)image.Height, pixels);
            // Build every base layer now so the first drag of any slider is already fast.
            frame.PrepareAll();
            int width = info.Format == Petal.Core.Formats.Formats.Svg ? (int)image.Width : info.DisplayWidth;
            int height = info.Format == Petal.Core.Formats.Formats.Svg ? (int)image.Height : info.DisplayHeight;
            return new PhotoEditSession(path, format, width, height, frame);
        }, cancellationToken);

    /// <summary>Renders the preview with <paramref name="adjustments"/> into a new buffer.</summary>
    public BgraBitmap RenderPreview(PhotoAdjustments adjustments)
    {
        var bitmap = new BgraBitmap(PreviewWidth, PreviewHeight);
        RenderPreview(adjustments, bitmap.Pixels, bitmap.Stride);
        return bitmap;
    }

    /// <summary>Renders into a caller buffer (e.g. a reused array), BGRA rows of <paramref name="stride"/> bytes.</summary>
    public unsafe void RenderPreview(PhotoAdjustments adjustments, Span<byte> destination, int stride)
    {
        if (stride < PreviewWidth * 4 || destination.Length < (long)stride * (PreviewHeight - 1) + PreviewWidth * 4)
            throw new ArgumentException("Destination is smaller than the preview", nameof(destination));
        fixed (byte* p = destination)
            RenderPreview(adjustments, (nint)p, stride);
    }

    /// <summary>Renders straight into unmanaged memory such as <c>WriteableBitmap.BackBuffer</c> (lock it first).</summary>
    public unsafe void RenderPreview(PhotoAdjustments adjustments, nint destination, int stride)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_renderGate)
        {
            var plan = RenderPlan.Create(adjustments, _preview.LongEdge, NeedsAirlight(adjustments) ? _preview.Airlight : 1f);
            if (plan.IsIdentity)
            {
                // Neutral is exactly the original.
                for (int y = 0; y < PreviewHeight; y++)
                    _preview.Pixels.AsSpan(y * PreviewWidth * 4, PreviewWidth * 4).CopyTo(new Span<byte>((byte*)destination + (long)y * stride, PreviewWidth * 4));
                return;
            }
            PhotoRenderer.Render(_preview, plan, (byte*)destination, stride);
        }
    }

    /// <summary>Histogram of a rendered preview.</summary>
    public static ImageHistogram Histogram(BgraBitmap rendered) => ImageHistogram.Compute(rendered);

    /// <summary>
    /// Applies <paramref name="adjustments"/> to the full-resolution image and saves it in the
    /// output path's format (default: the original's), keeping metadata and colour profile.
    /// </summary>
    public Task ExportAsync(string outputPath, PhotoAdjustments adjustments, IProgress<double>? progress = null,
        CancellationToken cancellationToken = default) =>
        Task.Run(async () =>
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            using var image = ImageLoader.Load(SourcePath, SourceFormat, new LoadOptions { SvgDensity = 192 });
            progress?.Report(0.2);
            cancellationToken.ThrowIfCancellationRequested();
            ApplyFullResolution(image, adjustments);
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(0.7);
            var target = ToolSupport.OutputFormat(outputPath, SourceFormat);
            await ImageWriter.WriteAsync(image, outputPath, target, ToolSupport.WriteOptions(image, SourceFormat, target), cancellationToken);
            progress?.Report(1);
        }, cancellationToken);

    internal static unsafe void ApplyFullResolution(MagickImage image, PhotoAdjustments adjustments)
    {
        if (adjustments.IsNeutral)
            return;
        int w = (int)image.Width, h = (int)image.Height;
        bool hadAlpha = image.HasAlpha;
        var pixels = ImagePreview.ToBgra(image).Pixels;
        var frame = new PhotoFrame(w, h, pixels);
        var plan = RenderPlan.Create(adjustments, frame.LongEdge, NeedsAirlight(adjustments) ? frame.Airlight : 1f);
        fixed (byte* p = pixels)
            PhotoRenderer.Render(frame, plan, p, w * 4); // in place: each pixel is read before it is written

        if (image.ColorSpace is ColorSpace.Gray or ColorSpace.LinearGray)
            image.ColorSpace = ColorSpace.sRGB; // colour edits on a grey photo produce colour
        image.ColorType = hadAlpha ? ColorType.TrueColorAlpha : ColorType.TrueColor;
        image.ImportPixels(pixels, new PixelImportSettings((uint)w, (uint)h, StorageType.Char, PixelMapping.BGRA));
        if (!hadAlpha)
            image.Alpha(AlphaAction.Off);
        MetadataHygiene.AfterGeometryChange(image);
    }

    private static bool NeedsAirlight(PhotoAdjustments a) => a.Dehaze != 0;

    public void Dispose() => _disposed = true;
}
