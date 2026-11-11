using Petal.Core.Util;

namespace Petal.Engines.Media.Internal;

internal enum StreamMode { Encode, Copy, Drop }

/// <summary>GIF output: frame rate, longest side (0 keeps the size) and palette size.</summary>
internal sealed record GifSettings(double Fps, int MaxSide, int Colors = 256, string Dither = "sierra2_4a");

/// <summary>Everything needed to turn one input into one output file.</summary>
internal sealed record EncodePlan
{
    public required string InputPath { get; init; }

    public required MediaInfo Source { get; init; }

    public required ContainerSpec Target { get; init; }

    /// <summary>Options placed before "-i", e.g. "-ss" for fast accurate seeking.</summary>
    public IReadOnlyList<string> InputOptions { get; init; } = [];

    /// <summary>Output options placed right after the inputs, e.g. "-t".</summary>
    public IReadOnlyList<string> OutputOptions { get; init; } = [];

    public StreamMode Video { get; init; } = StreamMode.Encode;

    public StreamMode Audio { get; init; } = StreamMode.Encode;

    /// <summary>Keep every audio track when the container allows it, otherwise only the first.</summary>
    public bool AllAudioTracks { get; init; } = true;

    public bool Subtitles { get; init; }

    public bool Chapters { get; init; } = true;

    public bool Metadata { get; init; } = true;

    /// <summary>Filter for the video stream; may be a graph as long as it has one input and one output.</summary>
    public string? VideoFilter { get; init; }

    public string? AudioFilter { get; init; }

    /// <summary>
    /// Audio taken from this file (a second input) instead of the source's own tracks, such as a
    /// cleaned-up voice; the source still gives the video, cover art and metadata.
    /// </summary>
    public string? ReplacementAudio { get; init; }

    public VideoQuality Quality { get; init; } = new(20);

    public int AudioKbps { get; init; } = 256;

    /// <summary>Bit depth for PCM/FLAC output; null derives it from the source.</summary>
    public int? AudioBitDepth { get; init; }

    public bool MaxFlacCompression { get; init; }

    /// <summary>Try the graphics card's H.264 encoder through Windows (Windows' software encoder if it fails).</summary>
    public bool Hardware { get; init; }

    /// <summary>Duration of the output in seconds, for progress.</summary>
    public double Duration { get; init; }

    public GifSettings? Gif { get; init; }
}

/// <summary>Runs an <see cref="EncodePlan"/>: GIF palettes, two-pass bitrate targets and the graphics card → software fallback.</summary>
internal static class MediaEncoder
{
    /// <summary>Encodes the plan; returns FFmpeg's output of the final pass (stderr holds filter reports at "info" level).</summary>
    public static async Task<ProcessResult> RunAsync(
        EncodePlan plan,
        string outputPath,
        IProgress<double>? progress,
        CancellationToken cancellationToken,
        string logLevel = "error")
    {
        if (plan.Target.IsGif)
            return await GifAsync(plan, outputPath, progress, cancellationToken).ConfigureAwait(false);

        var encodesVideo = plan.Video == StreamMode.Encode && plan.Source.HasVideo && !plan.Target.IsAudioOnly;
        if (encodesVideo && plan.Quality.BitrateKbps is not null && Containers.SupportsTwoPass(plan.Target.Video))
            return await TwoPassAsync(plan, outputPath, progress, cancellationToken).ConfigureAwait(false);

        var tryHardware = encodesVideo && plan.Hardware && plan.Target.Video == VideoCodecKind.H264;
        return await EncodeWithFallbackAsync(hw => BuildArgs(plan, hw), tryHardware, outputPath, plan.Target.Muxer, plan.Duration, progress, cancellationToken, logLevel).ConfigureAwait(false);
    }

    /// <summary>
    /// Encodes with the graphics card's H.264 encoder when asked and available, silently retrying with Windows'
    /// software encoder if it fails (size limits, busy sessions, old drivers).
    /// </summary>
    internal static async Task<ProcessResult> EncodeWithFallbackAsync(
        Func<bool, List<string>> buildArgs,
        bool tryHardware,
        string outputPath,
        string muxer,
        double duration,
        IProgress<double>? progress,
        CancellationToken cancellationToken,
        string logLevel = "error")
    {
        if (tryHardware && await MediaCapabilities.HasHardwareH264Async().ConfigureAwait(false))
        {
            try
            {
                return await Ffmpeg.EncodeAsync(buildArgs(true), outputPath, muxer, duration, progress, cancellationToken, logLevel).ConfigureAwait(false);
            }
            catch (ExternalToolException) when (!cancellationToken.IsCancellationRequested)
            {
                // Fall through to the software encoder.
            }
        }
        try
        {
            return await Ffmpeg.EncodeAsync(buildArgs(false), outputPath, muxer, duration, progress, cancellationToken, logLevel).ConfigureAwait(false);
        }
        catch (ExternalToolException) when (!cancellationToken.IsCancellationRequested)
        {
            // An N edition of Windows without the Media Feature Pack has no H.264 or AAC encoder at all: then that is
            // said. Any other failure is FFmpeg's own, and stays so.
            Foundation.MediaFoundationRuntime.Ensure();
            throw;
        }
    }

