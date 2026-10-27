using Petal.Core.Models;
using Petal.Core.Text;
using Petal.Recipes;
using Petal.Recipes.Translation;

namespace Petal.App.Words;

/// <summary>
/// Translating a piece of text on this PC: English into Arabic and Arabic into English with the translation
/// models (Opus-MT), sentence by sentence; with the AI model when the translation model isn't here but the AI
/// model is (measured on Arabic speech, Opus-MT's English is clearly the better). In Petal's other languages,
/// text goes into the language Petal speaks (or, already in it, into English), with the AI model.
/// </summary>
internal static class Translation
{
    /// <summary>Arabic text goes into English; anything else into Arabic.</summary>
    public static bool IntoEnglish(string text) => KeyboardLayout.LooksArabic(text);

    /// <summary>The interface language's English name when it is neither English nor Arabic; translations go into it.</summary>
    private static string? Into => Localization.L.Code switch
    {
        "tr" => "Turkish",
        "fa" => "Persian",
        "ur" => "Urdu",
        "id" => "Indonesian",
        "fr" => "French",
        "es" => "Spanish",
        _ => null,
    };

    /// <summary>The models a translation of this text needs, when they are not here yet; empty when it can run.</summary>
    public static IReadOnlyList<ModelInfo> Missing(string text)
    {
        if (Into is not null)
            return AskPetal.ModelReady ? [] : AskPetal.Models;
        if (IntoEnglish(text) ? Translator.EnglishReady : Translator.IsReady)
            return [];
        if (AskPetal.ModelReady)
            return [];
        return IntoEnglish(text) ? Translator.EnglishModels : Translator.Models;
    }

    public static async Task<string> RunAsync(string text, CancellationToken cancellationToken = default)
    {
        if (Into is { } language)
            return await AskPetal.WriteAsync(AskPetal.Instructions.TranslateInto(language), text, Math.Clamp(text.Length, 200, 1500), cancellationToken);
        bool english = IntoEnglish(text);
        if (english ? Translator.EnglishReady : Translator.IsReady)
            return await Task.Run(() => BySentence(text, sentences => english
                ? Translator.ToEnglish(sentences, null, cancellationToken)
                : Translator.ToArabic(sentences, null, cancellationToken)), cancellationToken);
        return await AskPetal.WriteAsync(AskPetal.Instructions.Translate(english), text, Math.Clamp(text.Length, 200, 1500), cancellationToken);
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
        var parts = System.Text.RegularExpressions.Regex.Split(line.Trim(), @"(?<=[.!?\u061F\u06D4])\s+").Where(s => s.Length > 0).ToList();
        return parts.Count > 0 ? parts : [""];
    }
}
