using System.Text.RegularExpressions;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using Petal.Engines.Documents.Pdf;
using Petal.Engines.Documents.Text;
using SkiaSharp;
using TextTranslator = System.Func<System.Collections.Generic.IReadOnlyList<string>, System.IProgress<double>?, System.Threading.CancellationToken, System.Threading.Tasks.Task<System.Collections.Generic.IReadOnlyList<string>>>;

namespace Petal.Engines.Documents.Translate;

/// <summary>
/// A PDF in another language, never turned into pictures. Words are read with their places (a page with no text layer with
/// OCR) and gathered into paragraphs by how they lie; each paragraph is covered in its own background colour and its
/// translation drawn in the same rectangle, in its text's colour, as large as fits; right-aligned and shaped for Arabic
/// script. A translation too long for the rectangle may spread sideways over empty page before it shrinks: a label of a word
/// or two would otherwise come out far smaller than the text around it. Pictures, drawings, links and the page's size stay.
/// The original words stay in the file, under the covers: they can no longer be seen, but they can still be found by
/// search, copied by selecting them, and read by a program, mixed in with the translation's letters where the two lie over
/// each other. A file with secrets in its original language still has them.
/// </summary>
internal static partial class PdfTranslation
{
    /// <summary>Enough to tell a paragraph's colours; a small page picture for each page.</summary>
    private const int SampleDpi = 110;

    /// <summary>Smaller than this is hard to read: the text is clipped rather than shrunk further.</summary>
    private const double SmallestSize = 6;

    private const double SizeStep = 0.95;

    [GeneratedRegex(@"^([•·▪◦●○■□►▸‣⁃–—*✓✔➢➤→∙-]|\(?\d{1,3}[.)])\s+")]
    private static partial Regex ListMark();

    public static async Task RunAsync(string input, string output, string from, string to, TextTranslator translate, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var bytes = await File.ReadAllBytesAsync(input, cancellationToken).ConfigureAwait(false);
        // Protected files stop here with a clear message, before any OCR is spent on them.
        using var document = PdfSharpIO.Open(bytes, PdfDocumentOpenMode.Modify, null, Path.GetFileName(input));
        var options = new PdfContentOptions { Pictures = false, OcrLanguages = from is "auto" or "" ? null : [from] };
        var (pages, _) = await PdfContent.ExtractAsync(input, options, Util.ScaledProgress.Of(progress, 0, 0.1), cancellationToken).ConfigureAwait(false);
        var blocks = pages.Select(Paragraphs).ToList();
        var slots = new List<TextSlot>();
        foreach (var block in blocks.SelectMany(b => b))
            slots.Add(new TextSlot(block.Words, translated =>
            {
                if (translated != block.Words)
                    block.Translation = block.Mark + translated;
            }));
        await DocumentTranslator.FillAsync(slots, translate, Util.ScaledProgress.Of(progress, 0.1, 0.9), cancellationToken).ConfigureAwait(false);
        PdfFonts.EnsureInitialized();
        bool rtl = DocumentTranslator.IsRightToLeft(to);
        for (int i = 0; i < pages.Count && i < document.PageCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Paint(document.Pages[i], pages[i], blocks[i], bytes, rtl);
            progress?.Report(0.9 + 0.09 * (i + 1) / pages.Count);
        }
        PdfSharpIO.Save(document, output);
    }

    /// <summary>A paragraph on a page: its lines' rectangle, how it is set, and its words.</summary>
    private sealed class Block(PdfLine first)
    {
        public List<PdfLine> Lines { get; } = [first];

        public string Mark { get; set; } = "";

        public string Words { get; set; } = "";

        public string? Translation { get; set; }

        public double Left => Lines.Min(l => l.Left);

        public double Right => Lines.Max(l => l.Right);

        public double Top => Lines.Min(l => l.Top);

        public double Bottom => Lines.Max(l => l.Bottom);

        public double Size => Lines.Average(l => l.FontSize);

        public IEnumerable<PdfWord> AllWords => Lines.SelectMany(l => l.Words);

        /// <summary>The distance between lines, in sizes of the letters.</summary>
        public double Leading => Lines.Count < 2 ? 1.2 : Math.Clamp((Lines[^1].Bottom - Lines[0].Bottom) / (Lines.Count - 1) / Size, 1.05, 1.6);

        public int? Colour => AllWords.Where(w => w.Color is not null).GroupBy(w => w.Color).OrderByDescending(g => g.Sum(w => w.Text.Length)).FirstOrDefault()?.Key;
    }

