using Petal.Engines.Media;

namespace Petal.Engines.Media.Tests;

/// <summary>
/// How the passes of the near-live captions engine are put together, without a model: what is settled, what is
/// still a draft, where a sentence ends and where its sound is cut. The words of each pass are written as Whisper
/// gave them in real runs.
/// </summary>
public sealed class StreamAssemblerTests
{
    /// <summary>Words a fifth of a second each, from a time on.</summary>
    private static List<StreamWord> Heard(double from, string words) =>
        words.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select((w, i) => new StreamWord(w, from + i * 0.2, from + (i + 1) * 0.2)).ToList();

    private static (StreamAssembler Words, List<StreamSentence> Sentences) New()
    {
        var words = new StreamAssembler { Language = "en" };
        var sentences = new List<StreamSentence>();
        words.Sentence += sentences.Add;
        return (words, sentences);
    }

    [Fact]
    public void What_two_readings_agree_on_is_settled_and_the_newest_word_never_is()
    {
        var (words, sentences) = New();
        words.Take(Heard(1, "It also had"), 1.6, closing: false);
        var first = words.State();
        Assert.Equal("", first.Current);
        Assert.Equal("It also had", first.Draft);

        words.Take(Heard(1, "It also had no authority"), 2.0, closing: false);
        var second = words.State();
        Assert.Equal("It also had", second.Current);
        Assert.Equal("no authority", second.Draft);
        Assert.Equal(1, second.Number);
        Assert.Empty(sentences);
    }

    [Fact]
    public void A_word_heard_wrongly_at_first_is_put_right_and_never_stands_twice()
    {
        // "quip" was heard four passes running, while only half of "equipped" had been said clearly.
        var (words, sentences) = New();
        words.Take(Heard(1, "Soon officers quip with"), 1.8, closing: false);
        words.Take(Heard(1, "Soon officers quip with riot gear"), 2.2, closing: false);
        Assert.Equal("Soon officers quip with", words.State().Current);

        words.Take(Heard(1, "Soon officers equipped with riot gear entered the"), 2.8, closing: false);
        words.Take(Heard(1, "Soon officers equipped with riot gear entered the yard."), 3.2, closing: true);
        var sentence = Assert.Single(sentences);
        Assert.Equal("Soon officers equipped with riot gear entered the yard.", sentence.Text);
        Assert.Equal(1, sentence.Number);
        Assert.Equal("en", sentence.Language);
    }

    [Fact]
    public void A_sentence_ends_at_its_full_stop_once_two_readings_put_it_there()
    {
        var (words, sentences) = New();
        // At the edge of the sound Whisper writes a full stop that isn't one: "laws." before "and tariffs" was said.
        words.Take(Heard(1, "It had no authority over tax laws."), 2.4, closing: false);
        words.Take(Heard(1, "It had no authority over tax laws and tariffs"), 2.8, closing: false);
        Assert.Empty(sentences);

        words.Take(Heard(1, "It had no authority over tax laws and tariffs between states."), 3.4, closing: false);
        Assert.Empty(sentences);
        words.Take(Heard(1, "It had no authority over tax laws and tariffs between states. New Kingdom"), 3.8, closing: false);
        var sentence = Assert.Single(sentences);
        Assert.Equal("It had no authority over tax laws and tariffs between states.", sentence.Text);
        var state = words.State();
        Assert.Equal(2, state.Number);
        Assert.Equal(sentence.Text, state.Previous);
        Assert.Equal("", state.Current);
        Assert.Equal("New Kingdom", state.Draft);
    }

