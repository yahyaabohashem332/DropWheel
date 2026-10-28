using System.IO;
using System.Text;
using System.Text.Json;
using Petal.Engines.Media;

namespace Petal.Recipes;

/// <summary>
/// Edit by Text: a recording heard once (as Make Shorts hears it, and kept with it: <see cref="ShortsMaker.ListenAsync"/>),
/// with its loudness a hundred times a second to place the cuts between words, shown as its words and cut by taking words
/// out (<see cref="TextCut"/>). The loudness and what was taken out are kept here, so the same recording opens again at
/// once and as it was left.
/// </summary>
public static class TextEditing
{
    /// <summary>Where the edits are kept.</summary>
    public static string CacheFolder { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Petal", "cache", "textedit");

    /// <summary>How many recordings' edits are kept; the oldest are let go.</summary>
    private const int Remembered = 60;

    /// <summary>Levels a second of the loudness that places the cuts.</summary>
    public const int Fine = 100;

    private sealed record Stored(int Pieces, List<int> Removed);

    /// <summary>The recording heard, with its loudness: from what was kept when it was heard this way before.</summary>
    /// <param name="language">"auto", or the language spoken.</param>
    public static async Task<(ShortsSource Source, SoundLevels Loudness)> ListenAsync(string video, string model, string language,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var key = ShortsMaker.CacheKey(video, model, language);
        bool heard = ShortsMaker.IsKnown(video, model, language);
        if (heard && ReadLoudness(key) is { } kept)
            return (await ShortsMaker.ListenAsync(video, model, language, progress, cancellationToken).ConfigureAwait(false), kept);
        var samples = await Transcriber.ReadSpeechAsync(video, cancellationToken).ConfigureAwait(false);
        progress?.Report(0.04);
        var loudness = SoundLevels.Measure(samples, perSecond: Fine);
        WriteLoudness(key, loudness);
        var source = heard
            ? await ShortsMaker.ListenAsync(video, model, language, null, cancellationToken).ConfigureAwait(false)
            : await ShortsMaker.HearAsync(video, samples, model, language, progress is null ? null : new Part(progress, 0.04, 1), cancellationToken).ConfigureAwait(false);
        progress?.Report(1);
        return (source, loudness);
    }

    /// <summary>
    /// The pieces with each sound between words heard again by Whisper on its own, all of them in one go with quiet
    /// between them: a sound found from the levels where no word was heard is most often an "uh", but now and then it
    /// is a word Whisper timed far out, and a clean-up must never take a word out. What was heard is kept, so it is
    /// done once for a recording.
    /// </summary>
    /// <param name="spoken">The language the recording was heard in.</param>
    public static async Task<List<EditToken>> CheckSoundsAsync(string video, string model, string language, string spoken, List<EditToken> tokens,
        CancellationToken cancellationToken = default)
    {
        var key = ShortsMaker.CacheKey(video, model, language);
        var heard = ReadSounds(key);
        static string Id(EditToken t) => FormattableString.Invariant($"{t.Start:0.00}-{t.End:0.00}");
        var unknown = Enumerable.Range(0, tokens.Count).Where(i => tokens[i].Kind == EditTokenKind.Sound && !heard.ContainsKey(Id(tokens[i]))).ToList();
        if (unknown.Count > 0)
        {
            const double Gap = 1.5;
            var sound = new List<float>();
            var places = new List<(double From, double To)>();
            foreach (var i in unknown)
            {
                var piece = await MediaPreview.DecodePcmAsync(video, new PcmDecodeOptions(16000, 1, TimeSpan.FromSeconds(tokens[i].Start),
                    TimeSpan.FromSeconds(tokens[i].Length)), cancellationToken).ConfigureAwait(false);
                sound.AddRange(new float[(int)(Gap * 16000)]);
                double from = sound.Count / 16000.0;
                sound.AddRange(piece.Samples);
                places.Add((from, sound.Count / 16000.0));
            }
            sound.AddRange(new float[(int)(Gap * 16000)]);
            var said = await Transcriber.TranscribeWordsAsync(sound.ToArray(), new TranscribeOptions(model, spoken, WordTimes: true), null, null, cancellationToken)
                .ConfigureAwait(false);
            var words = said.Segments.SelectMany(s => s.Words).ToList();
            for (int k = 0; k < unknown.Count; k++)
            {
                var (from, to) = places[k];
                heard[Id(tokens[unknown[k]])] = string.Join(' ', words.Where(w => w.Start >= from - 0.5 && w.Start < to + 0.5).Select(w => w.Text.Trim()));
            }
            WriteSounds(key, heard);
        }
        var speech = new Dictionary<int, string>();
        for (int i = 0; i < tokens.Count; i++)
            if (tokens[i].Kind == EditTokenKind.Sound && heard.TryGetValue(Id(tokens[i]), out var text) && !TextCut.NoWords(text))
                speech[i] = text;
        return speech.Count == 0 ? tokens : TextCut.Unsound(tokens, speech);
    }

    /// <summary>Whether all the sounds between words of these pieces were heard again before.</summary>
    public static bool SoundsChecked(string video, string model, string language, IEnumerable<EditToken> tokens)
    {
        var heard = ReadSounds(ShortsMaker.CacheKey(video, model, language));
        return tokens.Where(t => t.Kind == EditTokenKind.Sound).All(t => heard.ContainsKey(FormattableString.Invariant($"{t.Start:0.00}-{t.End:0.00}")));
    }

    private static string SoundsPath(string key) => Path.Combine(CacheFolder, key + ".sounds");

    private static Dictionary<string, string> ReadSounds(string key)
    {
        try
        {
            var path = SoundsPath(key);
            return File.Exists(path) ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) ?? [] : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    private static void WriteSounds(string key, Dictionary<string, string> heard)
    {
        try
        {
            Directory.CreateDirectory(CacheFolder);
            File.WriteAllText(SoundsPath(key), JsonSerializer.Serialize(heard), new UTF8Encoding(false));
            Prune("*.sounds");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Whether this recording was heard this way before, its loudness with it (it opens at once).</summary>
    public static bool IsKnown(string video, string model, string language) =>
        ShortsMaker.IsKnown(video, model, language) && File.Exists(LoudnessPath(ShortsMaker.CacheKey(video, model, language)));

    private static string LoudnessPath(string key) => Path.Combine(CacheFolder, key + ".loud");

    /// <summary>The loudness, a byte for each level (whole decibels below full scale, down to -127).</summary>
    private static void WriteLoudness(string key, SoundLevels loudness)
    {
        try
        {
            Directory.CreateDirectory(CacheFolder);
            File.WriteAllBytes(LoudnessPath(key), loudness.Values.Select(v => (byte)(sbyte)Math.Clamp(Math.Round(v), -127, 0)).ToArray());
            Prune("*.loud");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    internal static SoundLevels? ReadLoudness(string key)
    {
        try
        {
            var path = LoudnessPath(key);
            if (!File.Exists(path))
                return null;
            var levels = File.ReadAllBytes(path).Select(b => (float)(sbyte)b).ToArray();
            // Used again: kept longer.
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
            return new SoundLevels(levels, Fine);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void Prune(string pattern)
    {
        foreach (var old in new DirectoryInfo(CacheFolder).GetFiles(pattern).OrderByDescending(f => f.LastWriteTimeUtc).Skip(Remembered))
            old.Delete();
    }

    /// <summary>What was taken out of this recording last time, when it was heard the same way (the same pieces).</summary>
    public static HashSet<int>? Recall(string video, string model, string language, int pieces)
    {
        try
        {
            var path = Path.Combine(CacheFolder, ShortsMaker.CacheKey(video, model, language) + ".json");
            if (!File.Exists(path))
                return null;
            var stored = JsonSerializer.Deserialize<Stored>(File.ReadAllText(path));
            return stored is not null && stored.Pieces == pieces ? stored.Removed.Where(i => i >= 0 && i < pieces).ToHashSet() : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Keeps what is taken out of this recording (nothing taken out forgets it).</summary>
    public static void Remember(string video, string model, string language, int pieces, IReadOnlySet<int> removed)
    {
        try
        {
            Directory.CreateDirectory(CacheFolder);
            var path = Path.Combine(CacheFolder, ShortsMaker.CacheKey(video, model, language) + ".json");
            if (removed.Count == 0)
            {
                File.Delete(path);
                return;
            }
            File.WriteAllText(path, JsonSerializer.Serialize(new Stored(pieces, removed.Order().ToList())), new UTF8Encoding(false));
            Prune("*.json");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
