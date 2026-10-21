using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace Petal.App.Overlay;

/// <summary>
/// A font for single lines of wheel text, measured by their ink: what the eye sees, without the line
/// box's leading, the font's side bearings or a wrapped line's trailing space. Lines are cached, so the
/// names that come back on every hover are measured once.
/// </summary>
internal sealed class InkFont
{
    // As the wheel's text blocks had it: the default language, and Western digits (see L.Apply), which
    // also keeps "WEBP 1600" in that order in a right-to-left line.
    private static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("en-US");
    private static readonly NumberSubstitution Digits = new(NumberCultureSource.Override, Culture, NumberSubstitutionMethod.European);

    /// <summary>Lines are measured for fitting at this size, then scaled.</summary>
    private const double Reference = 10;

    private readonly Dictionary<(string Text, double Size), Rect> _ink = [];
    private readonly Dictionary<string, Rect> _rough = [];
    private readonly Dictionary<string, Rect[]> _strips = [];

    public InkFont(FontFamily family, FontWeight weight, FlowDirection direction, double pixelsPerDip)
    {
        Typeface = new Typeface(family, FontStyles.Normal, weight, FontStretches.Normal);
        Direction = direction;
        PixelsPerDip = pixelsPerDip;
    }

    public Typeface Typeface { get; }
    public FlowDirection Direction { get; }
    public double PixelsPerDip { get; }

    private FormattedText Format(string text, double size, Brush brush) =>
        new(text, Culture, Direction, Typeface, size, brush, Digits, TextFormattingMode.Display, PixelsPerDip);

    /// <summary>The ink of a line, with y measured from its baseline (up is negative).</summary>
    public Rect Ink(string text, double size)
    {
        if (_ink.TryGetValue((text, size), out var ink))
            return ink;
        var formatted = Format(text, size, Brushes.Black);
        var bounds = formatted.BuildGeometry(new Point(0, 0)).Bounds;
        ink = bounds.IsEmpty
            ? new Rect(0, -size * 0.7, formatted.Width, size * 0.7)
            : new Rect(bounds.X, bounds.Y - formatted.Baseline, bounds.Width, bounds.Height);
        _ink[(text, size)] = ink;
        return ink;
    }

    /// <summary>The ink of a line, y from the baseline, measured once and scaled to <paramref name="size"/>: close enough for fitting.</summary>
    public Rect RoughInk(string text, double size)
    {
        if (!_rough.TryGetValue(text, out var ink))
        {
            var formatted = Format(text, Reference, Brushes.Black);
            ink = formatted.BuildGeometry(new Point(0, -formatted.Baseline)).Bounds;
            if (ink.IsEmpty)
                ink = new Rect(0, -Reference * 0.7, formatted.Width, Reference * 0.7);
            _rough[text] = ink;
        }
        double k = size / Reference;
        return new Rect(ink.X * k, ink.Y * k, ink.Width * k, ink.Height * k);
    }

    /// <summary>
    /// The ink of a line as upright strips, y from the baseline, so the empty corners of a word don't count
    /// as ink. Measured once and scaled to <paramref name="size"/>: close enough for fitting.
    /// </summary>
    public Rect[] Strips(string text, double size)
    {
        if (!_strips.TryGetValue(text, out var strips))
        {
            var formatted = Format(text, Reference, Brushes.Black);
            strips = InkShape.Strips(formatted.BuildGeometry(new Point(0, -formatted.Baseline)), 4);
            _strips[text] = strips;
        }
        return InkShape.Scale(strips, size / Reference);
    }

    public InkText Create(string text, double size, Brush brush) => new(Format(text, size, brush), Ink(text, size));
}

