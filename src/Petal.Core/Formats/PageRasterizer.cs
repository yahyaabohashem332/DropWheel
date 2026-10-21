namespace Petal.Core.Formats;

/// <summary>
/// Renders one page of a PDF-based file (an Illustrator file saved with PDF content) as PNG
/// bytes. The documents engine, which has PDFium, fills this in at start-up; the image engine
/// uses it to read Illustrator files like any other picture.
/// </summary>
public static class PageRasterizer
{
    /// <summary>(path, page index, dots per inch, transparent background) → PNG; null until a PDF engine registers.</summary>
    public static Func<string, int, int, bool, byte[]>? Render { get; set; }
}
