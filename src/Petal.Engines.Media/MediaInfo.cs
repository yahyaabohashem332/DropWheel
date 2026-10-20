namespace Petal.Engines.Media;

public enum MediaStreamKind
{
    Video,
    Audio,
    Subtitle,
    Data,
    Attachment,
    Unknown,
}

/// <summary>One stream of a media file as FFprobe reports it.</summary>
public sealed record MediaStreamInfo
{
    /// <summary>Absolute stream index in the file (what FFmpeg's "-map 0:N" expects).</summary>
    public required int Index { get; init; }

    public required MediaStreamKind Kind { get; init; }

    /// <summary>FFmpeg codec name, e.g. "h264", "aac", "pcm_s16le".</summary>
    public required string Codec { get; init; }

    public string? CodecLongName { get; init; }

    public string? Profile { get; init; }

    /// <summary>Container codec tag, e.g. "avc1", "hvc1", "XVID".</summary>
    public string? CodecTag { get; init; }

    /// <summary>Coded size, before rotation.</summary>
    public int Width { get; init; }

    public int Height { get; init; }

    /// <summary>Clockwise rotation in degrees (0, 90, 180 or 270) a player applies when displaying.</summary>
    public int Rotation { get; init; }

    public string? PixelFormat { get; init; }

    public double FrameRate { get; init; }

    public int Channels { get; init; }

    public string? ChannelLayout { get; init; }

    public int SampleRate { get; init; }

    public string? SampleFormat { get; init; }

    /// <summary>Bit depth of PCM/lossless audio, 0 when not meaningful.</summary>
    public int BitsPerSample { get; init; }

    /// <summary>Bits per second, 0 when unknown.</summary>
    public long BitRate { get; init; }

    public TimeSpan? Duration { get; init; }

    /// <summary>Cover art stored as a video stream (MP3/M4A/FLAC artwork).</summary>
    public bool IsAttachedPicture { get; init; }

    public bool IsDefault { get; init; }

    public string? Language { get; init; }

    public IReadOnlyDictionary<string, string> Tags { get; init; } = new Dictionary<string, string>();

    /// <summary>Display width, i.e. after applying <see cref="Rotation"/>.</summary>
    public int DisplayWidth => Rotation is 90 or 270 ? Height : Width;

    public int DisplayHeight => Rotation is 90 or 270 ? Width : Height;
}

public sealed record MediaChapter(int Index, TimeSpan Start, TimeSpan End, IReadOnlyDictionary<string, string> Tags);

/// <summary>What a media file contains. Width/Height are display dimensions (rotation already applied).</summary>
public sealed record MediaInfo
{
    public required string Path { get; init; }

    /// <summary>FFprobe's format name, e.g. "mov,mp4,m4a,3gp,3g2,mj2" or "matroska,webm".</summary>
    public required string Container { get; init; }

    public string? ContainerLongName { get; init; }

    public TimeSpan Duration { get; init; }

    public double DurationSeconds => Duration.TotalSeconds;

    public long FileSize { get; init; }

    /// <summary>Overall bits per second, 0 when unknown.</summary>
    public long BitRate { get; init; }

    /// <summary>The first real video stream (cover art does not count).</summary>
    public MediaStreamInfo? Video { get; init; }

    /// <summary>The first audio stream.</summary>
    public MediaStreamInfo? Audio { get; init; }

    public MediaStreamInfo? CoverArt { get; init; }

    public bool HasVideo => Video is not null;

    public bool HasAudio => Audio is not null;

    /// <summary>Display width in pixels (rotation-corrected), 0 without video.</summary>
    public int Width => Video?.DisplayWidth ?? 0;

    public int Height => Video?.DisplayHeight ?? 0;

    /// <summary>Clockwise display rotation of the video stream.</summary>
    public int Rotation => Video?.Rotation ?? 0;

    public double FrameRate => Video?.FrameRate ?? 0;

    public string? VideoCodec => Video?.Codec;

    public string? AudioCodec => Audio?.Codec;

    public int AudioChannels => Audio?.Channels ?? 0;

    public int SampleRate => Audio?.SampleRate ?? 0;

    public long VideoBitRate => Video?.BitRate ?? 0;

    public long AudioBitRate => Audio?.BitRate ?? 0;

    public IReadOnlyList<MediaStreamInfo> Streams { get; init; } = [];

    public IReadOnlyList<MediaChapter> Chapters { get; init; } = [];

    /// <summary>Container-level tags (title, artist, creation_time…).</summary>
    public IReadOnlyDictionary<string, string> Tags { get; init; } = new Dictionary<string, string>();

    public IEnumerable<MediaStreamInfo> AudioStreams => Streams.Where(s => s.Kind == MediaStreamKind.Audio);

    public IEnumerable<MediaStreamInfo> SubtitleStreams => Streams.Where(s => s.Kind == MediaStreamKind.Subtitle);
}
