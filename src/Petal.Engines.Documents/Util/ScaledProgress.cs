namespace Petal.Engines.Documents.Util;

/// <summary>Maps a step's 0–1 progress onto part of the job's range, synchronously (unlike <see cref="Progress{T}"/>).</summary>
internal sealed class ScaledProgress(IProgress<double> inner, double from, double to) : IProgress<double>
{
    public void Report(double value) => inner.Report(from + (to - from) * Math.Clamp(value, 0, 1));

    public static IProgress<double>? Of(IProgress<double>? inner, double from, double to) =>
        inner is null ? null : new ScaledProgress(inner, from, to);
}
