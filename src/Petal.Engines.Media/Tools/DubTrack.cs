using System.Globalization;
using Petal.Engines.Media.Internal;

namespace Petal.Engines.Media;

/// <summary>A line of speech to dub: when it is said in the video, what was said, and what is to be said instead.</summary>
public sealed record DubLine(double Start, double End, string Original, string Translation)
{
    public double Length => End - Start;
}

/// <summary>Where a dubbed line goes in the video, and how much quicker it is said to fit.</summary>
public readonly record struct DubPlace(double At, double Speed, double Length);

/// <summary>
/// The dubbed voice laid over the video's own sound. Lines are cut from what Whisper heard (whole sentences, none longer than
/// twelve seconds); each new line goes where its original began, said up to 1.3 times quicker (keeping its pitch) when it is
/// longer than the room before the next, and pushes that one on a little when even that isn't enough. Its loudness follows
/// the original's (a whisper stays a whisper). The speaker's own sounds between lines (a laugh, a breath) stay; under the
/// lines the original voice gives way, and it all goes over the video's other sounds (whatever the caller keeps of them).
/// </summary>
public static class DubTrack
{
    /// <summary>The longest line.</summary>
    public const double LongestLine = 12;

    /// <summary>How much quicker a line may be said to fit its room.</summary>
    public const double Quickest = 1.3;

    /// <summary>The lines to dub, from what was said with each word's time.</summary>
    public static List<DubLine> Lines(IReadOnlyList<TranscriptSegment> segments)
    {
        var words = ShortMoments.Clean(segments);
        if (words.Count == 0)
            return [];
        var sentences = ShortMoments.Sentences(words);
        // Long sentences in pieces of at most twelve seconds, at their longest pauses (or commas).
        var pieces = new List<(int First, int Last)>();
        foreach (var s in sentences)
            Cut(words, s.First, s.Last, pieces);
        // A scrap of a line (a "yes", two words) joins the next when it follows closely, and a sentence broken off by a pause
        // ("and it's so … awesome") joins the rest of it: each is translated and said better whole. (Where Whisper wrote no
        // full stops at all, a line's end says nothing about its sentence.)
        bool punctuated = words.Count(w => ShortMoments.EndsSentence(w.Text)) * 45 >= words.Count;
        var lines = new List<(int First, int Last)>();
        foreach (var piece in pieces)
        {
            if (lines.Count > 0)
            {
                var (first, last) = lines[^1];
                bool scrap = last - first < 2 || words[last].End - words[first].Start < 1.2;
                bool unfinished = punctuated && !ShortMoments.EndsSentence(words[last].Text) && !words[last].Text.EndsWith(',') && !words[last].Text.EndsWith('،');
                double gap = words[piece.First].Start - words[last].End;
                if ((scrap && gap < 0.35 || unfinished && gap < 2.5) && !words[piece.First].Turn && words[piece.Last].End - words[first].Start <= LongestLine)
                {
                    lines[^1] = (first, piece.Last);
                    continue;
                }
            }
            lines.Add(piece);
        }
        return lines.Select(l => new DubLine(words[l.First].Start, words[l.Last].End,
            string.Join(' ', Enumerable.Range(l.First, l.Last - l.First + 1).Select(k => words[k].Text.Replace(ShortMoments.LaughMark, ""))), "")).ToList();
    }

    private static void Cut(IReadOnlyList<ShortMoments.Word> words, int first, int last, List<(int, int)> into)
    {
        if (words[last].End - words[first].Start <= LongestLine || last - first < 3)
        {
            into.Add((first, last));
            return;
        }
        int best = -1;
        double bestScore = -1;
        for (int i = first + 1; i < last - 1; i++)
        {
            double score = words[i + 1].Start - words[i].End + (words[i].Text.EndsWith(',') || words[i].Text.EndsWith('،') ? 0.3 : 0)
                           - Math.Abs((words[i].End - words[first].Start) / (words[last].End - words[first].Start) - 0.5) * 0.2;
            if (score > bestScore)
            {
                bestScore = score;
                best = i;
            }
        }
        Cut(words, first, best, into);
        Cut(words, best + 1, last, into);
    }

