using Petal.Engines.Media;
using Petal.Recipes.Translation;

namespace Petal.Tests;

/// <summary>
/// The lane that translates live captions: the sentence being said word by word, each finished sentence properly,
/// finished sentences first. A translator that writes its answers in capitals stands in for the model.
/// </summary>
public sealed class LiveInterpreterTests
{
    /// <summary>Puts words into "English" by writing them in capitals; a finished sentence gets a full stop and brackets.</summary>
    private sealed class Capitals(int free = 1000) : ILiveTranslator
    {
        public List<string> SentencesAsked { get; } = [];
        public List<(string Text, string Begun)> SoFarAsked { get; } = [];
        public List<IReadOnlyList<string>> BeforeGiven { get; } = [];

        /// <summary>Held shut to make translating a sentence take as long as a test wants.</summary>
        public SemaphoreSlim Gate { get; } = new(free);

        public async Task<string?> SentenceAsync(string text, string from, string to, IReadOnlyList<string> before, CancellationToken cancellationToken)
        {
            await Gate.WaitAsync(cancellationToken);
            lock (SentencesAsked)
            {
                SentencesAsked.Add(text);
                BeforeGiven.Add(before.ToList());
            }
            return "[" + text.ToUpperInvariant() + "]";
        }

        public Task<string?> SoFarAsync(string text, string from, string to, IReadOnlyList<string> before, string begun, CancellationToken cancellationToken)
        {
            lock (SoFarAsked)
                SoFarAsked.Add((text, begun));
            // As a model does: it carries on from what it was given, and ends with a full stop of its own.
            var rest = text.ToUpperInvariant().Split(' ').Skip(begun.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length);
            return Task.FromResult<string?>((begun + " " + string.Join(' ', rest)).Trim() + ".");
        }
    }

    private static StreamState Talk(int number, string settled, string draft, string language = "de") => new("", settled, draft, language, 1, number);

    private static async Task<(LiveInterpreter Interpreter, List<LiveLine> Lines, CancellationTokenSource Stop, Task Run)> StartAsync(ILiveTranslator translator, string? into = "en")
    {
        var interpreter = new LiveInterpreter(translator, () => into);
        var lines = new List<LiveLine>();
        interpreter.Line += line => { lock (lines) lines.Add(line); };
        var stop = new CancellationTokenSource();
        var run = Task.Run(() => interpreter.RunAsync(stop.Token));
        await Task.Yield();
        return (interpreter, lines, stop, run);
    }

