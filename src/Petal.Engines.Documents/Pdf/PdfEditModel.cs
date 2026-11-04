using PdfSharp.Pdf;

namespace Petal.Engines.Documents.Pdf;

/// <summary>
/// A rectangle on a page as it is displayed: points from its top-left corner, x to the right and y down, whatever the
/// page's /Rotate and crop box. Everything the editor is told and tells is in these.
/// </summary>
public readonly record struct PdfArea(double Left, double Top, double Width, double Height)
{
    public double Right => Left + Width;

    public double Bottom => Top + Height;

    public static PdfArea Between(double x1, double y1, double x2, double y2) =>
        new(Math.Min(x1, x2), Math.Min(y1, y2), Math.Abs(x2 - x1), Math.Abs(y2 - y1));

    public bool Contains(double x, double y) => x >= Left && x <= Right && y >= Top && y <= Bottom;

    public bool Overlaps(PdfArea other) => other.Left < Right && Left < other.Right && other.Top < Bottom && Top < other.Bottom;

    public PdfArea Inflated(double by) => new(Left - by, Top - by, Width + 2 * by, Height + 2 * by);

    public PdfArea Union(PdfArea other) => Between(Math.Min(Left, other.Left), Math.Min(Top, other.Top), Math.Max(Right, other.Right), Math.Max(Bottom, other.Bottom));

    public PdfArea Moved(double x, double y) => this with { Left = Left + x, Top = Top + y };
}

/// <summary>One thing the person put on a page. Colours are 0xRRGGBB.</summary>
public abstract record PdfPageItem(int Page);

/// <summary>Text typed anywhere: real text on the page, shaped and right to left where its letters are.</summary>
public sealed record PdfTextItem(int Page, PdfArea Area, string Text, double Size, int Colour, bool Bold = false) : PdfPageItem(Page);

public enum PdfMark
{
    Check,
    Cross,
    Dot,
}

/// <summary>A tick, a cross or a dot dropped on a form that can't be filled in.</summary>
public sealed record PdfMarkItem(int Page, PdfArea Area, PdfMark Mark, int Colour) : PdfPageItem(Page);

/// <summary>A line of the PDF's own text typed again: the old words go, the new ones are drawn where they were, set the same way.</summary>
public sealed record PdfReplaceItem(int Page, PdfArea Area, IReadOnlyList<PdfArea> Old, string Text, double Size, int Colour, bool Bold, bool Italic, string? FontName, bool RightToLeft, int Background = 0xFFFFFF) : PdfPageItem(Page);

public enum PdfMarkup
{
    Highlight,
    Underline,
    StrikeOut,
}

/// <summary>Words highlighted, underlined or struck out: a real annotation other readers can change.</summary>
public sealed record PdfMarkupItem(int Page, PdfMarkup Kind, IReadOnlyList<PdfArea> Areas, int Colour) : PdfPageItem(Page);

/// <summary>A sticky note: a real annotation with its words in it.</summary>
public sealed record PdfNoteItem(int Page, PdfArea Area, string Text, int Colour) : PdfPageItem(Page);

/// <summary>Freehand pen: a real ink annotation.</summary>
public sealed record PdfInkItem(int Page, IReadOnlyList<IReadOnlyList<(double X, double Y)>> Strokes, double Weight, int Colour) : PdfPageItem(Page);

/// <summary>A straight line or an arrow: a real line annotation.</summary>
public sealed record PdfLineItem(int Page, double X1, double Y1, double X2, double Y2, bool Arrow, double Weight, int Colour) : PdfPageItem(Page);

/// <summary>A rectangle or an ellipse: a real square or circle annotation.</summary>
public sealed record PdfShapeItem(int Page, PdfArea Area, bool Round, double Weight, int Colour) : PdfPageItem(Page);

/// <summary>A rectangle of solid colour painted over the page. It hides; what is under it stays in the file. <see cref="PdfRedactItem"/> removes.</summary>
public sealed record PdfCoverItem(int Page, PdfArea Area, int Colour = 0xFFFFFF) : PdfPageItem(Page);

/// <summary>A picture, or a signature, on the page. PNG, with its transparency.</summary>
public sealed record PdfPictureItem(int Page, PdfArea Area, byte[] Png) : PdfPageItem(Page);

/// <summary>A box whose contents really leave the file when it is saved.</summary>
public sealed record PdfRedactItem(int Page, PdfArea Area) : PdfPageItem(Page);

/// <summary>What to save: the fields' values by full name, what was put on the pages, and how.</summary>
public sealed record PdfEditRequest
{
    public IReadOnlyList<PdfPageItem> Items { get; init; } = [];

    /// <summary>
    /// A text field's text; a combo's or a single choice list's export value; a list's several values, one per line;
    /// a check box's or a radio group's on-state, or "Off".
    /// </summary>
    public IReadOnlyDictionary<string, string> Fields { get; init; } = new Dictionary<string, string>();

