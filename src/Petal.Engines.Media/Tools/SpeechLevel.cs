namespace Petal.Engines.Media;

/// <summary>
/// Brings speech up to the level speech models were made for. A video played quietly, a soft voice after a loud one,
/// a microphone far away: the words are all there, forty or fifty decibels down, and a detector that listens for
/// loudness hears nothing. Each moment is raised by what the sound around it needs, the quiet between words by no
/// more than a little, so a pause stays a pause.
/// </summary>
internal static class SpeechLevel
{
    private const int Block = StreamingTranscriber.Rate / 10;

    /// <summary>The sound with its speech at about <paramref name="target"/> at its peaks, whatever it came in at.</summary>
    public static float[] Level(ReadOnlySpan<float> sound, float target = 0.25f)
    {
        var levelled = new float[sound.Length];
        if (sound.Length == 0)
            return levelled;
        int blocks = (sound.Length + Block - 1) / Block;
        var peaks = new float[blocks];
        for (int b = 0; b < blocks; b++)
        {
            float peak = 0;
            int end = Math.Min(sound.Length, (b + 1) * Block);
            for (int i = b * Block; i < end; i++)
                peak = Math.Max(peak, Math.Abs(sound[i]));
            peaks[b] = peak;
        }
        // The quiet of this sound: its quietest tenth. Nothing is raised as if it were speech unless it stands well clear
        // of that, or a pause would come out as loud as the words.
        var sorted = (float[])peaks.Clone();
        Array.Sort(sorted);
        float loudest = sorted[^1];
        if (loudest < 1e-5f)
            return levelled;
        float quiet = sorted[blocks / 10];
        float floor = Math.Clamp(quiet * 4, 3e-5f, loudest * 0.5f);
        var gains = new float[blocks];
        for (int b = 0; b < blocks; b++)
        {
            // The loudest within four tenths of a second either side: a word isn't raised and lowered syllable by syllable.
            float around = 0;
            for (int k = Math.Max(0, b - 4); k <= Math.Min(blocks - 1, b + 4); k++)
                around = Math.Max(around, peaks[k]);
            gains[b] = target / Math.Max(around, floor);
        }
        for (int i = 0; i < sound.Length; i++)
        {
            // From one block's middle to the next, so the level never steps.
            double at = (i - Block / 2.0) / Block;
            int b = (int)Math.Floor(at);
            float t = (float)(at - b);
            float a = gains[Math.Clamp(b, 0, blocks - 1)], c = gains[Math.Clamp(b + 1, 0, blocks - 1)];
            levelled[i] = Math.Clamp(sound[i] * (a + (c - a) * t), -1f, 1f);
        }
        return levelled;
    }

    /// <summary>
    /// The quietest moment between two points of a sound (in samples): where it can be cut without cutting a word.
    /// </summary>
    public static int Quietest(ReadOnlySpan<float> sound, int from, int to)
    {
        const int Window = StreamingTranscriber.Rate * 40 / 1000, Hop = StreamingTranscriber.Rate * 10 / 1000;
        from = Math.Clamp(from, 0, sound.Length);
        to = Math.Clamp(to, from, sound.Length);
        if (to - from <= Window)
            return (from + to) / 2;
        int best = from + Window / 2;
        double least = double.MaxValue;
        for (int i = from; i + Window <= to; i += Hop)
        {
            double sum = 0;
            for (int k = 0; k < Window; k++)
                sum += sound[i + k] * sound[i + k];
            // Of two moments as quiet, the later: more of the finished sentence's last word is let go with it.
            if (sum <= least * 1.02)
            {
                least = Math.Min(least, sum);
                best = i + Window / 2;
            }
        }
        return best;
    }

    /// <summary>
    /// Where there is sound that could be speech, by loudness alone, for when the model that tells speech from other
    /// sound isn't here: the stretches of a levelled sound that stand clear of its own quiet, as seconds from its start.
    /// How loud counts is read from the sound itself (between its quietest tenth and its loudest moments), since a
    /// synthetic voice over digital silence and a far microphone in a noisy room share no fixed level. A sound that is
    /// the same all through (a hum, a held chord) has nothing standing clear, and so no speech.
    /// </summary>
    public static List<(double Start, double End)> Loud(ReadOnlySpan<float> levelled)
    {
        const int Window = StreamingTranscriber.Rate * 30 / 1000;
        var stretches = new List<(double, double)>();
        int frames = levelled.Length / Window;
        if (frames == 0)
            return stretches;
        var levels = new double[frames];
        for (int f = 0; f < frames; f++)
        {
            double sum = 0;
            for (int k = 0; k < Window; k++)
                sum += levelled[f * Window + k] * levelled[f * Window + k];
            levels[f] = Math.Sqrt(sum / Window);
        }
        var sorted = (double[])levels.Clone();
        Array.Sort(sorted);
        double quietest = Math.Max(sorted[frames / 10], 1e-5), loudest = sorted[Math.Min(frames - 1, frames * 95 / 100)];
        if (loudest < 0.01 || loudest < quietest * 2.5)
            return stretches;
        double level = Math.Max(Math.Sqrt(quietest * loudest), quietest * 2);
        // A quarter of a second of quiet ends a stretch: less is the gap inside a word ("s", "t").
        int hold = StreamingTranscriber.Rate / 4 / Window, shortest = StreamingTranscriber.Rate * 12 / 100;
        int start = -1, quiet = 0;
        for (int f = 0; f < frames; f++)
        {
            if (levels[f] > level)
            {
                if (start < 0)
                    start = f;
                quiet = 0;
            }
            else if (start >= 0 && ++quiet >= hold)
            {
                int end = f - quiet + 1;
                if ((end - start) * Window >= shortest)
                    stretches.Add(((double)start * Window / StreamingTranscriber.Rate, (double)end * Window / StreamingTranscriber.Rate));
                start = -1;
                quiet = 0;
            }
        }
        if (start >= 0 && (frames - quiet - start) * Window >= shortest)
            stretches.Add(((double)start * Window / StreamingTranscriber.Rate, (double)(frames - quiet) * Window / StreamingTranscriber.Rate));
        return stretches;
    }
}
