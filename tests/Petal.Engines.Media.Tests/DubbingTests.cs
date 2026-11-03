using System.Diagnostics;
using System.Text;
using Petal.Core.Models;
using Xunit.Abstractions;

namespace Petal.Engines.Media.Tests;

/// <summary>
/// Dub: the lines cut from what was heard, where each new line goes and how quick it is said, the voice track mixed over
/// what is kept, the finished file's tracks, Chatterbox's tokenizer and sampling, and (with the model) a voice copied.
/// </summary>
[Collection("Models")]
public sealed class DubbingTests(ITestOutputHelper output)
{
    /// <summary>A talk from sentences, each followed by its pause: words of 0.3 s with 0.05 s between them.</summary>
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

    // ---------- the lines ----------

    [Fact]
    public void A_sentence_broken_off_by_a_pause_is_one_line()
    {
        var lines = DubTrack.Lines(Talk(("And it's so", 1.6), ("awesome up here.", 0.4), ("We fly around the station every single day.", 0.4)));
        Assert.Equal(["And it's so awesome up here.", "We fly around the station every single day."], lines.Select(l => l.Original).ToArray());
        Assert.Equal(0.5, lines[0].Start, 3);
        Assert.All(lines, l => Assert.Equal("", l.Translation));
    }

    [Fact]
    public void A_short_reply_joins_the_line_that_follows_closely()
    {
        var lines = DubTrack.Lines(Talk(("Yes.", 0.2), ("I would love to go there with you one day.", 0.6), ("Never.", 1.5), ("That is all for today.", 0.3)));
        Assert.Equal(["Yes. I would love to go there with you one day.", "Never.", "That is all for today."], lines.Select(l => l.Original).ToArray());
    }

    [Fact]
    public void Speech_written_without_stops_keeps_its_phrases()
    {
        // As Whisper often writes Arabic: no full stops, phrases between pauses. Their ends say nothing about sentences.
        var lines = DubTrack.Lines(Talk(("ذهبنا إلى السوق في الصباح الباكر مع العائلة كلها", 0.8), ("واشترينا الخضار والفواكه من البائع القديم", 0.8),
            ("ثم رجعنا إلى البيت قبل الظهر بقليل", 0.3)));
        Assert.Equal(3, lines.Count);
    }

    [Fact]
    public void A_long_sentence_is_cut_at_its_longest_pause_into_lines_of_twelve_seconds_at_most()
    {
        // Forty words without a stop, about fourteen seconds, with one long breath after the twentieth.
        var words = new List<SpokenWord>();
        double t = 0;
        for (int i = 0; i < 40; i++)
        {
            words.Add(new SpokenWord(i == 39 ? "end." : $"word{i}", t, t + 0.3));
            t += i == 19 ? 0.9 : 0.35;
        }
        var lines = DubTrack.Lines([new TranscriptSegment(TimeSpan.Zero, TimeSpan.FromSeconds(t), "") { Words = words }]);
        Assert.Equal(2, lines.Count);
        Assert.All(lines, l => Assert.True(l.Length <= DubTrack.LongestLine, $"{l.Length:0.0} s"));
        Assert.EndsWith("word19", lines[0].Original);
        Assert.StartsWith("word20", lines[1].Original);
    }

    // ---------- where the new lines go ----------

    [Fact]
    public void Lines_go_where_their_originals_began_and_long_ones_are_said_quicker_and_push_on()
    {
        var places = DubTrack.Place([(0, 2.0), (3, 3.0), (5, 1.0), (8, 0.5)], 10);
        // Room enough: as said, where it was.
        Assert.Equal(new DubPlace(0, 1, 2.0), places[0]);
        // Three seconds into less than two: 1.3 times quicker at most, and the rest runs on into the next line's time.
        Assert.Equal(3, places[1].At, 3);
        Assert.Equal(DubTrack.Quickest, places[1].Speed, 3);
        Assert.Equal(3.0 / DubTrack.Quickest, places[1].Length, 3);
        Assert.Equal(3 + 3.0 / DubTrack.Quickest + 0.06, places[2].At, 3);
        // Once there is room again, back on time.
        Assert.Equal(8, places[3].At, 3);
        Assert.Equal(1, places[3].Speed, 3);
        // A little quicker is enough: no faster than needed.
        var close = DubTrack.Place([(0, 2.2), (2.05, 1)], 4);
        Assert.Equal(2.2 / 2.0, close[0].Speed, 3);
    }

