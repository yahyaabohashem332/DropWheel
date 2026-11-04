using System.Globalization;
using System.Text;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;
using Petal.Engines.Documents.Text;
using Petal.Engines.Documents.Util;
using SkiaSharp;

namespace Petal.Engines.Documents.Pdf;

/// <summary>
/// Everything the Edit PDF window puts in a file: its fields' values, what was typed, marked and placed on its pages, and, last
/// so nothing added escapes it, redaction. Each step goes from bytes to bytes, so each can check what the one before it wrote.
/// Text, marks, covers and pictures go on the page itself; highlights, comments and drawings are real annotations other readers
/// can change, each with an appearance of its own so that all of them show it alike.
/// </summary>
internal static class PdfEditor
{
    /// <summary>A retyped line isn't made smaller than this to keep it on the page.</summary>
    private const double SmallestSize = 5;

    public static PdfEditResult Apply(ref byte[] pdf, PdfEditRequest request, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        PdfFonts.EnsureInitialized();
        var replaced = request.Items.OfType<PdfReplaceItem>().ToList();
        var boxes = replaced.GroupBy(r => r.Page).ToDictionary(g => g.Key, g => g.SelectMany(r => r.Old).ToList());
        var failed = new HashSet<int>();
        if (boxes.Count > 0)
            (pdf, failed) = PdfContentEraser.Erase(pdf, boxes, EraseMode.TextOnly, cancellationToken);
        progress?.Report(0.2);

        var redactions = request.Items.OfType<PdfRedactItem>().ToList();
        bool touches = request.Fields.Count > 0 || request.Items.Any(i => i is not PdfRedactItem) || request.Flatten;
        if (touches)
        {
            using var document = PdfSharpIO.Open(pdf, PdfDocumentOpenMode.Modify);
            PdfForms.Fill(document, request.Fields);
            // A file certified or granted reader rights by its author isn't that file any more.
            document.Internals.Catalog.Elements.Remove("/Perms");
            foreach (var group in request.Items.Where(i => i is not PdfRedactItem).GroupBy(i => i.Page))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (group.Key >= 0 && group.Key < document.PageCount)
                    Put(document, document.Pages[group.Key], group.ToList(), failed.Contains(group.Key), pdf);
            }
            if (request.Flatten)
                Flatten(document, request.Items.OfType<PdfNoteItem>().ToList());
            pdf = PdfSharpIO.SaveToBytes(document);
        }
        progress?.Report(0.5);

