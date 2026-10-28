namespace Petal.Core.Recording;

/// <summary>
/// Where the view goes in by itself: a little before each click and for a while after it, and over each run of
/// typing. Moments close together become one stretch, so the view stays in rather than going out and back.
/// </summary>
public static class ZoomPlanner
{
    /// <summary>How long before a click the view starts going in, and how long it stays in after.</summary>
    public const double LeadIn = 0.8, Hold = 2.2;

    /// <summary>Typing: keys at most this far apart are one run, and a run needs this many keys.</summary>
    public const double TypingGap = 1.5;
    public const int TypingKeys = 3;

    /// <summary>Stretches closer than this are joined.</summary>
    public const double Join = 1.0;

    /// <summary>The shortest stretch kept.</summary>
    public const double Shortest = 0.6;

    public static List<ZoomSegment> Auto(StudioProject project, double level)
    {
        var spans = new List<(double Start, double End)>();
        if (level <= 1.01)
            return [];
        // A click in the last moments is the one that stopped the recording.
        foreach (var click in project.Clicks)
            if (click.Time < project.Duration - 0.4)
                spans.Add((click.Time - LeadIn, click.Time + Hold));
        foreach (var (start, end) in TypingRuns(project.Typing))
            spans.Add((start - 0.6, end + 1.6));
        var joined = new List<(double Start, double End)>();
        foreach (var span in spans.OrderBy(s => s.Start))
        {
            double start = Math.Max(0, span.Start), end = Math.Min(project.Duration, span.End);
            if (end <= start)
                continue;
            if (joined.Count > 0 && start - joined[^1].End < Join)
                joined[^1] = (joined[^1].Start, Math.Max(joined[^1].End, end));
            else
                joined.Add((start, end));
        }
        return joined.Where(s => s.End - s.Start >= Shortest).Select(s => new ZoomSegment(Round(s.Start), Round(s.End), level)).ToList();
    }

    /// <summary>The runs of typing: their first and last key.</summary>
    public static List<(double Start, double End)> TypingRuns(IReadOnlyList<TypingSample> typing)
    {
        var runs = new List<(double Start, double End)>();
        int count = 0;
        double start = 0, last = double.NegativeInfinity;
        foreach (var key in typing.OrderBy(k => k.Time))
        {
            if (key.Time - last > TypingGap)
            {
                if (count >= TypingKeys)
                    runs.Add((start, last));
                start = key.Time;
                count = 0;
            }
            count++;
            last = key.Time;
        }
        if (count >= TypingKeys)
            runs.Add((start, last));
        return runs;
    }

    private static double Round(double t) => Math.Round(t, 3);
}
