using System.IO;
using System.Windows;
using Petal.Engines.Documents;
using Petal.Engines.Documents.Ocr;
using Petal.Recipes.Translation;
using Color = System.Windows.Media.Color;
using Colors = System.Windows.Media.Colors;

namespace Petal.Tests;

/// <summary>Translating the text on a picture where it stands: what goes with what, how it looks, and what comes off.</summary>
public sealed class PictureTranslationTests
{
    private static ReadLine Line(string text, double x, double y, double width, double height = 40) =>
        new(text, new Rect(x, y, width, height), text.Split(' ').Length, false, height);

    private static PictureWord Word(string text, double x, double y, double width, int line, double height = 40) => new(text, x, y, width, height, line);

    [Fact]
    public void A_line_with_a_wide_gap_is_two_things_side_by_side()
    {
        var lines = PictureTranslation.LinesOf([Word("Lentil", 100, 100, 100, 0), Word("soup", 210, 100, 80, 0), Word("$4", 900, 100, 40, 0)]);
        Assert.Equal(["Lentil soup", "$4"], lines.Select(l => l.Text));
    }

    [Fact]
    public void An_arabic_line_comes_out_in_reading_order()
    {
        var lines = PictureTranslation.LinesOf([Word("الدخول", 100, 100, 120, 0), Word("ممنوع", 230, 100, 100, 0)]);
        Assert.Equal("ممنوع الدخول", Assert.Single(lines).Text);
        Assert.True(lines[0].RightToLeft);
    }

    [Fact]
    public void Lines_that_run_on_are_one_paragraph()
    {
        var blocks = PictureTranslation.Blocks([
            Line("Compound words are made when two separate", 100, 100, 800),
            Line("words come together and form a new", 100, 150, 790),
            Line("meaning of their own.", 100, 200, 400),
        ], 1000);
        var block = Assert.Single(blocks);
        Assert.Equal("Compound words are made when two separate words come together and form a new meaning of their own.", block.Source);
        Assert.Equal(3, block.Lines.Count);
    }

    [Fact]
    public void A_list_stays_a_list()
    {
        var blocks = PictureTranslation.Blocks([
            Line("Lentil soup", 100, 100, 200),
            Line("Fresh orange juice", 100, 150, 330),
            Line("Chocolate cake", 100, 200, 280),
        ], 1000);
        Assert.Equal(3, blocks.Count);
    }

    [Fact]
    public void A_sentence_that_ended_doesnt_run_on_and_nor_does_another_size()
    {
        Assert.Equal(2, PictureTranslation.Blocks([Line("This sentence ends right here at last.", 100, 100, 800), Line("And another one starts here now.", 100, 150, 700)], 1000).Count);
        Assert.Equal(2, PictureTranslation.Blocks([Line("A heading in three words", 100, 100, 800, 70), Line("and some text set far smaller below", 100, 180, 800, 30)], 1000).Count);
    }

    [Fact]
    public void Columns_side_by_side_stay_apart()
    {
        var blocks = PictureTranslation.Blocks([Line("The left column has this", 100, 100, 450), Line("The right column has this", 600, 100, 450), Line("and it goes on down here", 100, 150, 450), Line("and it goes on here too", 600, 150, 450)], 1200);
        Assert.Equal(2, blocks.Count);
        Assert.StartsWith("The left", blocks[0].Source);
        Assert.EndsWith("down here", blocks[0].Source);
    }

    [Theory]
    [InlineData("Our Menu", true)]
    [InlineData("ممنوع الدخول", true)]
    [InlineData("10 in", true)]
    [InlineData("Go to", true)]
    [InlineData("مثال: kaynana", true)]
    [InlineData("$4", false)]
    [InlineData("123", false)]
    [InlineData("x", false)]
    [InlineData("Gra de-e e وi l)", false)]
    [InlineData("#@! ~~ %% a", false)]
    [InlineData("a b c d e f", false)]
    public void Only_what_was_read_cleanly_is_translated(string text, bool trusted) =>
        Assert.Equal(trusted, PictureTranslation.Trusted(text));

