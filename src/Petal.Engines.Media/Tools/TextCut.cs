using System.Globalization;
using Petal.Engines.Media.Internal;

namespace Petal.Engines.Media;

/// <summary>What a piece of a recording is, as Edit by Text shows it.</summary>
public enum EditTokenKind
{
    /// <summary>A word, as Whisper heard it.</summary>
    Word,
    /// <summary>A sound between words that no word was written for: an "um", a hesitation, a breath, a laugh.</summary>
    Sound,
    /// <summary>A silence long enough to notice.</summary>
    Pause,
}

/// <summary>The output of Edit by Text: the conversion settings of the file written.</summary>
public sealed record TextCutOptions : MediaToolOptions;

/// <summary>One piece of a recording: a word, a sound or a pause, and when it is.</summary>
public sealed record EditToken(EditTokenKind Kind, string Text, double Start, double End)
{
    public double Length => End - Start;
}

/// <summary>
/// Edit by Text: a recording cut by its words. What was said becomes a run of pieces (<see cref="Tokens"/>): the words with
/// their times, the sounds between words that weren't words (Whisper leaves most "um"s out, but the sound is there), and
/// the pauses. Taking pieces out (<see cref="Keep"/>) takes their time out of the picture and the sound: a word with the
/// silence that went with it, keeping a few hundredths of a second beside the words left so they don't run into each other;
/// a pause is shortened rather than closed. The cut itself falls on the video's frames, so picture and sound stay together
/// however many cuts there are (<see cref="RenderAsync"/>).
/// </summary>
public static class TextCut
{
    /// <summary>A silence at least this long is shown as a pause.</summary>
    public const double PauseFrom = 0.5;

    /// <summary>What a pause taken out is shortened to: the breath between two sentences.</summary>
    public const double ShortPause = 0.3;

    /// <summary>A sound between words at least this long is shown.</summary>
    private const double SoundFrom = 0.2;

    /// <summary>Kept beside a word next to a cut, so its last sound isn't clipped (word times are good to a few hundredths).</summary>
    private const double Margin = 0.06;

    /// <summary>Quiet shorter than this inside a run of sound is part of it (the stop in the middle of a word).</summary>
    private const double Join = 0.12;

    /// <summary>A run of sound shorter than this is a click, not a word or a sound.</summary>
    private const double Click = 0.04;

    /// <summary>Kept beside a word's sound where it fades in or out, under the level counted as quiet.</summary>
    private const double Soft = 0.04;

    /// <summary>A word in a run of sound with others is at least this long.</summary>
    private const double ShortestWord = 0.05;

    /// <summary>The farthest a word is placed from where Whisper heard it start (its times can be half a second out).</summary>
    private const double Window = 2.5;

