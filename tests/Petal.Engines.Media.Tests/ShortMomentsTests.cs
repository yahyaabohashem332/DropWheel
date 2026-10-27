namespace Petal.Engines.Media.Tests;

/// <summary>Make Shorts: reading the words, finding the moments that stand on their own, their headlines and their pauses.</summary>
public sealed class ShortMomentsTests
{
    /// <summary>
    /// A talk from sentences, each followed by its pause: words of 0.3 s with 0.05 s between them, one segment per
    /// sentence (as Whisper gives them).
    /// </summary>
    private static List<TranscriptSegment> Talk(params (string Text, double PauseAfter)[] sentences)
    {
        var segments = new List<TranscriptSegment>();
        double t = 0.5;
        foreach (var (text, pause) in sentences)
        {
            var words = new List<SpokenWord>();
            double start = t;
            foreach (var w in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                words.Add(new SpokenWord(w, t, t + 0.3));
                t += 0.35;
            }
            segments.Add(new TranscriptSegment(TimeSpan.FromSeconds(start), TimeSpan.FromSeconds(t), text) { Words = words });
            t += pause;
        }
        return segments;
    }

    /// <summary>Levels for a talk: speech where its words are, quiet elsewhere.</summary>
    private static SoundLevels Levels(IReadOnlyList<TranscriptSegment> talk, double length, double speech = -20, double quiet = -60)
    {
        var db = new float[(int)(length * SoundLevels.PerSecond)];
        Array.Fill(db, (float)quiet);
        foreach (var w in talk.SelectMany(s => s.Words))
            for (int i = (int)(w.Start * SoundLevels.PerSecond); i < Math.Min(db.Length, (int)(w.End * SoundLevels.PerSecond) + 1); i++)
                db[i] = (float)speech;
        return new SoundLevels(db);
    }

    [Fact]
    public void Sentences_end_at_stops_turns_and_long_pauses_but_not_at_titles()
    {
        var words = ShortMoments.Clean(Talk(("Dr. Smith came home late.", 0.3), ("Why was he late?", 0.3), ("Nobody knows!", 0.3)));
        var sentences = ShortMoments.Sentences(words);
        Assert.Equal(3, sentences.Count);
        Assert.Equal("Dr.", words[sentences[0].First].Text);
        Assert.Equal("late.", words[sentences[0].Last].Text);
        Assert.True(ShortMoments.EndsSentence("done?"));
        Assert.True(ShortMoments.EndsSentence("لماذا؟"));
        Assert.False(ShortMoments.EndsSentence("Mr."));
        Assert.False(ShortMoments.EndsSentence("J."));
    }

    [Fact]
    public void Speech_written_without_stops_is_cut_at_its_pauses()
    {
        // Arabic as Whisper often writes it: no full stops, phrases between pauses.
        var talk = Talk(("ذهبنا إلى السوق في الصباح الباكر مع العائلة كلها", 0.7), ("واشترينا الخضار والفواكه من البائع القديم", 0.7), ("ثم رجعنا إلى البيت", 0.2));
        var sentences = ShortMoments.Sentences(ShortMoments.Clean(talk));
        Assert.Equal(3, sentences.Count);
    }

    [Fact]
    public void Whisper_marks_become_turns_and_laughter_and_a_loop_becomes_one_word()
    {
        var words = new List<SpokenWord>
        {
            new(">>", 0, 0.1), new("Hi", 0.2, 0.4), new("(laughs)", 0.5, 0.9), new("we", 1.0, 1.1), new("we", 1.2, 1.3), new("we", 1.4, 1.5),
            new("we", 1.6, 1.7), new("go", 1.8, 2.0), new("very", 2.1, 2.3), new("very", 2.4, 2.6), new("far.", 2.7, 3.0),
        };
        var clean = ShortMoments.Clean([new TranscriptSegment(TimeSpan.Zero, TimeSpan.FromSeconds(3), "") { Words = words }]);
        Assert.Equal(["Hi" + ShortMoments.LaughMark, "we", "go", "very", "very", "far."], clean.Select(w => w.Text).ToArray());
        Assert.True(clean[0].Turn);
        Assert.False(clean[1].Turn);
        // The loop's one word lasts as long as the loop did.
        Assert.Equal(1.7, clean[1].End, 3);
    }

