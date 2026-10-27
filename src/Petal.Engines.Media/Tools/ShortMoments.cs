using System.Globalization;
using System.Text;

namespace Petal.Engines.Media;

/// <summary>How long the clips are to be: up to half a minute, half a minute to a minute, or a minute to a minute and a half.</summary>
public enum ShortLength
{
    Quick,
    Standard,
    Long,
}

/// <summary>What makes a moment stand out (shown beside it).</summary>
public enum MomentReason
{
    /// <summary>It opens with a question.</summary>
    Question,
    /// <summary>Its first words make you want to hear the rest.</summary>
    Hook,
    /// <summary>Someone tells what happened.</summary>
    Story,
    /// <summary>Advice or a lesson.</summary>
    Advice,
    /// <summary>Numbers and facts.</summary>
    Numbers,
    /// <summary>Strong feeling.</summary>
    Feeling,
    /// <summary>Laughter.</summary>
    Laughter,
    /// <summary>Said with energy.</summary>
    Lively,
    /// <summary>A whole thought, from start to finish.</summary>
    Complete,
}

/// <summary>
/// A stretch of a video that makes a good short clip: from just before its first word to just after its last, how good
/// it is (1 to 99), why, and its words with their times in the video.
/// </summary>
public sealed record Moment(double Start, double End, int Score, IReadOnlyList<MomentReason> Reasons, IReadOnlyList<SpokenWord> Words)
{
    public double Length => End - Start;

    public string Text => string.Join(' ', Words.Select(w => w.Text));

    /// <summary>Its first sentence.</summary>
    public string Opening { get; init; } = "";

    /// <summary>A short title, written for it or taken from what is said.</summary>
    public string Title { get; init; } = "";

    /// <summary>The judgement the score comes from (about 0 to 1).</summary>
    internal double Raw { get; init; }
}

/// <summary>
/// Finds the moments of a long video that stand on their own as short clips, the way an editor would: each starts where
/// a sentence starts and ends where a thought ends, and they are ranked by what makes people stay. An opening that pulls
/// you in (a question, "imagine…", "the biggest mistake…"; not "and so, as I said…"), a whole thought with an ending,
/// lively delivery (louder and quicker than the speaker's usual, rising and falling), something worth hearing (a story,
/// advice, numbers, feeling, laughter), one topic from start to end, and a length near the one asked for. Words in
/// English and Arabic (and Moroccan, Egyptian and Gulf ways of saying them) are read; any other language is judged on
/// its delivery and shape. The best are kept apart: no two overlap or say the same thing.
/// </summary>
public static partial class ShortMoments
{
    /// <summary>The shortest, longest and best length of a clip, in seconds.</summary>
    public static (double Shortest, double Longest, double Ideal) Range(ShortLength length) => length switch
    {
        ShortLength.Quick => (12, 30, 22),
        ShortLength.Long => (55, 90, 70),
        _ => (25, 60, 42),
    };

    /// <summary>
    /// The best moments, the best first: <paramref name="count"/> of them, then up to <paramref name="spare"/> more that
    /// could take their place. Fewer when the video hasn't that much to say.
    /// </summary>
    /// <param name="segments">What was said, with the words' own times (<see cref="TranscribeOptions.WordTimes"/>).</param>
    /// <param name="levels">How loud the sound is; without it the delivery isn't judged.</param>
    public static IReadOnlyList<Moment> Find(IReadOnlyList<TranscriptSegment> segments, SoundLevels? levels, ShortLength length, int count, int spare = 0)
    {
        var words = Clean(segments);
        if (words.Count < 8)
            return [];
        var sentences = Sentences(words);
        var reading = new Reading(words, sentences, levels);
        var (shortest, longest, ideal) = Range(length);
        var candidates = reading.Candidates(shortest, longest, ideal);
        // A video shorter than the shortest clip asked for still gives its best stretch.
        if (candidates.Count == 0)
            candidates = reading.Candidates(Math.Min(shortest, 8), longest, ideal);
        var picked = new List<Candidate>();
        foreach (var candidate in candidates.OrderByDescending(c => c.Total))
        {
            if (picked.Count >= count + spare)
                break;
            if (picked.Any(p => Overlap(p, candidate) > 0.2 * Math.Min(p.Length, candidate.Length)))
                continue;
            if (picked.Any(p => Cosine(reading.Vector(p), reading.Vector(candidate)) > 0.55))
                continue;
            picked.Add(candidate);
        }
        return picked.Select(reading.ToMoment).ToList();
    }

    private static double Overlap(Candidate a, Candidate b) => Math.Max(0, Math.Min(a.End, b.End) - Math.Max(a.Start, b.Start));