    /// <summary>Longest filter put on the command line; Windows allows 32,767 characters for the whole of it.</summary>
    private const int LongestOnLine = 8000;

    /// <summary>
    /// A filter (<paramref name="option"/> "vf" or "af") on the command line, or, when it is too long for one (a list of
    /// hundreds of cuts), read by FFmpeg from a file ("-/vf file"). The files are kept a day, then cleared.
    /// </summary>
    internal static void AddFilter(List<string> args, string option, string filter)
    {
        if (filter.Length <= LongestOnLine)
        {
            args.AddRange(["-" + option, filter]);
            return;
        }
        var folder = Path.Combine(Path.GetTempPath(), "Petal", "filters");
        Directory.CreateDirectory(folder);
        foreach (var old in new DirectoryInfo(folder).GetFiles().Where(f => f.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-1)))
        {
            try { old.Delete(); } catch (IOException) { }
        }
        var path = Path.Combine(folder, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(filter)))[..20] + ".txt");
        if (!File.Exists(path))
            File.WriteAllText(path, filter);
        args.AddRange(["-/" + option, path]);
    }

    internal static List<string> BuildArgs(EncodePlan plan, bool hardware, int pass = 0, string? passLog = null)
    {
        var src = plan.Source;
        var target = plan.Target;
        var args = new List<string>(plan.InputOptions) { "-i", plan.InputPath };
        if (plan.ReplacementAudio is { } replacement)
            args.AddRange(["-i", replacement]);
        args.AddRange(plan.OutputOptions);
        var analysisPass = pass == 1;

        // Video, or cover art for audio containers.
        if (target.IsAudioOnly)
        {
            if (target.CoverArt && src.CoverArt is { } cover && plan.Video != StreamMode.Drop && !analysisPass &&
                cover.Codec is "mjpeg" or "png")
            {
                args.AddRange(["-map", $"0:{cover.Index}", "-c:v", "copy", "-disposition:v:0", "attached_pic"]);
            }
        }
        else if (src.Video is { } video && plan.Video != StreamMode.Drop)
        {
            args.AddRange(["-map", $"0:{video.Index}"]);
            if (plan.Video == StreamMode.Copy)
            {
                args.AddRange(["-c:v", "copy"]);
                if (video.Codec == "hevc" && target.IsIsoMp4)
                    args.AddRange(["-tag:v", "hvc1"]);
                // DivX-style "packed B-frames" only play inside AVI; the filter is a no-op for normal streams.
                if (video.Codec == "mpeg4" && target.Muxer != "avi")
                    args.AddRange(["-bsf:v", "mpeg4_unpack_bframes"]);
            }
            else
            {
                // yuv420p needs even dimensions; scale is a no-op when they already are.
                var filter = Join(plan.VideoFilter, "scale=trunc(iw/2)*2:trunc(ih/2)*2");
                AddFilter(args, "vf", filter);
                args.AddRange(Containers.VideoEncoderArgs(target.Video, plan.Quality, hardware, pass, passLog));
            }
            args.AddRange(["-max_muxing_queue_size", "4096"]);
        }

        // Audio.
        var audioMapped = false;
        if (plan.ReplacementAudio is not null && plan.Audio != StreamMode.Drop && !analysisPass)
        {
            args.AddRange(["-map", "1:a:0"]);
            audioMapped = true;
            if (!string.IsNullOrEmpty(plan.AudioFilter))
                AddFilter(args, "af", plan.AudioFilter);
            args.AddRange(Containers.AudioEncoderArgs(target.Audio, plan.AudioKbps, plan.AudioBitDepth ?? Containers.BitDepthOf(src.Audio), plan.MaxFlacCompression));
        }
        else if (plan.Audio != StreamMode.Drop && src.HasAudio && !analysisPass)
        {
            var tracks = target.MultipleAudio && plan.AllAudioTracks ? src.AudioStreams.ToList() : [src.Audio!];
            foreach (var track in tracks)
                args.AddRange(["-map", $"0:{track.Index}"]);
            audioMapped = true;
            if (plan.Audio == StreamMode.Copy)
            {
                args.AddRange(["-c:a", "copy"]);
            }
            else
            {
                if (!string.IsNullOrEmpty(plan.AudioFilter))
                    AddFilter(args, "af", plan.AudioFilter);
                var depth = plan.AudioBitDepth ?? Containers.BitDepthOf(src.Audio);
                args.AddRange(Containers.AudioEncoderArgs(target.Audio, plan.AudioKbps, depth, plan.MaxFlacCompression, src.Audio));
            }
        }
        if (analysisPass || !audioMapped)
            args.Add("-an");

        // Text subtitles survive container changes; picture subtitles are dropped.
        if (plan.Subtitles && !analysisPass && !target.IsAudioOnly)
        {
            var n = 0;
            foreach (var sub in src.SubtitleStreams)
            {
                var codec = Containers.SubtitleCodecFor(target, sub);
                if (codec is null)
                    continue;
                args.AddRange(["-map", $"0:{sub.Index}", $"-c:s:{n++}", codec]);
            }
        }

        args.AddRange(["-map_metadata", plan.Metadata ? "0" : "-1", "-map_chapters", plan.Chapters ? "0" : "-1"]);
        if (!analysisPass)
            args.AddRange(Containers.MuxerArgs(target));
        return args;
    }

    private static async Task<ProcessResult> TwoPassAsync(EncodePlan plan, string outputPath, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var safe = new SafeProgress(progress);
        using var scratch = new ScratchFolder();
        var log = scratch.File("pass");

        var first = BuildArgs(plan, hardware: false, pass: 1, passLog: log);
        await Ffmpeg.RunAsync([.. first, "-f", "null", "-"], plan.Duration, safe.Slice(0, 0.45), cancellationToken).ConfigureAwait(false);

        var second = BuildArgs(plan, hardware: false, pass: 2, passLog: log);
        return await Ffmpeg.EncodeAsync(second, outputPath, plan.Target.Muxer, plan.Duration, safe.Slice(0.45, 0.55), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>High-quality GIF: build an optimal palette first, then map every frame onto it.</summary>
    private static async Task<ProcessResult> GifAsync(EncodePlan plan, string outputPath, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        if (!plan.Source.HasVideo)
            throw new MediaException("This file has no video to turn into a GIF.");

        var gif = plan.Gif ?? new GifSettings(15, 0);
        var chain = Join(plan.VideoFilter, GifFilter(gif));
        var safe = new SafeProgress(progress);
        using var scratch = new ScratchFolder();
        var palette = scratch.File("palette.png");

        var first = new List<string>(plan.InputOptions) { "-i", plan.InputPath };
        first.AddRange(plan.OutputOptions);
        first.AddRange(["-map", $"0:{plan.Source.Video!.Index}", "-an", "-sn", "-dn",
            "-vf", $"{chain},palettegen=max_colors={gif.Colors}:stats_mode=diff",
            "-frames:v", "1", "-update", "1", "-c:v", "png", "-f", "image2", palette]);
        await Ffmpeg.RunAsync(first, plan.Duration, safe.Slice(0, 0.4), cancellationToken).ConfigureAwait(false);

        var second = new List<string>(plan.InputOptions) { "-i", plan.InputPath, "-i", palette };
        second.AddRange(plan.OutputOptions);
        second.AddRange([
            "-filter_complex", $"[0:{plan.Source.Video.Index}]{chain}[x];[x][1:v]paletteuse=dither={gif.Dither}:diff_mode=rectangle[out]",
            "-map", "[out]", "-an", "-sn", "-dn", "-loop", "0",
            "-map_metadata", plan.Metadata ? "0" : "-1",
        ]);
        return await Ffmpeg.EncodeAsync(second, outputPath, "gif", plan.Duration, safe.Slice(0.4, 0.6), cancellationToken).ConfigureAwait(false);
    }

    private static string GifFilter(GifSettings gif)
    {
        var parts = new List<string>();
        if (gif.Fps > 0)
            parts.Add($"fps={Inv.N(Math.Round(gif.Fps, 3))}");
        if (gif.MaxSide > 0)
        {
            var m = Inv.I(gif.MaxSide);
            parts.Add($"scale=w='if(gt(max(iw,ih),{m}),trunc(iw*{m}/max(iw,ih)),iw)':h=-1:flags=lanczos");
        }
        return parts.Count == 0 ? "null" : string.Join(',', parts);
    }

    /// <summary>Appends a filter chain to a (possibly graph-shaped) filter.</summary>
    internal static string Join(string? first, string second) =>
        string.IsNullOrEmpty(first) ? second : $"{first},{second}";
}
