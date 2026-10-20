using Petal.Core.Formats;
using Petal.Core.Jobs;
using Petal.Engines.Documents.Converters;
using Petal.Engines.Documents.IO;
using Petal.Engines.Documents.Model;
using Petal.Engines.Documents.Pdf;
using Petal.Engines.Documents.Util;
using F = Petal.Core.Formats.Formats;

namespace Petal.Engines.Documents;

/// <summary>
/// PDF to Word, ODT, PowerPoint, Excel, EPUB, Markdown, HTML, TXT, PNG and JPG.
/// Text keeps its reading order; headings, lists, tables and pictures are rebuilt from the
/// page layout, and scanned pages are read with Windows OCR.
/// </summary>
public sealed class PdfConverter(DocumentEngineOptions? options = null) : IConverter
{
    private readonly DocumentEngineOptions _options = options ?? DocumentEngineOptions.Default;

    public string Name => "PDF";

    public bool CanConvert(FileFormat from, FileFormat to) =>
        from == F.Pdf && ConversionMatrix.CanConvert(from, to) &&
        (to == F.Docx || to == F.Odt || to == F.Pptx || to == F.Xlsx || to == F.Jpg || to == F.Png ||
         to == F.Epub || to == F.Txt || to == F.Md || to == F.Html || to == F.Rtf);

    public Task<JobResult> ConvertAsync(ConversionJob job, IProgress<double>? progress, CancellationToken cancellationToken) =>
        Task.Run(async () =>
        {
            SafeOutput.EnsureNotInput(job.InputPath, job.OutputPath);
            progress?.Report(0.02);
            var ct = cancellationToken;
            switch (job.To.Id)
            {
                case "png" or "jpg":
                {
                    var bytes = await File.ReadAllBytesAsync(job.InputPath, ct).ConfigureAwait(false);
                    return new JobResult(PageImages.Write(bytes, job.InputPath, job.OutputPath, job.To == F.Jpg, job.Settings, progress, 0.02, ct));
                }
                case "pptx":
                {
                    var dpi = Math.Min(job.Settings.RenderDpi, 220);
                    await SafeOutput.WriteAsync(job.OutputPath, temp =>
                        PdfToPptx.WriteAsync(job.InputPath, temp, _options.PdfToPptxMode, dpi, job.Settings.ImageQuality, ContentOptions(), progress, ct)).ConfigureAwait(false);
                    return JobResult.Single(job.OutputPath);
                }
                case "xlsx":
                {
                    var (pages, _) = await PdfContent.ExtractAsync(job.InputPath, ContentOptions(pictures: false), Scaled(progress, 0, 0.8), ct).ConfigureAwait(false);
                    SafeOutput.Write(job.OutputPath, temp => PdfToXlsx.Write(pages, temp, ct));
                    progress?.Report(1);
                    return JobResult.Single(job.OutputPath);
                }
                case "epub" when _options.PdfToEpubLayout == PdfToEpubLayout.FixedLayout:
                    return await FixedLayoutEpubAsync(job, progress, ct).ConfigureAwait(false);
                default:
                {
                    var doc = await ReadModelAsync(job.InputPath, Scaled(progress, 0, 0.85), ct).ConfigureAwait(false);
                    var paths = DocumentIO.Write(doc, job.To, job.OutputPath);
                    progress?.Report(1);
                    return new JobResult(paths);
                }
            }
        }, cancellationToken);

    internal async Task<DocModel> ReadModelAsync(string pdf, IProgress<double>? progress, CancellationToken ct)
    {
        var (pages, metadata) = await PdfContent.ExtractAsync(pdf, ContentOptions(), progress, ct).ConfigureAwait(false);
        var doc = PdfLayout.Analyze(pages, metadata, new PdfLayoutOptions { KeepPageBreaks = _options.PdfKeepPageBreaks });
        doc.Metadata.Title ??= F.BaseName(pdf);
        return doc;
    }

    private PdfContentOptions ContentOptions(bool pictures = true) => new()
    {
        Ocr = _options.OcrScannedPages,
        OcrLanguages = _options.OcrLanguages,
        Pictures = pictures,
    };

    private async Task<JobResult> FixedLayoutEpubAsync(ConversionJob job, IProgress<double>? progress, CancellationToken ct)
    {
        var bytes = await File.ReadAllBytesAsync(job.InputPath, ct).ConfigureAwait(false);
        var sizes = PdfRaster.PageSizes(bytes);
        var pages = new List<EpubPage>(sizes.Count);
        for (var i = 0; i < sizes.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            // About 2000 pixels tall: sharp on tablets without making the book huge.
            var dpi = (int)Math.Clamp(2000 / (sizes[i].Height / 72), 72, job.Settings.RenderDpi);
            using var bitmap = PdfRaster.Render(bytes, i, dpi);
            pages.Add(new EpubPage(ImageTools.EncodeJpeg(bitmap, Math.Min(job.Settings.ImageQuality, 85)), true, bitmap.Width, bitmap.Height));
            progress?.Report(0.9 * (i + 1) / sizes.Count);
        }
        string? title = null;
        try
        {
            using var pig = UglyToad.PdfPig.PdfDocument.Open(bytes);
            title = string.IsNullOrWhiteSpace(pig.Information.Title) ? null : pig.Information.Title;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The title is optional.
        }
        SafeOutput.Write(job.OutputPath, temp => Epub.WriteFixedLayout(pages, temp, title ?? F.BaseName(job.InputPath), new DocMetadata()));
        progress?.Report(1);
        return JobResult.Single(job.OutputPath);
    }

    private static IProgress<double>? Scaled(IProgress<double>? progress, double from, double to) => ScaledProgress.Of(progress, from, to);
}
