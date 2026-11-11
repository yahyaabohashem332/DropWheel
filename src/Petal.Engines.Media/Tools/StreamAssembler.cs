namespace Petal.Engines.Media;

/// <summary>
/// Puts the passes of a near-live speech model together into sentences and a draft. The sound held always starts at
/// the sentence being said, so each pass reads that sentence whole, from its first word: its latest reading, which
/// has heard the most, is what is shown, and the words the last two readings agree on are the settled ones. A word
/// heard wrongly at first ("quip" for "equipped", when only half of it had been said) is put right by the readings
/// that follow, and never stands twice. A sentence ends at its full stop, once two readings have put it there, or
/// where the speaker pauses; then its words are final and its sound is let go.
/// Nothing here listens or waits: it is given words and times, so its rules can be tested without a model.
/// </summary>
internal sealed class StreamAssembler
{
    // Words of finished sentences whose sound is still held: the next pass hears them once more and shows where to cut.
    private readonly List<StreamWord> _ended = [];
    // The latest reading of the sentence being said, and how many of its words are settled.
    private List<StreamWord> _reading = [];
    private int _settled;
    // The last few final words and when the last one ended: what a pass may hear again at its start, after a cut.
    private readonly List<string> _lastKeys = [];
    private double _lastEnd = double.NegativeInfinity;
    // What was said before the sound still held, for the speech model to carry on from.
    private string _said = "";
    private string _previous = "";
    private int _finished;
    private int _blank;
    // How many words have been final since one ended with a full stop: talk that Whisper writes without any (Urdu, much
    // Arabic) has to be parted where the speaker breathes, and talk that has them mustn't be.
    private int _sinceStop;
    // How many of the finished words the cut now wanted is for (sentences that end later in the same pass wait their turn).
    private int _cutWords;

    /// <summary>A sentence became final.</summary>
    public event Action<StreamSentence>? Sentence;

    /// <summary>The language being heard, written into each sentence.</summary>
    public string Language { get; set; } = "";

    /// <summary>Words said this many seconds ago are no longer waited for: they are settled as the latest pass heard them.</summary>
    public double Patience { get; init; } = 3.5;

    /// <summary>
    /// Where the held sound can be cut, in seconds of the stream: between a finished sentence's last word and the
    /// next. The quietest moment in it is the place. Null when no sentence is waiting to be let go.
    /// </summary>
    public (double From, double To)? Cut { get; private set; }

    /// <summary>Whether words are waiting for their sentence to end.</summary>
    public bool Waiting => _reading.Count > 0;

    /// <summary>Whether words heard already still have their sound held (the next pass hears them again).</summary>
    public bool Holding => _ended.Count > 0 || _reading.Count > 0;

    /// <summary>The end of the last word heard; -1 when nothing waits.</summary>
    public double Newest => _reading.Count > 0 ? _reading[^1].End : -1;

    /// <summary>What was said before the held sound: the speech model reads it first, so spelling and names carry on.</summary>
    public string Before
    {
        get
        {
            if (_said.Length <= 200)
                return _said;
            var tail = _said[^200..];
            int space = tail.IndexOf(' ');
            return space >= 0 ? tail[(space + 1)..] : tail;
        }
    }

    /// <summary>
    /// What the speech model is told came before sound that starts at a point of the stream (seconds): nothing when
    /// the last words were said a while before that. Given words from long ago and a moment of music, Whisper reads
    /// those words back as if they had been said again.
    /// </summary>
    public string Context(double from) => from - _lastEnd > 8 ? "" : Before;

    /// <summary>
    /// The talk went over to another language: what was said before isn't told to the speech model any more. Whisper
    /// writes in the language of what it is told came before, whatever language it is asked for.
    /// </summary>
    public void Forget() => _said = "";

    public StreamState State() =>
        new(_previous, string.Join(' ', _reading.Take(_settled).Select(w => w.Text)), string.Join(' ', _reading.Skip(_settled).Select(w => w.Text)), Language, Newest, _finished + 1);

