using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Petal.App.Screen;
using Petal.Core.Text;
using Petal.Engines.Documents;
using Petal.Engines.Documents.Ocr;
using Petal.Engines.Imaging;

namespace Petal.Tests;

/// <summary>Circle Anything: what a stroke takes in, how the screen's words read, which petals come up, and the translation laid over.</summary>
public sealed class CircleTests
{
    /// <summary>Three lines of words at 20 pixels a word height, like a screen read by Windows.</summary>
    private static List<PictureWord> Page()
    {
        var words = new List<PictureWord>();
        string[][] lines = [["Hello", "there,", "friend"], ["The", "price", "is", "$25"], ["Call", "055", "123", "4567"]];
        for (int line = 0; line < lines.Length; line++)
        {
            double x = 100;
            foreach (var w in lines[line])
            {
                words.Add(new PictureWord(w, x, 100 + line * 30, w.Length * 10, 20, line));
                x += w.Length * 10 + 10;
            }
        }
        return words;
    }

    private static List<Point> Loop(double cx, double cy, double rx, double ry, int points = 48) =>
        Enumerable.Range(0, points + 1).Select(i => new Point(cx + rx * Math.Cos(2 * Math.PI * i / points), cy + ry * Math.Sin(2 * Math.PI * i / points))).ToList();

    [Fact]
    public void A_loop_round_a_line_takes_that_line_only()
    {
        var page = Page();
        // The second line runs from x 100 to about 300, at y 130 to 150.
        var loop = Loop(200, 140, 130, 18);
        Assert.True(CircleText.Encloses(loop));
        var taken = CircleText.Select(loop, page, 1920, 1080);
        Assert.True(taken.Closed);
        Assert.Equal("The price is $25", CircleText.Text(taken.Words));
        Assert.InRange(taken.Area.X, 60, 80);
        Assert.InRange(taken.Area.Y, 115, 125);
    }

    [Fact]
    public void A_stroke_along_a_line_takes_the_words_it_passes_over()
    {
        var page = Page();
        var stroke = Enumerable.Range(0, 30).Select(i => new Point(95 + i * 4, 110)).ToList();
        Assert.False(CircleText.Encloses(stroke));
        var taken = CircleText.Select(stroke, page, 1920, 1080);
        Assert.False(taken.Closed);
        Assert.Equal("Hello there,", CircleText.Text(taken.Words));
        // The area is the words', a little wider.
        Assert.InRange(taken.Area.Width, 120, 140);
    }

    [Fact]
    public void A_stroke_over_no_words_takes_the_picture_around_it()
    {
        var stroke = Loop(800, 600, 60, 40);
        var taken = CircleText.Select(stroke, Page(), 1920, 1080);
        Assert.Empty(taken.Words);
        // The loop's own box, a few pixels wider.
        Assert.InRange(taken.Area.Width, 124, 134);
        // A stroke that doesn't close over nothing takes the picture round it, with room.
        var line = CircleText.Select(Enumerable.Range(0, 20).Select(i => new Point(700 + i * 5, 700)).ToList(), Page(), 1920, 1080);
        Assert.InRange(line.Area.Width, 140, 150);
        // Kept inside the screen even near its edge.
        var edge = CircleText.Select(Loop(10, 10, 40, 40), Page(), 1920, 1080);
        Assert.True(edge.Area.X >= 0 && edge.Area.Y >= 0);
    }

    [Fact]
    public void Loops_points_and_strokes_are_told_apart()
    {
        var loop = Loop(0, 0, 50, 50);
        Assert.True(CircleText.Contains(loop, new Point(10, -5)));
        Assert.False(CircleText.Contains(loop, new Point(60, 0)));
        Assert.True(CircleText.Touches([new Point(0, 0), new Point(100, 100)], new Rect(40, 40, 10, 10), 0));
        Assert.False(CircleText.Touches([new Point(0, 0), new Point(100, 0)], new Rect(40, 40, 10, 10), 3));
        Assert.False(CircleText.Encloses(Loop(0, 0, 4, 4)));
        var smooth = CircleText.Smooth([new Point(0, 0), new Point(10, 0), new Point(10, 10)], closed: false);
        Assert.True(smooth.Count > 3);
        Assert.Equal(new Point(0, 0), smooth[0]);
        Assert.Equal(new Point(10, 10), smooth[^1]);
    }

