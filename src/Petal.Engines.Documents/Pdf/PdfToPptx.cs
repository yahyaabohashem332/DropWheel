using System.Text;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Presentation;
using Petal.Engines.Documents.Util;
using D = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;

namespace Petal.Engines.Documents.Pdf;

/// <summary>How PDF pages become slides.</summary>
public enum PdfToPptxMode
{
    /// <summary>The page's graphics as the slide background, with its text as editable text boxes on top.</summary>
    EditableText,

    /// <summary>Each page as a single picture: looks exactly like the PDF, nothing is editable.</summary>
    Picture,
}

/// <summary>Builds a PowerPoint deck with one slide per PDF page.</summary>
internal static partial class PdfToPptx
{
    private const long Emu = 12700;
    private const long MinSlide = 914400;      // 1 inch, PowerPoint's minimum
    private const long MaxSlide = 51206400;    // 56 inches, its maximum

    public static async Task WriteAsync(string pdfPath, string output, PdfToPptxMode mode, int dpi, int jpegQuality,
        PdfContentOptions contentOptions, IProgress<double>? progress, CancellationToken ct)
    {
        var pdf = await File.ReadAllBytesAsync(pdfPath, ct).ConfigureAwait(false);
        List<PdfPageContent>? pages = null;
        byte[] background = pdf;
        var stripped = Array.Empty<bool>();
        if (mode == PdfToPptxMode.EditableText)
        {
            // OCR'd words would sit on top of identical pixels, so scanned pages stay pictures.
            (pages, _) = await PdfContent.ExtractAsync(pdfPath, new PdfContentOptions { Ocr = false, Pictures = false }, null, ct).ConfigureAwait(false);
            (background, stripped) = PdfTextStripper.Strip(pdf);
        }
        progress?.Report(0.2);

        var sizes = PdfRaster.PageSizes(pdf);
        if (sizes.Count == 0)
            throw new InvalidDataException("The PDF has no pages.");
        var first = sizes[0];
        var scale = Math.Min(1.0, Math.Min(MaxSlide / (first.Width * Emu), MaxSlide / (first.Height * Emu)));
        scale = Math.Max(scale, Math.Max(MinSlide / (first.Width * Emu), MinSlide / (first.Height * Emu)));
        long slideW = (long)(first.Width * Emu * scale), slideH = (long)(first.Height * Emu * scale);

        using var doc = PresentationDocument.Create(output, PresentationDocumentType.Presentation);
        var presentation = doc.AddPresentationPart();
        presentation.Presentation = new Presentation();
        var layout = CreateMasterAndLayout(presentation, slideW, slideH);
        var slideIds = presentation.Presentation.SlideIdList!;

        for (var i = 0; i < sizes.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var size = sizes[i];
            // Pages of a different size are scaled to fit the slide and centred.
            var fit = Math.Min(slideW / (size.Width * Emu), slideH / (size.Height * Emu));
            var offX = (slideW - size.Width * Emu * fit) / 2;
            var offY = (slideH - size.Height * Emu * fit) / 2;

            var page = pages is not null && i < pages.Count ? pages[i] : null;
            var editable = mode == PdfToPptxMode.EditableText && page is not null && i < stripped.Length && stripped[i] &&
                           page.Words.Count > 0 && !page.Scanned;

            using var bitmap = PdfRaster.Render(editable ? background : pdf, i, dpi);
            var jpeg = ImageTools.EncodeJpeg(bitmap, jpegQuality);

            var slidePart = presentation.AddNewPart<SlidePart>();
            slidePart.AddPart(layout);
            var imagePart = slidePart.AddImagePart(ImagePartType.Jpeg);
            using (var ms = new MemoryStream(jpeg))
                imagePart.FeedData(ms);
            var imageId = slidePart.GetIdOfPart(imagePart);

            var tree = new ShapeTree(
                new P.NonVisualGroupShapeProperties(
                    new P.NonVisualDrawingProperties { Id = 1U, Name = "" },
                    new P.NonVisualGroupShapeDrawingProperties(),
                    new ApplicationNonVisualDrawingProperties()),
                new GroupShapeProperties(new D.TransformGroup()));

            uint shapeId = 2;
            if (Math.Abs(offX) > 1 || Math.Abs(offY) > 1)
            {
                // Letterboxed page: draw it as a picture so the margins stay plain.
                tree.Append(Picture(shapeId++, imageId, (long)offX, (long)offY, (long)(size.Width * Emu * fit), (long)(size.Height * Emu * fit)));
            }
            if (editable)
            {
                foreach (var row in PdfLayout.BuildRows(page!))
                    foreach (var line in row.Segments)
                        foreach (var chunk in Chunks(line))
                            tree.Append(TextBox(shapeId++, chunk, Emu * fit, offX, offY));
            }

            var data = new CommonSlideData();
            if (Math.Abs(offX) <= 1 && Math.Abs(offY) <= 1)
            {
                data.Append(new Background(new BackgroundProperties(
                    new D.BlipFill(new D.Blip { Embed = imageId }, new D.SourceRectangle(), new D.Stretch(new D.FillRectangle())) { Dpi = 0U, RotateWithShape = true },
                    new D.EffectList())));
            }
            data.Append(tree);
            slidePart.Slide = new Slide(data, new ColorMapOverride(new D.MasterColorMapping()));
            slidePart.Slide.Save();

            slideIds.Append(new SlideId { Id = (uint)(256 + i), RelationshipId = presentation.GetIdOfPart(slidePart) });
            progress?.Report(0.2 + 0.8 * (i + 1) / sizes.Count);
        }

        doc.PackageProperties.Title = Path.GetFileNameWithoutExtension(pdfPath);
        doc.PackageProperties.Created = DateTime.UtcNow;
        presentation.Presentation.Save();
    }

