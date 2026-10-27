using System.Diagnostics;
using System.Windows.Threading;
using Petal.App.Hud;
using Petal.App.Tools.Parts;
using Petal.Core.Models;
using Petal.Engines.Media;
using Petal.Recipes.Translation;

namespace Petal.Tests;

/// <summary>
/// Words in another language from speech (Transcribe, Animated Captions): which models each choice needs, the route
/// each takes, text translated a sentence at a time, and the progress card that steps aside to the taskbar.
/// </summary>
[Collection("Models")]
public sealed class SpeechTextTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"petal-speech-text-{Guid.NewGuid():N}");
    private readonly string _savedFolder = ModelStore.Folder;

    public SpeechTextTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        ModelStore.Folder = _savedFolder;
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    /// <summary>A file of the model's exact size, which is all ModelStore asks of one that is here.</summary>
    private void Fake(ModelInfo model)
    {
        using var file = File.Create(Path.Combine(_dir, model.FileName));
        file.SetLength(model.Size);
    }

    private static List<string> Ids(IEnumerable<ModelInfo> models) => models.Select(m => m.Id).ToList();

    [Fact]
    public void Each_choice_asks_only_for_the_models_it_needs()
    {
        ModelStore.Folder = _dir;
        var accurate = ModelCatalog.WhisperAccurate;
        Assert.Empty(SpeechLanguages.Needed(accurate, "auto", "spoken"));
        // An Arabic lesson in Arabic is written as spoken: nothing to translate.
        Assert.Empty(SpeechLanguages.Needed(accurate, "ar", "ar"));
        Assert.Empty(SpeechLanguages.Needed(accurate, "en", "en"));
        Assert.Equal(Ids(Translator.Models), Ids(SpeechLanguages.Needed(accurate, "auto", "ar")));
        Assert.Equal(Ids(Translator.Models), Ids(SpeechLanguages.Needed(accurate, "en", "ar")));
        // Arabic speech into English: Opus-MT, whatever the accuracy.
        Assert.Equal(Ids(Translator.EnglishModels), Ids(SpeechLanguages.Needed(accurate, "ar", "en")));
        Assert.Equal(Ids(Translator.EnglishModels), Ids(SpeechLanguages.Needed(ModelCatalog.WhisperFast, "auto", "en")));
        // French into English with Accurate, which can't translate: a model that can, unless one is here.
        Assert.Equal(["whisper-small"], Ids(SpeechLanguages.Needed(accurate, "fr", "en")));
        Assert.Empty(SpeechLanguages.Needed(ModelCatalog.WhisperBalanced, "fr", "en"));
        Assert.Null(SpeechLanguages.TranslatingModel());
        Fake(ModelCatalog.WhisperFast);
        Assert.Empty(SpeechLanguages.Needed(accurate, "fr", "en"));
        Assert.EndsWith(ModelCatalog.WhisperFast.FileName, SpeechLanguages.TranslatingModel());
        Fake(ModelCatalog.WhisperBalanced);
        Assert.EndsWith(ModelCatalog.WhisperBalanced.FileName, SpeechLanguages.TranslatingModel());
        Assert.Equal(SpeechInto.English, SpeechLanguages.Into("en"));
        Assert.Equal(SpeechInto.Spoken, SpeechLanguages.Into("spoken"));
    }

    [Fact]
    public async Task The_Accurate_model_is_never_asked_to_translate()
    {
        // large-v3 turbo writes nonsense when asked to translate: Petal stops before it would.
        var turbo = Path.Combine(_dir, ModelCatalog.WhisperAccurate.FileName);
        File.WriteAllBytes(turbo, [1, 2, 3]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Transcriber.TranscribeAsync(new float[16000], new TranscribeOptions(turbo, "ar", TranslateToEnglish: true)));
        Assert.False(Transcriber.CanTranslate(turbo));
        Assert.True(Transcriber.CanTranslate(ModelCatalog.WhisperBalanced.FileName));

        // French into English with it, and no model here that can translate: a clear message, nothing made up.
        var speech = await SayAsync("Good morning everyone.");
        if (speech is null)
            return;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => SpeechText.TranscribeAsync(speech, new SpeechTextOptions(turbo, "fr", SpeechInto.English)));
        Assert.Equal(SpeechText.NoTranslatingModel, error.Message);
    }

    [Fact]
    public async Task English_speech_wanted_in_English_is_written_as_spoken()
    {
        if (ModelStore.Find(ModelCatalog.WhisperFast) is not { } model)
            return;
        var speech = await SayAsync("Good morning everyone, the meeting starts at ten o clock.");
        if (speech is null)
            return;
        // The language is told first, so English isn't put through a translation.
        var transcript = await SpeechText.TranscribeAsync(speech, new SpeechTextOptions(model, "auto", SpeechInto.English));
        Assert.Equal("en", transcript.Language);
        Assert.Contains("meeting", transcript.ToText(), StringComparison.OrdinalIgnoreCase);
        var words = await Transcriber.ReadSpeechAsync(speech);
        Assert.Equal("en", await Transcriber.DetectLanguageAsync(words, model));
    }

    /// <summary>Windows' own voice saying a sentence, as a WAV file beside the test's others (null when it can't).</summary>
    private async Task<string?> SayAsync(string text)
    {
        var wav = Path.Combine(_dir, $"say-{Guid.NewGuid():N}.wav");
        var say = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "-NoProfile", "-Command", $"Add-Type -AssemblyName System.Speech; $s = New-Object System.Speech.Synthesis.SpeechSynthesizer; $s.SetOutputToWaveFile('{wav}'); $s.Speak('{text}'); $s.Dispose()" })
            say.ArgumentList.Add(a);
        using (var p = Process.Start(say)!)
            await p.WaitForExitAsync();
        return File.Exists(wav) ? wav : null;
    }

    [Fact]
    public void Text_is_translated_a_sentence_at_a_time_and_keeps_its_lines()
    {
        var asked = new List<string>();
        var result = Petal.App.Words.Translation.BySentence("One. Two? Three!\n\nهذا سطر. وآخر؟", sentences =>
        {
            asked.AddRange(sentences);
            return sentences.Select(s => s.Length == 0 ? "" : "<" + s + ">").ToList();
        });
        Assert.Equal(["One.", "Two?", "Three!", "", "هذا سطر.", "وآخر؟"], asked);
        Assert.Equal(string.Join(Environment.NewLine, "<One.> <Two?> <Three!>", "", "<هذا سطر.> <وآخر؟>"), result);
    }

    [Fact]
    public async Task A_card_steps_aside_to_the_taskbar_and_comes_back_when_the_work_is_done()
    {
        var started = new TaskCompletionSource<Dispatcher>();
        var thread = new Thread(() =>
        {
            started.SetResult(Dispatcher.CurrentDispatcher);
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        var dispatcher = await started.Task;
        try
        {
            await dispatcher.InvokeAsync(() =>
            {
                var card = HudCard.ShowAt(HudCard.CornerPoint(), "Adding captions", "lesson.mp4 · 40%");
                card.SetProgress(0.4);
                card.ToTaskbar();
                Assert.False(card.IsVisible);
                var standIn = Assert.IsAssignableFrom<System.Windows.Window>(card.TaskbarStandInWindow);
                Assert.True(standIn.ShowInTaskbar);
                Assert.Equal(System.Windows.WindowState.Minimized, standIn.WindowState);
                Assert.Equal(System.Windows.Shell.TaskbarItemProgressState.Normal, standIn.TaskbarItemInfo.ProgressState);
                Assert.Equal(0.4, standIn.TaskbarItemInfo.ProgressValue, 3);
                // The taskbar button follows the work.
                card.SetText("Adding captions", "lesson.mp4 · 70%");
                card.SetProgress(0.7);
                Assert.Equal(0.7, standIn.TaskbarItemInfo.ProgressValue, 3);
                Assert.Contains("70%", standIn.Title);
                // Done: the card is back where it was, its taskbar button gone.
                card.Succeed("Captions added", "1 of 1 saved", null);
                Assert.True(card.IsVisible);
                Assert.Null(card.TaskbarStandInWindow);
                Assert.False(standIn.IsVisible);
                card.Dismiss();

                // A click on the taskbar button brings a card back too.
                var other = HudCard.ShowAt(HudCard.CornerPoint(), "Converting", "song.wav · 10%");
                other.ToTaskbar();
                var button = other.TaskbarStandInWindow!;
                button.WindowState = System.Windows.WindowState.Normal;
                return other;
            }).Task.ContinueWith(async t =>
            {
                var other = t.Result;
                await dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
                await dispatcher.InvokeAsync(() =>
                {
                    Assert.True(other.IsVisible);
                    Assert.Null(other.TaskbarStandInWindow);
                    other.Dismiss();
                });
            }).Unwrap();
        }
        finally
        {
            dispatcher.InvokeShutdown();
        }
    }
}
