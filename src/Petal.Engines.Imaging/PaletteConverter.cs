using Petal.Core.Formats;
using Petal.Core.Jobs;
using F = Petal.Core.Formats.Formats;

namespace Petal.Engines.Imaging;

/// <summary>Swatch files: ASE ↔ ACO, and either as a picture of the colours.</summary>
public sealed class PaletteConverter : IConverter
{
    public string Name => "Swatches";

    public bool CanConvert(FileFormat from, FileFormat to) =>
        from.Family == FormatFamily.Palette && (to == F.Ase || to == F.Aco || to == F.Png) && from != to;

    public Task<JobResult> ConvertAsync(ConversionJob job, IProgress<double>? progress, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            var swatches = Swatches.Read(job.InputPath);
            if (swatches.Count == 0)
                throw new InvalidOperationException("This swatch file has no colours.");
            progress?.Report(0.5);
            if (job.To == F.Ase)
                Swatches.WriteAse(swatches, job.OutputPath);
            else if (job.To == F.Aco)
                Swatches.WriteAco(swatches, job.OutputPath);
            else
                Swatches.WriteSheet(swatches, job.OutputPath);
            progress?.Report(1);
            return JobResult.Single(job.OutputPath);
        }, cancellationToken);
}
