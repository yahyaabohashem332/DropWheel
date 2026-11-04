using Petal.Engines.Media;

namespace Petal.Recipes.Translation;

/// <summary>The language the words of a recording are wanted in: "spoken", or a language's code.</summary>
public static class SpeechInto
{
    /// <summary>The language spoken.</summary>
    public const string Spoken = "spoken";

    public const string English = "en";

    public const string Arabic = "ar";
}

/// <param name="Model">The Whisper model chosen (Fast, Balanced or Accurate).</param>
/// <param name="Language">"auto", or the language spoken ("ar").</param>
/// <param name="Into"><see cref="SpeechInto.Spoken"/>, or the code of the language wanted.</param>
public sealed record SpeechTextOptions(string Model, string Language, string Into);

/// <summary>
/// The words of a recording, with their times, in the language wanted. Whisper writes what is said with the model
/// chosen; when another language is wanted, the language spoken is told first (from the first half minute, unless
/// it was given), and then:
/// <list type="bullet">
/// <item>already the language wanted: written as spoken, nothing translated;</item>
/// <item>a translation from that language is ready: written as spoken, then translated sentence by sentence;</item>
/// <item>otherwise: Whisper's own translation into English, by a model trained for it, and from there into the
/// language wanted.</item>
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
        string wanted = options.Into == SpeechInto.Spoken ? language : options.Into;
        if (options.Into == SpeechInto.Spoken || wanted == language)
            return await Transcriber.TranscribeAsync(samples, new TranscribeOptions(options.Model, language), Part(progress, 0.06, 1), cancellationToken).ConfigureAwait(false);

        if (!Translator.ReadyFor(language, wanted))
            throw new InvalidOperationException(NoTranslationModel);
        // Written down first and then translated by meaning. Whisper's own translating is never used: it hears a sentence and
        // makes one up, the same one again and again where it isn't sure.
        var spoken = await Transcriber.TranscribeAsync(samples, new TranscribeOptions(options.Model, language), Part(progress, 0.06, 0.85), cancellationToken).ConfigureAwait(false);
        return await TranslateAsync(spoken, language, wanted, Part(progress, 0.85, 1), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Why the words couldn't be translated (shown on the card, translated).</summary>
    public const string NoTranslationModel = "The translation model isn't downloaded yet. Download it or use your key.";

    /// <summary>The same segments and times, each one's words translated.</summary>
    private static async Task<Transcript> TranslateAsync(Transcript transcript, string from, string into, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var lines = transcript.Segments.Select(s => s.Text).ToList();
        var translated = await Translator.TranslateAsync(lines, from, into, progress, cancellationToken).ConfigureAwait(false);
        // The words' own times belong to the words spoken, not to their translation.
        return new Transcript(transcript.Segments.Select((s, i) => s with { Text = translated[i], Words = [] }).ToList(), into);
    }

    private static IProgress<double>? Part(IProgress<double>? progress, double from, double to) =>
        progress is null ? null : new PartProgress(progress, from, to);

    private sealed class PartProgress(IProgress<double> inner, double from, double to) : IProgress<double>
    {
        public void Report(double value) => inner.Report(from + (to - from) * Math.Clamp(value, 0, 1));
    }
}
