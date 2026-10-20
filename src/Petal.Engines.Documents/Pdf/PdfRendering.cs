using PDFtoImage;
using SkiaSharp;

namespace Petal.Engines.Documents.Pdf;

/// <summary>Page size in points (1/72 inch) as displayed, i.e. with the page's rotation applied.</summary>
public sealed record PdfPageSize(double Width, double Height)
{
    public bool IsLandscape => Width > Height;
}

/// <summary>Raw page pixels: 32-bit BGRA, premultiplied alpha, rows of <see cref="Stride"/> bytes (WPF: PixelFormats.Pbgra32).</summary>
public sealed record PdfPageBitmap(int Width, int Height, int Stride, byte[] Pixels);

/// <summary>
/// A PDF opened for fast previews (Organize window thumbnails). The file is read once into
/// memory; every render goes through PDFium. Safe to call from any thread.
/// </summary>
public sealed class PdfDocumentView : IDisposable
{
    private readonly byte[] _bytes;
    private readonly string? _password;

    private PdfDocumentView(string path, byte[] bytes, string? password)
    {
        Path = path;
        _bytes = bytes;
        _password = password;
        lock (PdfRaster.Gate)
        {
            PageSizes = Conversion.GetPageSizes(_bytes, _password).Select(s => new PdfPageSize(s.Width, s.Height)).ToList();
        }
    }

    /// <summary>Opens a PDF. Throws <see cref="PdfOpenException"/> for damaged files, and for protected ones without the right password (<see cref="PdfOpenException.NeedsPassword"/>).</summary>
    public static PdfDocumentView Open(string path, string? password = null)
    {
        var bytes = File.ReadAllBytes(path);
        try
        {
            return new PdfDocumentView(path, bytes, password);
        }
        catch (PDFtoImage.Exceptions.PdfPasswordProtectedException ex)
        {
            throw new PdfOpenException($"{System.IO.Path.GetFileName(path)} is protected by a password.", ex) { NeedsPassword = true };
        }
        catch (PDFtoImage.Exceptions.PdfException ex)
        {
            throw new PdfOpenException($"{System.IO.Path.GetFileName(path)} could not be opened: {ex.Message}", ex);
        }
    }

    public string Path { get; }

    public long FileSize => _bytes.LongLength;

    public int PageCount => PageSizes.Count;

    public IReadOnlyList<PdfPageSize> PageSizes { get; }

    /// <summary>Renders one page <paramref name="width"/> pixels wide (height follows the aspect ratio).</summary>
    /// <param name="rotation">Extra clockwise rotation for previews: 0, 90, 180 or 270.</param>
    public PdfPageBitmap RenderPage(int pageIndex, int width, int rotation = 0)
    {
        using var bitmap = PdfRaster.RenderWidth(_bytes, _password, pageIndex, width, rotation);
        return PdfRaster.ToPageBitmap(bitmap);
    }

    /// <summary>Renders one page as PNG bytes.</summary>
    public byte[] RenderPagePng(int pageIndex, int width, int rotation = 0)
    {
        using var bitmap = PdfRaster.RenderWidth(_bytes, _password, pageIndex, width, rotation);
        return Util.ImageTools.EncodePng(bitmap);
    }

    /// <summary>Renders several pages, yielding each as soon as it is ready.</summary>
    public IEnumerable<(int PageIndex, PdfPageBitmap Bitmap)> RenderPages(IEnumerable<int> pageIndices, int width, CancellationToken cancellationToken = default)
    {
        foreach (var index in pageIndices)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return (index, RenderPage(index, width));
        }
    }

    public void Dispose()
    {
        // The bytes are managed; nothing native stays open between calls.
    }
}

/// <summary>One-off helpers when a <see cref="PdfDocumentView"/> is not needed.</summary>
public static class PdfInfo
{
    public static int GetPageCount(string path, string? password = null)
    {
        var bytes = File.ReadAllBytes(path);
        lock (PdfRaster.Gate)
            return Conversion.GetPageCount(bytes, password);
    }

