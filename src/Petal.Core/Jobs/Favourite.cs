using System.Text.Json.Serialization;
using Petal.Core.Formats;

namespace Petal.Core.Jobs;

/// <summary>
/// A conversion the user does often, kept as a starred petal on the conversion wheel: a target
/// format plus, optionally, a size limit and a quality ("WEBP, 1600 px, quality 80").
/// </summary>
public sealed class Favourite
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>The user's own name for it; empty means one is made from the settings.</summary>
    public string Name { get; set; } = "";

    /// <summary><see cref="FileFormat.Id"/> of the target.</summary>
    public string Format { get; set; } = Formats.Formats.Jpg.Id;

    /// <summary>Longest side of pictures and videos in pixels; 0 keeps the size. Never enlarges.</summary>
    public int LongestSide { get; set; }

    /// <summary>
    /// 0 uses the defaults from Settings. Otherwise, by target: picture quality 1–100, the video
    /// CRF (lower is better), or the audio bitrate in kbps.
    /// </summary>
    public int Quality { get; set; }

    [JsonIgnore]
    public FileFormat? Target => Formats.Formats.FromId(Format);

    /// <summary>True when it does more than change the format, so it is useful for files already in it.</summary>
    [JsonIgnore]
    public bool ChangesMoreThanFormat => SizeApplies && LongestSide > 0 || QualityApplies && Quality > 0;

    /// <summary>Pictures and videos (GIF included) can be made smaller.</summary>
    [JsonIgnore]
    public bool SizeApplies => Target?.Family is FormatFamily.Image or FormatFamily.Video;

    /// <summary>Quality only means something for lossy targets.</summary>
    [JsonIgnore]
    public bool QualityApplies => Target is { } t && QualityKindOf(t) != QualityKind.None;

    public static QualityKind QualityKindOf(FileFormat format) => format.Id switch
    {
        "jpg" or "webp" or "heic" or "avif" => QualityKind.Picture,
        "mp4" or "mov" or "mkv" or "webm" or "avi" or "wmv" => QualityKind.Video,
        "mp3" or "m4a" or "ogg" or "opus" or "wma" => QualityKind.Audio,
        _ => QualityKind.None,
    };

    /// <summary>The conversion settings for this favourite, starting from the user's defaults.</summary>
    public ConversionSettings Apply(ConversionSettings defaults)
    {
        var settings = defaults.Clone();
        settings.MaxLongEdge = SizeApplies ? Math.Max(0, LongestSide) : 0;
        if (Quality > 0 && Target is { } target)
        {
            switch (QualityKindOf(target))
            {
                case QualityKind.Picture:
                    settings.ImageQuality = Math.Clamp(Quality, 1, 100);
                    break;
                case QualityKind.Video:
                    settings.VideoCrf = Math.Clamp(Quality, 0, 51);
                    break;
                case QualityKind.Audio:
                    settings.AudioBitrateKbps = Math.Clamp(Quality, 32, 512);
                    break;
            }
        }
        return settings;
    }

    /// <summary>The converter for one file, or null when this favourite doesn't fit it.</summary>
    public IConverter? ConverterFor(FileFormat from, ConverterRegistry converters)
    {
        if (Target is not { } target)
            return null;
        if (from == target)
            return ChangesMoreThanFormat ? converters.FindResaver(target) : null;
        return converters.Find(from, target);
    }

    /// <summary>True when every file can go through this favourite.</summary>
    public bool AppliesTo(IReadOnlyCollection<FileFormat> sources, ConverterRegistry converters) =>
        sources.Count > 0 && sources.All(s => ConverterFor(s, converters) is not null);

    public Favourite Copy() => (Favourite)MemberwiseClone();
}

public enum QualityKind
{
    None,
    Picture,
    Video,
    Audio,
}
