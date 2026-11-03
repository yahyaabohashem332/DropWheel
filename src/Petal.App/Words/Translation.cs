using Petal.App.Localization;
using Petal.App.Settings;
using Petal.Core.Models;
using Petal.Core.Text;
using Petal.Recipes;
using Petal.Recipes.Cloud;
using Petal.Recipes.Translation;

namespace Petal.App.Words;

/// <summary>
/// Translating a piece of text: into the language chosen, or the one the interface speaks (and text already in it into
/// another). With the cloud model chosen when there is one; on this PC with the translation models, sentence by
/// sentence; or with the AI model when they aren't here but it is.
/// </summary>
internal static class Translation
{
    public static string From(string text) => LanguageGuess.Of(text) ?? "en";

    /// <summary>The language a text goes into when nothing else was chosen.</summary>
    public static string Into(string text)
    {
        var settings = SettingsStore.Current;
        string wanted = settings.TranslateInto is { Length: > 0 } chosen ? chosen : Tools.Parts.LanguageChoice.Interface;
        if (From(text) != wanted)
            return wanted;
        return wanted == "en" ? Second() : "en";
    }

    /// <summary>Another language this person writes, for English text under an English interface.</summary>
    private static string Second()
    {
        try
        {
            foreach (var language in System.Windows.Input.InputLanguageManager.Current.AvailableInputLanguages ?? Array.Empty<object>())
                if (language is System.Globalization.CultureInfo { TwoLetterISOLanguageName: var code } && code != "en" && Translator.Languages.Any(l => l.Code == code))
                    return code;
        }
        catch (InvalidOperationException)
        {
        }
        return "es";
    }

    /// <summary>The models this translation needs, when they are not here yet; empty when it can run.</summary>
    public static IReadOnlyList<ModelInfo> Missing(string text, string? into = null)
    {
        into ??= Into(text);
        string from = From(text);
        if (from == into || CloudAi.CanTranslate || AskPetal.ModelReady)
            return [];
        return Translator.ModelsFor(from, into).Where(m => !ModelStore.IsAvailable(m)).ToList();
    }

    public static async Task<string> RunAsync(string text, CancellationToken cancellationToken = default, string? into = null)
    {
        into ??= Into(text);
        string from = From(text);
        if (from == into)
            return text;
        if (CloudAi.CanTranslate)
            return await CloudAi.WriteAsync(CloudAi.Translating!, AskPetal.Instructions.TranslateInto(Translator.EnglishName(into)), text, cancellationToken: cancellationToken);
        if (Translator.ModelsFor(from, into).All(ModelStore.IsAvailable))
            return await Task.Run(() => BySentence(text, sentences => Translator.Lines(sentences, from, into, null, cancellationToken)), cancellationToken);
        return await AskPetal.WriteAsync(AskPetal.Instructions.TranslateInto(Translator.EnglishName(into)), text, Math.Clamp(text.Length, 200, 1500), cancellationToken);
    }

    /// <summary>
    /// Each line translated a sentence at a time (the translation model reads a few hundred pieces at most, and a
    /// sentence is what it was made for), the lines kept as they were.
    /// </summary>
    internal static string BySentence(string text, Func<IReadOnlyList<string>, IReadOnlyList<string>> translate)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n').Select(Sentences).ToList();
        var translated = translate(lines.SelectMany(l => l).ToList());
        var result = new List<string>(lines.Count);
        int at = 0;
        foreach (var line in lines)
        {
            result.Add(string.Join(' ', translated.Skip(at).Take(line.Count).Where(s => s.Length > 0)));
            at += line.Count;
        }
        return string.Join(Environment.NewLine, result);
    }

    /// <summary>A line's sentences: they end at a full stop, a question or an exclamation mark followed by a space.</summary>
    internal static List<string> Sentences(string line)
    {
        var parts = System.Text.RegularExpressions.Regex.Split(line.Trim(), @"(?<=[.!?؟۔。])\s+").Where(s => s.Length > 0).ToList();
        return parts.Count > 0 ? parts : [""];
    }

    /// <summary>"Translating on this PC…", or which cloud model it is going to, so nobody is told the wrong thing.</summary>
    public static string Working() =>
        CloudAi.CanTranslate ? L.F("Translating with {0}, on your own key…", CloudAi.Name(CloudAi.Translating!.Provider)) : L.T("Translating on this PC…");
}
