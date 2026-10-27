using Petal.Core.Models;
using Xunit.Abstractions;

namespace Petal.Engines.Media.Tests;

/// <summary>Make Shorts: the sound's levels, Whisper's pieces, the speaker's framing, the captions, and the clips made.</summary>
[Collection("Models")]
public sealed class ShortClipTests(ITestOutputHelper output)
{
    private static float[] Tone(double seconds, params (double From, double To)[] loud)
    {
        var samples = new float[(int)(seconds * 16000)];
        foreach (var (from, to) in loud)
            for (int i = (int)(from * 16000); i < Math.Min(samples.Length, (int)(to * 16000)); i++)
                samples[i] = (float)(0.3 * Math.Sin(2 * Math.PI * 220 * i / 16000.0));
        return samples;
    }

    [Fact]
    public void Levels_tell_speech_from_pauses()
    {
        var levels = SoundLevels.Measure(Tone(6, (0.5, 2), (3.5, 5.5)));
        Assert.Equal(120, levels.Count);
        Assert.True(levels.At(1) > levels.QuietBelow);
        Assert.True(levels.At(2.75) < levels.QuietBelow);
        Assert.Equal(1, levels.QuietShare(2.2, 3.3), 3);
        // The middle of the pause between the two.
        Assert.InRange(levels.LongestPauseBetween(1, 5), 2.6, 2.9);
        Assert.InRange(levels.SoundEndAfter(1.5, 3), 1.95, 2.1);
        Assert.InRange(levels.SoundStartBefore(4, 2.5), 3.45, 3.6);
    }

    [Fact]
    public void Whisper_is_given_pieces_under_half_a_minute_cut_at_pauses_and_no_long_quiet()
    {
        // Talk with a short pause every five seconds, a minute of it, then two minutes of quiet, then more talk.
        var loud = new List<(double, double)>();
        for (double t = 0; t < 60; t += 5)
            loud.Add((t, t + 4.4));
        loud.Add((180, 190));
        var levels = SoundLevels.Measure(Tone(195, [.. loud]));
        var pieces = Transcriber.Pieces(levels, 195);
        Assert.All(pieces, p => Assert.InRange(p.End - p.Start, 0.5, 28.01));
        // Every cut falls in a pause, not in the talk.
        foreach (var (start, end) in pieces.Take(pieces.Count - 1))
            Assert.True(levels.At(end) < levels.QuietBelow, $"cut at {end:0.00} s is in the talk");
        // The quiet between 60 and 180 s isn't read.
        Assert.DoesNotContain(pieces, p => p.Start > 65 && p.Start < 175);
        Assert.Contains(pieces, p => p.Start >= 179 && p.Start <= 180.1);
    }

    [Fact]
    public void A_word_split_across_tokens_keeps_its_spelling_and_takes_its_place_in_time()
    {
        List<(string, double, double)> timed = [("مرحبا", 0, 0.5), ("بكم", 0.5, 1)];
        var same = TokenWords.Spelled("مرحبا بكم", timed);
        Assert.Equal(["مرحبا", "بكم"], same.Select(w => w.Text).ToArray());
        Assert.Equal(0.5, same[1].Start, 3);
        // Three words for two token words (a letter split between tokens): the text decides, the times follow along it.
        var three = TokenWords.Spelled("مرحبا بكم جميعا", [("مرحبا", 0, 0.5), ("بكم ج�", 0.5, 1.5)]);
        Assert.Equal(3, three.Count);
        Assert.True(three.Zip(three.Skip(1)).All(p => p.Second.Start >= p.First.Start));
        Assert.InRange(three[^1].End, 1.2, 1.5);
    }