    /// <summary>
    /// Splits a line where words are further apart than a wide space (tabs after bullets,
    /// tab-aligned columns), so each piece sits exactly where it was on the page.
    /// </summary>
    private static IEnumerable<PdfLine> Chunks(PdfLine line)
    {
        if (line.Rtl || line.Words.Count < 2)
        {
            yield return line;
            yield break;
        }
        var ordered = line.Words.OrderBy(w => w.Left).ToList();
        var current = new List<PdfWord> { ordered[0] };
        for (var i = 1; i < ordered.Count; i++)
        {
            if (ordered[i].Left - ordered[i - 1].Right > 0.6 * line.FontSize)
            {
                yield return new PdfLine(current);
                current = [];
            }
            current.Add(ordered[i]);
        }
        yield return new PdfLine(current);
    }

    private static P.Picture Picture(uint id, string imageId, long x, long y, long cx, long cy) =>
        new(
            new P.NonVisualPictureProperties(
                new P.NonVisualDrawingProperties { Id = id, Name = $"Page {id}" },
                new P.NonVisualPictureDrawingProperties(new D.PictureLocks { NoChangeAspect = true }),
                new ApplicationNonVisualDrawingProperties()),
            new P.BlipFill(new D.Blip { Embed = imageId }, new D.Stretch(new D.FillRectangle())),
            new P.ShapeProperties(
                new D.Transform2D(new D.Offset { X = x, Y = y }, new D.Extents { Cx = cx, Cy = cy }),
                new D.PresetGeometry(new D.AdjustValueList()) { Preset = D.ShapeTypeValues.Rectangle }));