    /// <summary>
    /// The line of a moment that makes the best headline: a sentence of four to ten words from its first part, a question
    /// or a hook first, the earlier the better; failing that, its opening up to a comma or ten words.
    /// </summary>
    public static string Headline(Moment moment)
    {
        var sentences = new List<List<SpokenWord>>();
        var current = new List<SpokenWord>();
        foreach (var w in moment.Words)
        {
            current.Add(w);
            if (EndsSentence(w.Text))
            {
                sentences.Add(current);
                current = [];
            }
        }
        if (current.Count > 0)
            sentences.Add(current);
        double within = moment.Start + Math.Max(8, moment.Length * 0.4);
        List<SpokenWord>? best = null;
        double bestScore = double.MinValue;
        for (int i = 0; i < sentences.Count && sentences[i][0].Start <= within; i++)
        {
            var keys = sentences[i].Select(w => Key(w.Text)).ToList();
            int skip = 0;
            while (skip < keys.Count - 1 && Phrases.Fillers.Contains(keys[skip]))
                skip++;
            var said = keys.Skip(skip).Where(k => k.Length > 0).ToList();
            int content = said.Count(k => k.Length > 1 && !Phrases.StopWords.Contains(k));
            if (said.Count is < 4 or > 10 || content < 1)
                continue;
            var last = sentences[i][^1].Text;
            double score = (last.EndsWith('?') || last.EndsWith('؟') ? 1 : 0) + 0.8 * Count(said, Phrases.Hooks, 6) + 0.3 * said.Count(k => k.Any(char.IsDigit))
                           + 0.1 * content - 0.3 * i - (Phrases.Connectors.Contains(said[0]) ? 0.8 : 0) - (Phrases.Pronouns.Contains(said[0]) ? 0.3 : 0);
            if (score > bestScore)
            {
                bestScore = score;
                best = sentences[i].Skip(skip).ToList();
            }
        }
        if (best is null)
        {
            var opening = sentences.Count > 0 ? sentences[0] : [];
            int cut = opening.FindIndex(w => w.Text.EndsWith(',') || w.Text.EndsWith('،'));
            best = opening.Take(cut is >= 3 and < 10 ? cut + 1 : 10).ToList();
            if (best.Count == 0)
                return "";
            bool whole = best.Count == opening.Count || cut is >= 3 and < 10;
            return Tidy(best) + (whole ? "" : "…");
        }
        return Tidy(best);
    }

    /// <summary>Words as a headline: the first letter a capital, no full stop or comma at the end (a question keeps its mark).</summary>
    private static string Tidy(List<SpokenWord> words)
    {
        var text = string.Join(' ', words.Select(w => w.Text)).Trim().TrimEnd('.', ',', '،', ';', ':');
        return text.Length > 0 && char.IsLower(text[0]) ? char.ToUpper(text[0], CultureInfo.InvariantCulture) + text[1..] : text;
    }

    /// <summary>
    /// Whether a title holds to what a moment says: most of its words are words of the moment (by their stems), and it
    /// is in the same script (an Arabic clip gets an Arabic title).
    /// </summary>
    public static bool HoldsTo(string title, Moment moment)
    {
        static double ArabicShare(string text)
        {
            int letters = text.Count(char.IsLetter);
            return letters == 0 ? 0 : (double)text.Count(c => char.IsLetter(c) && c is >= (char)0x0600 and <= (char)0x06FF) / letters;
        }
        if (Math.Abs(ArabicShare(title) - ArabicShare(moment.Text)) > 0.5)
            return false;
        var said = new HashSet<string>(moment.Words.Select(w => Stem(Key(w.Text))), StringComparer.Ordinal);
        var keys = title.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(Key).Where(k => k.Length >= 3 && !Phrases.StopWords.Contains(k)).ToList();
        if (keys.Count == 0)
            return false;
        int found = keys.Count(k => said.Contains(Stem(k)) || said.Any(s => s.Length >= 4 && (s.StartsWith(Stem(k), StringComparison.Ordinal) || Stem(k).StartsWith(s, StringComparison.Ordinal))));
        return found >= Math.Ceiling(keys.Count * 0.6);
    }