    /// <summary>
    /// A page's lines gathered into paragraphs: a line belongs under the one above when it is set the same way, close
    /// below it, lined up with it, and when the one above must have ended where it did because the next word didn't fit.
    /// Cells of a table stay apart that way too: a word or two side by side with others is not a line that ran out of room.
    /// </summary>
    private static List<Block> Paragraphs(PdfPageContent page)
    {
        var blocks = new List<Block>();
        if (page.Words.Count == 0)
            return blocks;
        foreach (var row in PdfLayout.BuildRows(page))
        {
            foreach (var line in row.Segments)
            {
                if (blocks.LastOrDefault(b => Continues(b, line, page)) is { } home)
                    home.Lines.Add(line);
                else
                    blocks.Add(new Block(line));
            }
        }
        foreach (var block in blocks)
        {
            var words = "";
            foreach (var line in block.Lines)
            {
                var text = line.Text.Trim();
                // A word cut at the end of a line is one word again.
                if (words.EndsWith('-') && words.Length > 1 && char.IsLetter(words[^2]) && text.Length > 0 && char.IsLower(text[0]))
                    words = words[..^1] + text;
                else
                    words = words.Length == 0 ? text : words + " " + text;
            }
            var mark = ListMark().Match(words);
            block.Mark = mark.Success ? mark.Value : "";
            block.Words = words[block.Mark.Length..];
        }
        return blocks;
    }

    private static bool Continues(Block block, PdfLine next, PdfPageContent page)
    {
        var last = block.Lines[^1];
        if (last.Row == next.Row)
            return false;
        double size = Math.Max(last.FontSize, next.FontSize);
        if (Math.Abs(last.FontSize - next.FontSize) > 0.15 * size || last.Bold != next.Bold || last.Italic != next.Italic)
            return false;
        if (last.Words[0].Color != next.Words[0].Color)
            return false;
        double gap = next.Top - last.Bottom;
        if (gap < -0.4 * size || gap > 0.75 * size)
            return false;
        double lastWidth = last.Right - last.Left, nextWidth = next.Right - next.Left;
        if (Math.Min(last.Right, next.Right) - Math.Max(last.Left, next.Left) < 0.5 * Math.Min(lastWidth, nextWidth))
            return false;
        if (ListMark().IsMatch(next.Text))
            return false;
        // Lined up on either side or the middle; only the first line of a paragraph may stand further in.
        double room = block.Lines.Count == 1 ? 3 * size : size;
        bool lined = Math.Abs(last.Left - next.Left) <= room || Math.Abs(last.Right - next.Right) <= room
            || Math.Abs((last.Left + last.Right) / 2 - (next.Left + next.Right) / 2) <= size;
        if (!lined)
            return false;
        if (last.Words.Count < 3 && lastWidth < 0.3 * page.Width)
            return false;
        // The next line's first word would have fitted at the end of this one: this one ended on purpose.
        double width = Math.Max(block.Right - block.Left, nextWidth);
        var firstWord = next.Ordered[0];
        return lastWidth + 0.3 * size + (firstWord.Right - firstWord.Left) >= 0.97 * width;
    }

    /// <summary>
    /// Covers then words, in two rounds so that no cover falls on a neighbour's words. The page's picture gives the colours,
    /// and tells how far the page is empty on either side of a paragraph.
    /// </summary>
    private static void Paint(PdfPage page, PdfPageContent content, List<Block> blocks, byte[] pdf, bool rtl)
    {
        var todo = blocks.Where(b => b.Translation is not null).ToList();
        if (todo.Count == 0)
            return;
        using var picture = PdfRaster.Render(pdf, content.Index, SampleDpi, annotations: false);
        double scale = picture.Width / Math.Max(1, content.Width);
        var (width, height) = PdfStamper.DisplayedSize(page);
        using var gfx = PdfStamper.Upright(page);
        // PDFium, PDFsharp and PdfPig agree on a page's size; if not, stretch to fit.
        if (Math.Abs(width - content.Width) > 0.5 || Math.Abs(height - content.Height) > 0.5)
            gfx.ScaleTransform(width / Math.Max(1, content.Width), height / Math.Max(1, content.Height));
        double margin = Math.Max(18, blocks.Min(b => b.Left));
        var inks = new XBrush[todo.Count];
        var covers = new XRect[todo.Count];
        var rooms = new (double Left, double Right)[todo.Count];
        var starts = StartEdges(todo, rtl);
        for (int i = 0; i < todo.Count; i++)
        {
            var block = todo[i];
            double pad = 0.08 * block.Size;
            double left = Math.Max(0, block.Left - 1), top = Math.Max(0, block.Top - pad);
            covers[i] = new XRect(left, top, Math.Min(content.Width, block.Right + 1) - left, Math.Min(content.Height, block.Bottom + pad) - top);
            var (back, ink) = Colours(picture, scale, block, covers[i]);
            inks[i] = new XSolidBrush(ink);
            rooms[i] = Room(picture, scale, block, back, margin, content.Width);
            gfx.DrawRectangle(new XSolidBrush(back), covers[i]);
        }
        for (int i = 0; i < todo.Count; i++)
        {
            // A column's right edge is shared when it goes from starting at the left to starting at the right, and the other way.
            var (left, right) = (todo[i].Left, todo[i].Right);
            if (rtl)
                right = Math.Clamp(starts[i], right, Math.Max(right, rooms[i].Right));
            else
                left = Math.Clamp(starts[i], Math.Min(left, rooms[i].Left), left);
            Write(gfx, todo[i], covers[i], left, right, rooms[i], inks[i], content, rtl);
        }
    }

