using Petal.Core.Jobs;
using Petal.Core.Util;
using Petal.Engines.Media.Internal;
using F = Petal.Core.Formats.Formats;

namespace Petal.Engines.Media;

/// <param name="Start">Seconds from the start of the file.</param>
/// <param name="End">Seconds from the start of the file.</param>
/// <param name="FastCopy">Copy streams instead of re-encoding: instant and lossless, but cuts land on the nearest earlier keyframe.</param>
public sealed record TrimVideoOptions(double Start, double End, bool FastCopy = false) : MediaToolOptions;

/// <summary>A crop rectangle in display pixels of the source (as shown, after rotation). Audio is kept.</summary>
public sealed record CropVideoOptions(int X, int Y, int Width, int Height) : MediaToolOptions;

/// <param name="Factor">Playback speed, 0.25–4 (2 = twice as fast).</param>
/// <param name="PreservePitch">Keep voices at their natural pitch (time-stretch) instead of the "chipmunk" effect.</param>
public sealed record ChangeSpeedOptions(double Factor, bool PreservePitch = true) : MediaToolOptions;

/// <summary>A frame to save: time in seconds and the PNG path to write.</summary>
public sealed record VideoSnapshot(double Time, string OutputPath);

public sealed record VideoSnapshotsOptions(IReadOnlyList<VideoSnapshot> Snapshots) : MediaToolOptions;

/// <summary>Split at explicit times, or into <see cref="EqualParts"/> clips of equal length (used when no times are given).</summary>
public sealed record SplitVideoOptions(IReadOnlyList<double>? SplitTimes = null, int? EqualParts = null, bool FastCopy = false) : MediaToolOptions;

public sealed record JoinVideosOptions : MediaToolOptions;

public enum RedactStyle
{
    Solid,
    Blur,
}

/// <summary>A rectangle in display pixels, hidden from <see cref="Start"/> to <see cref="End"/> seconds (End ≤ Start means the whole video).</summary>
public sealed record RedactionBox(int X, int Y, int Width, int Height, double Start = 0, double End = 0);

/// <param name="Color">Fill colour for <see cref="RedactStyle.Solid"/> as "#RRGGBB".</param>
public sealed record RedactVideoOptions(IReadOnlyList<RedactionBox> Boxes, RedactStyle Style = RedactStyle.Solid, string Color = "#000000") : MediaToolOptions;

/// <summary>Video tool backends. Outputs keep the input's container unless the output path says otherwise.</summary>
public static class VideoTools
{
    public const double MinSpeed = 0.25;
    public const double MaxSpeed = 4;

    /// <summary>The tool suffix for a speed, e.g. "Speed 2x", "Speed 0.5x".</summary>
    public static string SpeedSuffix(double factor) => $"Speed {Inv.N(Math.Round(factor, 2))}x";

    /// <summary>A silent copy: the video stream is copied untouched, audio tracks are dropped.</summary>
    public static async Task<string> RemoveAudioAsync(string input, string output, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var info = await ToolSupport.ProbeAsync(input, output, cancellationToken).ConfigureAwait(false);
        ToolSupport.RequireVideo(info);
        var target = Containers.ForOutput(output, input);
        var safe = new SafeProgress(progress);

        List<string> args = ["-i", input, "-map", "0", "-map", "-0:a", "-map", "-0:d?", "-c", "copy", "-map_metadata", "0", .. Containers.MuxerArgs(target)];
        try
        {
            await Ffmpeg.EncodeAsync(args, output, target.Muxer, info.DurationSeconds, safe, cancellationToken).ConfigureAwait(false);
        }
        catch (ExternalToolException) when (!cancellationToken.IsCancellationRequested)
        {
            // A subtitle or attachment the container refuses: keep just the picture.
            args = ["-i", input, "-map", $"0:{info.Video!.Index}", "-c", "copy", "-map_metadata", "0", .. Containers.MuxerArgs(target)];
            await Ffmpeg.EncodeAsync(args, output, target.Muxer, info.DurationSeconds, safe, cancellationToken).ConfigureAwait(false);
        }
        return output;
    }

