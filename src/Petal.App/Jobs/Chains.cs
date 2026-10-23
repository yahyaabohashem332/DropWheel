using System.IO;
using Petal.App.Localization;
using Petal.App.Overlay;
using Petal.Core.Actions;
using Petal.Core.Formats;
using Petal.Core.Jobs;
using Petal.Core.Tools;
using Petal.Recipes;

namespace Petal.App.Jobs;

/// <summary>
/// Petals chosen one after another on the wheel, run as one recipe. A petal that is a recipe step
/// (a format, a favourite, a recipe, Compress, Remove Background, Make Searchable, Create PDF, Clean
/// for Sharing, Compress to ZIP) can be any step; any other petal can only come last, and gets what
/// the steps before it made.
/// </summary>
public static class Chains
{
    /// <summary>The recipe steps a petal stands for; null when it can only end a chain.</summary>
    public static IReadOnlyList<RecipeStep>? StepsFor(WheelItem item) => item.Payload switch
    {
        FileFormat format => [new RecipeStep { Kind = RecipeStepKind.Convert, Format = format.Id }],
        Favourite { Target: not null } favourite => [new RecipeStep { Kind = RecipeStepKind.Convert, Format = favourite.Format, LongestSide = favourite.LongestSide, Quality = favourite.Quality }],
        Recipe recipe when recipe.Steps.Count > 0 => recipe.Steps.Select(s => s.Copy()).ToList(),
        ToolInfo { Id: ToolId.Compress } => [new RecipeStep { Kind = RecipeStepKind.Compress }],
        ToolInfo { Id: ToolId.RemoveBackground } => [new RecipeStep { Kind = RecipeStepKind.RemoveBackground }],
        ToolInfo { Id: ToolId.MakeSearchable } => [new RecipeStep { Kind = RecipeStepKind.Searchable }],
        ToolInfo { Id: ToolId.CreatePdf or ToolId.MergePdfs } => [new RecipeStep { Kind = RecipeStepKind.CombinePdf }],
        FileActionInfo { Id: FileActionId.Clean } => [new RecipeStep { Kind = RecipeStepKind.Clean }],
        FileActionInfo { Id: FileActionId.Zip } => [new RecipeStep { Kind = RecipeStepKind.Zip }],
        _ => null,
    };

    /// <summary>Every step of the chain, in order.</summary>
    public static List<RecipeStep> Steps(IEnumerable<WheelItem> chain) => chain.SelectMany(i => StepsFor(i) ?? []).ToList();

    /// <summary>Why <paramref name="next"/> can't be the next step for these files; null when it can.</summary>
    public static string? Check(IReadOnlyList<WheelItem> chain, WheelItem next, IReadOnlyList<string> files, ConverterRegistry converters)
    {
        if (StepsFor(next) is not { } more)
            return "This can only be the last petal: drop on it";
        if (files.Any(Directory.Exists))
            return "Folders can't take steps";
        var formats = files.Select(Formats.FromPath).ToList();
        if (formats.Any(f => f is null))
            return "Petal doesn't know these files";
        var recipe = new Recipe { Steps = [.. Steps(chain), .. more] };
        return recipe.AppliesTo(formats!, converters) ? null : "It doesn't fit after the steps before";
    }

    /// <summary>
    /// The files' names as the steps will leave them ("photo.png" after WEBP is "photo.webp", anything
    /// after ZIP is one ".zip"), so the wheel offers what fits next. Nothing is made.
    /// </summary>
    public static IReadOnlyList<string> View(IReadOnlyList<string> files, IReadOnlyList<WheelItem> chain)
    {
        var steps = Steps(chain);
        var current = files.Select(f => (Path: f, Format: Formats.FromPath(f))).ToList();
        foreach (var step in steps)
        {
            if (step.Kind is RecipeStepKind.Zip or RecipeStepKind.CombinePdf && current.Count > 0)
            {
                var one = step.Kind == RecipeStepKind.Zip ? Formats.Zip : Formats.Pdf;
                current = [(Path.ChangeExtension(current[0].Path, one.WriteExtension), one)];
                continue;
            }
            current = current.Select(c => c.Format is { } format && Recipe.After(step, format) is var after && after != format
                ? (Path.ChangeExtension(c.Path, after.WriteExtension), after)
                : c).ToList();
        }
        return current.Select(c => c.Path).ToList();
    }

    /// <summary>A short name for a petal in the chain's title: a format's label, otherwise its name.</summary>
    public static string Name(WheelItem item) =>
        item.Payload is FileFormat or Favourite or Recipe ? item.Label : L.T(item.Name);
}