    public static IReadOnlyList<PdfPageSize> GetPageSizes(string path, string? password = null)
    {
        var bytes = File.ReadAllBytes(path);
        lock (PdfRaster.Gate)
            return Conversion.GetPageSizes(bytes, password).Select(s => new PdfPageSize(s.Width, s.Height)).ToList();
    }

    public static PdfPageBitmap RenderPage(string path, int pageIndex, int width, int rotation = 0)
    {
        using var view = PdfDocumentView.Open(path);
        return view.RenderPage(pageIndex, width, rotation);
    }

    public static byte[] RenderPagePng(string path, int pageIndex, int width, int rotation = 0)
    {
        using var view = PdfDocumentView.Open(path);
        return view.RenderPagePng(pageIndex, width, rotation);
    }
}

/// <summary>PDFium access for converters. PDFium is not thread-safe, so every call holds <see cref="Gate"/>.</summary>
internal static class PdfRaster
{
    public static readonly Lock Gate = new();

    /// <summary>Upper bound on rendered pixels; a poster at 300 DPI would otherwise need gigabytes.</summary>
    private const double MaxPixels = 100_000_000;
    private const int MaxSide = 16_000;

    public static IReadOnlyList<PdfPageSize> PageSizes(byte[] pdf)
    {
        lock (Gate)
            return Conversion.GetPageSizes(pdf).Select(s => new PdfPageSize(s.Width, s.Height)).ToList();
    }

    /// <summary>The DPI to use for a page so the bitmap stays within memory limits.</summary>
    public static int SafeDpi(PdfPageSize size, int dpi)
    {
        double w = size.Width / 72 * dpi, h = size.Height / 72 * dpi;
        var scale = Math.Min(1, Math.Min(Math.Sqrt(MaxPixels / Math.Max(1, w * h)), Math.Min(MaxSide / Math.Max(1, w), MaxSide / Math.Max(1, h))));
        return Math.Max(18, (int)Math.Floor(dpi * scale));
    }

    public static SKBitmap Render(byte[] pdf, int pageIndex, int dpi, bool annotations = true)
    {
        var sizes = PageSizes(pdf);
        var safe = SafeDpi(sizes[pageIndex], dpi);
        var options = new RenderOptions
        {
            Dpi = safe,
            WithAnnotations = annotations,
            WithFormFill = true,
            AntiAliasing = PdfAntiAliasing.All,
            BackgroundColor = SKColors.White,
            UseTiling = sizes[pageIndex].Width / 72 * safe * sizes[pageIndex].Height / 72 * safe > 40_000_000,
        };
        lock (Gate)
            return Conversion.ToImage(pdf, pageIndex, null, options);
    }

    public static SKBitmap RenderWidth(byte[] pdf, string? password, int pageIndex, int width, int rotation)
    {
        var options = new RenderOptions
        {
            Width = Math.Clamp(width, 8, MaxSide),
            WithAspectRatio = true,
            WithAnnotations = true,
            WithFormFill = true,
            AntiAliasing = PdfAntiAliasing.All,
            BackgroundColor = SKColors.White,
            Rotation = (((rotation % 360) + 360) % 360) switch
            {
                90 => PdfRotation.Rotate90,
                180 => PdfRotation.Rotate180,
                270 => PdfRotation.Rotate270,
                _ => PdfRotation.Rotate0,
            },
        };
        lock (Gate)
            return Conversion.ToImage(pdf, pageIndex, password, options);
    }

    public static PdfPageBitmap ToPageBitmap(SKBitmap bitmap)
    {
        SKBitmap source = bitmap;
        SKBitmap? converted = null;
        if (bitmap.ColorType != SKColorType.Bgra8888 || bitmap.AlphaType != SKAlphaType.Premul)
        {
            converted = new SKBitmap(new SKImageInfo(bitmap.Width, bitmap.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
            bitmap.CopyTo(converted, SKColorType.Bgra8888);
            source = converted;
        }
        try
        {
            return new PdfPageBitmap(source.Width, source.Height, source.RowBytes, source.Bytes);
        }
        finally
        {
            converted?.Dispose();
        }
    }
}
