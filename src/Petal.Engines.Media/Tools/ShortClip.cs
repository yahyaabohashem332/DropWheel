using System.Globalization;
using System.Text;
using Petal.Engines.Media.Internal;

namespace Petal.Engines.Media;

/// <summary>The shape of a short: tall (9:16, Reels, Shorts and TikTok), square, or the video's own.</summary>
public enum ShortShape
{
    Tall,
    Square,
    Original,
}

/// <summary>What the window shows: the person speaking (the whole picture when no face is clear), or the whole picture.</summary>
public enum ShortFraming
{
    Speaker,
    Whole,
}

/// <param name="Captions">The words burned in, as they are said.</param>
/// <param name="CutPauses">Pauses between sentences cut short, as short videos are edited.</param>
public sealed record ShortOptions(ShortShape Shape = ShortShape.Tall, ShortFraming Framing = ShortFraming.Speaker, bool Captions = true,
    CaptionStyle Style = CaptionStyle.WordByWord, string Highlight = "#FFD60A", bool CutPauses = true) : MediaToolOptions;

/// <summary>
/// Makes one short from a moment of a long video, in a single pass: the stretch cut out, its long pauses taken out,
/// framed tall on the person speaking (or the whole picture over a blurred copy of itself), the words burned in as they
/// are said, a headline over the first seconds, and the sound brought to the loudness short-video apps play at
/// (−14 LUFS, measured on the clip itself first).
/// </summary>
public static class ShortClip
{
    /// <summary>The loudness shorts are played at.</summary>
    private const double TargetLufs = -14;

    /// <summary>The picture size of a short of this shape made from this video.</summary>
    public static (int Width, int Height) Size(ShortShape shape, MediaInfo info)
    {
        switch (shape)
        {
            case ShortShape.Tall:
                return (1080, 1920);
            case ShortShape.Square:
                return (1080, 1080);
            default:
                int w = Math.Max(2, info.Width), h = Math.Max(2, info.Height);
                double scale = Math.Min(1, 1920.0 / Math.Max(w, h));
                return (ToolSupport.Even(w * scale), ToolSupport.Even(h * scale));
        }
    }

    /// <summary>The parts of a moment kept: all of it, or all but its pauses.</summary>
    internal static List<(double Start, double End)> Kept(double start, double end, IReadOnlyList<(double Start, double End)> pauses)
    {
        var kept = new List<(double Start, double End)>();
        double from = start;
        foreach (var (s, e) in pauses.OrderBy(p => p.Start))
        {
            if (e <= from || s >= end)
                continue;
            if (s > from)
                kept.Add((from, s));
            from = Math.Max(from, e);
        }
        if (end > from)
            kept.Add((from, end));
        return kept.Where(k => k.End - k.Start > 0.02).ToList();
    }

    /// <summary>A time in the video as a time in the short: the parts kept laid end to end; a time in a cut is where the next part starts.</summary>
    internal static double ToOutput(IReadOnlyList<(double Start, double End)> kept, double t)
    {
        double at = 0;
        foreach (var (s, e) in kept)
        {
            if (t < s)
                return at;
            if (t <= e)
                return at + (t - s);
            at += e - s;
        }
        return at;
    }

    /// <summary>The words in the short's time: those said in the parts kept, each lasting until the next begins at the latest.</summary>
    internal static List<CaptionWord> Timed(IReadOnlyList<CaptionWord> words, IReadOnlyList<(double Start, double End)> kept)
    {
        if (kept.Count == 0)
            return [];
        double start = kept[0].Start, end = kept[^1].End;
        var timed = new List<CaptionWord>();
        foreach (var w in words)
        {
            double s = w.Start.TotalSeconds, e = w.End.TotalSeconds;
            if (e <= start || s >= end)
                continue;
            timed.Add(new CaptionWord(w.Text, TimeSpan.FromSeconds(ToOutput(kept, Math.Max(s, start))), TimeSpan.FromSeconds(ToOutput(kept, Math.Min(e, end)))));
        }
        for (int i = 0; i + 1 < timed.Count; i++)
            if (timed[i].End > timed[i + 1].Start)
                timed[i] = timed[i] with { End = timed[i + 1].Start };
        return timed.Where(w => w.End > w.Start || w.Text.Length > 0).ToList();
    }