    /// <summary>
    /// A pass's words, at their times in the stream. <paramref name="closing"/> when the speaker has stopped and the pass
    /// heard all of it: then nothing is waited for. <paramref name="now"/> is where the pass's sound ended.
    /// <paramref name="pauses"/> are the moments nobody spoke within the pass's sound, however short: where a breath was
    /// taken is where talk without full stops (Whisper writes none for Urdu, and few for Arabic) is parted into sentences.
    /// </summary>
    public void Take(List<StreamWord> heard, double now, bool closing, IReadOnlyList<(double Start, double End)>? pauses = null)
    {
        Cut = null;
        var open = Open(heard);
        // A pass that hears nothing where the last heard words: once is a slip, and what was shown stays.
        if (open.Count == 0 && _reading.Count > 0 && !closing && ++_blank < 2)
            return;
        _blank = 0;
        var match = Match(_reading, open);
        int settled = 0;
        if (closing)
        {
            settled = open.Count;
        }
        else
        {
            // The last word heard while the talk goes on is never settled: at the edge of the sound it is often half a
            // word. Words the two readings disagree on are waited for only so long, or they would hold up all that follow.
            while (settled < open.Count - 1 && (match[settled] >= 0 || open[settled].End < now - Patience))
                settled++;
        }
        // The quiet after a word, in seconds: the longest pause that falls between it and the next.
        double Breath(int i)
        {
            double from = open[i].Start + 0.05, to = (i + 1 < open.Count ? open[i + 1].Start : open[i].End) + 0.12, longest = 0;
            foreach (var pause in pauses ?? [])
            {
                double middle = (pause.Start + pause.End) / 2;
                if (middle > from && middle < to)
                    longest = Math.Max(longest, pause.End - pause.Start);
            }
            return longest;
        }
        int begin = 0;
        for (int i = 0; i < open.Count; i++)
        {
            int length = i - begin + 1;
            bool within = i < settled;
            bool last = closing && i == open.Count - 1;
            // A full stop at the edge of the sound is often not one. It ends the sentence when the reading before put it
            // there too, or the words are old enough; or when three words or more already follow it in this reading:
            // then it isn't at the edge, and on a slow PC, whose passes come seconds apart, a reading isn't waited for.
            bool stop = EndsSentence(open[i].Text, i + 1 < open.Count ? open[i + 1].Text : null)
                && (closing || open.Count - 1 - i >= 3 || within && (match[i] >= 0 && Stops(_reading[match[i]].Text) || open[i].End < now - Patience));
            // Long talk without a full stop still makes sentences: at a comma, or where the speaker took a breath. In
            // talk that does have full stops a breath is only a breath, until the sentence has grown long.
            bool unstopped = _sinceStop + length > 40;
            if (last || stop || within && (length >= 16 && EndsClause(open[i].Text) || length >= (unstopped ? 12 : 24) && Breath(i) >= 0.4))
            {
                End(open.GetRange(begin, length));
                begin = i + 1;
            }
            else if (within && length >= 30)
            {
                // Thirty words and no place to stop: parted where the speaker paused longest in its second half, however briefly.
                int part = i;
                double longest = 0.1;
                for (int k = begin + 14; k < i; k++)
                {
                    double breath = Breath(k);
                    if (breath > longest)
                    {
                        longest = breath;
                        part = k;
                    }
                }
                End(open.GetRange(begin, part - begin + 1));
                begin = part + 1;
            }
        }
        if (begin > 0 && !closing && open.Count - begin >= 2)
        {
            // A sentence ended and two words or more of the next are heard already: where it stopped is known well
            // enough now, and its sound can go at once. (With one word after it, that word is at the edge of the sound
            // and its time may be out: then the next pass says where.) On a slow PC a pass saved is seconds.
            double boundary = open[begin].Start;
            Cut = (Math.Max(Math.Min(open[begin - 1].Start, boundary) + 0.05, boundary - 0.25), boundary + 0.02);
            _cutWords = _ended.Count;
        }
        _reading = open.GetRange(begin, open.Count - begin);
        _settled = Math.Max(0, settled - begin);
    }

    /// <summary>All that is waiting becomes final: it went quiet, or the sound stopped.</summary>
    public void FinishAll()
    {
        Cut = null;
        var waiting = _reading;
        _reading = [];
        _settled = 0;
        _blank = 0;
        int begin = 0;
        for (int i = 0; i < waiting.Count; i++)
        {
            if (i == waiting.Count - 1 || EndsSentence(waiting[i].Text, waiting[i + 1].Text))
            {
                End(waiting.GetRange(begin, i - begin + 1));
                begin = i + 1;
            }
        }
    }

    /// <summary>
    /// The held sound grew too long without a sentence ending: the reading becomes a sentence but for its last word, so
    /// the sound before that can go after the next pass.
    /// </summary>
    public void Force()
    {
        if (_reading.Count < 2)
            return;
        // At its last comma if there is one in its second half: a better place to part a sentence than anywhere.
        int end = _reading.Count - 2;
        for (int i = end; i >= _reading.Count / 2; i--)
        {
            if (EndsClause(_reading[i].Text))
            {
                end = i;
                break;
            }
        }
        End(_reading.GetRange(0, end + 1));
        _reading = _reading.GetRange(end + 1, _reading.Count - end - 1);
        _settled = 0;
    }

