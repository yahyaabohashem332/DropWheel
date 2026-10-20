using ImageMagick;
using Petal.Core.Formats;
using F = Petal.Core.Formats.Formats;

namespace Petal.Engines.Imaging;

/// <summary>Outcome of one file in a batch run.</summary>
public sealed record ImageBatchResult(string InputPath, string OutputPath, bool Succeeded, string? Error = null);

internal static class ToolSupport
{
    /// <summary>The format to save a tool's result in: the output path's extension, else the source's own format.</summary>
    public static FileFormat OutputFormat(string outputPath, FileFormat? source)
    {
        var byPath = F.FromPath(outputPath);
        if (byPath is not null && byPath != F.Pdf && ImageFormats.CanWrite(byPath))
            return byPath;
        if (source is not null && source != F.Pdf && ImageFormats.CanWrite(source))
            return source;
        return F.Png;
    }

    /// <summary>
    /// Quality for re-saving an edited image: a JPEG keeps roughly its own quality (ImageMagick
    /// estimates it from the quantisation tables) so edits don't visibly degrade it or bloat it.
    /// </summary>
    public static int ReencodeQuality(MagickImage decoded, FileFormat? source, FileFormat target)
    {
        if (target == F.Jpg && source == F.Jpg && decoded.Quality is >= 1 and <= 100)
            return Math.Clamp((int)decoded.Quality, 80, 97);
        return 92;
    }

    public static ImageWriteOptions WriteOptions(MagickImage decoded, FileFormat? source, FileFormat target) => new()
    {
        Quality = ReencodeQuality(decoded, source, target),
        Tiff = TiffMode.Lzw,
    };

    /// <summary>Runs <paramref name="work"/> for each item in turn; one failure doesn't stop the batch.</summary>
    public static async Task<IReadOnlyList<ImageBatchResult>> RunBatchAsync<T>(
        IReadOnlyList<T> items,
        Func<T, (string Input, string Output)> paths,
        Func<T, IProgress<double>?, CancellationToken, Task> work,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        var results = new List<ImageBatchResult>(items.Count);
        for (int i = 0; i < items.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (input, output) = paths(items[i]);
            int index = i;
            var itemProgress = progress is null ? null : new InlineProgress(p => progress.Report((index + Math.Clamp(p, 0, 1)) / items.Count));
            try
            {
                await work(items[i], itemProgress, cancellationToken);
                results.Add(new ImageBatchResult(input, output, true));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                results.Add(new ImageBatchResult(input, output, false, ex.Message));
            }
            progress?.Report((double)(i + 1) / items.Count);
        }
        return results;
    }
}

/// <summary>Forwards progress synchronously (unlike <see cref="Progress{T}"/>, which posts and can reorder).</summary>
internal sealed class InlineProgress(Action<double> report) : IProgress<double>
{
    public void Report(double value) => report(value);

    /// <summary>Maps 0–1 of a sub-step onto <paramref name="from"/>–<paramref name="to"/> of the parent.</summary>
    public static IProgress<double>? Slice(IProgress<double>? parent, double from, double to) =>
        parent is null ? null : new InlineProgress(p => parent.Report(from + (to - from) * Math.Clamp(p, 0, 1)));
}