    // ---------- the sound ----------

    private static float[] Tone(double seconds, int rate, double amplitude = 0.3, double frequency = 220) =>
        Enumerable.Range(0, (int)(seconds * rate)).Select(i => (float)(amplitude * Math.Sin(2 * Math.PI * frequency * i / rate))).ToArray();

    [Fact]
    public void The_quiet_the_voice_model_leaves_around_the_words_is_trimmed()
    {
        const int Rate = 24000;
        var sound = new float[Rate / 2].Concat(Tone(1, Rate)).Concat(new float[Rate]).ToArray();
        var trimmed = DubTrack.Trim(sound, Rate);
        double seconds = trimmed.Length / (double)Rate;
        Assert.InRange(seconds, 1.0, 1.2);
        Assert.Empty(DubTrack.Trim(new float[Rate], Rate));
    }

    [Fact]
    public void Speech_level_is_that_of_the_words_not_the_pauses()
    {
        const int Rate = 24000;
        double tone = DubTrack.SpeechLevel(Tone(1, Rate, 0.1), Rate);
        double withPauses = DubTrack.SpeechLevel(Tone(1, Rate, 0.1).Concat(new float[Rate * 3 / 4]).ToArray(), Rate);
        Assert.Equal(20 * Math.Log10(0.1 / Math.Sqrt(2)), tone, 1);
        Assert.Equal(tone, withPauses, 1);
    }

    [Fact]
    public void Under_each_new_line_the_original_voice_gives_way_and_comes_back_after()
    {
        const int Rate = 1000;
        var original = Enumerable.Repeat(0.5f, 3 * Rate).ToArray();
        var take = Enumerable.Repeat(0.25f, Rate / 2).ToArray();
        var track = DubTrack.Mix(original, [(take, 1.0, 0.9, 1.4)], Rate);
        Assert.Equal(original.Length, track.Length);
        Assert.Equal(0.5f, track[200]);
        Assert.Equal(0.25f, track[1200], 3);
        // From a little before the original line to a little after the new one, none of the old voice.
        Assert.Equal(0f, track[880], 3);
        Assert.Equal(0f, track[1510], 3);
        Assert.Equal(0.5f, track[1800]);
        // The new line fades in over a few milliseconds: no click.
        Assert.True(track[1000] < 0.05f);
    }

    [Fact]
    public async Task A_line_said_quicker_keeps_its_pitch_and_its_loudness()
    {
        var sound = Tone(1, 24000, 0.3, 440);
        var quicker = await DubTrack.ReshapeAsync(sound, 24000, 48000, 1.25, CancellationToken.None);
        Assert.InRange(quicker.Length / 48000.0, 0.76, 0.84);
        // Count the rises through zero in the middle: 440 a second still.
        int rises = 0;
        int from = quicker.Length / 4, to = quicker.Length * 3 / 4;
        for (int i = from + 1; i < to; i++)
            if (quicker[i - 1] < 0 && quicker[i] >= 0)
                rises++;
        double frequency = rises / ((to - from) / 48000.0);
        Assert.InRange(frequency, 425, 455);
        // And as loud as it was.
        Assert.Equal(DubTrack.SpeechLevel(sound, 24000), DubTrack.SpeechLevel(quicker, 48000), 0);
        var same = await DubTrack.ReshapeAsync(sound, 24000, 48000, 1, CancellationToken.None);
        Assert.InRange(same.Length, 47900, 48100);
    }

