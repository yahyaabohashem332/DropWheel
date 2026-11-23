using Petal.Core.Formats;
using Petal.Core.Jobs;
using Petal.Engines.Documents.IO;
using Petal.Engines.Documents.Util;
using F = Petal.Core.Formats.Formats;

namespace Petal.Engines.Documents;

/// <summary>
/// EPUB to Kobo's KEPUB and back, comic books (CBZ, CBR, CB7) to PDF, EPUB and CBZ, and a PDF to a CBZ of its pages.
/// Kindle books (MOBI, AZW3) are read as documents by <see cref="DocumentConverter"/>.
/// </summary>
public sealed class EbookConverter : IConverter
{
    public string Name => "E-books";

    private static bool IsComic(FileFormat f) => f == F.Cbz || f == F.Cbr || f == F.Cb7;

    public bool CanConvert(FileFormat from, FileFormat to) =>
        ConversionMatrix.CanConvert(from, to) &&
        (from == F.Epub && to == F.Kepub || from == F.Kepub && to == F.Epub
         || IsComic(from) && (to == F.Pdf || to == F.Epub || to == F.Cbz)
         || from == F.Pdf && to == F.Cbz);

    public Task<JobResult> ConvertAsync(ConversionJob job, IProgress<double>? progress, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            SafeOutput.EnsureNotInput(job.InputPath, job.OutputPath);
            progress?.Report(0.02);
            var title = F.BaseName(job.InputPath);
            if (job.From == F.Epub)
                SafeOutput.Write(job.OutputPath, temp => Kepub.FromEpub(job.InputPath, temp));
            else if (job.From == F.Kepub)
                SafeOutput.Write(job.OutputPath, temp => Kepub.ToEpub(job.InputPath, temp));
            else if (job.From == F.Pdf)
            {
                var pdf = File.ReadAllBytes(job.InputPath);
                SafeOutput.Write(job.OutputPath, temp => Comics.PdfToCbz(pdf, temp, title, job.Settings.RenderDpi, job.Settings.ImageQuality,
                    ScaledProgress.Of(progress, 0.05, 1), cancellationToken));
            }
            else
            {
                var comic = Comics.Read(job.InputPath, cancellationToken);
                progress?.Report(0.4);
                SafeOutput.Write(job.OutputPath, temp =>
                {
                    if (job.To == F.Pdf)
                        Comics.WritePdf(comic, temp, title, ScaledProgress.Of(progress, 0.4, 1), cancellationToken);
                    else if (job.To == F.Epub)
                        Comics.WriteEpub(comic, temp, title);
                    else
                        Comics.WriteCbz(comic, temp);
                });
            }
            progress?.Report(1);
            return JobResult.Single(job.OutputPath);
        }, cancellationToken);
}
