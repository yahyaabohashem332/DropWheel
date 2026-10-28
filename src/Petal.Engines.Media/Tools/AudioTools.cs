using System.Globalization;
using System.Text.RegularExpressions;
using Petal.Engines.Media.Internal;
using F = Petal.Core.Formats.Formats;

namespace Petal.Engines.Media;

/// <summary>EBU R128 loudness normalisation targets (the Normalize Volume sliders).</summary>
/// <param name="TargetLufs">Integrated loudness target, −70 to −5 LUFS.</param>
/// <param name="LoudnessRange">Loudness range target, 1 to 50 LU.</param>
/// <param name="TruePeak">Maximum true peak, −9 to 0 dBTP.</param>
public sealed record NormalizeOptions(double TargetLufs = -16, double LoudnessRange = 11, double TruePeak = -1.5) : MediaToolOptions;

/// <summary>Loudness as measured by FFmpeg's loudnorm filter.</summary>
public sealed record LoudnessStats(double IntegratedLufs, double TruePeakDbtp, double LoudnessRangeLu, double ThresholdLufs);

public sealed record NormalizeResult(string OutputPath, LoudnessStats Input, LoudnessStats Output);

/// <param name="TrimSilentEnds">Also remove silence at the very beginning and end of the range (pauses inside are kept).</param>
/// <param name="SilenceThresholdDb">Level below which audio counts as silence.</param>
public sealed record TrimAudioOptions(double Start, double End, bool TrimSilentEnds = false, double SilenceThresholdDb = -50) : MediaToolOptions;

public enum AudioChannelMode
{
    Mono,
    Stereo,
}

/// <param name="LeftVolume">Left channel gain, 0–2 (1 = 100 %).</param>
/// <param name="RightVolume">Right channel gain, 0–2 (1 = 100 %).</param>
public sealed record AudioChannelsOptions(AudioChannelMode Output = AudioChannelMode.Stereo, double LeftVolume = 1, double RightVolume = 1) : MediaToolOptions;

public enum VisualizerShape
{
    /// <summary>16:9, 1920×1080.</summary>
    Landscape,
    /// <summary>9:16, 1080×1920.</summary>
    Portrait,
    /// <summary>1:1, 1080×1080.</summary>
    Square,
}

public enum VisualizerStyle
{
    /// <summary>Animated bars in <see cref="AudioVisualizerOptions.AccentColor"/> on black.</summary>
    Waveform,
    /// <summary>A still picture (<see cref="AudioVisualizerOptions.ImagePath"/>) filling the frame.</summary>
    StillImage,
}

public sealed record AudioVisualizerOptions(
    VisualizerShape Shape = VisualizerShape.Landscape,
    VisualizerStyle Style = VisualizerStyle.Waveform,
    string AccentColor = "#7B5CFF",
    string? ImagePath = null) : MediaToolOptions;

/// <param name="Ranges">Sections replaced by the tone.</param>
/// <param name="Frequency">Tone frequency in Hz.</param>
/// <param name="ToneVolume">Tone amplitude, 0–1 of full scale.</param>
public sealed record BleepOptions(IReadOnlyList<MediaTimeRange> Ranges, double Frequency = 1000, double ToneVolume = 0.5) : MediaToolOptions;

/// <summary>Audio tool backends. They accept audio files and also the audio track of video files (the picture is copied).</summary>
public static partial class AudioTools
{
    /// <summary>Measures loudness without writing anything (for the "input" side of the comparison).</summary>
    public static async Task<LoudnessStats> MeasureLoudnessAsync(string input, NormalizeOptions? options = null, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        options ??= new NormalizeOptions();
        var info = await MediaProbe.ProbeAsync(input, cancellationToken).ConfigureAwait(false);
        ToolSupport.RequireAudio(info);
        var measured = await MeasureAsync(input, info, options, new SafeProgress(progress), cancellationToken).ConfigureAwait(false);
        return measured.Stats;
    }