    private static P.Shape TextBox(uint id, PdfLine line, double scale, double offX, double offY)
    {
        var fs = line.FontSize;
        // PowerPoint puts the first baseline about 0.92 em below the top of a box with no insets.
        var baseline = line.Top + 0.78 * fs;
        var x = (long)(offX + line.Left * scale);
        var y = (long)(offY + (baseline - 0.92 * fs) * scale);
        var cx = (long)Math.Max(Emu, (line.Right - line.Left + 0.5 * fs) * scale);
        var cy = (long)Math.Max(Emu, 1.25 * fs * scale);

        var paragraph = new D.Paragraph();
        if (line.Rtl)
            paragraph.Append(new D.ParagraphProperties { RightToLeft = true, Alignment = D.TextAlignmentTypeValues.Right });

        // One run per stretch of words that share font, style and colour.
        var groups = new List<(List<PdfWord> Words, string Font, bool Bold, bool Italic, int Color, double Size)>();
        foreach (var w in line.Ordered)
        {
            var font = FamilyOf(w.FontName);
            var color = w.Color ?? 0;
            if (groups.Count > 0 && groups[^1].Font == font && groups[^1].Bold == w.Bold && groups[^1].Italic == w.Italic &&
                groups[^1].Color == color && Math.Abs(groups[^1].Size - w.FontSize) < 0.3)
                groups[^1].Words.Add(w);
            else
                groups.Add(([w], font, w.Bold, w.Italic, color, w.FontSize));
        }
        for (var g = 0; g < groups.Count; g++)
        {
            var (words, font, bold, italic, color, size) = groups[g];
            var text = PdfLayout.JoinWords(words) + (g < groups.Count - 1 ? " " : "");
            var props = new D.RunProperties
            {
                Language = "en-US",
                FontSize = (int)Math.Clamp(Math.Round(size * scale / Emu * 100), 100, 400000),
                Bold = bold,
                Italic = italic,
                Dirty = false,
            };
            props.Append(new D.SolidFill(new D.RgbColorModelHex { Val = color.ToString("X6") }));
            props.Append(new D.LatinFont { Typeface = font });
            props.Append(new D.ComplexScriptFont { Typeface = line.Rtl ? "Arial" : font });
            paragraph.Append(new D.Run(props, new D.Text(IO.DocxWriter.Sanitize(text))));
        }

        return new P.Shape(
            new P.NonVisualShapeProperties(
                new P.NonVisualDrawingProperties { Id = id, Name = $"Text {id}" },
                new P.NonVisualShapeDrawingProperties { TextBox = true },
                new ApplicationNonVisualDrawingProperties()),
            new P.ShapeProperties(
                new D.Transform2D(new D.Offset { X = x, Y = y }, new D.Extents { Cx = cx, Cy = cy }),
                new D.PresetGeometry(new D.AdjustValueList()) { Preset = D.ShapeTypeValues.Rectangle },
                new D.NoFill()),
            new P.TextBody(
                new D.BodyProperties
                {
                    Wrap = D.TextWrappingValues.None, LeftInset = 0, TopInset = 0, RightInset = 0, BottomInset = 0,
                    Anchor = D.TextAnchoringTypeValues.Top, RightToLeftColumns = false,
                },
                new D.ListStyle(),
                paragraph));
    }

    /// <summary>Guesses an installed family from a PDF font name: "ABCDEF+TimesNewRomanPS-BoldMT" → "Times New Roman".</summary>
    internal static string FamilyOf(string? pdfFontName)
    {
        if (string.IsNullOrWhiteSpace(pdfFontName))
            return "Calibri";
        var name = PdfContent.StripSubset(pdfFontName);
        var cut = name.IndexOfAny(['-', ',']);
        if (cut > 0)
            name = name[..cut];
        name = TrailingTags().Replace(name, "");
        name = name switch
        {
            "Helvetica" or "ArialMT" or "Arial" => "Arial",
            "Times" or "TimesRoman" or "TimesNewRoman" => "Times New Roman",
            "Courier" or "CourierNew" => "Courier New",
            _ => CamelCase().Replace(name, "$1 $2"),
        };
        return name.Length == 0 ? "Calibri" : name;
    }

    [GeneratedRegex("(PSMT|MT|PS)$")]
    private static partial Regex TrailingTags();

    [GeneratedRegex("([a-z])([A-Z])")]
    private static partial Regex CamelCase();

    /// <summary>The slide master, one blank layout and a theme: the minimum PowerPoint needs.</summary>
    private static SlideLayoutPart CreateMasterAndLayout(PresentationPart presentation, long slideW, long slideH)
    {
        var master = presentation.AddNewPart<SlideMasterPart>("rId1");
        var layout = master.AddNewPart<SlideLayoutPart>("rId1");
        layout.SlideLayout = new SlideLayout(
            new CommonSlideData(new ShapeTree(
                new P.NonVisualGroupShapeProperties(
                    new P.NonVisualDrawingProperties { Id = 1U, Name = "" },
                    new P.NonVisualGroupShapeDrawingProperties(),
                    new ApplicationNonVisualDrawingProperties()),
                new GroupShapeProperties(new D.TransformGroup()))) { Name = "Blank" },
            new ColorMapOverride(new D.MasterColorMapping()))
        { Type = SlideLayoutValues.Blank, Preserve = true };
        layout.SlideLayout.Save();
        layout.AddPart(master);

        master.SlideMaster = new SlideMaster(
            new CommonSlideData(new ShapeTree(
                new P.NonVisualGroupShapeProperties(
                    new P.NonVisualDrawingProperties { Id = 1U, Name = "" },
                    new P.NonVisualGroupShapeDrawingProperties(),
                    new ApplicationNonVisualDrawingProperties()),
                new GroupShapeProperties(new D.TransformGroup()))),
            new P.ColorMap
            {
                Background1 = D.ColorSchemeIndexValues.Light1, Text1 = D.ColorSchemeIndexValues.Dark1,
                Background2 = D.ColorSchemeIndexValues.Light2, Text2 = D.ColorSchemeIndexValues.Dark2,
                Accent1 = D.ColorSchemeIndexValues.Accent1, Accent2 = D.ColorSchemeIndexValues.Accent2,
                Accent3 = D.ColorSchemeIndexValues.Accent3, Accent4 = D.ColorSchemeIndexValues.Accent4,
                Accent5 = D.ColorSchemeIndexValues.Accent5, Accent6 = D.ColorSchemeIndexValues.Accent6,
                Hyperlink = D.ColorSchemeIndexValues.Hyperlink, FollowedHyperlink = D.ColorSchemeIndexValues.FollowedHyperlink,
            },
            new SlideLayoutIdList(new SlideLayoutId { Id = 2147483649U, RelationshipId = master.GetIdOfPart(layout) }),
            new TextStyles(new TitleStyle(), new BodyStyle(), new OtherStyle()));

        var theme = master.AddNewPart<ThemePart>("rId5");
        theme.Theme = Theme();
        master.SlideMaster.Save();
        presentation.AddPart(theme, "rId5");

        presentation.Presentation!.Append(
            new SlideMasterIdList(new SlideMasterId { Id = 2147483648U, RelationshipId = presentation.GetIdOfPart(master) }),
            new SlideIdList(),
            new SlideSize { Cx = (int)Math.Clamp(slideW, MinSlide, MaxSlide), Cy = (int)Math.Clamp(slideH, MinSlide, MaxSlide) },
            new NotesSize { Cx = 6858000, Cy = 9144000 },
            new DefaultTextStyle());
        return layout;
    }

