using System.Windows.Media;
using Petal.App.Words;
using Petal.Engines.Media;
using Petal.Recipes.Translation;
using static Petal.Tests.CaptionLinesTests;

namespace Petal.Tests;

/// <summary>
/// Nothing on a caption line changes at a stroke: a sentence moving up from the second line keeps its words as they
/// were and brightens on the way, its proper translation changes only the words that differ, and new words come in
/// out of nothing.
/// </summary>
[Collection("Language")]
public sealed class CaptionShadesTests
{
    private static StreamState Talk(string current) => new("", current, "", "de", 1);

    private static readonly Color Dim = Color.FromArgb(0x86, 0xFF, 0xFF, 0xFF);
    private static readonly Color Settled = Color.FromArgb(0xDC, 0xFF, 0xFF, 0xFF);

    [Fact]
    public void A_sentence_moved_up_starts_with_the_colours_the_second_line_gave_its_words()
    {
        OnCaptions("english", window =>
        {
            var time = TimeSpan.FromSeconds(100);
            window.Clock = () => time;
            window.ShowTalk(Talk("Wir werden den Start verschieben"));
            window.ShowLine(new LiveLine(1, false, "We will postpone the", "We will".Length, false));
            time += TimeSpan.FromSeconds(1);
            var second = window.SecondShades.ToDictionary(w => w.Word, w => w.ColorAt(time));
            Assert.Equal(Settled, second["will"]);
            Assert.Equal(Dim, second["the"]);

            window.ShowLine(new LiveLine(1, true, "We will postpone the", 0, false));
            // On the first line now: each word starts as it was on the second, nothing brighter and nothing out of nothing,
            // and goes over to the first line's shade in a moment.
            Assert.All(window.FirstShades, w => Assert.Equal(second[w.Word], w.ColorAt(time)));
            Assert.Contains(window.FirstShades, w => !w.Still(time));
            time += CaptionShades.Fade;
            Assert.All(window.FirstShades, w => Assert.True(w.Still(time)));
            Assert.Equal("We will postpone the", window.Shown.Main);
        });
    }

    [Fact]
    public void The_proper_translation_changes_only_the_words_that_differ()
    {
        OnCaptions("english", window =>
        {
            var time = TimeSpan.FromSeconds(100);
            window.Clock = () => time;
            window.ShowTalk(Talk("Wir werden den Start verschieben"));
            window.ShowLine(new LiveLine(1, false, "We will the launch move", 7, false));
            window.ShowLine(new LiveLine(1, true, "We will the launch move", 0, false));
            time += TimeSpan.FromSeconds(2);
            var before = window.FirstShades.Select(w => w.ColorAt(time)).Distinct().Single();

            window.ShowLine(new LiveLine(1, true, "We will move the launch.", 0, true));
            var words = window.FirstShades.ToDictionary(w => w.Word);
            // The words it had brighten from where they stood ("launch" only gained its full stop); the one now in a new
            // place comes in out of nothing.
            foreach (var kept in new[] { "We", "will", "the", "launch." })
                Assert.Equal(before, words[kept].From);
            Assert.Equal(0, words["move"].From.A);
            Assert.All(window.FirstShades, w => Assert.Equal(Colors.White, w.To));
            Assert.Equal("We will move the launch.", window.Shown.Main);
        });
    }

    [Fact]
    public void Words_on_their_way_keep_going_when_the_line_is_written_again()
    {
        var shades = new CaptionShades();
        var at = TimeSpan.FromSeconds(10);
        shades.Update([("one", Dim), ("two", Dim)], at);
        shades.Update([("one", Dim), ("two", Dim), ("three", Dim)], at + TimeSpan.FromMilliseconds(100));
        Assert.Equal(at, shades.Words[0].At);
        Assert.Equal(at, shades.Words[1].At);
        Assert.Equal(0, shades.Words[2].From.A);

        // A word that settles goes from where it stood to its new shade, not from the start.
        var later = at + TimeSpan.FromMilliseconds(150);
        var halfway = shades.Words[0].ColorAt(later);
        Assert.InRange(halfway.A, 1, Dim.A - 1);
        shades.Update([("one", Settled), ("two", Dim), ("three", Dim)], later);
        Assert.Equal(halfway, shades.Words[0].From);
        Assert.Equal(Settled, shades.Words[0].To);
        Assert.Equal(halfway, shades.Words[0].ColorAt(later));
    }

    [Fact]
    public void Words_are_matched_by_what_the_lines_have_in_common_in_order()
    {
        var shades = new CaptionShades();
        shades.Update([("a", Dim), ("b", Dim), ("c", Dim), ("d", Dim)], TimeSpan.Zero);
        Assert.Equal(new int?[] { 0, null, 2, 3 }, CaptionShades.Match(shades.Words, [("a", Dim), ("x", Dim), ("c", Dim), ("d.", Dim)]));
        Assert.Equal(new int?[] { null, 0, 1 }, CaptionShades.Match(shades.Words, [("z", Dim), ("a", Dim), ("b", Dim)]));
    }

    [Fact]
    public void A_picture_of_the_window_has_every_word_at_its_colour()
    {
        OnCaptions("english", window =>
        {
            window.Show("Wir werden den Start verschieben.", "We will postpone the launch.", "And we will");
            var now = window.Clock();
            Assert.NotEmpty(window.FirstShades);
            Assert.All(window.FirstShades, w => Assert.True(w.Still(now)));
            Assert.All(window.SecondShades, w => Assert.True(w.Still(now)));
        });
    }
}
