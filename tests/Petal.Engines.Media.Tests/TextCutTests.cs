using Petal.Core.Models;
using Xunit.Abstractions;

namespace Petal.Engines.Media.Tests;

/// <summary>Edit by Text: a recording as words, sounds and pauses; what is kept when some are taken out; and the cut itself.</summary>
public sealed class TextCutTests(ITestOutputHelper output)
{
    /// <summary>Levels at 20 a second: speech (-20 dB) where <paramref name="loud"/> says, quiet (-60 dB) elsewhere.</summary>
    private static SoundLevels Levels(double length, params (double From, double To)[] loud) => Levels(length, SoundLevels.PerSecond, loud);

    private static SoundLevels Levels(double length, int perSecond, params (double From, double To)[] loud)
    {
        var db = new float[(int)(length * perSecond)];
        for (int i = 0; i < db.Length; i++)
        {
            double t = (i + 0.5) / perSecond;
            db[i] = loud.Any(l => t >= l.From && t < l.To) ? -20 : -60;
        }
        return new SoundLevels(db, perSecond);
    }

    private static TranscriptSegment Segment(params (string Text, double Start, double End)[] words) =>
        new(TimeSpan.FromSeconds(words[0].Start), TimeSpan.FromSeconds(words[^1].End), string.Join(' ', words.Select(w => w.Text)))
        {
            Words = words.Select(w => new SpokenWord(w.Text, w.Start, w.End)).ToList(),
        };

    [Fact]
    public void A_recording_is_its_words_the_sounds_that_were_not_words_and_its_pauses()
    {
        // "So" then an unwritten "um" (loud between 1.0 and 1.6), "we" and "go", a long silence, "home".
        var talk = Segment(("So", 0.3, 0.8), ("we", 1.9, 2.1), ("go", 2.15, 2.4), ("home.", 3.6, 4.0));
        var levels = Levels(4.3, (0.3, 0.8), (1.0, 1.6), (1.9, 2.4), (3.6, 4.0));
        var tokens = TextCut.Tokens([talk], levels, 4.3);
        Assert.Equal(
            [EditTokenKind.Word, EditTokenKind.Sound, EditTokenKind.Word, EditTokenKind.Word, EditTokenKind.Pause, EditTokenKind.Word],
            tokens.Select(t => t.Kind).ToArray());
        var sound = tokens[1];
        Assert.InRange(sound.Start, 0.95, 1.05);
        Assert.InRange(sound.End, 1.55, 1.7);
        // The pause from where "go" falls quiet (and a little of its fading) to the quiet just before "home".
        Assert.InRange(tokens[4].Start, 2.4, 2.46);
        Assert.InRange(tokens[4].End, 3.54, 3.6);
        // Each word starts in the quiet just before it, and "we" runs into "go" (no quiet between them).
        Assert.InRange(tokens[2].Start, 1.84, 1.9);
        Assert.Equal(tokens[3].Start, tokens[2].End, 3);
        // A short gap between words is neither.
        Assert.DoesNotContain(tokens, t => t.Kind != EditTokenKind.Word && t.Start >= 2.1 && t.End <= 2.15);
    }

    [Fact]
    public void A_word_Whisper_heard_without_a_length_is_kept_and_starts_in_the_quiet_before_it()
    {
        // "is a friend of mine": Whisper's alignment gives the short words a start and no end, and starts some a little late.
        var talk = Segment(("is", 1.0, 1.0), ("a", 1.25, 1.25), ("friend", 1.45, 1.45), ("of", 1.66, 1.66), ("mine.", 1.86, 2.1));
        var levels = Levels(2.6, 100, (1.02, 1.18), (1.22, 1.33), (1.38, 1.58), (1.63, 1.78), (1.82, 2.1));
        var tokens = TextCut.Tokens([talk], levels, 2.6);
        var words = tokens.Where(t => t.Kind == EditTokenKind.Word).ToList();
        Assert.Equal(["is", "a", "friend", "of", "mine."], words.Select(w => w.Text).ToArray());
        // Each starts in the quiet between the words, not where Whisper put it.
        double[] quiet = [1.01, 1.2, 1.355, 1.605, 1.8];
        for (int i = 0; i < words.Count; i++)
            Assert.InRange(words[i].Start, quiet[i] - 0.03, quiet[i] + 0.03);
        // One runs to the next, and the last to where its sound fades.
        for (int i = 0; i + 1 < words.Count; i++)
            Assert.Equal(words[i + 1].Start, words[i].End, 3);
        Assert.InRange(words[^1].End, 2.1, 2.16);
    }

