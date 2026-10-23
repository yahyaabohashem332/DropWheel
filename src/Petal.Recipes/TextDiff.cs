using System.Text.RegularExpressions;

namespace Petal.Recipes;

public enum DiffKind
{
    Same,
    Added,
    Removed,
}

/// <summary>A run of text that is in both versions, only in the new one, or only in the old one.</summary>
public readonly record struct DiffPart(DiffKind Kind, string Text);

/// <summary>A paragraph of the comparison: unchanged, or the old and new words woven together.</summary>
public sealed record DiffParagraph(IReadOnlyList<DiffPart> Parts)
{
    public bool Changed => Parts.Any(p => p.Kind != DiffKind.Same);
}

/// <summary>
/// What changed between two texts, the way a reviewer reads it: paragraphs matched first, then, inside the
/// paragraphs that differ, word by word. Uses Myers' difference algorithm (the one behind "git diff").
/// </summary>
public static partial class TextDiff
{
    /// <summary>Above this many differences a pair is called "rewritten" rather than matched word by word.</summary>
    private const int MostEdits = 3000;

    public static List<DiffParagraph> Compare(string before, string after)
    {
        var a = Paragraphs(before);
        var b = Paragraphs(after);
        var result = new List<DiffParagraph>();
        var edits = Edits(a, b) ?? Replace(a.Count, b.Count);
        // Removed and added paragraphs next to each other are one changed passage, compared word by word.
        var removed = new List<string>();
        var added = new List<string>();
        void Flush()
        {
            if (removed.Count == 0 && added.Count == 0)
                return;
            result.AddRange(Words(removed, added));
            removed.Clear();
            added.Clear();
        }
        foreach (var (kind, i, j) in edits)
        {
            switch (kind)
            {
                case DiffKind.Same:
                    Flush();
                    result.Add(new DiffParagraph([new DiffPart(DiffKind.Same, a[i])]));
                    break;
                case DiffKind.Removed:
                    removed.Add(a[i]);
                    break;
                default:
                    added.Add(b[j]);
                    break;
            }
        }
        Flush();
        return result;
    }

    /// <summary>The words of changed paragraphs compared, one result paragraph for each new paragraph (or old one, when none is left).</summary>
    private static IEnumerable<DiffParagraph> Words(List<string> removed, List<string> added)
    {
        if (added.Count == 0)
            return removed.Select(p => new DiffParagraph([new DiffPart(DiffKind.Removed, p)]));
        if (removed.Count == 0)
            return added.Select(p => new DiffParagraph([new DiffPart(DiffKind.Added, p)]));
        // Paragraph breaks become a marker word so the result can be split again where the new text breaks.
        const string Break = "\u2029";
        var oldWords = Tokens(string.Join(Break, removed));
        var newWords = Tokens(string.Join(Break, added));
        var edits = Edits(oldWords, newWords);
        if (edits is null)
        {
            return removed.Select(p => new DiffParagraph([new DiffPart(DiffKind.Removed, p)]))
                .Concat(added.Select(p => new DiffParagraph([new DiffPart(DiffKind.Added, p)])));
        }
        var paragraphs = new List<DiffParagraph>();
        var parts = new List<DiffPart>();
        void Add(DiffKind kind, string token)
        {
            if (token == Break)
            {
                // A break in the new text ends a paragraph; a break only in the old text is dropped.
                if (kind == DiffKind.Removed)
                    return;
                paragraphs.Add(new DiffParagraph(Merge(parts)));
                parts = [];
                return;
            }
            parts.Add(new DiffPart(kind, token));
        }
        foreach (var (kind, i, j) in edits)
            Add(kind, kind == DiffKind.Added ? newWords[j] : oldWords[i]);
        if (parts.Count > 0)
            paragraphs.Add(new DiffParagraph(Merge(parts)));
        return paragraphs;
    }

    /// <summary>Neighbouring parts of the same kind as one, the space between two changed words kept with them.</summary>
    private static List<DiffPart> Merge(List<DiffPart> parts)
    {
        var merged = new List<DiffPart>();
        foreach (var part in parts)
        {
            if (merged.Count > 0 && merged[^1].Kind == part.Kind)
                merged[^1] = new DiffPart(part.Kind, merged[^1].Text + part.Text);
            else
                merged.Add(part);
        }
        return merged;
    }

    /// <summary>The text's paragraphs: its non-empty lines, trimmed, with inner spaces made single.</summary>
    internal static List<string> Paragraphs(string text) =>
        text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n')
            .Select(l => Spaces().Replace(l, " ").Trim())
            .Where(l => l.Length > 0)
            .ToList();

