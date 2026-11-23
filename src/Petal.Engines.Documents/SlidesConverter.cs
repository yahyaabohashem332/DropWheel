using Petal.Core.Formats;
using Petal.Core.Jobs;
using Petal.Engines.Documents.Converters;
using Petal.Engines.Documents.Office;
using Petal.Engines.Documents.Util;
using F = Petal.Core.Formats.Formats;

namespace Petal.Engines.Documents;

/// <summary>
/// PPTX and PPT to PDF, PNG and JPG. Slides need a real presentation engine, so this uses
/// PowerPoint when installed, else LibreOffice; without either it reports it cannot convert.
/// </summary>
public sealed class SlidesConverter(DocumentEngineOptions? options = null) : IConverter
{
    private readonly DocumentEngineOptions _options = options ?? DocumentEngineOptions.Default;

    public string Name => "Slides";

    public bool CanConvert(FileFormat from, FileFormat to) =>
        (from == F.Pptx || from == F.Ppt || OfficePackages.PlainOf(from) == F.Pptx) && (to == F.Pdf || to == F.Png || to == F.Jpg) &&
        ConversionMatrix.CanConvert(from, to) && HighFidelity.Available(from, _options);

    public Task<JobResult> ConvertAsync(ConversionJob job, IProgress<double>? progress, CancellationToken cancellationToken) =>
        Task.Run(async () =>
        {
            SafeOutput.EnsureNotInput(job.InputPath, job.OutputPath);
            progress?.Report(0.02);
            using var scratch = SafeOutput.CreateScratch();
            var pdf = scratch.File("slides.pdf");
            // A show, a template or a presentation with macros goes to PowerPoint as the plain presentation it holds.
            var (input, _) = OfficePackages.AsPlain(job.InputPath, job.From, scratch);
            if (!await HighFidelity.TryPdfAsync(input, OfficeApp.PowerPoint, pdf, _options, cancellationToken, throwOnFailure: true).ConfigureAwait(false))
                throw new InvalidOperationException("Converting slides needs Microsoft PowerPoint or LibreOffice.");
            progress?.Report(0.6);

            if (job.To == F.Pdf)
            {
                SafeOutput.Write(job.OutputPath, temp => File.Copy(pdf, temp));
                progress?.Report(1);
                return JobResult.Single(job.OutputPath);
            }
            var bytes = await File.ReadAllBytesAsync(pdf, cancellationToken).ConfigureAwait(false);
            return new JobResult(PageImages.Write(bytes, job.InputPath, job.OutputPath, job.To == F.Jpg, job.Settings, progress, 0.6, cancellationToken));
        }, cancellationToken);
}
