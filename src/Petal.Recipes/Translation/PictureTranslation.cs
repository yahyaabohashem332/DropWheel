using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ImageMagick;
using TextAlignment = System.Windows.TextAlignment;
using Petal.Engines.Documents;
using Petal.Engines.Documents.Ocr;
using Petal.Engines.Imaging;

namespace Petal.Recipes.Translation;

/// <summary>Where a block's lines sit against each other, as seen on the picture.</summary>
public enum PictureAlign
{
    Left,
    Centre,
    Right,
}

/// <summary>A piece of text on a picture: a line, or the lines of one paragraph.</summary>
public sealed class PictureBlock
{
    public required string Source { get; init; }

    /// <summary>The boxes of its lines, in the picture's pixels: what is taken off the picture.</summary>
    public required IReadOnlyList<Rect> Lines { get; init; }

    public required Rect Box { get; init; }

    /// <summary>How tall its lines are, in pixels.</summary>
    public required double LineHeight { get; init; }

    /// <summary>The font size it looks to be set in, the same for text of one style on the picture.</summary>
    public double Size { get; set; }

    /// <summary>The column it is part of: lines starting at one edge end at the longest of them.</summary>
    public Rect Column { get; set; }

    /// <summary>The shape of one colour it sits in (a button, a band), or the whole picture.</summary>
    public Rect Shape { get; set; }

    public required PictureAlign Align { get; set; }

    public required bool RightToLeft { get; init; }

    public Color Ink { get; set; } = Colors.Black;

    public Color Paper { get; set; } = Colors.White;

    public bool Bold { get; set; }

    /// <summary>The background behind it is plain enough to be painted without the eraser model.</summary>
    public bool Plain { get; set; } = true;

    /// <summary>Its text in the other language, which the user may correct.</summary>
    public string Translation { get; set; } = "";

    /// <summary>It stays as it is on the picture: a number, a name, what was read badly, or what the user chose to keep.</summary>
    public bool Keep { get; set; }

    /// <summary>The translation had to go smaller than was comfortable to fit where it goes.</summary>
    public bool Tight { get; internal set; }
}

/// <summary>A line of words read off a picture, as the grouping sees it.</summary>
internal sealed record ReadLine(string Text, Rect Box, int Words, bool RightToLeft, double Height);

/// <summary>A picture on its way: what it looks like, and what was found on it.</summary>
public sealed class PictureWork
{
    public required byte[] Original { get; init; }

    public required int Width { get; init; }

    public required int Height { get; init; }

    public required IReadOnlyList<PictureBlock> Blocks { get; init; }

    /// <summary>The picture with its background taken out, dark where something stands out from what is beside it: where letters are.</summary>
    public byte[]? StandsOut { get; init; }

    /// <summary>The picture's colour profile, which the saved one keeps.</summary>
    public IColorProfile? Profile { get; init; }

    /// <summary>The picture with the text taken off; null until it has been.</summary>
    public byte[]? Clean { get; internal set; }

    /// <summary>Whether some text sits on a background only the eraser model can rebuild.</summary>
    public bool NeedsEraser => Blocks.Any(b => !b.Keep && !b.Plain);
}

/// <summary>
/// Translates the text on a picture where it stands: Windows reads the words, the translator reads them by meaning, the
/// text comes off (plain backgrounds are painted, anything else is rebuilt by LaMa) and the translation goes in its
/// place, in the colour, weight and alignment the original had. WPF lays the text out, so Arabic is shaped and ordered properly.
/// </summary>
public static class PictureTranslation
{
    /// <summary>The widest piece of text the eraser is given at once: its 512-pixel view then stays sharp enough.</summary>
    private const double WidestPiece = 360;

    private const string Font = "Segoe UI";

