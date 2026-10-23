using Petal.App.Localization;
using Petal.Core.Formats;
using Petal.Core.Jobs;
using Petal.Recipes;

namespace Petal.App.Jobs;

/// <summary>How recipes and their steps are described on the wheel and in Settings.</summary>
public static class RecipeText
{
    public static readonly (RecipeStepKind Kind, string Label)[] Kinds =
    [
        (RecipeStepKind.Convert, "Convert"),
        (RecipeStepKind.Resize, "Resize"),
        (RecipeStepKind.Rotate, "Rotate"),
        (RecipeStepKind.Compress, "Compress"),
        (RecipeStepKind.RemoveBackground, "Remove Background"),
        (RecipeStepKind.Watermark, "Add Watermark"),
        (RecipeStepKind.Clean, "Clean for Sharing"),
        (RecipeStepKind.Rename, "Rename"),
        (RecipeStepKind.Zip, "Compress to ZIP"),
        (RecipeStepKind.CombinePdf, "Combine into one PDF"),
        (RecipeStepKind.Searchable, "Make Searchable"),
    ];

    public static string KindName(RecipeStepKind kind) => L.T(Kinds.First(k => k.Kind == kind).Label);

    /// <summary>One step in a few words: "WEBP · 1600 px", "Resize to 1080 px", "WhatsApp".</summary>
    public static string Describe(RecipeStep step) => step.Kind switch
    {
        RecipeStepKind.Convert => FavouriteText.Describe(new Favourite { Format = step.Format ?? "", LongestSide = step.LongestSide, Quality = step.Quality }),
        RecipeStepKind.Resize => step.LongestSide > 0 ? L.F("Resize to {0}", L.F("{0} px", step.LongestSide)) : L.T("Resize"),
        RecipeStepKind.Rotate => L.F("Rotate {0}°", step.Turns),
        RecipeStepKind.Compress => step.App is { } app && ShareTargets.Find(app) is { } target
            ? L.F("Compress for {0}", L.T(target.Name))
            : L.T(step.Strong ? "Compress (strong)" : "Compress"),
        RecipeStepKind.RemoveBackground => step.Background is { Length: > 0 } ? L.T("Remove Background, on a colour") : L.T("Remove Background"),
        RecipeStepKind.Watermark => L.F("Watermark “{0}”", step.Text ?? ""),
        RecipeStepKind.Rename => L.F("Rename to “{0}”", step.Pattern ?? "{name}"),
        _ => KindName(step.Kind),
    };

    /// <summary>The whole recipe: "Remove Background → Resize to 1080 px → WEBP".</summary>
    public static string Describe(Recipe recipe) =>
        recipe.Steps.Count == 0 ? L.T("No steps yet") : string.Join(L.IsRightToLeft ? " ← " : " → ", recipe.Steps.Select(Describe));

    /// <summary>What a recipe can start from, for the editor's hint.</summary>
    public static string Accepts(Recipe recipe)
    {
        if (recipe.Steps.Count == 0)
            return "";
        var first = recipe.Steps[0];
        return first.Kind switch
        {
            RecipeStepKind.Resize or RecipeStepKind.Rotate or RecipeStepKind.RemoveBackground => L.T("It works on pictures."),
            RecipeStepKind.Watermark => L.T("It works on pictures, videos and PDFs."),
            RecipeStepKind.Compress => L.T("It works on pictures, audio, videos and PDFs."),
            RecipeStepKind.CombinePdf or RecipeStepKind.Searchable => L.T("It works on pictures and PDFs."),
            RecipeStepKind.Convert when Formats.FromId(first.Format ?? "") is { } target => L.F("It works on files that convert to {0}.", target.Label),
            _ => L.T("It works on any file."),
        };
    }
}