    [Fact]
    public void A_word_Whisper_heard_late_keeps_its_sound_and_an_uh_on_its_own_is_a_sound()
    {
        // As in a real interview: "Everybody" said from 0.5 s and heard by Whisper where it ends (0.93), "stay" heard a
        // third of a second late; later "growing, uhh, language": a held "uhh" (3.7–4.6) with quiet either side.
        var talk = Segment(("Everybody", 0.93, 0.93), ("stay", 1.46, 1.6), ("growing,", 3.0, 3.4), ("language", 4.75, 5.1));
        var levels = Levels(5.6, 100, (0.5, 0.92), (1.1, 1.4), (3.0, 3.45), (3.7, 4.6), (4.75, 5.2));
        var tokens = TextCut.Tokens([talk], levels, 5.6);
        var everybody = tokens.Single(t => t.Text == "Everybody");
        Assert.InRange(everybody.Start, 0.44, 0.5);
        Assert.InRange(everybody.End, 0.92, 0.97);
        Assert.InRange(tokens.Single(t => t.Text == "stay").Start, 1.05, 1.1);
        var sound = Assert.Single(tokens, t => t.Kind == EditTokenKind.Sound);
        Assert.InRange(sound.Start, 3.6, 3.7);
        Assert.InRange(sound.End, 4.6, 4.7);
        Assert.InRange(tokens.Single(t => t.Text == "language").Start, 4.68, 4.75);
    }

    [Fact]
    public void The_picture_keeps_up_with_the_sound_frame_by_frame()
    {
        // Stretches of sound that aren't whole frames (25 a second): the frames taken add up to the sound so far.
        var sound = new List<(double Start, double End)> { (0.013, 0.517), (1.002, 1.033), (1.5, 2.29), (3.01, 3.051), (4.0, 4.5) };
        var frames = TextCut.Frames(sound, 25);
        double heard = 0, shown = 0;
        int f = 0;
        foreach (var (start, end) in sound)
        {
            heard += end - start;
            while (f < frames.Count && frames[f].Start < end)
                shown += frames[f].End - frames[f++].Start;
            Assert.InRange(Math.Abs(shown - heard), 0, 0.5 / 25 + 1e-9);
        }
        // In order, never the same frame twice.
        for (int i = 1; i < frames.Count; i++)
            Assert.True(frames[i].Start >= frames[i - 1].End - 1e-9);
    }

    [Fact]
    public void A_sound_that_holds_a_word_is_a_sound_no_more()
    {
        Assert.True(TextCut.NoWords(""));
        Assert.True(TextCut.NoWords("Uh..."));
        Assert.True(TextCut.NoWords(" إمم، "));
        Assert.False(TextCut.NoWords("A language"));
        var tokens = new List<EditToken>
        {
            new(EditTokenKind.Word, "growing,", 0.0, 0.6),
            new(EditTokenKind.Sound, "", 0.8, 1.6),
            new(EditTokenKind.Word, "community", 1.7, 2.2),
            new(EditTokenKind.Pause, "", 2.2, 3.0),
            new(EditTokenKind.Sound, "", 3.0, 3.4),
            new(EditTokenKind.Pause, "", 3.4, 4.0),
            new(EditTokenKind.Word, "of", 4.0, 4.2),
        };
        var checkedOnes = TextCut.Unsound(tokens, new Dictionary<int, string> { [1] = "A language", [4] = " current " });
        // The first goes with the word after it; the second, between pauses, is a word of its own.
        Assert.Equal(6, checkedOnes.Count);
        Assert.Equal(("community", 0.8), (checkedOnes[1].Text, checkedOnes[1].Start));
        Assert.Equal((EditTokenKind.Word, "current"), (checkedOnes[3].Kind, checkedOnes[3].Text));
        Assert.DoesNotContain(checkedOnes, t => t.Kind == EditTokenKind.Sound);
    }

    [Fact]
    public void A_clean_up_takes_out_hesitations_stutters_and_unwritten_sounds()
    {
        var tokens = new List<EditToken>
        {
            new(EditTokenKind.Word, "I", 0.0, 0.2),
            new(EditTokenKind.Word, "I", 0.3, 0.5),
            new(EditTokenKind.Word, "think,", 0.6, 0.9),
            new(EditTokenKind.Word, "um,", 1.0, 1.3),
            new(EditTokenKind.Sound, "", 1.4, 1.9),
            new(EditTokenKind.Word, "يعني", 2.0, 2.3),
            new(EditTokenKind.Word, "إمم", 2.4, 2.8),
            new(EditTokenKind.Pause, "", 2.8, 4.2),
            new(EditTokenKind.Word, "well", 4.2, 4.5),
        };
        Assert.Equal([0, 3, 4, 6], TextCut.Fillers(tokens).Order().ToArray());
        Assert.Equal([7], TextCut.LongPauses(tokens, 1.0).ToArray());
        Assert.Empty(TextCut.LongPauses(tokens, 2.0));
    }