        int pictures = redactions.Count == 0 ? 0 : PdfRedactor.Apply(ref pdf, redactions, request.Secrets, request.RemoveHiddenDetails, Util.ScaledProgress.Of(progress, 0.5, 1), cancellationToken);
        progress?.Report(1);
        return new PdfEditResult(pictures, replaced.Count(r => failed.Contains(r.Page)));
    }

    // ---------- on the page ----------

    private static void Put(PdfDocument document, PdfPage page, List<PdfPageItem> items, bool oldWordsStayed, byte[] pdf)
    {
        var painted = items.Where(i => i is PdfTextItem or PdfMarkItem or PdfReplaceItem or PdfCoverItem or PdfPictureItem).ToList();
        if (painted.Count > 0)
        {
            using var gfx = PdfStamper.Upright(page);
            foreach (var item in painted)
            {
                switch (item)
                {
                    case PdfCoverItem cover:
                        gfx.DrawRectangle(new XSolidBrush(Colour(cover.Colour)), cover.Area.Left, cover.Area.Top, cover.Area.Width, cover.Area.Height);
                        break;
                    case PdfTextItem text:
                        DrawTyped(gfx, text);
                        break;
                    case PdfMarkItem mark:
                        DrawMark(gfx, mark);
                        break;
                    case PdfReplaceItem replace:
                        DrawReplacement(gfx, page, replace, oldWordsStayed);
                        break;
                    case PdfPictureItem picture:
                        using (var image = XImage.FromStream(new MemoryStream(picture.Png)))
                            gfx.DrawImage(image, picture.Area.Left, picture.Area.Top, picture.Area.Width, picture.Area.Height);
                        break;
                }
            }
        }
        foreach (var item in items)
        {
            switch (item)
            {
                case PdfMarkupItem markup:
                    PdfAnnotationWriter.Markup(document, page, markup);
                    break;
                case PdfNoteItem note:
                    PdfAnnotationWriter.Note(document, page, note);
                    break;
                case PdfInkItem ink:
                    PdfAnnotationWriter.Ink(document, page, ink);
                    break;
                case PdfLineItem line:
                    PdfAnnotationWriter.Line(document, page, line);
                    break;
                case PdfShapeItem shape:
                    PdfAnnotationWriter.Shape(document, page, shape);
                    break;
            }
        }
    }

    internal static XColor Colour(int rgb) => XColor.FromArgb((rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);

    /// <summary>The first baseline below an item's top and the distance between lines, in sizes of its letters: Arial's own, as the window sets it.</summary>
    internal const double FirstBaseline = 0.905, LineStep = 1.15;

    /// <summary>Typed text: its lines broken to the box's width, shaped, and set from the side their language starts on.</summary>
    private static void DrawTyped(XGraphics gfx, PdfTextItem item)
    {
        var text = item.Text;
        if (string.IsNullOrWhiteSpace(text))
            return;
        var fallback = PdfFonts.FallbackFor(text);
        var family = fallback is null || Bidi.ContainsRtl(text) ? PdfFonts.Arabic : fallback;
        bool rtl = Bidi.IsRtlParagraph(text);
        var font = new XFont(family, item.Size, item.Bold ? XFontStyleEx.Bold : XFontStyleEx.Regular, new XPdfFontOptions(PdfFontEncoding.Unicode));
        var brush = new XSolidBrush(Colour(item.Colour));
        double baseline = item.Area.Top + FirstBaseline * item.Size;
        foreach (var raw in PdfFonts.BreakRtl(text, family, item.Size, item.Bold, false, item.Area.Width, rtl))
        {
            var line = PdfFonts.Drawable(raw);
            double wide = gfx.MeasureString(line, font).Width;
            gfx.DrawString(line, font, brush, rtl ? item.Area.Right - wide : item.Area.Left, baseline);
            baseline += LineStep * item.Size;
        }
    }

    private static void DrawMark(XGraphics gfx, PdfMarkItem item)
    {
        var a = item.Area;
        double side = Math.Min(a.Width, a.Height);
        var ink = Colour(item.Colour);
        var pen = new XPen(ink, side * 0.14) { LineCap = XLineCap.Round, LineJoin = XLineJoin.Round };
        switch (item.Mark)
        {
            case PdfMark.Check:
                gfx.DrawLines(pen, new XPoint[] { Point(a, 0.14, 0.55), Point(a, 0.40, 0.80), Point(a, 0.86, 0.20) });
                break;
            case PdfMark.Cross:
                gfx.DrawLine(pen, Point(a, 0.18, 0.18), Point(a, 0.82, 0.82));
                gfx.DrawLine(pen, Point(a, 0.82, 0.18), Point(a, 0.18, 0.82));
                break;
            default:
                gfx.DrawEllipse(new XSolidBrush(ink), a.Left + a.Width * 0.14, a.Top + a.Height * 0.14, a.Width * 0.72, a.Height * 0.72);
                break;
        }
    }

    private static XPoint Point(PdfArea a, double x, double y) => new(a.Left + a.Width * x, a.Top + a.Height * y);

    /// <summary>A retyped line in the place and the set of the old one, made smaller only so far as to stay on the page.</summary>
    private static void DrawReplacement(XGraphics gfx, PdfPage page, PdfReplaceItem item, bool oldWordsStayed)
    {
        if (oldWordsStayed)
            foreach (var old in item.Old)
                gfx.DrawRectangle(new XSolidBrush(Colour(item.Background)), old.Left - 0.5, old.Top, old.Width + 1, old.Height);
        var text = (item.Text ?? "").Replace('\r', ' ').Replace('\n', ' ');
        if (text.Trim().Length == 0)
            return;
        bool arabicScript = Bidi.ContainsRtl(text);
        // By its first letter, as it is typed; with no letters at all, as the line it replaces ran.
        bool rtl = Bidi.IsRtlParagraph(text) || (item.RightToLeft && !text.Any(char.IsLetter));
        bool italic = item.Italic && !arabicScript;
        var family = PdfFonts.FallbackFor(text) is { } other && !arabicScript ? other : PdfFonts.FamilyLike(item.FontName, arabicScript);
        var line = PdfFonts.Drawable(Bidi.ToVisual(text, rtl));
        double size = item.Size;
        var (shownWidth, _) = PdfStamper.DisplayedSize(page);
        double room = rtl ? item.Area.Right : shownWidth - item.Area.Left;
        double measured = PdfFonts.Measure(line, family, size, item.Bold, italic);
        if (measured > room && measured > 0)
            size = Math.Max(SmallestSize, size * room / measured);
        var style = (item.Bold ? XFontStyleEx.Bold : XFontStyleEx.Regular) | (italic ? XFontStyleEx.Italic : XFontStyleEx.Regular);
        var font = new XFont(family, size, style, new XPdfFontOptions(PdfFontEncoding.Unicode));
        double wide = gfx.MeasureString(line, font).Width;
        // PdfWord's box ends this far below its baseline.
        double baseline = item.Area.Bottom - PdfContentEraser.Descent * item.Size;
        gfx.DrawString(line, font, new XSolidBrush(Colour(item.Colour)), rtl ? item.Area.Right - wide : item.Area.Left, baseline);
    }

    /// <summary>A colour for what is behind some words: the middle of what lies just around them on a picture of the page.</summary>
    public static int BackgroundAround(SKBitmap picture, double scale, PdfArea area)
    {
        var reds = new List<byte>();
        var greens = new List<byte>();
        var blues = new List<byte>();
        void Sample(int x, int y)
        {
            if (x < 0 || y < 0 || x >= picture.Width || y >= picture.Height)
                return;
            var pixel = picture.GetPixel(x, y);
            reds.Add(pixel.Red);
            greens.Add(pixel.Green);
            blues.Add(pixel.Blue);
        }
        int left = (int)((area.Left - 2) * scale), right = (int)((area.Right + 2) * scale), top = (int)((area.Top - 2) * scale), bottom = (int)((area.Bottom + 2) * scale);
        for (int x = left; x <= right; x += 2)
        {
            Sample(x, top);
            Sample(x, bottom);
        }
        for (int y = top; y <= bottom; y += 2)
        {
            Sample(left, y);
            Sample(right, y);
        }
        if (reds.Count == 0)
            return 0xFFFFFF;
        reds.Sort();
        greens.Sort();
        blues.Sort();
        return (reds[reds.Count / 2] << 16) | (greens[greens.Count / 2] << 8) | blues[blues.Count / 2];
    }

    // ---------- flattening ----------

    /// <summary>
    /// Burns every widget and annotation into its page, each by its own appearance in its own place, and leaves no form and
    /// nothing that can be changed. Links stay: they change nothing that is seen. A comment's words go on the page as a small note,
    /// or they'd be lost; hidden annotations and those with nothing to show go.
    /// </summary>
    public static void Flatten(PdfDocument document, IReadOnlyList<PdfNoteItem> notes)
    {
        for (int p = 0; p < document.PageCount; p++)
        {
            var page = document.Pages[p];
            if (page.Elements.GetArray("/Annots") is not { } annots)
                continue;
            var operators = new StringBuilder();
            var kept = new List<PdfItem>();
            var resources = page.Resources;
            foreach (var item in annots.Elements.ToList())
            {
                var annotation = PdfWalk.Dictionary(item);
                if (annotation is null)
                    continue;
                var subtype = annotation.Elements.GetName("/Subtype");
                if (subtype == "/Link")
                {
                    kept.Add(item);
                    continue;
                }
                if (subtype == "/Popup" || ((int)(PdfWalk.Number(annotation.Elements["/F"]) ?? 0) & (2 | 32)) != 0)
                    continue;
                if (PdfWalk.Rectangle(annotation.Elements["/Rect"]) is not { } rect)
                    continue;
                var form = NormalAppearance(annotation);
                if (form is null)
                    continue;
                if (PlaceForm(document, resources, form, rect) is { } drawn)
                    operators.Append(drawn);
            }
            annots.Elements.Clear();
            foreach (var item in kept)
                annots.Elements.Add(item);
            if (kept.Count == 0)
                page.Elements.Remove("/Annots");
            if (operators.Length > 0)
            {
                var content = page.Contents.CreateSingleContent().Stream?.UnfilteredValue ?? [];
                PdfWalk.SetContent(page, [.. Encoding.ASCII.GetBytes("q\n"), .. content, .. Encoding.ASCII.GetBytes("\nQ\n" + operators)]);
            }
            var words = notes.Where(n => n.Page == p).ToList();
            if (words.Count > 0)
                using (var gfx = PdfStamper.Upright(page))
                    foreach (var note in words)
                        DrawNoteWords(gfx, page, note);
        }
        document.Internals.Catalog.Elements.Remove("/AcroForm");
    }

    /// <summary>The stream a reader draws for an annotation now: its one normal appearance, or the one its state names.</summary>
    private static PdfDictionary? NormalAppearance(PdfDictionary annotation)
    {
        var normal = annotation.Elements.GetDictionary("/AP")?.Elements.GetDictionary("/N");
        if (normal is null)
            return null;
        if (normal.Stream is not null)
            return normal;
        var state = PdfWalk.Text(annotation.Elements["/AS"]) ?? "/Off";
        return normal.Elements.GetDictionary(state);
    }

    /// <summary>The spec's way: the appearance's box, through its matrix, stretched onto the annotation's rectangle.</summary>
    private static string? PlaceForm(PdfDocument document, PdfResources resources, PdfDictionary form, (double X1, double Y1, double X2, double Y2) rect)
    {
        if (form.Stream is null || PdfWalk.Rectangle(form.Elements["/BBox"]) is not { } box)
            return null;
        double a = 1, b = 0, c = 0, d = 1, e = 0, f = 0;
        if (form.Elements.GetArray("/Matrix") is { Elements.Count: 6 } m)
        {
            a = m.Elements.GetReal(0);
            b = m.Elements.GetReal(1);
            c = m.Elements.GetReal(2);
            d = m.Elements.GetReal(3);
            e = m.Elements.GetReal(4);
            f = m.Elements.GetReal(5);
        }
        double[] xs = [a * box.X1 + c * box.Y1 + e, a * box.X2 + c * box.Y1 + e, a * box.X1 + c * box.Y2 + e, a * box.X2 + c * box.Y2 + e];
        double[] ys = [b * box.X1 + d * box.Y1 + f, b * box.X2 + d * box.Y1 + f, b * box.X1 + d * box.Y2 + f, b * box.X2 + d * box.Y2 + f];
        double left = xs.Min(), right = xs.Max(), low = ys.Min(), high = ys.Max();
        if (right - left < 1e-6 || high - low < 1e-6)
            return null;
        double sx = (rect.X2 - rect.X1) / (right - left), sy = (rect.Y2 - rect.Y1) / (high - low);
        double tx = rect.X1 - left * sx, ty = rect.Y1 - low * sy;
        if (form.Reference is null)
            document.Internals.AddObject(form);
        var drawings = resources.Elements.GetDictionary("/XObject");
        if (drawings is null)
        {
            drawings = new PdfDictionary(document);
            resources.Elements["/XObject"] = drawings;
        }
        var name = "/PetalFlat" + drawings.Elements.Count.ToString(CultureInfo.InvariantCulture);
        drawings.Elements[name] = form.Reference!;
        return string.Format(CultureInfo.InvariantCulture, "q {0:0.#####} 0 0 {1:0.#####} {2:0.###} {3:0.###} cm {4} Do Q\n", sx, sy, tx, ty, name);
    }

    private static void DrawNoteWords(XGraphics gfx, PdfPage page, PdfNoteItem note)
    {
        if (string.IsNullOrWhiteSpace(note.Text))
            return;
        const double size = 8, pad = 3;
        var (shownWidth, _) = PdfStamper.DisplayedSize(page);
        bool rtl = Bidi.IsRtlParagraph(note.Text);
        double width = Math.Min(180, Math.Max(60, shownWidth - note.Area.Right - 4));
        var lines = PdfFonts.BreakRtl(note.Text, PdfFonts.Arabic, size, false, false, width - 2 * pad, rtl).Select(PdfFonts.Drawable).ToList();
        double left = Math.Max(2, Math.Min(note.Area.Right + 2, shownWidth - width - 2));
        double height = lines.Count * LineStep * size + 2 * pad;
        gfx.DrawRectangle(new XPen(XColor.FromArgb(150, 130, 40), 0.5), new XSolidBrush(XColor.FromArgb(255, 245, 190)), left, note.Area.Top, width, height);
        var font = new XFont(PdfFonts.Arabic, size, XFontStyleEx.Regular, new XPdfFontOptions(PdfFontEncoding.Unicode));
        double baseline = note.Area.Top + pad + FirstBaseline * size;
        foreach (var line in lines)
        {
            double wide = gfx.MeasureString(line, font).Width;
            gfx.DrawString(line, font, XBrushes.Black, rtl ? left + width - pad - wide : left + pad, baseline);
            baseline += LineStep * size;
        }
    }
}

