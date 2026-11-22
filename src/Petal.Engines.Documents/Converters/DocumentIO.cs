using System.Collections.Concurrent;
using PDFtoImage;
using Petal.Core.Formats;
using Petal.Core.Jobs;
using Petal.Engines.Documents.IO;
using Petal.Engines.Documents.Model;
using Petal.Engines.Documents.Office;
using Petal.Engines.Documents.Pdf;
using Petal.Engines.Documents.Util;
using SkiaSharp;
using F = Petal.Core.Formats.Formats;

namespace Petal.Engines.Documents.Converters;

/// <summary>Format dispatch for the document model.</summary>
internal static class DocumentIO
{
    public static bool CanRead(FileFormat f) => f == F.Docx || f == F.Odt || f == F.Rtf || f == F.Md || f == F.Html || f == F.Txt || f == F.Epub || f == F.Idml;

    public static bool CanWrite(FileFormat f) => f == F.Docx || f == F.Odt || f == F.Rtf || f == F.Md || f == F.Html || f == F.Txt || f == F.Epub;

    public static DocModel Read(string path, FileFormat format) => format.Id switch
    {
        "docx" => DocxReader.Read(path),
        "odt" => OdtReader.Read(path),
        "rtf" => RtfReader.Read(path),
        "md" => MarkdownReader.Read(path),
        "html" => HtmlReader.Read(path),
        "txt" => PlainTextIO.Read(path),
        "epub" => Epub.Read(path),
        "idml" => IdmlReader.Read(path),
        _ => throw new NotSupportedException($"Cannot read {format.Label} documents."),
    };

    /// <summary>Writes the model; returns every path produced (Markdown may add an images folder).</summary>
    public static IReadOnlyList<string> Write(DocModel doc, FileFormat format, string outputPath)
    {
        string? extra = null;
        SafeOutput.Write(outputPath, temp =>
        {
            switch (format.Id)
            {
                case "docx": DocxWriter.Write(doc, temp); break;
                case "odt": OdtWriter.Write(doc, temp); break;
                case "rtf": RtfWriter.Write(doc, temp); break;
                case "md": extra = MarkdownWriter.Write(doc, temp, outputPath); break;
                case "html": HtmlWriter.Write(doc, temp); break;
                case "txt": PlainTextIO.Write(doc, temp); break;
                case "epub":
                    doc.Metadata.Title ??= doc.EffectiveTitle() ?? F.BaseName(outputPath);
                    Epub.Write(doc, temp);
                    break;
                case "pdf": PdfDocWriter.Write(doc, temp); break;
                default: throw new NotSupportedException($"Cannot write {format.Label} documents.");
            }
        });
        return extra is null ? [outputPath] : [outputPath, extra];
    }
}

/// <summary>Exact-layout PDF through Word/Excel/PowerPoint or LibreOffice, when available.</summary>
internal static class HighFidelity
{
    public static OfficeApp? AppFor(FileFormat from) =>
        from == F.Docx || from == F.Odt || from == F.Rtf ? OfficeApp.Word :
        from == F.Xlsx || from == F.Xls || from == F.Ods ? OfficeApp.Excel :
        from == F.Pptx || from == F.Ppt ? OfficeApp.PowerPoint : null;

    public static bool OfficeAvailable(OfficeApp app, DocumentEngineOptions options) =>
        options.UseMicrosoftOffice && OfficeAutomation.IsInstalled(app);

    public static bool LibreOfficeAvailable(DocumentEngineOptions options) =>
        options.UseLibreOffice && LibreOffice.IsInstalled;

    public static bool Available(FileFormat from, DocumentEngineOptions options) =>
        AppFor(from) is { } app && (OfficeAvailable(app, options) || LibreOfficeAvailable(options));

    /// <summary>Exports <paramref name="input"/> to <paramref name="outputPdf"/>. False when no engine is available or all failed.</summary>
    public static async Task<bool> TryPdfAsync(string input, OfficeApp app, string outputPdf, DocumentEngineOptions options, CancellationToken ct, bool throwOnFailure = false)
    {
        Exception? last = null;
        if (OfficeAvailable(app, options))
        {
            try
            {
                await OfficeAutomation.ExportPdfAsync(app, Path.GetFullPath(input), outputPdf, options.OfficeTimeout, ct).ConfigureAwait(false);
                if (File.Exists(outputPdf) && new FileInfo(outputPdf).Length > 0)
                    return true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                last = ex;
            }
        }
        if (LibreOfficeAvailable(options))
        {
            try
            {
                await LibreOffice.ExportPdfAsync(input, outputPdf, options.OfficeTimeout, ct).ConfigureAwait(false);
                if (File.Exists(outputPdf) && new FileInfo(outputPdf).Length > 0)
                    return true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                last = ex;
            }
        }
        if (throwOnFailure && last is not null)
            throw new InvalidOperationException($"Could not convert {Path.GetFileName(input)}: {last.Message}", last);
        return false;
    }
}

