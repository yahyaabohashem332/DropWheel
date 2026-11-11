using Petal.Core.Jobs;
using Petal.Engines.Media.Internal;
using F = Petal.Core.Formats.Formats;

namespace Petal.Engines.Media;

public enum CompressPreset
{
    /// <summary>Smaller files with little visible or audible quality loss.</summary>
    Balanced,
    /// <summary>Much smaller files; quality loss may be noticeable.</summary>
    Strong,
}

/// <summary>
/// Compression choices. A target size wins over a percentage, which wins over the preset alone.
/// </summary>
/// <param name="TargetSizeBytes">Aim for this output size (bitrate computed from the duration, audio included).</param>
/// <param name="ReductionPercent">Aim for this much smaller than the original, e.g. 50.</param>
/// <param name="MaxResolution">Limit the shorter side of video/GIF to this many pixels (e.g. 1080, 720); null keeps the size.</param>
public sealed record CompressOptions(
    CompressPreset Preset = CompressPreset.Balanced,
    long? TargetSizeBytes = null,
    int? ReductionPercent = null,
    int? MaxResolution = null) : MediaToolOptions;

public sealed record CompressResult(string OutputPath, long OriginalBytes, long OutputBytes)
{
    /// <summary>Output size as a fraction of the original (0.4 = 60 % smaller).</summary>
    public double Ratio => OriginalBytes == 0 ? 1 : (double)OutputBytes / OriginalBytes;
}

/// <summary>Compress tool for audio, video and GIF.</summary>
public static class MediaCompressor
{
    /// <summary>
    /// The extension the compressed file should have: WAV and AIFF become FLAC (lossless, about half the size);
    /// everything else keeps its own extension.
    /// </summary>
    public static string CompressedExtension(string inputPath)
    {
        var format = F.FromPath(inputPath);
        return format == F.Wav || format == F.Aiff ? F.Flac.WriteExtension : OutputNaming.WritableExtensionOf(inputPath);
    }

    public static async Task<CompressResult> CompressAsync(string input, string output, CompressOptions? options = null, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        options ??= new CompressOptions();
        var info = await ToolSupport.ProbeAsync(input, output, cancellationToken).ConfigureAwait(false);
        var target = Containers.ForOutput(output, input);
        var safe = new SafeProgress(progress);

        long? targetBytes = options.TargetSizeBytes is > 0 ? options.TargetSizeBytes
            : options.ReductionPercent is { } pct and > 0 ? info.FileSize * (100 - Math.Clamp(pct, 1, 99)) / 100
            : null;

        if (target.IsGif)
            await GifAsync(input, info, output, target, options, targetBytes, safe, cancellationToken).ConfigureAwait(false);
        else if (info.HasVideo && !target.IsAudioOnly)
            await VideoAsync(input, info, output, target, options, targetBytes, safe, cancellationToken).ConfigureAwait(false);
        else
            await AudioAsync(input, info, output, target, options, targetBytes, safe, cancellationToken).ConfigureAwait(false);

        safe.Report(1);
        return new CompressResult(output, info.FileSize, new FileInfo(output).Length);
    }