    /// <summary>
    /// Where each dubbed line goes: at its original start, or just after the line before when that one ran over; said
    /// quicker (at most <see cref="Quickest"/>) when it is longer than the room before the next line's start.
    /// </summary>
    /// <param name="lines">Each line's original start and the length of its new sound.</param>
    public static List<DubPlace> Place(IReadOnlyList<(double Start, double Length)> lines, double total)
    {
        var places = new List<DubPlace>(lines.Count);
        // When the voice is free again (before the first line, always).
        double free = -1;
        for (int i = 0; i < lines.Count; i++)
        {
            var (start, length) = lines[i];
            double at = Math.Max(start, free + 0.06);
            double next = i + 1 < lines.Count ? lines[i + 1].Start : total;
            double room = Math.Max(0.2, next - at - 0.05);
            double speed = length > room ? Math.Min(Quickest, length / room) : 1;
            double said = length / speed;
            places.Add(new DubPlace(at, speed, said));
            free = at + said;
        }
        return places;
    }

    /// <summary>The sound without the quiet the voice model leaves before and after the words (a little of it kept).</summary>
    public static float[] Trim(float[] sound, int rate)
    {
        int frame = rate / 100;
        int frames = sound.Length / frame;
        if (frames < 3)
            return sound;
        var db = new double[frames];
        double loudest = -120;
        for (int f = 0; f < frames; f++)
        {
            double sum = 0;
            for (int i = f * frame; i < (f + 1) * frame; i++)
                sum += sound[i] * sound[i];
            db[f] = 10 * Math.Log10(sum / frame + 1e-12);
            loudest = Math.Max(loudest, db[f]);
        }
        // Nothing said at all.
        if (loudest < -60)
            return [];
        double floor = loudest - 38;
        int first = Array.FindIndex(db, d => d > floor), last = Array.FindLastIndex(db, d => d > floor);
        int from = Math.Max(0, (first - 4) * frame), to = Math.Min(sound.Length, (last + 9) * frame);
        return sound[from..to];
    }

    /// <summary>The loudness of speech in some sound (its loud tenths of a second), in decibels.</summary>
    public static double SpeechLevel(ReadOnlySpan<float> sound, int rate)
    {
        int frame = Math.Max(1, rate / 10);
        var levels = new List<double>();
        for (int f = 0; (f + 1) * frame <= sound.Length; f++)
        {
            double sum = 0;
            foreach (var s in sound.Slice(f * frame, frame))
                sum += s * s;
            levels.Add(10 * Math.Log10(sum / frame + 1e-12));
        }
        if (levels.Count == 0)
            return -90;
        levels.Sort();
        // The louder half: speech, not the pauses within it.
        var loud = levels.Skip(levels.Count / 2).ToList();
        return 10 * Math.Log10(loud.Average(d => Math.Pow(10, d / 10)));
    }