    /// <summary>
    /// The pieces of a recording, in order, from what Whisper heard and the sound's levels (best a hundred a second, as
    /// Edit by Text measures them). Whisper's word times can be half a second out, and a word of one token often has no
    /// length at all, but the sound itself says where speech is: runs of sound between quiet. The words are placed on
    /// those runs in order, each run taking the words heard starting nearest it (<see cref="Place"/>); a run no word
    /// was heard in is a sound that wasn't a word (an "um", a breath, a laugh), and a long one costs much more to leave
    /// without words than a short one, so speech Whisper timed late keeps its words. Within a run the words are parted at
    /// the quietest moments near where their times put them. What lies between the runs is a pause or nothing worth showing.
    /// </summary>
    public static List<EditToken> Tokens(IReadOnlyList<TranscriptSegment> segments, SoundLevels levels, double length)
    {
        var heard = segments.SelectMany(s => s.Words).Where(w => w.Text.Trim().Length > 0).OrderBy(w => w.Start).ToList();
        int n = heard.Count;
        var onsets = new double[n];
        var expected = new double[n];
        for (int i = 0; i < n; i++)
        {
            onsets[i] = Math.Clamp(Math.Max(heard[i].Start, i > 0 ? onsets[i - 1] : 0), 0, length);
            // About fifteen letters a second.
            expected[i] = Math.Clamp(0.06 + 0.065 * heard[i].Text.Count(char.IsLetterOrDigit), 0.1, 1.2);
        }
        var runs = Runs(levels, length);
        var on = Place(onsets, expected, runs);

        // Each word's place: its share of the run it was put on, or (heard where there was no sound) a moment at its time.
        var spans = new (double Start, double End)[n];
        var taken = new bool[runs.Count];
        for (int i = 0; i < n;)
        {
            if (on[i] < 0)
            {
                spans[i] = (onsets[i], Math.Min(length, onsets[i] + ShortestWord));
                i++;
                continue;
            }
            int run = on[i], last = i;
            while (last + 1 < n && on[last + 1] == run)
                last++;
            taken[run] = true;
            Part(levels, runs[run], onsets, expected, i, last, spans);
            i = last + 1;
        }

        // In time: the words and the sounds, with the pauses between them; each fades in and out a little beyond its run.
        var items = new List<(double Start, double End, int Word)>();
        for (int i = 0; i < n; i++)
            items.Add((spans[i].Start, spans[i].End, i));
        for (int r = 0; r < runs.Count; r++)
            if (!taken[r] && runs[r].End - runs[r].Start >= SoundFrom)
                items.Add((runs[r].Start, runs[r].End, -1));
        items.Sort((a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : a.Word.CompareTo(b.Word));
        var tokens = new List<EditToken>();
        double previous = 0;
        for (int k = 0; k < items.Count; k++)
        {
            var (start, end, word) = items[k];
            double next = k + 1 < items.Count ? items[k + 1].Start : length;
            start = Math.Max(previous, start - Soft);
            end = Math.Min(next, end + Soft);
            if (end <= start)
                end = Math.Min(length, start + 0.01);
            Pause(previous, start, tokens);
            tokens.Add(word >= 0
                ? new EditToken(EditTokenKind.Word, heard[word].Text.Trim(), start, end)
                : new EditToken(EditTokenKind.Sound, "", start, end));
            previous = Math.Max(previous, end);
        }
        Pause(previous, length, tokens);
        return tokens;
    }

    /// <summary>The runs of sound: louder than the quiet, a stop of under <see cref="Join"/> inside one, clicks left out.</summary>
    private static List<(double Start, double End)> Runs(SoundLevels levels, double length)
    {
        var runs = new List<(double Start, double End)>();
        double step = 1.0 / levels.Rate;
        int count = Math.Min(levels.Count, (int)Math.Ceiling(length * levels.Rate));
        for (int k = 0; k < count; k++)
        {
            if (levels.At((k + 0.5) * step) < levels.QuietBelow)
                continue;
            int from = k;
            while (k + 1 < count && levels.At((k + 1.5) * step) >= levels.QuietBelow)
                k++;
            double start = from * step, end = Math.Min(length, (k + 1) * step);
            if (runs.Count > 0 && start - runs[^1].End < Join)
                runs[^1] = (runs[^1].Start, end);
            else
                runs.Add((start, end));
        }
        return runs.Where(r => r.End - r.Start >= Click).ToList();
    }

    /// <summary>
    /// Which run each word is said in (-1: none), the words in order and the runs in order: a shortest path over (word,
    /// run) where a run takes the next few words or none. A word costs the distance of where it was heard to start from
    /// where its run starts (the first in a run) or from the run (the others), and a run how many times longer or shorter
    /// it is than its words would take; a run left without words costs more the longer it is, a word left without a run more.
    /// </summary>
    internal static int[] Place(IReadOnlyList<double> onsets, IReadOnlyList<double> expected, IReadOnlyList<(double Start, double End)> runs)
    {
        int n = onsets.Count, m = runs.Count;
        var on = new int[n];
        Array.Fill(on, -1);
        if (n == 0 || m == 0)
            return on;
        const double Early = 3, Inside = 3, Fit = 1.5, Unsaid = 1.5;
        // For each run, the words that can be next: those heard from a little before the run before it ends to a little
        // after this one starts.
        var first = new int[m + 1];
        var after = new int[m + 1];
        for (int j = 0; j <= m; j++)
        {
            double from = j > 0 ? runs[j - 1].End - Window : double.NegativeInfinity;
            double to = j < m ? runs[j].Start + Window : double.PositiveInfinity;
            first[j] = Lower(onsets, from);
            after[j] = Math.Max(first[j], Lower(onsets, to));
        }
        // cost[j][i - first[j]]: the least cost with the runs before j and the words before i placed.
        var cost = new double[m + 1][];
        var how = new int[m + 1][];
        for (int j = 0; j <= m; j++)
        {
            cost[j] = new double[after[j] - first[j] + 1];
            how[j] = new int[cost[j].Length];
            Array.Fill(cost[j], double.PositiveInfinity);
        }
        if (first[0] == 0)
            cost[0][0] = 0;
        for (int j = 0; j <= m; j++)
        {
            for (int x = 0; x < cost[j].Length; x++)
            {
                int i = first[j] + x;
                double here = cost[j][x];
                if (double.IsPositiveInfinity(here))
                    continue;
                // The word without a run of its own.
                if (i < n && i + 1 <= after[j])
                    Relax(cost, how, j, i + 1, first, here + Unsaid, -2);
                if (j == m)
                    continue;
                var (start, end) = runs[j];
                // The run without a word: a sound that wasn't one.
                Relax(cost, how, j + 1, i, first, here + 0.4 + 3 * (end - start), 0);
                // The run with the next k words.
                double inside = 0, said = 0;
                for (int k = 1; i + k <= n && k <= 200; k++)
                {
                    int w = i + k - 1;
                    if (k > 1)
                    {
                        double o = onsets[w];
                        if (o > end + Window)
                            break;
                        inside += o < start ? start - o : o > end ? o - end : 0;
                    }
                    said += expected[w];
                    double total = here + Early * Math.Abs(onsets[i] - start) + Inside * inside + Fit * Math.Abs(Math.Log((end - start + 0.1) / (said + 0.1)));
                    Relax(cost, how, j + 1, i + k, first, total, k);
                }
            }
        }
        if (n < first[m] || n - first[m] >= cost[m].Length || double.IsPositiveInfinity(cost[m][n - first[m]]))
        {
            // Nothing could be placed within reach (it shouldn't happen): every word where it was heard.
            return on;
        }
        for (int j = m, i = n; j > 0 || i > 0;)
        {
            int k = how[j][i - first[j]];
            if (k == -2)
                i--;
            else if (k == 0)
                j--;
            else
            {
                for (int w = i - k; w < i; w++)
                    on[w] = j - 1;
                i -= k;
                j--;
            }
        }
        return on;
    }

    private static void Relax(double[][] cost, int[][] how, int j, int i, int[] first, double value, int choice)
    {
        int x = i - first[j];
        if (x < 0 || x >= cost[j].Length || value >= cost[j][x])
            return;
        cost[j][x] = value;
        how[j][x] = choice;
    }

    /// <summary>The first index whose value is at least <paramref name="t"/>.</summary>
    private static int Lower(IReadOnlyList<double> sorted, double t)
    {
        int lo = 0, hi = sorted.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (sorted[mid] < t)
                lo = mid + 1;
            else
                hi = mid;
        }
        return lo;
    }