    /// <summary>Reads a picture file.</summary>
    public static async Task<PictureWork> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        var (bgra, width, height, profile) = await Task.Run(() =>
        {
            using var image = ObjectEraser.Load(path);
            image.ColorSpace = image.ColorSpace == ColorSpace.Gray ? ColorSpace.sRGB : image.ColorSpace;
            var pixels = image.GetPixels().ToByteArray(PixelMapping.BGRA) ?? throw new InvalidOperationException("The picture has no pixels.");
            return (pixels, (int)image.Width, (int)image.Height, image.GetColorProfile());
        }, cancellationToken).ConfigureAwait(false);
        return await ReadAsync(bgra, width, height, profile, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads a picture from its BGRA pixels: every language installed reads it, each line from the one that read it best.</summary>
    public static async Task<PictureWork> ReadAsync(byte[] bgra, int width, int height, IColorProfile? profile = null, CancellationToken cancellationToken = default)
    {
        var versions = await Task.Run(() => Versions(bgra, width, height), cancellationToken).ConfigureAwait(false);
        var words = await DocumentEngine.ReadPictureAsync(versions, width, height, cancellationToken).ConfigureAwait(false);
        var blocks = Blocks(LinesOf(words), width);
        foreach (var block in blocks)
        {
            Look(block, bgra, width, height, versions[^1]);
            block.Keep = !Trusted(block.Source);
        }
        Arrange(blocks, width, height, bgra);
        return new PictureWork { Original = bgra, Width = width, Height = height, Blocks = blocks, Profile = profile, StandsOut = versions[^1] };
    }

    /// <summary>
    /// The versions of a picture Windows reads: as it is; with its contrast raised place by place, for pale text on a
    /// pale ground; and with its background taken out, for text over a photograph.
    /// </summary>
    private static List<byte[]> Versions(byte[] bgra, int width, int height)
    {
        var versions = new List<byte[]> { bgra };
        using var source = new MagickImage();
        // Over white, as a transparent picture is seen.
        var solid = new byte[width * height * 3];
        for (int i = 0, n = width * height; i < n; i++)
        {
            int a = bgra[i * 4 + 3];
            for (int c = 0; c < 3; c++)
                solid[i * 3 + c] = (byte)(bgra[i * 4 + c] * a / 255 + 255 - a);
        }
        source.ReadPixels(solid, new PixelReadSettings((uint)width, (uint)height, StorageType.Char, "BGR"));
        using (var raised = (MagickImage)source.Clone())
        {
            raised.Clahe(new Percentage(12.5), new Percentage(12.5), 128, 3);
            versions.Add(raised.GetPixels().ToByteArray(PixelMapping.BGRA)!);
        }
        using (var blurred = (MagickImage)source.Clone())
        {
            // What differs from its neighbourhood stands out and what changes slowly goes: a photograph's light and shade, not its letters.
            blurred.Blur(0, Math.Max(8, Math.Min(width, height) / 40.0));
            using var difference = (MagickImage)source.Clone();
            difference.Composite(blurred, CompositeOperator.Difference);
            difference.ColorSpace = ColorSpace.Gray;
            difference.AutoLevel();
            difference.Negate();
            difference.ColorSpace = ColorSpace.sRGB;
            versions.Add(difference.GetPixels().ToByteArray(PixelMapping.BGRA)!);
        }
        return versions;
    }

    /// <summary>The lines read: a Windows line with a wide gap in it is two things side by side, like a dish and its price.</summary>
    internal static IReadOnlyList<ReadLine> LinesOf(IReadOnlyList<PictureWord> words)
    {
        var cut = new List<PictureWord>(words.Count);
        int number = 0;
        foreach (var line in words.GroupBy(w => w.Line))
        {
            PictureWord? last = null;
            foreach (var word in line.OrderBy(w => w.X))
            {
                if (last is not null && word.X - last.Right > 1.8 * Math.Max(word.Height, last.Height))
                    number++;
                cut.Add(word with { Line = number });
                last = word;
            }
            number++;
        }
        return ReadingOrder.Lines(cut)
            .Where(l => l.Count > 0)
            .Select(l =>
            {
                double left = l.Min(w => w.X), top = l.Min(w => w.Y), right = l.Max(w => w.Right), bottom = l.Max(w => w.Bottom);
                var text = string.Join(' ', l.Select(w => w.Text));
                var heights = l.Select(w => w.Height).Order().ToList();
                return new ReadLine(text, new Rect(left, top, Math.Max(1, right - left), Math.Max(1, bottom - top)), l.Count, IsRightToLeft(text), heights[heights.Count / 2]);
            })
            .ToList();
    }

    private static bool IsRightToLeft(string text)
    {
        int rtl = 0, ltr = 0;
        foreach (char c in text)
        {
            if (c is >= (char)0x0590 and <= (char)0x08FF or >= (char)0xFB1D and <= (char)0xFEFF)
                rtl++;
            else if (char.IsLetter(c))
                ltr++;
        }
        return rtl > ltr;
    }

    /// <summary>
    /// Lines become a paragraph only when one plainly ran on into the next: the same size, close, in one column and the
    /// next word didn't fit. Anything else (a menu, a list, a heading above its text) stays a line of its own, or a
    /// list would be read as one long sentence and lose its shape.
    /// </summary>
    internal static IReadOnlyList<PictureBlock> Blocks(IReadOnlyList<ReadLine> lines, int pictureWidth)
    {
        var groups = new List<List<ReadLine>>();
        foreach (var line in lines.OrderBy(l => l.Box.Top))
        {
            var home = groups.LastOrDefault(g => RunsOn(g, line));
            if (home is null)
                groups.Add([line]);
            else
                home.Add(line);
        }
        return groups.Select(g =>
        {
            var box = g[0].Box;
            foreach (var l in g.Skip(1))
                box.Union(l.Box);
            bool rtl = g.Sum(l => l.RightToLeft ? l.Text.Length : 0) * 2 > g.Sum(l => l.Text.Length);
            return new PictureBlock
            {
                Source = string.Join(' ', g.Select(l => l.Text)),
                Lines = g.Select(l => l.Box).ToList(),
                Box = box,
                LineHeight = g.Average(l => l.Height),
                Align = AlignOf(g, pictureWidth, rtl),
                RightToLeft = rtl,
            };
        }).ToList();
    }

    private static bool RunsOn(List<ReadLine> group, ReadLine next)
    {
        var last = group[^1];
        double height = Math.Min(last.Box.Height, next.Box.Height);
        if (Math.Max(last.Box.Height, next.Box.Height) > height * 1.3)
            return false;
        double gap = next.Box.Top - last.Box.Bottom;
        if (gap > height * 0.9 || gap < -height * 0.4)
            return false;
        if (last.Words < 3 || last.Box.Width < last.Height * 10 || last.Text.TrimEnd() is { Length: > 0 } t && t[^1] is '.' or ':' or '!' or '?' or '؟' or '۔' or '؛')
            return false;
        double overlap = Math.Min(last.Box.Right, next.Box.Right) - Math.Max(last.Box.Left, next.Box.Left);
        if (overlap < 0.5 * Math.Min(last.Box.Width, next.Box.Width))
            return false;
        bool sameEdge = Math.Abs(last.Box.Left - next.Box.Left) < height || Math.Abs(last.Box.Right - next.Box.Right) < height
            || Math.Abs(Mid(last.Box) - Mid(next.Box)) < height;
        if (!sameEdge)
            return false;
        // The first word of the next line wouldn't have fitted at the end of this one.
        double widest = group.Append(next).Max(l => l.Box.Width);
        double firstWord = next.Box.Width / Math.Max(1, next.Words);
        return last.Box.Width + firstWord + height * 0.3 > widest;
    }

    private static double Mid(Rect r) => r.Left + r.Width / 2;

    private static PictureAlign AlignOf(List<ReadLine> lines, int pictureWidth, bool rtl)
    {
        if (lines.Count == 1)
            return rtl ? PictureAlign.Right : PictureAlign.Left; // Until its surroundings are seen.
        double Spread(Func<Rect, double> edge) => lines.Max(l => edge(l.Box)) - lines.Min(l => edge(l.Box));
        double left = Spread(r => r.Left), right = Spread(r => r.Right), centre = Spread(Mid);
        // A paragraph's last line is short: its own edges say less than its start.
        if (centre < left && centre < right)
            return PictureAlign.Centre;
        return left <= right ? PictureAlign.Left : PictureAlign.Right;
    }

    /// <summary>Reads a block's look off the picture: the colour around the text, the colour of the text, how heavy it is.</summary>
    /// <param name="standsOut">The picture with its background taken out, dark where something stands out from its neighbourhood.</param>
    internal static void Look(PictureBlock block, byte[] bgra, int width, int height, byte[]? standsOut = null)
    {
        var around = new List<(byte B, byte G, byte R, byte A)>();
        foreach (var line in block.Lines)
        {
            int margin = Math.Max(2, (int)(line.Height * 0.3));
            var outer = Clip(Grow(line, margin), width, height);
            var inner = Grow(line, Math.Max(1, margin / 2));
            for (int y = outer.Top; y < outer.Bottom; y++)
                for (int x = outer.Left; x < outer.Right; x++)
                {
                    if (x >= inner.Left && x < inner.Right && y >= inner.Top && y < inner.Bottom)
                        continue;
                    if (block.Lines.Any(l => l.Contains(new Point(x, y))))
                        continue;
                    int i = (y * width + x) * 4;
                    around.Add((bgra[i], bgra[i + 1], bgra[i + 2], bgra[i + 3]));
                }
        }
        if (around.Count == 0)
            return;
        byte Median(Func<(byte B, byte G, byte R, byte A), byte> part)
        {
            var values = around.Select(part).ToArray();
            Array.Sort(values);
            return values[values.Length / 2];
        }
        byte paperB = Median(p => p.B), paperG = Median(p => p.G), paperR = Median(p => p.R);
        block.Paper = Color.FromRgb(paperR, paperG, paperB);
        block.Plain = Smooth(block, bgra, width, height);

        // The text is what stands furthest from that colour inside the boxes.
        var inside = new List<(byte B, byte G, byte R, int D)>();
        foreach (var line in block.Lines)
        {
            var box = Clip(Grow(line, 0), width, height);
            for (int y = box.Top; y < box.Bottom; y++)
                for (int x = box.Left; x < box.Right; x++)
                {
                    int i = (y * width + x) * 4;
                    inside.Add((bgra[i], bgra[i + 1], bgra[i + 2], Distance(bgra[i], bgra[i + 1], bgra[i + 2], paperB, paperG, paperR)));
                }
        }
        if (inside.Count == 0)
            return;
        int furthest = inside.Max(p => p.D);
        var ink = inside.Where(p => p.D >= furthest * 0.6).ToList();
        if (!block.Plain && standsOut is not null)
        {
            // On a photograph the colour furthest from the middle one may just be the sky: the letters are what stands out from what is right beside them.
            var sharp = new List<(byte B, byte G, byte R, int D)>();
            var strength = new List<byte>();
            foreach (var line in block.Lines)
            {
                var box = Clip(line, width, height);
                for (int y = box.Top; y < box.Bottom; y++)
                    for (int x = box.Left; x < box.Right; x++)
                        strength.Add(standsOut[(y * width + x) * 4]);
            }
            strength.Sort();
            if (strength.Count > 0)
            {
                byte cutoff = strength[strength.Count / 8];
                foreach (var line in block.Lines)
                {
                    var box = Clip(line, width, height);
                    for (int y = box.Top; y < box.Bottom; y++)
                        for (int x = box.Left; x < box.Right; x++)
                        {
                            int i = (y * width + x) * 4;
                            if (standsOut[i] <= cutoff)
                                sharp.Add((bgra[i], bgra[i + 1], bgra[i + 2], 0));
                        }
                }
                if (sharp.Count > 0)
                    ink = sharp;
            }
        }
        if (furthest < 60)
        {
            // Too faint to take a colour from: whichever of black and white reads on it.
            block.Ink = Luminance(paperR, paperG, paperB) > 128 ? Colors.Black : Colors.White;
        }
        else
        {
            byte Middle(Func<(byte B, byte G, byte R, int D), byte> part)
            {
                var values = ink.Select(part).ToArray();
                Array.Sort(values);
                return values[values.Length / 2];
            }
            block.Ink = Color.FromRgb(Middle(p => p.R), Middle(p => p.G), Middle(p => p.B));
        }
        block.Bold = Stroke(block, bgra, width, height, paperB, paperG, paperR, furthest) >= 0.125;
    }

    /// <summary>
    /// Whether what surrounds the text changes only slowly, a pixel to the next, whatever colour it is: a flat colour or a
    /// gradient can be painted across, a photo or a pattern has to be rebuilt.
    /// </summary>
    private static bool Smooth(PictureBlock block, byte[] bgra, int width, int height)
    {
        var steps = new List<int>();
        foreach (var line in block.Lines)
        {
            int margin = Math.Max(2, (int)Math.Ceiling(line.Height * 0.2));
            var outer = Clip(Grow(line, margin), width, height);
            void Row(int y)
            {
                if (y < 0 || y >= height)
                    return;
                for (int x = outer.Left; x + 2 < outer.Right; x += 2)
                {
                    int a = (y * width + x) * 4, b = (y * width + x + 2) * 4;
                    steps.Add(Distance(bgra[a], bgra[a + 1], bgra[a + 2], bgra[b], bgra[b + 1], bgra[b + 2]));
                }
            }
            void Column(int x)
            {
                if (x < 0 || x >= width)
                    return;
                for (int y = outer.Top; y + 2 < outer.Bottom; y += 2)
                {
                    int a = (y * width + x) * 4, b = ((y + 2) * width + x) * 4;
                    steps.Add(Distance(bgra[a], bgra[a + 1], bgra[a + 2], bgra[b], bgra[b + 1], bgra[b + 2]));
                }
            }
            Row(outer.Top);
            Row(outer.Bottom - 1);
            Column(outer.Left);
            Column(outer.Right - 1);
        }
        if (steps.Count == 0)
            return true;
        steps.Sort();
        // Compression and a little grain are allowed for; a hard edge here and there (a button's corner) is not enough to say it isn't plain, a lot of them is.
        return steps[(int)(steps.Count * 0.95)] <= 16;
    }

    /// <summary>How thick the strokes are, against the height of the line: the narrower of the runs of ink across and down.</summary>
    private static double Stroke(PictureBlock block, byte[] bgra, int width, int height, int paperB, int paperG, int paperR, int furthest)
    {
        double cut = furthest * 0.5;
        var across = new List<int>();
        var down = new List<int>();
        foreach (var line in block.Lines)
        {
            var box = Clip(line, width, height);
            bool Ink(int x, int y)
            {
                int i = (y * width + x) * 4;
                return Distance(bgra[i], bgra[i + 1], bgra[i + 2], paperB, paperG, paperR) >= cut;
            }
            for (int y = box.Top; y < box.Bottom; y++)
            {
                int run = 0;
                for (int x = box.Left; x < box.Right; x++)
                {
                    if (Ink(x, y))
                        run++;
                    else if (run > 0)
                    {
                        across.Add(run);
                        run = 0;
                    }
                }
            }
            for (int x = box.Left; x < box.Right; x++)
            {
                int run = 0;
                for (int y = box.Top; y < box.Bottom; y++)
                {
                    if (Ink(x, y))
                        run++;
                    else if (run > 0)
                    {
                        down.Add(run);
                        run = 0;
                    }
                }
            }
        }
        if (across.Count == 0 || down.Count == 0)
            return 0;
        across.Sort();
        down.Sort();
        return Math.Min(across[across.Count / 2], down[down.Count / 2]) / Math.Max(1, SizeOf(block.Source, block.LineHeight));
    }

    /// <summary>The font size a line of this height is set in: its box reaches from its tallest letter to its lowest, and which those are depends on its letters.</summary>
    internal static double SizeOf(string text, double height)
    {
        if (text.Any(c => c is >= (char)0x0590 and <= (char)0x08FF))
            return height / 1.25;
        return text.Any(c => c is 'g' or 'j' or 'p' or 'q' or 'y' or ',' or ';' or 'ç' or 'ş' or 'ğ' or 'Ç' or 'Ş' or '(' or ')') ? height / 0.95 : height / 0.74;
    }

    /// <summary>
    /// What can only be seen with all the text in view. Text of one colour and about one size is one style, set in one
    /// size. Lines starting at one edge are a column. A single line in the middle of the shape it sits in is centred.
    /// </summary>
    internal static void Arrange(IReadOnlyList<PictureBlock> blocks, int width, int height, byte[]? bgra)
    {
        foreach (var block in blocks)
        {
            block.Size = block.Lines.Count == 1 ? SizeOf(block.Source, block.LineHeight) : SizeOf(block.Source, block.LineHeight) * 0.98;
            block.Column = block.Box;
            block.Shape = bgra is null ? new Rect(0, 0, width, height) : ShapeAround(block, bgra, width, height);
        }
        var styles = new Dictionary<PictureBlock, (double Size, bool Bold)>();
        foreach (var block in blocks)
        {
            var mates = blocks.Where(o => Near(o.Ink, block.Ink) && Math.Max(o.Size, block.Size) <= Math.Min(o.Size, block.Size) * 1.4).ToList();
            var sizes = mates.Select(o => o.Size).Order().ToList();
            styles[block] = (sizes[sizes.Count / 2], mates.Count(o => o.Bold) * 2 > mates.Count);
        }
        foreach (var (block, style) in styles)
        {
            block.Size = Math.Max(block.Size * 0.8, Math.Min(block.Size * 1.2, style.Size));
            block.Bold = style.Bold;
        }
        foreach (var block in blocks.Where(b => b.Lines.Count == 1))
        {
            var shape = block.Shape;
            double before = block.Box.Left - shape.Left, after = shape.Right - block.Box.Right;
            bool middle = Math.Abs(before - after) <= Math.Max(0.12 * (before + after), 0.6 * block.Size) && before + after > 0.6 * block.Size && block.Box.Width < shape.Width * 0.85;
            if (middle)
                block.Align = PictureAlign.Centre;
        }
        foreach (var block in blocks.Where(b => b.Align != PictureAlign.Centre))
        {
            bool left = block.Align == PictureAlign.Left;
            var mates = blocks.Where(o => o.Align == block.Align
                && Math.Abs((left ? o.Box.Left : o.Box.Right) - (left ? block.Box.Left : block.Box.Right)) < Math.Max(block.Size, o.Size) * 0.6).ToList();
            double from = mates.Min(o => o.Box.Left), to = mates.Max(o => o.Box.Right);
            block.Column = new Rect(from, block.Box.Top, Math.Max(1, to - from), block.Box.Height);
        }
    }

    private static bool Near(Color a, Color b) => Distance(a.B, a.G, a.R, b.B, b.G, b.R) <= 60;

    /// <summary>How far the colour around a line goes on either way until an edge: the button or the band it sits in.</summary>
    private static Rect ShapeAround(PictureBlock block, byte[] bgra, int width, int height)
    {
        if (!block.Plain)
            return new Rect(0, 0, width, height);
        var box = block.Box;
        int margin = Math.Max(2, (int)Math.Ceiling(block.LineHeight * 0.25));
        int mx = Math.Clamp((int)(box.Left + box.Width / 2), 0, width - 1);
        int above = Math.Clamp((int)box.Top - margin, 0, height - 1), below = Math.Clamp((int)box.Bottom + margin, 0, height - 1);
        bool Step(int x1, int y1, int x2, int y2)
        {
            int a = (y1 * width + x1) * 4, b = (y2 * width + x2) * 4;
            return Distance(bgra[a], bgra[a + 1], bgra[a + 2], bgra[b], bgra[b + 1], bgra[b + 2]) <= 14;
        }
        int Walk(int row, int start, int direction)
        {
            int x = start;
            while (x + direction >= 0 && x + direction < width && Step(x, row, x + direction, row))
                x += direction;
            return x;
        }
        int left = Math.Min(Walk(above, Math.Clamp((int)box.Left, 0, width - 1), -1), Walk(below, Math.Clamp((int)box.Left, 0, width - 1), -1));
        int right = Math.Max(Walk(above, Math.Clamp((int)box.Right, 0, width - 1), 1), Walk(below, Math.Clamp((int)box.Right, 0, width - 1), 1));
        int top = above, bottom = below;
        while (top > 0 && Step(mx, top, mx, top - 1))
            top--;
        while (bottom < height - 1 && Step(mx, bottom, mx, bottom + 1))
            bottom++;
        return new Rect(left, top, Math.Max(1, right - left + 1), Math.Max(1, bottom - top + 1));
    }

    private static int Distance(int b1, int g1, int r1, int b2, int g2, int r2) => Math.Abs(b1 - b2) + Math.Abs(g1 - g2) + Math.Abs(r1 - r2);

    private static double Luminance(int r, int g, int b) => 0.299 * r + 0.587 * g + 0.114 * b;

    private static Rect Grow(Rect r, double by) => new(r.X - by, r.Y - by, r.Width + 2 * by, r.Height + 2 * by);

    private readonly record struct Area(int Left, int Top, int Right, int Bottom)
    {
        public int Width => Right - Left;

        public int Height => Bottom - Top;
    }

    private static Area Clip(Rect r, int width, int height) => new(
        Math.Clamp((int)Math.Floor(r.Left), 0, width), Math.Clamp((int)Math.Floor(r.Top), 0, height),
        Math.Clamp((int)Math.Ceiling(r.Right), 0, width), Math.Clamp((int)Math.Ceiling(r.Bottom), 0, height));

    /// <summary>Whether a block has words to translate and was read cleanly enough to trust.</summary>
    public static bool Trusted(string text)
    {
        int letters = text.Count(char.IsLetter);
        int marks = text.Count(c => !char.IsLetterOrDigit(c) && !char.IsWhiteSpace(c) && c is not ('.' or ',' or '\'' or '-' or ':' or '!' or '?' or '؟' or '،' or '؛' or '(' or ')' or '"' or '%' or '&' or '/'));
        if (letters < 2 || marks * 3 > letters)
            return false;
        // A line read badly falls into crumbs: many words of a letter or two, or a stray letter of another script.
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length >= 4 && (double)letters / words.Length < 2.3)
            return false;
        int arabic = text.Count(c => c is >= (char)0x0600 and <= (char)0x08FF), latin = text.Count(char.IsAsciiLetter);
        return Math.Min(arabic, latin) is 0 or > 2;
    }

