using System.Text.Json.Serialization;
using Petal.Core.Formats;
using Petal.Core.Jobs;

namespace Petal.Recipes;

/// <summary>One thing a recipe does. Steps run in order; each takes what the one before made.</summary>
public enum RecipeStepKind
{
    /// <summary>Into another format, with an optional size and quality (like a favourite).</summary>
    Convert,
    /// <summary>Pictures: longest side at most <see cref="RecipeStep.LongestSide"/> pixels.</summary>
    Resize,
    /// <summary>Pictures: quarter turns clockwise.</summary>
    Rotate,
    /// <summary>Smaller files: a preset, or what an app accepts (<see cref="RecipeStep.App"/>).</summary>
    Compress,
    /// <summary>Pictures: subject only, on transparency or a colour.</summary>
    RemoveBackground,
    /// <summary>Pictures, videos and PDFs: a text mark.</summary>
    Watermark,
    /// <summary>Without location, device and author details.</summary>
    Clean,
    /// <summary>All results named by a pattern ("{name}", "{n}", "{date}").</summary>
    Rename,
    /// <summary>All results packed into one ZIP.</summary>
    Zip,
    /// <summary>All pictures (or PDFs) as one PDF.</summary>
    CombinePdf,
    /// <summary>Scans and photos of pages: a PDF with an OCR text layer.</summary>
    Searchable,
}

/// <summary>A step's settings; each kind reads the ones it needs.</summary>
public sealed class RecipeStep
{
    public RecipeStepKind Kind { get; set; }

    /// <summary>Convert: the target's <see cref="FileFormat.Id"/>.</summary>
    public string? Format { get; set; }

    /// <summary>Convert, Resize: longest side in pixels (0 keeps the size).</summary>
    public int LongestSide { get; set; }

    /// <summary>Convert: as in <see cref="Favourite.Quality"/> (0 = Settings).</summary>
    public int Quality { get; set; }

    /// <summary>Compress: a <see cref="ShareTarget.Id"/>, or null for a preset.</summary>
    public string? App { get; set; }

    /// <summary>Compress: the Strong preset.</summary>
    public bool Strong { get; set; }

    /// <summary>RemoveBackground: "#RRGGBB", or null for transparency.</summary>
    public string? Background { get; set; }

    /// <summary>Watermark: the text.</summary>
    public string? Text { get; set; }

    public Place9 Place { get; set; } = Place9.BottomRight;

    /// <summary>Watermark: width as a fraction of the picture's.</summary>
    public double Size { get; set; } = 0.25;

    public double Opacity { get; set; } = 0.6;

    /// <summary>Watermark: "#RRGGBB".</summary>
    public string Color { get; set; } = "#FFFFFF";

    public bool Tiled { get; set; }

    /// <summary>Rotate: 90, 180 or 270.</summary>
    public int Turns { get; set; } = 90;

    /// <summary>Rename: e.g. "Trip {n}".</summary>
    public string? Pattern { get; set; }

    /// <summary>Zip, CombinePdf: the file's name; empty uses the recipe's.</summary>
    public string? Name { get; set; }

    public RecipeStep Copy() => (RecipeStep)MemberwiseClone();

    /// <summary>Steps that take the whole set at once rather than file by file.</summary>
    [JsonIgnore]
    public bool IsCollective => Kind is RecipeStepKind.Rename or RecipeStepKind.Zip or RecipeStepKind.CombinePdf;
}

/// <summary>A saved chain of steps: one petal that does several things, in order.</summary>
public sealed class Recipe
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = "";

    public List<RecipeStep> Steps { get; set; } = [];

    /// <summary>Show it on the tools wheel for the files it fits.</summary>
    public bool OnWheel { get; set; } = true;

    public Recipe Copy() => new() { Id = Id, Name = Name, OnWheel = OnWheel, Steps = Steps.Select(s => s.Copy()).ToList() };

    /// <summary>
    /// True when every step can take what the step before makes, starting from these formats.
    /// A recipe that converts pictures to PDF can't then resize them, for example.
    /// </summary>
    public bool AppliesTo(IReadOnlyCollection<FileFormat> sources, ConverterRegistry converters)
    {
        if (Steps.Count == 0 || sources.Count == 0)
            return false;
        var current = sources.ToList();
        foreach (var step in Steps)
        {
            if (!current.All(f => Accepts(step, f, converters)))
                return false;
            current = current.Select(f => After(step, f)).Distinct().ToList();
            if (step.Kind is RecipeStepKind.Zip)
                current = [Formats.Zip];
            else if (step.Kind is RecipeStepKind.CombinePdf)
                current = [Formats.Pdf];
        }
        return true;
    }

    internal static bool Accepts(RecipeStep step, FileFormat format, ConverterRegistry converters) => step.Kind switch
    {
        RecipeStepKind.Convert => step.Format is { } id && new Favourite { Format = id, LongestSide = step.LongestSide, Quality = step.Quality }.ConverterFor(format, converters) is not null,
        RecipeStepKind.Resize or RecipeStepKind.Rotate or RecipeStepKind.RemoveBackground => format.Family == FormatFamily.Image && format.CanWrite,
        RecipeStepKind.Compress => format.Family is FormatFamily.Image or FormatFamily.Audio or FormatFamily.Video or FormatFamily.Pdf,
        RecipeStepKind.Watermark => format.Family is FormatFamily.Image or FormatFamily.Video or FormatFamily.Pdf && format.CanWrite,
        RecipeStepKind.Clean => format.Family is FormatFamily.Image or FormatFamily.Audio or FormatFamily.Video or FormatFamily.Pdf
            || format == Formats.Docx || format == Formats.Xlsx || format == Formats.Pptx,
        RecipeStepKind.CombinePdf or RecipeStepKind.Searchable => format.Family == FormatFamily.Image && format.CanRead || format == Formats.Pdf,
        _ => true,
    };

    /// <summary>The format a file has after a step.</summary>
    internal static FileFormat After(RecipeStep step, FileFormat format) => step.Kind switch
    {
        RecipeStepKind.Convert when step.Format is { } id && Formats.FromId(id) is { } target => target,
        RecipeStepKind.Searchable => Formats.Pdf,
        RecipeStepKind.RemoveBackground when step.Background is null && format != Formats.Png && format != Formats.Webp => Formats.Png,
        _ => format,
    };
}
