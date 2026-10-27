namespace Petal.Engines.Documents.Ocr;

/// <summary>
/// The words Windows reads off a picture, in the order people read them. Windows gives a line's words from left to
/// right whatever the language, so an Arabic line comes out back to front, and it may cut one long line into pieces.
/// Here the pieces of a line are joined again, and each line is put in its reading order: an Arabic line from the right,
/// with any English words and numbers in it kept left to right, as the screen shows them; an English line from the
/// left, with any Arabic phrase in it from the right.
/// </summary>
public static class ReadingOrder
{
    /// <summary>The lines, top to bottom (side by side ones in the direction most of the text reads), each in reading order.</summary>
    public static IReadOnlyList<IReadOnlyList<PictureWord>> Lines(IReadOnlyList<PictureWord> words)
    {
        if (words.Count == 0)
            return [];
        // Windows' own lines, joined where it cut one line in two: on the same row and no more than a gap between words apart.
        var pieces = words.GroupBy(w => w.Line).Select(g => g.OrderBy(w => w.X).ToList()).OrderBy(p => Middle(p)).ToList();
        var lines = new List<List<PictureWord>>();
        foreach (var piece in pieces)
        {
            var home = lines.FirstOrDefault(line => SameRow(line, piece) && Gap(line, piece) < 1.2 * Math.Min(Height(line), Height(piece)));
            if (home is null)
                lines.Add(piece);
            else
            {
                home.AddRange(piece);
                home.Sort((a, b) => a.X.CompareTo(b.X));
            }
        }
        bool rtl = lines.Sum(l => l.Sum(w => Arabic(w.Text))) > lines.Sum(l => l.Sum(w => Latin(w.Text)));
        // Rows of lines, top to bottom; across a row from the side the text starts on.
        var ordered = new List<IReadOnlyList<PictureWord>>();
        var byMiddle = lines.OrderBy(Middle).ToList();
        int i = 0;
        while (i < byMiddle.Count)
        {
            var row = new List<List<PictureWord>> { byMiddle[i] };
            int j = i + 1;
            while (j < byMiddle.Count && SameRow(byMiddle[i], byMiddle[j]))
                row.Add(byMiddle[j++]);
            foreach (var line in rtl ? row.OrderByDescending(l => l.Max(w => w.Right)) : row.OrderBy(l => l.Min(w => w.X)))
                ordered.Add(Order(line, rtl));
            i = j;
        }
        return ordered;
    }

    /// <summary>The text of the words as it reads: a line per line.</summary>
    public static string Text(IReadOnlyList<PictureWord> words) => string.Join(Environment.NewLine, Lines(words).Select(l => string.Join(" ", l.Select(w => w.Text))));

    /// <summary>
    /// One line's words (left to right as on the screen) in reading order. In an Arabic line the words go from the right,
    /// and a run of words that aren't Arabic (English, numbers, a time) stays left to right; in an English line it is the
    /// other way round.
    /// </summary>
    internal static IReadOnlyList<PictureWord> Order(IReadOnlyList<PictureWord> visual, bool rightToLeftAround = false)
    {
        int arabic = visual.Sum(w => Arabic(w.Text)), latin = visual.Sum(w => Latin(w.Text));
        // Clearly one or the other by its letters; when close, an Arabic line starts on the right (its right-most word is
        // Arabic), and a line among Arabic ones is Arabic unless it is mostly English.
        bool rtl = arabic > 0 && (latin == 0 || arabic > 2 * latin
            || (arabic >= latin && Arabic(visual[^1].Text) > 0)
            || (rightToLeftAround && 2 * arabic >= latin));
        var result = new List<PictureWord>(visual.Count);
        // Walk from where the line starts; a run of the other direction's words is kept as the screen shows it.
        var sequence = rtl ? visual.Reverse().ToList() : visual.ToList();
        int i = 0;
        while (i < sequence.Count)
        {
            if (Belongs(sequence[i], rtl))
            {
                result.Add(sequence[i++]);
                continue;
            }
            int j = i;
            while (j < sequence.Count && !Belongs(sequence[j], rtl))
                j++;
            var run = sequence.GetRange(i, j - i);
            run.Reverse();
            result.AddRange(run);
            i = j;
        }
        return result;
    }

    /// <summary>Whether a word reads in the line's own direction: Arabic in an Arabic line; anything but Arabic in an English one.</summary>
    private static bool Belongs(PictureWord w, bool rtl)
    {
        int a = Arabic(w.Text), l = Latin(w.Text);
        // Marks alone (a dash, a dot) go with the line.
        if (a == 0 && l == 0)
            return true;
        return rtl ? a >= l : a == 0;
    }

    internal static int Arabic(string text) => text.Count(c => c is >= (char)0x0600 and <= (char)0x06FF or >= (char)0x0750 and <= (char)0x077F or >= (char)0x08A0 and <= (char)0x08FF or >= (char)0xFB50 and <= (char)0xFDFF or >= (char)0xFE70 and <= (char)0xFEFF);

    internal static int Latin(string text) => text.Count(c => char.IsAsciiLetterOrDigit(c) || c is >= (char)0x00C0 and <= (char)0x024F);

    private static double Middle(List<PictureWord> line) => line.Average(w => w.Y + w.Height / 2);

    private static double Height(List<PictureWord> line) => Math.Max(1, line.Average(w => w.Height));

    private static bool SameRow(List<PictureWord> a, List<PictureWord> b) => Math.Abs(Middle(a) - Middle(b)) < Math.Min(Height(a), Height(b)) / 2;

    /// <summary>The space between two pieces across, 0 when they overlap.</summary>
    private static double Gap(List<PictureWord> a, List<PictureWord> b)
    {
        double aLeft = a.Min(w => w.X), aRight = a.Max(w => w.Right), bLeft = b.Min(w => w.X), bRight = b.Max(w => w.Right);
        return Math.Max(0, Math.Max(bLeft - aRight, aLeft - bRight));
    }
}
