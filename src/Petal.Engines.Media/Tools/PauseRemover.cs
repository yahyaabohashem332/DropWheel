using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Petal.Engines.Media.Internal;

namespace Petal.Engines.Media;

/// <param name="ThresholdDb">Quieter than this counts as a pause.</param>
/// <param name="ShortestPause">Pauses shorter than this stay: they are the breath between words.</param>
/// <param name="Keep">Seconds of each pause kept on both sides, so words aren't clipped.</param>
public sealed record RemovePausesOptions(double ThresholdDb = -35, double ShortestPause = 0.6, double Keep = 0.15) : MediaToolOptions;

/// <summary>What removing the pauses did: how many, and the length before and after.</summary>
public sealed record PauseCut(int Pauses, TimeSpan Before, TimeSpan After, string? OutputPath)
{
    public TimeSpan Removed => Before - After;
}

/// <summary>
/// Remove Pauses: the silent stretches of a recording or video (a talk, a lesson, a screen recording) are
/// found with FFmpeg's silence detection and cut out in one pass, keeping a little of each so speech
/// doesn't sound clipped. The picture and the sound stay together.
/// </summary>
public static partial class PauseRemover
{
    /// <summary>
    /// Longest filter Petal hands FFmpeg (a long one goes in a file); beyond this, hours of short pauses, the shortest gaps
    /// between them are merged.
    /// </summary>
    private const int LongestFilter = 400_000;