    /// <summary>Phrases being redacted: they also leave the file's other places, such as comments, bookmarks and metadata.</summary>
    public IReadOnlyList<string> Secrets { get; init; } = [];

    /// <summary>Fields and annotations burned into the pages, so they can't be changed.</summary>
    public bool Flatten { get; init; }

    /// <summary>When redacting: the file's hidden details leave too (author, dates, XMP, attachments, scripts).</summary>
    public bool RemoveHiddenDetails { get; init; } = true;
}

/// <param name="PicturePages">Pages redacted by redrawing them as pictures, because their words couldn't be taken out alone.</param>
/// <param name="CoveredLines">Retyped lines whose old words couldn't be taken out: they stay in the file, under a cover.</param>
public sealed record PdfEditResult(int PicturePages, int CoveredLines);

public enum PdfFieldKind
{
    Text,
    CheckBox,
    Radio,
    Combo,
    List,
    Button,
    Signature,
}

/// <param name="Export">What goes in the file.</param>
/// <param name="Label">What the person sees.</param>
public sealed record PdfFieldOption(string Export, string Label);

/// <summary>Where a field is on a page. A check box's or a radio button's state when it is on.</summary>
public sealed record PdfFieldWidget(int Page, PdfArea Area, string? OnState);

/// <summary>A form field as the person fills it in.</summary>
public sealed record PdfFormField(string Name, PdfFieldKind Kind, string Value, IReadOnlyList<PdfFieldOption> Options, IReadOnlyList<PdfFieldWidget> Widgets)
{
    public bool ReadOnly { get; init; }

    public bool Required { get; init; }

    public bool MultiLine { get; init; }

    public bool Password { get; init; }

    public bool Comb { get; init; }

    public int MaxLength { get; init; }

    /// <summary>The size the form sets its text at; 0 means as large as fits.</summary>
    public double FontSize { get; init; }

    /// <summary>A combo box that takes other text too.</summary>
    public bool Editable { get; init; }

    public bool MultiSelect { get; init; }

    /// <summary>The tip the form gives for this field, when it has one.</summary>
    public string? Tip { get; init; }
}

/// <summary>What a PDF's author allows. Honoured as it is: a form locked but open for filling can be filled and no more.</summary>
public enum PdfEditRights
{
    /// <summary>Nothing may be changed.</summary>
    None,

    /// <summary>Fields only.</summary>
    Fill,

    /// <summary>Fields, comments and marks that are annotations.</summary>
    Comment,

    /// <summary>Everything.</summary>
    Full,
}

/// <summary>One word of a page's own text.</summary>
public sealed record PdfTextWord(string Text, PdfArea Area);

/// <summary>One line of a page's own text, in reading order, set one way.</summary>
public sealed record PdfTextLine(string Text, PdfArea Area, IReadOnlyList<PdfTextWord> Words, double Size, bool Bold, bool Italic, bool RightToLeft, int Colour, string? FontName);

/// <summary>A place where a phrase or a private detail was found.</summary>
public sealed record PdfFound(int Page, PdfArea Area, string Text);

/// <summary>Between the page as displayed and the page's own coordinates (y up, its /Rotate not applied).</summary>
internal readonly record struct PdfPageSpace(double X1, double Y1, double X2, double Y2, int Rotate)
{
    public static PdfPageSpace Of(PdfPage page)
    {
        var box = PdfSharpIO.VisibleBox(page);
        return new PdfPageSpace(Math.Min(box.X1, box.X2), Math.Min(box.Y1, box.Y2), Math.Max(box.X1, box.X2), Math.Max(box.Y1, box.Y2), ((page.Rotate % 360) + 360) % 360);
    }

    public double Width => Rotate is 90 or 270 ? Y2 - Y1 : X2 - X1;

    public double Height => Rotate is 90 or 270 ? X2 - X1 : Y2 - Y1;

    public (double X, double Y) ToDisplay(double x, double y) => Rotate switch
    {
        90 => (y - Y1, x - X1),
        180 => (X2 - x, y - Y1),
        270 => (Y2 - y, X2 - x),
        _ => (x - X1, Y2 - y),
    };

    public (double X, double Y) ToUser(double x, double y) => Rotate switch
    {
        90 => (X1 + y, Y1 + x),
        180 => (X2 - x, Y1 + y),
        270 => (X2 - y, Y2 - x),
        _ => (X1 + x, Y2 - y),
    };

    /// <summary>A rectangle in the page's own coordinates, as displayed.</summary>
    public PdfArea AreaOf(double x1, double y1, double x2, double y2)
    {
        var a = ToDisplay(x1, y1);
        var b = ToDisplay(x2, y2);
        return PdfArea.Between(a.X, a.Y, b.X, b.Y);
    }

    /// <summary>A displayed rectangle in the page's own coordinates: left, bottom, right, top.</summary>
    public (double X1, double Y1, double X2, double Y2) UserRect(PdfArea area)
    {
        var a = ToUser(area.Left, area.Top);
        var b = ToUser(area.Right, area.Bottom);
        return (Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));
    }
}