    /// <summary>
    /// The new voice track: the original voice where no line is dubbed (faded out under each line and back after it), the
    /// dubbed lines at their places, all at <paramref name="rate"/>.
    /// </summary>
    /// <param name="original">The original voice (the separated vocals), mono.</param>
    /// <param name="takes">Each line's sound (already at <paramref name="rate"/> and speed), where it goes, and the original line's span.</param>
    public static float[] Mix(float[] original, IReadOnlyList<(float[] Sound, double At, double OriginalStart, double OriginalEnd)> takes, int rate)
    {
        var track = new float[original.Length];
        // How much of the original voice stays: 0 under the dubbed lines and their originals, ramped over 60 ms.
        var keep = new float[original.Length];
        Array.Fill(keep, 1f);
        int ramp = rate * 60 / 1000;
        void Silence(double from, double to)
        {
            int a = Math.Clamp((int)(from * rate), 0, keep.Length), b = Math.Clamp((int)(to * rate), 0, keep.Length);
            for (int i = Math.Max(0, a - ramp); i < Math.Min(keep.Length, b + ramp); i++)
            {
                float weight = i < a ? (float)(a - i) / ramp : i >= b ? (float)(i - b + 1) / ramp : 0;
                keep[i] = Math.Min(keep[i], weight);
            }
        }
        foreach (var take in takes)
        {
            Silence(take.OriginalStart - 0.05, take.OriginalEnd + 0.12);
            Silence(take.At, take.At + take.Sound.Length / (double)rate);
        }
        for (int i = 0; i < original.Length; i++)
            track[i] = original[i] * keep[i];
        int fade = rate * 8 / 1000;
        foreach (var take in takes)
        {
            int at = (int)Math.Round(take.At * rate);
            for (int i = 0; i < take.Sound.Length && at + i < track.Length; i++)
            {
                if (at + i < 0)
                    continue;
                // A few milliseconds faded at each end: no click.
                float gain = i < fade ? (float)i / fade : take.Sound.Length - i < fade ? (float)(take.Sound.Length - i) / fade : 1;
                track[at + i] += take.Sound[i] * gain;
            }
        }
        return track;
    }