    /// <summary>Puts the blocks into the other language; a block with nothing to translate, or that came back the same, stays as it is.</summary>
    public static async Task TranslateAsync(PictureWork work, string from, string into, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var chosen = work.Blocks.Where(b => Trusted(b.Source)).ToList();
        foreach (var block in work.Blocks)
            block.Keep = true;
        var said = Cloud.CloudAi.CanTranslate
            ? await Translator.TranslateAsync(chosen.Select(b => b.Source).ToList(), from, into, progress, cancellationToken).ConfigureAwait(false)
            : await ByModelAsync(chosen, from, into, progress, cancellationToken).ConfigureAwait(false);
        for (int i = 0; i < chosen.Count && i < said.Count; i++)
        {
            var text = said[i].Trim();
            chosen[i].Translation = text;
            chosen[i].Keep = text.Length == 0 || Same(text, chosen[i].Source);
        }
    }

    /// <summary>Each block in turn, with the rest of the picture to read it by: a word on a sign means what the sign is about.</summary>
    private static async Task<IReadOnlyList<string>> ByModelAsync(List<PictureBlock> blocks, string from, string into, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var said = new List<string>(blocks.Count);
        try
        {
            for (int i = 0; i < blocks.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var text = await GemmaTranslation.TranslateAsync(blocks[i].Source, from, into, Around(blocks, i), false, cancellationToken, picture: true).ConfigureAwait(false);
                said.Add(into == "ar" ? Translator.Tidy(text) : text);
                progress?.Report((i + 1.0) / blocks.Count);
            }
        }
        finally
        {
            GemmaTranslation.Finished();
        }
        return said;
    }