    /// <summary>
    /// The pauses inside a moment worth cutting out, as (start, end) in the video's time: where the words stop for
    /// longer than <paramref name="longest"/> seconds and the sound there is quiet. A little of each pause is kept on
    /// both sides, so the speech keeps its breath and no word is clipped.
    /// </summary>
    public static List<(double Start, double End)> Pauses(Moment moment, SoundLevels? levels, double longest = 0.45)
    {
        var cuts = new List<(double Start, double End)>();
        for (int i = 0; i + 1 < moment.Words.Count; i++)
        {
            var (a, b) = (moment.Words[i], moment.Words[i + 1]);
            if (b.Start - a.End < longest)
                continue;
            double from = a.End + 0.12, to = b.Start - 0.1;
            if (levels is not null)
            {
                // Where the sound really stops and starts again (the words' times can be a little off).
                from = levels.SoundEndAfter(a.End, b.Start) + 0.1;
                to = levels.SoundStartBefore(b.Start + 0.1, from) - 0.08;
                if (to - from < 0.2 || levels.QuietShare(from, to) < 0.85)
                    continue;
            }
            if (to - from >= 0.2 && from > moment.Start && to < moment.End)
                cuts.Add((from, to));
        }
        return cuts;
    }

    // ---------- the words ----------

    /// <summary>A word as the moments are found from: a speaker's turn begins with it when Whisper marked one ("&gt;&gt;").</summary>
    internal readonly record struct Word(string Text, double Start, double End, bool Turn, int Segment, string Key);

    /// <summary>
    /// The words, without Whisper's marks: "&gt;&gt;" (another speaker) becomes a turn on the next word, and "(laughs)" or
    /// "[laughter]" a laugh on the word before. A word's end, which Whisper times loosely, is as long as its letters take
    /// to say, never into the next word.
    /// </summary>
    internal static List<Word> Clean(IReadOnlyList<TranscriptSegment> segments)
    {
        var words = new List<Word>();
        bool turn = false;
        int repeats = 0;
        for (int s = 0; s < segments.Count; s++)
        {
            foreach (var w in segments[s].Words)
            {
                var text = w.Text.Trim();
                if (text.StartsWith(">>", StringComparison.Ordinal))
                {
                    turn = true;
                    text = text[2..].Trim();
                }
                else if (text is "-" or "–" or "—")
                {
                    turn = true;
                    continue;
                }
                if (text.Length == 0)
                    continue;
                if (text[0] is '[' or '(' or '*' || text.All(c => c is '♪' or '.' or '…'))
                {
                    if (Laughing(text) && words.Count > 0)
                        words[^1] = words[^1] with { Text = words[^1].Text + LaughMark };
                    continue;
                }
                var key = Key(text);
                if (key.Length > 0 && words.Count > 0 && words[^1].Key == key && !turn)
                {
                    // A word said three times and more running ("we we we we") is Whisper going round in circles, or a
                    // stammer: once is enough. Twice ("very very") is how people talk.
                    if (++repeats >= 2)
                    {
                        if (repeats == 2)
                            words.RemoveAt(words.Count - 1);
                        words[^1] = words[^1] with { End = w.End };
                        continue;
                    }
                }
                else
                    repeats = 0;
                words.Add(new Word(text, w.Start, w.End, turn, s, key));
                turn = false;
            }
        }
        for (int i = 0; i < words.Count; i++)
        {
            var w = words[i];
            double next = i + 1 < words.Count ? words[i + 1].Start : double.MaxValue;
            double spoken = w.Start + 0.08 + Math.Min(1.2, Letters(w.Text) * 0.065);
            double end = Math.Min(next, Math.Max(w.End, spoken));
            words[i] = w with { End = Math.Max(w.Start, end) };
        }
        return words;
    }

    /// <summary>Put after a word that was followed by laughter; never shown.</summary>
    internal const string LaughMark = "\u2063";

    private static bool Laughing(string text)
    {
        var lower = text.ToLowerInvariant();
        return lower.Contains("laugh") || lower.Contains("ضحك") || lower.Contains("rire") || lower.Contains("risa");
    }

    private static int Letters(string text) => text.Count(char.IsLetterOrDigit);

    // ---------- sentences ----------

    internal readonly record struct Sentence(int First, int Last, double Start, double End);

    /// <summary>
    /// The words in sentences: at a full stop, a question or exclamation mark, a change of speaker or a long pause.
    /// Whisper doesn't always write full stops (often not in Arabic); then a pause of half a second ends a sentence
    /// too. A sentence never runs past 24 seconds: a long one is cut at its longest pause.
    /// </summary>
    internal static List<Sentence> Sentences(IReadOnlyList<Word> words)
    {
        int stops = words.Count(w => EndsSentence(w.Text));
        bool punctuated = stops * 45 >= words.Count;
        var sentences = new List<Sentence>();
        int first = 0;
        for (int i = 0; i < words.Count; i++)
        {
            bool last = i == words.Count - 1;
            bool end = last;
            if (!last)
            {
                var next = words[i + 1];
                double gap = next.Start - words[i].End;
                double length = words[i].End - words[first].Start;
                end = next.Turn || EndsSentence(words[i].Text) || gap >= (punctuated ? 1.5 : 0.9)
                      || !punctuated && (gap >= 0.5 && length >= 2.5 || next.Segment != words[i].Segment && gap >= 0.25 && length >= 1.5);
            }
            if (end)
            {
                Split(words, first, i, sentences);
                first = i + 1;
            }
        }
        return sentences;
    }

