using Petal.Core.Formats;
using Petal.Core.Jobs;

namespace Petal.Engines.Files.Archives;

/// <summary>Re-packs archives: ZIP ↔ TAR ↔ TAR.GZ, and RAR / 7Z into any of those.</summary>
public sealed class ArchiveConverter : IConverter
{
    public string Name => "Archives";

    public bool CanConvert(FileFormat from, FileFormat to) =>
        from.Family == FormatFamily.Archive && to.Family == FormatFamily.Archive && to.CanWrite && from != to;

    public Task<JobResult> ConvertAsync(ConversionJob job, IProgress<double>? progress, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            var temp = job.OutputPath + ".petal-tmp";
            try
            {
                var entries = ArchiveIO.Read(job.InputPath, job.From, f => progress?.Report(f * 0.98));
                ArchiveIO.Write(temp, job.To, entries, cancellationToken);
                File.Move(temp, job.OutputPath);
                progress?.Report(1);
                return JobResult.Single(job.OutputPath);
            }
            catch
            {
                TryDelete(temp);
                throw;
            }
        }, cancellationToken);

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* best effort */ }
    }
}