    /// <summary>
    /// Where each paragraph's lines start once they read the other way. Lines that all begin at one left edge, one under the
    /// other (a list, a column of a table, a heading over its text), end at different places, so turned round they would
    /// begin raggedly; they get the rightmost of their ends as their common start. The same in the other direction.
    /// </summary>
    private static double[] StartEdges(List<Block> blocks, bool rtl)
    {
        var starts = blocks.Select(b => rtl ? b.Right : b.Left).ToArray();
        bool Turned(Block b) => rtl != b.Lines.Count(l => l.Rtl) * 2 > b.Lines.Count;
        var groups = new List<(List<int> Members, double Edge, double Bottom, double Size)>();
        foreach (int i in Enumerable.Range(0, blocks.Count).Where(i => Turned(blocks[i])).OrderBy(i => blocks[i].Top))
        {
            var block = blocks[i];
            double edge = rtl ? block.Left : block.Right;
            int home = groups.FindIndex(g => Math.Abs(g.Edge - edge) <= 2 && block.Top - g.Bottom <= 2.5 * Math.Max(g.Size, block.Size));
            if (home < 0)
                groups.Add(([i], edge, block.Bottom, block.Size));
            else
            {
                groups[home].Members.Add(i);
                groups[home] = (groups[home].Members, groups[home].Edge, block.Bottom, block.Size);
            }
        }
        foreach (var (members, _, _, _) in groups)
        {
            double common = rtl ? members.Max(i => blocks[i].Right) : members.Min(i => blocks[i].Left);
            foreach (int i in members)
                starts[i] = common;
        }
        return starts;
    }

    /// <summary>
    /// How far a paragraph could spread sideways over nothing but its own background: to the next thing on the page beside it,
    /// a little short of it, and never into the page's margins.
    /// </summary>
    private static (double Left, double Right) Room(SKBitmap picture, double scale, Block block, XColor back, double margin, double width)
    {
        int top = Math.Clamp((int)(block.Top * scale), 0, picture.Height - 1), bottom = Math.Clamp((int)(block.Bottom * scale), top, picture.Height - 1);
        bool Empty(int x)
        {
            for (int y = top; y <= bottom; y += 2)
            {
                var pixel = picture.GetPixel(x, y);
                if (Math.Abs(pixel.Red - back.R) + Math.Abs(pixel.Green - back.G) + Math.Abs(pixel.Blue - back.B) > 40)
                    return false;
            }
            return true;
        }
        double gap = 1.5 * block.Size;
        int limitLeft = Math.Clamp((int)(margin * scale), 0, picture.Width - 1), limitRight = Math.Clamp((int)((width - margin) * scale), 0, picture.Width - 1);
        int left = Math.Clamp((int)((block.Left - 1.5) * scale), 0, picture.Width - 1), right = Math.Clamp((int)((block.Right + 1.5) * scale), 0, picture.Width - 1);
        while (left > limitLeft && Empty(left))
            left--;
        while (right < limitRight && Empty(right))
            right++;
        double roomLeft = left <= limitLeft ? margin : left / scale + gap, roomRight = right >= limitRight ? width - margin : right / scale - gap;
        return (Math.Min(block.Left, roomLeft), Math.Max(block.Right, roomRight));
    }

