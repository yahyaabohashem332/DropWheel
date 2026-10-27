using Petal.Core.Models;
using Petal.Engines.Media;
using Petal.Recipes.Translation;

namespace Petal.App.Tools.Parts;

/// <summary>
/// What words in another language need besides the speech model, in the tools that write speech down (Transcribe,
/// Animated Captions): Opus-MT into Arabic and from Arabic, and for another language with the Accurate model, which
/// can't translate, a model that can.
/// </summary>
internal static class SpeechLanguages
{
    /// <summary>A Whisper model on this PC that can translate, the more accurate first; null when there is none.</summary>
    public static string? TranslatingModel() =>
        new[] { ModelCatalog.WhisperBalanced, ModelCatalog.WhisperFast }.Select(ModelStore.Find).FirstOrDefault(path => path is not null);

    /// <summary>
    /// The models the words need with these choices ("spoken", "en" or "ar"; "auto" or the language spoken), beyond the
    /// speech model itself. Arabic speech wanted in Arabic needs none: it is written as spoken.
    /// </summary>
    public static IReadOnlyList<ModelInfo> Needed(ModelInfo speechModel, string language, string into)
    {
        var needed = new List<ModelInfo>();
        if (into == "ar" && language != "ar")
            needed.AddRange(Translator.Models);
        if (into == "en" && language is "ar" or "auto")
            needed.AddRange(Translator.EnglishModels);
        // Another language goes into English with Whisper's own translation.
        if (into != "spoken" && language is not ("ar" or "en" or "auto") && !Transcriber.CanTranslate(speechModel.FileName) && TranslatingModel() is null)
            needed.Add(ModelCatalog.WhisperBalanced);
        return needed;
    }

    public static SpeechInto Into(string into) => into switch
    {
        "en" => SpeechInto.English,
        "ar" => SpeechInto.Arabic,
        _ => SpeechInto.Spoken,
    };
}
