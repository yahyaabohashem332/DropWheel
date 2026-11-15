using System.Windows.Media;

namespace Petal.App.Words;

/// <summary>
/// The colour of each word of a caption line, and how it got there. A line is written again many times a second (each
/// time the talk is heard again or its translation grows), and a word that is the same as before keeps its colour, or
/// goes over to its new one gently; a new word comes in out of nothing; nothing on the line changes at a stroke.
/// Words are matched by the longest run they have in common, so a proper translation that takes the place of the
/// quick one changes only the words that differ.
/// </summary>
internal sealed class CaptionShades
{
    /// <summary>How long a word takes to come in, or to go over to a new colour.</summary>
    public static readonly TimeSpan Fade = TimeSpan.FromMilliseconds(320);

    /// <summary>A word, the colour it is going to, the one it comes from, and since when.</summary>
    public readonly record struct Shade(string Word, Color To, Color From, TimeSpan At)
    {
        /// <summary>Whether it is at its colour by then.</summary>
        public bool Still(TimeSpan now) => now - At >= Fade || From == To;

        /// <summary>Its colour at that time: eased out, quick at first and gentle at the end.</summary>
        public Color ColorAt(TimeSpan now)
        {
            if (Still(now))
                return To;
            double p = Math.Clamp((now - At).TotalMilliseconds / Fade.TotalMilliseconds, 0, 1);
            double e = 1 - Math.Pow(1 - p, 3);
            static byte Mix(byte a, byte b, double e) => (byte)Math.Round(a + (b - a) * e);
            return Color.FromArgb(Mix(From.A, To.A, e), Mix(From.R, To.R, e), Mix(From.G, To.G, e), Mix(From.B, To.B, e));
        }
    }

    private List<Shade> _words = [];

    /// <summary>The words as they stand now.</summary>
    public IReadOnlyList<Shade> Words => _words;

    /// <summary>
    /// Whether words change at a stroke: for pictures of the window, which are taken at once and would catch words
    /// still on their way in.
    /// </summary>
    public bool Still { get; set; }

    /// <summary>
    /// The words the line holds now, each with the colour it should have. A word that was there keeps its colour, or
    /// goes from where it stood to its new one; a new word comes in from <paramref name="from"/> when that has it (a
    /// sentence moved up from the second line keeps the colours it had there), and from nothing otherwise.
    /// </summary>
    public IReadOnlyList<Shade> Update(IReadOnlyList<(string Word, Color To)> words, TimeSpan now, CaptionShades? from = null)
    {
        var matched = Match(_words, words);
        var seeds = from is null ? null : Match(from._words, words);
        var next = new List<Shade>(words.Count);
        for (int i = 0; i < words.Count; i++)
        {
            var (word, to) = words[i];
            if (Still)
                next.Add(new Shade(word, to, to, now));
            else if (matched[i] is int old)
            {
                var was = _words[old];
                next.Add(was.To == to ? was with { Word = word } : new Shade(word, to, was.ColorAt(now), now));
            }
            else if (seeds?[i] is int seed)
                next.Add(new Shade(word, to, from!._words[seed].ColorAt(now), now));
            else
                next.Add(new Shade(word, to, Color.FromArgb(0, to.R, to.G, to.B), now));
        }
        _words = next;
        return next;
    }

    /// <summary>Forgets every word: what comes next comes in out of nothing.</summary>
    public void Clear() => _words = [];

    /// <summary>
    /// For each new word, the old word it is (by the longest run of words the two lines have in common, in order),
    /// or null for a word that is new.
    /// </summary>
    internal static int?[] Match(IReadOnlyList<Shade> old, IReadOnlyList<(string Word, Color To)> words)
    {
        int n = old.Count, m = words.Count;
        var result = new int?[m];
        if (n == 0 || m == 0)
            return result;
        var run = new int[n + 1, m + 1];
        for (int i = n - 1; i >= 0; i--)
        {
            for (int j = m - 1; j >= 0; j--)
                run[i, j] = Same(old[i].Word, words[j].Word) ? run[i + 1, j + 1] + 1 : Math.Max(run[i + 1, j], run[i, j + 1]);
        }
        for (int i = 0, j = 0; i < n && j < m;)
        {
            if (Same(old[i].Word, words[j].Word))
                result[j++] = i++;
            else if (run[i + 1, j] >= run[i, j + 1])
                i++;
            else
                j++;
        }
        return result;
    }

    // A word that only gained or lost its full stop or comma is the same word: the sentence ending put it there.
    private static bool Same(string a, string b) => a == b || Bare(a) == Bare(b);

    private static string Bare(string word) => word.TrimEnd('.', ',', '،', '؟', '?', '!', '؛', ';', ':', '۔');
}