    /// <summary>
    /// A run of sound parted between the words said in it, all the partings chosen together: the first word starts with
    /// the run, and each of the others at a moment that is near where it was heard to start, quiet against the run (the
    /// dip between two words), and leaves each word about as long as its letters take. Twenty decibels quieter is worth a second further; half or twice a word's length costs about five.
    /// </summary>
    private static void Part(SoundLevels levels, (double Start, double End) run, double[] onsets, double[] expected, int first, int last,
        (double Start, double End)[] spans)
    {
        int words = last - first + 1;
        if (words == 1 || levels.Count == 0)
        {
            Even(run, onsets, first, last, spans);
            return;
        }
        double step = 1.0 / levels.Rate;
        int from = (int)Math.Round(run.Start * levels.Rate), to = (int)Math.Round(run.End * levels.Rate);
        int shortest = Math.Max(1, (int)Math.Round(ShortestWord * levels.Rate));
        if (to - from < words * shortest)
        {
            Even(run, onsets, first, last, spans);
            return;
        }
        // The run's speech level, for how quiet a moment is against it.
        var inRun = Enumerable.Range(from, to - from).Select(k => levels.At((k + 0.5) * step)).Order().ToList();
        double loud = inRun[(int)(inRun.Count * 0.8)];
        int reach = (int)Math.Round(0.35 * levels.Rate);
        double Length(int a, int b, int word) => 4 / Math.Log(2) * Math.Abs(Math.Log(Math.Max(1, b - a) * step / expected[word]));

        // The frames each parting can fall on: near where its word was heard to start, room left for the words around it.
        var candidates = new int[words][];
        for (int w = 1; w < words; w++)
        {
            int target = (int)Math.Round(onsets[first + w] * levels.Rate);
            int lo = Math.Max(from + w * shortest, target - reach), hi = Math.Min(to - (words - w) * shortest, target + reach);
            if (hi < lo)
                (lo, hi) = (Math.Clamp(target, from + w * shortest, to - (words - w) * shortest), Math.Clamp(target, from + w * shortest, to - (words - w) * shortest));
            candidates[w] = Enumerable.Range(lo, hi - lo + 1).ToArray();
        }
        candidates[0] = [from];
        var best = new double[words][];
        var back = new int[words][];
        best[0] = [0];
        back[0] = [-1];
        for (int w = 1; w < words; w++)
        {
            int target = (int)Math.Round(onsets[first + w] * levels.Rate);
            best[w] = new double[candidates[w].Length];
            back[w] = new int[candidates[w].Length];
            for (int c = 0; c < candidates[w].Length; c++)
            {
                int at = candidates[w][c];
                double here = 20 * Math.Abs(at - target) * step + (levels.At((at + 0.5) * step) - loud);
                double least = double.PositiveInfinity;
                int from1 = -1;
                for (int p = 0; p < candidates[w - 1].Length; p++)
                {
                    int before = candidates[w - 1][p];
                    if (at - before < shortest || double.IsPositiveInfinity(best[w - 1][p]))
                        continue;
                    double total = best[w - 1][p] + Length(before, at, first + w - 1);
                    if (total < least)
                        (least, from1) = (total, p);
                }
                best[w][c] = least + here;
                back[w][c] = from1;
            }
        }
        // The last word runs to the end of the run.
        int end = -1;
        double least2 = double.PositiveInfinity;
        for (int c = 0; c < candidates[words - 1].Length; c++)
        {
            double total = best[words - 1][c] + Length(candidates[words - 1][c], to, last);
            if (total < least2)
                (least2, end) = (total, c);
        }
        if (end < 0)
        {
            Even(run, onsets, first, last, spans);
            return;
        }
        var starts = new double[words];
        for (int w = words - 1, c = end; w >= 0; c = back[w][c], w--)
            starts[w] = w == 0 ? run.Start : candidates[w][c] * step;
        for (int w = 0; w < words; w++)
            spans[first + w] = (starts[w], w + 1 < words ? starts[w + 1] : run.End);
    }

