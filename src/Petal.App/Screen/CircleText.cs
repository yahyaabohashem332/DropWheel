using System.Windows;
using Petal.Core.Text;
using Petal.Engines.Documents.Ocr;

namespace Petal.App.Screen;

/// <summary>A line of text read from the screen: its words in reading order, the box around them, and its direction.</summary>
internal sealed record ScreenLine(IReadOnlyList<PictureWord> Words, Rect Box, bool RightToLeft)
{
    public string Text => string.Join(" ", Words.Select(w => w.Text));
}

/// <summary>Lines that read as one: a paragraph, a caption, a button's label.</summary>
internal sealed record ScreenBlock(IReadOnlyList<ScreenLine> Lines, Rect Box)
{
    public bool RightToLeft => Lines.Count(l => l.RightToLeft) * 2 > Lines.Count;

    public IEnumerable<PictureWord> Words => Lines.SelectMany(l => l.Words);

    /// <summary>The block's lines joined where the screen broke them, so a paragraph reads as one.</summary>
    public string Text => ScreenTextWindow.JoinLines(string.Join("\n", Lines.Select(l => l.Text)));
}

/// <summary>What was circled: the area, and the words of the screen it takes in.</summary>
internal sealed record CircleArea(Int32Rect Area, IReadOnlyList<PictureWord> Words, bool Closed);

/// <summary>
/// Turning the words Windows reads on a screen into lines and paragraphs, and a circle, a scribble, a stroke along a line
/// or a click into what was meant: the words it takes in and the area around them. All in the pixels of the still.
/// </summary>
internal static class CircleText
{
    /// <summary>
    /// The words as lines, in reading order: top to bottom, and across a row of lines (columns, cells) from the left, or
    /// from the right when most of the text is Arabic.
    /// </summary>
    public static IReadOnlyList<ScreenLine> Lines(IEnumerable<PictureWord> words) =>
        ReadingOrder.Lines(words.ToList())
            .Select(line => new ScreenLine(line, Union(line.ToList()), KeyboardLayout.LooksArabic(string.Concat(line.Select(w => w.Text)))))
            .ToList();

    /// <summary>
    /// Lines that belong together: one under the other with no more than about a line's height between them, of a similar
    /// size, and overlapping across. Columns side by side stay apart.
    /// </summary>
    public static IReadOnlyList<ScreenBlock> Blocks(IReadOnlyList<ScreenLine> lines)
    {
        var blocks = new List<List<ScreenLine>>();
        foreach (var line in lines.OrderBy(l => l.Box.Top))
        {
            List<ScreenLine>? home = null;
            foreach (var block in blocks)
            {
                var last = block[^1];
                double height = Math.Max(1, Math.Min(last.Box.Height, line.Box.Height));
                double gap = line.Box.Top - last.Box.Bottom;
                double overlap = Math.Min(last.Box.Right, line.Box.Right) - Math.Max(last.Box.Left, line.Box.Left);
                double narrower = Math.Max(1, Math.Min(last.Box.Width, line.Box.Width));
                double ratio = Math.Max(last.Box.Height, line.Box.Height) / height;
                if (gap > -height * 0.3 && gap < height * 1.1 && overlap > narrower * 0.25 && ratio < 1.5)
                {
                    home = block;
                    break;
                }
            }
            if (home is null)
                blocks.Add([line]);
            else
                home.Add(line);
        }
        return blocks.Select(b => new ScreenBlock(b, Union(b.SelectMany(l => l.Words).ToList()))).ToList();
    }

    /// <summary>The text of words as it reads: lines in order, one per line.</summary>
    public static string Text(IEnumerable<PictureWord> words) => string.Join("\n", Lines(words).Select(l => l.Text));

    /// <summary>
    /// What a stroke meant. A closed loop takes in the words inside it and the area it drew round; a stroke that doesn't
    /// close (along a line, through a word, a scribble) takes the words it passed over, and the area around them. A
    /// stroke over no words takes the area around it, for a picture.
    /// </summary>
    public static CircleArea Select(IReadOnlyList<Point> stroke, IReadOnlyList<PictureWord> words, int width, int height)
    {
        var bounds = Bounds(stroke);
        bool closed = Encloses(stroke);
        var picked = words.Where(w => Takes(stroke, closed, w)).ToList();
        Rect area;
        if (closed)
            area = Rect.Union(bounds, picked.Count > 0 ? Union(picked) : bounds);
        else if (picked.Count > 0)
            area = Union(picked);
        else
            area = bounds;
        double pad = closed ? 4 : picked.Count > 0 ? 6 : 24;
        area.Inflate(pad, pad);
        return new CircleArea(Clamp(area, width, height), picked, closed);
    }

    /// <summary>
    /// Whether a stroke takes a word. A loop takes what it holds, and what it cuts through the middle of: drawn snugly
    /// round a line, it grazes the lines above and below without taking them. A line along the text takes what it passes
    /// over or just under.
    /// </summary>
    public static bool Takes(IReadOnlyList<Point> stroke, bool closed, PictureWord w)
    {
        var box = Box(w);
        if (!closed)
            return Touches(stroke, box, Math.Max(3, w.Height * 0.3));
        if (Contains(stroke, new Point(w.CentreX, w.CentreY)))
            return true;
        box.Inflate(-Math.Min(box.Width, box.Height) * 0.25, -box.Height * 0.25);
        return !box.IsEmpty && Touches(stroke, box, 0);
    }

