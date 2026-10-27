using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Petal.App.Visuals;
using Petal.Core.Text;

namespace Petal.App.Screen;

/// <summary>
/// A translation laid over the words it translates: each paragraph covered in its own background colour and written in
/// the other language, in its own text colour, as large as fits, right to left for Arabic. Everything here is in the
/// pixels of the still.
/// </summary>
internal static class CircleTranslation
{
    public static void Render(Canvas layer, IReadOnlyList<ScreenBlock> blocks, IReadOnlyList<string> translations, byte[] still, int width, int height)
    {
        layer.Children.Clear();
        for (int i = 0; i < blocks.Count && i < translations.Count; i++)
        {
            var text = translations[i].Trim();
            if (text.Length == 0)
                continue;
            var block = blocks[i];
            var box = block.Box;
            box.Inflate(3, 3);
            box.Intersect(new Rect(0, 0, width, height));
            if (box.IsEmpty)
                continue;
            var (back, ink) = Colours(still, width, height, block);
            double line = block.Lines.Average(l => l.Box.Height);
            bool rtl = KeyboardLayout.LooksArabic(text);
            var words = new TextBlock
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(ink),
                FontFamily = UiFonts.Text,
                FlowDirection = rtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight,
                TextAlignment = TextAlignment.Left,
            };
            TextOptions.SetTextFormattingMode(words, TextFormattingMode.Display);
            // The words' own size if it fits; smaller (to a little under half a line) when the translation is longer,
            // and the box a line taller at most.
            // A word's box is about the height of its letters, a little under the size it was set in.
            double room = box.Height + line * 0.6;
            words.FontSize = Fit(words, Math.Max(8, box.Width - 4), room, line * 1.05, line * 0.5);
            var cover = new Border
            {
                Background = new SolidColorBrush(back),
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(2, 1, 2, 1),
                Width = box.Width,
                MinHeight = box.Height,
                Child = words,
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(cover, box.X);
            Canvas.SetTop(cover, box.Y);
            layer.Children.Add(cover);
        }
    }

    /// <summary>The largest size from <paramref name="largest"/> down to <paramref name="smallest"/> at which the text fits.</summary>
    internal static double Fit(TextBlock text, double width, double height, double largest, double smallest)
    {
        largest = Math.Max(largest, 6);
        smallest = Math.Clamp(smallest, 5, largest);
        for (double size = largest; size >= smallest; size *= 0.92)
        {
            text.FontSize = size;
            text.Measure(new Size(width, double.PositiveInfinity));
            if (text.DesiredSize.Height <= height)
                return size;
        }
        return smallest;
    }

    /// <summary>
    /// A paragraph's colours: its background (the middle of what lies just around it) and its text (the pixels inside its
    /// words that differ most from the background). Text too close to the background becomes black or white.
    /// </summary>
    internal static (Color Back, Color Ink) Colours(byte[] still, int width, int height, ScreenBlock block)
    {
        var box = block.Box;
        box.Inflate(3, 3);
        var reds = new List<byte>();
        var greens = new List<byte>();
        var blues = new List<byte>();
        void Sample(int x, int y)
        {
            if (x < 0 || y < 0 || x >= width || y >= height)
                return;
            int p = (y * width + x) * 4;
            blues.Add(still[p]);
            greens.Add(still[p + 1]);
            reds.Add(still[p + 2]);
        }
        int left = (int)box.Left, top = (int)box.Top, right = (int)box.Right, bottom = (int)box.Bottom;
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
            return (Colors.White, Colors.Black);
        var back = Color.FromRgb(Median(reds), Median(greens), Median(blues));
        double backLuma = Luma(back.R, back.G, back.B);
        // The text's colour: what differs most from the background inside the words.
        var inks = new List<(double Diff, byte R, byte G, byte B)>();
        foreach (var word in block.Words)
        {
            int x0 = Math.Max(0, (int)word.X), y0 = Math.Max(0, (int)word.Y);
            int x1 = Math.Min(width - 1, (int)word.Right), y1 = Math.Min(height - 1, (int)word.Bottom);
            for (int y = y0; y <= y1; y += 1)
                for (int x = x0; x <= x1; x += 1)
                {
                    int p = (y * width + x) * 4;
                    double diff = Math.Abs(Luma(still[p + 2], still[p + 1], still[p]) - backLuma);
                    if (diff > 40)
                        inks.Add((diff, still[p + 2], still[p + 1], still[p]));
                }
        }
        Color ink;
        if (inks.Count < 8)
            ink = backLuma > 140 ? Colors.Black : Colors.White;
        else
        {
            var strongest = inks.OrderByDescending(i => i.Diff).Take(Math.Max(8, inks.Count / 4)).ToList();
            ink = Color.FromRgb((byte)strongest.Average(i => i.R), (byte)strongest.Average(i => i.G), (byte)strongest.Average(i => i.B));
            if (Math.Abs(Luma(ink.R, ink.G, ink.B) - backLuma) < 70)
                ink = backLuma > 140 ? Colors.Black : Colors.White;
        }
        return (back, ink);
    }

    private static byte Median(List<byte> values)
    {
        values.Sort();
        return values[values.Count / 2];
    }

    private static double Luma(byte r, byte g, byte b) => 0.299 * r + 0.587 * g + 0.114 * b;
}