/// <summary>Small building blocks for what goes in a PDF's object graph directly.</summary>
internal static class PdfEditorParts
{
    /// <summary>A form XObject with a box and its drawing operators, added to the file.</summary>
    public static PdfDictionary Form(PdfDocument document, double x1, double y1, double x2, double y2, string operators, PdfDictionary? resources = null) =>
        Form(document, x1, y1, x2, y2, Encoding.Latin1.GetBytes(operators), resources);

    public static PdfDictionary Form(PdfDocument document, double x1, double y1, double x2, double y2, byte[] operators, PdfDictionary? resources = null)
    {
        var form = new PdfDictionary(document);
        form.Elements["/Type"] = new PdfName("/XObject");
        form.Elements["/Subtype"] = new PdfName("/Form");
        form.Elements["/BBox"] = new PdfArray(document, new PdfReal(x1), new PdfReal(y1), new PdfReal(x2), new PdfReal(y2));
        form.Elements["/Resources"] = resources ?? new PdfDictionary(document);
        form.CreateStream(operators);
        document.Internals.AddObject(form);
        return form;
    }
}

/// <summary>
/// Real annotations: other readers list them and can change them. Their places are in the page's own coordinates, its turn and crop
/// undone, and so are their appearances, drawn on a box the size of their rectangle.
/// </summary>
internal static class PdfAnnotationWriter
{
    private static string N(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    private static PdfArray Numbers(PdfDocument document, IEnumerable<double> values) =>
        new(document, values.Select(v => (PdfItem)new PdfReal(Math.Round(v, 3))).ToArray());

    private static PdfArray Rgb(PdfDocument document, int rgb) =>
        Numbers(document, [((rgb >> 16) & 0xFF) / 255.0, ((rgb >> 8) & 0xFF) / 255.0, (rgb & 0xFF) / 255.0]);

    private static string RgbOperators(int rgb, string op) =>
        $"{N(((rgb >> 16) & 0xFF) / 255.0)} {N(((rgb >> 8) & 0xFF) / 255.0)} {N((rgb & 0xFF) / 255.0)} {op} ";

    private static PdfDictionary New(PdfDocument document, PdfPage page, string subtype, int colour, (double X1, double Y1, double X2, double Y2) rect)
    {
        var annotation = new PdfDictionary(document);
        annotation.Elements["/Type"] = new PdfName("/Annot");
        annotation.Elements["/Subtype"] = new PdfName(subtype);
        annotation.Elements["/Rect"] = Numbers(document, [rect.X1, rect.Y1, rect.X2, rect.Y2]);
        annotation.Elements["/F"] = new PdfInteger(4);
        annotation.Elements["/C"] = Rgb(document, colour);
        annotation.Elements["/NM"] = new PdfString(Guid.NewGuid().ToString("N"));
        annotation.Elements.SetDateTime("/M", DateTime.Now);
        document.Internals.AddObject(annotation);
        annotation.Elements["/P"] = page.Reference!;
        var annots = page.Elements.GetArray("/Annots");
        if (annots is null)
        {
            annots = new PdfArray(document);
            page.Elements["/Annots"] = annots;
        }
        annots.Elements.Add(annotation.Reference!);
        return annotation;
    }

    private static void Appear(PdfDocument document, PdfDictionary annotation, (double X1, double Y1, double X2, double Y2) rect, string operators, PdfDictionary? resources = null)
    {
        var form = PdfEditorParts.Form(document, rect.X1, rect.Y1, rect.X2, rect.Y2, operators, resources);
        var appearance = new PdfDictionary(document);
        appearance.Elements["/N"] = form.Reference!;
        annotation.Elements["/AP"] = appearance;
    }

    private static (double X1, double Y1, double X2, double Y2) Bounds(IEnumerable<(double X, double Y)> points, double grow)
    {
        var list = points.ToList();
        return (list.Min(p => p.X) - grow, list.Min(p => p.Y) - grow, list.Max(p => p.X) + grow, list.Max(p => p.Y) + grow);
    }

    public static void Markup(PdfDocument document, PdfPage page, PdfMarkupItem item)
    {
        if (item.Areas.Count == 0)
            return;
        var space = PdfPageSpace.Of(page);
        // A word's corners in the order readers expect: top left, top right, bottom left, bottom right, as it reads.
        var quads = item.Areas.Select(a => new[] { space.ToUser(a.Left, a.Top), space.ToUser(a.Right, a.Top), space.ToUser(a.Left, a.Bottom), space.ToUser(a.Right, a.Bottom) }).ToList();
        var rect = Bounds(quads.SelectMany(q => q), 0.5);
        var subtype = item.Kind switch { PdfMarkup.Highlight => "/Highlight", PdfMarkup.Underline => "/Underline", _ => "/StrikeOut" };
        var annotation = New(document, page, subtype, item.Colour, rect);
        annotation.Elements["/QuadPoints"] = Numbers(document, quads.SelectMany(q => q).SelectMany(p => new[] { p.X, p.Y }));
        var drawn = new StringBuilder();
        PdfDictionary? resources = null;
        if (item.Kind == PdfMarkup.Highlight)
        {
            // Multiplied with the page, as a highlighter is: the words stay readable under it.
            var state = new PdfDictionary(document);
            state.Elements["/BM"] = new PdfName("/Multiply");
            var states = new PdfDictionary(document);
            states.Elements["/G0"] = state;
            resources = new PdfDictionary(document);
            resources.Elements["/ExtGState"] = states;
            drawn.Append("/G0 gs ").Append(RgbOperators(item.Colour, "rg"));
            foreach (var q in quads)
                drawn.Append($"{N(q[0].X)} {N(q[0].Y)} m {N(q[1].X)} {N(q[1].Y)} l {N(q[3].X)} {N(q[3].Y)} l {N(q[2].X)} {N(q[2].Y)} l h f ");
        }
        else
        {
            drawn.Append(RgbOperators(item.Colour, "RG"));
            foreach (var q in quads)
            {
                // Under the letters' feet, or through their middle.
                double along = item.Kind == PdfMarkup.Underline ? 0.12 : 0.55;
                double fromX = q[2].X + (q[0].X - q[2].X) * along, fromY = q[2].Y + (q[0].Y - q[2].Y) * along;
                double toX = q[3].X + (q[1].X - q[3].X) * along, toY = q[3].Y + (q[1].Y - q[3].Y) * along;
                double tall = Math.Sqrt(Math.Pow(q[0].X - q[2].X, 2) + Math.Pow(q[0].Y - q[2].Y, 2));
                drawn.Append($"{N(Math.Max(0.6, tall * 0.07))} w {N(fromX)} {N(fromY)} m {N(toX)} {N(toY)} l S ");
            }
        }
        Appear(document, annotation, rect, drawn.ToString(), resources);
        if (resources is not null)
            annotation.Elements.GetDictionary("/AP")!.Elements.GetDictionary("/N")!.Elements["/Group"] = TransparencyGroup(document);
    }

    private static PdfDictionary TransparencyGroup(PdfDocument document)
    {
        var group = new PdfDictionary(document);
        group.Elements["/S"] = new PdfName("/Transparency");
        return group;
    }

    public static void Note(PdfDocument document, PdfPage page, PdfNoteItem item)
    {
        var space = PdfPageSpace.Of(page);
        var rect = space.UserRect(item.Area);
        var annotation = New(document, page, "/Text", item.Colour, rect);
        annotation.Elements["/Contents"] = new PdfString(item.Text ?? "", PdfStringEncoding.Unicode);
        annotation.Elements["/Name"] = new PdfName("/Comment");
        annotation.Elements["/Open"] = new PdfBoolean(false);
        double w = rect.X2 - rect.X1, h = rect.Y2 - rect.Y1;
        var drawn = new StringBuilder();
        drawn.Append(RgbOperators(item.Colour, "rg")).Append("0.35 0.3 0.1 RG 0.6 w ");
        drawn.Append($"{N(rect.X1 + 0.5)} {N(rect.Y1 + 0.5)} {N(w - 1)} {N(h - 1)} re B ");
        for (int i = 1; i <= 3; i++)
            drawn.Append($"{N(rect.X1 + w * 0.2)} {N(rect.Y1 + h * i / 4)} m {N(rect.X1 + w * 0.8)} {N(rect.Y1 + h * i / 4)} l S ");
        Appear(document, annotation, rect, drawn.ToString());
    }

    public static void Ink(PdfDocument document, PdfPage page, PdfInkItem item)
    {
        var space = PdfPageSpace.Of(page);
        var strokes = item.Strokes.Where(s => s.Count > 0).Select(s => s.Select(p => space.ToUser(p.X, p.Y)).ToList()).ToList();
        if (strokes.Count == 0)
            return;
        var rect = Bounds(strokes.SelectMany(s => s), item.Weight);
        var annotation = New(document, page, "/Ink", item.Colour, rect);
        annotation.Elements["/InkList"] = new PdfArray(document, strokes.Select(s => (PdfItem)Numbers(document, s.SelectMany(p => new[] { p.X, p.Y }))).ToArray());
        annotation.Elements["/BS"] = BorderWidth(document, item.Weight);
        var drawn = new StringBuilder(RgbOperators(item.Colour, "RG")).Append($"{N(item.Weight)} w 1 J 1 j ");
        foreach (var stroke in strokes)
        {
            drawn.Append($"{N(stroke[0].X)} {N(stroke[0].Y)} m ");
            foreach (var point in stroke.Skip(1))
                drawn.Append($"{N(point.X)} {N(point.Y)} l ");
            if (stroke.Count == 1)
                drawn.Append($"{N(stroke[0].X)} {N(stroke[0].Y)} l ");
            drawn.Append("S ");
        }
        Appear(document, annotation, rect, drawn.ToString());
    }

    private static PdfDictionary BorderWidth(PdfDocument document, double weight)
    {
        var border = new PdfDictionary(document);
        border.Elements["/W"] = new PdfReal(weight);
        return border;
    }

    public static void Line(PdfDocument document, PdfPage page, PdfLineItem item)
    {
        var space = PdfPageSpace.Of(page);
        var a = space.ToUser(item.X1, item.Y1);
        var b = space.ToUser(item.X2, item.Y2);
        double head = item.Arrow ? Math.Max(6, item.Weight * 4) : 0;
        var rect = Bounds([a, b], item.Weight + head);
        var annotation = New(document, page, "/Line", item.Colour, rect);
        annotation.Elements["/L"] = Numbers(document, [a.X, a.Y, b.X, b.Y]);
        annotation.Elements["/BS"] = BorderWidth(document, item.Weight);
        if (item.Arrow)
        {
            annotation.Elements["/LE"] = new PdfArray(document, new PdfName("/None"), new PdfName("/ClosedArrow"));
            annotation.Elements["/IC"] = Rgb(document, item.Colour);
        }
        double dx = b.X - a.X, dy = b.Y - a.Y, length = Math.Max(1e-6, Math.Sqrt(dx * dx + dy * dy));
        dx /= length;
        dy /= length;
        var drawn = new StringBuilder(RgbOperators(item.Colour, "RG")).Append(RgbOperators(item.Colour, "rg")).Append($"{N(item.Weight)} w 1 J 1 j ");
        // The line stops inside the head, so its tip stays sharp.
        double stopX = b.X - dx * head * 0.6, stopY = b.Y - dy * head * 0.6;
        drawn.Append($"{N(a.X)} {N(a.Y)} m {N(stopX)} {N(stopY)} l S ");
        if (item.Arrow)
        {
            double backX = b.X - dx * head, backY = b.Y - dy * head, side = head * 0.5;
            drawn.Append($"{N(b.X)} {N(b.Y)} m {N(backX - dy * side)} {N(backY + dx * side)} l {N(backX + dy * side)} {N(backY - dx * side)} l h f ");
        }
        Appear(document, annotation, rect, drawn.ToString());
    }

    public static void Shape(PdfDocument document, PdfPage page, PdfShapeItem item)
    {
        var space = PdfPageSpace.Of(page);
        var area = space.UserRect(item.Area);
        double half = item.Weight / 2;
        var rect = (area.X1 - half, area.Y1 - half, area.X2 + half, area.Y2 + half);
        var annotation = New(document, page, item.Round ? "/Circle" : "/Square", item.Colour, rect);
        annotation.Elements["/BS"] = BorderWidth(document, item.Weight);
        var drawn = new StringBuilder(RgbOperators(item.Colour, "RG")).Append($"{N(item.Weight)} w ");
        if (item.Round)
        {
            const double k = 0.5523;
            double cx = (area.X1 + area.X2) / 2, cy = (area.Y1 + area.Y2) / 2, rx = (area.X2 - area.X1) / 2, ry = (area.Y2 - area.Y1) / 2;
            drawn.Append($"{N(cx + rx)} {N(cy)} m ");
            drawn.Append($"{N(cx + rx)} {N(cy + k * ry)} {N(cx + k * rx)} {N(cy + ry)} {N(cx)} {N(cy + ry)} c ");
            drawn.Append($"{N(cx - k * rx)} {N(cy + ry)} {N(cx - rx)} {N(cy + k * ry)} {N(cx - rx)} {N(cy)} c ");
            drawn.Append($"{N(cx - rx)} {N(cy - k * ry)} {N(cx - k * rx)} {N(cy - ry)} {N(cx)} {N(cy - ry)} c ");
            drawn.Append($"{N(cx + k * rx)} {N(cy - ry)} {N(cx + rx)} {N(cy - k * ry)} {N(cx + rx)} {N(cy)} c h S ");
        }
        else
            drawn.Append($"{N(area.X1)} {N(area.Y1)} {N(area.X2 - area.X1)} {N(area.Y2 - area.Y1)} re S ");
        Appear(document, annotation, rect, drawn.ToString());
    }
}