    /// <summary>A plain picture with a dark bar for a word on it.</summary>
    private static byte[] Picture(int width, int height, Color paper, Color ink, Rect? word = null)
    {
        var pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                // Strokes every 8 pixels, about the width of a letter's.
                bool on = word is { } r && r.Contains(new Point(x, y)) && (x - (int)r.X) % 8 < 3;
                var c = on ? ink : paper;
                int i = (y * width + x) * 4;
                (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]) = (c.B, c.G, c.R, 255);
            }
        return pixels;
    }

    private static PictureBlock Block(string source, Rect box, PictureAlign align = PictureAlign.Left, bool rtl = false, string translation = "") => new()
    {
        Source = source,
        Lines = [box],
        Box = box,
        LineHeight = box.Height,
        Align = align,
        RightToLeft = rtl,
        Translation = translation,
        Size = box.Height,
        Column = box,
        Shape = new Rect(0, 0, 400, 200),
    };

    [Fact]
    public void A_blocks_colours_are_read_off_the_picture()
    {
        var (paper, ink) = (Color.FromRgb(180, 20, 30), Color.FromRgb(255, 240, 200));
        var box = new Rect(50, 50, 200, 40);
        var block = Block("Warning", box);
        PictureTranslation.Look(block, Picture(400, 200, paper, ink, box), 400, 200);
        Assert.Equal(paper, block.Paper);
        Assert.Equal(ink, block.Ink);
        Assert.True(block.Plain);
    }

    [Fact]
    public void Noise_around_the_text_isnt_plain()
    {
        var random = new Random(7);
        var pixels = new byte[400 * 200 * 4];
        random.NextBytes(pixels);
        var block = Block("Sale", new Rect(50, 50, 200, 40));
        PictureTranslation.Look(block, pixels, 400, 200);
        Assert.False(block.Plain);
    }

    [Fact]
    public void A_gradient_is_plain_and_carries_on_under_the_text()
    {
        var pixels = new byte[400 * 200 * 4];
        for (int y = 0; y < 200; y++)
            for (int x = 0; x < 400; x++)
            {
                int i = (y * 400 + x) * 4;
                (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]) = ((byte)(x / 2), 40, 20, 255);
            }
        var box = new Rect(50, 50, 300, 40);
        var clean = (byte[])pixels.Clone();
        for (int y = 50; y < 90; y++)
            for (int x = 50; x < 350; x += 3)
                (pixels[(y * 400 + x) * 4], pixels[(y * 400 + x) * 4 + 1]) = (255, 255);
        var block = Block("Words", box);
        PictureTranslation.Look(block, pixels, 400, 200);
        Assert.True(block.Plain);
        var work = new PictureWork { Original = pixels, Width = 400, Height = 200, Blocks = [block] };
        PictureTranslation.EraseAsync(work).GetAwaiter().GetResult();
        int worst = 0;
        for (int i = 0; i < clean.Length; i++)
            worst = Math.Max(worst, Math.Abs(clean[i] - work.Clean![i]));
        Assert.True(worst <= 4, $"Off by {worst}");
    }

    private static PictureWork Work(params PictureBlock[] blocks) =>
        new() { Original = Picture(400, 200, Colors.White, Colors.Black, new Rect(40, 40, 200, 40)), Width = 400, Height = 200, Blocks = blocks };

    /// <summary>How many pixels of a part of the picture aren't white.</summary>
    private static int Marked(byte[] pixels, Rect area)
    {
        int marked = 0;
        for (int y = (int)area.Top; y < area.Bottom; y++)
            for (int x = (int)area.Left; x < area.Right; x++)
                if (pixels[(y * 400 + x) * 4] < 200)
                    marked++;
        return marked;
    }

    [Theory]
    [InlineData("en", "Ready")]
    [InlineData("ar", "جاهز")]
    public void The_translation_is_drawn_where_the_original_was(string into, string text)
    {
        var block = Block("Prêt", new Rect(40, 40, 200, 40), rtl: false, translation: text);
        block.Ink = Colors.Black;
        var work = Work(block);
        PictureTranslation.EraseAsync(work).GetAwaiter().GetResult();
        Assert.Equal(0, Marked(work.Clean!, new Rect(40, 40, 200, 40)));
        var done = PictureTranslation.Compose(work, into);
        Assert.True(Marked(done, new Rect(40, 30, 220, 70)) > 30);
        Assert.Equal(0, Marked(done, new Rect(0, 120, 400, 80)));
    }

    [Fact]
    public void Going_to_a_language_that_reads_the_other_way_goes_to_the_other_side_of_the_column()
    {
        var box = new Rect(40, 40, 60, 30);
        var block = Block("Soup", box, translation: "شوربة");
        block.Column = new Rect(40, 40, 300, 30);
        block.Ink = Colors.Black;
        var work = new PictureWork { Original = Picture(400, 200, Colors.White, Colors.Black), Width = 400, Height = 200, Blocks = [block] };
        PictureTranslation.EraseAsync(work).GetAwaiter().GetResult();
        var done = PictureTranslation.Compose(work, "ar");
        Assert.True(Marked(done, new Rect(240, 30, 110, 60)) > 20, "Not at the column's right");
        Assert.Equal(0, Marked(done, new Rect(0, 30, 200, 60)));
    }

    [Fact]
    public void What_is_kept_stays_as_it_was()
    {
        var box = new Rect(40, 40, 200, 40);
        var block = Block("Brand", box, translation: "Marque");
        block.Keep = true;
        var work = Work(block);
        PictureTranslation.EraseAsync(work).GetAwaiter().GetResult();
        Assert.Equal(work.Original, work.Clean);
        Assert.Equal(work.Original, PictureTranslation.Compose(work, "fr"));
    }

    [Fact]
    public void A_block_kept_again_after_coming_off_comes_back()
    {
        var box = new Rect(40, 40, 200, 40);
        var block = Block("Brand", box, translation: "Marque");
        var work = Work(block);
        PictureTranslation.EraseAsync(work).GetAwaiter().GetResult();
        block.Keep = true;
        Assert.Equal(work.Original, PictureTranslation.Compose(work, "fr"));
    }

    [Fact]
    public void Too_long_a_translation_goes_smaller_and_says_so()
    {
        var block = Block("Go", new Rect(180, 90, 40, 20), PictureAlign.Centre, translation: string.Join(' ', Enumerable.Repeat("a very long translation indeed", 30)));
        block.Ink = Colors.Black;
        block.Shape = new Rect(150, 80, 100, 40);
        var work = new PictureWork { Original = Picture(400, 200, Colors.White, Colors.Black), Width = 400, Height = 200, Blocks = [block] };
        PictureTranslation.EraseAsync(work).GetAwaiter().GetResult();
        PictureTranslation.Compose(work, "en");
        Assert.True(block.Tight);
    }

    [Fact]
    public void A_line_in_the_middle_of_its_button_is_centred()
    {
        var label = Block("Save", new Rect(150, 90, 100, 20));
        label.Shape = new Rect(100, 80, 200, 40);
        var free = Block("Title here", new Rect(10, 10, 100, 20));
        free.Shape = new Rect(0, 0, 400, 200);
        var blocks = new[] { label, free };
        foreach (var b in blocks)
            b.Size = 20;
        PictureTranslation.Arrange(blocks, 400, 200, null);
        // Arranging without the pixels sees no shapes: the whole picture is every line's shape.
        Assert.Equal(PictureAlign.Left, free.Align);
        var pixels = Picture(400, 200, Colors.White, Colors.Black);
        for (int y = 80; y < 120; y++)
            for (int x = 100; x < 300; x++)
            {
                int i = (y * 400 + x) * 4;
                (pixels[i], pixels[i + 1], pixels[i + 2]) = (200, 80, 40);
            }
        var button = Block("Save", new Rect(150, 90, 100, 20));
        PictureTranslation.Look(button, pixels, 400, 200);
        PictureTranslation.Arrange([button], 400, 200, pixels);
        Assert.Equal(PictureAlign.Centre, button.Align);
        Assert.Equal(200, button.Shape.Width, 1);
    }

    [Fact]
    public void Lines_starting_at_one_edge_are_a_column_and_of_one_style_are_one_size()
    {
        var a = Block("Lentil soup", new Rect(100, 100, 200, 43));
        var b = Block("Chicken with rice", new Rect(102, 150, 430, 33));
        PictureTranslation.Arrange([a, b], 1000, 800, null);
        Assert.Equal(532, a.Column.Right, 1);
        Assert.Equal(532, b.Column.Right, 1);
        Assert.True(Math.Abs(a.Size - b.Size) < a.Size * 0.3, $"{a.Size} and {b.Size}");
    }

    [Fact]
    public void Picture_prompts_say_the_text_was_read_by_a_machine_and_give_the_rest_of_the_picture()
    {
        var prompt = GemmaTranslation.Prompt("Lentil soup", "en", "ar", ["Our Menu"], picture: true);
        Assert.Contains("misread", prompt);
        Assert.Contains("Other text on the same picture", prompt);
        Assert.Contains("Our Menu", prompt);
        Assert.DoesNotContain("misread", GemmaTranslation.Prompt("Lentil soup", "en", "ar", null));
        Assert.Contains("misread", GemmaTranslation.ProofPrompt("a", "b", "en", "ar", picture: true));
    }

    [Fact]
    public void Each_block_is_read_with_those_nearest_to_it()
    {
        var blocks = new[] { "One", "Two", "Three", "Four" }.Select(s => Block(s, new Rect(0, 0, 10, 10))).ToList();
        Assert.Equal(["Two", "Four", "One"], PictureTranslation.Around(blocks, 2));
    }

    [Fact]
    public async Task A_picture_is_read_for_real()
    {
        if (!DocumentEngine.OcrLanguages.Any(l => l.StartsWith("en", StringComparison.OrdinalIgnoreCase)))
            return;
        // Letters drawn large on a plain ground: Windows reads them, and they are one block in dark on light.
        var pixels = Picture(800, 200, Colors.White, Colors.Black);
        var target = new System.Windows.Media.Imaging.RenderTargetBitmap(800, 200, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        var thread = new Thread(() =>
        {
            var visual = new System.Windows.Media.DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(System.Windows.Media.Brushes.White, null, new Rect(0, 0, 800, 200));
                dc.DrawText(new System.Windows.Media.FormattedText("Fresh orange juice", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new System.Windows.Media.Typeface("Segoe UI"), 60, System.Windows.Media.Brushes.Black, 1.0), new Point(60, 50));
            }
            var own = new System.Windows.Media.Imaging.RenderTargetBitmap(800, 200, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            own.Render(visual);
            own.CopyPixels(pixels, 800 * 4, 0);
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        _ = target;
        var work = await PictureTranslation.ReadAsync(pixels, 800, 200);
        var block = Assert.Single(work.Blocks);
        Assert.Equal("Fresh orange juice", block.Source);
        Assert.True(block.Plain);
        Assert.False(block.Keep);
        Assert.Equal(PictureAlign.Left, block.Align);
        Assert.Equal(Colors.Black, block.Ink);
        Assert.InRange(block.Size, 45, 80);
    }

    [Fact]
    public void A_copy_keeps_the_pictures_name_and_says_its_language()
    {
        var folder = Path.Combine(Path.GetTempPath(), "Petal", "picture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            Assert.Equal("menu.ar.jpg", Path.GetFileName(Petal.App.Tools.Windows.TranslatePictureWindow.OutputFor(Path.Combine(folder, "menu.jpg"), "ar")));
            Assert.Equal("scan.fr.png", Path.GetFileName(Petal.App.Tools.Windows.TranslatePictureWindow.OutputFor(Path.Combine(folder, "scan.png"), "fr")));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }
}