internal static class InkShape
{
    /// <summary>
    /// The ink of a geometry cut into <paramref name="count"/> upright strips of equal width, each as tall as
    /// the ink inside it. Strips with no ink are left out.
    /// </summary>
    public static Rect[] Strips(Geometry geometry, int count)
    {
        var bounds = geometry.Bounds;
        if (bounds.IsEmpty)
            return [];
        double width = bounds.Width / count;
        var top = Enumerable.Repeat(double.MaxValue, count).ToArray();
        var bottom = Enumerable.Repeat(double.MinValue, count).ToArray();
        void Add(int strip, double y)
        {
            top[strip] = Math.Min(top[strip], y);
            bottom[strip] = Math.Max(bottom[strip], y);
        }
        int StripOf(double x) => Math.Clamp((int)((x - bounds.Left) / width), 0, count - 1);
        // Each edge counts in every strip it crosses, so a long straight stroke isn't missed between its ends.
        void Edge(Point a, Point b)
        {
            Add(StripOf(a.X), a.Y);
            Add(StripOf(b.X), b.Y);
            for (int j = 1; j < count; j++)
            {
                double x = bounds.Left + j * width;
                if (x <= Math.Min(a.X, b.X) || x >= Math.Max(a.X, b.X))
                    continue;
                double y = a.Y + (x - a.X) / (b.X - a.X) * (b.Y - a.Y);
                Add(j - 1, y);
                Add(j, y);
            }
        }
        foreach (var figure in geometry.GetFlattenedPathGeometry(0.1, ToleranceType.Absolute).Figures)
        {
            var previous = figure.StartPoint;
            foreach (var segment in figure.Segments)
            {
                IEnumerable<Point> points = segment switch
                {
                    PolyLineSegment line => line.Points,
                    LineSegment line => [line.Point],
                    _ => [],
                };
                foreach (var point in points)
                {
                    Edge(previous, point);
                    previous = point;
                }
            }
            Edge(previous, figure.StartPoint);
        }
        var strips = new List<Rect>();
        for (int i = 0; i < count; i++)
            if (top[i] <= bottom[i])
                strips.Add(new Rect(bounds.Left + i * width, top[i], width, bottom[i] - top[i]));
        return [.. strips];
    }

    public static Rect[] Scale(Rect[] strips, double scale) =>
        [.. strips.Select(r => new Rect(r.X * scale, r.Y * scale, r.Width * scale, r.Height * scale))];

    /// <summary>The box around all the strips.</summary>
    public static Rect Bounds(Rect[] strips) =>
        strips.Length == 0 ? Rect.Empty : strips.Aggregate(Rect.Union);
}

/// <summary>One line of text whose box is exactly its ink, so placing the box places what the eye sees.</summary>
internal sealed class InkText : FrameworkElement
{
    private readonly FormattedText _text;

    public InkText(FormattedText text, Rect ink)
    {
        _text = text;
        Ink = ink;
        // Drawn unmirrored; the text keeps its own direction.
        FlowDirection = FlowDirection.LeftToRight;
        IsHitTestVisible = false;
    }

    /// <summary>The ink, with y measured from the baseline.</summary>
    public Rect Ink { get; }

    public Brush Foreground
    {
        set
        {
            _text.SetForegroundBrush(value);
            InvalidateVisual();
        }
    }

    /// <summary>Puts the ink's centre on <paramref name="x"/> and the baseline on <paramref name="baseline"/>.</summary>
    public void Place(double x, double baseline)
    {
        System.Windows.Controls.Canvas.SetLeft(this, x - Ink.Width / 2);
        System.Windows.Controls.Canvas.SetTop(this, baseline + Ink.Y);
    }

    protected override Size MeasureOverride(Size availableSize) => Ink.Size;

    protected override void OnRender(DrawingContext drawingContext) =>
        drawingContext.DrawText(_text, new Point(-Ink.X, -(Ink.Y + _text.Baseline)));
}

internal static class WheelText
{
    /// <summary>
    /// The words of a name. A mark with no letters or digits ("·", "&") stays with the word before it,
    /// so no line starts with one.
    /// </summary>
    public static string[] Words(string text, string separator = " ")
    {
        var words = new List<string>();
        foreach (var part in text.Split(separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (words.Count > 0 && !part.Any(char.IsLetterOrDigit))
                words[^1] += separator + part;
            else
                words.Add(part);
        }
        return [.. words];
    }

    /// <summary>
    /// Every way to put the words, in order, on <paramref name="lines"/> lines. A separator left at the
    /// end of a broken line goes: "TEXT · QR" becomes "TEXT" over "QR".
    /// </summary>
    public static IEnumerable<string[]> Splits(IReadOnlyList<string> words, int lines, string separator = " ")
    {
        if (lines <= 1 || words.Count <= 1)
        {
            yield return [string.Join(separator, words)];
            yield break;
        }
        for (int first = 1; first <= words.Count - lines + 1; first++)
        {
            var head = string.Join(separator, words.Take(first)).TrimEnd(' ', '·', '•', '|', '-', '–', '—', '\u200F');
            foreach (var rest in Splits(words.Skip(first).ToList(), lines - 1, separator))
                yield return [head, .. rest];
        }
    }
}

/// <summary>An icon's ink as upright strips, inside its box.</summary>
internal sealed record IconInk(Rect Box, Rect[] Strips);

/// <summary>A petal's label: its lines and size, the point its ink is centred on, and where the icon and lines go from there.</summary>
internal sealed record PetalLabel(string[] Lines, double Size, Point Centre, double IconTop, double[] Baselines, double Clearance);

/// <summary>
/// Fits each petal's label inside its own petal. A petal is a wedge, so one beside the hub is short where
/// one above it is narrow: every arrangement of the label (its lines, a size up to a point and a half smaller,
/// and a small move along the petal) is measured against the petal's real edges, its gaps and rounded corners
/// included, and the ink is kept clear of them. A label that already fits keeps its size, centred on
/// the petal.
/// </summary>
internal static class PetalLayout
{
    /// <summary>Ink stays at least this far inside its petal.</summary>
    public const double Margin = 2;
    /// <summary>The petals' rounded outer corners, taken as circles; their Bézier corners are a little tighter.</summary>
    private const double Corner = 13;
    /// <summary>Space between an icon's box and the caption under it.</summary>
    private const double IconGap = 1.5;

