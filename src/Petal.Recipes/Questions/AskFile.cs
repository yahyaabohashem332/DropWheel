using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Petal.Core.Formats;
using Petal.Core.Models;
using Petal.Engines.Documents;
using Petal.Engines.Media;
using Petal.Recipes.Translation;

namespace Petal.Recipes.Questions;

/// <summary>A passage of a file: its words and where they are.</summary>
public sealed record Passage(int Number, PlacedText Place, string Text);

/// <summary>A file read for questions: its passages with where each is, and what each means.</summary>
public sealed class FileKnowledge
{
    private WordIndex? _words;

    internal FileKnowledge(string path, string language, IReadOnlyList<Passage> passages, float[][] meanings, string? pagesPdf)
    {
        Path = path;
        Language = language;
        Passages = passages;
        Meanings = meanings;
        PagesPdf = pagesPdf;
    }

    public string Path { get; }

    /// <summary>"ar" or "en" when the file is mostly in Arabic or English letters, otherwise "".</summary>
    public string Language { get; }

    public IReadOnlyList<Passage> Passages { get; }

    /// <summary>The PDF its pages can be shown from: the file itself, or the document as Word or LibreOffice laid it out.</summary>
    public string? PagesPdf { get; }

    public bool IsRecording => Passages.Count > 0 && Passages[0].Place.Kind == PlaceKind.Time;

    internal float[][] Meanings { get; }

    internal WordIndex Words => _words ??= new WordIndex(Passages.Select(AskFile.Indexed).ToList());
}

/// <summary>
/// An answer about a file: what it says (in the question's language), the passages it comes from, and the passages
/// found for the question, best first. <see cref="NotFound"/> when the file doesn't say.
/// </summary>
public sealed record PlacedAnswer(string Text, IReadOnlyList<Passage> Sources, IReadOnlyList<Passage> Found, bool NotFound);

