namespace Petal.App.Tools.Parts;

/// <summary>Maps a step's 0–1 onto a part of the whole job.</summary>
internal sealed class ScaledProgress(IProgress<double> inner, double from, double to) : IProgress<double>
{
    public void Report(double value) => inner.Report(from + (to - from) * Math.Clamp(value, 0, 1));
}
