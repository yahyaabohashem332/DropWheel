using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Petal.Core.Models;
using Petal.Engines.Media;
using Petal.Recipes.Translation;

namespace Petal.Recipes;

/// <summary>
/// A video made ready to dub: its voices apart from the rest of its sound (<paramref name="Rest"/>: the music and every other
/// sound), what is said in it, line by line, with each line's translation.
/// </summary>
/// <summary>What is spoken turned out to need translation models that aren't here; what was heard is kept for the next try.</summary>
public sealed class DubModelsMissing(string language) : InvalidOperationException($"No translation models for {language} yet.")
{
    public string Language { get; } = language;
}

public sealed record DubSource(string Video, string Language, string Into, string Vocals, string Rest, IReadOnlyList<DubLine> Lines, double Length)
{
    /// <summary>Where the lines are kept, to keep the translations as they were checked.</summary>
    public string? Kept { get; init; }
}

/// <param name="KeepOriginal">The original sound (its music too) kept as a second track, for players that let the viewer choose.</param>
/// <param name="Subtitles">The new words as subtitles in the file.</param>
public sealed record DubOptions(bool KeepOriginal = false, bool Subtitles = true);

/// <summary>
/// Dub: a video's speech said again in another language (from and into <see cref="Languages"/>) in each speaker's own
/// voice, with the music taken out and every other sound kept. Prepared once (<see cref="PrepareAsync"/>): the voices are
/// separated from the rest of the sound by Demucs, written down with each word's time by Whisper, cut into lines and
/// translated by the translation models, all on this PC, and kept so the same video opens again at once. Then dubbed
/// (<see cref="DubAsync"/>) after the translations have been checked: the music is taken out of the rest by TIGER (once,
/// and kept), each line is said by Chatterbox in the voice of the person who said it (learnt from that very line), heard
/// back by Whisper and said again when it came out wrong, fitted into its place, and mixed over the sounds that are left.
/// </summary>
public static class Dubbing
{
    /// <summary>Where prepared videos are kept (their voices, the rest of their sound, and lines).</summary>
    public static string CacheFolder { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Petal", "cache", "dub");

    /// <summary>How many prepared videos are kept; the oldest are let go (each holds two tracks of its sound).</summary>
    private const int Remembered = 6;

    private const int CacheVersion = 2;

    /// <summary>What dubbing needs beyond the speech model: the voice model and the two separation models.</summary>
    public static IReadOnlyList<ModelInfo> Models { get; } = [.. ModelCatalog.Chatterbox, ModelCatalog.Demucs, ModelCatalog.Tiger];

    public static bool Ready => Models.All(ModelStore.IsAvailable);

    /// <summary>
    /// Where preparing's steps end on its way from 0 to 1: separating the voices, then listening, then translating. A
    /// translation by the AI model takes a good while, so it is given more of the way.
    /// </summary>
    public static (double Separated, double Heard) Steps(bool natural) => natural ? (0.3, 0.6) : (0.45, 0.85);

    /// <summary>A dub goes from and into every language of the interface.</summary>
    public static IReadOnlyList<string> Languages { get; } = Translator.Languages.Select(l => l.Code).ToList();

    /// <summary>The language a dub goes into from one spoken when none was chosen: English, and from English the one given.</summary>
    public static string Opposite(string spoken, string fromEnglish = "ar") => spoken != "en" ? "en" : fromEnglish;

    // ---------- preparing ----------

    /// <summary>
    /// Separates, hears and translates the video (or takes all that from the last time, for the same video, speech model,
    /// language and translation).
    /// </summary>
    /// <param name="spoken">"auto", or the language spoken.</param>
    /// <param name="into">A language's code; "auto" for English, or from English into Arabic.</param>
    /// <param name="natural">Translated by the AI model, by meaning (<see cref="DubTranslation"/>), rather than word for word.</param>
    /// <param name="otherwise">Where to go instead when what is spoken turns out to be <paramref name="into"/>, for a language nobody chose.</param>
    public static async Task<DubSource> PrepareAsync(string video, string model, string spoken, string into, IProgress<double>? progress = null,
        CancellationToken cancellationToken = default, bool natural = false, string? otherwise = null)
    {
        natural &= DubTranslation.Ready;
        var (separatedAt, heardAt) = Steps(natural);
        var folder = Path.Combine(CacheFolder, Key(video));
        Directory.CreateDirectory(folder);
        Touch(folder);
        // Demucs names the rest of the sound Music.
        string vocals = Path.Combine(folder, "Vocals.wav"), rest = Path.Combine(folder, "Music.wav");
        if (!File.Exists(vocals) || !File.Exists(rest))
        {
            foreach (var old in Directory.GetFiles(folder, "*.wav"))
                File.Delete(old);
            await StemSplitter.SplitAsync(video, folder, "wav", new SplitStemsOptions(StemSet.VocalsAndMusic), Slice(progress, 0, separatedAt), cancellationToken).ConfigureAwait(false);
        }
        progress?.Report(separatedAt);
        // The voice model loads while the rest is prepared and checked: ready when the dub starts.
        Voices.WarmUp();

        var heard = Path.Combine(folder, $"heard-{Path.GetFileNameWithoutExtension(model)}-{spoken}.json");
        List<DubLine> lines;
        string language;
        if (Read(heard) is { } known)
            (language, lines) = known;
        else
        {
            var speech = (await MediaPreview.DecodePcmAsync(vocals, new PcmDecodeOptions(16000, 1), cancellationToken).ConfigureAwait(false)).Samples;
            var transcript = await Transcriber.TranscribeWordsAsync(speech, new TranscribeOptions(model, spoken, WordTimes: true), null,
                Slice(progress, separatedAt, heardAt), cancellationToken).ConfigureAwait(false);
            language = transcript.Language;
            lines = DubTrack.Lines(transcript.Segments);
            Write(heard, language, lines);
        }
        progress?.Report(heardAt);

        var target = TargetFor(into, language, otherwise);
        if (target == language)
            throw new InvalidOperationException("This video is already in that language.");
        if (!Languages.Contains(language) || !Languages.Contains(target))
            throw new InvalidOperationException("Dub doesn't work on this language yet.");
        // Each kind of translation kept apart: the one by meaning doesn't take the word-for-word one's place, nor the other way.
        var translations = Path.Combine(folder, $"lines-{Path.GetFileNameWithoutExtension(model)}-{spoken}-{target}{TranslatedBy(natural)}.json");
        if (Read(translations) is { } done && done.Lines.Count == lines.Count)
            lines = done.Lines;
        else
        {
            if (!natural && !Translator.ReadyFor(language, target))
                throw new DubModelsMissing(language);
            var originals = lines.Select(l => l.Original).ToList();
            IReadOnlyList<string> translated = natural
                ? await DubTranslation.TranslateAsync(lines, language, target, Slice(progress, heardAt, 1), cancellationToken).ConfigureAwait(false)
                : await Translator.TranslateAsync(originals, language, target, Slice(progress, heardAt, 1), cancellationToken).ConfigureAwait(false);
            lines = lines.Select((l, i) => l with { Translation = translated[i].Trim() }).ToList();
            Write(translations, language, lines);
        }
        progress?.Report(1);
        var info = await MediaProbe.ProbeAsync(video, cancellationToken).ConfigureAwait(false);
        return new DubSource(video, language, target, vocals, rest, lines, info.DurationSeconds) { Kept = translations };
    }

    /// <summary>The language to dub into, now what is spoken is known.</summary>
    internal static string TargetFor(string into, string language, string? otherwise)
    {
        var target = into != "auto" ? into : Opposite(language);
        return target == language && otherwise is not null && otherwise != language ? otherwise : target;
    }

    /// <summary>
    /// Who translates, for the name of the kept translation: another model, or a key instead of this PC, translates again
    /// rather than bringing back another's words as its own.
    /// </summary>
    internal static string TranslatedBy(bool natural)
    {
        var by = natural ? "-" + DubTranslation.Writer.Id : "";
        var cloud = natural ? (Cloud.CloudAi.CanWrite ? Cloud.CloudAi.Writing : null) : Cloud.CloudAi.CanTranslate ? Cloud.CloudAi.Translating : null;
        if (cloud is null)
            return by;
        var name = $"{cloud.Provider}-{cloud.Model}";
        foreach (char bad in Path.GetInvalidFileNameChars())
            name = name.Replace(bad, '_');
        return $"{by}-{name}";
    }

    /// <summary>Whether this video was prepared before (it then opens again at once).</summary>
    public static bool IsKnown(string video) => File.Exists(Path.Combine(CacheFolder, Key(video), "Vocals.wav"));

    // ---------- dubbing ----------

    /// <summary>
    /// The dubbed video, written to <paramref name="output"/>. <paramref name="lines"/> are the prepared lines, their
    /// translations as checked (an empty translation leaves that line in the original voice).
    /// </summary>
    /// <param name="checker">A Whisper model that hears each new line back, to say it again when it came out wrong.</param>
    public static async Task<string> DubAsync(DubSource source, IReadOnlyList<DubLine> lines, string output, DubOptions options, string? checker,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        // The translations as checked, kept: the video opens with them next time.
        if (source.Kept is { } kept && lines.Count == source.Lines.Count)
            Write(kept, source.Language, [.. lines]);
        // The music out of the rest of the sound first (it takes a while; then it is kept), the voices after.
        var sounds = Path.Combine(Path.GetDirectoryName(source.Rest)!, "Sounds.wav");
        if (!File.Exists(sounds))
        {
            await RemoveMusicAsync(source.Rest, sounds, Slice(progress, 0, 0.4), cancellationToken).ConfigureAwait(false);
            progress = Slice(progress, 0.4, 1);
        }
        var safe = new Part(progress, 0, 1);
        var cloner = await Voices.GetAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            safe.Report(0.08);
            var voice = (await MediaPreview.DecodePcmAsync(source.Vocals, new PcmDecodeOptions(VoiceCloner.Rate, 1), cancellationToken).ConfigureAwait(false)).Samples;
            var (hearer, good) = Hearer(checker, source.Into);
            using var ears = hearer is not null ? new LiveTranscriber(hearer, source.Into) : null;
            var said = new List<(DubLine Line, float[] Sound, double Gain)>();
            var todo = lines.Where(l => !string.IsNullOrWhiteSpace(l.Translation)).ToList();
            // Each line is heard back while the next one is being said (the hearing mostly on the graphics card, the saying
            // on the processor); one heard wrong is said again before going on.
            Take? waiting = null, next = null;
            try
            {
                for (int i = 0; i <= todo.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    next = i < todo.Count ? await StartAsync(cloner, voice, todo[i], source.Into, ears, cancellationToken).ConfigureAwait(false) : null;
                    if (waiting is not null)
                    {
                        var sound = await FinishAsync(cloner, waiting, source.Into, ears, good, cancellationToken).ConfigureAwait(false);
                        if (sound.Length > 0)
                        {
                            // As loud as the original line was said, within reason.
                            double gain = Math.Pow(10, Math.Clamp(DubTrack.SpeechLevel(waiting.Reference, VoiceCloner.Rate) - DubTrack.SpeechLevel(sound, VoiceCloner.Rate), -12, 12) / 20);
                            said.Add((waiting.Line, sound, gain));
                        }
                        waiting.Dispose();
                        safe.Report(0.1 + 0.75 * i / todo.Count);
                    }
                    (waiting, next) = (next, null);
                }
            }
            finally
            {
                // A dub stopped part way: Whisper is let finish hearing (it stops at once) before it is let go.
                foreach (var take in new[] { waiting, next })
                    if (take is not null)
                        await take.CloseAsync().ConfigureAwait(false);
            }

            // Placed, fitted and mixed with what is kept of the original voice.
            const int Rate = 48000;
            var places = DubTrack.Place(said.Select(s => (s.Line.Start, s.Sound.Length / (double)VoiceCloner.Rate)).ToList(), source.Length);
            var takes = new List<(float[] Sound, double At, double OriginalStart, double OriginalEnd)>();
            for (int i = 0; i < said.Count; i++)
            {
                var shaped = await DubTrack.ReshapeAsync(said[i].Sound, VoiceCloner.Rate, Rate, places[i].Speed, cancellationToken).ConfigureAwait(false);
                for (int k = 0; k < shaped.Length; k++)
                    shaped[k] *= (float)said[i].Gain;
                takes.Add((shaped, places[i].At, said[i].Line.Start, said[i].Line.End));
            }
            safe.Report(0.88);
            var original = (await MediaPreview.DecodePcmAsync(source.Vocals, new PcmDecodeOptions(Rate, 1), cancellationToken).ConfigureAwait(false)).Samples;
            var work = Path.Combine(Path.GetTempPath(), "Petal", "dub-" + Guid.NewGuid().ToString("N")[..10]);
            Directory.CreateDirectory(work);
            try
            {
                var voices = Path.Combine(work, "voices.wav");
                var track = DubTrack.Mix(original, takes, Rate);
                // FFmpeg makes stereo mono by adding its two sides at 0.71 each, which puts a voice in the middle 3 dB up:
                // brought back down, so the voices sit against the music as they did.
                if ((await MediaProbe.ProbeAsync(source.Vocals, cancellationToken).ConfigureAwait(false)).Audio?.Channels >= 2)
                    for (int i = 0; i < track.Length; i++)
                        track[i] *= 0.70710677f;
                DubTrack.WriteWav(voices, track, Rate);
                string? subtitles = null;
                if (options.Subtitles && said.Count > 0)
                {
                    subtitles = Path.Combine(work, "words.srt");
                    await File.WriteAllTextAsync(subtitles, Srt(said.Select((s, i) => (places[i].At, places[i].At + places[i].Length, s.Line.Translation)).ToList()),
                        new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
                }
                await DubTrack.MuxAsync(source.Video, sounds, voices, output, source.Into, options.KeepOriginal ? OriginalTitle(source.Language) : null, subtitles,
                    Slice(progress, 0.9, 1), cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                try { Directory.Delete(work, recursive: true); } catch (IOException) { }
            }
            safe.Report(1);
            return output;
        }
        finally
        {
            Voices.Release();
        }
    }

    /// <summary>The sounds of the rest of the video (all but its voices) without its music, written to <paramref name="sounds"/>.</summary>
    private static async Task RemoveMusicAsync(string rest, string sounds, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var model = ModelStore.Find(ModelCatalog.Tiger) ?? throw new InvalidOperationException("The separation model isn't downloaded yet.");
        var pcm = await MediaPreview.DecodePcmAsync(rest, new PcmDecodeOptions(MusicRemover.Rate, 2), cancellationToken).ConfigureAwait(false);
        int length = pcm.FrameCount;
        var channels = new[] { new float[length], new float[length] };
        for (int i = 0; i < length; i++)
        {
            channels[0][i] = pcm.Samples[2 * i];
            channels[1][i] = pcm.Samples[2 * i + 1];
        }
        var kept = await Task.Run(() => MusicRemover.Sounds(model, channels, progress, cancellationToken), cancellationToken).ConfigureAwait(false);
        var stereo = new float[2 * length];
        for (int i = 0; i < length; i++)
        {
            stereo[2 * i] = kept[0][i];
            stereo[2 * i + 1] = kept[1][i];
        }
        // Written beside, then put in place: a stopped dub leaves no half-made file to be taken as done.
        var partial = sounds + ".part";
        DubTrack.WriteWav(partial, stereo, MusicRemover.Rate, channels: 2);
        File.Move(partial, sounds, overwrite: true);
    }

    /// <summary>The original track's name in the dubbed file: the language it is in.</summary>
    private static string OriginalTitle(string language) => language switch { "ar" => "Arabic (original)", "en" => "English (original)", not null when Translator.Languages.Any(l => l.Code == language) => Translator.EnglishName(language) + " (original)", _ => "Original" };

    /// <summary>
    /// A line on its way: its speaker's voice learnt from the line itself, what is to be said (its figures as words, its
    /// brackets gone: <see cref="SpokenText"/>), the first saying of it, and Whisper hearing that back.
    /// </summary>
    private sealed class Take(DubLine line, float[] reference, ClonedVoice voice, string spoken) : IDisposable
    {
        public DubLine Line => line;

        public float[] Reference => reference;

        public ClonedVoice Voice => voice;

        public string Spoken => spoken;

        public float[] First { get; set; } = [];

        public Task<LiveText>? Hearing { get; set; }

        public void Dispose() => voice.Dispose();

        /// <summary>Let go once Whisper is done with it, whatever it heard (the dub is being stopped).</summary>
        public async Task CloseAsync()
        {
            if (Hearing is { } hearing)
            {
                try
                {
                    await hearing.ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // What it heard no longer matters.
                }
            }
            Dispose();
        }
    }

    /// <summary>Learns the line's voice, says it once and starts hearing it back.</summary>
    private static async Task<Take> StartAsync(VoiceCloner cloner, float[] voice, DubLine line, string language, LiveTranscriber? ears, CancellationToken ct)
    {
        var reference = Reference(voice, line, VoiceCloner.Rate);
        var take = new Take(line, reference, cloner.Learn(reference), SpokenText.Of(line.Translation, language));
        try
        {
            take.First = await SayAsync(cloner, take, language, 1, ct).ConfigureAwait(false);
        }
        catch
        {
            take.Dispose();
            throw;
        }
        if (ears is not null && take.First.Length > 0)
            take.Hearing = ears.TranscribeAsync(Resample(take.First, VoiceCloner.Rate, 16000), false, ct);
        return take;
    }

    /// <summary>
    /// The line's sound: the first saying when what Whisper heard is no further from the line than <paramref name="good"/>
    /// allows; otherwise said again (with other chances), the best of up to three.
    /// </summary>
    private static async Task<float[]> FinishAsync(VoiceCloner cloner, Take take, string language, LiveTranscriber? ears, double good, CancellationToken ct)
    {
        float[] best = take.First;
        double bestErrors = take.Hearing is { } hearing ? WordErrors(take.Spoken, SpokenText.Of((await hearing.ConfigureAwait(false)).Text, language))
            : ears is null && best.Length > 0 ? 0 : double.MaxValue;
        for (int attempt = 2; attempt <= 3 && bestErrors > good; attempt++)
        {
            var sound = await SayAsync(cloner, take, language, attempt, ct).ConfigureAwait(false);
            if (sound.Length == 0)
                continue;
            double errors = ears is null ? 0
                : WordErrors(take.Spoken, SpokenText.Of((await ears.TranscribeAsync(Resample(sound, VoiceCloner.Rate, 16000), false, ct).ConfigureAwait(false)).Text, language));
            if (errors < bestErrors)
            {
                bestErrors = errors;
                best = sound;
            }
        }
        return best;
    }

    /// <summary>The line said once more, each try with its own chance (seed).</summary>
    private static async Task<float[]> SayAsync(VoiceCloner cloner, Take take, string language, int attempt, CancellationToken ct) =>
        DubTrack.Trim(await Task.Run(() => cloner.Speak(take.Spoken, language, take.Voice, seed: attempt, cancellationToken: ct), ct).ConfigureAwait(false), VoiceCloner.Rate);

    /// <summary>
    /// The Whisper that hears each new line back, and the share of words it may hear wrong before the line is said again:
    /// the most accurate on this PC, since a weaker one mishears good speech (Arabic above all) and has lines said again for
    /// nothing. The fast model hears Arabic too poorly to judge it: Arabic lines are then taken as said.
    /// </summary>
    internal static (string? Path, double Good) Hearer(string? chosen, string language)
    {
        if (ModelStore.Find(ModelCatalog.WhisperAccurate) is { } accurate)
            return (accurate, 0.2);
        if (ModelStore.Find(ModelCatalog.WhisperBalanced) is { } balanced)
            return (balanced, language == "ar" ? 0.35 : 0.25);
        if (chosen is null || !File.Exists(chosen) || language == "ar")
            return (null, 1);
        return (chosen, 0.35);
    }

    /// <summary>
    /// Some words heard against those meant: the share wrong, missing or added (compared as Petal compares words). A word of
    /// four letters or more heard one letter off counts as heard: Whisper writes the case ending it hears (شخصاً for شخص) and
    /// spells a name its own way, and the line need not be said again for that; a word said wrong (محلوض for محظوظ) counts.
    /// </summary>
    internal static double WordErrors(string meant, string heard)
    {
        var a = Words(meant);
        var b = Words(heard);
        if (a.Length == 0)
            return b.Length == 0 ? 0 : 1;
        var d = new int[a.Length + 1, b.Length + 1];
        for (int i = 0; i <= a.Length; i++)
            d[i, 0] = i;
        for (int j = 0; j <= b.Length; j++)
            d[0, j] = j;
        for (int i = 1; i <= a.Length; i++)
            for (int j = 1; j <= b.Length; j++)
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (Alike(a[i - 1], b[j - 1]) ? 0 : 1));
        return (double)d[a.Length, b.Length] / a.Length;
    }

    /// <summary>The same word, or one of four letters or more with a letter added, missing or changed.</summary>
    private static bool Alike(string x, string y)
    {
        if (x == y)
            return true;
        if (Math.Max(x.Length, y.Length) < 4 || Math.Abs(x.Length - y.Length) > 1)
            return false;
        // At most one edit: skip the common start and end, and what is left is one letter or none on each side.
        int start = 0;
        while (start < x.Length && start < y.Length && x[start] == y[start])
            start++;
        int endX = x.Length, endY = y.Length;
        while (endX > start && endY > start && x[endX - 1] == y[endY - 1])
        {
            endX--;
            endY--;
        }
        return endX - start <= 1 && endY - start <= 1;
    }

    private static string[] Words(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var raw in text.ToLowerInvariant())
        {
            if (raw is >= (char)0x064B and <= (char)0x065F || raw == (char)0x0640)
                continue;
            char c = raw switch
            {
                (char)0x0622 or (char)0x0623 or (char)0x0625 => (char)0x0627,
                (char)0x0649 => (char)0x064A,
                (char)0x0629 => (char)0x0647,
                _ => raw,
            };
            sb.Append(char.IsLetterOrDigit(c) ? c : ' ');
        }
        return sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>Sound at another rate, by straight lines between samples: enough for Whisper to hear it.</summary>
    internal static float[] Resample(float[] sound, int from, int to)
    {
        var result = new float[(int)((long)sound.Length * to / from)];
        double step = (double)from / to;
        for (int i = 0; i < result.Length; i++)
        {
            double at = i * step;
            int k = (int)at;
            double f = at - k;
            result[i] = k + 1 < sound.Length ? (float)(sound[k] * (1 - f) + sound[k + 1] * f) : sound[Math.Min(k, sound.Length - 1)];
        }
        return result;
    }

    /// <summary>
    /// The voice to copy for a line: the line itself as its speaker said it, a little either side; a short line takes in a
    /// little more around it (up to five seconds), since a voice is learnt better from more.
    /// </summary>
    internal static float[] Reference(float[] voice, DubLine line, int rate)
    {
        double start = line.Start - 0.1, end = line.End + 0.1;
        double wanted = 5;
        if (end - start < wanted)
        {
            double more = (wanted - (end - start)) / 2;
            start -= more;
            end += more;
        }
        int a = Math.Clamp((int)Math.Round(start * rate), 0, voice.Length), b = Math.Clamp((int)Math.Round(end * rate), a, voice.Length);
        return voice[a..b];
    }

    /// <summary>The dubbed lines as SubRip subtitles, at their new times.</summary>
    internal static string Srt(IReadOnlyList<(double Start, double End, string Text)> lines)
    {
        var sb = new StringBuilder();
        static string Stamp(double s)
        {
            var t = TimeSpan.FromSeconds(Math.Max(0, s));
            return string.Create(CultureInfo.InvariantCulture, $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00},{t.Milliseconds:000}");
        }
        for (int i = 0; i < lines.Count; i++)
            sb.Append(i + 1).Append('\n').Append(Stamp(lines[i].Start)).Append(" --> ").Append(Stamp(lines[i].End)).Append('\n').Append(lines[i].Text).Append("\n\n");
        return sb.ToString();
    }

    // ---------- the voice model, kept loaded while it is used ----------

    /// <summary>
    /// The voice model, loaded once (it takes a few seconds) and kept while dubs follow one another; let
    /// go three minutes after the last, since it holds well over a gigabyte.
    /// </summary>
    public static class Voices
    {
        private static readonly SemaphoreSlim Gate = new(1, 1);
        private static Task<VoiceCloner>? _loading;
        private static int _users;
        private static CancellationTokenSource? _letGo;

        /// <summary>
        /// Starts loading the voice model in the background (while the video is still being prepared, or its lines checked);
        /// let go again if no dub follows within ten minutes.
        /// </summary>
        public static void WarmUp()
        {
            if (!ModelCatalog.Chatterbox.All(ModelStore.IsAvailable))
                return;
            Gate.Wait();
            try
            {
                _loading ??= Task.Run(() => new VoiceCloner(Folder(), graphics: true));
                if (_users == 0)
                    LetGoAfter(TimeSpan.FromMinutes(10));
            }
            finally
            {
                Gate.Release();
            }
        }

        public static async Task<VoiceCloner> GetAsync(CancellationToken cancellationToken)
        {
            await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            Task<VoiceCloner> loading;
            try
            {
                _letGo?.Cancel();
                _users++;
                loading = _loading ??= Task.Run(() => new VoiceCloner(Folder(), graphics: true));
            }
            finally
            {
                Gate.Release();
            }
            try
            {
                return await loading.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                Release();
                throw;
            }
        }

        public static void Release()
        {
            Gate.Wait();
            try
            {
                if (--_users > 0)
                    return;
                LetGoAfter(TimeSpan.FromMinutes(3));
            }
            finally
            {
                Gate.Release();
            }
        }

        /// <summary>Lets the model go after a while unless it is used again first. Called holding the gate.</summary>
        private static void LetGoAfter(TimeSpan wait)
        {
            _letGo?.Cancel();
            var letGo = _letGo = new CancellationTokenSource();
            _ = Task.Delay(wait, letGo.Token).ContinueWith(async t =>
            {
                if (t.IsCanceled)
                    return;
                await Gate.WaitAsync().ConfigureAwait(false);
                try
                {
                    if (_users == 0 && _loading is { } loaded && !letGo.IsCancellationRequested)
                    {
                        _loading = null;
                        try
                        {
                            (await loaded.ConfigureAwait(false)).Dispose();
                        }
                        catch (Exception ex) when (ex is not OutOfMemoryException)
                        {
                            // It never loaded: there is nothing to let go.
                        }
                    }
                }
                finally
                {
                    Gate.Release();
                }
            }, TaskScheduler.Default);
        }

        private static string Folder() =>
            Path.GetDirectoryName(ModelStore.Find(ModelCatalog.ChatterboxEncoder) ?? throw new InvalidOperationException("The voice model isn't downloaded yet."))!;
    }

    // ---------- what was prepared, kept ----------

    private static string Key(string video)
    {
        var file = new FileInfo(video);
        var identity = $"{CacheVersion}|{Path.GetFullPath(video).ToLowerInvariant()}|{(file.Exists ? file.Length : 0)}|{(file.Exists ? file.LastWriteTimeUtc.Ticks : 0)}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..24].ToLowerInvariant();
    }

    /// <summary>Marks a prepared video as used, and lets the oldest go.</summary>
    private static void Touch(string folder)
    {
        try
        {
            Directory.SetLastWriteTimeUtc(folder, DateTime.UtcNow);
            foreach (var old in new DirectoryInfo(CacheFolder).GetDirectories().OrderByDescending(d => d.LastWriteTimeUtc).Skip(Remembered))
                old.Delete(recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private sealed record Stored(int Version, string Language, List<DubLine> Lines);

    private static void Write(string path, string language, List<DubLine> lines)
    {
        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(new Stored(CacheVersion, language, lines)), new UTF8Encoding(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static (string Language, List<DubLine> Lines)? Read(string path)
    {
        try
        {
            if (!File.Exists(path))
                return null;
            var stored = JsonSerializer.Deserialize<Stored>(File.ReadAllText(path));
            return stored is { Version: CacheVersion } ? (stored.Language, stored.Lines) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static Part? Slice(IProgress<double>? progress, double from, double to) => progress is null ? null : new Part(progress, from, to);
}

/// <summary>Progress of a part of the work, as a share of the whole.</summary>
internal sealed class Part(IProgress<double>? inner, double from, double to) : IProgress<double>
{
    public void Report(double value) => inner?.Report(from + (to - from) * Math.Clamp(value, 0, 1));
}