    /// <summary>Keeps <see cref="TrimVideoOptions.Start"/>–<see cref="TrimVideoOptions.End"/>; frame-accurate unless FastCopy.</summary>
    public static async Task<string> TrimAsync(string input, string output, TrimVideoOptions options, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var info = await ToolSupport.ProbeAsync(input, output, cancellationToken).ConfigureAwait(false);
        ToolSupport.RequireVideo(info);
        var (start, end) = ToolSupport.ClampRange(info, options.Start, options.End);
        await CutAsync(input, info, output, start, end, options.FastCopy, options.EffectiveSettings, new SafeProgress(progress), cancellationToken).ConfigureAwait(false);
        return output;
    }

    /// <summary>Crops the picture; the audio is copied.</summary>
    public static async Task<string> CropAsync(string input, string output, CropVideoOptions options, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var info = await ToolSupport.ProbeAsync(input, output, cancellationToken).ConfigureAwait(false);
        ToolSupport.RequireVideo(info);
        var target = Containers.ForOutput(output, input);

        var x = Math.Clamp(options.X, 0, Math.Max(0, info.Width - 2));
        var y = Math.Clamp(options.Y, 0, Math.Max(0, info.Height - 2));
        var w = Math.Clamp(options.Width, 2, info.Width - x);
        var h = Math.Clamp(options.Height, 2, info.Height - y);
        if (!target.IsGif)
        {
            w = ToolSupport.Even(w);
            h = ToolSupport.Even(h);
        }

        var plan = ToolSupport.Plan(input, info, target, options.EffectiveSettings) with
        {
            VideoFilter = $"crop={w}:{h}:{x}:{y}",
            Audio = ToolSupport.CopyAudioIfPossible(info, target),
        };
        await MediaEncoder.RunAsync(plan, output, new SafeProgress(progress), cancellationToken).ConfigureAwait(false);
        return output;
    }

    /// <summary>Speeds up or slows down picture and sound together; silent videos work too.</summary>
    public static async Task<string> ChangeSpeedAsync(string input, string output, ChangeSpeedOptions options, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!double.IsFinite(options.Factor) || options.Factor < MinSpeed || options.Factor > MaxSpeed)
            throw new ArgumentOutOfRangeException(nameof(options), $"Speed must be between {MinSpeed}× and {MaxSpeed}×.");
        var info = await ToolSupport.ProbeAsync(input, output, cancellationToken).ConfigureAwait(false);
        ToolSupport.RequireVideo(info);
        var target = Containers.ForOutput(output, input);
        var factor = options.Factor;

        var video = $"setpts=(PTS-STARTPTS)/{Inv.N(factor)}";
        var sourceFps = info.FrameRate is > 0 and <= 240 ? info.FrameRate : 30;
        var gifFps = 0.0;
        if (target.IsGif)
        {
            // Browsers slow down GIF frames shorter than 20 ms.
            if (sourceFps * factor > 50)
                gifFps = 50;
        }
        else if (factor > 1)
        {
            // Drop the surplus frames instead of encoding 4× as many.
            video += $",fps={Inv.N(Math.Round(sourceFps, 3))}";
        }

        string? audio = null;
        if (info.HasAudio && !target.IsGif)
            audio = await SpeedAudioFilterAsync(factor, options.PreservePitch, info.SampleRate).ConfigureAwait(false);

