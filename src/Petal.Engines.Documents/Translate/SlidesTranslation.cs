using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using D = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;
using TextTranslator = System.Func<System.Collections.Generic.IReadOnlyList<string>, System.IProgress<double>?, System.Threading.CancellationToken, System.Threading.Tasks.Task<System.Collections.Generic.IReadOnlyList<string>>>;

namespace Petal.Engines.Documents.Translate;

/// <summary>
/// A PowerPoint deck in another language, changed in place on its copy: the slides (table cells too) and their notes.
/// A paragraph is translated as a whole, into its first run, so that run's formatting stays; a run with a link is translated
/// on its own, and the fields PowerPoint fills in (slide number, date) are left alone. Text boxes are told to shrink what
/// doesn't fit, with a shrink worked out here, because PowerPoint only works one out itself when the text is next edited.
/// </summary>
internal static class SlidesTranslation
{
    /// <summary>Text is about this much of its size wide, letter by letter.</summary>
    private const double LatinWidth = 0.52, ArabicWidth = 0.46;

    private const double LineHeight = 1.2;

    public static async Task RunAsync(string path, string from, string to, TextTranslator translate, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        using var document = PresentationDocument.Open(path, true);
        var presentation = document.PresentationPart ?? throw new InvalidDataException("The presentation has no slides.");
        var slides = presentation.SlideParts.Where(s => s.Slide is not null).ToList();
        var slots = new List<TextSlot>();
        foreach (var slide in slides)
        {
            Read(slide.Slide!, slots, to);
            if (slide.NotesSlidePart?.NotesSlide is { } notes)
                Read(notes, slots, to);
        }
        await DocumentTranslator.FillAsync(slots, translate, progress, cancellationToken).ConfigureAwait(false);
        var known = DocumentTranslator.Known(from, slots);
        foreach (var slide in slides)
        {
            SetDirection(slide.Slide!, slide, known, to);
            if (slide.NotesSlidePart?.NotesSlide is { } notes)
                SetDirection(notes, null, known, to);
            foreach (var shape in slide.Slide!.Descendants<P.Shape>())
                KeepInside(shape, slide, to);
        }
        document.Save();
    }

    private static void Read(OpenXmlElement root, List<TextSlot> slots, string to)
    {
        foreach (var paragraph in root.Descendants<D.Paragraph>().ToList())
        {
            var unit = new List<OpenXmlElement>();
            string? link = null;
            void Flush()
            {
                if (unit.OfType<D.Run>().Any())
                {
                    var parts = unit;
                    slots.Add(new TextSlot(string.Concat(parts.Select(p => p is D.Run run ? run.Text?.Text ?? "" : "\n")), words => Put(parts, words, to)));
                }
                unit = [];
            }
            foreach (var child in paragraph.ChildElements)
            {
                switch (child)
                {
                    case D.Run run:
                        var target = run.RunProperties?.GetFirstChild<D.HyperlinkOnClick>()?.Id?.Value;
                        if (target != link)
                            Flush();
                        link = target;
                        unit.Add(run);
                        break;
                    case D.Break:
                        unit.Add(child);
                        break;
                    case D.Field:
                        Flush();
                        link = null;
                        break;
                }
            }
            Flush();
        }
    }

    /// <summary>The first run takes the first line; each further line is a copy of it, after a line break.</summary>
    private static void Put(List<OpenXmlElement> parts, string words, string to)
    {
        var first = parts.OfType<D.Run>().First();
        // The look of most of the words: a bold lead-in doesn't make the whole paragraph bold.
        var most = parts.OfType<D.Run>().MaxBy(r => r.Text?.Text.Length ?? 0)!;
        if (most != first && most.RunProperties is { } look)
            first.RunProperties = (D.RunProperties)look.CloneNode(true);
        var lines = words.Split('\n');
        var properties = first.RunProperties ??= new D.RunProperties();
        properties.Language = DocumentTranslator.Tag(to);
        var old = parts.Where(p => p != first).ToList();
        var breakProperties = parts.OfType<D.Break>().FirstOrDefault()?.RunProperties;
        (first.Text ??= new D.Text()).Text = lines[0];
        OpenXmlElement last = first;
        foreach (var line in lines.Skip(1))
        {
            var lineBreak = new D.Break();
            if (breakProperties is not null)
                lineBreak.RunProperties = (D.RunProperties)breakProperties.CloneNode(true);
            last = first.Parent!.InsertAfter(lineBreak, last);
            var next = (D.Run)first.CloneNode(true);
            next.Text!.Text = line;
            last = first.Parent.InsertAfter(next, last);
        }
        foreach (var part in old)
            part.Remove();
    }

