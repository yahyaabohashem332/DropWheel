using Petal.Core.Actions;
using Petal.Core.Formats;
using Petal.Core.Jobs;
using Petal.Core.Tools;
using Petal.App.Jobs;
using Petal.App.Localization;

namespace Petal.App.Overlay;

public enum WheelMode
{
    Conversions,
    Tools,
    Actions,
}

/// <summary>One petal. <see cref="Payload"/> is a <see cref="FileFormat"/>, <see cref="Favourite"/>, <see cref="ToolInfo"/> or <see cref="FileActionInfo"/>.</summary>
public sealed record WheelItem(string Label, string Name, string? Icon, object Payload)
{
    public static WheelItem From(FileFormat format) => new(format.Label, L.F("Convert to {0}", format.Label), null, format);

    public static WheelItem From(Favourite favourite) => new(FavouriteText.Label(favourite), FavouriteText.Describe(favourite), "star", favourite);

    public static WheelItem From(Petal.Recipes.Recipe recipe) => new(recipe.Name, RecipeText.Describe(recipe), "workflow", recipe);

    public static WheelItem From(ToolInfo tool) => new(tool.WheelTitle, tool.Name, tool.Icon, tool);

    public static WheelItem From(Petal.Recipes.CustomPetal petal) => new(petal.DisplayName(L.IsArabic), petal.DisplayDescription(L.IsArabic) ?? petal.DisplayName(L.IsArabic),
        petal.Icon is { } icon && Visuals.Icons.Keys.Contains(icon) ? icon : "puzzle", petal);

    public static WheelItem From(FileActionInfo action) => new(action.WheelTitle, action.Name, action.Icon, action);
}

/// <summary>What the wheel shows for a selection in one mode.</summary>
public sealed record WheelContent(IReadOnlyList<WheelItem> Items, string EmptyMessage)
{
    public bool IsEmpty => Items.Count == 0;
}
