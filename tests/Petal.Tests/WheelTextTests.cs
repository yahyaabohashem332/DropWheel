using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Petal.App.Overlay;
using Petal.App.Visuals;
using Petal.Core.Actions;
using Petal.Core.Tools;
using Xunit.Abstractions;

namespace Petal.Tests;

/// <summary>The wheel's own text layout, in both languages: names in the centre and labels on the petals.</summary>
public class WheelTextTests(ITestOutputHelper output)
{
    private static string Arabic(string english) =>
        Petal.App.Localization.Arabic.Table.TryGetValue(english, out var arabic) ? arabic.Split('|')[^1] : english;

    private static InkFont Font(bool arabic, bool title = true) => new(
        title ? UiFonts.Display : UiFonts.Text,
        title ? FontWeights.SemiBold : FontWeights.Normal,
        arabic ? FlowDirection.RightToLeft : FlowDirection.LeftToRight,
        1);

    /// <summary>Letters and digits only, so a separator dropped at a line break doesn't count as lost text.</summary>
    private static string Letters(string text) => new(text.Where(char.IsLetterOrDigit).ToArray());

    private static IEnumerable<string> Names(bool arabic) =>
        ToolRegistry.All.Select(t => t.Name).Concat(FileActionRegistry.All.Select(a => a.Name)).Select(n => arabic ? Arabic(n) : n);