    /// <summary>What a paragraph takes over from its shape's list style, its placeholder on the layout, and the master.</summary>
    private readonly record struct Inherited(D.TextAlignmentTypeValues? Alignment, bool RightToLeft, int? Size);

    private static Inherited InheritedBy(D.Paragraph paragraph, SlidePart? slide)
    {
        int level = (paragraph.ParagraphProperties?.Level?.Value ?? 0) + 1;
        D.TextAlignmentTypeValues? alignment = null;
        bool? rtl = null;
        int? size = null;
        void From(OpenXmlElement? list)
        {
            if (list?.ChildElements.FirstOrDefault(e => e.LocalName == $"lvl{level}pPr") is not D.TextParagraphPropertiesType properties)
                return;
            alignment ??= properties.Alignment?.Value;
            rtl ??= properties.RightToLeft?.Value;
            size ??= properties.GetFirstChild<D.DefaultRunProperties>()?.FontSize?.Value;
        }
        var shape = paragraph.Ancestors<P.Shape>().FirstOrDefault();
        From(shape?.TextBody?.GetFirstChild<D.ListStyle>());
        var placeholder = shape?.NonVisualShapeProperties?.ApplicationNonVisualDrawingProperties?.GetFirstChild<P.PlaceholderShape>();
        if (slide is not null && placeholder is not null)
        {
            var layout = slide.SlideLayoutPart;
            From(Matching(layout?.SlideLayout, placeholder)?.TextBody?.GetFirstChild<D.ListStyle>());
            var master = layout?.SlideMasterPart?.SlideMaster;
            From(Matching(master, placeholder)?.TextBody?.GetFirstChild<D.ListStyle>());
            var kind = placeholder.Type?.Value;
            var styles = master?.TextStyles;
            From(kind == P.PlaceholderValues.Title || kind == P.PlaceholderValues.CenteredTitle ? styles?.TitleStyle
                : kind == P.PlaceholderValues.DateAndTime || kind == P.PlaceholderValues.Footer || kind == P.PlaceholderValues.SlideNumber ? styles?.OtherStyle
                : styles?.BodyStyle);
        }
        return new Inherited(alignment, rtl == true, size);
    }

    /// <summary>The shape on a layout or master that a placeholder takes after: by its number on the layout, else by its kind.</summary>
    private static P.Shape? Matching(OpenXmlElement? root, P.PlaceholderShape placeholder)
    {
        if (root is null)
            return null;
        var shapes = root.Descendants<P.Shape>().Select(s => (Shape: s, Place: s.NonVisualShapeProperties?.ApplicationNonVisualDrawingProperties?.GetFirstChild<P.PlaceholderShape>()))
            .Where(s => s.Place is not null).ToList();
        if (placeholder.Index?.Value is { } index && shapes.FirstOrDefault(s => s.Place!.Index?.Value == index).Shape is { } byIndex)
            return byIndex;
        var kind = placeholder.Type?.Value ?? P.PlaceholderValues.Body;
        // A master has one title, and one body for all the kinds of content.
        if (root is P.SlideMaster)
            kind = kind == P.PlaceholderValues.CenteredTitle ? P.PlaceholderValues.Title : kind == P.PlaceholderValues.SubTitle ? P.PlaceholderValues.Body : kind;
        return shapes.FirstOrDefault(s => (s.Place!.Type?.Value ?? P.PlaceholderValues.Body) == kind).Shape;
    }

    private static void SetDirection(OpenXmlElement root, SlidePart? slide, string from, string to)
    {
        bool fromRtl = DocumentTranslator.IsRightToLeft(from), toRtl = DocumentTranslator.IsRightToLeft(to);
        if (fromRtl == toRtl)
            return;
        foreach (var paragraph in root.Descendants<D.Paragraph>())
        {
            var inherited = InheritedBy(paragraph, slide);
            var properties = paragraph.ParagraphProperties;
            var alignment = properties?.Alignment?.Value ?? inherited.Alignment;
            if (toRtl)
            {
                properties = paragraph.ParagraphProperties ??= new D.ParagraphProperties();
                properties.RightToLeft = true;
                // Left, or nothing said, becomes right; a centred title stays centred.
                if (alignment == D.TextAlignmentTypeValues.Right)
                    properties.Alignment = D.TextAlignmentTypeValues.Left;
                else if (alignment is null || alignment == D.TextAlignmentTypeValues.Left)
                    properties.Alignment = D.TextAlignmentTypeValues.Right;
            }
            else if (properties is not null || inherited.RightToLeft || alignment == D.TextAlignmentTypeValues.Right)
            {
                properties = paragraph.ParagraphProperties ??= new D.ParagraphProperties();
                // The attribute only goes when the master doesn't say right to left itself.
                if (inherited.RightToLeft)
                    properties.RightToLeft = false;
                else
                    properties.RightToLeft = null;
                if (alignment == D.TextAlignmentTypeValues.Right)
                    properties.Alignment = D.TextAlignmentTypeValues.Left;
                else if (alignment == D.TextAlignmentTypeValues.Left)
                    properties.Alignment = D.TextAlignmentTypeValues.Right;
            }
        }
        foreach (var table in root.Descendants<D.TableProperties>())
            table.RightToLeft = toRtl ? true : null;
    }

