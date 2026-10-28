namespace Petal.Engines.Media;

/// <summary>
/// How loud a recording is, twenty times a second (or as often as asked), in decibels below full scale: where the speech
/// is, how lively it is, and where it pauses. Read once from the same sound Whisper listens to.
/// </summary>
public sealed class SoundLevels
{
    /// <summary>How many levels a second, unless asked for otherwise.</summary>
    public const int PerSecond = 20;

    private readonly float[] _db;

    public SoundLevels(float[] db, int perSecond = PerSecond)
    {
        _db = db;
        Rate = perSecond;
        if (db.Length == 0)
        {
            Floor = Speech = -90;
            QuietBelow = -60;
            return;
        }
        var sorted = (float[])db.Clone();
        Array.Sort(sorted);
        Floor = sorted[(int)(sorted.Length * 0.1)];
        // Speech: the middle of what is well above the quiet.
        var loud = sorted.Where(d => d > Floor + 10).ToArray();
        Speech = loud.Length > 0 ? loud[loud.Length / 2] : sorted[sorted.Length / 2];
        // A pause is near the quiet level and well under the speech; music under the talk is not a pause.
        QuietBelow = Math.Max(Floor + 6, Speech - 22);
    }

    /// <summary>Levels of 16 kHz mono samples, one for each twentieth of a second (or <paramref name="perSecond"/> a second).</summary>
    public static SoundLevels Measure(ReadOnlySpan<float> samples, int rate = 16000, int perSecond = PerSecond)
    {
        int step = rate / perSecond;
        var db = new float[samples.Length / step];
        for (int i = 0; i < db.Length; i++)
        {
            double sum = 0;
            var part = samples.Slice(i * step, step);
            foreach (var s in part)
                sum += s * s;
            db[i] = (float)(10 * Math.Log10(sum / step + 1e-10));
        }
        return new SoundLevels(db, perSecond);
    }

    /// <summary>How many levels a second.</summary>
    public int Rate { get; }

    /// <summary>The level of the quiet between words (a tenth of the time is quieter).</summary>
    public double Floor { get; }

    /// <summary>The usual level of the speech.</summary>
    public double Speech { get; }

    /// <summary>Quieter than this is a pause.</summary>
    public double QuietBelow { get; }

    public int Count => _db.Length;

    public double Length => (double)_db.Length / Rate;

    /// <summary>The level at a moment (seconds).</summary>
    public double At(double t) => _db.Length == 0 ? -90 : _db[Math.Clamp((int)(t * Rate), 0, _db.Length - 1)];

    /// <summary>The loudness of a stretch, from its average power.</summary>
    public double Mean(double from, double to)
    {
        var (a, b) = Span(from, to);
        if (b <= a)
            return At(from);
        double power = 0;
        for (int i = a; i < b; i++)
            power += Math.Pow(10, _db[i] / 10);
        return 10 * Math.Log10(power / (b - a) + 1e-10);
    }

    /// <summary>How much the level of the stretch's speech moves, in decibels (a lively voice rises and falls).</summary>
    public double Spread(double from, double to)
    {
        var (a, b) = Span(from, to);
        var speech = new List<double>();
        for (int i = a; i < b; i++)
            if (_db[i] > QuietBelow)
                speech.Add(_db[i]);
        if (speech.Count < 4)
            return 0;
        double mean = speech.Average();
        return Math.Sqrt(speech.Sum(d => (d - mean) * (d - mean)) / speech.Count);
    }

    /// <summary>The share of a stretch that is quiet.</summary>
    public double QuietShare(double from, double to)
    {
        var (a, b) = Span(from, to);
        if (b <= a)
            return At(from) < QuietBelow ? 1 : 0;
        int quiet = 0;
        for (int i = a; i < b; i++)
            if (_db[i] < QuietBelow)
                quiet++;
        return (double)quiet / (b - a);
    }

    /// <summary>The quietest moment between two times, for a cut that falls between words.</summary>
    public double QuietestBetween(double from, double to)
    {
        var (a, b) = Span(from, to);
        if (b <= a)
            return (from + to) / 2;
        int best = a;
        for (int i = a + 1; i < b; i++)
            if (_db[i] < _db[best])
                best = i;
        return (best + 0.5) / Rate;
    }

    /// <summary>
    /// The middle of the longest pause between two times, where a recording can be cut without cutting a word; the
    /// quietest moment when there is no pause, the later of equals (so the piece before is as long as it can be).
    /// </summary>
    public double LongestPauseBetween(double from, double to)
    {
        var (a, b) = Span(from, to);
        if (b <= a)
            return to;
        int bestStart = -1, bestLength = 0, runStart = -1;
        for (int i = a; i <= b; i++)
        {
            bool quiet = i < b && _db[i] < QuietBelow;
            if (quiet && runStart < 0)
                runStart = i;
            else if (!quiet && runStart >= 0)
            {
                if (i - runStart >= bestLength)
                {
                    bestLength = i - runStart;
                    bestStart = runStart;
                }
                runStart = -1;
            }
        }
        if (bestStart >= 0)
            return (bestStart + bestLength / 2.0) / Rate;
        int quietest = a;
        for (int i = a + 1; i < b; i++)
            if (_db[i] <= _db[quietest])
                quietest = i;
        return (quietest + 0.5) / Rate;
    }

    /// <summary>
    /// Where the sound falls quiet after <paramref name="t"/> (the tail of a word), looking no further than
    /// <paramref name="limit"/>: the start of the first two quiet twentieths running, or the limit.
    /// </summary>
    public double SoundEndAfter(double t, double limit)
    {
        var (a, b) = Span(t, limit);
        for (int i = a; i + 1 < b; i++)
            if (_db[i] < QuietBelow && _db[i + 1] < QuietBelow)
                return Math.Max(t, (double)i / Rate);
        return Math.Max(t, limit);
    }

    /// <summary>Where the sound starts again before <paramref name="t"/> (the start of a word), looking back no further than <paramref name="limit"/>.</summary>
    public double SoundStartBefore(double t, double limit)
    {
        var (a, b) = Span(limit, t);
        for (int i = b - 1; i - 1 >= a; i--)
            if (_db[i] < QuietBelow && _db[i - 1] < QuietBelow)
                return Math.Min(t, (double)(i + 1) / Rate);
        return Math.Min(t, limit);
    }

    private (int From, int To) Span(double from, double to) =>
        (Math.Clamp((int)Math.Floor(from * Rate), 0, _db.Length), Math.Clamp((int)Math.Ceiling(to * Rate), 0, _db.Length));

    /// <summary>The levels, for keeping with a transcript.</summary>
    public IReadOnlyList<float> Values => _db;
}