/// <summary>PDF pages as PNG or JPEG files: one file, or a folder for several pages.</summary>
internal static class PageImages
{
    public static IReadOnlyList<string> Write(byte[] pdf, string inputPath, string outputPath, bool jpeg, ConversionSettings settings,
        IProgress<double>? progress, double progressStart, CancellationToken ct)
    {
        var count = PdfRaster.PageSizes(pdf).Count;
        int saved = 0;
        return SafeOutput.WritePages(inputPath, outputPath, count, paths =>
        {
            // Pages are drawn one at a time (PDFium does one thing at once) and saved on the other cores
            // meanwhile; each worker takes the next page only when it is free, so few are held at once.
            var options = new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = PdfRaster.SavingAtOnce(pdf, settings.RenderDpi) };
            try
            {
                Parallel.ForEach(Partitioner.Create(PdfRaster.RenderAll(pdf, settings.RenderDpi), EnumerablePartitionerOptions.NoBuffering), options, page =>
                {
                    using var bitmap = page.Bitmap;
                    ImageTools.Save(bitmap, paths[page.PageIndex], jpeg, settings.ImageQuality);
                    progress?.Report(progressStart + (1 - progressStart) * Interlocked.Increment(ref saved) / count);
                });
            }
            catch (AggregateException ex) when (ex.InnerExceptions.Count > 0)
            {
                // The first failure as itself ("The disk is full"), not wrapped.
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerExceptions[0]).Throw();
            }
        });
    }

    /// <summary>The tallest long picture: JPEG can't be taller than 65,535 pixels.</summary>
    internal const int LongMaxSide = 65_000;

    /// <summary>The widest, as for one page.</summary>
    internal const int LongMaxWidth = 16_000;

    /// <summary>The most pixels a long picture has (400 MB while it is drawn), as for one page.</summary>
    internal const double LongMaxPixels = 100_000_000;

    /// <summary>
    /// The DPI every page of a long picture is drawn at: the settings' own, or less so the picture stays within
    /// <see cref="LongMaxSide"/>, <see cref="LongMaxWidth"/> and <see cref="LongMaxPixels"/>. One DPI for all, so pages keep
    /// their sizes to each other.
    /// </summary>
    internal static int LongDpi(IReadOnlyList<PdfPageSize> sizes, int dpi)
    {
        double widest = sizes.Max(s => s.Width) / 72, tall = sizes.Sum(s => s.Height) / 72;
        double most = Math.Min(Math.Min(LongMaxSide / tall, LongMaxWidth / widest), Math.Sqrt(LongMaxPixels / Math.Max(1e-6, widest * tall)));
        // Below this a page's words can't be read (a Letter page 425 pixels wide): a picture of each page instead.
        if (most < LongMinDpi)
            throw new PdfTooLongException();
        return Math.Min(dpi, (int)Math.Floor(most));
    }

    /// <summary>The least DPI a long picture is drawn at; about 110 Letter or A4 pages fit.</summary>
    internal const int LongMinDpi = 50;

    /// <summary>
    /// Every page of the PDF one under another in one picture, each centred. Pages are drawn one at a time into it
    /// and let go, so a long book takes the picture's memory and one page's, not every page's.
    /// </summary>
    public static IReadOnlyList<string> WriteLong(byte[] pdf, string inputPath, string outputPath, bool jpeg, ConversionSettings settings,
        IProgress<double>? progress, double progressStart, CancellationToken ct)
    {
        var sizes = PdfRaster.PageSizes(pdf);
        if (sizes.Count == 0)
            return [];
        var dpi = LongDpi(sizes, settings.RenderDpi);
        // Room for every page as the DPI gives it; a page drawn a pixel smaller leaves the picture cut to what was drawn.
        int width = (int)Math.Ceiling(sizes.Max(s => s.Width) / 72 * dpi);
        int height = sizes.Sum(s => (int)Math.Ceiling(s.Height / 72 * dpi));
        using var whole = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        int y = 0, drawn = 0;
        using (var canvas = new SKCanvas(whole))
        {
            canvas.Clear(SKColors.White);
            foreach (var page in DrawnForLong(pdf, Enumerable.Range(0, sizes.Count).ToList(), PdfRaster.Options(sizes[0], dpi)))
            {
                using (page)
                {
                    ct.ThrowIfCancellationRequested();
                    canvas.DrawBitmap(page, (width - page.Width) / 2f, y);
                    y += page.Height;
                }
                progress?.Report(progressStart + (1 - progressStart) * 0.9 * ++drawn / sizes.Count);
            }
        }
        using var picture = y < height ? Cut(whole, width, y) : null;
        return SafeOutput.WritePages(inputPath, outputPath, 1, paths =>
        {
            ImageTools.Save(picture ?? whole, paths[0], jpeg, settings.ImageQuality);
            progress?.Report(1);
        });
    }

    private static SKBitmap Cut(SKBitmap bitmap, int width, int height)
    {
        var cut = new SKBitmap(width, Math.Max(1, height), bitmap.ColorType, bitmap.AlphaType);
        using var canvas = new SKCanvas(cut);
        canvas.DrawBitmap(bitmap, 0, 0);
        return cut;
    }

    private static IEnumerable<SKBitmap> DrawnForLong(byte[] pdf, IReadOnlyList<int> pages, RenderOptions options)
    {
        IEnumerator<SKBitmap> drawn;
        lock (PdfRaster.Gate)
            drawn = Conversion.ToImages(pdf, pages, null, options).GetEnumerator();
        try
        {
            while (true)
            {
                SKBitmap? bitmap;
                lock (PdfRaster.Gate)
                    bitmap = drawn.MoveNext() ? drawn.Current : null;
                if (bitmap is null)
                    yield break;
                yield return bitmap;
            }
        }
        finally
        {
            lock (PdfRaster.Gate)
                drawn.Dispose();
        }
    }

    /// <summary>Writes only the first page of the PDF as an image.</summary>
    public static IReadOnlyList<string> WriteFirst(byte[] pdf, string inputPath, string outputPath, bool jpeg, ConversionSettings settings,
        IProgress<double>? progress, double progressStart, CancellationToken ct)
    {
        var count = PdfRaster.PageSizes(pdf).Count;
        if (count == 0)
            return [];

        using var bitmap = PdfRaster.Render(pdf, 0, settings.RenderDpi);
        return SafeOutput.WritePages(inputPath, outputPath, 1, paths =>
        {
            ImageTools.Save(bitmap, paths[0], jpeg, settings.ImageQuality);
            progress?.Report(1);
        });
    }
}
