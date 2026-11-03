using Petal.Core.Models;
using Xunit.Abstractions;

namespace Petal.Engines.Media.Tests;

[CollectionDefinition("Models", DisableParallelization = true)]
public sealed class ModelsCollection;

/// <summary>Reading text aloud into MP3s, checked by transcribing them back with Whisper.</summary>
[Collection("Models")]
public sealed class SpeechTests(ITestOutputHelper output)
{
    [Fact]
    public void Text_is_spoken_in_pieces_cut_at_sentences()
    {
        var sentence = "This sentence is about sixty characters long, more or less. ";
        var text = "A Heading\n\n" + string.Concat(Enumerable.Repeat(sentence, 60)) + "\n\nمرحبا بكم. هذه جملة عربية؟ نعم";
        var chunks = Speech.Chunks(text);
        output.WriteLine(string.Join("\n---\n", chunks.Select(c => c.Length + ": " + c[..Math.Min(70, c.Length)])));
        Assert.All(chunks, c => Assert.True(c.Length <= 1500, $"{c.Length} characters"));
        Assert.StartsWith("A Heading.", chunks[0]);
        Assert.All(chunks.SkipLast(1), c => Assert.Matches(@"[.!?؟]$", c));
        Assert.Equal(text.Count(char.IsLetter), chunks.Sum(c => c.Count(char.IsLetter)));
        Assert.Empty(Speech.Chunks("  \n\n ... \n"));
    }

    [Theory]
    [InlineData("The quick brown fox", "en")]
    [InlineData("هذا نص عربي مع كلمة Petal واحدة", "ar")]
    [InlineData("Bugün hava çok güzel, değil mi? Işık ve şeker.", "tr")]
    [InlineData("Привет, как дела?", "ru")]
    [InlineData("12345 !!!", "en")]
    public void The_language_comes_from_the_letters(string text, string language) =>
        Assert.Equal(language, Speech.GuessLanguage(text));

    [Fact]
    public void A_natural_voice_already_here_is_preferred()
    {
        var windows = new SpeechVoice("w", "Microsoft Zira", "en-US", VoiceKind.Windows);
        var arabic = new SpeechVoice("h", "Microsoft Hoda", "ar-EG", VoiceKind.Windows);
        var voices = new[] { windows, arabic, Speech.Linda };
        // Linda isn't on a fresh PC: English falls back to Windows until she is; Arabic is Windows' own voice.
        Assert.Same(Speech.Linda.IsReady ? Speech.Linda : windows, Speech.Choose("en", voices));
        Assert.Same(arabic, Speech.Choose("ar", voices));
        Assert.Null(Speech.Choose("ja", voices));
        // Only natural voices whose recordings may be used commercially are offered: none for Arabic.
        Assert.DoesNotContain(Speech.NaturalVoices, v => v.Speaks("ar"));
    }

    private static string? Whisper() => ModelStore.Find(ModelCatalog.WhisperFast);

    [Fact]
    public async Task A_windows_voice_reads_text_into_an_mp3()
    {
        var voice = Speech.WindowsVoices().FirstOrDefault(v => v.Speaks("en"));
        if (voice is null)
        {
            output.WriteLine("Skipped: no English Windows voice.");
            return;
        }
        var mp3 = Path.Combine(Fixtures.NewDir(), "read aloud.mp3");
        var steps = new List<double>();
        await Speech.SpeakToFileAsync("Petal reads documents aloud.\n\nThis is the second paragraph of the test.", voice, 1.0, mp3, "Test", new SyncProgress(steps.Add));

        var info = await MediaAssert.ValidAsync(mp3);
        output.WriteLine($"{voice.Name}: {info.DurationSeconds:0.0} s");
        Assert.InRange(info.DurationSeconds, 2, 15);
        Assert.Equal(1.0, steps[^1]);
        if (Whisper() is { } model)
        {
            var text = (await Transcriber.TranscribeAsync(mp3, new TranscribeOptions(model, "en"))).ToText();
            output.WriteLine(text);
            Assert.Contains("second paragraph", text, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// With PETAL_VOICES pointing at a folder holding Piper and its voice (as ModelStore would download them), the natural
    /// voice speaks English.
    /// </summary>
    [Theory]
    [InlineData("en", "Welcome to Petal. This document is read aloud with a natural voice.", "natural voice")]
    public async Task A_natural_voice_reads_text_into_an_mp3(string language, string text, string heard)
    {
        var voices = Environment.GetEnvironmentVariable("PETAL_VOICES");
        var whisper = Whisper();
        if (voices is null)
        {
            output.WriteLine("Skipped: PETAL_VOICES isn't set.");
            return;
        }
        var folder = ModelStore.Folder;
        ModelStore.Folder = voices;
        try
        {
            var voice = Speech.Linda;
            Assert.True(voice.IsReady, $"{voice.Name} isn't in {voices}");
            var mp3 = Path.Combine(Fixtures.NewDir(), $"{language}.mp3");
            await Speech.SpeakToFileAsync(text, voice, 1.0, mp3, "Test");
            var info = await MediaAssert.ValidAsync(mp3);
            output.WriteLine($"{voice.Name}: {info.DurationSeconds:0.0} s");
            Assert.InRange(info.DurationSeconds, 2, 15);

            // Faster speech is shorter.
            var fast = Path.Combine(Fixtures.NewDir(), $"{language} fast.mp3");
            await Speech.SpeakToFileAsync(text, voice, 1.5, fast);
            Assert.True((await MediaAssert.ValidAsync(fast)).DurationSeconds < info.DurationSeconds * 0.85);

            if (whisper is not null)
            {
                var transcript = (await Transcriber.TranscribeAsync(mp3, new TranscribeOptions(whisper, language))).ToText();
                output.WriteLine(transcript);
                Assert.Contains(heard, transcript, StringComparison.OrdinalIgnoreCase);
            }
        }
        finally
        {
            ModelStore.Folder = folder;
        }
    }

    private sealed class SyncProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