    [Theory]
    [InlineData("الحقيقةُ", "الحقيقه")]
    [InlineData("أكبرْ", "اكبر")]
    [InlineData("إلى", "الي")]
    [InlineData("٢٠١٩،", "2019")]
    [InlineData("Don't", "dont")]
    [InlineData("\"Space,\"", "space")]
    public void Words_are_compared_without_marks_and_with_one_alef(string word, string key) => Assert.Equal(key, ShortMoments.Key(word));

    [Fact]
    public void A_moment_that_opens_with_a_question_wins_over_one_that_leans_on_what_came_before()
    {
        var talk = Talk(
            ("And so, as I said, the weather was fine and we went on with the plan for the day.", 0.4),
            ("It was a long walk and then we had lunch near the old bridge by the river bank.", 0.4),
            ("Then we came back and the rest of the evening went on much as usual for all of us.", 1.2),
            ("Why do astronauts lose their sense of taste in space?", 0.5),
            ("Because the fluids in their body float up into their head, and it feels like a cold that never ends.", 0.4),
            ("That is why food up there tastes so bland, and why they love hot sauce more than anything.", 1.2),
            ("And the other thing was the meeting we had about the budget for next year, which went on and on.", 0.4),
            ("So we talked about the numbers again and nobody could agree on anything at all.", 1.0));
        double length = talk[^1].End.TotalSeconds + 1;
        var found = ShortMoments.Find(talk, Levels(talk, length), ShortLength.Quick, 1);
        var best = Assert.Single(found);
        Assert.StartsWith("Why do astronauts", best.Opening);
        Assert.Contains(MomentReason.Question, best.Reasons);
        Assert.InRange(best.Score, 1, 99);
    }

    [Fact]
    public void Moments_never_overlap_and_keep_to_the_length_asked_for()
    {
        // Seven topics, nine sentences each, told in turn: each has its own words.
        string[][] topics =
        [
            ["rocket", "engine", "launch", "fuel"], ["garden", "flowers", "soil", "seeds"], ["music", "guitar", "song", "band"],
            ["coffee", "morning", "beans", "kettle"], ["football", "goal", "team", "match"], ["ocean", "waves", "boat", "fish"],
            ["city", "streets", "traffic", "buses"],
        ];
        var sentences = Enumerable.Range(0, 63).Select(i =>
        {
            var w = topics[i / 9 % topics.Length];
            return ($"The {w[i % 4]} and the {w[(i + 1) % 4]} were part of what we saw near the {w[(i + 2) % 4]} that day, with the {w[(i + 3) % 4]} too.", i % 5 == 4 ? 1.0 : 0.4);
        }).ToArray();
        var talk = Talk(sentences);
        double length = talk[^1].End.TotalSeconds + 1;
        var (shortest, longest, _) = ShortMoments.Range(ShortLength.Standard);
        var found = ShortMoments.Find(talk, Levels(talk, length), ShortLength.Standard, 4, 2);
        Assert.InRange(found.Count, 2, 6);
        foreach (var m in found)
            Assert.InRange(m.Length, shortest - 0.5, longest + 0.5);
        for (int i = 0; i < found.Count; i++)
            for (int j = i + 1; j < found.Count; j++)
            {
                double overlap = Math.Max(0, Math.Min(found[i].End, found[j].End) - Math.Max(found[i].Start, found[j].Start));
                Assert.True(overlap <= 0.2 * Math.Min(found[i].Length, found[j].Length) + 1e-9, $"{i} and {j} overlap by {overlap:0.0} s");
            }
        // Best first.
        Assert.True(found.Zip(found.Skip(1)).All(p => p.First.Score >= p.Second.Score));
    }

