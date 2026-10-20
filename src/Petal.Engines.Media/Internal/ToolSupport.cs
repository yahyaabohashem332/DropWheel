using Petal.Core.Jobs;

namespace Petal.Engines.Media.Internal;

/// <summary>Shared plumbing for the tool backends.</summary>
internal static class ToolSupport
{
    public static async Task<MediaInfo> ProbeAsync(string input, string output, CancellationToken cancellationToken)
    {
        TempOutput.EnsureNotInput(output, input);
        return await MediaProbe.ProbeAsync(input, cancellationToken).ConfigureAwait(false);
    }

    public static void RequireVideo(MediaInfo info)
    {
        if (!info.HasVideo)
            throw new MediaException($"\"{Path.GetFileName(info.Path)}\" has no video.");
    }

    public static void RequireAudio(MediaInfo info)
    {
        if (!info.HasAudio)
            throw new MediaException($"\"{Path.GetFileName(info.Path)}\" has no audio track.");
    }

    /// <summary>
    /// Bitrate for re-encoding lossy audio inside a tool: about the source's, so edits do not bloat or degrade
    /// the file, capped by the user's audio quality setting.
    /// </summary>
    public static int AudioKbps(MediaInfo info, ConversionSettings settings)
    {
        var cap = Math.Max(320, settings.AudioBitrateKbps);
        var source = info.AudioBitRate > 0 ? info.AudioBitRate / 1000
            : !info.HasVideo && info.BitRate > 0 ? info.BitRate / 1000
            : 0;
        return source > 0 ? (int)Math.Clamp(source, 96, cap) : settings.AudioBitrateKbps;
    }

    /// <summary>A same-format re-encode of the whole file with the user's quality settings.</summary>
    public static EncodePlan Plan(string input, MediaInfo info, ContainerSpec target, ConversionSettings settings) => new()
    {
        InputPath = input,
        Source = info,
        Target = target,
        Quality = new VideoQuality(settings.VideoCrf),
        AudioKbps = AudioKbps(info, settings),
        Hardware = settings.HardwareVideoEncoding,
        Duration = info.DurationSeconds,
        // Tools keep a GIF's own timing and size.
        Gif = target.IsGif ? new GifSettings(0, 0) : null,
    };

    /// <summary>Audio is copied when the target container accepts the codec, otherwise re-encoded.</summary>
    public static StreamMode CopyAudioIfPossible(MediaInfo info, ContainerSpec target) =>
        !info.HasAudio || target.IsGif ? StreamMode.Drop
        : info.AudioStreams.All(a => target.CopyAudio.Contains(a.Codec)) ? StreamMode.Copy
        : StreamMode.Encode;

    /// <summary>Video is copied when the target container accepts the codec (audio-only edits on a video file).</summary>
    public static StreamMode CopyVideoIfPossible(MediaInfo info, ContainerSpec target) =>
        info.Video is { } v && !target.IsAudioOnly && target.CopyVideo.Contains(v.Codec) ? StreamMode.Copy : StreamMode.Encode;

    public static (double Start, double End) ClampRange(MediaInfo info, double start, double end)
    {
        var duration = info.DurationSeconds;
        if (!double.IsFinite(start) || !double.IsFinite(end))
            throw new ArgumentException("Start and end must be finite numbers of seconds.");
        start = Math.Max(0, start);
        if (duration > 0)
        {
            start = Math.Min(start, duration);
            end = Math.Min(end, duration);
        }
        if (end - start < 0.01)
            throw new ArgumentException("The end must be after the start.");
        return (start, end);
    }

    public static int Even(double value) => Math.Max(2, (int)Math.Floor(value / 2) * 2);
}
