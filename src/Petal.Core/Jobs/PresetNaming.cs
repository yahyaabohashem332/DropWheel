using System.Globalization;
using System.Text.RegularExpressions;

namespace Petal.Core.Jobs;

/// <summary>
/// Names for a favourite's results (<see cref="Favourite.NamePattern"/>): "{name}" is the original's name,
/// "{date}" today's date (2026-11-23), "{preset}" the favourite's own name; anything else is kept as written.
/// </summary>
public static partial class PresetNaming
{
    public const string Name = "{name}", Date = "{date}", Preset = "{preset}";

    /// <summary>The patterns the favourite window offers, the first being DropWheel's usual name.</summary>
    public static IReadOnlyList<string> Offered { get; } = ["", $"{Name} {Date}", $"{Date} {Name}", $"{Name} {Preset}"];

    /// <summary>Longest pattern kept; longer ones (from a shared file) are cut.</summary>
    public const int MostLength = 80;

    /// <summary>
    /// The result's name without its extension, or null for DropWheel's usual name (no pattern, or one that
    /// leaves nothing a file can be called).
    /// </summary>
    public static string? Stem(string? pattern, string inputPath, string presetLabel, DateTime today)
    {
        if (string.IsNullOrWhiteSpace(pattern))
            return null;
        var text = Words().Replace(pattern, m => m.Value.ToLowerInvariant() switch
        {
            Name => Formats.Formats.BaseName(inputPath),
            Date => today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            Preset => presetLabel,
            _ => m.Value,
        });
        return Clean(text);
    }

    /// <summary>A pattern from a shared file made safe: no characters a file name can't hold, not too long.</summary>
    public static string? Tidy(string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
            return null;
        var text = pattern.Trim();
        if (text.Length > MostLength)
            text = text[..MostLength];
        foreach (var c in Path.GetInvalidFileNameChars())
            text = text.Replace(c.ToString(), "");
        text = Spaces().Replace(text, " ").Trim();
        return text.Length == 0 ? null : text;
    }

    private static string? Clean(string text)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            text = text.Replace(c, '-');
        text = Spaces().Replace(text, " ").Trim().TrimEnd('.').Trim();
        if (text.Length > 150)
            text = text[..150].Trim();
        return text.Length == 0 ? null : text;
    }

    [GeneratedRegex(@"\{[A-Za-z]+\}")]
    private static partial Regex Words();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