    private static void Split(IReadOnlyList<Word> words, int first, int last, List<Sentence> into)
    {
        if (words[last].End - words[first].Start <= 24 || last - first < 4)
        {
            into.Add(new Sentence(first, last, words[first].Start, words[last].End));
            return;
        }
        // At the longest pause, or failing that a comma, not too near either end.
        int best = -1;
        double bestScore = -1;
        for (int i = first + 2; i < last - 1; i++)
        {
            double score = words[i + 1].Start - words[i].End + (words[i].Text.EndsWith(',') || words[i].Text.EndsWith('،') ? 0.3 : 0);
            if (score > bestScore)
            {
                bestScore = score;
                best = i;
            }
        }
        Split(words, first, best, into);
        Split(words, best + 1, last, into);
    }

    private static readonly string[] Abbreviations = ["mr.", "mrs.", "ms.", "dr.", "st.", "jr.", "sr.", "vs.", "e.g.", "i.e.", "prof.", "mt.", "no."];

    /// <summary>Whether a sentence ends with this word: a full stop (not "Dr." or "J."), "?", "!", "؟" or "…".</summary>
    internal static bool EndsSentence(string word)
    {
        var w = word.TrimEnd('"', '\'', '”', '’', '»', ')', LaughMark[0]);
        if (w.Length == 0)
            return false;
        char c = w[^1];
        if (c is '?' or '!' or '؟' or '…' or '۔' or '。')
            return true;
        if (c != '.')
            return false;
        var lower = w.ToLowerInvariant();
        if (Abbreviations.Contains(lower))
            return false;
        // An initial: "J." in "J. Smith".
        return !(w.Length == 2 && char.IsUpper(w[0]));
    }

    // ---------- reading the words ----------

    /// <summary>A word as it is compared: lower case, without marks around it; Arabic without vowel marks and with one alef, one ya and one ha.</summary>
    internal static string Key(string word)
    {
        var sb = new StringBuilder(word.Length);
        foreach (var raw in word)
        {
            char c = char.ToLowerInvariant(raw);
            if (c is >= (char)0x064B and <= (char)0x065F || c == (char)0x0670 || c == (char)0x0640 || c is '\'' or '’' or '‘')
                continue;
            c = c switch
            {
                (char)0x0622 or (char)0x0623 or (char)0x0625 or (char)0x0671 => (char)0x0627,
                (char)0x0649 or (char)0x06CC or (char)0x0626 => (char)0x064A,
                (char)0x0629 => (char)0x0647,
                (char)0x0624 => (char)0x0648,
                (char)0x06A9 => (char)0x0643,
                >= (char)0x0660 and <= (char)0x0669 => (char)('0' + (c - 0x0660)),
                >= (char)0x06F0 and <= (char)0x06F9 => (char)('0' + (c - 0x06F0)),
                _ => c,
            };
            if (char.IsLetterOrDigit(c) || c == '%')
                sb.Append(c);
            else if (c is '-' && sb.Length > 0)
                sb.Append(c);
        }
        return sb.ToString().Trim('-');
    }

    private static bool IsArabic(string key) => key.Length > 0 && key[0] is >= (char)0x0600 and <= (char)0x06FF;

    /// <summary>A word's stem for telling topics apart: Arabic without "ال" and common endings, English without plural and tense endings.</summary>
    internal static string Stem(string key)
    {
        if (IsArabic(key))
        {
            foreach (var prefix in (string[])["وال", "بال", "كال", "فال", "لل", "ال"])
                if (key.StartsWith(prefix, StringComparison.Ordinal) && key.Length - prefix.Length >= 3)
                {
                    key = key[prefix.Length..];
                    break;
                }
            foreach (var suffix in (string[])["ها", "هم", "هن", "كم", "نا", "ات", "ون", "ين", "ان", "يه", "ه"])
                if (key.EndsWith(suffix, StringComparison.Ordinal) && key.Length - suffix.Length >= 3)
                    return key[..^suffix.Length];
            return key;
        }
        if (key.Length > 6 && key.EndsWith("ing", StringComparison.Ordinal))
            return key[..^3];
        if (key.Length > 5 && key.EndsWith("ed", StringComparison.Ordinal))
            return key[..^2];
        if (key.Length > 4 && key.EndsWith('s') && !key.EndsWith("ss", StringComparison.Ordinal))
            return key[..^1];
        return key;
    }

