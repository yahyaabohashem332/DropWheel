using Petal.Core.Formats;
using Petal.Core.Jobs;
using Petal.Engines.Documents.Converters;
using Petal.Engines.Documents.IO;
using Petal.Engines.Documents.Model;
using Petal.Engines.Documents.Office;
using Petal.Engines.Documents.Pdf;
using Petal.Engines.Documents.Text;
using Petal.Engines.Documents.Util;
using F = Petal.Core.Formats.Formats;

namespace Petal.Engines.Documents;

/// <summary>
/// DOCX, ODT, RTF, Markdown, HTML, TXT and EPUB to each other, and to PDF, PNG and JPG.
/// Word documents go through Word (or LibreOffice) for PDF and page images when installed,
/// so the layout matches exactly; otherwise the built-in renderer lays them out.
/// </summary>
public sealed class DocumentConverter(DocumentEngineOptions? options = null) : IConverter
{
    private readonly DocumentEngineOptions _options = options ?? DocumentEngineOptions.Default;

    public string Name => "Documents";

    public bool CanConvert(FileFormat from, FileFormat to) =>
        DocumentIO.CanRead(from) && ConversionMatrix.CanConvert(from, to) &&
        (DocumentIO.CanWrite(to) || to == F.Pdf || to == F.Png || to == F.Jpg);

    public Task<JobResult> ConvertAsync(ConversionJob job, IProgress<double>? progress, CancellationToken cancellationToken) =>
        Task.Run(async () =>
        {
            SafeOutput.EnsureNotInput(job.InputPath, job.OutputPath);
            progress?.Report(0.02);
            if (job.To == F.Pdf || job.To == F.Png || job.To == F.Jpg)
                return await ToPdfOrImagesAsync(job, progress, cancellationToken).ConfigureAwait(false);

            var doc = DocumentIO.Read(job.InputPath, job.From);
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(0.5);
            var paths = DocumentIO.Write(doc, job.To, job.OutputPath);
            progress?.Report(1);
            return new JobResult(paths);
        }, cancellationToken);

    private async Task<JobResult> ToPdfOrImagesAsync(ConversionJob job, IProgress<double>? progress, CancellationToken ct)
    {
        using var scratch = SafeOutput.CreateScratch();
        var pdf = scratch.File("document.pdf");
        await RenderPdfAsync(job.InputPath, job.From, pdf, _options, ct).ConfigureAwait(false);
        progress?.Report(0.6);

        if (job.To == F.Pdf)
        {
            SafeOutput.Write(job.OutputPath, temp => File.Copy(pdf, temp));
            progress?.Report(1);
            return JobResult.Single(job.OutputPath);
        }
        var bytes = await File.ReadAllBytesAsync(pdf, ct).ConfigureAwait(false);
        var paths = PageImages.Write(bytes, job.InputPath, job.OutputPath, job.To == F.Jpg, job.Settings, progress, 0.6, ct);
        return new JobResult(paths);
    }

    /// <summary>
    /// The best available PDF for a document: Word or LibreOffice for Word-family files, Word for
    /// text in scripts the built-in renderer cannot shape (Indic, Thai…), else the built-in renderer.
    /// </summary>
    internal static async Task RenderPdfAsync(string input, FileFormat from, string outputPdf, DocumentEngineOptions options, CancellationToken ct)
    {
        if (HighFidelity.AppFor(from) is { } app && await HighFidelity.TryPdfAsync(input, app, outputPdf, options, ct).ConfigureAwait(false))
            return;

        var doc = DocumentIO.Read(input, from);
        ct.ThrowIfCancellationRequested();
        if (Bidi.NeedsComplexLayout(doc.PlainText()) && HighFidelity.OfficeAvailable(OfficeApp.Word, options))
        {
            // The built-in renderer shapes Arabic and Hebrew itself but loses inline formatting there,
            // and cannot shape Indic or Thai at all; Word does all of them properly.
            using var scratch = SafeOutput.CreateScratch();
            var docx = scratch.File("document.docx");
            DocxWriter.Write(doc, docx);
            if (await HighFidelity.TryPdfAsync(docx, OfficeApp.Word, outputPdf, options, ct).ConfigureAwait(false))
                return;
        }
        PdfDocWriter.Write(doc, outputPdf);
    }
}