    /// <summary>
    /// Makes the short. <paramref name="captions"/> are the words to show, in the video's time (the moment's own when
    /// null; a translation's otherwise); <paramref name="headline"/> is shown over the first seconds when not empty.
    /// </summary>
    public static async Task<string> RenderAsync(string video, Moment moment, string output, ShortOptions options, string? headline = null,
        IReadOnlyList<CaptionWord>? captions = null, SoundLevels? levels = null, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var info = await ToolSupport.ProbeAsync(video, output, cancellationToken).ConfigureAwait(false);
        ToolSupport.RequireVideo(info);
        var safe = new SafeProgress(progress);
        double start = Math.Max(0, moment.Start), end = info.DurationSeconds > 0 ? Math.Min(moment.End, info.DurationSeconds) : moment.End;
        if (end - start < 0.5)
            throw new MediaException("This moment is too short to make a clip of.");

        // Frames on a steady clock (a phone's video often isn't), at no more than 30 a second: the cuts fall on frames.
        double sourceRate = info.FrameRate is > 1 and < 1000 ? info.FrameRate : 30;
        double rate = sourceRate / Math.Ceiling(sourceRate / 30.5);
        string rateText = Math.Abs(rate - 30000 / 1001.0) < 0.005 ? "30000/1001" : Math.Abs(rate - 24000 / 1001.0) < 0.005 ? "24000/1001" : Inv(rate);
        var pauses = options.CutPauses ? ShortMoments.Pauses(moment, levels) : [];
        // Each part a whole number of the clip's frames (counted from its start), so its sound lasts exactly as long as its picture.
        double OnFrame(double t) => start + Math.Round((t - start) * rate) / rate;
        var kept = Kept(start, end, pauses).Select(k => (OnFrame(k.Start), OnFrame(k.End)))
            .Where(k => k.Item2 - k.Item1 > 0.5 / rate).ToList();
        if (kept.Count == 0)
            kept = [(start, end)];
        double length = kept.Sum(k => k.Item2 - k.Item1);
        bool cuts = kept.Count > 1;
        // The select filter's times are the clip's own, counted from the seek. Each part runs from half a frame before its
        // first frame to half a frame before the next part's: a whole number of frames, and the same stretch of sound.
        var within = kept.Select(k => (k.Item1 - start - 0.5 / rate, k.Item2 - start - 0.5 / rate)).ToList();
        string Expression() => PauseRemover.Expression(within);
        double toOutput(double t) => ToOutput(kept, t);

        var (width, height) = Size(options.Shape, info);
        var picture = new StringBuilder($"fps={rateText}");
        if (cuts)
            picture.Append(Inv($",select='{Expression()}',setpts=N/FRAME_RATE/TB"));
        safe.Report(0.02);

        if (options.Shape == ShortShape.Original)
            picture.Append(Inv($",scale={width}:{height}:flags=lanczos,setsar=1"));
        else
        {
            double shape = width / (double)height;
            int fullW = Math.Min(info.Width, ToolSupport.Even(info.Height * shape));
            int fullH = fullW < info.Width ? info.Height : Math.Min(info.Height, ToolSupport.Even(info.Width / shape));
            CameraPath? camera = null;
            if (options.Framing == ShortFraming.Speaker && fullW < info.Width)
            {
                // No further in than keeps the picture sharp: at most 2.25 times enlarged.
                double maxZoom = Math.Min(2, fullH / (height / 2.25));
                camera = await SpeakerFraming.FollowAsync(video, info, start, end, fullW / (double)info.Width, maxZoom, cancellationToken).ConfigureAwait(false);
            }
            safe.Report(0.12);
            if (camera is not null)
            {
                int cropH = ToolSupport.Even(fullH / camera.Zoom), cropW = ToolSupport.Even(fullW / camera.Zoom);
                // The speaker's face about two fifths of the way down the window.
                int top = (int)Math.Clamp(camera.CentreY * info.Height - cropH * 0.4, 0, info.Height - cropH);
                // The camera keys are in the clip's own time; the crop sees the short's.
                var keys = camera.Keys.Select(k => k with { T = k.T + start }).ToList();
                var x = SpeakerFraming.Expression(keys, cropW, info.Width, toOutput);
                picture.Append(Inv($",crop={cropW}:{cropH}:'{x}':{top},scale={width}:{height}:flags=lanczos,setsar=1"));
            }
            else
            {
                // The whole picture over a blurred, enlarged copy of itself.
                picture.Append(Inv($",split[a][b];[a]scale={width}:{height}:force_original_aspect_ratio=increase,crop={width}:{height},boxblur=24:4,eq=brightness=-0.08[bg];"));
                picture.Append(Inv($"[b]scale={width}:{height}:force_original_aspect_ratio=decrease:flags=lanczos[fg];[bg][fg]overlay=(W-w)/2:(H-h)/2,setsar=1"));
            }
        }

        string? ass = null;
        try
        {
            var words = captions ?? moment.Words.Select(w => new CaptionWord(w.Text, TimeSpan.FromSeconds(w.Start), TimeSpan.FromSeconds(w.End))).ToList();
            var timed = options.Captions ? Timed(words, kept) : [];
            if (timed.Count > 0 || !string.IsNullOrWhiteSpace(headline))
            {
                bool tall = height > width * 1.2;
                var style = new AnimatedCaptionOptions(options.Style, tall ? CaptionPlace.Lower : CaptionPlace.Bottom,
                    tall ? 0.042 : options.Shape == ShortShape.Square ? 0.05 : 0.055, options.Highlight, tall ? 4 : 6);
                ass = Path.Combine(Path.GetTempPath(), $"petal-short-{Guid.NewGuid():N}.ass");
                await File.WriteAllTextAsync(ass, AnimatedCaptions.Ass(timed, width, height, style, headline, Math.Min(4.5, length)),
                    new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
                picture.Append($",ass=filename={VideoTools.FilterPath(ass)}");
            }

            string? sound = null;
            if (info.HasAudio)
            {
                var select = cuts ? $"asetnsamples=n=64:p=0,aselect='{Expression()}',asetpts=N/SR/TB," : "";
                var measured = await MeasureAsync(video, info, start, end - start, select, length, cancellationToken).ConfigureAwait(false);
                safe.Report(0.18);
                // Brought to the level only when it could be measured: a near-silent clip would have its hiss raised instead.
                sound = select + (measured is { } m
                    ? Inv($"loudnorm=I={TargetLufs}:LRA=11:TP=-1.5:measured_I={m.I}:measured_LRA={m.Lra}:measured_TP={m.Tp}:measured_thresh={m.Threshold}:offset={m.Offset}:linear=true,")
                    : "") + "aresample=48000";
            }

            var target = Containers.ForOutput(output, video);
            var settings = options.EffectiveSettings;
            var plan = ToolSupport.Plan(video, info, target, settings) with
            {
                // Both before the input: only the moment is read (the pauses cut, the short is shorter than the moment).
                InputOptions = ["-ss", Inv(start), "-t", Inv(end - start)],
                VideoFilter = picture.ToString(),
                AudioFilter = sound,
                Audio = info.HasAudio ? StreamMode.Encode : StreamMode.Drop,
                AllAudioTracks = false,
                Subtitles = false,
                Chapters = false,
                Metadata = false,
                // Sharp enough to survive the apps' own compression.
                Quality = new VideoQuality(Math.Min(settings.VideoCrf, 20)),
                AudioKbps = 192,
                Duration = length,
            };
            await MediaEncoder.RunAsync(plan, output, safe.Slice(0.2, 0.8), cancellationToken).ConfigureAwait(false);
            safe.Report(1);
            return output;
        }
        finally
        {
            if (ass is not null)
                try { File.Delete(ass); } catch (IOException) { }
        }
    }

    private sealed record Loudness(string I, string Lra, string Tp, string Threshold, string Offset);

    /// <summary>The clip's own loudness, as its sound will be (pauses cut), for an exact two-pass level.</summary>
    private static async Task<Loudness?> MeasureAsync(string video, MediaInfo info, double start, double span, string select, double length, CancellationToken ct)
    {
        List<string> args = ["-ss", Inv(start), "-t", Inv(span), "-i", video, "-map", $"0:{info.Audio!.Index}", "-vn", "-sn", "-dn",
            "-af", select + Inv($"loudnorm=I={TargetLufs}:LRA=11:TP=-1.5:print_format=json"), "-f", "null", "-"];
        var result = await Ffmpeg.RunAsync(args, length, null, ct, logLevel: "info").ConfigureAwait(false);
        var report = AudioTools.ParseLoudnorm(result.StandardError);
        if (report is null)
            return null;
        double i = report.Value("input_i");
        // Silence can't be brought up to a level.
        if (!double.IsFinite(i) || i < -69)
            return null;
        return new Loudness(Inv(i), Inv(report.Value("input_lra")), Inv(report.Value("input_tp")), Inv(report.Value("input_thresh")), Inv(report.Value("target_offset")));
    }

    private static string Inv(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static string Inv(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