/// <summary>
/// Ask a File: questions about a document or a recording answered from its own words, with where each answer is (the
/// page, the slide, the rows, the minute). The file is read once (a PDF's pages, a document as Word or LibreOffice lays
/// it out, slides, sheets; a recording heard with Whisper) and parted into passages of about a hundred words, each
/// kept with its place and its meaning (<see cref="PassageModel"/>). A question finds its passages by meaning and by
/// words together (and in the file's language too, translated, when it is asked in the other of Arabic and English);
/// the answer model (Qwen3 4B, on this PC) is given the best of them, numbered, and answers from them alone, saying
/// which ones it used, or that the file doesn't say. The passages it names are what the answer points to; where it is
/// unsure, the passages found are shown all the same. All on this PC.
/// </summary>
public static partial class AskFile
{
    /// <summary>Where what was read is kept.</summary>
    public static string CacheFolder { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Petal", "cache", "ask");

    private const int CacheVersion = 1;

    /// <summary>How many files' readings are kept; the oldest are let go.</summary>
    private const int Remembered = 30;

    /// <summary>A passage is about this many words, and no more than <see cref="LongestPassage"/>.</summary>
    private const int PassageWords = 110;
    private const int LongestPassage = 160;

    /// <summary>
    /// The passages the answer model is given, and about the most of the pieces it reads them in (what it takes to read
    /// them, at some fifty pieces a second on four cores, is most of the wait for an answer).
    /// </summary>
    private const int Given = 5;
    private const int GivenPieces = 900;

    public static IReadOnlyList<ModelInfo> FindingModels => PassageModel.Models;

    public static IReadOnlyList<ModelInfo> AnsweringModels { get; } = [ModelCatalog.LlamaCpp, ModelCatalog.QwenAnswers];

    public static bool CanFind => PassageModel.Ready;

    public static bool CanAnswer => Cloud.CloudAi.CanWrite || AnsweringModels.All(ModelStore.IsAvailable);

    public static bool IsRecording(FileFormat format) => format.Family is FormatFamily.Audio || format.Family is FormatFamily.Video && format != Formats.Gif;

    public static bool CanRead(FileFormat format) => IsRecording(format) || DocumentPlaces.CanRead(format) || format == Formats.Ppt;

    // ---------- reading ----------

    /// <summary>Whether this file was read before (and hasn't changed): it opens at once.</summary>
    public static bool IsKnown(string path, string? speechModel = null) => File.Exists(Path.Combine(CacheFolder, Key(path, speechModel) + ".json"));

    /// <summary>
    /// The file read for questions: from what was kept when it was read before, otherwise read now. A recording is heard
    /// with <paramref name="speechModel"/> (as Make Shorts hears it, and kept with it).
    /// </summary>
    public static async Task<FileKnowledge> ReadAsync(string path, string? speechModel = null, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var key = Key(path, speechModel);
        if (Load(key, path) is { } known)
        {
            progress?.Report(1);
            return known;
        }
        var format = Formats.FromPath(path) ?? throw new InvalidOperationException("DropWheel doesn't know this kind of file.");
        List<PlacedText> places;
        string? pdf = null;
        if (IsRecording(format))
        {
            var model = speechModel ?? throw new InvalidOperationException("The speech model isn't downloaded yet.");
            var heard = await ShortsMaker.ListenAsync(path, model, "auto", Slice(progress, 0, 0.85), cancellationToken).ConfigureAwait(false);
            places = Times(heard.Segments);
        }
        else if (format == Formats.Pdf)
        {
            places = await DocumentPlaces.PdfPagesAsync(path, Slice(progress, 0, 0.6), cancellationToken).ConfigureAwait(false);
            pdf = path;
        }
        else
        {
            Directory.CreateDirectory(CacheFolder);
            var laid = Path.Combine(CacheFolder, key + ".pdf");
            places = await DocumentPlaces.LaidOutAsync(path, format, laid, Slice(progress, 0, 0.6), cancellationToken).ConfigureAwait(false) ?? [];
            if (places.Count > 0)
                pdf = laid;
            else if (format == Formats.Ppt)
                throw new InvalidOperationException("Reading a PowerPoint 97–2003 file needs Microsoft PowerPoint or LibreOffice.");
            else
                places = await Task.Run(() => DocumentPlaces.Read(path, format), cancellationToken).ConfigureAwait(false);
        }
        var passages = Split(places);
        if (passages.Count == 0)
            throw new InvalidOperationException(IsRecording(format) ? "No speech was found in this file." : "There are no words to read in this file.");
        using var reader = new PassageModel();
        double from = IsRecording(format) ? 0.85 : 0.6;
        var meanings = await Task.Run(() => reader.Passages(passages.Select(Indexed).ToList(), Slice(progress, from, 1), cancellationToken), cancellationToken).ConfigureAwait(false);
        var knowledge = new FileKnowledge(path, LanguageOf(passages.Select(p => p.Text)), passages, meanings, pdf);
        Save(key, knowledge);
        progress?.Report(1);
        return knowledge;
    }

    /// <summary>A recording's places: what was said, about a minute (or a hundred words) at a time.</summary>
    internal static List<PlacedText> Times(IReadOnlyList<TranscriptSegment> segments)
    {
        var places = new List<PlacedText>();
        var text = new StringBuilder();
        double start = -1, end = 0;
        int words = 0;
        foreach (var segment in segments)
        {
            var said = segment.Text.Trim();
            if (said.Length == 0)
                continue;
            // A stretch ends before what would take it past a minute or a passage's words.
            if (start >= 0 && (words + Count(said) > PassageWords || segment.End.TotalSeconds - start > 60))
            {
                places.Add(new PlacedText(PlaceKind.Time, places.Count + 1, text.ToString().Trim()) { Start = start, End = end });
                text.Clear();
                start = -1;
                words = 0;
            }
            if (start < 0)
                start = segment.Start.TotalSeconds;
            text.Append(said).Append(' ');
            end = segment.End.TotalSeconds;
            words += Count(said);
        }
        if (start >= 0)
            places.Add(new PlacedText(PlaceKind.Time, places.Count + 1, text.ToString().Trim()) { Start = start, End = end });
        return places;
    }

    /// <summary>
    /// Places parted into passages of about <see cref="PassageWords"/> words, at sentences (rows, for a sheet), each
    /// beginning with the sentence the one before ended with, so a thought cut between two is whole in one of them.
    /// </summary>
    internal static List<Passage> Split(IReadOnlyList<PlacedText> places)
    {
        var passages = new List<Passage>();
        foreach (var place in places)
        {
            // A recording's place is a passage already.
            if (place.Kind == PlaceKind.Time)
            {
                passages.Add(new Passage(passages.Count, place, place.Text));
                continue;
            }
            var sentences = Sentences(place.Text, place.Kind == PlaceKind.Rows).ToList();
            var current = new List<string>();
            int words = 0;
            for (int i = 0; i < sentences.Count; i++)
            {
                int length = Count(sentences[i]);
                if (words > 0 && words + length > PassageWords)
                {
                    passages.Add(new Passage(passages.Count, place, string.Join(' ', current)));
                    var last = current[^1];
                    current.Clear();
                    words = 0;
                    // The sentence it ended with begins the next (never alone: a new one always follows).
                    if (Count(last) <= 40 && place.Kind != PlaceKind.Rows)
                    {
                        current.Add(last);
                        words = Count(last);
                    }
                }
                current.Add(sentences[i]);
                words += length;
            }
            if (current.Count > 0)
                passages.Add(new Passage(passages.Count, place, string.Join(' ', current)));
        }
        return passages;
    }

    /// <summary>A text's sentences (a sheet's rows), a sentence longer than a passage cut into passages' lengths.</summary>
    private static IEnumerable<string> Sentences(string text, bool rows)
    {
        var parts = rows ? text.Split('\n') : SentenceEnd().Split(text);
        foreach (var raw in parts)
        {
            var sentence = raw.Replace('\n', ' ').Trim();
            if (sentence.Length == 0)
                continue;
            var words = sentence.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            for (int from = 0; from < words.Length; from += LongestPassage)
                yield return string.Join(' ', words.Skip(from).Take(LongestPassage));
        }
    }

    [GeneratedRegex(@"(?<=[.!?؟؛;:])\s+|\n+")]
    private static partial Regex SentenceEnd();

    private static int Count(string text) => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    /// <summary>What a passage is found by: its words, after its section's heading or its sheet's name.</summary>
    internal static string Indexed(Passage passage) => passage.Place.Name.Length > 0 ? passage.Place.Name + ": " + passage.Text : passage.Text;

    /// <summary>"ar" or "en" for texts mostly in Arabic or Latin letters; "" otherwise.</summary>
    public static string LanguageOf(IEnumerable<string> texts)
    {
        long arabic = 0, latin = 0, letters = 0;
        foreach (var text in texts.Take(400))
        {
            foreach (var c in text)
            {
                if (!char.IsLetter(c))
                    continue;
                letters++;
                if (c is >= (char)0x0600 and <= (char)0x06FF)
                    arabic++;
                else if (c is >= 'A' and <= 'z')
                    latin++;
            }
        }
        if (letters == 0)
            return "";
        return arabic * 2 >= letters ? "ar" : latin * 2 >= letters ? "en" : "";
    }

    // ---------- finding ----------

    /// <summary>
    /// The passages that speak to a question, best first: by meaning and by words, each ranking counted alike (one over
    /// sixty plus the place in it), and by the question in the file's language too when it was asked in the other of
    /// Arabic and English and the translation models are here.
    /// </summary>
    public static IReadOnlyList<Passage> Find(FileKnowledge file, string question, int count = 8)
    {
        var questions = new List<string> { question.Trim() };
        var asked = LanguageOf([question]);
        if (file.Language is "ar" or "en" && asked is "ar" or "en" && asked != file.Language)
        {
            try
            {
                if (file.Language == "ar" && Translator.IsReady)
                    questions.Add(Translator.ToArabic(question, quick: true));
                else if (file.Language == "en" && Translator.EnglishReady)
                    questions.Add(Translator.ToEnglish(question, quick: true));
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or Microsoft.ML.OnnxRuntime.OnnxRuntimeException)
            {
            }
        }
        var fused = new double[file.Passages.Count];
        using var reader = new PassageModel();
        foreach (var q in questions.Where(q => q.Length > 0).Distinct())
        {
            var meaning = reader.Query(q);
            var byMeaning = Enumerable.Range(0, fused.Length).OrderByDescending(i => PassageModel.Likeness(meaning, file.Meanings[i])).ToList();
            for (int rank = 0; rank < byMeaning.Count; rank++)
                fused[byMeaning[rank]] += 1.0 / (60 + rank);
            var scores = file.Words.Scores(q);
            var byWords = Enumerable.Range(0, fused.Length).Where(i => scores[i] > 0).OrderByDescending(i => scores[i]).ToList();
            for (int rank = 0; rank < byWords.Count; rank++)
                fused[byWords[rank]] += 1.0 / (60 + rank);
        }
        return Enumerable.Range(0, fused.Length).OrderByDescending(i => fused[i]).Take(count).Select(i => file.Passages[i]).ToList();
    }

    // ---------- answering ----------

    /// <summary>
    /// The answer to a question from the passages found for it: the best of them (no more than <see cref="Given"/>, nor
    /// <see cref="GivenPieces"/> pieces) are given to the answer model, numbered with where they are, and it answers from them
    /// alone in a few sentences, saying which it used ("Sources: 2, 4"), or "NOT FOUND". What it writes is handed to
    /// <paramref name="partial"/> as it comes.
    /// </summary>
    public static async Task<PlacedAnswer> AnswerAsync(string question, IReadOnlyList<Passage> found, Action<string>? partial = null, CancellationToken cancellationToken = default)
    {
        var given = new List<Passage>();
        int pieces = 0;
        foreach (var passage in found)
        {
            int length = Pieces(passage.Text);
            if (given.Count == Given || given.Count > 0 && pieces + length > GivenPieces)
                break;
            given.Add(passage);
            pieces += length;
        }
        var prompt = new StringBuilder("Passages:\n");
        for (int i = 0; i < given.Count; i++)
            prompt.Append('[').Append(i + 1).Append("] (").Append(Where(given[i].Place)).Append(") ").Append(given[i].Text.Replace("<|", "< |")).Append('\n');
        prompt.Append("\nQuestion: ").Append(question.Trim());
        var instruction = string.Format(CultureInfo.InvariantCulture, Instruction, LanguageOf([question]) == "ar" ? "Arabic" : "the language of the question");
        var reply = await AskPetal.WriteAsync(instruction, prompt.ToString(), partial is null ? null : text => partial(Shown(text)), 260, cancellationToken,
            temperature: 0, model: ModelCatalog.QwenAnswers, begun: "Answer:").ConfigureAwait(false);
        return Read(reply, given, found);
    }

    internal const string Instruction =
        "You answer questions about a document using only the numbered passages from it. Reply in this form:\n" +
        "Answer: <one to four short sentences in {0}>\n" +
        "Sources: <the numbers of the passages the answer comes from>\n" +
        "If the passages do not answer the question, reply only: Answer: NOT FOUND";

    /// <summary>What the model has written so far, as the reader sees it: the answer alone.</summary>
    internal static string Shown(string reply)
    {
        var (text, _) = Parts(reply);
        // "NOT FOUND", or the start of it while it is being written, is no answer to show.
        return text.StartsWith("NOT FOUND", StringComparison.Ordinal) || "NOT FOUND".StartsWith(text, StringComparison.Ordinal) ? "" : text;
    }

    private static (string Answer, string Sources) Parts(string reply)
    {
        var text = reply.Trim();
        if (text.StartsWith("Answer:", StringComparison.OrdinalIgnoreCase))
            text = text["Answer:".Length..];
        // "Sources:", or now and then "Source:".
        var mark = SourcesMark().Match(text);
        return mark.Success ? (text[..mark.Index].Trim(), text[(mark.Index + mark.Length)..]) : (text.Trim(), "");
    }

    [GeneratedRegex(@"\b(?:Sources?|المصادر|المصدر)\s*:", RegexOptions.IgnoreCase)]
    private static partial Regex SourcesMark();

    /// <summary>About how many pieces the answer model reads a text in: a word of Latin letters is a little over one, of Arabic about two.</summary>
    internal static int Pieces(string text)
    {
        int arabic = 0, other = 0;
        foreach (var c in text)
        {
            if (c is >= (char)0x0600 and <= (char)0x06FF)
                arabic++;
            else if (!char.IsWhiteSpace(c))
                other++;
        }
        return (int)(arabic / 2.4 + other / 4.0) + 1;
    }

    /// <summary>The model's reply read: the answer, and the passages it names (the numbers it was given, each once).</summary>
    internal static PlacedAnswer Read(string reply, IReadOnlyList<Passage> given, IReadOnlyList<Passage> found)
    {
        var (text, sources) = Parts(reply);
        if (text.Length == 0 || text.StartsWith("NOT FOUND", StringComparison.OrdinalIgnoreCase))
            return new PlacedAnswer("", [], found, true);
        var named = new List<Passage>();
        foreach (Match m in Number().Matches(sources + " " + string.Join(' ', Bracketed().Matches(text).Select(b => b.Value))))
            if (int.TryParse(m.Value, NumberStyles.None, CultureInfo.InvariantCulture, out int n) && n >= 1 && n <= given.Count && !named.Contains(given[n - 1]))
                named.Add(given[n - 1]);
        text = Bracketed().Replace(text, "").Replace("  ", " ").Trim();
        // Named none: the passage that shares most words with the answer.
        if (named.Count == 0 && given.Count > 0)
        {
            var said = WordIndex.Terms(text).ToHashSet(StringComparer.Ordinal);
            named.Add(given.OrderByDescending(p => WordIndex.Terms(p.Text).Count(said.Contains)).First());
        }
        return new PlacedAnswer(text, named, found, false);
    }

    [GeneratedRegex(@"\d+")]
    private static partial Regex Number();

    [GeneratedRegex(@"\[\s*\d+(?:\s*[,،]\s*\d+)*\s*\]")]
    private static partial Regex Bracketed();

    /// <summary>Where a passage is, for the model (in English, which it reads best).</summary>
    internal static string Where(PlacedText place) => place.Kind switch
    {
        PlaceKind.Page => $"page {place.Number}",
        PlaceKind.Slide => $"slide {place.Number}",
        PlaceKind.Rows => $"{place.Name}, rows {place.Number}–{place.Last}",
        PlaceKind.Time => $"at {Clock(place.Start)}",
        _ => place.Name.Length > 0 ? $"section \"{place.Name}\"" : $"part {place.Number}",
    };

    /// <summary>A moment as a clock: "4:05", "1:02:03".</summary>
    public static string Clock(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, Math.Floor(seconds)));
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{(int)t.TotalMinutes}:{t.Seconds:00}";
    }

    /// <summary>The sentence of a passage that says most of what the answer (or the question) does: what to show of it.</summary>
    public static string Quote(Passage passage, string answer)
    {
        var said = WordIndex.Terms(answer).ToHashSet(StringComparer.Ordinal);
        var sentences = Sentences(passage.Text, passage.Place.Kind == PlaceKind.Rows).ToList();
        if (sentences.Count == 0)
            return passage.Text;
        return sentences.OrderByDescending(s => WordIndex.Terms(s).Count(said.Contains)).ThenBy(s => s.Length).First();
    }

    // ---------- kept ----------

    private sealed record Stored(int Version, string Language, string? Pdf, List<StoredPassage> Passages);

    private sealed record StoredPassage(int Kind, int Number, string Name, int Last, double Start, double End, string Text);

    internal static string Key(string path, string? speechModel)
    {
        var file = new FileInfo(path);
        var identity = $"{CacheVersion}|{Path.GetFullPath(path).ToLowerInvariant()}|{(file.Exists ? file.Length : 0)}|{(file.Exists ? file.LastWriteTimeUtc.Ticks : 0)}|{Path.GetFileName(speechModel ?? "")}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..32].ToLowerInvariant();
    }

    private static void Save(string key, FileKnowledge knowledge)
    {
        try
        {
            Directory.CreateDirectory(CacheFolder);
            var stored = new Stored(CacheVersion, knowledge.Language, knowledge.PagesPdf,
                knowledge.Passages.Select(p => new StoredPassage((int)p.Place.Kind, p.Place.Number, p.Place.Name, p.Place.Last, p.Place.Start, p.Place.End, p.Text)).ToList());
            using (var meanings = new BinaryWriter(File.Create(Path.Combine(CacheFolder, key + ".meanings"))))
                foreach (var meaning in knowledge.Meanings)
                    foreach (var value in meaning)
                        meanings.Write(value);
            File.WriteAllText(Path.Combine(CacheFolder, key + ".json"), JsonSerializer.Serialize(stored), new UTF8Encoding(false));
            var keep = new DirectoryInfo(CacheFolder).GetFiles("*.json").OrderByDescending(f => f.LastWriteTimeUtc).Take(Remembered).Select(f => Path.GetFileNameWithoutExtension(f.Name)).ToHashSet();
            foreach (var old in new DirectoryInfo(CacheFolder).GetFiles().Where(f => !keep.Contains(Path.GetFileNameWithoutExtension(f.Name))))
                old.Delete();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static FileKnowledge? Load(string key, string path)
    {
        try
        {
            var json = Path.Combine(CacheFolder, key + ".json");
            var binary = Path.Combine(CacheFolder, key + ".meanings");
            if (!File.Exists(json) || !File.Exists(binary))
                return null;
            var stored = JsonSerializer.Deserialize<Stored>(File.ReadAllText(json));
            if (stored is null || stored.Version != CacheVersion || stored.Pdf is { } pdf && !File.Exists(pdf))
                return null;
            var passages = stored.Passages.Select((p, i) => new Passage(i, new PlacedText((PlaceKind)p.Kind, p.Number, p.Text) { Name = p.Name, Last = p.Last, Start = p.Start, End = p.End }, p.Text)).ToList();
            var bytes = File.ReadAllBytes(binary);
            if (bytes.Length != passages.Count * PassageModel.Size * 4)
                return null;
            var meanings = new float[passages.Count][];
            for (int i = 0; i < passages.Count; i++)
            {
                meanings[i] = new float[PassageModel.Size];
                Buffer.BlockCopy(bytes, i * PassageModel.Size * 4, meanings[i], 0, PassageModel.Size * 4);
            }
            // Used again: kept longer.
            File.SetLastWriteTimeUtc(json, DateTime.UtcNow);
            return new FileKnowledge(path, stored.Language, passages, meanings, stored.Pdf);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static IProgress<double>? Slice(IProgress<double>? progress, double from, double to) => progress is null ? null : new Part(progress, from, to);
}