    /// <summary>Words with the space after each, so joining the tokens gives the text back.</summary>
    internal static List<string> Tokens(string text) => Token().Matches(text).Select(m => m.Value).ToList();

    private static List<(DiffKind Kind, int A, int B)> Replace(int a, int b) =>
        [.. Enumerable.Range(0, a).Select(i => (DiffKind.Removed, i, -1)), .. Enumerable.Range(0, b).Select(j => (DiffKind.Added, -1, j))];

    /// <summary>
    /// The shortest list of steps from <paramref name="a"/> to <paramref name="b"/>: Same (both indexes),
    /// Removed (an index in a) or Added (an index in b), in order. Null when they differ too much to be worth matching.
    /// Words compare without their trailing space.
    /// </summary>
    internal static List<(DiffKind Kind, int A, int B)>? Edits(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        int n = a.Count, m = b.Count;
        static string Key(string s) => s.TrimEnd();
        // The same start and end are kept aside: most edits are small and somewhere in the middle.
        int head = 0;
        while (head < n && head < m && Key(a[head]) == Key(b[head]))
            head++;
        int tail = 0;
        while (tail < n - head && tail < m - head && Key(a[n - 1 - tail]) == Key(b[m - 1 - tail]))
            tail++;
        var x0 = a.Skip(head).Take(n - head - tail).Select(Key).ToArray();
        var y0 = b.Skip(head).Take(m - head - tail).Select(Key).ToArray();
        var middle = Myers(x0, y0);
        if (middle is null)
            return null;
        var edits = new List<(DiffKind, int, int)>(n + m);
        for (int i = 0; i < head; i++)
            edits.Add((DiffKind.Same, i, i));
        foreach (var (kind, i, j) in middle)
            edits.Add((kind, i < 0 ? -1 : i + head, j < 0 ? -1 : j + head));
        for (int t = 0; t < tail; t++)
            edits.Add((DiffKind.Same, n - tail + t, m - tail + t));
        return edits;
    }

    private static List<(DiffKind, int, int)>? Myers(string[] a, string[] b)
    {
        int n = a.Length, m = b.Length;
        if (n == 0 || m == 0)
            return Replace(n, m);
        int max = n + m;
        int limit = Math.Min(max, MostEdits);
        // v[k] for k = -d..d is kept for each d, to walk back the path.
        var trace = new List<int[]>();
        var v = new int[2 * max + 3];
        int offset = max + 1;
        int found = -1;
        for (int d = 0; d <= limit && found < 0; d++)
        {
            var snapshot = new int[2 * d + 3];
            Array.Copy(v, offset - d - 1, snapshot, 0, 2 * d + 3);
            trace.Add(snapshot);
            for (int k = -d; k <= d; k += 2)
            {
                int x = k == -d || (k != d && v[offset + k - 1] < v[offset + k + 1]) ? v[offset + k + 1] : v[offset + k - 1] + 1;
                int y = x - k;
                while (x < n && y < m && a[x] == b[y])
                {
                    x++;
                    y++;
                }
                v[offset + k] = x;
                if (x >= n && y >= m)
                {
                    found = d;
                    break;
                }
            }
        }
        if (found < 0)
            return null;
        var path = new List<(DiffKind, int, int)>();
        int px = n, py = m;
        for (int d = found; d >= 0; d--)
        {
            var before = trace[d];
            int Get(int k) => before[k + d + 1];
            int k0 = px - py;
            int prevK = k0 == -d || (k0 != d && Get(k0 - 1) < Get(k0 + 1)) ? k0 + 1 : k0 - 1;
            int prevX = d == 0 ? 0 : Get(prevK);
            int prevY = d == 0 ? 0 : prevX - prevK;
            while (px > prevX && py > prevY)
            {
                path.Add((DiffKind.Same, px - 1, py - 1));
                px--;
                py--;
            }
            if (d > 0)
            {
                if (px == prevX)
                    path.Add((DiffKind.Added, -1, py - 1));
                else
                    path.Add((DiffKind.Removed, px - 1, -1));
            }
            px = prevX;
            py = prevY;
        }
        path.Reverse();
        return path;
    }

    [GeneratedRegex(@"[ \t\u00A0]+")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"\u2029|[^\s\u2029]+[^\S\u2029]*|[^\S\u2029]+")]
    private static partial Regex Token();
}
