namespace Petal.Core.Formats;

public enum FormatFamily
{
    Image,
    /// <summary>Pictures Petal reads but never writes as pictures: SVG (written only from drawings), Photoshop, camera RAW, Illustrator and EPS.</summary>
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
    /// <summary>Fonts, on the desktop and for the web.</summary>
    Font,
    /// <summary>Structured data (JSON, JSON Lines, YAML, TOML, XML) and contacts (vCard).</summary>
    Data,
    /// <summary>3D models: for printing (STL, 3MF), from 3D programs (OBJ, PLY), for the web and AR (GLB, glTF).</summary>
    Model,
    /// <summary>CAD drawings: DXF, and DWG read.</summary>
    Drawing,
}

/// <summary>A file format Petal knows how to read and/or write.</summary>
/// <param name="Id">Stable lower-case identifier, also the default extension.</param>
/// <param name="Label">Short upper-case label shown on the wheel.</param>
/// <param name="Extensions">Recognised extensions without the dot, the first one is used when writing.</param>
/// <param name="Writes">
/// For a target that is a way of writing another format (a PDF as one long PNG), the extension it writes;
/// such a format has no extensions of its own, so a file on disk is never taken for it.
/// </param>
public sealed record FileFormat(string Id, string Label, FormatFamily Family, string[] Extensions, bool CanRead = true, bool CanWrite = true, string? Writes = null)
{
    public string WriteExtension => Writes ?? Extensions[0];

    public override string ToString() => Label;
}