    [Fact]
    public void A_word_taken_out_goes_with_its_silence_and_the_words_left_keep_a_margin()
    {
        var tokens = new List<EditToken>
        {
            new(EditTokenKind.Word, "one", 1.0, 1.4),
            new(EditTokenKind.Word, "two", 1.6, 2.0),
            new(EditTokenKind.Word, "three", 2.2, 2.6),
        };
        var kept = TextCut.Keep(tokens, new HashSet<int> { 1 }, 3.0);
        Assert.Equal(2, kept.Count);
        Assert.Equal(0, kept[0].Start, 3);
        Assert.Equal(1.46, kept[0].End, 3);
        Assert.Equal(2.14, kept[1].Start, 3);
        Assert.Equal(3.0, kept[1].End, 3);
        // Nothing taken out: the whole recording.
        Assert.Equal([(0.0, 3.0)], TextCut.Keep(tokens, new HashSet<int>(), 3.0));
        // The first word out: the silence before it goes too, all but a margin from the start.
        var first = TextCut.Keep(tokens, new HashSet<int> { 0 }, 3.0);
        Assert.Equal(0.06, first[0].End, 3);
        Assert.Equal(1.54, first[1].Start, 3);
        // Where the edited recording places a moment of the original.
        Assert.Equal(1.0, TextCut.EditedTime(kept, 1.0)!.Value, 3);
        Assert.Equal(1.46 + 0.36, TextCut.EditedTime(kept, 2.5)!.Value, 3);
        Assert.Null(TextCut.EditedTime(kept, 1.8));
    }

    [Fact]
    public void A_pause_taken_out_is_shortened_not_closed()
    {
        var tokens = new List<EditToken>
        {
            new(EditTokenKind.Word, "first.", 0.0, 0.5),
            new(EditTokenKind.Pause, "", 0.5, 3.5),
            new(EditTokenKind.Word, "second", 3.5, 4.0),
        };
        var kept = TextCut.Keep(tokens, new HashSet<int> { 1 }, 4.0);
        double length = kept.Sum(k => k.End - k.Start);
        Assert.Equal(4.0 - 3.0 + TextCut.ShortPause, length, 3);
        Assert.Equal(2, kept.Count);
    }

    [Fact]
    public async Task Picture_and_sound_stay_together_however_many_cuts()
    {
        var video = await Fixtures.GetAsync("hq.mp4");
        // Eighteen stretches of two frames kept (the video runs at 15 a second), two frames cut between each.
        var kept = Enumerable.Range(0, 18).Select(i => (i * 4 / 15.0, (i * 4 + 2) / 15.0)).ToList();
        var output = Path.Combine(Fixtures.NewDir(), "cut.mp4");
        await TextCut.RenderAsync(video, output, kept);
        var info = await MediaProbe.ProbeAsync(output);
        double picture = info.Video!.Duration!.Value.TotalSeconds, sound = info.Audio!.Duration!.Value.TotalSeconds;
        // 18 × 2/15 s: before, the picture gained a frame at every cut (3.6 s against the sound's 2.4).
        Assert.InRange(picture, 2.35, 2.45);
        Assert.InRange(Math.Abs(picture - sound), 0, 0.1);
    }

    [Fact]
    public async Task Hundreds_of_cuts_are_all_made()
    {
        var video = await Fixtures.GetAsync("long.mp4");
        // 250 single frames kept (the video runs at 30 a second), one frame cut between each.
        var kept = Enumerable.Range(0, 250).Select(i => (i * 2 / 30.0, (i * 2 + 1) / 30.0)).ToList();
        var output = Path.Combine(Fixtures.NewDir(), "cut.mp4");
        await TextCut.RenderAsync(video, output, kept);
        var info = await MediaProbe.ProbeAsync(output);
        double picture = info.Video!.Duration!.Value.TotalSeconds, sound = info.Audio!.Duration!.Value.TotalSeconds;
        Assert.InRange(picture, 250 / 30.0 - 0.05, 250 / 30.0 + 0.05);
        Assert.InRange(Math.Abs(picture - sound), 0, 0.1);
    }