    [Fact]
    public async Task The_dubbed_file_keeps_the_picture_and_has_the_new_voice_the_original_and_subtitles()
    {
        var video = await Fixtures.GetAsync("hq.mp4");
        var dir = Fixtures.NewDir();
        var music = Path.Combine(dir, "Sounds.wav");
        var voices = Path.Combine(dir, "voices.wav");
        DubTrack.WriteWav(music, Tone(5, 48000, 0.1, 110), 48000);
        DubTrack.WriteWav(voices, Tone(5, 48000, 0.2, 330), 48000);
        var srt = Path.Combine(dir, "words.srt");
        // The words end well before the picture does: the picture and sound still run to the end.
        await File.WriteAllTextAsync(srt, "1\n00:00:00,500 --> 00:00:02,000\nمرحبا بكم\n\n", new UTF8Encoding(false));
        var result = await DubTrack.MuxAsync(video, music, voices, Path.Combine(dir, "hq Arabic.mp4"), "ar", "English (original)", srt, null, CancellationToken.None);

        var info = await MediaAssert.ValidAsync(result, 5);
        var source = await MediaProbe.ProbeAsync(video);
        Assert.Equal(source.VideoCodec, info.VideoCodec);
        var audio = info.AudioStreams.ToList();
        Assert.Equal(2, audio.Count);
        Assert.Equal("ara", audio[0].Language);
        Assert.True(audio[0].IsDefault);
        Assert.False(audio[1].IsDefault);
        // Players show a track by its handler name.
        Assert.Equal("English (original)", audio[1].Tags.GetValueOrDefault("handler_name"));
        Assert.Equal("Arabic (dubbed)", audio[0].Tags.GetValueOrDefault("handler_name"));
        var subtitle = Assert.Single(info.SubtitleStreams);
        Assert.Equal("ara", subtitle.Language);
        Assert.Contains("made by AI", info.Tags.GetValueOrDefault("comment") ?? "");

        // The new track is the sounds and the voices together, the voices twice as loud as the sounds, as they were made.
        var mixed = (await MediaPreview.DecodePcmAsync(result, new PcmDecodeOptions(48000, 1))).Samples.Skip(48000).Take(48000).ToArray();
        double Level(double frequency)
        {
            double sine = 0, cosine = 0;
            for (int i = 0; i < mixed.Length; i++)
            {
                sine += mixed[i] * Math.Sin(2 * Math.PI * frequency * i / 48000);
                cosine += mixed[i] * Math.Cos(2 * Math.PI * frequency * i / 48000);
            }
            return 2 * Math.Sqrt(sine * sine + cosine * cosine) / mixed.Length;
        }
        double musicLevel = Level(110), voicesLevel = Level(330);
        Assert.True(musicLevel > 0.05, $"music {musicLevel:0.000}");
        Assert.InRange(voicesLevel / musicLevel, 1.8, 2.2);
    }

    [Fact]
    public void A_wav_written_reads_back()
    {
        var path = Path.Combine(Fixtures.NewDir(), "line.wav");
        var sound = Tone(0.5, 24000);
        DubTrack.WriteWav(path, sound, 24000);
        var bytes = File.ReadAllBytes(path);
        Assert.Equal(44 + sound.Length * 4, bytes.Length);
        Assert.Equal("RIFF", Encoding.ASCII.GetString(bytes, 0, 4));
        Assert.Equal(24000, BitConverter.ToInt32(bytes, 24));
        Assert.Equal(sound[123], BitConverter.ToSingle(bytes, 44 + 123 * 4));
    }