    // What each change costs. Labels of one size read as a set, so shrinking costs most: a quarter point
    // outweighs the whole range of moves along the petal, and an extra line weighs as much as a 10 DIP move.
    private const double ShrinkCost = 4;   // per point of size
    private const double LineCost = 0.5;   // per extra line
    private const double MoveCost = 0.05;  // per DIP along the petal

    private static readonly double[] Radii = [.. Enumerable.Range(0, 43).Select(i => WheelGeometry.LabelRadius - 6 + i * 0.5)];

    public static PetalLabel Fit(string text, InkFont font, double size, double smallest, double step, IconInk? icon, double iconSize, int index, int count)
    {
        var words = WheelText.Words(text);
        double angle = WheelGeometry.Angle(index, count);
        (string[] Lines, double Size, double Radius, double Clearance)? best = null, closest = null;
        double bestScore = double.MinValue, closestScore = double.MinValue;
        for (double s = size; s >= smallest; s -= 0.25)
        {
            // Nothing smaller can beat a label that already fits.
            if (best is not null && -ShrinkCost * (size - s) <= bestScore)
                break;
            for (int lines = 1; lines <= Math.Max(1, Math.Min(3, words.Length)); lines++)
            {
                string[] split = words.Length == 0 ? [] : Balanced(words, lines, font, s);
                var (shapes, _, _) = Block(split, s, step * s / size, line => font.Strips(line, s), icon, iconSize);
                if (shapes.Count == 0)
                    return new PetalLabel([], size, WheelGeometry.LabelPosition(index, count), 0, [], 0);
                foreach (double r in Radii)
                {
                    var centre = WheelGeometry.At(r, angle);
                    double clearance = double.MaxValue;
                    foreach (var shape in shapes)
                        clearance = Math.Min(clearance, Clearance(new Rect(shape.X + centre.X, shape.Y + centre.Y, shape.Width, shape.Height), index, count));
                    double score = -ShrinkCost * (size - s) - LineCost * (lines - 1) - MoveCost * Math.Abs(r - WheelGeometry.LabelRadius);
                    if (clearance >= Margin && score > bestScore)
                    {
                        best = (split, s, r, clearance);
                        bestScore = score;
                    }
                    // When nothing fits, the arrangement that crosses its edges least; a quarter DIP is no difference.
                    if (best is null && (closest is null || clearance > closest.Value.Clearance + 0.25 || (clearance > closest.Value.Clearance - 0.25 && score > closestScore)))
                    {
                        closest = (split, s, r, clearance);
                        closestScore = score;
                    }
                }
            }
        }

        // Placed with each line's exact ink, measured at the size chosen.
        var (chosen, chosenSize, radius, room) = (best ?? closest)!.Value;
        var (_, iconTop, baselines) = Block(chosen, chosenSize, step * chosenSize / size, line => [font.Ink(line, chosenSize)], icon, iconSize);
        return new PetalLabel(chosen, chosenSize, WheelGeometry.At(radius, angle), iconTop, baselines, room);
    }

