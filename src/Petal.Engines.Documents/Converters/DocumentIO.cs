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

    /// <summary>Writes all PDF pages as a single vertically concatenated image (long image).</summary>
    public static IReadOnlyList<string> WriteLong(byte[] pdf, string inputPath, string outputPath, bool jpeg, ConversionSettings settings,
        IProgress<double>? progress, double progressStart, CancellationToken ct)
    {
        var sizes = PdfRaster.PageSizes(pdf);
        var pageCount = sizes.Count;
        if (pageCount == 0)
            return [];

        // Render all pages first to calculate total size
        var bitmaps = new List<SKBitmap>();
        try
        {
            double maxWidth = 0;
            double totalHeight = 0;
            foreach (var (pageIndex, bitmap) in RenderAllForLong(pdf, settings.RenderDpi))
            {
                ct.ThrowIfCancellationRequested();
                bitmaps.Add(bitmap);
                maxWidth = Math.Max(maxWidth, bitmap.Width);
                totalHeight += bitmap.Height;
                progress?.Report(progressStart + (1 - progressStart) * 0.5 * bitmaps.Count / pageCount);
            }

            // Create concatenated bitmap
            using var concatenated = new SKBitmap((int)maxWidth, (int)totalHeight, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var canvas = new SKCanvas(concatenated);
            canvas.Clear(SKColors.White);

            double y = 0;
            foreach (var bitmap in bitmaps)
            {
                ct.ThrowIfCancellationRequested();
                // Center each page horizontally
                double x = (maxWidth - bitmap.Width) / 2.0;
                canvas.DrawBitmap(bitmap, (float)x, (float)y);
                y += bitmap.Height;
                progress?.Report(progressStart + (1 - progressStart) * (0.5 + 0.5 * bitmaps.IndexOf(bitmap) / pageCount));
            }

            // Save the concatenated image
            return SafeOutput.WritePages(inputPath, outputPath, 1, paths =>
            {
                ImageTools.Save(concatenated, paths[0], jpeg, settings.ImageQuality);
                progress?.Report(1);
            });
        }
        finally
        {
            foreach (var b in bitmaps)
                b.Dispose();
        }
    }

    /// <summary>Renders all pages for long image concatenation, yielding each bitmap.</summary>
    private static IEnumerable<(int PageIndex, SKBitmap Bitmap)> RenderAllForLong(byte[] pdf, int dpi)
    {
        var sizes = PdfRaster.PageSizes(pdf);
        for (int start = 0; start < sizes.Count;)
        {
            int end = start + 1;
            while (end < sizes.Count && PdfRaster.SafeDpi(sizes[end], dpi) == PdfRaster.SafeDpi(sizes[start], dpi) &&
                   PdfRaster.Pixels(sizes[end], dpi) > 40_000_000 == PdfRaster.Pixels(sizes[start], dpi) > 40_000_000)
                end++;
            var pages = Enumerable.Range(start, end - start).ToList();
            int i = 0;
            foreach (var bitmap in DrawnForLong(pdf, pages, PdfRaster.Options(sizes[start], dpi)))
                yield return (pages[i++], bitmap);
            start = end;
        }
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
