using Petal.Core.Imaging;
using Petal.Core.Text;

namespace Petal.Tests;

/// <summary>The text tools, the calculator and the scrolling capture's stitching.</summary>
public sealed class TextToolTests
{
    [Theory]
    [InlineData("ؤشف", "cat")]
    [InlineData("اثممخ", "hello")]
    [InlineData("مهلاف", "light")]
    [InlineData("لاخخن", "book")]
    [InlineData("لاعف ىخص", "but now")]
    public void Arabic_typed_for_English_is_put_right(string typed, string meant) =>
        Assert.Equal(meant, KeyboardLayout.Fix(typed));

    [Theory]
    [InlineData("hgsghl ugd;l", "السلام عليكم")]
    [InlineData("lvpfh", "مرحبا")]
    [InlineData("a;vh", "شكرا")]
    public void English_typed_for_Arabic_is_put_right(string typed, string meant) =>
        Assert.Equal(meant, KeyboardLayout.Fix(typed));

    [Fact]
    public void Fixing_twice_gives_the_text_back()
    {
        foreach (var word in new[] { "keyboard", "petal", "screen" })
            Assert.Equal(word, KeyboardLayout.Fix(KeyboardLayout.Fix(word)));
    }

    [Fact]
    public void Text_tools_do_what_they_say()
    {
        Assert.Equal("محمد", TextTools.RemoveDiacritics("مُحَمَّدٌ"));
        Assert.Equal("كتــاب".Replace("ـ", ""), TextTools.RemoveDiacritics("كتــاب"));
        Assert.Equal("٢٠٢٦ و١٥", TextTools.SwapDigits("2026 و15"));
        Assert.Equal("2026 و15", TextTools.SwapDigits("٢٠٢٦ و١٥"));
        Assert.Equal("Hello World Again", TextTools.Title("hello WORLD again"));
        Assert.Equal("One. Two? Three", TextTools.Sentence("ONE. two? three"));
        Assert.Equal("a b" + Environment.NewLine + Environment.NewLine + "c", TextTools.TidySpaces("  a   b \n\n\n\n c  \n"));
        var count = TextTools.Count("مرحبا يا عالم. Hello there!\nSecond line");
        Assert.Equal(7, count.Words);
        Assert.Equal(2, count.Lines);
        Assert.Equal(2, count.Sentences);
    }

    [Theory]
    [InlineData("2+3*4", 14)]
    [InlineData("(2+3)*4", 20)]
    [InlineData("2^10", 1024)]
    [InlineData("12% of 340", 40.8)]
    [InlineData("340 + 12%", 380.8)]
    [InlineData("200 - 25%", 150)]
    [InlineData("50%", 0.5)]
    [InlineData("sqrt 16 + 1", 5)]
    [InlineData("١٢ × ٣", 36)]
    [InlineData("12,500.5 / 2", 6250.25)]
    [InlineData("3,5 * 2", 7)]
    [InlineData("-(4 - 10)", 6)]
    [InlineData("sin 30", 0.5)]
    [InlineData("10 ÷ 4 =", 2.5)]
    public void Sums_are_worked_out(string sum, double answer)
    {
        var result = Calculator.Evaluate(sum);
        Assert.NotNull(result);
        Assert.Equal(answer, result.Value, 6);
    }

    [Theory]
    [InlineData("5 km in mi", 3.10686, "mi")]
    [InlineData("70 kg to lb", 154.3236, "lb")]
    [InlineData("٧٠ كغ بالرطل", 154.3236, "lb")]
    [InlineData("98.6 f to c", 37, "°C")]
    [InlineData("100 c in f", 212, "°F")]
    [InlineData("1.5 GB in MB", 1500, "MB")]
    [InlineData("90 min in h", 1.5, "h")]
    [InlineData("100 كم/س إلى mph", 62.1371, "mph")]
    [InlineData("2 * 3 m in cm", 600, "cm")]
    public void Units_are_converted(string text, double answer, string unit)
    {
        var result = Calculator.Evaluate(text);
        Assert.NotNull(result);
        Assert.Equal(answer, result.Value, 3);
        Assert.Equal(unit, result.Unit);
    }

    [Theory]
    [InlineData("hello")]
    [InlineData("5 km in kg")]
    [InlineData("(2+")]
    [InlineData("")]
    public void Nonsense_is_not_a_sum(string text) => Assert.Null(Calculator.Evaluate(text));

    [Fact]
    public void Answers_read_cleanly()
    {
        Assert.Equal("0.3", Calculator.Format(0.1 + 0.2));
        Assert.Equal("1,234,567", Calculator.Format(1234567));
        Assert.Equal("3.141592654", Calculator.Format(Math.PI));
    }

    [Fact]
    public void A_scrolled_page_is_stitched_without_repeating_rows()
    {
        const int width = 60, frame = 80, pageHeight = 300, step = 30;
        var page = Page(width, pageHeight);
        var stitcher = new ScrollStitcher(width, frame);
        for (int top = 0; top + frame <= pageHeight; top += step)
            Assert.True(stitcher.Add(Slice(page, width, top, frame)) > 0);
        // Scrolled to the end: the same frame again adds nothing.
        Assert.Equal(0, stitcher.Add(Slice(page, width, pageHeight - frame - (pageHeight - frame) % step, frame)));
        int expected = frame + (pageHeight - frame) / step * step;
        Assert.Equal(expected, stitcher.Height);
        Assert.Equal(Slice(page, width, 0, expected), stitcher.Result());
    }

    [Fact]
    public void A_sticky_header_and_footer_appear_once()
    {
        const int width = 60, frame = 80, pageHeight = 260, step = 20, header = 8, footer = 6;
        var page = Page(width, pageHeight);
        var stitcher = new ScrollStitcher(width, frame);
        for (int top = 0; top + frame - header - footer <= pageHeight; top += step)
        {
            var shot = Slice(page, width, top, frame);
            // The same header and footer cover the top and the bottom of every frame.
            for (int y = 0; y < header; y++) Fill(shot, width, y, 200);
            for (int y = frame - footer; y < frame; y++) Fill(shot, width, y, 90);
            stitcher.Add(shot);
        }
        var result = stitcher.Result();
        int headers = 0, footers = 0;
        for (int y = 0; y < stitcher.Height; y++)
        {
            if (result[y * width * 4 + 120] == 200 && result[y * width * 4 + 124] == 200) headers++;
            if (result[y * width * 4 + 120] == 90 && result[y * width * 4 + 124] == 90) footers++;
        }
        Assert.Equal(header, headers);
        Assert.Equal(footer, footers);
    }

    /// <summary>A page whose every row differs: stripes and noise that a match can't confuse.</summary>
    private static byte[] Page(int width, int height)
    {
        var random = new Random(7);
        var page = new byte[width * height * 4];
        random.NextBytes(page);
        for (int i = 3; i < page.Length; i += 4) page[i] = 255;
        return page;
    }

    private static byte[] Slice(byte[] page, int width, int top, int height)
    {
        var slice = new byte[width * height * 4];
        Buffer.BlockCopy(page, top * width * 4, slice, 0, slice.Length);
        return slice;
    }

    private static void Fill(byte[] frame, int width, int y, byte value)
    {
        for (int x = 0; x < width * 4; x++) frame[y * width * 4 + x] = value;
    }
}
