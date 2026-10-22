using Petal.Core.Models;
using Petal.Core.Text;
using Petal.Recipes;
using Petal.Recipes.Translation;

namespace Petal.App.Words;

/// <summary>
/// Translating a piece of text on this PC: English into Arabic with the translation model, and other
/// directions (Arabic into English and the rest) with the AI model.
/// </summary>
internal static class Translation
{
    /// <summary>Arabic text goes into English; anything else into Arabic.</summary>
    public static bool IntoEnglish(string text) => KeyboardLayout.LooksArabic(text);

    /// <summary>The models a translation of this text needs, when they are not here yet; empty when it can run.</summary>
    public static IReadOnlyList<ModelInfo> Missing(string text)
    {
        if (!IntoEnglish(text) && Translator.IsReady)
            return [];
        if (AskPetal.ModelReady)
            return [];
        return IntoEnglish(text) ? AskPetal.Models : Translator.Models;
    }

    public static async Task<string> RunAsync(string text, CancellationToken cancellationToken = default)
    {
        if (!IntoEnglish(text) && Translator.IsReady)
        {
            var lines = text.Replace("\r\n", "\n").Split('\n');
            var arabic = await Task.Run(() => Translator.ToArabic(lines, null, cancellationToken), cancellationToken);
            return string.Join(Environment.NewLine, arabic);
        }
        return await AskPetal.WriteAsync(AskPetal.Instructions.Translate(IntoEnglish(text)), text, Math.Clamp(text.Length, 200, 1500), cancellationToken);
    }
}