    /// <summary>
    /// With PETAL_TIGER pointing at tiger.int8.onnx: of a chord progression over a beat with rain under it, the rain stays and
    /// the music goes.
    /// </summary>
    [Fact]
    public async Task The_music_is_taken_out_and_the_other_sounds_stay()
    {
        var model = Environment.GetEnvironmentVariable("PETAL_TIGER");
        if (model is null)
        {
            output.WriteLine("Skipped: PETAL_TIGER isn't set.");
            return;
        }
        var dir = Fixtures.NewDir();
        string music = Path.Combine(dir, "music.wav"), rain = Path.Combine(dir, "rain.wav");
        // A piano-like chord every half second over a kick drum, and rain: noise in drops and a steady hiss.
        using (var p = Process.Start(new ProcessStartInfo(Fixtures.Ffmpeg, ["-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i",
            "aevalsrc='0.08*(sin(2*PI*(220+110*floor(mod(t,4)))*t)+0.5*sin(4*PI*(220+110*floor(mod(t,4)))*t)+sin(2*PI*277*t)+sin(2*PI*330*t))*exp(-3*mod(t,0.5))+0.3*sin(2*PI*55*t)*exp(-12*mod(t,0.5))':s=44100:d=20",
            "-ac", "2", "-c:a", "pcm_f32le", music]) { CreateNoWindow = true })!)
            p.WaitForExit();
        using (var p = Process.Start(new ProcessStartInfo(Fixtures.Ffmpeg, ["-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i",
            "anoisesrc=color=pink:amplitude=0.08:d=20:r=44100,highpass=f=300,volume='0.6+0.4*gt(random(0),0.97)':eval=frame", "-ac", "2", "-c:a", "pcm_f32le", rain])
            { CreateNoWindow = true })!)
            p.WaitForExit();
        var m = (await MediaPreview.DecodePcmAsync(music, new PcmDecodeOptions(MusicRemover.Rate, 2))).Samples;
        var r = (await MediaPreview.DecodePcmAsync(rain, new PcmDecodeOptions(MusicRemover.Rate, 2))).Samples;
        int n = Math.Min(m.Length, r.Length) / 2;
        var channels = new[] { new float[n], new float[n] };
        for (int i = 0; i < n; i++)
            for (int c = 0; c < 2; c++)
                channels[c][i] = m[2 * i + c] + r[2 * i + c];
        var clock = Stopwatch.StartNew();
        var kept = MusicRemover.Sounds(model, channels);
        output.WriteLine($"{n / (double)MusicRemover.Rate:0} s in {clock.Elapsed.TotalSeconds:0} s");
        Assert.Equal(n, kept[0].Length);

        // How much of each is left in what was kept: the kept sound's projection on each.
        double Left(float[] source, int c)
        {
            double dot = 0, own = 0;
            for (int i = 0; i < n; i++)
            {
                dot += kept[c][i] * (double)source[2 * i + c];
                own += source[2 * i + c] * (double)source[2 * i + c];
            }
            return dot / own;
        }
        double musicLeft = Left(m, 0), rainLeft = Left(r, 0);
        output.WriteLine($"music left {musicLeft:0.00}, rain left {rainLeft:0.00}");
        Assert.True(rainLeft > 0.7, $"rain left {rainLeft:0.00}");
        Assert.True(musicLeft < 0.2, $"music left {musicLeft:0.00}");
    }

    // ---------- what the voice is given to say ----------

    [Theory]
    [InlineData(0, "صفر")]
    [InlineData(2, "اثنين")]
    [InlineData(12, "اثني عشر")]
    [InlineData(20, "عشرين")]
    [InlineData(21, "واحد وعشرين")]
    [InlineData(340, "ثلاثمئة وأربعين")]
    [InlineData(1000, "ألف")]
    [InlineData(2026, "ألفين وستة وعشرين")]
    [InlineData(10_000, "عشرة آلاف")]
    [InlineData(15_000, "خمسة عشر ألفًا")]
    [InlineData(103_000, "مئة وثلاثة آلاف")]
    [InlineData(200_000, "مئتي ألف")]
    [InlineData(1_234_567, "مليون ومئتين وأربعة وثلاثين ألفًا وخمسمئة وسبعة وستين")]
    [InlineData(3_000_000_000, "ثلاثة مليارات")]
    public void Numbers_are_said_in_Arabic(long number, string said) => Assert.Equal(said, SpokenText.Arabic(number));

