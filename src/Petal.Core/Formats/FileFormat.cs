namespace Petal.Core.Formats;

public enum FormatFamily
{
    Image,
    /// <summary>Pictures Petal reads but never writes: SVG, Photoshop, camera RAW, Illustrator and EPS.</summary>
    Vector,
    Audio,
    Video,
    Pdf,
    Document,
    Text,
    Ebook,
    Slides,
    Spreadsheet,
    Subtitle,
    Archive,
    /// <summary>Design and video projects (InDesign, XD, Premiere, After Effects): information and tools, no conversions.</summary>
    Project,
    /// <summary>Colour swatches (Adobe Swatch Exchange and Photoshop swatches).</summary>
    Palette,
}

/// <summary>A file format Petal knows how to read and/or write.</summary>
/// <param name="Id">Stable lower-case identifier, also the default extension.</param>
/// <param name="Label">Short upper-case label shown on the wheel.</param>
/// <param name="Extensions">Recognised extensions without the dot, the first one is used when writing.</param>
public sealed record FileFormat(string Id, string Label, FormatFamily Family, string[] Extensions, bool CanRead = true, bool CanWrite = true)
{
    public string WriteExtension => Extensions[0];

    public override string ToString() => Label;
}