    /// <summary>The other text on the picture, nearest first, up to what is worth reading.</summary>
    internal static IReadOnlyList<string> Around(IReadOnlyList<PictureBlock> blocks, int index)
    {
        var around = new List<string>();
        int room = 700;
        foreach (var other in Enumerable.Range(0, blocks.Count).Where(i => i != index).OrderBy(i => Math.Abs(i - index)))
        {
            var text = blocks[other].Source;
            if (text.Length > room)
                break;
            room -= text.Length;
            around.Add(text);
        }
        return around;
    }

    private static bool Same(string a, string b)
    {
        static string Bare(string s) => new string(s.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        return Bare(a) == Bare(b);
    }

    /// <summary>Takes the text off the picture; what stays (a number, a name) stays on it.</summary>
    public static async Task EraseAsync(PictureWork work, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var clean = (byte[])work.Original.Clone();
        await Task.Run(() =>
        {
            var hard = new List<Area>();
            var inks = new Dictionary<Area, Color>();
            foreach (var block in work.Blocks.Where(b => !b.Keep))
            {
                foreach (var line in block.Lines)
                {
                    int margin = Math.Max(1, (int)Math.Ceiling(line.Height * 0.14));
                    var area = Clip(Grow(line, margin), work.Width, work.Height);
                    if (block.Plain)
                        Paint(clean, work.Width, work.Height, area);
                    else
                        foreach (var piece in Pieces(area))
                        {
                            hard.Add(piece);
                            inks[piece] = block.Ink;
                        }
                }
            }
            if (hard.Count > 0)
                Rebuild(clean, work.Width, work.Height, hard, inks, work.StandsOut, progress, cancellationToken);
        }, cancellationToken).ConfigureAwait(false);
        work.Clean = clean;
    }

    /// <summary>
    /// Paints over a piece of plain background from the colours just outside it: a line of text is wider than tall, so
    /// each column goes from what is above to what is below it, and a gradient or a change of colour along the line carries on.
    /// </summary>
    private static void Paint(byte[] pixels, int width, int height, Area area)
    {
        if (area.Width <= 0 || area.Height <= 0)
            return;
        bool down = area.Width >= area.Height;
        int along = down ? area.Width : area.Height, across = down ? area.Height : area.Width;
        var source = new byte[area.Width * area.Height * 4];
        var near = new double[4];
        var far = new double[4];
        // What lies just outside, a little smoothed along the edge so a stray dot of a letter doesn't draw a streak.
        void Edge(int position, bool first, double[] into)
        {
            Array.Clear(into);
            int count = 0;
            for (int step = -2; step <= 2; step++)
            {
                int p = Math.Clamp(position + step, 0, along - 1);
                int x, y;
                if (down)
                {
                    x = area.Left + p;
                    y = first ? area.Top - 1 : area.Bottom;
                }
                else
                {
                    x = first ? area.Left - 1 : area.Right;
                    y = area.Top + p;
                }
                if (x < 0 || y < 0 || x >= width || y >= height)
                    continue;
                int i = (y * width + x) * 4;
                for (int c = 0; c < 4; c++)
                    into[c] += pixels[i + c];
                count++;
            }
            if (count > 0)
                for (int c = 0; c < 4; c++)
                    into[c] /= count;
            else
                Array.Fill(into, -1);
        }
        for (int p = 0; p < along; p++)
        {
            Edge(p, true, near);
            Edge(p, false, far);
            if (near[0] < 0)
                Array.Copy(far, near, 4);
            if (far[0] < 0)
                Array.Copy(near, far, 4);
            for (int q = 0; q < across; q++)
            {
                double share = (q + 0.5) / across;
                int x = down ? p : q, y = down ? q : p;
                int i = (y * area.Width + x) * 4;
                for (int c = 0; c < 4; c++)
                    source[i + c] = (byte)Math.Clamp(near[c] * (1 - share) + far[c] * share + 0.5, 0, 255);
            }
        }
        for (int y = 0; y < area.Height; y++)
            Buffer.BlockCopy(source, y * area.Width * 4, pixels, ((area.Top + y) * width + area.Left) * 4, area.Width * 4);
    }

    /// <summary>A long line cut into pieces the eraser sees up close.</summary>
    private static IEnumerable<Area> Pieces(Area area)
    {
        double most = Math.Max(WidestPiece, area.Height * 3.0);
        int count = Math.Max(1, (int)Math.Ceiling(area.Width / most));
        for (int i = 0; i < count; i++)
            yield return area with { Left = area.Left + area.Width * i / count, Right = area.Left + area.Width * (i + 1) / count };
    }

    /// <summary>
    /// Rebuilds what is behind the pieces with LaMa, one square of the picture at a time. All the text inside a square is
    /// masked, also what only reaches into it, or the model takes those letters for a pattern and carries it on.
    /// </summary>
    private static void Rebuild(byte[] pixels, int width, int height, List<Area> pieces, Dictionary<Area, Color> inks, byte[]? standsOut, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        // Only the letters and a little around them go, so as much of the photograph as possible stays the photograph.
        var letters = new byte[width * height];
        foreach (var piece in pieces)
        {
            MarkLetters(letters, standsOut, width, piece);
            MarkInk(letters, pixels, width, piece, inks[piece]);
        }
        var left = new List<Area>(pieces);
        int all = pieces.Count;
        while (left.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var piece = left[0];
            int side = Math.Clamp(Math.Max(piece.Width, piece.Height) * 2 + 64, 256, Math.Max(width, height));
            int w = Math.Min(side, width), h = Math.Min(side, height);
            int x0 = Math.Clamp((piece.Left + piece.Right - w) / 2, 0, width - w), y0 = Math.Clamp((piece.Top + piece.Bottom - h) / 2, 0, height - h);
            var mask = new byte[w * h];
            var inside = left.Where(p => p.Left >= x0 && p.Right <= x0 + w && p.Top >= y0 && p.Bottom <= y0 + h).Append(piece).Distinct().ToList();
            foreach (var p in pieces)
            {
                int a = Math.Max(p.Left, x0), b = Math.Min(p.Right, x0 + w);
                if (b <= a)
                    continue;
                for (int y = Math.Max(p.Top, y0); y < Math.Min(p.Bottom, y0 + h); y++)
                    Buffer.BlockCopy(letters, y * width + a, mask, (y - y0) * w + a - x0, b - a);
            }
            var tile = new byte[w * h * 4];
            for (int y = 0; y < h; y++)
                Buffer.BlockCopy(pixels, ((y0 + y) * width + x0) * 4, tile, y * w * 4, w * 4);
            using (var image = new MagickImage())
            {
                image.ReadPixels(tile, new PixelReadSettings((uint)w, (uint)h, StorageType.Char, PixelMapping.BGRA));
                using var filled = ObjectEraser.Erase(image, mask);
                var result = filled.GetPixels().ToByteArray(PixelMapping.BGRA)!;
                for (int y = 0; y < h; y++)
                    Buffer.BlockCopy(result, y * w * 4, pixels, ((y0 + y) * width + x0) * 4, w * 4);
            }
            left.RemoveAll(inside.Contains);
            progress?.Report(1 - (double)left.Count / all);
        }
    }

    /// <summary>Marks what has the colour of the text too: white letters on a pale sky hardly stand out from it, but they are still white.</summary>
    private static void MarkInk(byte[] letters, byte[] pixels, int width, Area piece, Color ink)
    {
        int grow = Math.Max(2, piece.Height / 10);
        for (int y = piece.Top; y < piece.Bottom; y++)
            for (int x = piece.Left; x < piece.Right; x++)
            {
                int i = (y * width + x) * 4;
                if (Distance(pixels[i], pixels[i + 1], pixels[i + 2], ink.B, ink.G, ink.R) > 70)
                    continue;
                int left = Math.Max(piece.Left, x - grow), right = Math.Min(piece.Right - 1, x + grow);
                for (int yy = Math.Max(piece.Top, y - grow); yy <= Math.Min(piece.Bottom - 1, y + grow); yy++)
                    Array.Fill(letters, (byte)255, yy * width + left, right - left + 1);
            }
    }

    /// <summary>Marks what is to go in a piece: what stands out most in it, grown a little; all of it when nothing says where the letters are.</summary>
    private static void MarkLetters(byte[] letters, byte[]? standsOut, int width, Area piece)
    {
        if (standsOut is null || piece.Width <= 0 || piece.Height <= 0)
        {
            for (int y = piece.Top; y < piece.Bottom; y++)
                Array.Fill(letters, (byte)255, y * width + piece.Left, piece.Width);
            return;
        }
        // Judged a little at a time along the line: pale letters on a pale sky stand out less than a dark fold further along it does.
        int grow = Math.Max(2, piece.Height / 10);
        int stretch = Math.Max(8, piece.Height * 2);
        for (int from = 0; from < piece.Width; from += stretch)
        {
            int count = Math.Min(stretch, piece.Width - from);
            var values = new byte[count * piece.Height];
            for (int y = 0; y < piece.Height; y++)
                for (int x = 0; x < count; x++)
                    values[y * count + x] = standsOut[((piece.Top + y) * width + piece.Left + from + x) * 4];
            var sorted = (byte[])values.Clone();
            Array.Sort(sorted);
            double cutoff = (sorted[sorted.Length / 20] + sorted[sorted.Length / 2]) / 2.0;
            if (sorted[sorted.Length / 2] - sorted[sorted.Length / 20] < 12)
            {
                // Nothing stands out enough to tell letters by: faint letters may be here, and the whole stretch is safer gone than half of them.
                for (int y = 0; y < piece.Height; y++)
                    Array.Fill(letters, (byte)255, (piece.Top + y) * width + piece.Left + from, count);
                continue;
            }
            for (int y = 0; y < piece.Height; y++)
                for (int x = 0; x < count; x++)
                {
                    if (values[y * count + x] > cutoff)
                        continue;
                    int left = Math.Max(0, from + x - grow), right = Math.Min(piece.Width - 1, from + x + grow);
                    for (int dy = Math.Max(0, y - grow); dy <= Math.Min(piece.Height - 1, y + grow); dy++)
                        Array.Fill(letters, (byte)255, (piece.Top + dy) * width + piece.Left + left, right - left + 1);
                }
        }
    }

    /// <summary>The picture with the translations on it, as BGRA pixels. <see cref="EraseAsync"/> comes first.</summary>
    public static byte[] Compose(PictureWork work, string into) => OnSta(() => Draw(work, into));

    private static T OnSta<T>(Func<T> job)
    {
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
            return job();
        T? result = default;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { result = job(); }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        thread.Join();
        if (error is not null)
            throw error;
        return result!;
    }

    private static byte[] Draw(PictureWork work, string into)
    {
        var clean = work.Clean ?? throw new InvalidOperationException("The text hasn't been taken off yet.");
        var canvas = (byte[])clean.Clone();
        bool rtl = into is "ar" or "fa" or "ur";
        var drawn = work.Blocks.Where(b => !b.Keep && b.Translation.Trim().Length > 0).ToList();
        // A block the user went back to keeping shows the picture as it was.
        foreach (var block in work.Blocks.Where(b => b.Keep))
            foreach (var line in block.Lines)
            {
                var area = Clip(Grow(line, Math.Max(1, Math.Ceiling(line.Height * 0.14))), work.Width, work.Height);
                for (int y = area.Top; y < area.Bottom; y++)
                    Buffer.BlockCopy(work.Original, (y * work.Width + area.Left) * 4, canvas, (y * work.Width + area.Left) * 4, area.Width * 4);
            }
        var culture = CultureInfo.GetCultureInfo(into);
        foreach (var block in drawn)
        {
            var typeface = new Typeface(new FontFamily(Font), FontStyles.Normal, block.Bold ? FontWeights.SemiBold : FontWeights.Normal, FontStretches.Normal);
            // The text keeps to the side the original kept to; going to a language that reads the other way, to the other side of its column.
            var side = block.Align;
            if (block.RightToLeft != rtl && side != PictureAlign.Centre)
                side = side == PictureAlign.Left ? PictureAlign.Right : PictureAlign.Left;
            double start = block.Align == PictureAlign.Centre ? block.Box.Left : side == PictureAlign.Left ? block.Column.Left : block.Box.Left;
            double end = block.Align == PictureAlign.Centre ? block.Box.Right : side == PictureAlign.Right ? block.Column.Right : block.Box.Right;
            if (side == block.Align)
            {
                start = block.Box.Left;
                end = block.Box.Right;
            }
            var room = Room(block, work.Blocks, work.Width, work.Height);
            // A column's far side is no place to go when something else stands in the way on this line.
            start = Math.Max(start, Math.Min(room.Left, block.Box.Left));
            end = Math.Min(end, Math.Max(room.Right, block.Box.Right));
            var centre = block.Align == PictureAlign.Centre && block.Lines.Count == 1 ? block.Shape.Left + block.Shape.Width / 2 : block.Box.Left + block.Box.Width / 2;
            double most = side switch
            {
                PictureAlign.Left => room.Right - start,
                PictureAlign.Right => end - room.Left,
                _ => 2 * Math.Min(centre - room.Left, room.Right - centre),
            };
            var fit = Fit(block, Math.Max(1, most), Math.Max(1, room.Bottom - block.Box.Top), typeface, culture, rtl);
            block.Tight = fit.Tight;
            double x = side switch
            {
                PictureAlign.Left => start,
                PictureAlign.Right => end - fit.Width,
                _ => centre - fit.Width / 2,
            };
            Blend(canvas, work.Width, work.Height, block, fit, x, side, rtl, typeface, culture);
        }
        return canvas;
    }

    /// <summary>How far a block's text may reach: up to the next text, the edge of the shape it sits in, or the picture's edge.</summary>
    private static Rect Room(PictureBlock block, IReadOnlyList<PictureBlock> all, int width, int height)
    {
        double pad = Math.Max(2, block.LineHeight * 0.3);
        double left = Math.Max(pad, block.Shape.Left + (block.Shape.Width < width ? pad : 0));
        double right = Math.Min(width - pad, block.Shape.Right - (block.Shape.Width < width ? pad : 0));
        double bottom = Math.Min(height - pad, block.Shape.Bottom - (block.Shape.Height < height ? pad / 2 : 0));
        foreach (var other in all)
        {
            if (ReferenceEquals(other, block))
                continue;
            var o = other.Box;
            if (o.Bottom > block.Box.Top && o.Top < block.Box.Bottom)
            {
                if (o.Right <= block.Box.Left)
                    left = Math.Max(left, o.Right + pad);
                else if (o.Left >= block.Box.Right)
                    right = Math.Min(right, o.Left - pad);
            }
            else if (o.Top >= block.Box.Bottom && o.Right > block.Box.Left && o.Left < block.Box.Right)
                bottom = Math.Min(bottom, o.Top - pad / 2);
        }
        return new Rect(left, block.Box.Top, Math.Max(1, right - left), Math.Max(1, bottom - block.Box.Top));
    }

    private readonly record struct Fitted(double Size, double Width, double Height, bool Tight);

    /// <summary>The largest size at which the text fits: first in the place the original had, then with the free room around it.</summary>
    private static Fitted Fit(PictureBlock block, double mostWidth, double mostHeight, Typeface typeface, CultureInfo culture, bool rtl)
    {
        double start = Math.Max(6, block.Size);
        double boxWidth = Math.Min(mostWidth, Math.Max(Math.Min(block.Column.Width, mostWidth), block.LineHeight * 2)), boxHeight = Math.Max(block.Box.Height * 1.25 + 2, start * 1.5);
        double freeWidth = Math.Min(mostWidth, Math.Max(boxWidth * 1.5, boxWidth + block.LineHeight * 6));
        double freeHeight = Math.Max(boxHeight, mostHeight);
        bool Fits(double size, double w, double h)
        {
            var text = Format(block.Translation, size, w, typeface, culture, rtl, block.Ink);
            return text.Height <= h && text.MinWidth <= w;
        }
        for (double scale = 1; scale >= 0.85; scale -= 0.05)
            if (Fits(start * scale, boxWidth, boxHeight))
                return new Fitted(start * scale, boxWidth, boxHeight, false);
        for (double scale = 1; scale >= 0.8; scale -= 0.05)
            if (Fits(start * scale, freeWidth, boxHeight))
                return new Fitted(start * scale, freeWidth, boxHeight, false);
        for (double scale = 1; scale >= 0.7; scale -= 0.05)
            if (Fits(start * scale, boxWidth, freeHeight))
                return new Fitted(start * scale, boxWidth, freeHeight, false);
        for (double scale = 1; scale >= 0.7; scale -= 0.05)
            if (Fits(start * scale, freeWidth, freeHeight))
                return new Fitted(start * scale, freeWidth, freeHeight, false);
        for (double scale = 0.65; scale >= 0.2; scale -= 0.05)
        {
            double size = Math.Max(6, start * scale);
            if (Fits(size, freeWidth, freeHeight))
                return new Fitted(size, freeWidth, freeHeight, true);
        }
        return new Fitted(Math.Max(6, start * 0.2), freeWidth, freeHeight, true);
    }

    private static FormattedText Format(string text, double size, double width, Typeface typeface, CultureInfo culture, bool rtl, Color ink)
    {
        var brush = new SolidColorBrush(ink);
        brush.Freeze();
        return new FormattedText(text, culture, rtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight, typeface, size, brush, 1.0)
        {
            MaxTextWidth = Math.Max(1, width),
            TextAlignment = TextAlignment.Left,
        };
    }

    /// <summary>Draws one block's text and lays it over the picture.</summary>
    private static void Blend(byte[] canvas, int width, int height, PictureBlock block, Fitted fit, double x, PictureAlign side, bool rtl, Typeface typeface, CultureInfo culture)
    {
        var text = Format(block.Translation, fit.Size, fit.Width, typeface, culture, rtl, block.Ink);
        // Within a flow that reads from the right, WPF's Left is the right-hand side.
        text.TextAlignment = side == PictureAlign.Centre ? TextAlignment.Center
            : (side == PictureAlign.Left) != rtl ? TextAlignment.Left : TextAlignment.Right;
        x = Math.Clamp(x, 0, Math.Max(0, width - fit.Width));
        // A single line goes where the original's letters were, by its own letters and not its line's empty space.
        double middle = block.Box.Top + block.Box.Height / 2;
        double inkBottom = text.Height + text.OverhangAfter, inkTop = inkBottom - text.Extent;
        double y = text.Height <= block.Box.Height * 1.25 + 2 || text.Height <= fit.Size * 1.5 ? middle - (inkTop + inkBottom) / 2 : block.Box.Top;
        y = Math.Clamp(y, -inkTop, Math.Max(0, height - inkBottom));

        int w = (int)Math.Ceiling(fit.Width) + 2, h = (int)Math.Ceiling(text.Height) + 2;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
            dc.DrawText(text, new Point(1, 1));
        TextOptions.SetTextFormattingMode(visual, TextFormattingMode.Ideal);
        var target = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        var layer = new byte[w * h * 4];
        target.CopyPixels(layer, w * 4, 0);
        int ox = (int)Math.Round(x), oy = (int)Math.Round(y);
        for (int row = 0; row < h; row++)
        {
            int ty = oy + row;
            if (ty < 0 || ty >= height)
                continue;
            for (int col = 0; col < w; col++)
            {
                int tx = ox + col;
                if (tx < 0 || tx >= width)
                    continue;
                int s = (row * w + col) * 4, t = (ty * width + tx) * 4;
                int a = layer[s + 3];
                if (a == 0)
                    continue;
                for (int c = 0; c < 3; c++)
                    canvas[t + c] = (byte)Math.Min(255, layer[s + c] + canvas[t + c] * (255 - a) / 255);
                canvas[t + 3] = (byte)Math.Min(255, a + canvas[t + 3] * (255 - a) / 255);
            }
        }
    }

    /// <summary>The pixels as a picture WPF can show.</summary>
    public static BitmapSource Show(byte[] bgra, int width, int height)
    {
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, bgra, width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>Saves the pixels in the output's format, with the picture's colour profile.</summary>
    public static async Task SaveAsync(PictureWork work, byte[] bgra, string output, CancellationToken cancellationToken = default)
    {
        using var image = new MagickImage();
        image.ReadPixels(bgra, new PixelReadSettings((uint)work.Width, (uint)work.Height, StorageType.Char, PixelMapping.BGRA));
        if (work.Profile is { } profile)
            image.SetProfile(profile);
        await ObjectEraser.SaveAsync(image, output, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>A picture file into another language, as a new file. Returns how many pieces of text were translated.</summary>
    public static async Task<int> TranslateFileAsync(string input, string output, string from, string into, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var work = await ReadAsync(input, cancellationToken).ConfigureAwait(false);
        if (work.Blocks.Count == 0)
            throw new InvalidOperationException("No text was found. Try a sharper scan, or add its language under Windows Settings › Time & language.");
        progress?.Report(0.03);
        await TranslateAsync(work, from, into, Part(progress, 0.03, 0.75), cancellationToken).ConfigureAwait(false);
        await EraseAsync(work, Part(progress, 0.75, 0.97), cancellationToken).ConfigureAwait(false);
        var pixels = await Task.Run(() => Compose(work, into), cancellationToken).ConfigureAwait(false);
        await SaveAsync(work, pixels, output, cancellationToken).ConfigureAwait(false);
        return work.Blocks.Count(b => !b.Keep);
    }

    private static Progress<double>? Part(IProgress<double>? progress, double from, double to) =>
        progress is null ? null : new Progress<double>(f => progress.Report(from + (to - from) * Math.Clamp(f, 0, 1)));
}
