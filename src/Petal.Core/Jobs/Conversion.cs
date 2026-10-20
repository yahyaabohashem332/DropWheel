using Petal.Core.Formats;

namespace Petal.Core.Jobs;

/// <summary>Quality choices shared by all converters. Defaults favour quality over size.</summary>
public sealed class ConversionSettings
{
    /// <summary>JPEG / WEBP / HEIC / AVIF quality, 1–100.</summary>
    public int ImageQuality { get; set; } = 90;

    /// <summary>Resolution used when rendering PDF pages, slides or documents to images.</summary>
    public int RenderDpi { get; set; } = 300;

    /// <summary>Constant rate factor for H.264/H.265/VP9/AV1, lower is better quality.</summary>
    public int VideoCrf { get; set; } = 20;

    /// <summary>Bitrate for lossy audio formats.</summary>
    public int AudioBitrateKbps { get; set; } = 256;

    /// <summary>Frames per second for GIF exports.</summary>
    public int GifFps { get; set; } = 15;

    /// <summary>Longest side of GIF exports in pixels, 0 keeps the source size.</summary>
    public int GifMaxWidth { get; set; } = 720;

    /// <summary>Use hardware video encoders when the GPU has them.</summary>
    public bool HardwareVideoEncoding { get; set; } = true;

    /// <summary>
    /// Longest side of picture and video results in pixels; 0 keeps the source size. Only
    /// favourites set it, so it is not saved with the user's defaults.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public int MaxLongEdge { get; set; }

    public static ConversionSettings Default { get; } = new();

    public ConversionSettings Clone() => (ConversionSettings)MemberwiseClone();
}

public sealed record ConversionJob(string InputPath, FileFormat From, FileFormat To, string OutputPath, ConversionSettings Settings);

/// <summary>What a job produced. Multi-page exports (e.g. PDF to PNG) write a folder.</summary>
public sealed record JobResult(IReadOnlyList<string> OutputPaths)
{
    public static JobResult Single(string path) => new([path]);

    public string Primary => OutputPaths[0];
}

/// <summary>
/// A converter that can also save a file again in its own format with other settings (smaller,
/// lower quality), for favourites such as "JPG, 1600 px".
/// </summary>
public interface IResaver
{
    bool CanResave(FileFormat format);
}

public interface IConverter
{
    string Name { get; }

    /// <summary>True when this converter can do from → to on this machine (its dependencies are present).</summary>
    bool CanConvert(FileFormat from, FileFormat to);

    Task<JobResult> ConvertAsync(ConversionJob job, IProgress<double>? progress, CancellationToken cancellationToken);
}

/// <summary>Routes a conversion to the first registered converter that can handle it.</summary>
public sealed class ConverterRegistry
{
    private readonly List<IConverter> _converters = [];

    public IReadOnlyList<IConverter> Converters => _converters;

    public void Register(IConverter converter) => _converters.Add(converter);

    public IConverter? Find(FileFormat from, FileFormat to) =>
        ConversionMatrix.CanConvert(from, to) ? _converters.FirstOrDefault(c => c.CanConvert(from, to)) : null;

    public bool CanConvert(FileFormat from, FileFormat to) => Find(from, to) is not null;

    /// <summary>The converter that re-saves files of <paramref name="format"/> in the same format, if any.</summary>
    public IConverter? FindResaver(FileFormat format) =>
        _converters.FirstOrDefault(c => c is IResaver r && r.CanResave(format));

    /// <summary>The wheel's conversion targets, filtered to what this machine can actually do.</summary>
    public IReadOnlyList<FileFormat> AvailableTargets(IReadOnlyCollection<FileFormat> sources) =>
        ConversionMatrix.TargetsFor(sources)
            .Where(t => sources.All(s => s == t || CanConvert(s, t)))
            .ToList();
}