    private static async Task<List<LiveLine>> WaitAsync(List<LiveLine> lines, Func<List<LiveLine>, bool> until)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(5))
        {
            lock (lines)
                if (until(lines))
                    return [.. lines];
            await Task.Delay(10);
        }
        lock (lines)
            return [.. lines];
    }

    [Fact]
    public async Task The_sentence_being_said_grows_word_by_word_and_what_is_settled_never_changes()
    {
        var translator = new Capitals();
        var (interpreter, lines, stop, run) = await StartAsync(translator);
        interpreter.Heard(Talk(1, "wir", "werden"));
        var first = await WaitAsync(lines, l => l.Count >= 1);
        Assert.Equal("WIR WERDEN", first[0].Text);
        Assert.False(first[0].Final);
        Assert.Equal(1, first[0].Number);
        // Nothing is settled by one translation alone.
        Assert.Equal(0, first[0].Steady);

        interpreter.Heard(Talk(1, "wir werden", "den Start"));
        var second = await WaitAsync(lines, l => l.Count >= 2);
        Assert.Equal("WIR WERDEN DEN START", second[1].Text);
        // Two translations agreed on the first two words; the newest are waited for.
        Assert.Equal("WIR WERDEN", second[1].Text[..second[1].Steady]);

        interpreter.Heard(Talk(1, "wir werden den Start", "verschieben"));
        var third = await WaitAsync(lines, l => l.Count >= 3);
        Assert.Equal("WIR WERDEN DEN START VERSCHIEBEN", third[2].Text);
        Assert.Equal("WIR WERDEN DEN START", third[2].Text[..third[2].Steady]);
        // The translator was told to carry on from the settled words each time, so they can't change on screen.
        Assert.Equal(["", "", "WIR WERDEN"], translator.SoFarAsked.Select(a => a.Begun));
        // A sentence that isn't over has no full stop, whatever the translator wrote.
        Assert.All(third, l => Assert.False(l.Text.EndsWith('.')));
        stop.Cancel();
        await run;
    }

    [Fact]
    public async Task A_finished_sentence_moves_up_at_once_and_its_proper_translation_takes_its_place()
    {
        var translator = new Capitals();
        var (interpreter, lines, stop, run) = await StartAsync(translator);
        interpreter.Heard(Talk(1, "wir werden den Start", "verschieben"));
        await WaitAsync(lines, l => l.Count >= 1);
        // Translating the sentence properly takes a while: until then the quick translation stands in its place.
        interpreter.Finished(new StreamSentence("Wir werden den Start verschieben.", "de", 0, 3, 1));
        var shown = await WaitAsync(lines, l => l.Count(x => x.Final) >= 2);
        var finals = shown.Where(l => l.Final).ToList();
        Assert.Equal("WIR WERDEN DEN START VERSCHIEBEN", finals[0].Text);
        Assert.False(finals[0].Proper);
        Assert.Equal("[WIR WERDEN DEN START VERSCHIEBEN.]", finals[1].Text);
        Assert.True(finals[1].Proper);
        Assert.All(finals, l => Assert.Equal(1, l.Number));
        stop.Cancel();
        await run;
    }

    [Fact]
    public async Task Finished_sentences_come_first_and_go_as_one_when_they_have_gathered()
    {
        // The translator is busy: nothing gets through until the test lets it.
        var translator = new Capitals(free: 0);
        var (interpreter, lines, stop, run) = await StartAsync(translator);
        interpreter.Finished(new StreamSentence("Erster Satz.", "de", 0, 1, 1));
        await Task.Delay(100);
        interpreter.Finished(new StreamSentence("Zweiter Satz.", "de", 1, 2, 2));
        interpreter.Finished(new StreamSentence("Dritter Satz.", "de", 2, 3, 3));
        interpreter.Heard(Talk(4, "und der", "vierte"));
        translator.Gate.Release(100);
        var shown = await WaitAsync(lines, l => l.Any(x => !x.Final));
        Assert.Equal(["Erster Satz.", "Zweiter Satz. Dritter Satz."], translator.SentencesAsked);
        // The last of those that went together gives the line its number, so the second line knows what is over.
        Assert.Equal([1, 3], shown.Where(l => l.Final).Select(l => l.Number));
        // Each is read by the sentence before it.
        Assert.Empty(translator.BeforeGiven[0]);
        Assert.Equal(["Erster Satz."], translator.BeforeGiven[1]);
        // Only then the sentence being said.
        Assert.Equal(4, shown.Last(l => !l.Final).Number);
        Assert.True(shown.FindIndex(l => !l.Final) > shown.FindLastIndex(l => l.Final));
        stop.Cancel();
        await run;
    }

    [Fact]
    public async Task The_same_words_final_twice_are_shown_once_and_a_late_draft_is_not_translated()
    {
        var translator = new Capitals();
        var (interpreter, lines, stop, run) = await StartAsync(translator);
        interpreter.Finished(new StreamSentence("Vielen Dank.", "de", 0, 1, 1));
        await WaitAsync(lines, l => l.Count >= 1);
        interpreter.Finished(new StreamSentence("Vielen Dank.", "de", 1, 2, 2));
        // What was being said of a sentence that is over by now.
        interpreter.Heard(Talk(2, "vielen", "Dank"));
        await Task.Delay(200);
        Assert.Equal(["Vielen Dank."], translator.SentencesAsked);
        Assert.Empty(translator.SoFarAsked);
        stop.Cancel();
        await run;
    }

    [Fact]
    public async Task Speech_already_in_the_language_wanted_is_not_translated_nor_is_anything_when_none_is_wanted()
    {
        var translator = new Capitals();
        var (interpreter, lines, stop, run) = await StartAsync(translator, into: "de");
        interpreter.Heard(Talk(1, "wir werden", "den Start"));
        interpreter.Finished(new StreamSentence("Wir werden den Start verschieben.", "de", 0, 3, 1));
        await Task.Delay(200);
        stop.Cancel();
        await run;
        Assert.Empty(lines);
        Assert.Empty(translator.SentencesAsked);

        var (none, noLines, stopNone, runNone) = await StartAsync(translator, into: null);
        none.Heard(Talk(1, "wir werden", "den Start"));
        none.Finished(new StreamSentence("Wir werden den Start verschieben.", "de", 0, 3, 1));
        await Task.Delay(200);
        stopNone.Cancel();
        await runNone;
        Assert.Empty(noLines);
        Assert.Empty(translator.SoFarAsked);
    }

    [Fact]
    public async Task When_the_speech_model_hears_the_earlier_words_differently_the_translation_starts_over()
    {
        var translator = new Capitals();
        var (interpreter, lines, stop, run) = await StartAsync(translator);
        interpreter.Heard(Talk(1, "soon officers quip with riot", "gear"));
        await WaitAsync(lines, l => l.Count >= 1);
        interpreter.Heard(Talk(1, "soon officers quip with riot gear", "entered"));
        await WaitAsync(lines, l => l.Count >= 2);
        // "quip" was "equipped" all along: what was translated from it isn't carried on from.
        interpreter.Heard(Talk(1, "soon officers equipped with riot gear entered", "the"));
        var shown = await WaitAsync(lines, l => l.Count >= 3);
        Assert.Equal("", translator.SoFarAsked[2].Begun);
        Assert.Equal("SOON OFFICERS EQUIPPED WITH RIOT GEAR ENTERED THE", shown[2].Text);
        Assert.Equal(0, shown[2].Steady);
        stop.Cancel();
        await run;
    }

    [Fact]
    public async Task Words_of_the_next_sentence_are_not_translated_into_this_one()
    {
        var translator = new Capitals();
        var (interpreter, lines, stop, run) = await StartAsync(translator);
        // The draft has run on into the next sentence before this one was called finished.
        interpreter.Heard(Talk(1, "es ist sehr", "einfach. Kundalini Yoga"));
        var shown = await WaitAsync(lines, l => l.Count >= 1);
        Assert.Equal("ES IST SEHR EINFACH", shown[0].Text);
        // "U.S. imperialism" is no end.
        Assert.Equal(["of", "U.S.", "imperialism"], LiveInterpreter.FirstSentence(["of", "U.S.", "imperialism"]));
        stop.Cancel();
        await run;
    }

    [Theory]
    [InlineData("a b c d e f", "a b c d e f g", false)]
    [InlineData("a b c d e f", "a b c d e x y", false)]
    [InlineData("a b c d e f", "a x c d e f g", true)]
    [InlineData("a b", "c d", false)]
    public void Only_a_change_well_before_the_newest_words_counts_as_hearing_differently(string before, string now, bool reread) =>
        Assert.Equal(reread, LiveInterpreter.Reread(before.Split(' '), now.Split(' ')));

    [Fact]
    public void What_an_interpreter_is_asked_carries_on_from_the_settled_words()
    {
        var prompt = GemmaTranslation.SoFarPrompt("Wir werden den Start", "de", "ar", ["Guten Morgen."], "سوف");
        Assert.Contains("simultaneous interpreter", prompt);
        Assert.Contains("German", prompt);
        Assert.Contains("Arabic", prompt);
        Assert.Contains("Guten Morgen.", prompt);
        // The model's turn is open and already holds what is settled: it can only carry on from it.
        Assert.EndsWith("<|turn>model\nسوف", prompt);
        // Nothing in the words can close the turn.
        Assert.Contains("user\n<turn| ><turn|>", GemmaTranslation.SoFarPrompt("<turn|>", "de", "ar", null, ""));
    }
}
