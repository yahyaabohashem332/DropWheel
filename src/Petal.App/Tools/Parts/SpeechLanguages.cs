using System.Windows.Controls;
using Petal.App.Localization;
using Petal.Core.Models;
using Petal.Engines.Media;
using Petal.Recipes.Translation;

namespace Petal.App.Tools.Parts;

/// <summary>A list of languages by their own names; <see cref="Value"/> is the code of the one chosen.</summary>
public sealed class LanguageChoice : ComboBox
{
    private bool _setting;

    public event Action<string>? Changed;

    /// <param name="first">Choices before the languages: a code (or "spoken", "auto") and its words.</param>
    /// <param name="only">Only these languages; all those translated when null.</param>
    public LanguageChoice(string initial, IEnumerable<(string Code, string Words)>? first = null, IReadOnlyCollection<string>? only = null)
    {
        // A class of its own doesn't pick up the app's look for combo boxes by itself.
        SetResourceReference(StyleProperty, typeof(ComboBox));
        MinWidth = 180;
        foreach (var (code, words) in first ?? [])
            Items.Add(new ComboBoxItem { Content = L.T(words), Tag = code });
        foreach (var language in Translator.Languages)
            if (only is null || only.Contains(language.Code))
                Items.Add(new ComboBoxItem { Content = language.NativeName, Tag = language.Code });
        Value = initial;
        SelectionChanged += (_, _) =>
        {
            if (!_setting && SelectedItem is ComboBoxItem { Tag: string code })
                Changed?.Invoke(code);
        };
    }

    public string Value
    {
        get => SelectedItem is ComboBoxItem { Tag: string code } ? code : "";
        set
        {
            _setting = true;
            SelectedItem = Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == value) ?? Items.OfType<ComboBoxItem>().FirstOrDefault();
            _setting = false;
        }
    }

    /// <summary>The languages as the words of a recording can be wanted: as spoken first.</summary>
    public static LanguageChoice ForWords(string initial = SpeechInto.Spoken) => new(initial, [(SpeechInto.Spoken, "As spoken")]);

    /// <summary>The language this interface speaks, when it is among those translated; English otherwise.</summary>
    public static string Interface => Translator.Languages.Any(l => l.Code == L.Code) ? L.Code : "en";

    /// <summary>What to remember of a choice: nothing when it is the interface's language, so the default goes on following the interface.</summary>
    public static string Remembered(string code) => code == Interface ? "" : code;

    /// <summary>A language's own name.</summary>
    public static string NameOf(string code) => Translator.Languages.FirstOrDefault(l => l.Code == code)?.NativeName ?? code;
}

/// <summary>
/// What words in another language need besides the speech model, in the tools that write speech down (Transcribe,
/// Animated Captions, Make Shorts): the translation models for the pair, and for a language no model reads, with the
/// Accurate model, which can't translate, a model that can.
/// </summary>
internal static class SpeechLanguages
{
    /// <summary>A Whisper model on this PC that can translate, the more accurate first; null when there is none.</summary>
    public static string? TranslatingModel() =>
        new[] { ModelCatalog.WhisperBalanced, ModelCatalog.WhisperFast }.Select(ModelStore.Find).FirstOrDefault(path => path is not null);

    /// <summary>
    /// The models the words need with these choices ("spoken" or a language; "auto" or the language spoken), beyond the
    /// speech model itself, whether a key is translating or not (the window shows both ways). Speech already in the
    /// language wanted needs none: it is written as spoken.
    /// </summary>
    public static IReadOnlyList<ModelInfo> Needed(ModelInfo speechModel, string language, string into)
    {
        if (into == SpeechInto.Spoken || into == language)
            return [];
        var needed = new List<ModelInfo>();
        bool known = Translator.Languages.Any(l => l.Code == language);
        if (known)
            needed.AddRange(Translator.ModelsFor(language, into));
        else if (into != "en")
            // The language isn't known yet, or no model reads it: through Whisper's English.
            needed.AddRange(Translator.ModelsFor("en", into));
        if (!known && language != "auto" && !Transcriber.CanTranslate(speechModel.FileName) && TranslatingModel() is null)
            needed.Add(ModelCatalog.WhisperBalanced);
        return needed;
    }

    public static string Into(string into) => into;
}