    [Fact]
    public void Times_in_the_video_become_times_in_the_short()
    {
        var kept = ShortClip.Kept(10, 20, [(12, 13), (15, 16.5)]);
        Assert.Equal([(10.0, 12.0), (13.0, 15.0), (16.5, 20.0)], kept);
        Assert.Equal(0, ShortClip.ToOutput(kept, 10), 6);
        Assert.Equal(2, ShortClip.ToOutput(kept, 12.5), 6);
        Assert.Equal(3, ShortClip.ToOutput(kept, 14), 6);
        Assert.Equal(7.5, ShortClip.ToOutput(kept, 20), 6);
        var words = ShortClip.Timed([new CaptionWord("a", TimeSpan.FromSeconds(9.8), TimeSpan.FromSeconds(10.4)), new CaptionWord("b", TimeSpan.FromSeconds(13.2), TimeSpan.FromSeconds(13.6)),
            new CaptionWord("c", TimeSpan.FromSeconds(25), TimeSpan.FromSeconds(26))], kept);
        Assert.Equal(["a", "b"], words.Select(w => w.Text).ToArray());
        Assert.Equal(0, words[0].Start.TotalSeconds, 6);
        Assert.Equal(2.2, words[1].Start.TotalSeconds, 6);
    }

    [Fact]
    public void The_shapes_have_the_sizes_the_apps_want()
    {
        var info = new MediaInfo { Path = "x", Container = "mp4", Video = new MediaStreamInfo { Index = 0, Kind = MediaStreamKind.Video, Codec = "h264", Width = 3840, Height = 2160 } };
        Assert.Equal((1080, 1920), ShortClip.Size(ShortShape.Tall, info));
        Assert.Equal((1080, 1080), ShortClip.Size(ShortShape.Square, info));
        Assert.Equal((1920, 1080), ShortClip.Size(ShortShape.Original, info));
    }

    // ---------- the speaker's framing ----------

    private static SeenFace Face(double x, double mouth = 0, double size = 0.12) => new(x - size / 2, 0.3, size, size * 1.6, mouth);

    [Fact]
    public void One_person_is_followed_and_the_camera_holds_still_while_they_stay_near_the_middle()
    {
        // Five seconds wobbling near the middle, then a walk to the right over two and a half seconds, then still.
        var frames = new List<List<SeenFace>>();
        for (int f = 0; f < 50; f++)
            frames.Add([Face(f < 20 ? 0.5 + (f % 2 == 0 ? 0.01 : -0.01) : Math.Min(0.75, 0.5 + (f - 20) * 0.025))]);
        var path = SpeakerFraming.Plan(frames, 0.316, 1)!;
        Assert.NotNull(path);
        // One place while they wobble; then the camera glides after them, never cutting.
        Assert.Single(path.Keys, k => k.T < 5);
        Assert.NotEmpty(path.Keys.Where(k => k.T >= 5));
        Assert.All(path.Keys.Skip(1), k => Assert.False(k.Cut));
        Assert.InRange(path.Keys[^1].X, 0.7, 0.8);
    }

    [Fact]
    public void The_camera_cuts_to_whoever_starts_talking()
    {
        var frames = new List<List<SeenFace>>();
        for (int f = 0; f < 48; f++)
        {
            bool leftTalks = f < 24;
            frames.Add([Face(0.25, leftTalks ? 8 : 0.5), Face(0.75, leftTalks ? 0.5 : 8)]);
        }
        var path = SpeakerFraming.Plan(frames, 0.316, 1)!;
        Assert.InRange(path.Keys[0].X, 0.2, 0.3);
        var cut = Assert.Single(path.Keys.Skip(1));
        Assert.True(cut.Cut);
        Assert.InRange(cut.X, 0.7, 0.8);
        // Within a second of the change, not before it.
        Assert.InRange(cut.T, 24 / SpeakerFraming.Rate, 24 / SpeakerFraming.Rate + 1.2);
    }

    [Fact]
    public void Without_a_steady_face_there_is_no_path()
    {
        Assert.Null(SpeakerFraming.Plan(Enumerable.Range(0, 40).Select(_ => new List<SeenFace>()).ToList(), 0.316, 1));
        // A face in three frames of forty: a passer-by.
        var frames = Enumerable.Range(0, 40).Select(f => f is 5 or 6 or 7 ? new List<SeenFace> { Face(0.5) } : []).ToList();
        Assert.Null(SpeakerFraming.Plan(frames, 0.316, 1));
    }