        var plan = ToolSupport.Plan(input, info, target, options.EffectiveSettings) with
        {
            VideoFilter = video,
            AudioFilter = audio,
            Chapters = false,
            Duration = info.DurationSeconds / factor,
            Gif = target.IsGif ? new GifSettings(gifFps, 0) : null,
        };
        await MediaEncoder.RunAsync(plan, output, new SafeProgress(progress), cancellationToken).ConfigureAwait(false);
        return output;
    }

    /// <summary>Saves full-resolution PNG frames at the given times. Returns the written paths in order.</summary>
    public static async Task<IReadOnlyList<string>> SaveSnapshotsAsync(string input, VideoSnapshotsOptions options, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        if (options.Snapshots.Count == 0)
            throw new ArgumentException("Add at least one frame.", nameof(options));
        foreach (var shot in options.Snapshots)
            TempOutput.EnsureNotInput(shot.OutputPath, input);

        var info = await MediaProbe.ProbeAsync(input, cancellationToken).ConfigureAwait(false);
        ToolSupport.RequireVideo(info);
        var safe = new SafeProgress(progress);
        var lastFrame = info.DurationSeconds > 0
            ? Math.Max(0, info.DurationSeconds - 1.0 / Math.Max(1, info.FrameRate))
            : double.MaxValue;

        var written = new List<string>();
        for (var i = 0; i < options.Snapshots.Count; i++)
        {
            var shot = options.Snapshots[i];
            var time = Math.Clamp(shot.Time, 0, lastFrame);
            List<string> args = ["-ss", Inv.N(time), "-i", input, "-map", $"0:{info.Video!.Index}", "-an", "-sn", "-dn",
                "-frames:v", "1", "-update", "1", "-c:v", "png"];
            await Ffmpeg.EncodeAsync(args, shot.OutputPath, "image2", 0, null, cancellationToken).ConfigureAwait(false);
            written.Add(shot.OutputPath);
            safe.Report((i + 1.0) / options.Snapshots.Count);
        }
        return written;
    }

    /// <summary>
    /// Splits into clips written into <paramref name="outputFolder"/> (created if needed), named "&lt;name&gt; 1.mp4", "&lt;name&gt; 2.mp4"…
    /// </summary>
    public static async Task<IReadOnlyList<string>> SplitAsync(string input, string outputFolder, SplitVideoOptions options, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var info = await MediaProbe.ProbeAsync(input, cancellationToken).ConfigureAwait(false);
        ToolSupport.RequireVideo(info);
        var duration = info.DurationSeconds;
        if (duration <= 0)
            throw new MediaException("The video's length is unknown, so it cannot be split.");

        var cuts = SplitPoints(duration, options);
        if (cuts.Count == 0)
            throw new ArgumentException("Add at least one split point inside the video.", nameof(options));

        Directory.CreateDirectory(outputFolder);
        var bounds = new List<double> { 0 };
        bounds.AddRange(cuts);
        bounds.Add(duration);

        var safe = new SafeProgress(progress);
        var baseName = F.BaseName(input);
        var extension = OutputNaming.ExtensionOf(input);
        var written = new List<string>();
        var done = 0.0;
        for (var i = 0; i < bounds.Count - 1; i++)
        {
            var (start, end) = (bounds[i], bounds[i + 1]);
            var path = OutputNaming.Unique(outputFolder, $"{baseName} {i + 1}", extension);
            var slice = safe.Slice(done / duration, (end - start) / duration);
            await CutAsync(input, info, path, start, end, options.FastCopy, options.EffectiveSettings, slice, cancellationToken).ConfigureAwait(false);
            written.Add(path);
            done = end;
        }
        safe.Report(1);
        return written;
    }

    internal static List<double> SplitPoints(double duration, SplitVideoOptions options)
    {
        IEnumerable<double> points = options.SplitTimes is { Count: > 0 } times
            ? times
            : options.EqualParts is { } parts && parts >= 2
                ? Enumerable.Range(1, parts - 1).Select(i => duration * i / parts)
                : [];

        // Ignore cuts at the very ends and cuts closer together than a tenth of a second.
        var result = new List<double>();
        foreach (var t in points.Where(double.IsFinite).Order())
        {
            if (t < 0.1 || t > duration - 0.1)
                continue;
            if (result.Count > 0 && t - result[^1] < 0.1)
                continue;
            result.Add(t);
        }
        return result;
    }

    /// <summary>
    /// Joins clips in order into one MP4. The first clip sets the frame size and rate; others are letterboxed.
    /// Clips without sound get silence so audio stays in sync.
    /// </summary>
    public static async Task<string> JoinAsync(IReadOnlyList<string> inputs, string output, JoinVideosOptions? options = null, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        if (inputs.Count < 2)
            throw new ArgumentException("Choose at least two videos to join.", nameof(inputs));
        TempOutput.EnsureNotInput(output, inputs);
        var settings = options?.EffectiveSettings ?? ConversionSettings.Default;

        var infos = new List<MediaInfo>();
        foreach (var path in inputs)
        {
            var info = await MediaProbe.ProbeAsync(path, cancellationToken).ConfigureAwait(false);
            ToolSupport.RequireVideo(info);
            infos.Add(info);
        }

        var first = infos[0];
        var width = ToolSupport.Even(first.Width);
        var height = ToolSupport.Even(first.Height);
        var fps = Inv.N(Math.Round(first.FrameRate is > 1 and <= 120 ? first.FrameRate : 30, 3));
        var withAudio = infos.Any(i => i.HasAudio);
        var total = infos.Sum(i => i.DurationSeconds);

        var graph = new List<string>();
        var concatInputs = "";
        for (var i = 0; i < infos.Count; i++)
        {
            var info = infos[i];
            graph.Add($"[{i}:{info.Video!.Index}]setpts=PTS-STARTPTS," +
                      $"scale={width}:{height}:force_original_aspect_ratio=decrease:force_divisible_by=2," +
                      $"pad={width}:{height}:(ow-iw)/2:(oh-ih)/2:color=black,setsar=1,fps={fps},format=yuv420p[v{i}]");
            concatInputs += $"[v{i}]";
            if (withAudio)
            {
                graph.Add(info.HasAudio
                    ? $"[{i}:{info.Audio!.Index}]asetpts=PTS-STARTPTS,aresample=48000,aformat=sample_fmts=fltp:channel_layouts=stereo[a{i}]"
                    : $"anullsrc=r=48000:cl=stereo,atrim=duration={Inv.N(Math.Max(0.04, info.DurationSeconds))},aformat=sample_fmts=fltp:channel_layouts=stereo[a{i}]");
                concatInputs += $"[a{i}]";
            }
        }
        graph.Add($"{concatInputs}concat=n={infos.Count}:v=1:a={(withAudio ? 1 : 0)}[v]" + (withAudio ? "[a]" : ""));

        var target = Containers.For(F.Mp4);
        List<string> Build(bool hardware)
        {
            var args = new List<string>();
            foreach (var path in inputs)
                args.AddRange(["-i", path]);
            args.AddRange(["-filter_complex", string.Join(';', graph), "-map", "[v]"]);
            if (withAudio)
                args.AddRange(["-map", "[a]"]);
            args.AddRange(Containers.VideoEncoderArgs(VideoCodecKind.H264, new VideoQuality(settings.VideoCrf), hardware));
            if (withAudio)
                args.AddRange(Containers.AudioEncoderArgs(AudioCodecKind.Aac, Math.Min(settings.AudioBitrateKbps, 256), 16));
            args.AddRange(["-map_metadata", "-1", "-map_chapters", "-1", "-max_muxing_queue_size", "4096", .. Containers.MuxerArgs(target)]);
            return args;
        }

        await MediaEncoder.EncodeWithFallbackAsync(Build, settings.HardwareVideoEncoding, output, target.Muxer, total, new SafeProgress(progress), cancellationToken).ConfigureAwait(false);
        return output;
    }

    /// <summary>Covers boxes with a solid colour or a heavy blur, each during its own time range. Audio is copied.</summary>
    public static async Task<string> RedactAsync(string input, string output, RedactVideoOptions options, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        if (options.Boxes.Count == 0)
            throw new ArgumentException("Add at least one box to redact.", nameof(options));
        var info = await ToolSupport.ProbeAsync(input, output, cancellationToken).ConfigureAwait(false);
        ToolSupport.RequireVideo(info);
        var target = Containers.ForOutput(output, input);

        var boxes = options.Boxes.Select(b => Fit(b, info.Width, info.Height)).Where(b => b is not null).Select(b => b!.Value).ToList();
        if (boxes.Count == 0)
            throw new ArgumentException("The boxes are outside the video frame.", nameof(options));

        var filter = options.Style == RedactStyle.Solid ? SolidFilter(boxes, Inv.Color(options.Color)) : BlurFilter(boxes);
        var plan = ToolSupport.Plan(input, info, target, options.EffectiveSettings) with
        {
            VideoFilter = filter,
            Audio = ToolSupport.CopyAudioIfPossible(info, target),
        };
        await MediaEncoder.RunAsync(plan, output, new SafeProgress(progress), cancellationToken).ConfigureAwait(false);
        return output;
    }

    /// <summary>
    /// Lays a transparent PNG over every frame, for watermarks. <see cref="WatermarkVideoOptions.Overlay"/>
    /// draws it at the video's display size (rotation applied). Audio is copied.
    /// </summary>
    public static async Task<string> WatermarkAsync(string input, string output, WatermarkVideoOptions options, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var info = await ToolSupport.ProbeAsync(input, output, cancellationToken).ConfigureAwait(false);
        ToolSupport.RequireVideo(info);
        var target = Containers.ForOutput(output, input);
        var png = Path.Combine(Path.GetTempPath(), $"petal-watermark-{Guid.NewGuid():N}.png");
        await File.WriteAllBytesAsync(png, options.Overlay(info.Width, info.Height), cancellationToken).ConfigureAwait(false);
        try
        {
            var plan = ToolSupport.Plan(input, info, target, options.EffectiveSettings) with
            {
                // The unlabelled ends are the video stream itself: null passes it to [main].
                VideoFilter = $"null[main];movie={FilterPath(png)},format=rgba[mark];[main][mark]overlay=0:0:format=auto",
                Audio = ToolSupport.CopyAudioIfPossible(info, target),
            };
            await MediaEncoder.RunAsync(plan, output, new SafeProgress(progress), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try { File.Delete(png); } catch (IOException) { }
        }
        return output;
    }

    /// <summary>
    /// A file path inside a filter graph: escaped once for the filter's options (":" and quotes)
    /// and once more for the graph itself (brackets, commas, semicolons, backslashes).
    /// </summary>
    internal static string FilterPath(string path)
    {
        var forward = path.Replace('\\', '/');
        var option = System.Text.RegularExpressions.Regex.Replace(forward, @"[\\:']", m => "\\" + m.Value);
        return System.Text.RegularExpressions.Regex.Replace(option, @"[\\'\[\],;]", m => "\\" + m.Value);
    }

    private readonly record struct Box(int X, int Y, int W, int H, string? Enable);

    private static Box? Fit(RedactionBox b, int frameWidth, int frameHeight)
    {
        // Even coordinates keep 4:2:0 chroma aligned so the blurred patch lands exactly on its hole.
        var x = Math.Clamp(b.X, 0, frameWidth) / 2 * 2;
        var y = Math.Clamp(b.Y, 0, frameHeight) / 2 * 2;
        var right = Math.Clamp(b.X + b.Width, 0, frameWidth);
        var bottom = Math.Clamp(b.Y + b.Height, 0, frameHeight);
        var w = Math.Min(frameWidth - x, (right - x + 1) / 2 * 2);
        var h = Math.Min(frameHeight - y, (bottom - y + 1) / 2 * 2);
        if (w < 2 || h < 2)
            return null;
        var enable = b.End > b.Start ? $"between(t\\,{Inv.N(Math.Max(0, b.Start))}\\,{Inv.N(b.End)})" : null;
        return new Box(x, y, w, h, enable);
    }

    private static string SolidFilter(List<Box> boxes, string color) =>
        string.Join(',', boxes.Select(b =>
            $"drawbox=x={b.X}:y={b.Y}:w={b.W}:h={b.H}:color={color}:t=fill" + (b.Enable is null ? "" : $":enable={b.Enable}")));

    private static string BlurFilter(List<Box> boxes)
    {
        var parts = new List<string>();
        var labels = string.Concat(Enumerable.Range(0, boxes.Count).Select(i => $"[c{i}]"));
        parts.Add($"split={boxes.Count + 1}[base]{labels}");
        for (var i = 0; i < boxes.Count; i++)
        {
            var b = boxes[i];
            // Strong enough that faces and text cannot be recovered, relative to the box size.
            var sigma = Inv.N(Math.Clamp(Math.Min(b.W, b.H) / 4.0, 8, 120));
            parts.Add($"[c{i}]crop={b.W}:{b.H}:{b.X}:{b.Y},gblur=sigma={sigma}:steps=3[b{i}]");
        }
        var current = "[base]";
        for (var i = 0; i < boxes.Count; i++)
        {
            var b = boxes[i];
            var overlay = $"{current}[b{i}]overlay={b.X}:{b.Y}" + (b.Enable is null ? "" : $":enable={b.Enable}");
            var last = i == boxes.Count - 1;
            parts.Add(last ? overlay : overlay + $"[o{i}]");
            current = $"[o{i}]";
        }
        return string.Join(';', parts);
    }

    /// <summary>A section of the file: accurate re-encode, or stream copy from the nearest keyframe.</summary>
    private static async Task CutAsync(string input, MediaInfo info, string output, double start, double end, bool fastCopy,
        ConversionSettings settings, IProgress<double> progress, CancellationToken cancellationToken)
    {
        var target = Containers.ForOutput(output, input);
        var length = end - start;
        if (fastCopy && !target.IsGif)
        {
            List<string> args = ["-ss", Inv.N(start), "-i", input, "-t", Inv.N(length), "-map", $"0:{info.Video!.Index}", "-map", "0:a?",
                "-c", "copy", "-avoid_negative_ts", "make_zero", "-map_metadata", "0", .. Containers.MuxerArgs(target)];
            await Ffmpeg.EncodeAsync(args, output, target.Muxer, length, progress, cancellationToken).ConfigureAwait(false);
            return;
        }

        var plan = ToolSupport.Plan(input, info, target, settings) with
        {
            InputOptions = ["-ss", Inv.N(start)],
            OutputOptions = ["-t", Inv.N(length)],
            Duration = length,
        };
        await MediaEncoder.RunAsync(plan, output, progress, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Tempo change keeping pitch (Rubber Band, else an atempo chain), or a plain resample when pitch may change.</summary>
    internal static async Task<string> SpeedAudioFilterAsync(double factor, bool preservePitch, int sampleRate)
    {
        if (!preservePitch)
        {
            var rate = sampleRate > 0 ? sampleRate : 44100;
            return $"asetrate={Inv.I((long)Math.Round(rate * factor))},aresample={Inv.I(rate)}";
        }
        if (await MediaCapabilities.HasRubberbandAsync().ConfigureAwait(false))
            return $"rubberband=tempo={Inv.N(factor)}:pitchq=quality";

        var stages = new List<string>();
        var f = factor;
        while (f > 2.0) { stages.Add("atempo=2"); f /= 2; }
        while (f < 0.5) { stages.Add("atempo=0.5"); f /= 0.5; }
        stages.Add($"atempo={Inv.N(f)}");
        return string.Join(',', stages);
    }
}