    /// <summary>
    /// The sound before a point of the stream (seconds) was let go. <paramref name="atCut"/> when it was cut where
    /// <see cref="Cut"/> said: the finished sentences that cut was for have gone. Otherwise every finished word begun
    /// before that point has.
    /// </summary>
    public void Dropped(double time, bool atCut = false)
    {
        int gone = 0;
        if (atCut)
        {
            gone = Math.Min(_cutWords, _ended.Count);
        }
        else
        {
            while (gone < _ended.Count && _ended[gone].Start < time)
                gone++;
        }
        _cutWords = 0;
        Cut = null;
        if (gone > 0)
        {
            _said = (_said + " " + string.Join(' ', _ended.Take(gone).Select(w => w.Text))).Trim();
            if (_said.Length > 400)
                _said = _said[^400..];
            _ended.RemoveRange(0, gone);
        }
        // Nothing finished is held any more: the sound now starts at the cut, and no finished word reaches past that.
        if (_ended.Count == 0)
            _lastEnd = Math.Min(_lastEnd, time);
    }

    /// <summary>
    /// The words of a pass that belong to the sentence being said. The words of sentences that ended a pass ago are
    /// still in its sound: they are matched against its start, allowing for a word heard differently, missed or added
    /// this time, and what follows them is the sentence being said. Now that the pass has heard what follows, it is
    /// known well enough where the finished sentence stopped: that is where the sound can be cut.
    /// </summary>
    internal List<StreamWord> Open(List<StreamWord> heard)
    {
        if (_ended.Count == 0)
        {
            var all = new List<StreamWord>(heard);
            // The last final words once more, squeezed in at the very start: the end of a word from before the cut heard
            // as a word, or the model reading back what it was told came before.
            for (int n = Math.Min(4, Math.Min(_lastKeys.Count, all.Count)); n >= 1; n--)
            {
                bool again = n == 1 ? all[0].Start < _lastEnd + 0.1 && all[0].End < _lastEnd + 0.25 : all[n - 1].End < _lastEnd + 0.6;
                for (int i = 0; i < n && again; i++)
                    again = all[i].Key == _lastKeys[_lastKeys.Count - n + i];
                if (again)
                {
                    all.RemoveRange(0, n);
                    break;
                }
            }
            return all;
        }
        int from = Resume(_ended, heard);
        double boundary = from < heard.Count ? heard[from].Start : from > 0 ? heard[from - 1].End : _ended[^1].End;
        double lastStart = from > 0 ? Math.Min(heard[from - 1].Start, boundary) : boundary - 0.3;
        Cut = (Math.Max(lastStart + 0.05, boundary - 0.25), boundary + 0.02);
        _cutWords = _ended.Count;
        _lastEnd = boundary;
        return heard.GetRange(from, heard.Count - from);
    }

    /// <summary>
    /// Where the sentence being said begins in a pass that heard finished words again before it: the place that makes
    /// the pass's start most like those words (a word changed, missed or added counts one), and of places as good, the
    /// one whose next word begins nearest to where the last finished word ended.
    /// </summary>
    internal static int Resume(IReadOnlyList<StreamWord> ended, IReadOnlyList<StreamWord> heard)
    {
        int n = ended.Count, m = heard.Count;
        if (m == 0)
            return 0;
        double end = ended[^1].End;
        var d = Distances(ended, heard);
        int best = 0;
        double least = double.MaxValue;
        for (int j = 0; j <= m; j++)
        {
            double starts = j < m ? heard[j].Start : heard[m - 1].End;
            double cost = d[n, j] + Math.Min(0.9, Math.Abs(starts - end) * 1.2);
            if (cost < least)
            {
                least = cost;
                best = j;
            }
        }
        // Nothing like the finished words: the pass heard something else altogether (or made it up). By time alone then.
        if (d[n, best] > Math.Max(2, n * 0.6))
        {
            best = 0;
            while (best < m && (heard[best].Start + heard[best].End) / 2 < end)
                best++;
        }
        return best;
    }