    [Fact]
    public void A_small_face_is_brought_closer_no_further_than_allowed()
    {
        var frames = Enumerable.Range(0, 20).Select(_ => new List<SeenFace> { Face(0.5, 0, 0.05) }).ToList();
        Assert.Equal(1.27, SpeakerFraming.Plan(frames, 0.316, 1.27)!.Zoom, 3);
        var near = Enumerable.Range(0, 20).Select(_ => new List<SeenFace> { Face(0.5, 0, 0.2) }).ToList();
        Assert.Equal(1, SpeakerFraming.Plan(near, 0.316, 2)!.Zoom, 3);
    }

    [Fact]
    public void The_window_jumps_at_a_cut_and_eases_into_a_glide()
    {
        var cut = SpeakerFraming.Expression([new CameraKey(0, 0.25, true), new CameraKey(5, 0.75, true)], 600, 1920, t => t);
        Assert.Equal("if(lt(t,5),180,1140)", cut);
        var glide = SpeakerFraming.Expression([new CameraKey(0, 0.25, true), new CameraKey(5, 0.75, false)], 600, 1920, t => t);
        Assert.Equal("if(lt(t,5),if(lt(t,4.2),180,180+(1140-180)*(3*((t-4.2)/0.8)*((t-4.2)/0.8)-2*((t-4.2)/0.8)*((t-4.2)/0.8)*((t-4.2)/0.8))),1140)", glide);
        // Times move with the pauses cut out.
        var moved = SpeakerFraming.Expression([new CameraKey(0, 0.25, true), new CameraKey(5, 0.75, true)], 600, 1920, t => t - 1);
        Assert.Equal("if(lt(t,4),180,1140)", moved);
    }

    [Fact]
    public void A_talking_mouth_moves_more_than_the_eyes()
    {
        int w = 40, h = 40;
        var before = new byte[w * h];
        var now = new byte[w * h];
        for (int y = 26; y < 34; y++)
            for (int x = 14; x < 26; x++)
                now[y * w + x] = 120;
        Assert.True(SpeakerFraming.MouthMovement(now, before, w, h, 10, 10, 20, 20) > 10);
        // The whole face moving: no more in the mouth than in the eyes.
        var moved = Enumerable.Repeat((byte)90, w * h).ToArray();
        Assert.Equal(0, SpeakerFraming.MouthMovement(moved, before, w, h, 10, 10, 20, 20), 3);
    }

    // ---------- captions and headline ----------

    [Fact]
    public void Tall_captions_sit_a_third_up_in_short_lines_under_a_headline()
    {
        var words = "This is how a short clip shows its words one line at a time"
            .Split(' ').Select((w, i) => new CaptionWord(w, TimeSpan.FromSeconds(i * 0.4), TimeSpan.FromSeconds(i * 0.4 + 0.35))).ToList();
        var ass = AnimatedCaptions.Ass(words, 1080, 1920, new AnimatedCaptionOptions(CaptionStyle.Lines, CaptionPlace.Lower, 0.042, "#FFD60A", 4), "Why space food tastes bland", 4.5);
        Assert.Contains("Style: Headline,Segoe UI,61,&H00141414,&H00141414,&H000AD6FF,", ass);
        Assert.Contains(@"Dialogue: 1,0:00:00.00,0:00:04.50,Headline,,0,0,0,,{\fad(200,350)}Why space food tastes bland", ass);
        // A third of the way up: 0.28 of the height above the bottom.
        Assert.Contains(",2,90,90,538,-1", ass);
        var lines = ass.Split('\n').Where(l => l.StartsWith("Dialogue: 0,")).Select(l => l[(l.LastIndexOf(",,", StringComparison.Ordinal) + 2)..].Trim()).ToList();
        Assert.All(lines, l => Assert.True(l.Split(' ').Length <= 4 && l.Length <= 20, l));
    }

    // ---------- clips made ----------

    private static Moment TalkMoment()
    {
        // Words in the fixture's talk: 1–3, 4.5–6.5, 7–9.5 and 10–11.5 s.
        var words = new List<SpokenWord>();
        foreach (var (from, to) in new[] { (1.0, 3.0), (4.5, 6.5), (7.0, 9.5), (10.0, 11.5) })
            for (double t = from; t < to - 0.1; t += 0.5)
                words.Add(new SpokenWord(words.Count % 5 == 4 ? "word." : "word", t, Math.Min(to, t + 0.45)));
        return new Moment(0.85, 11.75, 80, [MomentReason.Question], words) { Opening = "word word word" };
    }

