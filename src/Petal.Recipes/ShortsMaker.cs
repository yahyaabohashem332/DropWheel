using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Petal.Engines.Media;
using Petal.Recipes.Translation;

namespace Petal.Recipes;

/// <summary>A long video as listened to: what was said, with each word's time; the language; how loud it is, moment by moment.</summary>
public sealed record ShortsSource(string Video, string Language, IReadOnlyList<TranscriptSegment> Segments, SoundLevels Levels)
{
    public bool HasSpeech => Segments.Any(s => s.Words.Count > 0);
}

/// <summary>
/// Make Shorts: a long video (a talk, an interview, a podcast, a lesson) into short clips ready to post. The video is
/// listened to once on this PC (and remembered, so it opens again at once), its best moments found
/// (<see cref="ShortMoments"/>), each titled, and each made tall with its words burned in (<see cref="ShortClip"/>).
/// </summary>
public static class ShortsMaker
{
    /// <summary>Where what was heard is kept between runs.</summary>
    public static string CacheFolder { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Petal", "cache", "shorts");

    /// <summary>How many videos are remembered; the oldest are let go.</summary>
    private const int Remembered = 40;

    private const int CacheVersion = 1;

    /// <summary>What the video says and how it sounds: from the cache when this video was heard before with the same model and language.</summary>
    /// <param name="language">"auto", or the language spoken.</param>
    public static async Task<ShortsSource> ListenAsync(string video, string model, string language, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var key = CacheKey(video, model, language);
        if (Read(key, video) is { } known)
        {
            progress?.Report(1);
            return known;
        }
        var samples = await Transcriber.ReadSpeechAsync(video, cancellationToken).ConfigureAwait(false);
        progress?.Report(0.04);
        return await HearAsync(video, samples, model, language, progress is null ? null : new Part(progress, 0.04, 1), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>What is said in the video's sound, already read (16 kHz mono), heard and kept as <see cref="ListenAsync"/> keeps it.</summary>
    public static async Task<ShortsSource> HearAsync(string video, float[] samples, string model, string language, IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var levels = SoundLevels.Measure(samples);
        var transcript = await Transcriber.TranscribeWordsAsync(samples, new TranscribeOptions(model, language, WordTimes: true), levels, progress, cancellationToken)
            .ConfigureAwait(false);
        var source = new ShortsSource(video, transcript.Language, transcript.Segments, levels);
        Write(CacheKey(video, model, language), source);
        return source;
    }

    /// <summary>Whether this video was heard before with this model and language (opening it again is then immediate).</summary>
    public static bool IsKnown(string video, string model, string language) => File.Exists(Path.Combine(CacheFolder, CacheKey(video, model, language) + ".json"));

    // ---------- titles ----------

    /// <summary>
    /// A title for a moment: written by the language model on this PC when it is there and its title holds to what is
    /// said (its words are the clip's own, in the clip's language); otherwise the moment's best line. A small model
    /// sometimes makes things up, and a title that promises what the clip doesn't say is worse than none.
    /// </summary>
    public static async Task<string> TitleAsync(Moment moment, string language, CancellationToken cancellationToken = default)
    {
        var line = ShortMoments.Headline(moment);
        if (!AskPetal.ModelReady)
            return line;
        try
        {
            var text = moment.Text.Length > 900 ? moment.Text[..900] : moment.Text;
            var written = Clean(await AskPetal.WriteAsync(TitleInstruction(language), text, 32, cancellationToken, 0.2).ConfigureAwait(false));
            return written.Length > 0 && ShortMoments.HoldsTo(written, moment) ? written : line;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return line;
        }
    }

    /// <summary>
    /// A title in the language the words are shown in: as it is when they are in the language spoken; otherwise
    /// translated as they are (Arabic and English by the translation models, others by the language model when it is
    /// here). Left as it is when it can't be translated.
    /// </summary>
    public static async Task<string> TitleInAsync(string title, string spoken, string into, CancellationToken cancellationToken = default)
    {
        string wanted = into == SpeechInto.Spoken ? spoken : into;
        if (into == SpeechInto.Spoken || wanted == spoken || string.IsNullOrWhiteSpace(title))
            return title;
        try
        {
            if (SpeechText.KnownPair(spoken, wanted) && Translator.ReadyFor(spoken, wanted))
                return (await Translator.TranslateAsync([title], spoken, wanted, null, cancellationToken).ConfigureAwait(false))[0].Trim();
            if (AskPetal.ModelReady)
            {
                var written = await AskPetal.WriteAsync(AskPetal.Instructions.TranslateInto(Translator.EnglishName(wanted)), title, 60, cancellationToken, 0).ConfigureAwait(false);
                var line = written.Replace('\r', '\n').Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "";
                line = line.Trim('"', '\'', '“', '”', '«', '»', '`', ' ');
                if (line.Length is > 0 and <= 100)
                    return line;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
        }
        return title;
    }

    private static string TitleInstruction(string language)
    {
        var name = language switch
        {
            "ar" => "Arabic",
            "en" => "English",
            _ => CultureInfo.GetCultureInfo(language) is { } c && !string.IsNullOrEmpty(c.EnglishName) ? c.EnglishName : "the language of the clip",
        };
        return $"You name short video clips cut from a longer video. Write one catchy title for the clip whose words are below: at most 7 words, in {name}, " +
               "using the clip's own words and saying only what the clip says, like a headline that makes people want to watch. " +
               "No quotation marks, hashtags or emojis. Reply with the title only.";
    }

    /// <summary>The model's title without quotation marks, a "Title:" before it, a full stop after it, or a second line.</summary>
    internal static string Clean(string written)
    {
        var line = written.Replace('\r', '\n').Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "";
        foreach (var prefix in (string[])["Title:", "title:", "TITLE:", "العنوان:", "عنوان:"])
            if (line.StartsWith(prefix, StringComparison.Ordinal))
                line = line[prefix.Length..].Trim();
        line = line.Trim('"', '\'', '“', '”', '«', '»', '`', '*', ' ').TrimEnd('.', '،', ',', ';', ':').Trim();
        line = string.Join(' ', line.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        int words = line.Split(' ').Length;
        return line.Length is > 0 and <= 80 && words is >= 2 and <= 10 && !line.Contains('#') ? line : "";
    }

    // ---------- captions ----------

    /// <summary>
    /// The words to show on a moment, in the language wanted: as spoken, each with its own time; otherwise translated
    /// sentence by sentence (Arabic and English by the translation models on this PC, other languages through
    /// Whisper's English), each word of a sentence given its share of the sentence's time.
    /// </summary>
    /// <param name="translatingModel">A Whisper model that can translate, for a language the translation models don't read.</param>
    public static async Task<IReadOnlyList<CaptionWord>> CaptionsAsync(ShortsSource source, Moment moment, string into, string? translatingModel = null,
        CancellationToken cancellationToken = default)
    {
        string wanted = into == SpeechInto.Spoken ? source.Language : into;
        if (into == SpeechInto.Spoken || wanted == source.Language)
            return moment.Words.Select(w => new CaptionWord(w.Text, TimeSpan.FromSeconds(w.Start), TimeSpan.FromSeconds(w.End))).ToList();

        // The sentences said in the moment, timed by their words.
        var said = source.Segments
            .Where(s => s.Words.Count > 0 && s.Words[^1].End > moment.Start && s.Words[0].Start < moment.End)
            .Select(s => new TranscriptSegment(TimeSpan.FromSeconds(s.Words[0].Start), TimeSpan.FromSeconds(s.Words[^1].End), s.Text))
            .ToList();
        if (SpeechText.KnownPair(source.Language, wanted) && Translator.ReadyFor(source.Language, wanted))
            return AnimatedCaptions.Words(await TranslateAsync(said, source.Language, wanted, cancellationToken).ConfigureAwait(false));
        List<TranscriptSegment> english;
        if (source.Language == "en")
            english = said;
        else
        {
            var model = translatingModel ?? throw new InvalidOperationException(SpeechText.NoTranslatingModel);
            var pcm = await MediaPreview.DecodePcmAsync(source.Video, new PcmDecodeOptions(16000, 1, TimeSpan.FromSeconds(moment.Start), TimeSpan.FromSeconds(moment.Length)),
                cancellationToken).ConfigureAwait(false);
            var heard = await Transcriber.TranscribeAsync(pcm.Samples, new TranscribeOptions(model, source.Language, TranslateToEnglish: true), null, cancellationToken).ConfigureAwait(false);
            var offset = TimeSpan.FromSeconds(moment.Start);
            english = heard.Segments.Where(s => s.Text.Length > 0).Select(s => new TranscriptSegment(s.Start + offset, s.End + offset, s.Text)).ToList();
        }
        var shown = wanted == "en" ? english : await TranslateAsync(english, "en", wanted, cancellationToken).ConfigureAwait(false);
        return AnimatedCaptions.Words(shown);
    }

    private static async Task<List<TranscriptSegment>> TranslateAsync(List<TranscriptSegment> segments, string from, string into, CancellationToken cancellationToken)
    {
        var translated = await Translator.TranslateAsync(segments.Select(s => s.Text).ToList(), from, into, null, cancellationToken).ConfigureAwait(false);
        return segments.Select((s, i) => s with { Text = translated[i] }).ToList();
    }

    // ---------- what was heard, kept ----------

    internal static string CacheKey(string video, string model, string language)
    {
        var file = new FileInfo(video);
        var identity = $"{CacheVersion}|{Path.GetFullPath(video).ToLowerInvariant()}|{(file.Exists ? file.Length : 0)}|{(file.Exists ? file.LastWriteTimeUtc.Ticks : 0)}|{Path.GetFileName(model)}|{language}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..32].ToLowerInvariant();
    }

    private sealed record Stored(int Version, string Language, List<StoredSegment> Segments, string Levels);

    private sealed record StoredSegment(double S, double E, string T, List<StoredWord> W);

    private sealed record StoredWord(string T, double S, double E);

    internal static void Write(string key, ShortsSource source)
    {
        try
        {
            Directory.CreateDirectory(CacheFolder);
            var levels = new byte[source.Levels.Count * 2];
            for (int i = 0; i < source.Levels.Count; i++)
                BitConverter.TryWriteBytes(levels.AsSpan(i * 2), (short)Math.Clamp(Math.Round(source.Levels.Values[i] * 100), short.MinValue, short.MaxValue));
            var stored = new Stored(CacheVersion, source.Language,
                source.Segments.Select(s => new StoredSegment(Math.Round(s.Start.TotalSeconds, 3), Math.Round(s.End.TotalSeconds, 3), s.Text,
                    s.Words.Select(w => new StoredWord(w.Text, Math.Round(w.Start, 3), Math.Round(w.End, 3))).ToList())).ToList(),
                Convert.ToBase64String(levels));
            var path = Path.Combine(CacheFolder, key + ".json");
            var temp = path + ".part";
            File.WriteAllText(temp, JsonSerializer.Serialize(stored), new UTF8Encoding(false));
            File.Move(temp, path, overwrite: true);
            foreach (var old in new DirectoryInfo(CacheFolder).GetFiles("*.json").OrderByDescending(f => f.LastWriteTimeUtc).Skip(Remembered))
                old.Delete();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not kept: the video is listened to again next time.
        }
    }

    internal static ShortsSource? Read(string key, string video)
    {
        var path = Path.Combine(CacheFolder, key + ".json");
        try
        {
            if (!File.Exists(path))
                return null;
            var stored = JsonSerializer.Deserialize<Stored>(File.ReadAllText(path));
            if (stored is null || stored.Version != CacheVersion)
                return null;
            var bytes = Convert.FromBase64String(stored.Levels);
            var levels = new float[bytes.Length / 2];
            for (int i = 0; i < levels.Length; i++)
                levels[i] = BitConverter.ToInt16(bytes, i * 2) / 100f;
            var segments = stored.Segments.Select(s => new TranscriptSegment(TimeSpan.FromSeconds(s.S), TimeSpan.FromSeconds(s.E), s.T)
            {
                Words = s.W.Select(w => new SpokenWord(w.T, w.S, w.E)).ToList(),
            }).ToList();
            // Used again: kept longer.
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
            return new ShortsSource(video, stored.Language, segments, new SoundLevels(levels));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or FormatException)
        {
            return null;
        }
    }

    private sealed class Part(IProgress<double> inner, double from, double to) : IProgress<double>
    {
        public void Report(double value) => inner.Report(from + (to - from) * Math.Clamp(value, 0, 1));
    }
}