    [Theory]
    [InlineData(0, "zero")]
    [InlineData(42, "forty-two")]
    [InlineData(340, "three hundred forty")]
    [InlineData(10_000, "ten thousand")]
    [InlineData(1_234_567, "one million two hundred thirty-four thousand five hundred sixty-seven")]
    public void Numbers_are_said_in_English(long number, string said) => Assert.Equal(said, SpokenText.English(number));

    [Theory]
    [InlineData("وأنا حوالي 340 رطلاً", "ar", "وأنا حوالي ثلاثمئة وأربعين رطلاً")]
    [InlineData("سيكون لدينا 10,000 شخص", "ar", "سيكون لدينا عشرة آلاف شخص")]
    [InlineData("في حوالي ٢٠ ثانية", "ar", "في حوالي عشرين ثانية")]
    [InlineData("نجح 50% منهم و3 آخرون", "ar", "نجح خمسين بالمئة منهم وثلاثة آخرون")]
    [InlineData("السرعة 1.5 مرة", "ar", "السرعة واحد فاصلة خمسة مرة")]
    // A name in brackets is said, not skipped; quotation marks go.
    [InlineData("نحن نطير مثل (سوبرمان) ونحبه", "ar", "نحن نطير مثل سوبرمان ونحبه")]
    [InlineData("وأقول، \" يو، أنا في أوروبا\"", "ar", "وأقول، يو، أنا في أوروبا")]
    [InlineData("In 2026 about 10,000 people flew, 1.5% of them for $20.", "en", "In twenty twenty-six about ten thousand people flew, one point five percent of them for twenty dollars.")]
    [InlineData("It's 007 and MP3, not 2005", "en", "It's zero zero seven and MP3, not two thousand five")]
    public void Lines_are_made_ready_to_say(string text, string language, string said) => Assert.Equal(said, SpokenText.Of(text, language));

    // ---------- Chatterbox's tokenizer and sampling ----------

