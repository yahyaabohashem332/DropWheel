namespace Petal.Engines.Documents.Model;

/// <summary>
/// Format-neutral document: every reader produces one and every writer consumes one,
/// so N formats need N readers and N writers instead of N² converters.
/// </summary>
internal sealed class DocModel
{
    public DocMetadata Metadata { get; } = new();

    public List<Block> Blocks { get; } = [];
}

internal sealed class DocMetadata
{
    public string? Title { get; set; }
    public string? Author { get; set; }
    public string? Subject { get; set; }
    public string? Keywords { get; set; }
    public string? Language { get; set; }
}

internal abstract class Block;

internal sealed class Heading(int level) : Block
{
    public int Level { get; set; } = Math.Clamp(level, 1, 6);
    public List<Inline> Inlines { get; } = [];
}

internal enum Alignment
{
    Start,
    Center,
    End,
    Justify,
}

internal sealed class Paragraph : Block
{
    public List<Inline> Inlines { get; } = [];
    public Alignment Alignment { get; set; }
}

internal sealed class ListBlock(bool ordered) : Block
{
    public bool Ordered { get; set; } = ordered;
    public int Start { get; set; } = 1;
    public List<ListItem> Items { get; } = [];
}

internal sealed class ListItem
{
    public List<Block> Blocks { get; } = [];
}

internal sealed class Table : Block
{
    public List<TableRow> Rows { get; } = [];

    public int ColumnCount => Rows.Count == 0 ? 0 : Rows.Max(r => r.Cells.Sum(c => Math.Max(1, c.ColSpan)));
}

internal sealed class TableRow
{
    public List<TableCell> Cells { get; } = [];
    public bool IsHeader { get; set; }
}

internal sealed class TableCell
{
    public List<Block> Blocks { get; } = [];
    public int ColSpan { get; set; } = 1;
    public int RowSpan { get; set; } = 1;
}

internal sealed class ImageBlock(ImageData image) : Block
{
    public ImageData Image { get; } = image;
}

internal sealed class CodeBlock(string text) : Block
{
    public string Text { get; set; } = text;
    public string? Language { get; set; }
}

internal sealed class Quote : Block
{
    public List<Block> Blocks { get; } = [];
}

internal sealed class HorizontalRule : Block;

internal sealed class PageBreak : Block;

internal abstract class Inline;

[Flags]
internal enum TextStyle
{
    None = 0,
    Bold = 1,
    Italic = 2,
    Underline = 4,
    Strike = 8,
    Code = 16,
    Superscript = 32,
    Subscript = 64,
}

internal sealed class TextRun(string text, TextStyle style = TextStyle.None, string? link = null) : Inline
{
    public string Text { get; set; } = text;
    public TextStyle Style { get; set; } = style;
    public string? Link { get; set; } = link;

    public bool Has(TextStyle s) => (Style & s) == s;
}

internal sealed class LineBreak : Inline;

internal sealed class InlineImage(ImageData image) : Inline
{
    public ImageData Image { get; } = image;
}

/// <summary>An embedded picture, always PNG or JPEG after <see cref="ImageData.Create"/>.</summary>
internal sealed class ImageData
{
    private ImageData(byte[] bytes, string mediaType, int width, int height)
    {
        Bytes = bytes;
        MediaType = mediaType;
        PixelWidth = width;
        PixelHeight = height;
    }

    public byte[] Bytes { get; }
    public string MediaType { get; }
    public int PixelWidth { get; }
    public int PixelHeight { get; }

    /// <summary>Display size in points when the source specified one.</summary>
    public double? WidthPt { get; set; }
    public double? HeightPt { get; set; }

    public string? Alt { get; set; }

    public string Extension => MediaType == "image/jpeg" ? "jpg" : "png";

    public bool IsJpeg => MediaType == "image/jpeg";

    /// <summary>Normalises any decodable picture to PNG or JPEG; null when it cannot be decoded.</summary>
    public static ImageData? Create(byte[] bytes, string? alt = null)
    {
        var normalized = Util.ImageTools.Normalize(bytes);
        if (normalized is null)
            return null;
        return new ImageData(normalized.Value.Bytes, normalized.Value.MediaType, normalized.Value.Width, normalized.Value.Height) { Alt = alt };
    }

    /// <summary>Display size in points, scaled down to fit <paramref name="maxWidth"/> × <paramref name="maxHeight"/>.</summary>
    public (double Width, double Height) FitSize(double maxWidth, double maxHeight)
    {
        double w = WidthPt ?? PixelWidth * 0.75;
        double h = HeightPt ?? PixelHeight * 0.75;
        if (w <= 0 || h <= 0)
            (w, h) = (100, 100);
        var scale = Math.Min(1, Math.Min(maxWidth / w, maxHeight / h));
        return (w * scale, h * scale);
    }
}