    /// <summary>The split whose widest line is narrowest.</summary>
    private static string[] Balanced(string[] words, int lines, InkFont font, double size)
    {
        string[] best = [];
        double bestWidth = double.MaxValue;
        foreach (var split in WheelText.Splits(words, lines))
        {
            double widest = split.Max(line => font.RoughInk(line, size).Width);
            if (widest < bestWidth - 0.01)
                (best, bestWidth) = (split, widest);
        }
        return best;
    }

    /// <summary>
    /// The ink of the icon and of each line (as <paramref name="ink"/> gives it, from the baseline), every
    /// piece centred across x = 0 and the whole centred on y = 0; with the icon box's top and each baseline.
    /// </summary>
    private static (List<Rect> Shapes, double IconTop, double[] Baselines) Block(string[] lines, double size, double step,
        Func<string, Rect[]> ink, IconInk? icon, double iconSize)
    {
        var shapes = new List<Rect>();
        double first = 0;
        if (icon is not null)
        {
            double middle = icon.Box.X + icon.Box.Width / 2;
            shapes.AddRange(icon.Strips.Select(r => new Rect(r.X - middle, r.Y, r.Width, r.Height)));
            // The first baseline sits where a caption line's would under the icon.
            first = iconSize + IconGap + step * 0.8;
        }
        var baselines = new double[lines.Length];
        for (int i = 0; i < lines.Length; i++)
        {
            var pieces = ink(lines[i]);
            var box = InkShape.Bounds(pieces);
            double middle = box.X + box.Width / 2;
            baselines[i] = first + i * step;
            shapes.AddRange(pieces.Select(r => new Rect(r.X - middle, r.Y + baselines[i], r.Width, r.Height)));
        }
        if (shapes.Count == 0)
            return (shapes, 0, baselines);
        double shift = -(shapes.Min(r => r.Top) + shapes.Max(r => r.Bottom)) / 2;
        return (shapes.Select(r => new Rect(r.X, r.Y + shift, r.Width, r.Height)).ToList(), shift, baselines.Select(b => b + shift).ToArray());
    }

    /// <summary>How far a rectangle (wheel coordinates, y down) keeps inside its petal; negative once it crosses the edge.</summary>
    private static double Clearance(Rect rect, int index, int count)
    {
        double inner = WheelGeometry.PetalInner, outer = WheelGeometry.PetalOuter, gap = WheelGeometry.Gap / 2;
        // The rectangle's nearest point to the centre decides the inner edge; its corners decide the rest.
        double nx = Math.Clamp(0, rect.Left, rect.Right), ny = Math.Clamp(0, rect.Top, rect.Bottom);
        double clearance = Math.Sqrt(nx * nx + ny * ny) - inner;
        double theta = WheelGeometry.Angle(index, count), half = Math.PI / count;
        double a = theta - half, b = theta + half;
        double turn = Math.Asin((Corner + gap) / (outer - Corner));
        var cornerA = WheelGeometry.At(outer - Corner, a + turn);
        var cornerB = WheelGeometry.At(outer - Corner, b - turn);
        foreach (var p in new[] { rect.TopLeft, rect.TopRight, rect.BottomLeft, rect.BottomRight })
        {
            double x = p.X, y = -p.Y;
            double toOuter = outer - Math.Sqrt(x * x + y * y);
            clearance = Math.Min(clearance, toOuter);
            if (count == 1)
                continue;
            double sideA = -x * Math.Sin(a) + y * Math.Cos(a) - gap;
            double sideB = x * Math.Sin(b) - y * Math.Cos(b) - gap;
            clearance = Math.Min(clearance, Math.Min(sideA, sideB));
            // Near a rounded outer corner the edge is the corner's arc.
            if (toOuter < Corner && sideA < Corner)
                clearance = Math.Min(clearance, Corner - (p - cornerA).Length);
            if (toOuter < Corner && sideB < Corner)
                clearance = Math.Min(clearance, Corner - (p - cornerB).Length);
        }
        return clearance;
    }
}

/// <summary>A line of the wheel's centre, its baseline measured from the centre.</summary>
internal sealed record HubRow(string Text, double Size, bool IsTitle, double Baseline, Rect Ink)
{
    public double Top => Baseline + Ink.Top;
    public double Bottom => Baseline + Ink.Bottom;
}

/// <summary>
/// The centre's lines and their outline: a pill with fully round ends, half <see cref="HalfWidth"/> wide and
/// half <see cref="HalfHeight"/> high. Equal halves make it a disc.
/// </summary>
internal sealed record HubFit(IReadOnlyList<HubRow> Rows, double HalfWidth, double HalfHeight)
{
    /// <summary>How far the outline reaches from the centre.</summary>
    public double Reach => Math.Max(HalfWidth, HalfHeight);
}

/// <summary>
/// Sets the name of the hovered petal inside the hub, a circle, instead of cutting it short. A name that
/// fits on one line keeps the usual pill; a longer one is broken into up to three lines, and the pill
/// swells into a disc whose round edge every line is fitted to, wide lines through the middle and short
/// ones near the top and bottom, the way words are set on a coin. The size shrinks only as far as needed.
/// </summary>
internal static class HubLayout
{
    /// <summary>The outline's furthest reach: 3 DIPs inside the hub's groove.</summary>
    public const double Room = WheelGeometry.HubRadius - 3;

