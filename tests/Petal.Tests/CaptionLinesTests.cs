using System.Runtime.ExceptionServices;
using System.Windows;
using Petal.App.Settings;
using Petal.App.Words;
using Petal.Engines.Media;
using Petal.Recipes.Translation;

namespace Petal.Tests;

/// <summary>
/// A translation has two lines: the first holds only finished sentences, the second the sentence being said, word by
/// word, and a sentence moves up to the first line the moment it is finished.
/// </summary>
[Collection("Language")]
public sealed class CaptionLinesTests
{
    internal static void OnCaptions(string show, Action<CaptionWindow> test)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            string before = SettingsStore.Current.CaptionsShow;
            try
            {
                SettingsStore.Current.CaptionsShow = show;
                var window = new CaptionWindow { ShowActivated = false };
                window.Loaded += (_, _) => { window.Left = -20000; window.Top = -20000; };
                window.Show();
                window.UpdateLayout();
                try { test(window); }
                finally { window.Close(); }
            }
            catch (Exception e) { failure = ExceptionDispatchInfo.Capture(e); }
            finally
            {
                SettingsStore.Current.CaptionsShow = before;
                System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        failure?.Throw();
    }

    private static StreamState Talk(string current) => new("", current, "", "de", 1);

    private static LiveLine Saying(int number, string text, int steady = 0) => new(number, false, text, steady, false);

    private static LiveLine Finished(int number, string text, bool proper = true) => new(number, true, text, 0, proper);

    [Fact]
    public void The_sentence_being_said_is_on_the_second_line_until_it_is_finished()
    {
        OnCaptions("english", window =>
        {
            window.ShowTalk(Talk("Wir werden"));
            window.ShowLine(Saying(1, "We will"));
            Assert.Equal("", window.Shown.Main);
            Assert.Equal("We will", window.Shown.Live);

            window.ShowLine(Finished(1, "We will move the launch."));
            Assert.Equal("We will move the launch.", window.Shown.Main);
            Assert.Equal("", window.Shown.Live);
        });
    }

    [Fact]
    public void It_moves_up_at_once_as_it_stood_and_its_proper_translation_takes_its_place()
    {
        OnCaptions("english", window =>
        {
            window.ShowTalk(Talk("Wir werden den Start verschieben"));
            window.ShowLine(Saying(1, "We will the launch move", 16));
            // The sentence ended: what the second line held is on the first before the proper translation is made.
            window.ShowLine(Finished(1, "We will the launch move", proper: false));
            Assert.Equal("We will the launch move", window.Shown.Main);
            Assert.Equal("", window.Shown.Live);
            window.ShowLine(Finished(1, "We will postpone the launch."));
            Assert.Equal("We will postpone the launch.", window.Shown.Main);
        });
    }

    [Fact]
    public void It_moves_up_the_moment_the_talk_goes_on_even_before_the_translator_has_got_to_it()
    {
        OnCaptions("english", window =>
        {
            window.ShowTalk(new StreamState("", "Wir werden den Start verschieben", "", "de", 1, 1));
            window.ShowLine(Saying(1, "We will the launch move", 16));
            // The next sentence has begun, and the translator is still busy with something else.
            window.ShowTalk(new StreamState("Wir werden den Start verschieben.", "Und", "dann", "de", 2, 2));
            Assert.Equal("We will the launch move", window.Shown.Main);
            Assert.Equal("", window.Shown.Live);
            // Its proper translation takes its place when it comes, and the next sentence has the second line.
            window.ShowLine(Saying(2, "And then"));
            window.ShowLine(Finished(1, "We will postpone the launch."));
            Assert.Equal("We will postpone the launch.", window.Shown.Main);
            Assert.Equal("And then", window.Shown.Live);
        });
    }

    [Fact]
    public void The_settled_words_of_the_second_line_are_brighter_than_its_newest()
    {
        OnCaptions("english", window =>
        {
            window.ShowTalk(Talk("Wir werden den Start"));
            window.ShowLine(Saying(1, "We will the launch", "We will".Length));
            Assert.Equal("We will the launch", window.Shown.Live);
            Assert.Equal("We will", window.SteadyShown);
        });
    }

    [Fact]
    public void The_first_line_holds_only_the_last_finished_sentence()
    {
        OnCaptions("english", window =>
        {
            var time = TimeSpan.FromSeconds(100);
            window.Clock = () => time;
            window.ShowTalk(Talk("Eins"));
            window.ShowLine(Finished(1, "One sentence."));
            // Long enough on screen to have been read.
            time += TimeSpan.FromSeconds(4);
            window.ShowLine(Finished(2, "Another sentence."));
            Assert.Equal("Another sentence.", window.Shown.Main);
            Assert.DoesNotContain("One sentence", window.Shown.Main);
        });
    }

    [Fact]
    public void A_short_sentence_followed_at_once_by_another_stays_beside_it_to_be_read()
    {
        OnCaptions("english", window =>
        {
            var time = TimeSpan.FromSeconds(100);
            window.Clock = () => time;
            window.ShowTalk(Talk("Ja"));
            window.ShowLine(Finished(1, "Yes."));
            time += TimeSpan.FromSeconds(0.3);
            window.ShowLine(Finished(2, "I agree."));
            Assert.Equal("Yes. I agree.", window.Shown.Main);
            // The one after has the line to itself again once this one has been read.
            time += TimeSpan.FromSeconds(3);
            window.ShowLine(Finished(3, "Let us begin."));
            Assert.Equal("Let us begin.", window.Shown.Main);
        });
    }

    [Fact]
    public void The_next_sentence_starts_on_the_second_line_and_the_first_stays()
    {
        OnCaptions("english", window =>
        {
            window.ShowTalk(Talk("Eins"));
            window.ShowLine(Finished(1, "One sentence."));
            window.ShowLine(Saying(2, "And the"));
            Assert.Equal("One sentence.", window.Shown.Main);
            Assert.Equal("And the", window.Shown.Live);
        });
    }

    [Fact]
    public void A_late_draft_of_a_finished_sentence_is_let_go()
    {
        OnCaptions("english", window =>
        {
            window.ShowTalk(Talk("Eins"));
            window.ShowLine(Finished(1, "One sentence."));
            window.ShowLine(Saying(1, "One sen"));
            Assert.Equal("One sentence.", window.Shown.Main);
            Assert.Equal("", window.Shown.Live);
            // And so is an older finished sentence that comes after a newer one.
            window.ShowLine(Finished(3, "A third."));
            window.ShowLine(Finished(2, "A second."));
            Assert.DoesNotContain("second", window.Shown.Main);
        });
    }

    [Fact]
    public void What_was_said_stays_small_above_the_two_lines()
    {
        OnCaptions("both", window =>
        {
            window.ShowTalk(Talk("Wir werden"));
            window.ShowLine(Saying(1, "We will"));
            Assert.Contains("Wir werden", window.Shown.Small);
            Assert.Equal("We will", window.Shown.Live);
        });
    }

    [Fact]
    public void Without_a_translation_there_is_no_second_line()
    {
        OnCaptions("original", window =>
        {
            window.ShowTalk(new StreamState("", "Wir werden", "den Start", "de", 1));
            window.ShowLine(Saying(1, "We will"));
            Assert.Equal("Wir werden den Start", window.Shown.Main);
            Assert.Equal("", window.Shown.Live);
        });
    }
}
