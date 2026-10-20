using Petal.Core.Jobs;

namespace Petal.Engines.Media;

/// <summary>Base of every tool's options: the encoder quality and hardware choices to use when re-encoding.</summary>
public abstract record MediaToolOptions
{
    /// <summary>Quality settings (CRF, audio bitrate, hardware encoding); null uses <see cref="ConversionSettings.Default"/>.</summary>
    public ConversionSettings? Settings { get; init; }

    internal ConversionSettings EffectiveSettings => Settings ?? ConversionSettings.Default;
}

/// <summary>A time range in seconds from the start of the file.</summary>
public sealed record MediaTimeRange(double Start, double End)
{
    public double Duration => End - Start;

    public bool Contains(double time) => time >= Start && time <= End;

    public override string ToString() => $"{Start:0.###}–{End:0.###} s";
}