    [Fact]
    public void Lines_make_paragraphs_and_columns_stay_apart()
    {
        var words = new List<PictureWord>
        {
            // A heading, larger, then a paragraph of two lines under it, and a column of its own on the right.
            new("Weekly", 100, 50, 120, 40, 0), new("news", 230, 50, 90, 40, 0),
            new("First", 100, 120, 50, 18, 1), new("line", 160, 120, 40, 18, 1),
            new("second", 100, 142, 60, 18, 2), new("line", 170, 142, 40, 18, 2),
            new("Side", 900, 120, 40, 18, 3), new("note", 950, 120, 40, 18, 3),
        };
        var lines = CircleText.Lines(words);
        Assert.Equal(["Weekly news", "First line", "Side note", "second line"], lines.Select(l => l.Text));
        var blocks = CircleText.Blocks(lines);
        Assert.Equal(["Weekly news", "First line second line", "Side note"], blocks.Select(b => b.Text));
        Assert.Equal("First line second line", CircleText.BlockAt(new Point(150, 150), blocks)!.Text);
        Assert.Null(CircleText.BlockAt(new Point(600, 600), blocks));
    }

    [Fact]
    public void Arabic_lines_on_one_row_read_from_the_right()
    {
        var words = new List<PictureWord>
        {
            new("اليسار", 100, 100, 80, 20, 0),
            new("اليمين", 500, 100, 80, 20, 1),
            new("تحت", 500, 140, 40, 20, 2),
        };
        Assert.Equal(["اليمين", "اليسار", "تحت"], CircleText.Lines(words).Select(l => l.Text));
    }

    /// <summary>Words as Windows gives them: left to right on the screen, one line.</summary>
    private static List<PictureWord> Shown(params string[] words)
    {
        var list = new List<PictureWord>();
        double x = 100;
        foreach (var w in words)
        {
            list.Add(new PictureWord(w, x, 0, w.Length * 10, 20, 0));
            x += w.Length * 10 + 8;
        }
        return list;
    }

    [Fact]
    public void Arabic_reads_from_the_right_with_english_and_numbers_kept_as_shown()
    {
        // "السعر 150 ريال" shows as ريال 150 السعر.
        Assert.Equal("السعر 150 ريال", ReadingOrder.Text(Shown("ريال", "150", "السعر")));
        // "اضغط Save ثم OK" shows as OK ثم Save اضغط.
        Assert.Equal("اضغط Save ثم OK", ReadingOrder.Text(Shown("OK", "ثم", "Save", "اضغط")));
        // A time inside Arabic stays "3:00 PM".
        Assert.Equal("يبدأ من 3:00 PM يوم الأحد", ReadingOrder.Text(Shown("الأحد", "يوم", "3:00", "PM", "من", "يبدأ")));
        // An Arabic phrase inside English reads from its right.
        Assert.Equal("Welcome مرحبا بكم to DropWheel", ReadingOrder.Text(Shown("Welcome", "بكم", "مرحبا", "to", "DropWheel")));
        Assert.Equal("Plain English stays put", ReadingOrder.Text(Shown("Plain", "English", "stays", "put")));
    }

    [Fact]
    public void A_line_windows_cut_in_two_is_one_line_again()
    {
        // One Arabic line that Windows gave as two pieces (its lines 0 and 1), side by side with an ordinary gap.
        var words = new List<PictureWord>
        {
            new("الغروب", 100, 50, 60, 20, 1), new("قبل", 168, 50, 30, 20, 1),
            new("وتنتهي", 206, 50, 60, 20, 0), new("صباحا،", 274, 50, 60, 20, 0),
        };
        var lines = ReadingOrder.Lines(words);
        Assert.Single(lines);
        Assert.Equal("صباحا، وتنتهي قبل الغروب", string.Join(" ", lines[0].Select(w => w.Text)));
        // Columns far apart stay two lines.
        var columns = new List<PictureWord> { new("Left", 100, 0, 40, 20, 0), new("Right", 600, 0, 50, 20, 1) };
        Assert.Equal(2, ReadingOrder.Lines(columns).Count);
    }