    private sealed record Candidate(int FirstSentence, int LastSentence, int FirstWord, int LastWord, double Start, double End, double Total,
        IReadOnlyList<MomentReason> Reasons)
    {
        public double Length => End - Start;
    }

    /// <summary>What is known of each sentence, worked out once; and the candidates from it.</summary>
    private sealed class Reading
    {
        private readonly IReadOnlyList<Word> _words;
        private readonly IReadOnlyList<Sentence> _sentences;
        private readonly SoundLevels? _levels;
        private readonly Facts[] _facts;
        private readonly Dictionary<string, double> _idf = new(StringComparer.Ordinal);
        private readonly double _loud, _loudSpread, _rate, _rateSpread, _spreadMiddle, _spreadSpread, _salience;

        public Reading(IReadOnlyList<Word> words, IReadOnlyList<Sentence> sentences, SoundLevels? levels)
        {
            _words = words;
            _sentences = sentences;
            _levels = levels;
            _facts = sentences.Select(s => Learn(words, s, levels)).ToArray();
            // How rare each stem is across the sentences: rare words that come back within a moment mark its topic.
            var df = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var f in _facts)
                foreach (var stem in f.Counts.Keys)
                    df[stem] = df.GetValueOrDefault(stem) + 1;
            foreach (var (stem, n) in df)
                _idf[stem] = Math.Log((sentences.Count + 1.0) / (n + 0.5));
            (_loud, _loudSpread) = Middle(_facts.Where(f => f.Length >= 1).Select(f => f.Loud));
            (_rate, _rateSpread) = Middle(_facts.Where(f => f.Length >= 1).Select(f => f.Rate));
            (_spreadMiddle, _spreadSpread) = Middle(_facts.Where(f => f.Length >= 1).Select(f => f.Spread));
            _salience = _facts.Length == 0 ? 0 : _facts.Average(Salience);
        }

        /// <summary>The middle of some numbers and how far they usually are from it.</summary>
        private static (double Middle, double Spread) Middle(IEnumerable<double> values)
        {
            var sorted = values.Where(double.IsFinite).OrderBy(v => v).ToList();
            if (sorted.Count == 0)
                return (0, 1);
            double middle = sorted[sorted.Count / 2];
            var distances = sorted.Select(v => Math.Abs(v - middle)).OrderBy(v => v).ToList();
            return (middle, Math.Max(1e-6, distances[distances.Count / 2] * 1.4826));
        }

        private double Salience(Facts f) =>
            f.Counts.Count == 0 ? 0 : f.Counts.Sum(p => _idf.GetValueOrDefault(p.Key) * p.Value) / f.Counts.Values.Sum();

        public List<Candidate> Candidates(double shortest, double longest, double ideal)
        {
            var list = new List<Candidate>();
            for (int i = 0; i < _sentences.Count; i++)
            {
                var start = _facts[i];
                if (start.Words - start.Fillers <= 0)
                    continue;
                int firstWord = _sentences[i].First + start.Fillers;
                double from = StartOf(firstWord);
                for (int j = i; j < _sentences.Count; j++)
                {
                    int lastWord = _sentences[j].Last;
                    double to = EndOf(lastWord);
                    double length = to - from;
                    if (length > longest)
                        break;
                    if (length < shortest)
                        continue;
                    var (total, reasons) = Judge(i, j, from, to, ideal, longest - shortest);
                    list.Add(new Candidate(i, j, firstWord, lastWord, from, to, total, reasons));
                }
            }
            return list;
        }

        /// <summary>A clip starts a little before its first word, never in the word before.</summary>
        private double StartOf(int word)
        {
            double t = _words[word].Start - 0.12;
            if (word > 0)
                t = Math.Max(t, Math.Min(_words[word].Start, _words[word - 1].End + 0.04));
            return Math.Max(0, t);
        }

        /// <summary>A clip ends a little after its last word, before the next one begins.</summary>
        private double EndOf(int word)
        {
            double t = _words[word].End + 0.35;
            if (_levels is not null)
                t = Math.Max(t, Math.Min(_levels.SoundEndAfter(_words[word].End, _words[word].End + 1.2) + 0.15, t + 0.6));
            if (word + 1 < _words.Count)
                t = Math.Min(t, Math.Max(_words[word].End, _words[word + 1].Start - 0.06));
            return _levels is null ? t : Math.Min(t, _levels.Length);
        }

