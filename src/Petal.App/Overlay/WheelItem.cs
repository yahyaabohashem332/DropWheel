using System.Windows.Media;
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

/// <summary>
/// One petal. <see cref="Payload"/> is a <see cref="FileFormat"/>, <see cref="Favourite"/>, <see cref="ToolInfo"/>,
/// <see cref="FileActionInfo"/>, a recipe, a custom petal, a <see cref="Wheels.WheelEntry"/> of the user's wheels,
/// or a <see cref="WheelGroup"/> whose petals are <see cref="Children"/>.
/// </summary>
public sealed record WheelItem(string Label, string Name, string? Icon, object Payload)
{
    /// <summary>A picture drawn instead of a line icon: an app's own icon, an album cover.</summary>
    public ImageSource? Picture { get; init; }

    /// <summary>The petal's own colour, when the user gave it one.</summary>
    public Color? Tint { get; init; }

    /// <summary>For switches: on or off, shown on the petal and in the centre. Null for everything else.</summary>
    public bool? State { get; init; }

    /// <summary>Why the petal can't do its job yet ("Needs a 224 MB download"); it is drawn dim, with this in the centre.</summary>
    public string? Unavailable { get; init; }

    /// <summary>With <see cref="Unavailable"/>: choosing it only explains (true), or still opens, e.g. a window with the download (false).</summary>
    public bool Blocked { get; init; }

    /// <summary>The centre's second line instead of the kind of petal ("Convert", "Tool"…): what the choice does.</summary>
    public string? Note { get; init; }

    /// <summary>The petal usually chosen for this kind of file: marked with a small star.</summary>
    public bool Suggested { get; init; }

    /// <summary>A group's petals, opened around it.</summary>
    public IReadOnlyList<WheelItem>? Children { get; init; }

    public bool IsGroup => Children is not null;

    /// <summary>Who the petal is, for what the wheel remembers, colours and statistics: "format:webp", "tool:Compress".</summary>
    public string Key => KeyOf(Payload);

    public static string KeyOf(object payload) => payload switch
    {
        FileFormat f => "format:" + f.Id,
        Favourite f => "favourite:" + f.Id,
        ToolInfo t => "tool:" + t.Id,
        FileActionInfo a => "action:" + a.Id,
        Petal.Recipes.Recipe r => "recipe:" + r.Id,
        Petal.Recipes.CustomPetal p => "petal:" + System.IO.Path.GetFileNameWithoutExtension(p.File),
        Wheels.WheelEntry e => "entry:" + e.Id,
        WheelGroup g => "group:" + g.Id,
        Wheels.RunningWindow w => "window:" + w.Handle,
        Clips.ClipItem c => "clip:" + c.Id,
        Screen.Workspace w => "workspace:" + w.Id,
        Wheels.FolderDestination d => "folder:" + d.Id,
        _ => "other:" + payload,
    };

    public static WheelItem From(FileFormat format) => new(format.Label, L.F("Convert to {0}", format.Label), null, format)
    {
        Note = FormatNotes.For(format),
    };

    public static WheelItem From(Favourite favourite) => new(FavouriteText.Label(favourite), FavouriteText.Describe(favourite), "star", favourite);

    public static WheelItem From(Petal.Recipes.Recipe recipe) => new(recipe.Name, RecipeText.Describe(recipe), "workflow", recipe);

    public static WheelItem From(ToolInfo tool) => new(tool.WheelTitle, tool.Name, tool.Icon, tool);

    public static WheelItem From(Petal.Recipes.CustomPetal petal) => new(petal.DisplayName(L.IsArabic), petal.DisplayDescription(L.IsArabic) ?? petal.DisplayName(L.IsArabic),
        petal.Icon is { } icon && Visuals.Icons.Keys.Contains(icon) ? icon : "puzzle", petal);

    public static WheelItem From(FileActionInfo action) => new(action.WheelTitle, action.Name, action.Icon, action);

    /// <summary>A group of petals that opens around itself, such as "More" on the actions wheel.</summary>
    public static WheelItem Group(string id, string label, string name, string icon, IReadOnlyList<WheelItem> children) =>
        new(label, name, icon, new WheelGroup(id)) { Children = children };
}

/// <summary>A group petal's payload: its petals are on the <see cref="WheelItem"/>.</summary>
public sealed record WheelGroup(string Id);

/// <summary>What the wheel shows for a selection in one mode.</summary>
public sealed record WheelContent(IReadOnlyList<WheelItem> Items, string EmptyMessage)
{
    public bool IsEmpty => Items.Count == 0;
}