    private static CircleContent Content(string text, int words, double cover = 0.3, PictureTable? table = null, int width = 400, int height = 100)
    {
        var list = Enumerable.Range(0, words).Select(i => new PictureWord("w" + i, i * 5, 0, 4, 4, 0)).ToList();
        var picture = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, new byte[width * height * 4], width * 4);
        return new CircleContent
        {
            Picture = picture,
            Pixels = new byte[width * height * 4],
            Area = new Int32Rect(0, 0, width, height),
            Words = list,
            Lines = [],
            Blocks = [],
            Text = text,
            Finds = Finder.Find(text, new DateTime(2026, 10, 27), CultureInfo.GetCultureInfo("en-GB"), "SAR"),
            Codes = [],
            Table = table,
            TextCover = cover,
        };
    }

    [Fact]
    public void What_the_text_holds_comes_first_then_what_words_can_do()
    {
        var c = Content("Visit dropwheel.app or call 055 123 4567", 7);
        var ids = CircleChoices.For(c, searchOn: false);
        Assert.Equal(["link", "call", "copy", "translate", "explain", "ask", "search", "read", "picturegroup"], ids);
        Assert.Contains("files", CircleChoices.For(c, searchOn: true));
    }

    [Fact]
    public void A_picture_gets_the_picture_petals_and_text_on_it_adds_the_word_petals()
    {
        var picture = Content("", 0, 0, width: 600, height: 400);
        var ids = CircleChoices.For(picture, false);
        Assert.Equal("describe", ids[0]);
        Assert.DoesNotContain("copy", ids);
        var poster = Content("Summer sale", 2, 0.02, width: 600, height: 400);
        var mixed = CircleChoices.For(poster, false);
        Assert.Equal("describe", mixed[0]);
        Assert.Contains("copy", mixed);
        Assert.Contains("translate", mixed);
    }

    [Fact]
    public void Long_text_gets_a_summary_and_a_table_its_table_petal()
    {
        var long_ = Content(string.Join(' ', Enumerable.Repeat("word", 70)), 70);
        Assert.Contains("summary", CircleChoices.For(long_, false));
        var table = new PictureTable([["a", "b"], ["c", "d"]], false);
        Assert.Contains("table", CircleChoices.For(Content("a b c d", 4, table: table), false));
    }

    [Fact]
    public void The_centre_shows_the_answer_when_there_is_one()
    {
        Assert.Equal("= 84", CircleChoices.Hub(Content("12 × (3 + 4)", 5), null, "SAR").Title);
        Assert.Equal("3.11 mi", CircleChoices.Hub(Content("a 5 km run", 3), null, "SAR").Title);
        var rates = new ExchangeRates(DateTime.UtcNow, new Dictionary<string, double> { ["USD"] = 1, ["SAR"] = 3.75 });
        Assert.Equal("≈ 93.75 SAR", CircleChoices.Hub(Content("$25", 1), rates, "SAR").Title);
        Assert.Equal("USD", CircleChoices.Target("SAR", "SAR"));
        Assert.Equal("SAR", CircleChoices.Target("USD", "SAR"));
        Assert.Equal("EUR", CircleChoices.Target("USD", "USD"));
    }

    [Fact]
    public void Every_petal_has_its_words_and_an_icon_that_exists()
    {
        foreach (var (id, label, name, icon) in CircleChoices.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(label), id);
            Assert.Equal(label.ToUpperInvariant(), label);
            Assert.False(string.IsNullOrWhiteSpace(name), id);
            Assert.True(Petal.App.Visuals.Icons.Keys.Contains(icon), $"{id}: no icon {icon}");
        }
        Assert.Equal(CircleChoices.All.Count, CircleChoices.All.Select(c => c.Id).Distinct().Count());
        Assert.All(CircleChoices.PictureGroup, id => Assert.Contains(CircleChoices.All, c => c.Id == id));
    }

    [Fact]
    public void A_click_on_a_photo_takes_the_photo_even_on_its_plain_sky()
    {
        // A white screen with a photo: a plain blue sky over a busy, noisy ground, with a dark frame.
        int w = 800, h = 600;
        var pixels = new byte[w * h * 4];
        var random = new Random(4);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int p = (y * w + x) * 4;
                byte r = 255, g = 255, b = 255;
                if (x >= 100 && x < 400 && y >= 100 && y < 300)
                {
                    bool edge = x < 102 || x >= 398 || y < 102 || y >= 298;
                    (r, g, b) = edge ? ((byte)40, (byte)40, (byte)40) : y < 200 ? ((byte)120, (byte)170, (byte)230) : ((byte)random.Next(60, 200), (byte)random.Next(60, 200), (byte)random.Next(60, 200));
                }
                pixels[p] = b;
                pixels[p + 1] = g;
                pixels[p + 2] = r;
                pixels[p + 3] = 255;
            }
        var region = PictureRegions.Around(pixels, w, h, 250, 150);
        Assert.NotNull(region);
        Assert.InRange(region.Value.X, 92, 104);
        Assert.InRange(region.Value.Y, 92, 104);
        Assert.InRange(region.Value.X + region.Value.Width, 396, 408);
        Assert.InRange(region.Value.Y + region.Value.Height, 296, 308);
        // Plain background far from anything is nothing to take.
        Assert.Null(PictureRegions.Around(pixels, w, h, 650, 500));
    }

    [Fact]
    public void A_translation_takes_the_paragraphs_own_colours()
    {
        int w = 200, h = 60;
        var pixels = new byte[w * h * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 250;
            pixels[i + 1] = 240;
            pixels[i + 2] = 240;
            pixels[i + 3] = 255;
        }
        // Dark blue strokes inside the word.
        for (int y = 22; y < 38; y++)
            for (int x = 30; x < 150; x += 3)
            {
                int p = (y * w + x) * 4;
                pixels[p] = 120;
                pixels[p + 1] = 40;
                pixels[p + 2] = 20;
            }
        var word = new PictureWord("Hello", 28, 20, 124, 20, 0);
        var line = new ScreenLine([word], CircleText.Box(word), false);
        var block = new ScreenBlock([line], line.Box);
        var (back, ink) = CircleTranslation.Colours(pixels, w, h, block);
        Assert.Equal(Color.FromRgb(240, 240, 250), back);
        Assert.InRange(ink.R, 15, 30);
        Assert.InRange(ink.B, 110, 130);
    }

    [Fact]
    public void The_translation_is_as_large_as_fits()
    {
        var size = OnSta(() =>
        {
            var text = new TextBlock { Text = "A rather long translation of a short line", TextWrapping = TextWrapping.Wrap };
            return (Wide: CircleTranslation.Fit(text, 2000, 30, 20, 8), Narrow: CircleTranslation.Fit(text, 120, 30, 20, 8));
        });
        Assert.Equal(20, size.Wide, 3);
        Assert.True(size.Narrow < 20 && size.Narrow >= 8, size.Narrow.ToString(CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Exchange_rates_are_read_from_the_feed()
    {
        var rates = Petal.Recipes.ExchangeRateService.Parse("""{"result":"success","time_last_update_unix":1793059201,"base_code":"USD","rates":{"USD":1,"SAR":3.75,"EUR":0.92,"BAD":"x"}}""");
        Assert.NotNull(rates);
        Assert.Equal(3.75, rates.PerDollar["SAR"]);
        Assert.False(rates.PerDollar.ContainsKey("BAD"));
        Assert.Equal(new DateTime(2026, 10, 27, 0, 0, 1, DateTimeKind.Utc), rates.Updated);
        Assert.Null(Petal.Recipes.ExchangeRateService.Parse("""{"result":"error","error-type":"unsupported-code"}"""));
        Assert.Null(Petal.Recipes.ExchangeRateService.Parse("not json"));
    }

    [Fact]
    public async Task A_loop_on_a_real_screen_reads_its_words_and_what_they_hold()
    {
        if (!DocumentEngine.IsOcrAvailable)
            return;
        // A screen of text as Windows draws it: two lines, and a price to convert.
        var (pixels, width, height) = OnSta(() => Render(["Lunch with the team at noon", "Total: $25.50 per person"]));
        var words = await DocumentEngine.ReadWordsAsync(pixels, width, height);
        var lines = CircleText.Lines(words);
        Assert.Equal(2, lines.Count);
        Assert.Contains("team", lines[0].Text);
        // A loop round the second line only.
        var second = lines[1].Box;
        var loop = Loop(second.X + second.Width / 2, second.Y + second.Height / 2, second.Width / 2 + 20, second.Height / 2 + 8);
        var taken = CircleText.Select(loop, words, width, height);
        var content = await CircleReader.ReadAsync(pixels, width, taken, loop, CancellationToken.None);
        Assert.Contains("per person", content.Text);
        Assert.DoesNotContain("team", content.Text);
        var money = Assert.Single(content.Finds, f => f.Kind == FoundKind.Money);
        Assert.Equal(25.5, money.Amount, 3);
        Assert.Equal("USD", money.Unit);
        Assert.Equal("money", CircleChoices.For(content, false)[0]);
    }

    /// <summary>Lines of text drawn the way a window shows them, as BGRA pixels.</summary>
    private static (byte[] Pixels, int Width, int Height) Render(string[] lines)
    {
        int width = 900, height = 60 + lines.Length * 48;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
            for (int i = 0; i < lines.Length; i++)
            {
                var text = new FormattedText(lines[i], CultureInfo.GetCultureInfo("en-US"), FlowDirection.LeftToRight, new Typeface("Segoe UI"), 26, Brushes.Black, 1);
                dc.DrawText(text, new Point(40, 30 + i * 48));
            }
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var pixels = new byte[width * height * 4];
        bitmap.CopyPixels(pixels, width * 4, 0);
        return (pixels, width, height);
    }

    private static T OnSta<T>(Func<T> work)
    {
        T result = default!;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { result = work(); }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null)
            throw new Xunit.Sdk.XunitException("On the STA thread: " + error);
        return result;
    }
}