        /// <summary>How good a clip from sentence <paramref name="i"/> to <paramref name="j"/> is (about 0 to 1), and why.</summary>
        private (double Total, IReadOnlyList<MomentReason> Reasons) Judge(int i, int j, double from, double to, double ideal, double spread)
        {
            var first = _facts[i];
            var last = _facts[j];
            double length = to - from;

            // The opening. A question or a hook counts when it says something ("What about, like, you know?" doesn't).
            double hook = 0;
            bool question = first.Question && first.Length <= 12 && first.Content >= 1;
            bool hooked = first.Hooks > 0 && first.Content >= 1;
            if (question)
                hook += 1.0;
            if (hooked)
                hook += 0.6 + (first.Hooks > 1 ? 0.2 : 0);
            if (first.Words - first.Fillers is >= 3 and <= 12)
                hook += 0.3;
            if (first.Turn)
                hook += 0.25;
            if (first.Numbers > 0)
                hook += 0.15;
            if (first.Connector)
                hook -= 0.9;
            if (first.Pronoun)
                hook -= 0.4;
            if (first.BackReference)
                hook -= 0.7;
            if (first.Words - first.Fillers < 3 && !first.Question)
                hook -= 0.5;
            hook = Math.Clamp((hook + 0.6) / 2.2, 0, 1);

            // The ending: a whole sentence, then a pause or the next question (the answer is over). Not a question late in
            // the clip, nor a short "So, you travel…" that starts the next part.
            double pauseAfter = j + 1 < _sentences.Count ? _sentences[j + 1].Start - _sentences[j].End : 9;
            bool nextAsks = j + 1 < _sentences.Count && (_facts[j + 1].Question || _facts[j + 1].Turn || _facts[j + 1].MovesOn);
            bool lateQuestion = j > i && (last.Question || j - 1 > i && _facts[j - 1].Question);
            bool turnsAway = j > i && (last.Leading && last.Words <= 12 || last.MovesOn);
            double ending = (last.Complete ? 0.5 : 0) + (pauseAfter >= 0.5 ? 0.2 : 0) + (nextAsks ? 0.3 : 0) - (last.Dangling ? 0.8 : 0)
                            - (lateQuestion ? 0.3 : 0) - (turnsAway ? 0.35 : 0) + (last.Feeling + last.Laughter > 0 ? 0.2 : 0);
            ending = Math.Clamp((ending + 0.4) / 1.4, 0, 1);

            // The delivery: louder, quicker and more varied than this speaker's usual.
            double energy = 0.5, spoken = 0, wordsSaid = 0, loud = 0, spreadSum = 0, speech = 0;
            for (int k = i; k <= j; k++)
            {
                var f = _facts[k];
                spoken += f.Spoken;
                wordsSaid += f.Words;
                loud += f.Loud * f.Length;
                spreadSum += f.Spread * f.Length;
                speech += f.Length;
            }
            if (_levels is not null && speech > 0)
            {
                double zLoud = (loud / speech - _loud) / Math.Max(2, _loudSpread);
                double zRate = (wordsSaid / speech - _rate) / Math.Max(0.3, _rateSpread);
                double zSpread = (spreadSum / speech - _spreadMiddle) / Math.Max(1, _spreadSpread);
                energy = 1 / (1 + Math.Exp(-(0.6 * zLoud + 0.35 * zRate + 0.25 * zSpread)));
            }
            double density = Math.Clamp((spoken / Math.Max(1, length) - 0.45) / 0.4, 0, 1);

            // What is said.
            double story = 0, advice = 0, numbers = 0, feeling = 0, laughter = 0, questions = 0, backReferences = 0, salience = 0;
            for (int k = i; k <= j; k++)
            {
                var f = _facts[k];
                story += f.Story;
                advice += f.Advice;
                numbers += f.Numbers;
                feeling += f.Feeling;
                laughter += f.Laughter;
                questions += f.Question ? 1 : 0;
                if (k > i && f.BackReference)
                    backReferences++;
                salience += Salience(f) * f.Length;
            }
            double halfMinutes = Math.Max(0.5, length / 30);
            double markers = (story * 1.0 + advice * 0.8 + Math.Min(numbers, 4) * 0.5 + feeling * 0.6 + laughter * 1.2 + questions * 0.3) / halfMinutes;
            double rare = speech > 0 ? salience / speech - _salience : 0;
            double content = 0.7 * (1 - Math.Exp(-markers / 2)) + 0.3 / (1 + Math.Exp(-rare * 2));

            // One topic: the two halves share their words more than the moment shares them with what is around it.
            double cohesion = 0.5;
            if (j > i)
            {
                int middle = (i + j) / 2;
                double inside = Cosine(Sum(i, middle), Sum(middle + 1, j));
                double before = i > 0 ? Cosine(Sum(Math.Max(0, i - 2), i - 1), Sum(i, j)) : 0;
                double after = j + 1 < _sentences.Count ? Cosine(Sum(j + 1, Math.Min(_sentences.Count - 1, j + 2)), Sum(i, j)) : 0;
                cohesion = Math.Clamp(0.5 + inside - 0.5 * (before + after), 0, 1);
            }

            // Near the length asked for, gently: a whole thought a little shorter beats one padded with the next.
            double fit = Math.Exp(-Math.Pow((length - ideal) / Math.Max(6, spread * 0.8), 2));
            double total = 0.27 * hook + 0.12 * ending + 0.17 * energy + 0.08 * density + 0.16 * content + 0.10 * cohesion + 0.10 * fit
                           - Math.Min(0.2, backReferences * 0.08);

            var reasons = new List<(MomentReason Reason, double Strength)>();
            if (question)
                reasons.Add((MomentReason.Question, 1));
            else if (hooked)
                reasons.Add((MomentReason.Hook, 0.9));
            if (laughter > 0)
                reasons.Add((MomentReason.Laughter, 0.8));
            if (story > 0)
                reasons.Add((MomentReason.Story, 0.6 + 0.1 * story));
            if (advice > 0)
                reasons.Add((MomentReason.Advice, 0.55 + 0.1 * advice));
            if (numbers >= 2)
                reasons.Add((MomentReason.Numbers, 0.5));
            if (feeling >= 2)
                reasons.Add((MomentReason.Feeling, 0.5 + 0.05 * feeling));
            if (energy >= 0.7)
                reasons.Add((MomentReason.Lively, energy));
            if (cohesion >= 0.6 && ending >= 0.6)
                reasons.Add((MomentReason.Complete, 0.45));
            return (total, reasons.OrderByDescending(r => r.Strength).Take(3).Select(r => r.Reason).ToList());
        }