    private static IEnumerable<string> WheelTitles(bool arabic) =>
        ToolRegistry.All.Select(t => t.WheelTitle).Concat(FileActionRegistry.All.Select(a => a.WheelTitle)).Select(n => arabic ? Arabic(n) : n);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Every_tool_and_action_name_fits_the_centre_whole(bool arabic)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var titleFont = Font(arabic);
        var subtitleFont = Font(arabic, title: false);
        string subtitle = arabic ? Arabic("Tool") : "Tool";
        foreach (var name in Names(arabic).Distinct())
        {
            var fit = HubLayout.Fit(name, subtitle, titleFont, subtitleFont, arabic);
            var title = fit.Rows.Where(r => r.IsTitle).ToList();
            // Every letter is there: nothing is cut short with an ellipsis.
            Assert.Equal(Letters(name), Letters(string.Concat(title.Select(r => r.Text))));
            Assert.DoesNotContain(title, r => r.Text.Contains('…'));
            Assert.InRange(title.Count, 1, 3);
            Assert.All(title, r => Assert.InRange(r.Size, HubLayout.SmallestTitle, HubLayout.TitleSize));
            Assert.InRange(fit.Reach, 0, HubLayout.Room);
            AssertInsideOutline(fit, name);
            output.WriteLine($"{name}: {string.Join(" / ", title.Select(r => r.Text))} at {title[0].Size}, {fit.HalfWidth:0.0}x{fit.HalfHeight:0.0}");
        }
        output.WriteLine($"{clock.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void A_short_name_keeps_the_pill_and_a_long_one_grows_into_a_disc()
    {
        var fit = HubLayout.Fit("PNG", "Convert", Font(false), Font(false, title: false), false);
        Assert.Single(fit.Rows, r => r.IsTitle);
        Assert.Equal(HubLayout.TitleSize, fit.Rows[0].Size);
        Assert.True(fit.HalfWidth > fit.HalfHeight + 5, "a pill is wider than it is tall");

        // The name from the report: whole, on two lines, at full size.
        var clean = HubLayout.Fit(Arabic("Clean for Sharing"), Arabic("Action"), Font(true), Font(true, title: false), true);
        Assert.Equal(["تنظيف", "للمشاركة"], clean.Rows.Where(r => r.IsTitle).Select(r => r.Text));
        Assert.Equal(HubLayout.TitleSize, clean.Rows[0].Size);
        Assert.True(clean.HalfHeight > fit.HalfHeight + 5, "the outline grows taller, towards a disc");
    }

    [Fact]
    public void A_name_too_long_for_three_lines_ends_in_an_ellipsis_and_still_fits()
    {
        var name = string.Join(' ', Enumerable.Repeat("Unbelievably long recipe name", 4));
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var fit = HubLayout.Fit(name, "Recipe", Font(false), Font(false, title: false), false);
        output.WriteLine($"{clock.ElapsedMilliseconds} ms: {string.Join(" / ", fit.Rows.Select(r => r.Text))}");
        // Once measured, names come back instantly; even this one, measured from nothing, is quick.
        Assert.InRange(clock.ElapsedMilliseconds, 0, 1000);
        Assert.Contains(fit.Rows, r => r.Text.EndsWith('…'));
        Assert.InRange(fit.Reach, 0, HubLayout.Room);
        AssertInsideOutline(fit, name);
    }

    [Fact]
    public void A_favourite_description_breaks_between_its_parts()
    {
        var fit = HubLayout.Fit("WEBP 1600", "WEBP · 1600 px · quality 80", Font(false), Font(false, title: false), false);
        var lines = fit.Rows.Where(r => !r.IsTitle).Select(r => r.Text).ToList();
        Assert.Equal(Letters("WEBP · 1600 px · quality 80"), Letters(string.Concat(lines)));
        Assert.All(lines, l => Assert.False(l.StartsWith('·') || l.EndsWith('·'), l));
        Assert.DoesNotContain(lines, l => l.EndsWith("1600") || l.StartsWith("px"));
    }

    /// <summary>Every row's ink inside the outline: a rounded rectangle whose corner radius is its smaller half.</summary>
    private static void AssertInsideOutline(HubFit fit, string name)
    {
        double a = fit.HalfWidth, b = fit.HalfHeight, c = Math.Min(a, b);
        foreach (var row in fit.Rows)
        {
            foreach (var y in new[] { row.Top, row.Bottom })
            {
                double dx = Math.Max(row.Ink.Width / 2 - (a - c), 0), dy = Math.Max(Math.Abs(y) - (b - c), 0);
                Assert.True(dx * dx + dy * dy <= c * c, $"{name}: \"{row.Text}\" leaves the outline");
            }
        }
    }

    [Fact]
    public void A_separator_never_starts_or_ends_a_broken_line()
    {
        var words = WheelText.Words("TEXT · QR");
        Assert.Equal(["TEXT ·", "QR"], words);
        Assert.Equal(["TEXT", "QR"], WheelText.Splits(words, 2).Single());
        Assert.Equal(["TEXT · QR"], WheelText.Splits(words, 1).Single());
    }

    [Fact]
    public void A_trailing_space_has_no_ink()
    {
        var font = Font(arabic: true);
        Assert.Equal(font.Ink("إلى", 10).Width, font.Ink("إلى ", 10).Width, 3);
        Assert.Equal(font.Ink("إلى", 10).Width, InkShape.Bounds(font.Strips("إلى", 10)).Width, 0.5);
        Assert.Equal(font.Ink("إلى", 10).Width, font.RoughInk("إلى", 10).Width, 0.5);
    }

    [Theory]
    [InlineData(false, 8)]
    [InlineData(true, 8)]
    [InlineData(false, 12)]
    [InlineData(true, 12)]
    public void Every_label_fits_inside_its_petal_at_every_place(bool arabic, int count)
    {
        var font = Font(arabic);
        // As the wheel sets them: the caption size, the line step and the icon.
        double size = arabic ? (count > 8 ? 10 : 10.5) : count > 8 ? 8 : 8.5;
        double step = arabic ? 13 : 9.5, iconSize = count > 8 ? 16 : 18;
        var outline = Icons.InkGeometry("share", iconSize);
        var icon = new IconInk(outline.Bounds, InkShape.Strips(outline, 3));
        var shrunk = new List<string>();
        var tight = new List<string>();
        int placements = 0;
        foreach (var title in WheelTitles(arabic).Distinct())
        {
            for (int i = 0; i < count; i++)
            {
                var label = PetalLayout.Fit(title, font, size, size - 1.5, step, icon, iconSize, i, count);
                Assert.Equal(Letters(title), Letters(string.Concat(label.Lines)));
                Assert.InRange(label.Size, size - 1.5, size);
                // Moved, if at all, only along the petal's axis.
                var axis = WheelGeometry.At(1, WheelGeometry.Angle(i, count));
                Assert.Equal(0, label.Centre.X * axis.Y - label.Centre.Y * axis.X, 6);
                // Never visibly over the edge, and the full margin wherever an arrangement allows it: a wide
                // word with an icon can be pinned between the rim and a side of a slanted petal.
                Assert.True(label.Clearance >= -0.5, $"{title} at {i} of {count}: {label.Clearance:0.0}");
                placements++;
                if (label.Clearance < PetalLayout.Margin)
                    tight.Add($"{title} at {i}: {label.Clearance:0.0}");
                if (label.Size < size)
                    shrunk.Add($"{title} at {i}: {label.Size}");
            }
        }
        output.WriteLine($"{shrunk.Count} shrunk: {string.Join(", ", shrunk)}");
        output.WriteLine($"{tight.Count} under the margin: {string.Join(", ", tight)}");
        Assert.True(tight.Count * 50 <= placements, $"{tight.Count} of {placements}: {string.Join(", ", tight)}");
    }

    [Theory]
    [InlineData("إلى الهاتف", true)]
    [InlineData("نسخ ", true)]
    [InlineData("COPY PATH", false)]
    public void Drawn_ink_lands_where_it_was_placed(string text, bool arabic)
    {
        OnSta(() =>
        {
            var line = Font(arabic).Create(text, 20, Brushes.Black);
            var canvas = new Canvas { Width = 200, Height = 60, Background = Brushes.White };
            canvas.Children.Add(line);
            line.Place(100, 40);
            canvas.Measure(new Size(200, 60));
            canvas.Arrange(new Rect(0, 0, 200, 60));
            var bitmap = new RenderTargetBitmap(200, 60, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(canvas);
            var pixels = new byte[200 * 60 * 4];
            bitmap.CopyPixels(pixels, 200 * 4, 0);
            int left = 200, right = -1, top = 60, bottom = -1;
            for (int y = 0; y < 60; y++)
                for (int x = 0; x < 200; x++)
                    if (pixels[(y * 200 + x) * 4] < 160)
                        (left, right, top, bottom) = (Math.Min(left, x), Math.Max(right, x), Math.Min(top, y), Math.Max(bottom, y));
            Assert.InRange((left + right + 1) / 2.0, 99, 101);
            Assert.InRange(bottom + 1, 40 + line.Ink.Bottom - 1.5, 40 + line.Ink.Bottom + 1.5);
        });
    }

    private static void OnSta(Action test)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try { test(); }
            catch (Exception e) { failure = ExceptionDispatchInfo.Capture(e); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        failure?.Throw();
    }
}