    private static string Separator => Jobs.FavouriteText.Separator;

    public const double TitleSize = 11.5;
    public const double SmallestTitle = 8;
    public const double SubtitleSize = 8.5;
    private const double SmallestSubtitle = 7.5;

    /// <summary>Ink to the round ends, measured towards their centres.</summary>
    private const double Edge = 4;
    /// <summary>Ink to the far left and right of the outline.</summary>
    private const double Side = 8.5;
    /// <summary>Ink to the top and bottom of the outline.</summary>
    private const double Cap = 7.5;
    /// <summary>The narrowest pill, so short names such as "PNG" keep a pill rather than a coin.</summary>
    private const double NarrowestHalf = 26;

    public static HubFit Fit(string title, string subtitle, InkFont titleFont, InkFont subtitleFont, bool arabic)
    {
        // Chosen with each line measured once and scaled, then set with its exact ink.
        Func<string, double, Rect> roughTitle = titleFont.RoughInk;
        Func<string, double, Rect> roughSubtitle = subtitleFont.RoughInk;
        var chosen = Search(title, subtitle, roughTitle, roughSubtitle, arabic) ?? Shortened(title, subtitle, roughTitle, roughSubtitle, arabic);
        var titleRows = chosen.Rows.Where(r => r.IsTitle).ToList();
        var subtitleRows = chosen.Rows.Where(r => !r.IsTitle).ToList();
        var rows = Rows(titleRows.Select(r => r.Text).ToList(), titleRows[0].Size,
            subtitleRows.Select(r => r.Text).ToList(), subtitleRows.Count > 0 ? subtitleRows[0].Size : SubtitleSize,
            titleFont.Ink, subtitleFont.Ink, arabic);
        return Outline(rows) ?? chosen with { Rows = rows };
    }

    private static HubFit? Search(string title, string subtitle, Func<string, double, Rect> titleInk, Func<string, double, Rect> subtitleInk, bool arabic)
    {
        if (TooLong(title, titleInk))
            return null;
        var titleWords = WheelText.Words(title);
        // A favourite's description breaks between its parts ("WEBP · 1600 px"), never inside one.
        string separator = subtitle.Contains(Separator) ? Separator : " ";
        var subtitleWords = WheelText.Words(subtitle, separator);

        var subtitles = new List<(string[] Lines, double Size, double Cost)>();
        if (subtitleWords.Length == 0)
        {
            subtitles.Add(([], SubtitleSize, 0));
        }
        else
        {
            for (double size = SubtitleSize; size >= SmallestSubtitle; size -= 0.5)
                for (int lines = 1; lines <= Math.Min(2, subtitleWords.Length); lines++)
                    foreach (var split in WheelText.Splits(subtitleWords, lines, separator))
                        subtitles.Add((split, size, (SubtitleSize - size) * 0.8 + (lines - 1) * 1.5));
        }

        HubFit? best = null;
        double bestScore = double.MinValue;
        for (double size = TitleSize; size >= SmallestTitle; size -= 0.25)
        {
            for (int lines = 1; lines <= Math.Min(3, Math.Max(1, titleWords.Length)); lines++)
            {
                // An extra line costs about as much as a point of size: "Clean for Sharing" stays on one
                // line at 11 but goes onto two rather than shrinking to 9.
                double cost = TitleSize - size + (lines - 1) * 1.0;
                if (-cost <= bestScore)
                    continue;
                foreach (var split in WheelText.Splits(titleWords, lines))
                {
                    foreach (var (subLines, subSize, subCost) in subtitles)
                    {
                        double score = -cost - subCost;
                        if (score <= bestScore)
                            continue;
                        var fit = Outline(Rows(split, size, subLines, subSize, titleInk, subtitleInk, arabic));
                        if (fit is null)
                            continue;
                        // Of equal choices the smaller outline wins.
                        score -= fit.Reach * 0.001;
                        if (score > bestScore)
                            (best, bestScore) = (fit, score);
                    }
                }
            }
        }
        return best;
    }