    [Fact]
    public void A_finished_sentence_is_cut_away_a_pass_later_and_nothing_is_said_twice()
    {
        var (words, sentences) = New();
        words.Take(Heard(1, "It ended there."), 1.6, closing: false);
        words.Take(Heard(1, "It ended there. New"), 1.8, closing: false);
        Assert.Single(sentences);
        // Its sound is still held: with one word heard after it (at the edge of the sound, where a word's time may be
        // out), the cut waits for the next pass, which hears what follows and so where it ends.
        Assert.Null(words.Cut);

        words.Take(Heard(1, "It ended there. New Kingdom ancient"), 2.2, closing: false);
        var cut = Assert.NotNull(words.Cut);
        Assert.InRange(1.6, cut.From, cut.To);
        Assert.Equal("New", words.State().Current);
        words.Dropped(1.6, atCut: true);
        Assert.Equal("It ended there.", words.Before);
        // The speech model is told what came just before, and not what was said long ago: it would read it back.
        Assert.Equal("It ended there.", words.Context(1.6));
        Assert.Equal("", words.Context(30));
        // Nor anything at all once the talk has gone over to another language: Whisper would go on writing the first.
        words.Forget();
        Assert.Equal("", words.Context(1.6));

        // From now the sound starts at the new sentence.
        words.Take(Heard(1.6, "New Kingdom ancient Egyptians marvelled."), 2.6, closing: true);
        Assert.Equal(["It ended there.", "New Kingdom ancient Egyptians marvelled."], sentences.Select(s => s.Text));
        Assert.Equal([1, 2], sentences.Select(s => s.Number));
    }

    [Fact]
    public void On_a_slow_PC_a_sentence_ends_and_is_cut_in_one_pass()
    {
        // Passes seconds apart: by the time the second comes, the sentence is over and several words of the next are said.
        var (words, sentences) = New();
        words.Take(Heard(1, "It ended"), 1.4, closing: false);
        words.Take(Heard(1, "It ended there. New Kingdom ancient Egyptians"), 2.6, closing: false);
        Assert.Equal("It ended there.", Assert.Single(sentences).Text);
        // Where it stopped is known from the words after it: no pass more is waited for.
        var cut = Assert.NotNull(words.Cut);
        Assert.InRange(1.6, cut.From, cut.To);
        words.Dropped(1.58, atCut: true);
        Assert.Equal("", words.State().Current);
        Assert.Equal("New Kingdom ancient Egyptians", words.State().Draft);
        words.Take(Heard(1.6, "New Kingdom ancient Egyptians marvelled"), 3.0, closing: false);
        Assert.Equal("New Kingdom ancient Egyptians", words.State().Current);
        Assert.Single(sentences);
    }

    [Fact]
    public void The_finished_words_are_found_again_however_the_pass_spells_them()
    {
        var ended = Heard(1, "making Japan the 7th largest island.");
        // Heard again with a word changed and one missing; the new words follow.
        var heard = Heard(1, "making Japan the seventh largest island. Before an");
        Assert.Equal(6, StreamAssembler.Resume(ended, heard));
        var shorter = new List<StreamWord>(Heard(1, "making Japan the 7th largest"));
        shorter.AddRange(Heard(2.2, "Before an organization"));
        Assert.Equal(5, StreamAssembler.Resume(ended, shorter));
    }

    [Fact]
    public void The_end_of_a_word_from_before_the_cut_heard_as_a_word_is_left_out()
    {
        var (words, sentences) = New();
        words.Take([new("all", 1.0, 1.2), new("between", 1.2, 1.4), new("states.", 1.4, 1.9)], 2.0, closing: true);
        words.Dropped(1.8);
        // The sound now starts inside "states": Whisper hears it once more, squeezed in at the start.
        var again = new List<StreamWord> { new("States.", 1.8, 1.86) };
        again.AddRange(Heard(2.4, "New Kingdom ancient"));
        words.Take(again, 3.2, closing: false);
        Assert.Equal("New Kingdom ancient", words.State().Draft);
        // A sentence that really begins with the word the last one ended on keeps it.
        var (other, _) = New();
        other.Take(Heard(1, "Not at all."), 2.0, closing: true);
        other.Dropped(1.8);
        other.Take(Heard(2.3, "All of them left"), 3.2, closing: false);
        Assert.Equal("All of them left", other.State().Draft);
        Assert.Single(sentences);
    }

    [Fact]
    public void Words_the_readings_keep_disagreeing_on_are_not_waited_for_for_ever()
    {
        var (words, _) = New();
        words.Take(Heard(1, "the Tibetan Buddhist is very simple and it"), 3.0, closing: false);
        words.Take(Heard(1, "a Tibetan Buddhism is very simple and it consists"), 3.4, closing: false);
        // Nothing agreed from the first word on.
        Assert.Equal("", words.State().Current);
        // Seconds later they still disagree: the latest reading is taken at its word for what is old.
        words.Take(Heard(1, "the Tibetan Buddhism is very simple and it consists of yoga"), 6.6, closing: false);
        Assert.StartsWith("the Tibetan Buddhism is very simple", words.State().Current);
    }