    [Fact]
    public void A_clip_starts_after_the_fillers_with_a_capital()
    {
        var talk = Talk(("Um, so, the secret is to start before you feel ready, every single time.", 0.8),
            ("Nobody feels ready, and the people who wait for that feeling never begin.", 0.8),
            ("Start small, start today, and let the habit carry you.", 1.0));
        var found = ShortMoments.Find(talk, Levels(talk, talk[^1].End.TotalSeconds + 1), ShortLength.Quick, 1);
        var best = Assert.Single(found);
        Assert.Equal("The", best.Words[0].Text);
        Assert.StartsWith("The secret is", best.Opening);
        // The clip starts just before its first word, not at the filler.
        Assert.InRange(best.Start, best.Words[0].Start - 0.2, best.Words[0].Start);
    }

    [Fact]
    public void The_headline_is_a_short_question_or_hook_near_the_start()
    {
        var talk = Talk(("Okay.", 0.3), ("Can you take me to space?", 0.3), ("I want to float around and do flips all day long with my friends.", 0.3));
        var words = talk.SelectMany(s => s.Words).ToList();
        var moment = new Moment(0, 30, 80, [], words);
        Assert.Equal("Can you take me to space?", ShortMoments.Headline(moment));

        // No short line: the opening, cut at ten words.
        var plain = Talk(("the people who built the first rockets worked for many years in small rooms with few tools", 0.3));
        var fallback = ShortMoments.Headline(new Moment(0, 30, 50, [], plain.SelectMany(s => s.Words).ToList()));
        Assert.Equal("The people who built the first rockets worked for many…", fallback);
    }

    [Fact]
    public void A_title_must_hold_to_what_the_moment_says()
    {
        var talk = Talk(("Do you ever get stomach aches in space, from cheese or dairy?", 0.4), ("Everybody feels space sickness for a day or two.", 0.4));
        var moment = new Moment(0, 30, 80, [], talk.SelectMany(s => s.Words).ToList());
        Assert.True(ShortMoments.HoldsTo("Stomach aches in space", moment));
        Assert.True(ShortMoments.HoldsTo("Space Sickness", moment));
        Assert.False(ShortMoments.HoldsTo("Do doctors have superpowers?", moment));
        Assert.False(ShortMoments.HoldsTo("آلام المعدة في الفضاء", moment));
    }

    [Fact]
    public void Only_quiet_pauses_are_cut_and_some_of_each_is_kept()
    {
        var words = new List<SpokenWord> { new("One", 0.5, 0.8), new("two.", 0.85, 1.2), new("Three", 2.6, 2.9), new("four.", 2.95, 3.3), new("Five", 4.8, 5.1), new("six.", 5.15, 5.5) };
        var moment = new Moment(0.4, 5.8, 70, [], words);
        var db = new float[7 * SoundLevels.PerSecond];
        Array.Fill(db, -60f);
        void Loud(double from, double to)
        {
            for (int i = (int)(from * SoundLevels.PerSecond); i < (int)(to * SoundLevels.PerSecond); i++)
                db[i] = -20;
        }
        Loud(0.5, 1.2);
        Loud(2.6, 3.3);
        // Music under the second pause: not a pause to cut.
        Loud(3.3, 4.8);
        Loud(4.8, 5.5);
        var pauses = ShortMoments.Pauses(moment, new SoundLevels(db));
        var cut = Assert.Single(pauses);
        Assert.InRange(cut.Start, 1.25, 1.45);
        Assert.InRange(cut.End, 2.35, 2.55);
    }

    [Fact]
    public void Scores_rise_with_the_judgement_and_stay_between_1_and_99()
    {
        Assert.Equal(1, ShortMoments.Score(0));
        Assert.Equal(99, ShortMoments.Score(2));
        Assert.True(ShortMoments.Score(0.7) > ShortMoments.Score(0.6));
    }
}