    /// <summary>
    /// With PETAL_TEXTCUT pointing at a recording with speech: it is heard, then edited six ways (nothing taken out; the
    /// sounds between words; the hesitations and words said twice; every third sentence; a word here and there; the long
    /// pauses), and each edit is heard again. The words heard are the words kept, none cut short and none of those taken
    /// out left in, nearly as well as the recording itself is heard again; picture and sound end together. Measured on a
    /// four-minute interview in English and two and a half minutes of Arabic, the clean-ups come within a word or two in a
    /// hundred of the recording itself, whole sentences taken out within four, single words cut from fluent speech within fifteen.
    /// </summary>
    [Fact]
    public async Task A_real_recording_edited_by_its_words_says_the_words_kept()
    {
        var media = Environment.GetEnvironmentVariable("PETAL_TEXTCUT");
        // The most accurate Whisper on this PC, as Edit by Text prefers (its word times are the best too).
        var model = ModelStore.Find(ModelCatalog.WhisperAccurate) ?? ModelStore.Find(ModelCatalog.WhisperBalanced) ?? ModelStore.Find(ModelCatalog.WhisperFast);
        if (media is null || model is null)
        {
            output.WriteLine("Skipped: PETAL_TEXTCUT isn't set, or there is no Whisper model.");
            return;
        }
        var samples = await Transcriber.ReadSpeechAsync(media);
        var levels = SoundLevels.Measure(samples);
        double length = samples.Length / 16000.0;
        var heard = await Transcriber.TranscribeWordsAsync(samples, new TranscribeOptions(model, "auto", WordTimes: true), levels);
        // The cuts placed by the loudness a hundred times a second, as Edit by Text measures it.
        var fine = SoundLevels.Measure(samples, perSecond: 100);
        var tokens = TextCut.Tokens(heard.Segments, fine, length);
        var words = Enumerable.Range(0, tokens.Count).Where(i => tokens[i].Kind == EditTokenKind.Word).ToList();
        output.WriteLine($"{words.Count} words, {tokens.Count(t => t.Kind == EditTokenKind.Sound)} sounds, {tokens.Count(t => t.Kind == EditTokenKind.Pause)} pauses; quiet below {fine.QuietBelow:0.0} dB");
        output.WriteLine(string.Join(' ', tokens.Take(80).Select(t => t.Kind switch
        {
            EditTokenKind.Word => $"{t.Text}@{t.Start:0.00}-{t.End:0.00}",
            EditTokenKind.Sound => $"~{t.Start:0.00}-{t.End:0.00}",
            _ => $"[{t.Start:0.00}-{t.End:0.00}]",
        })));
        // As Edit by Text does: each sound heard again on its own, and one that holds a word is a sound no more.
        var speech = new Dictionary<int, string>();
        for (int i = 0; i < tokens.Count; i++)
        {
            if (tokens[i].Kind != EditTokenKind.Sound)
                continue;
            var clip = new float[32000 + (int)(tokens[i].Length * 16000)];
            Array.Copy(samples, (int)(tokens[i].Start * 16000), clip, 16000, Math.Min(clip.Length - 32000, samples.Length - (int)(tokens[i].Start * 16000)));
            var said = string.Join(' ', (await Transcriber.TranscribeWordsAsync(clip, new TranscribeOptions(model, heard.Language, WordTimes: true))).Segments.Select(seg => seg.Text));
            output.WriteLine($"sound {tokens[i].Start:0.00}-{tokens[i].End:0.00} heard as \"{said}\"");
            if (!TextCut.NoWords(said))
                speech[i] = said;
        }
        tokens = TextCut.Unsound(tokens, speech);
        words = Enumerable.Range(0, tokens.Count).Where(i => tokens[i].Kind == EditTokenKind.Word).ToList();
        var fillers = TextCut.Fillers(tokens);
        var edits = new (string Name, HashSet<int> Out)[]
        {
            ("nothing", []),
            ("sounds", Enumerable.Range(0, tokens.Count).Where(i => tokens[i].Kind == EditTokenKind.Sound).ToHashSet()),
            ("hesitations", fillers.Where(i => tokens[i].Kind == EditTokenKind.Word).ToHashSet()),
            ("every third sentence", Sentences(tokens, words).Where((_, k) => k % 3 == 1).SelectMany(x => x).ToHashSet()),
            ("a word here and there", words.Where((_, k) => k % 10 == 5).ToHashSet()),
            ("long pauses", TextCut.LongPauses(tokens, 0.8)),
        };
        var errors = new Dictionary<string, double>();
        foreach (var (name, removed) in edits)
        {
            var kept = TextCut.Keep(tokens, removed, length);
            var edited = Path.Combine(Fixtures.NewDir(), "edited.mp4");
            await TextCut.RenderAsync(media, edited, kept);
            var again = await Transcriber.TranscribeWordsAsync(await Transcriber.ReadSpeechAsync(edited), new TranscribeOptions(model, heard.Language, WordTimes: true));
            var expected = words.Where(i => !removed.Contains(i)).Select(i => TextCut.Plain(tokens[i].Text)).Where(w => w.Length > 0).ToList();
            var got = again.Segments.SelectMany(s => s.Words).Select(w => TextCut.Plain(w.Text)).Where(w => w.Length > 0).ToList();
            var all = Differences(expected, got);
            // Whisper now and then leaves out a whole piece of what it hears: a run of words missing is counted apart.
            var differences = new List<string>();
            int skipped = 0;
            for (int d = 0; d < all.Count;)
            {
                int run = 0;
                while (d + run < all.Count && all[d + run].StartsWith('-'))
                    run++;
                if (run >= 6)
                {
                    skipped += run;
                    d += run;
                }
                else if (run > 0)
                {
                    differences.AddRange(all.Skip(d).Take(run));
                    d += run;
                }
                else
                    differences.Add(all[d++]);
            }
            errors[name] = differences.Count / (double)expected.Count;
            var info = await MediaProbe.ProbeAsync(edited);
            double picture = info.Video?.Duration?.TotalSeconds ?? 0, sound = info.Audio?.Duration?.TotalSeconds ?? 0;
            output.WriteLine($"{name}: {removed.Count} out, {kept.Count} stretches, {length:0.0} s into {kept.Sum(k => k.End - k.Start):0.0} s "
                + $"(picture {picture:0.000}, sound {sound:0.000}); {differences.Count} of {expected.Count} words differ ({errors[name]:P1}), {skipped} in runs Whisper left out: {string.Join(", ", differences.Take(60))}");
            if (picture > 0 && sound > 0)
                Assert.InRange(Math.Abs(picture - sound), 0, 0.06);
        }
        // A word cut from the middle of fluent speech, where Whisper's word times are good to a tenth of a second, is the
        // hardest (and Whisper hearing the choppy result puts words back); everything else is heard nearly as well as the
        // recording itself.
        foreach (var (name, _) in edits)
            Assert.InRange(errors[name], 0, errors["nothing"] + (name == "a word here and there" ? 0.2 : 0.06));
    }