    /// <summary>A run parted at where its words were heard to start (when the levels can't say better).</summary>
    private static void Even((double Start, double End) run, double[] onsets, int first, int last, (double Start, double End)[] spans)
    {
        double previous = run.Start;
        for (int i = first; i <= last; i++)
        {
            double start = i == first ? run.Start
                : Math.Clamp(onsets[i], previous + 0.01, Math.Max(previous + 0.01, run.End - (last - i + 1) * 0.01));
            if (i > first)
                spans[i - 1] = (spans[i - 1].Start, start);
            spans[i] = (start, run.End);
            previous = start;
        }
    }

    private static void Pause(double from, double to, List<EditToken> tokens)
    {
        if (to - from >= PauseFrom)
            tokens.Add(new EditToken(EditTokenKind.Pause, "", from, to));
    }

    // ---------- what to take out ----------

    // Hesitations as Whisper writes them, in English and Arabic.
    private static readonly HashSet<string> Hesitations = new(StringComparer.Ordinal)
    {
        "um", "umm", "uh", "uhh", "uhm", "er", "erm", "ah", "eh", "hmm", "hm", "mm", "mmm", "mhm",
        "إمم", "امم", "اممم", "ام", "آآ", "اا", "ااا", "آه", "اه", "ممم", "مم", "إيه", "ايه", "ءء", "أأ",
    };