    /// <summary>A click: the paragraph under it (or a line within a few pixels), or null when it fell on no text.</summary>
    public static ScreenBlock? BlockAt(Point p, IReadOnlyList<ScreenBlock> blocks)
    {
        foreach (var block in blocks.OrderBy(b => b.Box.Width * b.Box.Height))
        {
            var box = block.Box;
            box.Inflate(4, 4);
            if (box.Contains(p))
                return block;
        }
        foreach (var block in blocks)
            foreach (var line in block.Lines)
            {
                var box = line.Box;
                box.Inflate(10, Math.Max(6, line.Box.Height * 0.4));
                if (box.Contains(p))
                    return block;
            }
        return null;
    }

    /// <summary>The line under a point, for showing what a click would take.</summary>
    public static ScreenLine? LineAt(Point p, IReadOnlyList<ScreenLine> lines)
    {
        foreach (var line in lines)
        {
            var box = line.Box;
            box.Inflate(4, 3);
            if (box.Contains(p))
                return line;
        }
        return null;
    }

    // ---------- geometry ----------

    /// <summary>
    /// A loop: the stroke ends near where it began (within a third of its size), and is big enough to have drawn round
    /// something. A line along a sentence ends far from its start.
    /// </summary>
    public static bool Encloses(IReadOnlyList<Point> stroke)
    {
        if (stroke.Count < 6)
            return false;
        var bounds = Bounds(stroke);
        if (bounds.Width < 14 || bounds.Height < 14)
            return false;
        double size = Math.Sqrt(bounds.Width * bounds.Width + bounds.Height * bounds.Height);
        return (stroke[0] - stroke[^1]).Length <= Math.Max(30, size * 0.35);
    }

    /// <summary>Whether a point is inside the loop (the stroke closed back to its start).</summary>
    public static bool Contains(IReadOnlyList<Point> polygon, Point p)
    {
        bool inside = false;
        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
        {
            var a = polygon[i];
            var b = polygon[j];
            if ((a.Y > p.Y) != (b.Y > p.Y) && p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y) + a.X)
                inside = !inside;
        }
        return inside;
    }

    /// <summary>Whether the stroke passes within <paramref name="reach"/> of the box.</summary>
    public static bool Touches(IReadOnlyList<Point> stroke, Rect box, double reach)
    {
        box.Inflate(reach, reach);
        if (stroke.Count == 1)
            return box.Contains(stroke[0]);
        for (int i = 1; i < stroke.Count; i++)
            if (SegmentMeetsRect(stroke[i - 1], stroke[i], box))
                return true;
        return false;
    }

    private static bool SegmentMeetsRect(Point a, Point b, Rect r)
    {
        if (r.Contains(a) || r.Contains(b))
            return true;
        // Clipped against the box (Liang–Barsky): some of the segment is left inside.
        double t0 = 0, t1 = 1, dx = b.X - a.X, dy = b.Y - a.Y;
        foreach (var (p, q) in new[] { (-dx, a.X - r.Left), (dx, r.Right - a.X), (-dy, a.Y - r.Top), (dy, r.Bottom - a.Y) })
        {
            if (p == 0)
            {
                if (q < 0)
                    return false;
                continue;
            }
            double t = q / p;
            if (p < 0)
                t0 = Math.Max(t0, t);
            else
                t1 = Math.Min(t1, t);
            if (t0 > t1)
                return false;
        }
        return true;
    }

    public static Rect Bounds(IReadOnlyList<Point> points)
    {
        if (points.Count == 0)
            return Rect.Empty;
        double left = points.Min(p => p.X), top = points.Min(p => p.Y), right = points.Max(p => p.X), bottom = points.Max(p => p.Y);
        return new Rect(left, top, right - left, bottom - top);
    }

    /// <summary>
    /// A hand-drawn stroke made smooth for showing (Chaikin's corner cutting, twice); the stroke itself is what counts
    /// for what it takes in.
    /// </summary>
    public static IReadOnlyList<Point> Smooth(IReadOnlyList<Point> points, bool closed)
    {
        var current = points.ToList();
        for (int pass = 0; pass < 2 && current.Count > 2; pass++)
        {
            var next = new List<Point>(current.Count * 2);
            int n = current.Count;
            if (!closed)
                next.Add(current[0]);
            for (int i = 0; i < (closed ? n : n - 1); i++)
            {
                var a = current[i];
                var b = current[(i + 1) % n];
                next.Add(new Point(a.X * 0.75 + b.X * 0.25, a.Y * 0.75 + b.Y * 0.25));
                next.Add(new Point(a.X * 0.25 + b.X * 0.75, a.Y * 0.25 + b.Y * 0.75));
            }
            if (!closed)
                next.Add(current[^1]);
            current = next;
        }
        return current;
    }

    public static Rect Box(PictureWord w) => new(w.X, w.Y, Math.Max(1, w.Width), Math.Max(1, w.Height));

    public static Rect Union(IReadOnlyCollection<PictureWord> words)
    {
        if (words.Count == 0)
            return Rect.Empty;
        double left = words.Min(w => w.X), top = words.Min(w => w.Y), right = words.Max(w => w.Right), bottom = words.Max(w => w.Bottom);
        return new Rect(left, top, Math.Max(1, right - left), Math.Max(1, bottom - top));
    }

    /// <summary>An area kept inside the still, in whole pixels.</summary>
    public static Int32Rect Clamp(Rect area, int width, int height)
    {
        int left = Math.Clamp((int)Math.Floor(area.Left), 0, Math.Max(0, width - 1));
        int top = Math.Clamp((int)Math.Floor(area.Top), 0, Math.Max(0, height - 1));
        int right = Math.Clamp((int)Math.Ceiling(area.Right), left + 1, width);
        int bottom = Math.Clamp((int)Math.Ceiling(area.Bottom), top + 1, height);
        return new Int32Rect(left, top, right - left, bottom - top);
    }
}