    /// <summary>
    /// For each word of a reading, the word of the reading before that it is (the same word, at its place in the
    /// sentence), or -1 when the two readings differ there.
    /// </summary>
    internal static int[] Match(IReadOnlyList<StreamWord> before, IReadOnlyList<StreamWord> now)
    {
        int n = before.Count, m = now.Count;
        var match = new int[m];
        Array.Fill(match, -1);
        if (n == 0 || m == 0)
            return match;
        var d = Distances(before, now);
        for (int i = n, j = m; i > 0 && j > 0;)
        {
            bool same = before[i - 1].Key == now[j - 1].Key;
            if (d[i, j] == d[i - 1, j - 1] + (same ? 0 : 1))
            {
                if (same && now[j - 1].Key.Length > 0)
                    match[j - 1] = i - 1;
                i--;
                j--;
            }
            else if (d[i, j] == d[i - 1, j] + 1)
            {
                i--;
            }
            else
            {
                j--;
            }
        }
        return match;
    }

    /// <summary>How many words must change, go or come to turn the first i of one list into the first j of another.</summary>
    private static int[,] Distances(IReadOnlyList<StreamWord> a, IReadOnlyList<StreamWord> b)
    {
        int n = a.Count, m = b.Count;
        var d = new int[n + 1, m + 1];
        for (int j = 0; j <= m; j++)
            d[0, j] = j;
        for (int i = 1; i <= n; i++)
        {
            d[i, 0] = i;
            for (int j = 1; j <= m; j++)
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (a[i - 1].Key == b[j - 1].Key ? 0 : 1));
        }
        return d;
    }

    private void End(List<StreamWord> words)
    {
        if (words.Count == 0)
            return;
        var text = string.Join(' ', words.Select(w => w.Text));
        _previous = text;
        // Its sound isn't needed again. Where exactly it stops is clearer to the next pass, which hears what follows:
        // the cut waits for that.
        _ended.AddRange(words);
        foreach (var word in words)
        {
            _lastKeys.Add(word.Key);
            _sinceStop = Stops(word.Text) ? 0 : _sinceStop + 1;
        }
        if (_lastKeys.Count > 6)
            _lastKeys.RemoveRange(0, _lastKeys.Count - 6);
        _lastEnd = words[^1].End;
        Sentence?.Invoke(new StreamSentence(text, Language, words[0].Start, words[^1].End, ++_finished));
    }

    private static readonly HashSet<string> Short = new(StringComparer.OrdinalIgnoreCase)
    {
        "mr", "mrs", "ms", "dr", "prof", "sr", "jr", "st", "vs", "etc", "no", "vol", "fig", "approx", "inc", "ltd", "co", "gen", "col", "lt", "sgt", "capt",
        "mme", "mlle", "av", "env", "sra", "srta", "ud", "uds", "pág", "núm", "bkz", "vb", "yy", "hz", "sn", "dll", "dsb", "dst", "hlm", "tgl",
    };

    private static readonly char[] Closing = ['"', '\'', '”', '’', '»', ')', ']', '›'];

    /// <summary>Whether a word carries a mark that ends sentences, whatever follows it.</summary>
    internal static bool Stops(string word)
    {
        var core = word.TrimEnd(Closing);
        return core.Length > 0 && core[^1] is '.' or '?' or '!' or '؟' or '。' or '۔' or '！' or '？' && !core.EndsWith("..", StringComparison.Ordinal);
    }

    /// <summary>
    /// Whether a word ends its sentence, knowing the word after it when there is one: "U.S. imperialism" and "Dr. Smith"
    /// go on, and so does a full stop followed by a small letter.
    /// </summary>
    internal static bool EndsSentence(string word, string? next)
    {
        if (!Stops(word))
            return false;
        var core = word.TrimEnd(Closing);
        if (core[^1] != '.')
            return true;
        var body = core.TrimEnd('.').TrimStart('"', '\'', '“', '‘', '«', '(', '[');
        if (body.Length == 0)
            return false;
        if (next is not null)
        {
            // An initial, letters with full stops between them, a number as in "3. Bölüm", or a known short form.
            if (body.Length == 1 && char.IsLetter(body[0]) || body.Contains('.') && body.Length <= 6 || body.All(char.IsDigit) || Short.Contains(body))
                return false;
            var first = next.TrimStart('"', '\'', '“', '‘', '«', '(', '[', '¿', '¡');
            if (first.Length > 0 && char.IsLower(first[0]))
                return false;
        }
        return true;
    }

    private static bool EndsClause(string word)
    {
        var core = word.TrimEnd(Closing);
        return core.Length > 0 && core[^1] is ',' or '،' or ';' or '؛' or ':' or '、' or '，';
    }
}