    /// <summary>
    /// A paragraph's colours: the middle of what lies just around it, and its text's own colour where the PDF gives one that
    /// shows on that background; otherwise what stands out most inside its words; otherwise black or white.
    /// </summary>
    private static (XColor Back, XColor Ink) Colours(SKBitmap picture, double scale, Block block, XRect box)
    {
        var reds = new List<byte>();
        var greens = new List<byte>();
        var blues = new List<byte>();
        void Sample(int x, int y)
        {
            if (x < 0 || y < 0 || x >= picture.Width || y >= picture.Height)
                return;
            var pixel = picture.GetPixel(x, y);
            reds.Add(pixel.Red);
            greens.Add(pixel.Green);
            blues.Add(pixel.Blue);
        }
        int left = (int)((box.Left - 2) * scale), right = (int)((box.Right + 2) * scale), top = (int)((box.Top - 2) * scale), bottom = (int)((box.Bottom + 2) * scale);
        for (int x = left; x <= right; x += 2)
        {
            Sample(x, top);
            Sample(x, bottom);
        }
        for (int y = top; y <= bottom; y += 2)
        {
            Sample(left, y);
            Sample(right, y);
        }
        if (reds.Count == 0)
            return (XColors.White, XColors.Black);
        var back = (R: Middle(reds), G: Middle(greens), B: Middle(blues));
        double backLuma = Luma(back.R, back.G, back.B);
        var plain = backLuma > 140 ? XColors.Black : XColors.White;
        var backColour = XColor.FromArgb(back.R, back.G, back.B);
        if (block.Colour is { } known)
        {
            byte r = (byte)(known >> 16), g = (byte)(known >> 8), b = (byte)known;
            if (Math.Abs(Luma(r, g, b) - backLuma) >= 50)
                return (backColour, XColor.FromArgb(r, g, b));
        }
        var inks = new List<(double Difference, byte R, byte G, byte B)>();
        foreach (var word in block.AllWords)
        {
            for (int y = Math.Max(0, (int)(word.Top * scale)); y <= Math.Min(picture.Height - 1, (int)(word.Bottom * scale)); y++)
                for (int x = Math.Max(0, (int)(word.Left * scale)); x <= Math.Min(picture.Width - 1, (int)(word.Right * scale)); x++)
                {
                    var pixel = picture.GetPixel(x, y);
                    double difference = Math.Abs(Luma(pixel.Red, pixel.Green, pixel.Blue) - backLuma);
                    if (difference > 40)
                        inks.Add((difference, pixel.Red, pixel.Green, pixel.Blue));
                }
        }
        if (inks.Count < 8)
            return (backColour, plain);
        var strongest = inks.OrderByDescending(i => i.Difference).Take(Math.Max(8, inks.Count / 4)).ToList();
        byte red = (byte)strongest.Average(i => i.R), green = (byte)strongest.Average(i => i.G), blue = (byte)strongest.Average(i => i.B);
        return (backColour, Math.Abs(Luma(red, green, blue) - backLuma) < 50 ? plain : XColor.FromArgb(red, green, blue));
    }

    private static byte Middle(List<byte> values)
    {
        values.Sort();
        return values[values.Count / 2];
    }

    private static double Luma(byte r, byte g, byte b) => 0.299 * r + 0.587 * g + 0.114 * b;

    private enum LineUp { Start, Middle, End }

    /// <summary>
    /// A paragraph's translation at its own size in its rectangle when it fits; if not, spreading over the empty page beside it;
    /// and only then smaller, down to the least that can be read, below which it is clipped so as to stay in place.
    /// </summary>
    private static void Write(XGraphics gfx, Block block, XRect cover, double left, double right, (double Left, double Right) room, XBrush ink, PdfPageContent page, bool rtl)
    {
        var words = block.Translation!;
        bool bold = block.Lines.Count(l => l.Bold) * 2 >= block.Lines.Count;
        bool arabicScript = rtl || Bidi.ContainsRtl(words);
        bool italic = !arabicScript && block.Lines.Count(l => l.Italic) * 2 > block.Lines.Count;
        var family = Family(block, arabicScript);
        double height = block.Bottom - block.Top;
        var lineUp = Alignment(block, page);
        var box = new XRect(left, block.Top, right - left, height);
        var (size, lines) = Fit(words, family, bold, italic, box, block.Size, block.Leading, rtl);
        if (size < block.Size)
        {
            // Only as wide as the side it can grow on allows: a centred line grows on both alike.
            var wider = lineUp == LineUp.Middle
                ? Math.Min((block.Left + block.Right) / 2 - room.Left, room.Right - (block.Left + block.Right) / 2) is var half ? new XRect((block.Left + block.Right) / 2 - half, block.Top, 2 * half, height) : box
                : rtl ? new XRect(room.Left, block.Top, right - room.Left, height) : new XRect(left, block.Top, room.Right - left, height);
            if (wider.Width > box.Width + 0.5)
            {
                box = wider;
                (size, lines) = Fit(words, family, bold, italic, box, block.Size, block.Leading, rtl);
            }
        }
        var style = (bold ? XFontStyleEx.Bold : XFontStyleEx.Regular) | (italic ? XFontStyleEx.Italic : XFontStyleEx.Regular);
        var font = new XFont(family, size, style, new XPdfFontOptions(PdfFontEncoding.Unicode));
        double step = block.Leading * size, tall = (lines.Count - 1) * step + size;
        // A line on its own sits in the middle of the height it had; lines of a paragraph start at its top.
        double baseline = box.Top + (lines.Count == 1 ? Math.Max(0, (box.Height - tall) / 2) : 0) + 0.78 * size;
        var state = gfx.Save();
        gfx.IntersectClip(XRect.Union(cover, new XRect(box.Left, cover.Top, box.Width, cover.Height)));
        foreach (var line in lines)
        {
            double wide = gfx.MeasureString(line, font).Width;
            double x = lineUp == LineUp.Middle ? box.Left + (box.Width - wide) / 2 : rtl ? box.Right - wide : box.Left;
            gfx.DrawString(line, font, ink, x, baseline);
            baseline += step;
        }
        gfx.Restore(state);
    }