    /// <summary>
    /// A text box that has no say in how its text fits is told to shrink it. By the box's size and the text's, roughly: when
    /// it wouldn't fit as it stands, the shrink is written down too. A box that grows with its text is left to grow.
    /// </summary>
    private static void KeepInside(P.Shape shape, SlidePart slide, string to)
    {
        if (shape.TextBody?.GetFirstChild<D.BodyProperties>() is not { } body || !shape.TextBody.Descendants<D.Text>().Any())
            return;
        if (body.GetFirstChild<D.ShapeAutoFit>() is not null)
            return;
        var fit = body.GetFirstChild<D.NormalAutoFit>();
        var off = body.GetFirstChild<D.NoAutoFit>();
        if (fit is null && off is null)
        {
            fit = new D.NormalAutoFit();
            body.AddChild(fit, false);
        }
        double scale = Shrink(shape, body, slide, to);
        if (scale >= 1)
            return;
        if (fit is null)
        {
            // It was told never to shrink, but the translation would run out of it.
            off!.Remove();
            fit = new D.NormalAutoFit();
            body.AddChild(fit, false);
        }
        int percent = (int)(scale * 100_000);
        if (fit.FontScale?.Value is { } already && already <= percent)
            return;
        fit.FontScale = percent;
        fit.LineSpaceReduction = scale < 0.9 ? 10_000 : null;
    }

    /// <summary>How much smaller the text has to be drawn to fit its box; 1 when it fits, or the box's size isn't known.</summary>
    private static double Shrink(P.Shape shape, D.BodyProperties body, SlidePart slide, string to)
    {
        if (body.Wrap?.Value == D.TextWrappingValues.None || (body.Vertical?.Value is { } vertical && vertical != D.TextVerticalValues.Horizontal))
            return 1;
        var placeholder = shape.NonVisualShapeProperties?.ApplicationNonVisualDrawingProperties?.GetFirstChild<P.PlaceholderShape>();
        var extents = shape.ShapeProperties?.Transform2D?.Extents;
        if (extents is null && placeholder is not null)
        {
            var layout = slide.SlideLayoutPart;
            extents = Matching(layout?.SlideLayout, placeholder)?.ShapeProperties?.Transform2D?.Extents
                ?? Matching(layout?.SlideMasterPart?.SlideMaster, placeholder)?.ShapeProperties?.Transform2D?.Extents;
        }
        if (extents?.Cx?.Value is not { } cx || extents.Cy?.Value is not { } cy)
            return 1;
        const double Emu = 12700;
        double width = (cx - (body.LeftInset?.Value ?? 91440) - (body.RightInset?.Value ?? 91440)) / Emu;
        double height = (cy - (body.TopInset?.Value ?? 45720) - (body.BottomInset?.Value ?? 45720)) / Emu;
        if (width <= 0 || height <= 0)
            return 1;
        double letter = DocumentTranslator.IsRightToLeft(to) ? ArabicWidth : LatinWidth;
        double needed = 0;
        foreach (var paragraph in shape.TextBody!.Elements<D.Paragraph>())
        {
            var run = paragraph.Elements<D.Run>().FirstOrDefault();
            double size = (run?.RunProperties?.FontSize?.Value ?? InheritedBy(paragraph, slide).Size ?? 1800) / 100.0;
            int letters = paragraph.Elements<D.Run>().Sum(r => r.Text?.Text.Length ?? 0);
            int lines = Math.Max(1, (int)Math.Ceiling(letters * size * letter / width));
            needed += lines * size * LineHeight;
        }
        // Both the size of the letters and the length of the lines shrink with the scale.
        return needed <= height ? 1 : Math.Clamp(Math.Sqrt(height / needed), 0.4, 1);
    }
}
