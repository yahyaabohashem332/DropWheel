using System.Text;
using Whisper.net;

namespace Petal.Engines.Media;

/// <summary>A segment's words with their own times, from Whisper's tokens.</summary>
internal static class TokenWords
{
    /// <summary>
    /// A segment's tokens joined into words (a token starting with a space starts a word), at their times in the sound
    /// Whisper was given: a word runs from its first token's time (aligned to the sound when <paramref name="aligned"/>)
    /// to the next word's, the segment's end for the last.
    /// </summary>
    public static List<(string Text, double Start, double End)> Of(SegmentData segment, bool aligned)
    {
        var spans = new List<(string Text, double Start, double End)>();
        var text = new StringBuilder();
        double start = -1, end = 0;
        foreach (var token in segment.Tokens)
        {
            var t = token.Text;
            if (string.IsNullOrEmpty(t) || t.StartsWith("[_") || t.StartsWith("<|"))
                continue;
            double at = aligned && token.DtwTimestamp > 0 ? token.DtwTimestamp / 100.0 : token.Start / 100.0;
            if (t.StartsWith(' ') && text.Length > 0)
            {
                spans.Add((text.ToString().Trim(), start, end));
                text.Clear();
                start = -1;
            }
            if (start < 0)
                start = at;
            end = Math.Max(Math.Max(at, token.End / 100.0), start);
            text.Append(t);
        }
        if (text.ToString().Trim().Length > 0)
            spans.Add((text.ToString().Trim(), Math.Max(0, start), Math.Max(end, segment.End.TotalSeconds)));
        var words = new List<(string, double, double)>(spans.Count);
        for (int i = 0; i < spans.Count; i++)
        {
            double wordEnd = i + 1 < spans.Count ? Math.Max(spans[i].Start, Math.Min(spans[i].End, spans[i + 1].Start)) : spans[i].End;
            words.Add((spans[i].Text, spans[i].Start, wordEnd));
        }
        return words;
    }

    /// <summary>
    /// The words as the segment's text spells them, at the tokens' times. A letter can be split between two tokens
    /// (Arabic letters take two bytes), and such a token alone reads as "�"; the segment's text is always whole.
    /// When the counts agree each word takes its token word's times; otherwise a word takes the time at its place
    /// in the text, measured in letters along the token words.
    /// </summary>
    public static List<SpokenWord> Spelled(string segmentText, IReadOnlyList<(string Text, double Start, double End)> timed)
    {
        var spelled = segmentText.Split((char[])[' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (spelled.Length == 0 || timed.Count == 0)
            return [];
        if (spelled.Length == timed.Count)
            return spelled.Select((w, i) => new SpokenWord(w, timed[i].Start, timed[i].End)).ToList();
        // Where each token word starts and ends along the text, as a share of its letters.
        double total = timed.Sum(t => Math.Max(1, t.Text.Length) + 1.0);
        var marks = new List<(double From, double To, double Start, double End)>(timed.Count);
        double at = 0;
        foreach (var t in timed)
        {
            double size = Math.Max(1, t.Text.Length) + 1.0;
            marks.Add((at / total, (at + size) / total, t.Start, t.End));
            at += size;
        }
        double TimeAt(double share)
        {
            foreach (var m in marks)
                if (share < m.To || m == marks[^1])
                    return m.Start + (m.End - m.Start) * Math.Clamp((share - m.From) / Math.Max(1e-9, m.To - m.From), 0, 1);
            return marks[^1].End;
        }
        double letters = spelled.Sum(w => w.Length + 1.0);
        var words = new List<SpokenWord>(spelled.Length);
        double place = 0;
        foreach (var w in spelled)
        {
            double size = w.Length + 1.0;
            words.Add(new SpokenWord(w, TimeAt(place / letters), TimeAt((place + w.Length) / letters)));
            place += size;
        }
        return words;
    }
}