    /// <summary>Finds the pauses without changing anything.</summary>
    public static async Task<IReadOnlyList<(double Start, double End)>> FindAsync(string input, RemovePausesOptions options, CancellationToken cancellationToken = default)
    {
        var info = await MediaProbe.ProbeAsync(input, cancellationToken).ConfigureAwait(false);
        if (info.Audio is null)
            throw new MediaException("This file has no sound, so it has no pauses to find.");
        return await DetectAsync(input, info, options, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<(double Start, double End)>> DetectAsync(string input, MediaInfo info, RemovePausesOptions options, CancellationToken ct)
    {
        double length = info.Duration.TotalSeconds;
        List<string> args = ["-i", input, "-map", $"0:{info.Audio!.Index}", "-vn", "-sn", "-dn",
            "-af", $"silencedetect=noise={Inv.N(Math.Clamp(options.ThresholdDb, -80, -10))}dB:d={Inv.N(Math.Max(0.1, options.ShortestPause))}", "-f", "null", "-"];
        var result = await Ffmpeg.RunAsync(args, length, null, ct, logLevel: "info").ConfigureAwait(false);
        return Pauses(result.StandardError, length);
    }

    /// <summary>The pauses FFmpeg reported ("silence_start: 3.2", "silence_end: 5.8"); one still open runs to the end.</summary>
    internal static List<(double Start, double End)> Pauses(string report, double length)
    {
        var pauses = new List<(double, double)>();
        double? start = null;
        foreach (Match m in SilenceEvent().Matches(report))
        {
            var t = double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            if (m.Groups[1].Value == "start")
                start = Math.Max(0, t);
            else if (start is { } s)
            {
                pauses.Add((s, Math.Min(length, t)));
                start = null;
            }
        }
        if (start is { } open && length - open > 0)
            pauses.Add((open, length));
        return pauses;
    }

    /// <summary>
    /// The parts kept: everything but the pauses, each pause shortened by <paramref name="keep"/> seconds
    /// beside the speech: before the first word, after the last, and on both sides between them.
    /// </summary>
    public static List<(double Start, double End)> KeepRanges(IReadOnlyList<(double Start, double End)> pauses, double length, double keep)
    {
        var ranges = new List<(double Start, double End)>();
        double from = 0;
        foreach (var (start, end) in pauses.OrderBy(p => p.Start))
        {
            double cutFrom = start <= 0.01 ? 0 : start + keep;
            double cutTo = end >= length - 0.01 ? length : end - keep;
            if (cutTo - cutFrom < 0.05)
                continue;
            if (cutFrom > from)
                ranges.Add((from, cutFrom));
            from = Math.Max(from, cutTo);
        }
        if (length > from)
            ranges.Add((from, length));
        return ranges.Where(r => r.End - r.Start >= 0.02).ToList();
    }

    /// <summary>
    /// The ranges as an FFmpeg expression that is true while a frame is kept: a search through them by where they start
    /// (if(lt(t,start),earlier,later)), so a frame is tested against a dozen of them rather than all of them, and FFmpeg,
    /// whose parser takes no more than a hundred terms added in a row, reads any number. Ranges that overlap are joined.
    /// Each takes in its start and not its end, so a run of sound blocks that starts on a cut isn't a block longer than the
    /// picture at every cut.
    /// </summary>
    internal static string Expression(IReadOnlyList<(double Start, double End)> ranges)
    {
        var joined = new List<(double Start, double End)>();
        foreach (var (start, end) in ranges.Where(r => r.End >= r.Start).OrderBy(r => r.Start))
        {
            if (joined.Count > 0 && start <= joined[^1].End)
                joined[^1] = (joined[^1].Start, Math.Max(joined[^1].End, end));
            else
                joined.Add((start, end));
        }
        if (joined.Count == 0)
            return "0";
        var sb = new StringBuilder();
        void Search(int first, int last)
        {
            if (first == last)
            {
                sb.Append("gte(t,").Append(Short(joined[first].Start)).Append(")*lt(t,").Append(Short(joined[first].End)).Append(')');
                return;
            }
            int middle = (first + last + 1) / 2;
            sb.Append("if(lt(t,").Append(Short(joined[middle].Start)).Append("),");
            Search(first, middle - 1);
            sb.Append(',');
            Search(middle, last);
            sb.Append(')');
        }
        Search(0, joined.Count - 1);
        return sb.ToString();
    }

    /// <summary>
    /// The sound filter that keeps what <paramref name="expression"/> keeps: the sound in blocks of 32 samples (under a
    /// millisecond, so the cuts fall within one of the picture's), each kept or not, laid end to end.
    /// </summary>
    internal static string AudioSelect(string expression) => $"asetnsamples=n=32:p=0,aselect='{expression}',asetpts=N/SR/TB";

    private static string Short(double seconds) => seconds.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>
    /// The ranges moved onto the video's frames, so picture and sound are cut at the same moments and stay
    /// together however many cuts there are.
    /// </summary>
    internal static List<(double Start, double End)> OnFrames(List<(double Start, double End)> ranges, double fps)
    {
        if (fps <= 0 || !double.IsFinite(fps))
            return ranges;
        double Snap(double t) => Math.Round(t * fps) / fps;
        return ranges.Select(r => (Snap(r.Start), Snap(r.End))).Where(r => r.Item2 - r.Item1 > 0.5 / fps).ToList();
    }

    /// <summary>How many pauses the cut takes out: the gaps around and between the parts kept.</summary>
    public static int Cuts(IReadOnlyList<(double Start, double End)> ranges, double length) =>
        ranges.Count == 0 ? 0 : ranges.Count - 1 + (ranges[0].Start > 0.01 ? 1 : 0) + (ranges[^1].End < length - 0.01 ? 1 : 0);

    /// <summary>Fewer ranges, by joining those with the shortest pauses between them, until the filter fits.</summary>
    internal static List<(double Start, double End)> Fit(List<(double Start, double End)> ranges)
    {
        while (ranges.Count > 1 && Expression(ranges).Length > LongestFilter)
        {
            int shortest = 0;
            for (int i = 1; i < ranges.Count - 1; i++)
                if (ranges[i + 1].Start - ranges[i].End < ranges[shortest + 1].Start - ranges[shortest].End)
                    shortest = i;
            ranges[shortest] = (ranges[shortest].Start, ranges[shortest + 1].End);
            ranges.RemoveAt(shortest + 1);
        }
        return ranges;
    }

    /// <summary>Cuts the pauses out into <paramref name="output"/>; when there are none, nothing is written.</summary>
    public static async Task<PauseCut> RemoveAsync(string input, string output, RemovePausesOptions options, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var info = await ToolSupport.ProbeAsync(input, output, cancellationToken).ConfigureAwait(false);
        if (info.Audio is null)
            throw new MediaException("This file has no sound, so it has no pauses to find.");
        var length = info.Duration;
        var pauses = await DetectAsync(input, info, options, cancellationToken).ConfigureAwait(false);
        var ranges = Fit(OnFrames(KeepRanges(pauses, length.TotalSeconds, Math.Max(0, options.Keep)), info.FrameRate));
        double kept = ranges.Sum(r => r.End - r.Start);
        int cut = Cuts(ranges, length.TotalSeconds);
        if (ranges.Count == 0 || length.TotalSeconds - kept < 0.2)
            return new PauseCut(0, length, length, null);

        // Each part from half a frame before its first frame to half a frame before the next part's, so the frames' times sit
        // well inside it whatever the rounding: a whole number of frames, and the picture doesn't gain one at every cut.
        double rate = info.Video is not null && info.FrameRate is > 1 and < 1000 ? info.FrameRate : 0;
        var expression = Expression(rate > 0 ? ranges.Select(r => (r.Start - 0.5 / rate, r.End - 0.5 / rate)).ToList() : ranges);
        var target = Containers.ForOutput(output, input);
        var plan = ToolSupport.Plan(input, info, target, options.EffectiveSettings) with
        {
            VideoFilter = info.Video is null ? null : $"select='{expression}',setpts=N/FRAME_RATE/TB",
            // Sound cut in small steps, not whole encoded frames, so it ends where the picture does.
            AudioFilter = AudioSelect(expression),
            AllAudioTracks = false,
            Subtitles = false,
            Chapters = false,
            Duration = kept,
        };
        await MediaEncoder.RunAsync(plan, output, new SafeProgress(progress), cancellationToken).ConfigureAwait(false);
        return new PauseCut(cut, length, TimeSpan.FromSeconds(kept), output);
    }

    [GeneratedRegex(@"silence_(start|end):\s*(-?[0-9.]+)")]
    private static partial Regex SilenceEvent();
}