    private static async Task VideoAsync(string input, MediaInfo info, string output, ContainerSpec target, CompressOptions options,
        long? targetBytes, IProgress<double> progress, CancellationToken cancellationToken)
    {
        var strong = options.Preset == CompressPreset.Strong;
        var crf = strong ? 31 : 26;
        var preset = strong ? "slow" : "medium";
        var audioKbps = strong ? 96 : 128;
        if (info.AudioBitRate > 0)
            audioKbps = (int)Math.Clamp(Math.Min(audioKbps, info.AudioBitRate / 1000), 32, audioKbps);

        var maxShortSide = options.MaxResolution;
        int? videoKbps = null;
        var duration = info.DurationSeconds;
        if (targetBytes is { } bytes && duration > 0)
        {
            // Leave ~4 % for the container, then share the budget between audio and video.
            var totalKbps = bytes * 8 / 1000.0 / duration * 0.96;
            if (info.HasAudio)
            {
                audioKbps = totalKbps >= 1000 ? 128 : totalKbps >= 400 ? 96 : totalKbps >= 160 ? 64 : 32;
                // Windows' AAC encoder goes no lower than 96 kbps: the picture's share is what is left after it.
                if (target.Audio == AudioCodecKind.Aac)
                    audioKbps = MediaEncoders.AacKbpsFor(audioKbps);
            }
            else
            {
                audioKbps = 0;
            }
            // FFmpeg's own one-pass encoders land a little over the bitrate asked for. (H.264 is fitted by its quality
            // setting, below; its bitrate here only picks the picture size to start from.)
            var share = Containers.SupportsTwoPass(target.Video) || target.Video == VideoCodecKind.H264 ? 1.0 : 0.9;
            videoKbps = (int)Math.Max(24, (totalKbps - audioKbps) * share);
            maxShortSide ??= ShortSideForBitrate(info, videoKbps.Value);
        }

        var shortSide = Math.Min(info.Width, info.Height);
        var fpsCap = strong ? 30 : 60;
        string? FilterFor(int? side)
        {
            var filters = new List<string>();
            if (side is { } limit && limit > 0 && shortSide > limit)
                filters.Add(info.Width >= info.Height ? $"scale=-2:{limit}:flags=lanczos" : $"scale={limit}:-2:flags=lanczos");
            if (info.FrameRate > fpsCap + 0.5)
                filters.Add($"fps={fpsCap}");
            return filters.Count == 0 ? null : string.Join(',', filters);
        }

        var plan = ToolSupport.Plan(input, info, target, options.EffectiveSettings) with
        {
            VideoFilter = FilterFor(maxShortSide),
            Quality = new VideoQuality(crf, preset, videoKbps),
            AudioKbps = Math.Max(32, audioKbps),
            // Windows' software encoder, the same on every PC: compression is about size.
            Hardware = false,
        };

        // Windows' H.264 encoder misses a bitrate on short videos and small sizes by up to several times: a size is
        // met through its quality setting instead.
        if (targetBytes is { } fitTo && videoKbps is not null && target.Video == VideoCodecKind.H264)
        {
            var start = maxShortSide is { } most and > 0 ? Math.Min(most, shortSide) : shortSide;
            await SizeFit.RunAsync(plan, output, fitTo, start, side => FilterFor(side), progress as SafeProgress ?? new SafeProgress(progress), cancellationToken).ConfigureAwait(false);
            return;
        }
        await MediaEncoder.RunAsync(plan, output, progress, cancellationToken).ConfigureAwait(false);

        // FFmpeg's own one-pass encoders can land over the size: then once more, with the picture's bitrate cut by as
        // much as it was over.
        if (targetBytes is { } wanted && videoKbps is { } kbps && !Containers.SupportsTwoPass(target.Video) && new FileInfo(output).Length is var made && made > wanted * 1.05)
        {
            var soundBytes = info.HasAudio ? audioKbps * 1000 / 8.0 * duration : 0;
            var pictureMade = Math.Max(1, made - soundBytes);
            var pictureWanted = Math.Max(1, wanted * 0.97 - soundBytes);
            var again = (int)Math.Max(16, kbps * pictureWanted / pictureMade);
            File.Delete(output);
            await MediaEncoder.RunAsync(plan with { Quality = plan.Quality with { BitrateKbps = again } }, output, progress, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Picks a resolution that leaves enough bits per pixel for a watchable picture at a tight bitrate.</summary>
    internal static int? ShortSideForBitrate(MediaInfo info, int videoKbps)
    {
        var shortSide = Math.Min(info.Width, info.Height);
        if (shortSide <= 0)
            return null;
        var fps = info.FrameRate is > 0 and < 240 ? info.FrameRate : 30;
        int[] candidates = [shortSide, 1080, 720, 540, 480, 360, 240];
        foreach (var side in candidates.Where(c => c <= shortSide).Distinct().OrderDescending())
        {
            var scale = (double)side / shortSide;
            var pixels = info.Width * scale * info.Height * scale;
            if (videoKbps * 1000.0 / (pixels * fps) >= 0.05)
                return side == shortSide ? null : side;
        }
        return Math.Min(shortSide, 240);
    }

    private static async Task AudioAsync(string input, MediaInfo info, string output, ContainerSpec target, CompressOptions options,
        long? targetBytes, IProgress<double> progress, CancellationToken cancellationToken)
    {
        ToolSupport.RequireAudio(info);
        var strong = options.Preset == CompressPreset.Strong;
        var plan = ToolSupport.Plan(input, info, target, options.EffectiveSettings) with
        {
            Video = info.HasVideo ? StreamMode.Drop : StreamMode.Copy,
            Audio = StreamMode.Encode,
        };

        if (target.Audio is AudioCodecKind.Flac or AudioCodecKind.PcmLe or AudioCodecKind.PcmBe)
        {
            // Lossless: squeeze FLAC harder; Strong also drops to 16-bit / 48 kHz, which is inaudible for listening copies.
            var outputOptions = new List<string>();
            if (strong && info.SampleRate > 48000)
                outputOptions.AddRange(["-ar", "48000"]);
            plan = plan with
            {
                MaxFlacCompression = true,
                AudioBitDepth = strong ? 16 : null,
                OutputOptions = outputOptions,
            };
        }
        else
        {
            var kbps = PresetKbps(target.Audio, strong);
            var sourceKbps = info.AudioBitRate > 0 ? info.AudioBitRate / 1000 : info.BitRate / 1000;
            if (sourceKbps > 0 && !Containers.IsLossless(info.Audio!))
                kbps = (int)Math.Min(kbps, sourceKbps * (strong ? 0.5 : 0.75));
            if (targetBytes is { } bytes && info.DurationSeconds > 0)
            {
                // Cover art and tags take space too.
                var overhead = info.AudioBitRate > 0 ? Math.Max(0, info.FileSize - info.AudioBitRate * info.DurationSeconds / 8) : 0;
                kbps = (int)((bytes - overhead) * 8 / 1000.0 / info.DurationSeconds * 0.97);
            }
            plan = plan with { AudioKbps = Math.Max(32, kbps) };
        }
        await MediaEncoder.RunAsync(plan, output, progress, cancellationToken).ConfigureAwait(false);
    }

    private static int PresetKbps(AudioCodecKind codec, bool strong) => codec switch
    {
        AudioCodecKind.Opus => strong ? 48 : 96,
        _ => strong ? 96 : 128,
    };

    private static async Task GifAsync(string input, MediaInfo info, string output, ContainerSpec target, CompressOptions options,
        long? targetBytes, SafeProgress progress, CancellationToken cancellationToken)
    {
        ToolSupport.RequireVideo(info);
        var strong = options.Preset == CompressPreset.Strong;
        var longSide = Math.Max(info.Width, info.Height);
        var shortSide = Math.Min(info.Width, info.Height);
        var sourceFps = info.FrameRate is > 0 and <= 50 ? info.FrameRate : 25;

        var fps = Math.Min(sourceFps, strong ? 10 : 15);
        var colors = strong ? 64 : 128;
        var scale = strong ? 0.75 : 1.0;
        if (options.MaxResolution is { } limit && limit > 0 && shortSide > limit)
            scale = Math.Min(scale, (double)limit / shortSide);

        // A target size is approached by shrinking the frame and palette until the file fits (a few attempts at most).
        var attempts = targetBytes is null ? 1 : 4;
        string? best = null;
        var tried = new List<string>();
        try
        {
            for (var attempt = 0; attempt < attempts; attempt++)
            {
                var candidate = $"{output}.{attempt}{TempOutput.Suffix}";
                tried.Add(candidate);
                var plan = ToolSupport.Plan(input, info, target, options.EffectiveSettings) with
                {
                    Gif = new GifSettings(fps, scale < 1 ? Math.Max(16, (int)(longSide * scale)) : 0, colors, strong ? "bayer:bayer_scale=3" : "sierra2_4a"),
                };
                var slice = progress.Slice((double)attempt / attempts, 1.0 / attempts);
                await MediaEncoder.RunAsync(plan, candidate, slice, cancellationToken).ConfigureAwait(false);
                best = candidate;

                var size = new FileInfo(candidate).Length;
                if (targetBytes is not { } goal || size <= goal)
                    break;
                scale *= Math.Clamp(Math.Sqrt((double)goal / size) * 0.95, 0.3, 0.95);
                colors = Math.Max(32, colors / 2);
                fps = Math.Max(5, fps * 0.8);
            }
            File.Move(best!, Path.GetFullPath(output), overwrite: false);
        }
        finally
        {
            foreach (var path in tried)
                TempOutput.Discard(path);
        }
    }
}
