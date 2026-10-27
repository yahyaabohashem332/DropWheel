using Petal.Engines.Media;

namespace Petal.Recipes.Translation;

/// <summary>The language the words of a recording are wanted in.</summary>
public enum SpeechInto
{
    /// <summary>The language spoken.</summary>
    Spoken,
    English,
    Arabic,
}

/// <param name="Model">The Whisper model chosen (Fast, Balanced or Accurate).</param>
/// <param name="Language">"auto", or the language spoken ("ar").</param>
/// <param name="TranslatingModel">
/// A Whisper model trained to translate, for English from a language Opus-MT doesn't read when <paramref name="Model"/>
/// can't translate (see <see cref="Transcriber.CanTranslate"/>).
/// </param>
public sealed record SpeechTextOptions(string Model, string Language, SpeechInto Into, string? TranslatingModel = null);

/// <summary>
/// The words of a recording, with their times, in the language wanted. Whisper writes what is said with the model
/// chosen; when another language is wanted, the language spoken is told first (from the first half minute, unless
/// it was given), and then:
/// <list type="bullet">
/// <item>already the language wanted (an Arabic lesson, in Arabic): written as spoken, nothing translated;</item>
/// <item>Arabic into English: written in Arabic, then translated by Opus-MT Arabic → English, sentence by sentence;</item>
/// <item>English into Arabic: written in English, then translated by Opus-MT English → Arabic;</item>
/// <item>another language: Whisper's own translation into English, by a model trained for it, and from there into
/// Arabic when Arabic is wanted.</item>
/// </list>
/// The times are always those Whisper heard in the speech itself.
/// </summary>
public static class SpeechText
{
    public static async Task<Transcript> TranscribeAsync(string input, SpeechTextOptions options, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var samples = await Transcriber.ReadSpeechAsync(input, cancellationToken).ConfigureAwait(false);
        progress?.Report(0.03);
        var language = string.IsNullOrEmpty(options.Language) ? "auto" : options.Language;
        if (options.Into != SpeechInto.Spoken && language == "auto")
            language = await Transcriber.DetectLanguageAsync(samples, options.Model, cancellationToken).ConfigureAwait(false) ?? "auto";
        string wanted = options.Into switch { SpeechInto.English => "en", SpeechInto.Arabic => "ar", _ => language };
        if (options.Into == SpeechInto.Spoken || wanted == language)
            return await Transcriber.TranscribeAsync(samples, new TranscribeOptions(options.Model, language), Part(progress, 0.06, 1), cancellationToken).ConfigureAwait(false);

        var write = Part(progress, 0.06, 0.85);
        var translate = Part(progress, 0.85, 1);
        Transcript english;
        if (language == "en")
            english = await Transcriber.TranscribeAsync(samples, new TranscribeOptions(options.Model, "en"), write, cancellationToken).ConfigureAwait(false);
        else if (language == "ar" && Translator.EnglishReady)
        {
            var arabic = await Transcriber.TranscribeAsync(samples, new TranscribeOptions(options.Model, "ar"), write, cancellationToken).ConfigureAwait(false);
            english = await TranslateAsync(arabic, "en", translate, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var model = Transcriber.CanTranslate(options.Model) ? options.Model
                : options.TranslatingModel ?? throw new InvalidOperationException(NoTranslatingModel);
            english = await Transcriber.TranscribeAsync(samples, new TranscribeOptions(model, language, TranslateToEnglish: true), write, cancellationToken).ConfigureAwait(false);
        }
        return wanted == "en" ? english : await TranslateAsync(english, "ar", translate, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Why English couldn't be had (shown on the card, translated).</summary>
    public const string NoTranslatingModel = "The Accurate model writes what is said but can't translate it. Download the Balanced model too, and it puts the words into English.";

    /// <summary>The same segments and times, each one's words translated.</summary>
    private static async Task<Transcript> TranslateAsync(Transcript transcript, string into, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var lines = transcript.Segments.Select(s => s.Text).ToList();
        var translated = await Task.Run(() => into == "ar"
            ? Translator.ToArabic(lines, progress, cancellationToken)
            : Translator.ToEnglish(lines, progress, cancellationToken), cancellationToken).ConfigureAwait(false);
        return new Transcript(transcript.Segments.Select((s, i) => s with { Text = translated[i] }).ToList(), into);
    }

    private static IProgress<double>? Part(IProgress<double>? progress, double from, double to) =>
        progress is null ? null : new PartProgress(progress, from, to);

    private sealed class PartProgress(IProgress<double> inner, double from, double to) : IProgress<double>
    {
        public void Report(double value) => inner.Report(from + (to - from) * Math.Clamp(value, 0, 1));
    }
}