    /// <summary>
    /// The pieces a clean-up takes out: hesitation words ("um", "uh", "إمم"), a word said twice in a row ("I I think", "في
    /// في"), the first time; and the sounds between words that weren't words.
    /// </summary>
    public static HashSet<int> Fillers(IReadOnlyList<EditToken> tokens)
    {
        var result = new HashSet<int>();
        int last = -1;
        for (int i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (token.Kind == EditTokenKind.Sound)
                result.Add(i);
            if (token.Kind != EditTokenKind.Word)
                continue;
            var plain = Plain(token.Text);
            if (Hesitations.Contains(plain))
                result.Add(i);
            else if (last >= 0 && plain.Length > 0 && plain == Plain(tokens[last].Text) && token.Start - tokens[last].End < 0.6 && !result.Contains(last))
                result.Add(last);
            last = i;
        }
        return result;
    }

    /// <summary>Whether what Whisper heard in a sound on its own is no word at all: nothing, or only hesitations ("Uh…", "إمم").</summary>
    public static bool NoWords(string heard) =>
        heard.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(Plain).Where(w => w.Length > 0).All(Hesitations.Contains);

    /// <summary>
    /// The pieces once the sounds that turned out to hold words (<paramref name="words"/>: what was heard in each, by its
    /// place) are no longer sounds to take out: one touching a word goes with it (the word after it first: Whisper hears
    /// words late more often than early), one on its own between pauses becomes a word of its own, as it was heard.
    /// </summary>
    public static List<EditToken> Unsound(IReadOnlyList<EditToken> tokens, IReadOnlyDictionary<int, string> words)
    {
        var result = tokens.ToList();
        var gone = new HashSet<int>();
        foreach (var (i, heard) in words.Where(w => w.Key >= 0 && w.Key < tokens.Count && tokens[w.Key].Kind == EditTokenKind.Sound).OrderBy(w => w.Key))
        {
            bool next = i + 1 < result.Count && result[i + 1].Kind == EditTokenKind.Word && !gone.Contains(i + 1);
            bool previous = i > 0 && result[i - 1].Kind == EditTokenKind.Word && !gone.Contains(i - 1);
            if (next)
                result[i + 1] = result[i + 1] with { Start = result[i].Start };
            else if (previous)
                result[i - 1] = result[i - 1] with { End = result[i].End };
            else
            {
                result[i] = result[i] with { Kind = EditTokenKind.Word, Text = heard.Trim() };
                continue;
            }
            gone.Add(i);
        }
        return result.Where((_, k) => !gone.Contains(k)).ToList();
    }

    /// <summary>The pauses longer than <paramref name="longest"/> seconds, to be shortened.</summary>
    public static HashSet<int> LongPauses(IReadOnlyList<EditToken> tokens, double longest) =>
        Enumerable.Range(0, tokens.Count).Where(i => tokens[i].Kind == EditTokenKind.Pause && tokens[i].Length > longest).ToHashSet();

    /// <summary>A word as it is compared: lower case, no punctuation or vowel marks.</summary>
    public static string Plain(string word)
    {
        var chars = word.ToLower(CultureInfo.InvariantCulture).Where(c => char.IsLetterOrDigit(c) && c is not (>= (char)0x064B and <= (char)0x065F));
        return new string(chars.ToArray());
    }

    // ---------- what is kept ----------