    [Fact]
    public void A_pass_that_hears_nothing_once_leaves_what_was_shown()
    {
        var (words, _) = New();
        words.Take(Heard(1, "Combined with its"), 1.8, closing: false);
        words.Take(Heard(1, "Combined with its relative"), 2.2, closing: false);
        words.Take([], 2.6, closing: false);
        Assert.Equal("Combined with its", words.State().Current);
        Assert.Equal("relative", words.State().Draft);
    }

    [Fact]
    public void A_pause_finishes_all_that_was_waiting()
    {
        var (words, sentences) = New();
        words.Take(Heard(1, "They are still trying"), 2.0, closing: false);
        Assert.True(words.Waiting);
        words.FinishAll();
        Assert.False(words.Waiting);
        Assert.Equal("They are still trying", Assert.Single(sentences).Text);
        Assert.Equal(-1, words.State().Newest);
    }

    [Fact]
    public void Long_talk_without_a_full_stop_still_makes_sentences()
    {
        var (words, sentences) = New();
        var talk = string.Join(' ', Enumerable.Range(1, 40).Select(i => "word" + i));
        words.Take(Heard(1, talk), 9.2, closing: false);
        words.Take(Heard(1, talk + " more"), 9.6, closing: false);
        Assert.Equal(30, Assert.Single(sentences).Text.Split(' ').Length);
        // And sound held too long is parted at a comma.
        var (held, parted) = New();
        held.Take(Heard(1, "one two three four, five six seven"), 2.6, closing: false);
        held.Force();
        Assert.Equal("one two three four,", Assert.Single(parted).Text);
    }

    [Fact]
    public void Talk_without_full_stops_is_parted_where_the_speaker_took_a_breath()
    {
        // Urdu as Whisper writes it: not one full stop in all that was said. A breath after the fourteenth word, then more.
        var (words, sentences) = New();
        words.Take(Heard(-9, string.Join(' ', Enumerable.Range(1, 30).Select(i => "پہلے" + i))), -2, closing: true);
        sentences.Clear();
        var first = Heard(1, string.Join(' ', Enumerable.Range(1, 14).Select(i => "لفظ" + i)));
        var second = Heard(4.3, string.Join(' ', Enumerable.Range(15, 8).Select(i => "لفظ" + i)));
        var all = first.Concat(second).ToList();
        var pauses = new List<(double, double)> { (3.80, 4.28) };
        words.Take(all.Take(20).ToList(), 5.4, closing: false, pauses);
        words.Take(all, 5.9, closing: false, pauses);
        Assert.Equal(14, Assert.Single(sentences).Text.Split(' ').Length);
        // Where Whisper does write full stops, a breath in the middle of a sentence is only a breath.
        var (stopped, whole) = New();
        stopped.Take(Heard(-9, "It ended there."), -2, closing: true);
        whole.Clear();
        var english = Heard(1, "Before an organization can be innovative leadership must create a culture of innovation as well").Concat(Heard(4.3, "as shared knowledge and learning")).ToList();
        stopped.Take(english.Take(18).ToList(), 5.0, closing: false, pauses);
        stopped.Take(english, 5.4, closing: false, pauses);
        Assert.Empty(whole);
        // A breath after a few words only is no place to part a sentence.
        var (few, none) = New();
        var start = Heard(1, "one two three four");
        var rest = Heard(2.4, "five six seven eight nine ten");
        few.Take(start.Concat(rest).Take(9).ToList(), 3.4, closing: false, [(1.82, 2.38)]);
        few.Take(start.Concat(rest).ToList(), 3.8, closing: false, [(1.82, 2.38)]);
        Assert.Empty(none);
    }

    [Theory]
    [InlineData("states.", "New", true)]
    [InlineData("U.S.", "imperialism", false)]
    [InlineData("Dr.", "Smith", false)]
    [InlineData("3.", "Bölüm", false)]
    [InlineData("laws.", "and", false)]
    [InlineData("gittim.", null, true)]
    [InlineData("begins?", "Japan", true)]
    [InlineData("است.", "این", true)]
    [InlineData("ہے۔", "یہ", true)]
    [InlineData("ماذا؟", "قال", true)]
    [InlineData("well...", "over", false)]
    [InlineData("then,", "well", false)]
    public void A_sentence_ends_where_it_does(string word, string? next, bool ends) =>
        Assert.Equal(ends, StreamAssembler.EndsSentence(word, next));

