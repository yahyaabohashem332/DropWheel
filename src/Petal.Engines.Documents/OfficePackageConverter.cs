using Petal.Core.Formats;
using Petal.Core.Jobs;
using Petal.Engines.Documents.Office;
using Petal.Engines.Documents.Util;

namespace Petal.Engines.Documents;

/// <summary>
/// A Word document to a template and back, a presentation to a show or a template and back, a workbook to a template
/// and back, and documents, workbooks and presentations with macros to plain ones: the same file in another kind of
/// package, everything in it kept, macros taken out where the kind has none.
/// </summary>
public sealed class OfficePackageConverter : IConverter
{
    public string Name => "Office packages";

    public bool CanConvert(FileFormat from, FileFormat to) =>
        to.CanWrite && OfficePackages.SameProgram(from, to) && ConversionMatrix.CanConvert(from, to);

    public Task<JobResult> ConvertAsync(ConversionJob job, IProgress<double>? progress, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            SafeOutput.EnsureNotInput(job.InputPath, job.OutputPath);
            progress?.Report(0.1);
            SafeOutput.Write(job.OutputPath, temp => OfficePackages.Retype(job.InputPath, job.To, temp));
            progress?.Report(1);
            return JobResult.Single(job.OutputPath);
        }, cancellationToken);
}
