using F = Petal.Core.Formats.Formats;

namespace Petal.Engines.Imaging;

public enum PdfPageSize
{
    /// <summary>Each page is exactly the size of its image (at the image's DPI, 72 when unknown).</summary>
    FitImage,
    A4,
    Letter,
}

public sealed record PdfCreateOptions
{
    public PdfPageSize PageSize { get; init; } = PdfPageSize.FitImage;

    /// <summary>White border on A4/Letter pages, in points (1/72 inch).</summary>
    public double MarginPoints { get; init; } = 36;

    /// <summary>Quality used when a lossy image (WEBP, HEIC, AVIF) must be re-encoded as JPEG. JPEG files are embedded unchanged.</summary>
    public int JpegQuality { get; init; } = 90;

    public string? Title { get; init; }
}

/// <summary>Create PDF tool: one page per image, in the given order.</summary>
public static class PdfCreator
{
    private const double A4Width = 595.276, A4Height = 841.89, LetterWidth = 612, LetterHeight = 792;

    /// <summary>Writes the PDF and returns its page count. The output file must not exist.</summary>
    public static Task<int> CreateAsync(IReadOnlyList<string> imagePaths, string outputPath, PdfCreateOptions? options = null,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            if (imagePaths.Count == 0)
                throw new ArgumentException("No images to put in the PDF.", nameof(imagePaths));
            options ??= new PdfCreateOptions();

            using var file = AtomicFile.Create(outputPath);
            using (var fs = new FileStream(file.TempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16))
            {
                var pdf = new PdfDocumentWriter(fs);
                for (int i = 0; i < imagePaths.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var page = LoadPage(imagePaths[i], options);
                    AddPage(pdf, page, options);
                    progress?.Report((i + 1.0) / (imagePaths.Count + 1));
                }
                pdf.Finish(options.Title);
            }
            cancellationToken.ThrowIfCancellationRequested();
            file.Commit();
            progress?.Report(1);
            return imagePaths.Count;
        }, cancellationToken);

    private static PdfImage LoadPage(string path, PdfCreateOptions options)
    {
        var format = ImageFormats.Detect(path);
        if (format == F.Jpg && PdfImage.TryJpegPassthrough(path) is { } passthrough)
            return passthrough;

        // SVGs are rasterised at 300 dpi so they stay sharp when printed.
        using var image = ImageLoader.Load(path, format, new LoadOptions { SvgDensity = 300 });
        if (format == F.Svg)
            image.Density = new ImageMagick.Density(300, ImageMagick.DensityUnit.PixelsPerInch);
        ImageWriter.FlattenOnWhite(image);
        bool lossy = format is not null && ImageFormats.IsLossy(format);
        return PdfImage.FromMagick(image, preferJpeg: lossy, options.JpegQuality, keepIcc: true);
    }

    private static void AddPage(PdfDocumentWriter pdf, PdfImage image, PdfCreateOptions options)
    {
        if (options.PageSize == PdfPageSize.FitImage)
        {
            pdf.AddFittedPage(image);
            return;
        }

        var (shortSide, longSide) = options.PageSize == PdfPageSize.A4 ? (A4Width, A4Height) : (LetterWidth, LetterHeight);
        // Landscape images get landscape pages.
        bool landscape = image.Width > image.Height;
        double pageW = landscape ? longSide : shortSide, pageH = landscape ? shortSide : longSide;
        double margin = Math.Clamp(options.MarginPoints, 0, Math.Min(pageW, pageH) / 3);
        double boxW = pageW - 2 * margin, boxH = pageH - 2 * margin;
        var (naturalW, naturalH) = image.NaturalPageSize();
        double scale = Math.Min(boxW / naturalW, boxH / naturalH);
        double drawW = naturalW * scale, drawH = naturalH * scale;
        pdf.AddPage(image, pageW, pageH, (pageW - drawW) / 2, (pageH - drawH) / 2, drawW, drawH);
    }
}