    /// <summary>
    /// A name too long even for three small lines loses its end to an ellipsis: the longest beginning that
    /// fits, found by halving (a shorter beginning fits whenever a longer one does).
    /// </summary>
    private static HubFit Shortened(string title, string subtitle, Func<string, double, Rect> titleInk, Func<string, double, Rect> subtitleInk, bool arabic)
    {
        HubFit? Try(int length)
        {
            var text = title[..length].TrimEnd() + "…";
            if (TooLong(text, titleInk))
                return null;
            var words = WheelText.Words(text);
            foreach (var sub in new[] { subtitle, "" })
            {
                string[] subLines = WheelText.Words(sub).Length == 0 ? [] : [sub];
                for (int lines = 1; lines <= Math.Min(3, words.Length); lines++)
                    foreach (var split in WheelText.Splits(words, lines))
                        if (Outline(Rows(split, SmallestTitle, subLines, SmallestSubtitle, titleInk, subtitleInk, arabic)) is { } fit)
                            return fit;
            }
            return null;
        }
        int low = 0, high = title.Length - 1;
        while (low < high)
        {
            int middle = (low + high + 1) / 2;
            if (Try(middle) is null)
                high = middle - 1;
            else
                low = middle;
        }
        return (low > 0 ? Try(low) : null) ?? Outline(Rows(["…"], SmallestTitle, [], SmallestSubtitle, titleInk, subtitleInk, arabic))!;
    }

    /// <summary>Wider on one line than three of the widest lines the hub holds, even at the smallest size.</summary>
    private static bool TooLong(string title, Func<string, double, Rect> titleInk) =>
        titleInk(title, SmallestTitle).Width > 3 * 2 * (Room - Side) + 20;

    /// <summary>The lines stacked on their baselines, then moved so their ink is centred on the hub.</summary>
    private static List<HubRow> Rows(IReadOnlyList<string> title, double titleSize, IReadOnlyList<string> subtitle, double subtitleSize,
        Func<string, double, Rect> titleInk, Func<string, double, Rect> subtitleInk, bool arabic)
    {
        var rows = new List<HubRow>();
        double baseline = 0;
        double titleStep = titleSize * (arabic ? 1.3 : 1.2);
        foreach (var line in title)
        {
            rows.Add(new HubRow(line, titleSize, true, baseline, titleInk(line, titleSize)));
            baseline += titleStep;
        }
        baseline += subtitleSize * 1.42 - titleStep;
        foreach (var line in subtitle)
        {
            rows.Add(new HubRow(line, subtitleSize, false, baseline, subtitleInk(line, subtitleSize)));
            baseline += subtitleSize * 1.25;
        }
        double shift = -(rows.Min(r => r.Top) + rows.Max(r => r.Bottom)) / 2;
        return rows.Select(r => r with { Baseline = r.Baseline + shift }).ToList();
    }

    /// <summary>The smallest pill or disc that holds the rows and stays inside the hub, if there is one.</summary>
    private static HubFit? Outline(List<HubRow> rows)
    {
        double half = rows.Max(r => Math.Max(-r.Top, r.Bottom));
        HubFit? best = null;
        for (double b = half + Cap; b <= Room; b += 0.25)
        {
            if (best is not null && b >= best.Reach)
                break;
            // The ends are half-circles of radius b; the ink keeps Edge from them.
            double radius = b - Edge;
            double a = Math.Max(b, NarrowestHalf);
            bool fits = true;
            foreach (var row in rows)
            {
                double y = Math.Max(Math.Abs(row.Top), Math.Abs(row.Bottom));
                double halfWidth = row.Ink.Width / 2;
                if (y >= radius)
                {
                    fits = false;
                    break;
                }
                a = Math.Max(a, Math.Max(halfWidth + Side, halfWidth + b - Math.Sqrt(radius * radius - y * y)));
            }
            if (!fits || a > Room)
                continue;
            if (best is null || Math.Max(a, b) < best.Reach)
                best = new HubFit(rows, a, b);
        }
        return best;
    }
}