    /// <summary>The words of each sentence, a sentence ending with a word that ends with a full stop or a question mark.</summary>
    private static List<List<int>> Sentences(IReadOnlyList<EditToken> tokens, IReadOnlyList<int> words)
    {
        var sentences = new List<List<int>> { new() };
        foreach (var i in words)
        {
            sentences[^1].Add(i);
            if (tokens[i].Text.Length > 0 && ".?!؟".Contains(tokens[i].Text[^1]))
                sentences.Add([]);
        }
        return sentences.Where(x => x.Count > 0).ToList();
    }

    /// <summary>The words that differ between what was kept and what was heard again ("-word" missing, "+word" extra, "a/b" heard as another).</summary>
    private static List<string> Differences(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        var cost = new int[a.Count + 1, b.Count + 1];
        for (int i = 0; i <= a.Count; i++)
            cost[i, 0] = i;
        for (int j = 0; j <= b.Count; j++)
            cost[0, j] = j;
        for (int i = 1; i <= a.Count; i++)
            for (int j = 1; j <= b.Count; j++)
                cost[i, j] = Math.Min(Math.Min(cost[i - 1, j] + 1, cost[i, j - 1] + 1), cost[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
        var found = new List<string>();
        for (int i = a.Count, j = b.Count; i > 0 || j > 0;)
        {
            if (i > 0 && j > 0 && cost[i, j] == cost[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1))
            {
                if (a[i - 1] != b[j - 1])
                    found.Add($"{a[i - 1]}/{b[j - 1]}");
                i--;
                j--;
            }
            else if (i > 0 && cost[i, j] == cost[i - 1, j] + 1)
                found.Add("-" + a[--i]);
            else
                found.Add("+" + b[--j]);
        }
        found.Reverse();
        return found;
    }
}
