using System.Runtime.ExceptionServices;
using System.Windows;
using Petal.App.Settings;
using Petal.App.Words;
using Petal.Engines.Media;

namespace Petal.Tests;

/// <summary>
/// A translation has two lines: the first holds only the last finished sentence, the second the sentence being
/// said, and a sentence moves up to the first line as soon as it is finished.
/// </summary>
[Collection("Language")]
public sealed class CaptionLinesTests
{
    private static void OnCaptions(string show, Action<CaptionWindow> test)
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

    [Fact]
    public void The_sentence_being_said_is_on_the_second_line_until_it_is_finished()
    {
        OnCaptions("english", window =>
        {
            window.ShowTalk(Talk("Wir werden"));
            window.ShowTranslation("We will", final: false, 1);
            Assert.Equal("", window.Shown.Main);
            Assert.Equal("We will", window.Shown.Live);

            window.ShowTranslation("We will move the launch.", final: true, 1);
            Assert.Equal("We will move the launch.", window.Shown.Main);
            Assert.Equal("", window.Shown.Live);
        });
    }

    [Fact]
    public void The_first_line_holds_only_the_last_finished_sentence()
    {
        OnCaptions("english", window =>
        {
            window.ShowTalk(Talk("Eins"));
            window.ShowTranslation("One sentence.", final: true, 1);
            window.ShowTranslation("Another sentence.", final: true, 2);
            Assert.Equal("Another sentence.", window.Shown.Main);
            Assert.DoesNotContain("One sentence", window.Shown.Main);
        });
    }

    [Fact]
    public void The_next_sentence_starts_on_the_second_line_and_the_first_stays()
    {
        OnCaptions("english", window =>
        {
            window.ShowTalk(Talk("Eins"));
            window.ShowTranslation("One sentence.", final: true, 1);
            window.ShowTranslation("And the", final: false, 2);
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
            window.ShowTranslation("One sentence.", final: true, 1);
            window.ShowTranslation("One sen", final: false, 1);
            Assert.Equal("One sentence.", window.Shown.Main);
            Assert.Equal("", window.Shown.Live);
        });
    }

    [Fact]
    public void What_was_said_stays_small_above_the_two_lines()
    {
        OnCaptions("both", window =>
        {
            window.ShowTalk(Talk("Wir werden"));
            window.ShowTranslation("We will", final: false, 1);
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
            window.ShowTranslation("We will", final: false, 1);
            Assert.Equal("Wir werden den Start", window.Shown.Main);
            Assert.Equal("", window.Shown.Live);
        });
    }
}