    [Theory]
    [InlineData(ShortShape.Tall, ShortFraming.Speaker, 1080, 1920)]
    [InlineData(ShortShape.Square, ShortFraming.Whole, 1080, 1080)]
    [InlineData(ShortShape.Original, ShortFraming.Speaker, 1280, 720)]
    public async Task A_moment_becomes_a_short_with_its_pauses_cut_and_its_sound_levelled(ShortShape shape, ShortFraming framing, int width, int height)
    {
        var input = await Fixtures.GetAsync("talk.mp4");
        var levels = SoundLevels.Measure(await Transcriber.ReadSpeechAsync(input));
        var moment = TalkMoment();
        var pauses = ShortMoments.Pauses(moment, levels);
        Assert.NotEmpty(pauses);
        var result = Path.Combine(Fixtures.NewDir(), $"short {shape}.mp4");
        var progress = new RecordingProgress();
        await ShortClip.RenderAsync(input, moment, result, new ShortOptions(shape, framing), "Why does the tone waver?", null, levels, progress);
        var info = await MediaProbe.ProbeAsync(result);
        Assert.Equal((width, height), (info.Width, info.Height));
        Assert.NotNull(info.Audio);
        double expected = moment.Length - pauses.Sum(p => p.End - p.Start);
        output.WriteLine($"{shape}: {info.DurationSeconds:0.00} s, expected {expected:0.00} s ({pauses.Count} pauses cut)");
        Assert.InRange(info.DurationSeconds, expected - 0.25, expected + 0.25);
        Assert.True(info.FrameRate is > 29 and < 31);
        // Picture and sound end together: each part kept is a whole number of frames and the same stretch of sound.
        Assert.InRange(Math.Abs(info.Video!.Duration!.Value.TotalSeconds - info.Audio!.Duration!.Value.TotalSeconds), 0, 0.06);
        var loudness = await AudioTools.MeasureLoudnessAsync(result);
        Assert.InRange(loudness.IntegratedLufs, -15.5, -12.5);
        Assert.Equal(1, progress.Last, 3);
    }

    [Fact]
    public async Task Real_speech_gets_its_words_timed_and_a_moment_that_starts_at_a_sentence()
    {
        var model = ModelStore.Find(ModelCatalog.WhisperFast);
        if (model is null)
        {
            output.WriteLine($"Skipped: {ModelCatalog.WhisperFast.FileName} isn't in {ModelStore.Folder}.");
            return;
        }
        var wav = Fixtures.Speech("Here is a question for you. Why do astronauts lose their sense of taste in space? " +
            "Because the fluids in the body float up into the head, and it feels like a cold that never ends. " +
            "That is why food up there tastes so bland, and why so many of them love hot sauce. " +
            "The next thing we did was test the new water system, which took most of the afternoon.");
        var samples = await Transcriber.ReadSpeechAsync(wav);
        var transcript = await Transcriber.TranscribeWordsAsync(samples, new TranscribeOptions(model, "en", WordTimes: true));
        var words = transcript.Segments.SelectMany(s => s.Words).ToList();
        output.WriteLine(string.Join(" ", words.Select(w => $"{w.Text}@{w.Start:0.00}")));
        Assert.True(words.Count >= 50, $"only {words.Count} words");
        double length = samples.Length / 16000.0;
        Assert.All(words, w => Assert.InRange(w.Start, 0, length));
        Assert.True(words.Zip(words.Skip(1)).All(p => p.Second.Start >= p.First.Start - 0.05), "word times go back");
        // "astronauts" is said in the second sentence, a few seconds in.
        var astronauts = words.First(w => w.Text.StartsWith("astronaut", StringComparison.OrdinalIgnoreCase));
        Assert.InRange(astronauts.Start, 1.5, 6);
        var found = ShortMoments.Find(transcript.Segments, SoundLevels.Measure(samples), ShortLength.Quick, 1);
        var best = Assert.Single(found);
        output.WriteLine($"{best.Start:0.00}–{best.End:0.00} {best.Score}: {best.Text}");
        Assert.StartsWith("Why do astronauts", best.Opening);
        // It ends with the answer, not with the next topic.
        Assert.DoesNotContain("next thing", best.Text);
    }
}