    private static string Tokenizer(bool fullCase)
    {
        var vocab = new Dictionary<string, int> { ["[START]"] = 255, ["[STOP]"] = 0, ["[UNK]"] = 1, ["[SPACE]"] = 2, ["[en]"] = 700, ["[ar]"] = 701 };
        foreach (var piece in new[] { "h", "i", "H", "t", "e", "r", "!", "hi", "Hi", "th", "the", "ther", "there", "ا", "ٔ", "ن", "أ" })
            vocab[piece] = vocab.Count + 10;
        var merges = new[] { "h i", "H i", "t h", "th e", "the r", "ther e", "ا ٔ" };
        var added = new[] { "[START]", "[STOP]", "[UNK]", "[SPACE]", "[en]", "[ar]" }.Select(t => new Dictionary<string, object> { ["content"] = t, ["id"] = vocab[t] });
        var json = new Dictionary<string, object>
        {
            ["added_tokens"] = added.ToArray(),
            ["model"] = new Dictionary<string, object> { ["type"] = "BPE", ["vocab"] = vocab, ["merges"] = merges },
        };
        if (fullCase)
            json["normalizer"] = new Dictionary<string, object> { ["type"] = "NFKD" };
        var path = Path.Combine(Fixtures.NewDir(), "tokenizer.json");
        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(json));
        return path;
    }

    [Fact]
    public void Text_is_tagged_with_its_language_spaced_and_joined_into_pieces()
    {
        var tokenizer = ChatterboxTokenizer.Load(Tokenizer(fullCase: true));
        Assert.Equal("[en]Hi[SPACE]there!", tokenizer.Prepare("  Hi   there! ", "en"));
        var vocab = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Tokenizer(fullCase: true))).RootElement.GetProperty("model").GetProperty("vocab");
        long Id(string piece) => vocab.GetProperty(piece).GetInt32();
        Assert.Equal([ChatterboxTokenizer.Exaggeration, ChatterboxTokenizer.Start, 700, Id("Hi"), 2, Id("there"), Id("!"),
                ChatterboxTokenizer.End, ChatterboxTokenizer.StartSpeech, ChatterboxTokenizer.StartSpeech],
            tokenizer.Encode("Hi there!", "en"));
        // Older models learnt lower case only.
        Assert.Equal("[en]hi[SPACE]there!", ChatterboxTokenizer.Load(Tokenizer(fullCase: false)).Prepare("Hi there!", "en"));
    }

    [Fact]
    public void Arabic_letters_are_taken_apart_as_the_model_learnt_them()
    {
        var tokenizer = ChatterboxTokenizer.Load(Tokenizer(fullCase: true));
        // أ is alef and hamza above once taken apart (NFKD), and those two join back into the model's own piece.
        var prepared = tokenizer.Prepare("أنا", "ar");
        Assert.Equal("[ar]" + (char)0x0627 + (char)0x0654 + (char)0x0646 + (char)0x0627, prepared);
        Assert.Equal([(char)0x0627 + "" + (char)0x0654, ((char)0x0646).ToString(), ((char)0x0627).ToString()], tokenizer.Merge(prepared[4..]).ToArray());
    }

    [Fact]
    public void Sampling_keeps_to_likely_tokens_and_tires_of_what_was_said()
    {
        var random = new Random(7);
        // One clear favourite: always it.
        for (int i = 0; i < 50; i++)
            Assert.Equal(3, VoiceCloner.Sample([0, 0, 0, 12, 0], [], random));
        // Two close ones and a far one: never the far one (min-p).
        var seen = new HashSet<long>();
        for (int i = 0; i < 200; i++)
            seen.Add(VoiceCloner.Sample([5, 5.2f, -10, 0, 0], [], random));
        Assert.Equal([0, 1], seen.Order().ToArray());
        // A token already said is less likely: with two equal, the one not said wins more often.
        int fresh = 0;
        for (int i = 0; i < 400; i++)
            if (VoiceCloner.Sample([4, 4], [0], random) == 1)
                fresh++;
        Assert.InRange(fresh, 240, 400);
    }

    // ---------- the voice, with the model ----------

    /// <summary>
    /// With PETAL_CHATTERBOX pointing at a models folder holding chatterbox-v3 (and Whisper's fast model, to hear it back),
    /// a voice is learnt from a few seconds of speech and says a new line in English and in Arabic.
    /// </summary>
    [Fact]
    public async Task A_voice_learnt_from_a_few_seconds_says_new_lines_in_English_and_Arabic()
    {
        var folder = Environment.GetEnvironmentVariable("PETAL_CHATTERBOX");
        if (folder is null)
        {
            output.WriteLine("Skipped: PETAL_CHATTERBOX isn't set.");
            return;
        }
        var saved = ModelStore.Folder;
        ModelStore.Folder = folder;
        try
        {
            var clock = Stopwatch.StartNew();
            using var cloner = new VoiceCloner(Path.GetDirectoryName(ModelStore.Find(ModelCatalog.ChatterboxEncoder))!);
            output.WriteLine($"loaded in {clock.Elapsed.TotalSeconds:0.0} s: {cloner.Describe()}");
            var speech = await MediaPreview.DecodePcmAsync(Fixtures.Speech("Good morning everyone. Today we are going to talk about the stars, and how far away they really are."),
                new PcmDecodeOptions(VoiceCloner.Rate, 1));
            using var voice = cloner.Learn(speech.Samples);
            Assert.Equal(192, voice.Speaker.Length);

            var whisper = ModelStore.Find(ModelCatalog.WhisperFast);
            foreach (var (text, language) in new[] { ("The stars are much further away than they look.", "en"), ("النجوم أبعد بكثير مما تبدو.", "ar") })
            {
                clock.Restart();
                var sound = DubTrack.Trim(cloner.Speak(text, language, voice), VoiceCloner.Rate);
                double seconds = sound.Length / (double)VoiceCloner.Rate;
                output.WriteLine($"{language}: {seconds:0.0} s of speech in {clock.Elapsed.TotalSeconds:0.0} s ({cloner.LastTokens} tokens)");
                Assert.InRange(seconds, 1.0, 8.0);
                Assert.True(DubTrack.SpeechLevel(sound, VoiceCloner.Rate) > -40);
                if (whisper is null)
                    continue;
                using var ears = new LiveTranscriber(whisper, language);
                var heard = await ears.TranscribeAsync(Resample(sound, VoiceCloner.Rate, 16000), false, CancellationToken.None);
                output.WriteLine($"  heard: {heard.Text}");
                Assert.False(string.IsNullOrWhiteSpace(heard.Text));
            }
        }
        finally
        {
            ModelStore.Folder = saved;
        }
    }

    /// <summary>
    /// With PETAL_CHATTERBOX set, downloads what it needs into that folder, and a voice says a line in every language of the
    /// interface, heard back by the most accurate Whisper. Persian, Urdu and Indonesian have no mark of their own in the model.
    /// </summary>
    [Fact]
    public async Task A_voice_says_a_line_in_every_language_of_the_interface()
    {
        var folder = Environment.GetEnvironmentVariable("PETAL_CHATTERBOX");
        if (folder is null)
        {
            output.WriteLine("Skipped: PETAL_CHATTERBOX isn't set.");
            return;
        }
        var saved = ModelStore.Folder;
        ModelStore.Folder = folder;
        try
        {
            foreach (var model in ModelCatalog.Chatterbox.Append(ModelCatalog.WhisperAccurate))
                await ModelStore.DownloadAsync(model);
            using var cloner = new VoiceCloner(Path.GetDirectoryName(ModelStore.Find(ModelCatalog.ChatterboxEncoder))!);
            var speech = await MediaPreview.DecodePcmAsync(Fixtures.Speech("Good morning everyone. Today we are going to talk about the stars, and how far away they really are."),
                new PcmDecodeOptions(VoiceCloner.Rate, 1));
            using var voice = cloner.Learn(speech.Samples);
            var whisper = ModelStore.Find(ModelCatalog.WhisperAccurate)!;
            foreach (var (language, text) in new[]
            {
                ("fr", "La réunion commencera demain matin."),
                ("fr", "Nous parlerons des étoiles et de leur distance."),
                ("fa", "جلسه فردا صبح شروع می‌شود."),
                ("ur", "اجلاس کل صبح شروع ہوگا۔"),
            })
            {
                // A dub says a line again when it comes out wrong: a line gets three tries here too.
                for (int seed = 1; seed <= 3; seed++)
                {
                    var clock = Stopwatch.StartNew();
                    var sound = DubTrack.Trim(cloner.Speak(text, language, voice, seed: seed), VoiceCloner.Rate);
                    using var ears = new LiveTranscriber(whisper, language);
                    var heard = await ears.TranscribeAsync(Resample(sound, VoiceCloner.Rate, 16000), false, CancellationToken.None);
                    output.WriteLine($"{language} try {seed}, {sound.Length / (double)VoiceCloner.Rate:0.0}s in {clock.Elapsed.TotalSeconds:0}s said: {text} | heard: {heard.Text}");
                    Assert.False(string.IsNullOrWhiteSpace(heard.Text));
                }
            }
        }
        finally
        {
            ModelStore.Folder = saved;
        }
    }

    private static float[] Resample(float[] sound, int from, int to)
    {
        var result = new float[(int)((long)sound.Length * to / from)];
        for (int i = 0; i < result.Length; i++)
        {
            double at = i * (double)from / to;
            int k = (int)at;
            result[i] = k + 1 < sound.Length ? (float)(sound[k] * (1 - (at - k)) + sound[k + 1] * (at - k)) : sound[^1];
        }
        return result;
    }
}