    /// <summary>Two-pass EBU R128 normalisation. Returns the measured input and output levels.</summary>
    public static async Task<NormalizeResult> NormalizeAsync(string input, string output, NormalizeOptions? options = null, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        options ??= new NormalizeOptions();
        var info = await ToolSupport.ProbeAsync(input, output, cancellationToken).ConfigureAwait(false);
        ToolSupport.RequireAudio(info);
        var safe = new SafeProgress(progress);

        var measured = await MeasureAsync(input, info, options, safe.Slice(0, 0.4), cancellationToken).ConfigureAwait(false);
        if (double.IsNegativeInfinity(measured.IntegratedLufs) || measured.IntegratedLufs < -69)
            throw new MediaException("The audio is silent, so there is nothing to normalize.");

        var (i, lra, tp) = Targets(options);
        var rate = info.SampleRate > 0 ? info.SampleRate : 48000;
        // loudnorm works at 192 kHz internally; resample back to the source rate.
        var filter = $"loudnorm=I={i}:LRA={lra}:TP={tp}" +
                     $":measured_I={Inv.N(measured.IntegratedLufs)}:measured_LRA={Inv.N(measured.LoudnessRangeLu)}" +
                     $":measured_TP={Inv.N(measured.TruePeakDbtp)}:measured_thresh={Inv.N(measured.ThresholdLufs)}" +
                     $":offset={Inv.N(measured.TargetOffset)}:linear=true:print_format=json,aresample={rate}";

        var plan = AudioPlan(input, info, output, options) with { AudioFilter = filter };
        var result = await MediaEncoder.RunAsync(plan, output, safe.Slice(0.4, 0.6), cancellationToken, logLevel: "info").ConfigureAwait(false);
        var report = ParseLoudnorm(result.StandardError);
        var outputStats = report is null
            ? new LoudnessStats(double.NaN, double.NaN, double.NaN, double.NaN)
            : new LoudnessStats(report.Value("output_i"), report.Value("output_tp"), report.Value("output_lra"), report.Value("output_thresh"));
        safe.Report(1);
        return new NormalizeResult(output, measured.Stats, outputStats);
    }