        private Dictionary<string, double> Sum(int from, int to)
        {
            var sum = new Dictionary<string, double>(StringComparer.Ordinal);
            for (int k = from; k <= to; k++)
                foreach (var (stem, n) in _facts[k].Counts)
                    sum[stem] = sum.GetValueOrDefault(stem) + n * _idf.GetValueOrDefault(stem);
            return sum;
        }

        public Dictionary<string, double> Vector(Candidate c) => Sum(c.FirstSentence, c.LastSentence);

        public Moment ToMoment(Candidate c)
        {
            var words = new List<SpokenWord>();
            for (int k = c.FirstWord; k <= c.LastWord; k++)
                words.Add(new SpokenWord(_words[k].Text.Replace(LaughMark, ""), _words[k].Start, _words[k].End));
            // The fillers before it gone, the first word starts the clip's first sentence ("the truth is…" → "The truth is…").
            if (words.Count > 0 && words[0].Text is { Length: > 0 } first && char.IsLower(first[0]))
                words[0] = words[0] with { Text = char.ToUpper(first[0], CultureInfo.InvariantCulture) + first[1..] };
            int openingEnd = _sentences[c.FirstSentence].Last;
            var opening = string.Join(' ', words.Take(openingEnd - c.FirstWord + 1).Select(w => w.Text));
            return new Moment(c.Start, c.End, Score(c.Total), c.Reasons, words) { Opening = opening, Raw = c.Total };
        }
    }

    /// <summary>The judgement as a score from 1 to 99: a strong clip is in the eighties and nineties, a weak one under fifty.</summary>
    internal static int Score(double total) => (int)Math.Round(Math.Clamp((total - 0.3) / 0.52, 0.01, 0.99) * 100);

    private static double Cosine(Dictionary<string, double> a, Dictionary<string, double> b)
    {
        if (a.Count == 0 || b.Count == 0)
            return 0;
        double dot = 0;
        var (small, large) = a.Count <= b.Count ? (a, b) : (b, a);
        foreach (var (key, value) in small)
            if (large.TryGetValue(key, out var other))
                dot += value * other;
        double na = Math.Sqrt(a.Values.Sum(v => v * v)), nb = Math.Sqrt(b.Values.Sum(v => v * v));
        return na == 0 || nb == 0 ? 0 : dot / (na * nb);
    }

    /// <summary>What a sentence holds and how it was said.</summary>
    private sealed class Facts
    {
        public int Words, Fillers, Content, Hooks, Story, Advice, Numbers, Feeling, Laughter;
        public bool Question, Connector, Pronoun, BackReference, Complete, Dangling, Turn, Leading, MovesOn;
        public double Length, Spoken, Loud, Spread, Rate;
        public Dictionary<string, int> Counts = new(StringComparer.Ordinal);
    }

    private static Facts Learn(IReadOnlyList<Word> words, Sentence sentence, SoundLevels? levels)
    {
        var f = new Facts();
        var keys = new List<string>();
        for (int k = sentence.First; k <= sentence.Last; k++)
        {
            keys.Add(words[k].Key);
            f.Spoken += words[k].End - words[k].Start;
            if (words[k].Text.EndsWith(LaughMark, StringComparison.Ordinal) || IsLaugh(words[k].Key))
                f.Laughter++;
        }
        f.Words = keys.Count;
        f.Turn = words[sentence.First].Turn;
        f.Length = Math.Max(0.3, sentence.End - sentence.Start);
        f.Rate = f.Words / f.Length;
        if (levels is not null)
        {
            f.Loud = levels.Mean(sentence.Start, sentence.End);
            f.Spread = levels.Spread(sentence.Start, sentence.End);
        }
        // Fillers at the start are left out of the clip ("So, um, the thing is…" starts at "the").
        while (f.Fillers < Math.Min(4, keys.Count - 1))
        {
            if (Phrases.Fillers.Contains(keys[f.Fillers]))
                f.Fillers++;
            else if (f.Fillers + 2 < keys.Count && Phrases.TwoWordFillers.Any(p => p[0] == keys[f.Fillers] && p[1] == keys[f.Fillers + 1]))
                f.Fillers += 2;
            else
                break;
        }
        f.Leading = keys.Count > 0 && (Phrases.Turns.Contains(keys[0]) || Phrases.Fillers.Contains(keys[0]));
        var said = keys.Skip(f.Fillers).Where(k => k.Length > 0).ToList();
        if (said.Count == 0)
            return f;
        f.Content = said.Count(k => k.Length > 1 && !Phrases.StopWords.Contains(k) && !Phrases.Fillers.Contains(k));
        var lastText = words[sentence.Last].Text.Replace(LaughMark, "");
        f.Question = (lastText.EndsWith('?') || lastText.EndsWith('؟') || Phrases.QuestionWords.Contains(said[0]) && said.Count <= 14) && !Tagged(said);
        f.Connector = Phrases.Connectors.Contains(said[0]);
        f.Pronoun = Phrases.Pronouns.Contains(said[0]);
        f.Complete = EndsSentence(lastText);
        f.Dangling = Phrases.Connectors.Contains(said[^1]) || lastText.EndsWith(',') || lastText.EndsWith('،');
        f.Hooks = Count(said, Phrases.Hooks, 6);
        f.MovesOn = Count(keys, Phrases.Transitions, 5) > 0;
        f.BackReference = Count(said, Phrases.BackReferences, said.Count) > 0;
        f.Story = Count(said, Phrases.Story, said.Count);
        f.Advice = Count(said, Phrases.Advice, said.Count);
        f.Feeling = Count(said, Phrases.Feeling, said.Count);
        f.Numbers = said.Count(k => k.Any(char.IsDigit) || Phrases.Numbers.Contains(k));
        foreach (var key in said)
        {
            if (key.Length < 2 || Phrases.StopWords.Contains(key) || key.All(char.IsDigit))
                continue;
            var stem = Stem(key);
            f.Counts[stem] = f.Counts.GetValueOrDefault(stem) + 1;
        }
        return f;
    }

    /// <summary>Whether a sentence ends with a tag ("…, right?"): a statement, not a question. A sentence that is only the tag ("Right?") asks.</summary>
    private static bool Tagged(List<string> keys) =>
        Phrases.Tags.Any(tag => keys.Count > tag.Length && keys.Skip(keys.Count - tag.Length).SequenceEqual(tag));

    private static bool IsLaugh(string key) =>
        key.Length >= 4 && (key.All(c => c == 'ه') || key.StartsWith("haha", StringComparison.Ordinal) || key.StartsWith("hehe", StringComparison.Ordinal));

    /// <summary>How many of the phrases are said within the first <paramref name="within"/> words.</summary>
    private static int Count(List<string> keys, string[][] phrases, int within)
    {
        int found = 0;
        int limit = Math.Min(within, keys.Count);
        foreach (var phrase in phrases)
            for (int i = 0; i < limit; i++)
            {
                if (i + phrase.Length > keys.Count)
                    break;
                bool match = true;
                for (int k = 0; k < phrase.Length && match; k++)
                    match = keys[i + k] == phrase[k];
                if (match)
                {
                    found++;
                    break;
                }
            }
        return found;
    }
}