    /// <summary>
    /// A line's sound at another rate and quicker (pitch kept, by FFmpeg's atempo), through FFmpeg, as loud as it was:
    /// stretching leaves speech a decibel or more quieter, which is given back.
    /// </summary>
    public static async Task<float[]> ReshapeAsync(float[] sound, int fromRate, int toRate, double speed, CancellationToken cancellationToken)
    {
        var folder = Path.Combine(Path.GetTempPath(), "Petal", "dub-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(folder);
        try
        {
            var input = Path.Combine(folder, "in.raw");
            var output = Path.Combine(folder, "out.raw");
            await File.WriteAllBytesAsync(input, System.Runtime.InteropServices.MemoryMarshal.AsBytes(sound.AsSpan()).ToArray(), cancellationToken).ConfigureAwait(false);
            var filter = Math.Abs(speed - 1) > 0.01
                ? $"{Tempo(speed)},aresample={toRate}"
                : $"aresample={toRate}";
            List<string> args = ["-f", "f32le", "-ar", fromRate.ToString(CultureInfo.InvariantCulture), "-ac", "1", "-i", input, "-af", filter, "-f", "f32le", "-ar", toRate.ToString(CultureInfo.InvariantCulture), "-ac", "1", output];
            await Ffmpeg.RunAsync(args, 0, null, cancellationToken).ConfigureAwait(false);
            var bytes = await File.ReadAllBytesAsync(output, cancellationToken).ConfigureAwait(false);
            var result = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(bytes).ToArray();
            if (Math.Abs(speed - 1) > 0.01 && result.Length > 0)
            {
                double lost = Math.Clamp(SpeechLevel(sound, fromRate) - SpeechLevel(result, toRate), -6, 6);
                var gain = (float)Math.Pow(10, lost / 20);
                for (int i = 0; i < result.Length; i++)
                    result[i] *= gain;
            }
            return result;
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>atempo stages for a speed: each between half and twice.</summary>
    internal static string Tempo(double speed)
    {
        var stages = new List<string>();
        var f = speed;
        while (f > 2.0) { stages.Add("atempo=2"); f /= 2; }
        while (f < 0.5) { stages.Add("atempo=0.5"); f /= 0.5; }
        stages.Add($"atempo={f.ToString("0.#####", CultureInfo.InvariantCulture)}");
        return string.Join(',', stages);
    }

    /// <summary>Samples (interleaved, when there are several channels) as a 32-bit float WAV file.</summary>
    public static void WriteWav(string path, ReadOnlySpan<float> samples, int rate, int channels = 1)
    {
        using var file = new BinaryWriter(File.Create(path));
        file.Write("RIFF"u8);
        file.Write(36 + samples.Length * 4);
        file.Write("WAVEfmt "u8);
        file.Write(16);
        file.Write((short)3);
        file.Write((short)channels);
        file.Write(rate);
        file.Write(rate * 4 * channels);
        file.Write((short)(4 * channels));
        file.Write((short)32);
        file.Write("data"u8);
        file.Write(samples.Length * 4);
        file.Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes(samples));
    }

    /// <summary>
    /// The dubbed video: its picture as it was, the sounds given (all but the voices) with the new voice track over them, and, when
    /// asked, the original sound as a second track and subtitles of the new words. The file says the voices were made by AI.
    /// </summary>
    public static async Task<string> MuxAsync(string video, string sounds, string voices, string output, string language, string? originalTitle,
        string? subtitles, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var info = await ToolSupport.ProbeAsync(video, output, cancellationToken).ConfigureAwait(false);
        ToolSupport.RequireVideo(info);
        string code = Transcriber.Iso639_2(language);
        List<string> args = ["-i", video, "-i", sounds, "-i", voices];
        if (subtitles is not null)
            args.AddRange(["-i", subtitles]);
        // Mono sound goes to both sides as it is (FFmpeg's own way of making it stereo turns it 3 dB down).
        var soundsInfo = await MediaProbe.ProbeAsync(sounds, cancellationToken).ConfigureAwait(false);
        string toStereo = soundsInfo.Audio?.Channels == 1 ? "pan=stereo|c0=c0|c1=c0" : "aformat=channel_layouts=stereo";
        args.AddRange(["-filter_complex", $"[1:a]aresample=48000,{toStereo}[m];[2:a]aresample=48000,pan=stereo|c0=c0|c1=c0[v];[m][v]amix=inputs=2:normalize=0:duration=first,alimiter=limit=0.97:level=false[dub]"]);
        args.AddRange(["-map", $"0:{info.Video!.Index}", "-map", "[dub]"]);
        if (originalTitle is not null && info.Audio is { } original)
            args.AddRange(["-map", $"0:{original.Index}"]);
        if (subtitles is not null)
            args.AddRange(["-map", "3:0"]);
        // The picture as it was when MP4 can hold it; otherwise H.264.
        bool copy = info.VideoCodec is "h264" or "hevc" or "av1" or "vp9" or "mpeg4";
        args.AddRange(copy ? ["-c:v", "copy"] : MediaEncoders.H264(20, hardware: false));
        if (copy && info.VideoCodec == "hevc")
            args.AddRange(["-tag:v", "hvc1"]);
        // Each track named as players show it (MP4 keeps a track's name as its handler's).
        string dubbed = $"{CultureInfo.GetCultureInfo(language).EnglishName} (dubbed)";
        args.AddRange([.. MediaEncoders.Aac(192, 2, 48000), "-metadata:s:a:0", $"language={code}", "-metadata:s:a:0", $"title={dubbed}",
            "-metadata:s:a:0", $"handler_name={dubbed}", "-disposition:a:0", "default"]);
        if (originalTitle is not null && info.Audio is not null)
            args.AddRange(["-metadata:s:a:1", $"title={originalTitle}", "-metadata:s:a:1", $"handler_name={originalTitle}", "-disposition:a:1", "0"]);
        // The words in the language now heard (MP4 shows the first subtitle track to players as on, whatever is asked).
        if (subtitles is not null)
            args.AddRange(["-c:s", "mov_text", "-metadata:s:s:0", $"language={code}"]);
        args.AddRange(["-map_metadata", "0", "-metadata", "comment=Dubbed by DropWheel: the voices were made by AI from the speakers' own.", "-movflags", "+faststart"]);
        await Ffmpeg.EncodeAsync(args, output, "mp4", info.DurationSeconds, new SafeProgress(progress), cancellationToken).ConfigureAwait(false);
        return output;
    }
}