    /// <summary>
    /// The stretches kept when the pieces in <paramref name="removed"/> are taken out, in order. Taking out a word takes its
    /// time and the silence around it, keeping <see cref="Margin"/> beside each word left; a pause taken out is shortened to
    /// <see cref="ShortPause"/>.
    /// </summary>
    public static List<(double Start, double End)> Keep(IReadOnlyList<EditToken> tokens, IReadOnlySet<int> removed, double length)
    {
        var kept = new List<(double Start, double End)>();
        void Add(double from, double to)
        {
            from = Math.Clamp(from, 0, length);
            to = Math.Clamp(to, 0, length);
            if (to <= from)
                return;
            if (kept.Count > 0 && from <= kept[^1].End + 1e-6)
                kept[^1] = (kept[^1].Start, Math.Max(kept[^1].End, to));
            else
                kept.Add((from, to));
        }
        double cursor = 0;
        // The start of the recording counts as kept: silence before a word left out goes with it.
        bool before = true;
        for (int i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            bool keep = !removed.Contains(i);
            double gap = Math.Max(0, token.Start - cursor);
            // The little silence between two pieces: kept between two pieces kept, a margin of it beside one kept.
            if (before && keep)
                Add(cursor, token.Start);
            else if (before)
                Add(cursor, cursor + Math.Min(Margin, gap));
            else if (keep)
                Add(token.Start - Math.Min(Margin, gap), token.Start);

            if (keep)
                Add(token.Start, token.End);
            else if (token.Kind == EditTokenKind.Pause && token.Length > ShortPause)
            {
                Add(token.Start, token.Start + ShortPause / 2);
                Add(token.End - ShortPause / 2, token.End);
            }
            else if (token.Kind == EditTokenKind.Pause)
                Add(token.Start, token.End);
            cursor = Math.Max(cursor, token.End);
            before = keep || token.Kind == EditTokenKind.Pause;
        }
        if (before)
            Add(cursor, length);
        return kept.Where(k => k.End - k.Start >= 0.02).ToList();
    }

    /// <summary>Where a moment of the original falls in the edited recording (null when it was cut out).</summary>
    public static double? EditedTime(IReadOnlyList<(double Start, double End)> kept, double t)
    {
        double before = 0;
        foreach (var (start, end) in kept)
        {
            if (t < start)
                return null;
            if (t <= end)
                return before + t - start;
            before += end - start;
        }
        return null;
    }

    // ---------- the cut ----------

    /// <summary>
    /// The recording with only <paramref name="kept"/> left, written to <paramref name="output"/> in the container its name
    /// asks for. The sound is cut where asked, in the quiet between words; the picture in whole frames, as many for each
    /// stretch as keep it with the sound so far, so they are never half a frame apart however many cuts there are.
    /// </summary>
    public static async Task<string> RenderAsync(string input, string output, IReadOnlyList<(double Start, double End)> kept, TextCutOptions? options = null,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        options ??= new TextCutOptions();
        var info = await ToolSupport.ProbeAsync(input, output, cancellationToken).ConfigureAwait(false);
        if (kept.Count == 0)
            throw new MediaException("Nothing would be left of this recording.");
        double rate = info.Video is not null && info.FrameRate is > 1 and < 1000 ? info.FrameRate : 0;
        var sound = kept.Where(k => k.End > k.Start).OrderBy(k => k.Start).ToList();
        double length = sound.Sum(k => k.End - k.Start);
        var target = Containers.ForOutput(output, input);
        var plan = ToolSupport.Plan(input, info, target, options.EffectiveSettings) with
        {
            VideoFilter = info.Video is null ? null : $"select='{PauseRemover.Expression(rate > 0 ? Frames(sound, rate) : sound)}',setpts=N/FRAME_RATE/TB",
            AudioFilter = info.HasAudio ? PauseRemover.AudioSelect(PauseRemover.Expression(sound)) : null,
            AllAudioTracks = false,
            Subtitles = false,
            Chapters = false,
            Duration = length,
        };
        await MediaEncoder.RunAsync(plan, output, new SafeProgress(progress), cancellationToken).ConfigureAwait(false);
        return output;
    }

    /// <summary>
    /// The frames that go with stretches of sound: each stretch from the frame nearest its start (or the one after the last
    /// frame taken), for as many frames as bring the picture so far to the sound so far. Each range runs from half a frame
    /// before its first frame to half a frame before the next, so the frames' times sit well inside it.
    /// </summary>
    internal static List<(double Start, double End)> Frames(IReadOnlyList<(double Start, double End)> sound, double rate)
    {
        var frames = new List<(double Start, double End)>();
        double heard = 0;
        long made = 0, next = 0;
        foreach (var (start, end) in sound)
        {
            heard += end - start;
            long count = (long)Math.Round(heard * rate) - made;
            if (count <= 0)
                continue;
            long from = Math.Max((long)Math.Round(start * rate), next);
            frames.Add(((from - 0.5) / rate, (from + count - 0.5) / rate));
            made += count;
            next = from + count;
        }
        return frames;
    }
}