    private static D.Theme Theme()
    {
        static D.SolidFill Scheme(D.SchemeColorValues v) => new(new D.SchemeColor { Val = v });
        var colors = new D.ColorScheme(
            new D.Dark1Color(new D.SystemColor { Val = D.SystemColorValues.WindowText, LastColor = "000000" }),
            new D.Light1Color(new D.SystemColor { Val = D.SystemColorValues.Window, LastColor = "FFFFFF" }),
            new D.Dark2Color(new D.RgbColorModelHex { Val = "44546A" }),
            new D.Light2Color(new D.RgbColorModelHex { Val = "E7E6E6" }),
            new D.Accent1Color(new D.RgbColorModelHex { Val = "4472C4" }),
            new D.Accent2Color(new D.RgbColorModelHex { Val = "ED7D31" }),
            new D.Accent3Color(new D.RgbColorModelHex { Val = "A5A5A5" }),
            new D.Accent4Color(new D.RgbColorModelHex { Val = "FFC000" }),
            new D.Accent5Color(new D.RgbColorModelHex { Val = "5B9BD5" }),
            new D.Accent6Color(new D.RgbColorModelHex { Val = "70AD47" }),
            new D.Hyperlink(new D.RgbColorModelHex { Val = "0563C1" }),
            new D.FollowedHyperlinkColor(new D.RgbColorModelHex { Val = "954F72" }))
        { Name = "Office" };
        var fonts = new D.FontScheme(
            new D.MajorFont(new D.LatinFont { Typeface = "Calibri Light" }, new D.EastAsianFont { Typeface = "" }, new D.ComplexScriptFont { Typeface = "" }),
            new D.MinorFont(new D.LatinFont { Typeface = "Calibri" }, new D.EastAsianFont { Typeface = "" }, new D.ComplexScriptFont { Typeface = "" }))
        { Name = "Office" };
        var format = new D.FormatScheme(
            new D.FillStyleList(Scheme(D.SchemeColorValues.PhColor), Scheme(D.SchemeColorValues.PhColor), Scheme(D.SchemeColorValues.PhColor)),
            new D.LineStyleList(
                new D.Outline(Scheme(D.SchemeColorValues.PhColor)) { Width = 6350 },
                new D.Outline(Scheme(D.SchemeColorValues.PhColor)) { Width = 12700 },
                new D.Outline(Scheme(D.SchemeColorValues.PhColor)) { Width = 19050 }),
            new D.EffectStyleList(new D.EffectStyle(new D.EffectList()), new D.EffectStyle(new D.EffectList()), new D.EffectStyle(new D.EffectList())),
            new D.BackgroundFillStyleList(Scheme(D.SchemeColorValues.PhColor), Scheme(D.SchemeColorValues.PhColor), Scheme(D.SchemeColorValues.PhColor)))
        { Name = "Office" };
        return new D.Theme(new D.ThemeElements(colors, fonts, format), new D.ObjectDefaults(), new D.ExtraColorSchemeList()) { Name = "Office Theme" };
    }
}