    /// <summary>
    /// Finds where sound starts and stops inside <paramref name="within"/> (default: the whole file), so the
    /// UI can move the trim handles. Silence in the middle is not touched.
    /// </summary>
    public static async Task<MediaTimeRange> DetectSilentEndsAsync(string input, double thresholdDb = -50, MediaTimeRange? within = null, CancellationToken cancellationToken = default)
    {
        var info = await MediaProbe.ProbeAsync(input, cancellationToken).ConfigureAwait(false);
        ToolSupport.RequireAudio(info);
        return await SilentEndsAsync(input, info, thresholdDb, within ?? new MediaTimeRange(0, info.DurationSeconds), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Keeps Start–End, optionally shrinking the range to where the sound actually starts and ends.</summary>
    public static async Task<string> TrimAsync(string input, string output, TrimAudioOptions options, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var info = await ToolSupport.ProbeAsync(input, output, cancellationToken).ConfigureAwait(false);
        ToolSupport.RequireAudio(info);
        var (start, end) = ToolSupport.ClampRange(info, options.Start, options.End);
        var safe = new SafeProgress(progress);

        if (options.TrimSilentEnds)
        {
            var sound = await SilentEndsAsync(input, info, options.SilenceThresholdDb, new MediaTimeRange(start, end), cancellationToken).ConfigureAwait(false);
            (start, end) = (sound.Start, sound.End);
            safe.Report(0.2);
        }

        var length = end - start;
        var plan = AudioPlan(input, info, output, options) with
        {
            InputOptions = ["-ss", Inv.N(start)],
            OutputOptions = ["-t", Inv.N(length)],
            Duration = length,
            // A video's picture cannot be copied from an arbitrary point, so it is re-encoded with the cut.
            Video = info.HasVideo ? StreamMode.Encode : StreamMode.Copy,
        };
        await MediaEncoder.RunAsync(plan, output, options.TrimSilentEnds ? safe.Slice(0.2, 0.8) : safe, cancellationToken).ConfigureAwait(false);
        return output;
    }

    /// <summary>Mono or stereo output with independent left/right gain. Mono sources become two identical channels.</summary>
    public static async Task<string> ConvertChannelsAsync(string input, string output, AudioChannelsOptions options, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var info = await ToolSupport.ProbeAsync(input, output, cancellationToken).ConfigureAwait(false);
        ToolSupport.RequireAudio(info);
        var plan = AudioPlan(input, info, output, options) with
        {
            AudioFilter = ChannelsPreviewFilter(options, info.AudioChannels),
            AllAudioTracks = false,
        };
        await MediaEncoder.RunAsync(plan, output, new SafeProgress(progress), cancellationToken).ConfigureAwait(false);
        return output;
    }

    /// <summary>The audio filter <see cref="ConvertChannelsAsync"/> applies, for live previews through <see cref="MediaPreview.DecodePcmAsync"/>.</summary>
    public static string ChannelsPreviewFilter(AudioChannelsOptions options, int sourceChannels)
    {
        var l = Math.Clamp(options.LeftVolume, 0, 2);
        var r = Math.Clamp(options.RightVolume, 0, 2);
        // Multichannel sources are first folded down to stereo with FFmpeg's standard matrix.
        var prefix = sourceChannels > 2 ? "aformat=channel_layouts=stereo," : "";
        if (options.Output == AudioChannelMode.Stereo)
        {
            return sourceChannels <= 1
                ? $"pan=stereo|c0={Inv.N(l)}*c0|c1={Inv.N(r)}*c0"
                : $"{prefix}pan=stereo|c0={Inv.N(l)}*c0|c1={Inv.N(r)}*c1";
        }
        return sourceChannels <= 1
            ? $"pan=mono|c0={Inv.N((l + r) / 2)}*c0"
            : $"{prefix}pan=mono|c0={Inv.N(l / 2)}*c0+{Inv.N(r / 2)}*c1";
    }

    /// <summary>The output frame size for a visualizer shape.</summary>
    public static (int Width, int Height) VisualizerSize(VisualizerShape shape) => shape switch
    {
        VisualizerShape.Portrait => (1080, 1920),
        VisualizerShape.Square => (1080, 1080),
        _ => (1920, 1080),
    };

    /// <summary>Turns audio into an MP4 (H.264 + AAC): animated waveform bars on black, or a still picture.</summary>
    public static async Task<string> CreateVisualizerAsync(string input, string output, AudioVisualizerOptions? options = null, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        options ??= new AudioVisualizerOptions();
        var info = await ToolSupport.ProbeAsync(input, output, cancellationToken).ConfigureAwait(false);
        ToolSupport.RequireAudio(info);
        if (options.Style == VisualizerStyle.StillImage && (options.ImagePath is null || !File.Exists(options.ImagePath)))
            throw new ArgumentException("Choose an image for the background.", nameof(options));

        var settings = options.EffectiveSettings;
        var (w, h) = VisualizerSize(options.Shape);
        var duration = info.DurationSeconds;
        var target = Containers.For(F.Mp4);

        string graph;
        var inputs = new List<string> { "-i", input };
        if (options.Style == VisualizerStyle.Waveform)
        {
            // A narrow waveform scaled up without smoothing becomes square bars; drawgrid cuts the gaps between them.
            const int barPitch = 20;
            var columns = w / barPitch;
            var waveHeight = ToolSupport.Even(Math.Min(w, h) * 0.45);
            graph = $"[0:{info.Audio!.Index}]aformat=channel_layouts=mono,showwaves=s={columns}x{waveHeight}:mode=cline:rate=30" +
                    $":colors={Inv.Color(options.AccentColor)}:scale=sqrt:draw=full,format=rgba," +
                    $"scale={w}:{waveHeight}:flags=neighbor,drawgrid=w={barPitch}:h={waveHeight}:t=6:c=black@1[wave];" +
                    $"color=c=black:s={w}x{h}:r=30[bg];[bg][wave]overlay=0:(H-h)/2:shortest=1,format=yuv420p[v]";
        }
        else
        {
            inputs.AddRange(["-loop", "1", "-framerate", "1", "-i", options.ImagePath!]);
            graph = $"[1:v]scale={w}:{h}:force_original_aspect_ratio=increase,crop={w}:{h},setsar=1,fps=30,format=yuv420p[v]";
        }

        List<string> Build(bool hardware)
        {
            var args = new List<string>(inputs) { "-filter_complex", graph, "-map", "[v]", "-map", $"0:{info.Audio!.Index}" };
            args.AddRange(Containers.VideoEncoderArgs(VideoCodecKind.H264, new VideoQuality(settings.VideoCrf, "veryfast"), hardware));
            if (!hardware)
                args.AddRange(["-tune", options.Style == VisualizerStyle.StillImage ? "stillimage" : "animation"]);
            args.AddRange(Containers.AudioEncoderArgs(AudioCodecKind.Aac, Math.Min(ToolSupport.AudioKbps(info, settings), 256), 16));
            if (duration > 0)
                args.AddRange(["-t", Inv.N(duration)]);
            args.AddRange(["-shortest", "-map_metadata", "0", "-map_chapters", "-1", .. Containers.MuxerArgs(target)]);
            return args;
        }

        await MediaEncoder.EncodeWithFallbackAsync(Build, settings.HardwareVideoEncoding, output, target.Muxer, duration, new SafeProgress(progress), cancellationToken).ConfigureAwait(false);
        return output;
    }

    /// <summary>Replaces each range with a tone. On a video file only the audio is re-encoded.</summary>
    public static async Task<string> BleepAsync(string input, string output, BleepOptions options, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        if (options.Ranges.Count == 0 || options.Ranges.All(r => r.End <= r.Start))
            throw new ArgumentException("Add at least one bleep range.", nameof(options));
        var info = await ToolSupport.ProbeAsync(input, output, cancellationToken).ConfigureAwait(false);
        ToolSupport.RequireAudio(info);
        var plan = AudioPlan(input, info, output, options) with { AudioFilter = BleepPreviewFilter(options) };
        await MediaEncoder.RunAsync(plan, output, new SafeProgress(progress), cancellationToken).ConfigureAwait(false);
        return output;
    }

    /// <summary>The audio filter <see cref="BleepAsync"/> applies, for "With Bleeps" previews through <see cref="MediaPreview.DecodePcmAsync"/>.</summary>
    public static string BleepPreviewFilter(BleepOptions options)
    {
        var ranges = options.Ranges.Where(r => r.End > r.Start).ToList();
        if (ranges.Count == 0)
            return "anull";
        var inside = PauseRemover.Expression(ranges.Select(r => (Math.Max(0, r.Start), r.End)).ToList());
        var amplitude = Inv.N(Math.Clamp(options.ToneVolume, 0, 1));
        var frequency = Inv.N(Math.Clamp(options.Frequency, 20, 20000));
        return $"aeval='if({inside},{amplitude}*sin(2*PI*{frequency}*t),val(ch))':c=same";
    }

    /// <summary>Re-encodes the audio into the same container; a video stream is copied, cover art is kept.</summary>
    private static EncodePlan AudioPlan(string input, MediaInfo info, string output, MediaToolOptions options)
    {
        var target = Containers.ForOutput(output, input);
        return ToolSupport.Plan(input, info, target, options.EffectiveSettings) with
        {
            Video = info.HasVideo ? ToolSupport.CopyVideoIfPossible(info, target) : StreamMode.Copy,
            Audio = StreamMode.Encode,
        };
    }

    private static (string I, string Lra, string Tp) Targets(NormalizeOptions o) =>
        (Inv.N(Math.Clamp(o.TargetLufs, -70, -5)), Inv.N(Math.Clamp(o.LoudnessRange, 1, 50)), Inv.N(Math.Clamp(o.TruePeak, -9, 0)));

    private sealed record Measurement(LoudnessStats Stats, double TargetOffset)
    {
        public double IntegratedLufs => Stats.IntegratedLufs;
        public double TruePeakDbtp => Stats.TruePeakDbtp;
        public double LoudnessRangeLu => Stats.LoudnessRangeLu;
        public double ThresholdLufs => Stats.ThresholdLufs;
    }

    private static async Task<Measurement> MeasureAsync(string input, MediaInfo info, NormalizeOptions options, IProgress<double> progress, CancellationToken cancellationToken)
    {
        var (i, lra, tp) = Targets(options);
        List<string> args = ["-i", input, "-map", $"0:{info.Audio!.Index}", "-vn", "-sn", "-dn",
            "-af", $"loudnorm=I={i}:LRA={lra}:TP={tp}:print_format=json", "-f", "null", "-"];
        var result = await Ffmpeg.RunAsync(args, info.DurationSeconds, progress, cancellationToken, logLevel: "info").ConfigureAwait(false);
        var report = ParseLoudnorm(result.StandardError) ?? throw new MediaException("Could not measure the loudness of this file.");
        var stats = new LoudnessStats(report.Value("input_i"), report.Value("input_tp"), report.Value("input_lra"), report.Value("input_thresh"));
        return new Measurement(stats, report.Value("target_offset"));
    }

    internal sealed class LoudnormReport(Dictionary<string, string> values)
    {
        public double Value(string key)
        {
            if (!values.TryGetValue(key, out var text))
                return double.NaN;
            return text.Trim() switch
            {
                "-inf" => double.NegativeInfinity,
                "inf" or "+inf" => double.PositiveInfinity,
                var t => double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : double.NaN,
            };
        }
    }

    /// <summary>loudnorm prints a flat JSON object after its "[Parsed_loudnorm…]" line.</summary>
    internal static LoudnormReport? ParseLoudnorm(string stderr)
    {
        var marker = stderr.LastIndexOf("Parsed_loudnorm", StringComparison.Ordinal);
        if (marker < 0)
            return null;
        var open = stderr.IndexOf('{', marker);
        var close = open < 0 ? -1 : stderr.IndexOf('}', open);
        if (close < 0)
            return null;
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match m in JsonPair().Matches(stderr[open..(close + 1)]))
            values[m.Groups[1].Value] = m.Groups[2].Value;
        return values.Count == 0 ? null : new LoudnormReport(values);
    }

    [GeneratedRegex("\"([a-z_]+)\"\\s*:\\s*\"([^\"]*)\"")]
    private static partial Regex JsonPair();

    private static async Task<MediaTimeRange> SilentEndsAsync(string input, MediaInfo info, double thresholdDb, MediaTimeRange within, CancellationToken cancellationToken)
    {
        var (start, end) = ToolSupport.ClampRange(info, within.Start, within.End);
        var length = end - start;
        List<string> args = ["-ss", Inv.N(start), "-i", input, "-t", Inv.N(length), "-map", $"0:{info.Audio!.Index}", "-vn", "-sn", "-dn",
            "-af", $"silencedetect=noise={Inv.N(Math.Clamp(thresholdDb, -100, -10))}dB:d=0.05", "-f", "null", "-"];
        var result = await Ffmpeg.RunAsync(args, length, null, cancellationToken, logLevel: "info").ConfigureAwait(false);

        var silences = new List<(double Start, double? End)>();
        foreach (Match m in SilenceEvent().Matches(result.StandardError))
        {
            var t = Inv.ParseDouble(m.Groups[2].Value);
            if (m.Groups[1].Value == "start")
                silences.Add((t, null));
            else if (silences.Count > 0 && silences[^1].End is null)
                silences[^1] = (silences[^1].Start, t);
        }

        double soundStart = 0, soundEnd = length;
        const double edge = 0.05;
        if (silences.Count > 0 && silences[0].Start <= edge)
            soundStart = silences[0].End ?? length;
        if (silences.Count > 0 && (silences[^1].End is null || silences[^1].End >= length - edge))
            soundEnd = Math.Min(soundEnd, silences[^1].Start);

        // All silent: leave the range alone rather than produce an empty file.
        if (soundEnd - soundStart < 0.01)
            return new MediaTimeRange(start, end);
        return new MediaTimeRange(start + Math.Max(0, soundStart), start + Math.Min(length, soundEnd));
    }

    [GeneratedRegex(@"silence_(start|end):\s*(-?[0-9.]+)")]
    private static partial Regex SilenceEvent();
}