    [Fact]
    public void Words_put_where_nobody_spoke_are_left_out()
    {
        var speech = new List<(double, double)> { (0.5, 2.0), (6.0, 7.0) };
        var words = new List<StreamWord> { new("Hello", 10.6, 11.0), new("there.", 11.0, 11.6), new("Thanks", 13.4, 13.8), new("for", 13.8, 14.0), new("again", 16.1, 16.5) };
        var kept = StreamingTranscriber.InSpeech(words, speech, offset: 10);
        Assert.Equal(["Hello", "there.", "again"], kept.Select(w => w.Text));
    }

    [Theory]
    [InlineData(" Subtitles by the Amara.org community", true)]
    [InlineData("ترجمة نانسي قنقر", true)]
    [InlineData("Altyazı M.K.", true)]
    [InlineData("Sous-titres réalisés par la communauté d'Amara.org", true)]
    [InlineData("The subtitles were late again, and nobody at the studio could say by whose mistake.", false)]
    [InlineData("Thank you for coming.", false)]
    public void Lines_from_the_ends_of_subtitle_files_are_not_speech(string text, bool credit) =>
        Assert.Equal(credit, Credits.Is(text));

    [Fact]
    public void A_sentence_said_over_again_in_no_time_at_the_end_of_a_pass_was_never_said()
    {
        // As Whisper small gave it on a processor: the sentence, then most of it again in four tenths of a second.
        var said = Heard(97.5, "They are still trying to determine how large the crash was.");
        var again = "They are still trying to determine how large the".Split(' ').Select((w, i) => new StreamWord(w, 103.72 + i * 0.045, 103.72 + (i + 1) * 0.045));
        var words = said.Concat(again).ToList();
        StreamingTranscriber.DropSqueezed(words);
        Assert.Equal(said.Select(w => w.Text), words.Select(w => w.Text));
        // Quick words really said stay.
        var quick = Heard(1, "and so it is that we go on");
        StreamingTranscriber.DropSqueezed(quick);
        Assert.Equal(8, quick.Count);
    }

    [Fact]
    public void A_phrase_going_round_in_circles_is_kept_once()
    {
        var words = Heard(1, "Thank you. Thank you. Thank you. Thank you.");
        Assert.Equal(["Thank", "you."], StreamingTranscriber.Clean(words).Select(w => w.Text));
        var said = Heard(1, "No, no, I said no.");
        Assert.Equal(5, StreamingTranscriber.Clean(said).Count);
    }

    [Fact]
    public void Quiet_speech_is_raised_to_the_level_of_loud_speech_and_a_pause_stays_a_pause()
    {
        // Half a second of loud "speech", a second of quiet, then a voice forty decibels down.
        var sound = new float[StreamingTranscriber.Rate * 4];
        var random = new Random(3);
        for (int i = 0; i < sound.Length; i++)
        {
            double seconds = (double)i / StreamingTranscriber.Rate;
            double tone = Math.Sin(2 * Math.PI * 220 * seconds);
            sound[i] = (float)(seconds < 0.5 ? 0.4 * tone : seconds < 1.5 ? 0.0004 * (random.NextDouble() * 2 - 1) : 0.004 * tone);
        }
        var level = SpeechLevel.Level(sound);
        float Peak(double from, double to) => level.AsSpan((int)(from * StreamingTranscriber.Rate), (int)((to - from) * StreamingTranscriber.Rate)).ToArray().Max(Math.Abs);
        Assert.InRange(Peak(0.1, 0.4), 0.2, 0.3);
        Assert.InRange(Peak(2.5, 3.5), 0.2, 0.3);
        Assert.True(Peak(0.9, 1.1) < 0.08, $"the pause came out at {Peak(0.9, 1.1)}");
        // By loudness alone, when the model that tells speech isn't here: two stretches.
        var loud = SpeechLevel.Loud(level);
        Assert.Equal(2, loud.Count);
        Assert.InRange(loud[1].Start, 1.3, 1.7);
        // The quietest moment between the two is in the pause.
        int quietest = SpeechLevel.Quietest(level, 0, StreamingTranscriber.Rate * 2);
        Assert.InRange((double)quietest / StreamingTranscriber.Rate, 0.5, 1.5);
    }
}