    /// <summary>A centred paragraph stays centred; anything else starts on the side the language starts on.</summary>
    private static LineUp Alignment(Block block, PdfPageContent page)
    {
        double size = block.Size;
        if (block.Lines.Count > 1)
        {
            var middles = block.Lines.Select(l => (l.Left + l.Right) / 2).ToList();
            bool sameMiddle = middles.Max() - middles.Min() <= size;
            bool sameLeft = block.Lines.Max(l => l.Left) - block.Lines.Min(l => l.Left) <= size;
            return sameMiddle && !sameLeft ? LineUp.Middle : LineUp.Start;
        }
        // One line in the middle of the page, away from both margins.
        double middle = (block.Left + block.Right) / 2;
        return Math.Abs(middle - page.Width / 2) <= 3 && block.Left > 0.15 * page.Width ? LineUp.Middle : LineUp.Start;
    }

    /// <summary>The paragraph's own font when this PC has it and it has the letters; Arial, which has them all, otherwise.</summary>
    private static string Family(Block block, bool arabicScript)
    {
        if (arabicScript)
            return PdfFonts.Arabic;
        var name = (block.AllWords.FirstOrDefault(w => w.FontName is not null)?.FontName ?? "").Replace(" ", "");
        var known = new (string Part, string Family)[]
        {
            ("Calibri", "Calibri"), ("Cambria", "Cambria"), ("Georgia", "Georgia"), ("Garamond", "Garamond"), ("Verdana", "Verdana"), ("Tahoma", "Tahoma"),
            ("Segoe", "Segoe UI"), ("Times", "Times New Roman"), ("Courier", "Courier New"), ("Consolas", "Consolas"), ("Arial", "Arial"), ("Helvetica", "Arial"),
        };
        foreach (var (part, family) in known)
            if (name.Contains(part, StringComparison.OrdinalIgnoreCase) && PdfFonts.IsAvailable(family))
                return family;
        return "Arial";
    }

    /// <summary>
    /// The largest size up to the paragraph's own at which the words fit the rectangle, and their lines at that size, shaped
    /// and in the order they are drawn.
    /// </summary>
    private static (double Size, List<string> Lines) Fit(string words, string family, bool bold, bool italic, XRect box, double largest, double leading, bool rtl)
    {
        var sizes = new List<double> { largest };
        while (sizes[^1] * SizeStep >= Math.Min(SmallestSize, largest))
            sizes.Add(sizes[^1] * SizeStep);
        List<string> LinesAt(double size) => PdfFonts.BreakRtl(words, family, size, bold, italic, box.Width, rtl).Select(Drawable).ToList();
        bool Fits(double size, List<string> lines) =>
            (lines.Count - 1) * leading * size + size <= box.Height + 0.01
            && lines.All(l => PdfFonts.Measure(l, family, size, bold, italic) <= box.Width + 0.01);
        var largestLines = LinesAt(largest);
        if (Fits(largest, largestLines))
            return (largest, largestLines);
        // The first size in the list that fits: the words only get shorter as the size falls.
        int low = 1, high = sizes.Count - 1;
        while (low < high)
        {
            int middle = (low + high) / 2;
            if (Fits(sizes[middle], LinesAt(sizes[middle])))
                high = middle;
            else
                low = middle + 1;
        }
        return (sizes[low], LinesAt(sizes[low]));
    }

    /// <summary>The marks that only steer joining and direction have nothing to draw, and would show as boxes.</summary>
    private static string Drawable(string line) =>
        new(line.Where(c => c is not ((char)0x200C or (char)0x200D or (char)0x200E or (char)0x200F)).ToArray());
}
