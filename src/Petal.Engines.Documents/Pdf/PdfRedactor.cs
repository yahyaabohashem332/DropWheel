using System.Text;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;
using Petal.Engines.Documents.Ocr;
using SkiaSharp;
using UglyToad.PdfPig;
using PdfDocument = PdfSharp.Pdf.PdfDocument;
using PigDocument = UglyToad.PdfPig.PdfDocument;

namespace Petal.Engines.Documents.Pdf;

/// <summary>
/// True redaction. Whatever lies under a box leaves the file; it isn't covered. A page keeps its vector content when only letters
/// lie under its boxes: they come out of its content stream, and a black box is drawn where they were. Otherwise (a picture
/// or a drawing under a box, text in a Form XObject, a font that can't be followed) the page is redrawn as a picture with the boxes
/// burned into its pixels before it is encoded, over an invisible text layer holding only the words outside the boxes. Annotations
/// and fields on the area go, and the same words go from the file's other places too. Before anything is written PdfPig
/// reads the result: a letter left in a box stops the save.
/// </summary>
internal static class PdfRedactor
{
    /// <summary>How sharp a page redrawn as a picture is.</summary>
    private const int PictureDpi = 200;

    /// <summary>Shorter than this, a word under a box tells nothing if it turns up in a comment or a title, and is often an everyday word.</summary>
    private const int ShortestWord = 4;

    /// <summary>Returns how many pages had to become pictures.</summary>
    public static int Apply(ref byte[] pdf, IReadOnlyList<PdfRedactItem> redactions, IReadOnlyList<string> secrets, bool removeHidden, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var boxes = redactions.Where(r => r.Area.Width > 0 && r.Area.Height > 0).GroupBy(r => r.Page).ToDictionary(g => g.Key, g => g.Select(r => r.Area).ToList());
        var words = WordsUnder(pdf, boxes).Where(w => w.Length >= ShortestWord).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var phrases = secrets.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var (erased, failed) = PdfContentEraser.Erase(pdf, boxes, EraseMode.Everything, cancellationToken);
        progress?.Report(0.3);

        using var document = PdfSharpIO.Open(erased, PdfDocumentOpenMode.Modify);
        int pictures = failed.Count(p => p >= 0 && p < document.PageCount);
        int done = 0;
        foreach (var (index, areas) in boxes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (index < 0 || index >= document.PageCount)
                continue;
            var page = document.Pages[index];
            RemoveAnnotations(document, page, areas);
            page.Elements.Remove("/Thumb");
            page.Elements.Remove("/PieceInfo");
            page.Elements.Remove("/Metadata");
            if (failed.Contains(index))
                RedrawAsPicture(page, erased, index, areas);
            else
                Burn(page, areas);
            progress?.Report(0.3 + 0.5 * ++done / Math.Max(1, boxes.Count));
        }

        // What repeats the words elsewhere in the file: reading aids, form data kept as XFA, and any string that holds them.
        var catalog = document.Internals.Catalog.Elements;
        catalog.Remove("/StructTreeRoot");
        catalog.Remove("/MarkInfo");
        catalog.GetDictionary("/AcroForm")?.Elements.Remove("/XFA");
        foreach (PdfPage page in document.Pages)
            page.Elements.Remove("/StructParents");
        var needles = words.Concat(phrases).ToList();
        if (needles.Count > 0)
            Purge(document, needles, phrases);
        if (removeHidden)
            RemoveHiddenDetails(document);
        cancellationToken.ThrowIfCancellationRequested();

        var bytes = PdfSharpIO.SaveToBytes(document);
        if (removeHidden)
            bytes = PdfSharpIO.BlankWriterComments(PdfSharpIO.Finish(bytes, [], PdfSharpIO.XmpAction.Remove));
        Check(bytes, boxes);
        pdf = bytes;
        progress?.Report(1);
        return pictures;
    }

    /// <summary>The words PdfPig reads under the boxes: the things to look for in the file's other places.</summary>
    private static IEnumerable<string> WordsUnder(byte[] pdf, Dictionary<int, List<PdfArea>> boxes)
    {
        var found = new List<string>();
        try
        {
            using var pig = PigDocument.Open(pdf, new ParsingOptions { UseLenientParsing = true, SkipMissingFonts = true });
            foreach (var (index, areas) in boxes)
            {
                if (index < 0 || index >= pig.NumberOfPages)
                    continue;
                var page = pig.GetPage(index + 1);
                foreach (var word in page.GetWords())
                    if (word.Letters.Any(l => PlacedLetter.AreaOf(l, page.Height) is var area && areas.Any(a => a.Overlaps(area))))
                        found.Add(word.Text);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
        {
            // Unreadable to PdfPig: the check at the end stops the save if it matters.
        }
        return found;
    }

    private static void Burn(PdfPage page, List<PdfArea> areas)
    {
        using var gfx = PdfStamper.Upright(page);
        foreach (var area in areas)
            gfx.DrawRectangle(XBrushes.Black, area.Left, area.Top, area.Width, area.Height);
    }

    /// <summary>
    /// The page as a picture with the boxes burned in: its pixels under them are black before they are ever encoded. All the old
    /// content goes with its resources, so no image or font of it is left to pull the words out of.
    /// </summary>
    private static void RedrawAsPicture(PdfPage page, byte[] pdf, int index, List<PdfArea> areas)
    {
        var text = ReadLayer(pdf, index, areas);
        var (width, height) = PdfStamper.DisplayedSize(page);
        byte[] picture;
        using (var bitmap = PdfRaster.RenderContentOnly(pdf, index, PictureDpi))
        {
            using (var canvas = new SKCanvas(bitmap))
            using (var black = new SKPaint { Color = SKColors.Black, IsAntialias = false, Style = SKPaintStyle.Fill })
            {
                double sx = bitmap.Width / width, sy = bitmap.Height / height;
                foreach (var area in areas)
                    canvas.DrawRect(SKRect.Create((float)Math.Floor(area.Left * sx) - 1, (float)Math.Floor(area.Top * sy) - 1, (float)Math.Ceiling(area.Width * sx) + 2, (float)Math.Ceiling(area.Height * sy) + 2), black);
            }
            picture = Encode(bitmap);
        }
        PdfWalk.SetContent(page, []);
        // Replaced, not emptied: other pages may share the old ones.
        page.Elements.Remove("/Resources");
        using (var gfx = PdfStamper.Upright(page))
        using (var image = XImage.FromStream(new MemoryStream(picture)))
            gfx.DrawImage(image, 0, 0, width, height);
        if (text is { Lines.Count: > 0 })
            SearchablePdf.AddTextLayer(page, text);
    }

    /// <summary>Smallest wins: PNG for pages of text, JPEG for pages of photographs.</summary>
    private static byte[] Encode(SKBitmap bitmap)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var png = image.Encode(SKEncodedImageFormat.Png, 100);
        using var jpeg = image.Encode(SKEncodedImageFormat.Jpeg, 92);
        return (jpeg.Size < png.Size ? jpeg : png).ToArray();
    }

    /// <summary>The words clear of the boxes, as lines for the invisible layer. A word only partly under a box stays out whole.</summary>
    private static SearchablePdf.PageText? ReadLayer(byte[] pdf, int index, List<PdfArea> areas)
    {
        try
        {
            using var pig = PigDocument.Open(pdf, new ParsingOptions { UseLenientParsing = true, SkipMissingFonts = true });
            var content = PdfTextReader.Horizontal(pig.GetPage(index + 1), index);
            var clear = content.Words.Where(w => !areas.Any(a => a.Overlaps(new PdfArea(w.Left, w.Top, w.Right - w.Left, w.Height)))).ToList();
            if (clear.Count == 0)
                return null;
            var lines = new List<List<OcrWordBox>>();
            foreach (var row in PdfLayout.BuildRows(content, clear))
            {
                foreach (var segment in row.Segments)
                {
                    int number = lines.Count;
                    lines.Add(segment.Words.Select(w => new OcrWordBox(w.Text, w.Left, w.Top, w.Right - w.Left, w.Height, number)).ToList());
                }
            }
            return new SearchablePdf.PageText(content.Width, content.Height, lines, 1);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>Annotations and fields on the area leave the file, with any popup that belongs to them.</summary>
    private static void RemoveAnnotations(PdfDocument document, PdfPage page, List<PdfArea> areas)
    {
        if (page.Elements.GetArray("/Annots") is not { } annots)
            return;
        var space = PdfPageSpace.Of(page);
        var gone = new List<PdfDictionary>();
        foreach (var item in annots.Elements)
        {
            if (PdfWalk.Dictionary(item) is not { } annotation || PdfWalk.Rectangle(annotation.Elements["/Rect"]) is not { } rect)
                continue;
            var area = space.AreaOf(rect.X1, rect.Y1, rect.X2, rect.Y2);
            if (areas.Any(a => a.Overlaps(area)))
                gone.Add(annotation);
        }
        foreach (var annotation in gone)
            RemoveAnnotation(document, annots, annotation);
    }

    private static void RemoveAnnotation(PdfDocument document, PdfArray annots, PdfDictionary annotation)
    {
        var popup = annotation.Elements.GetDictionary("/Popup");
        for (int i = annots.Elements.Count - 1; i >= 0; i--)
        {
            var item = PdfWalk.Dictionary(annots.Elements[i]);
            if (ReferenceEquals(item, annotation) || (popup is not null && ReferenceEquals(item, popup)))
                annots.Elements.RemoveAt(i);
        }
        if (annotation.Elements.GetName("/Subtype") == "/Widget")
            PdfForms.RemoveWidget(document, annotation);
    }

    /// <summary>
    /// The words out of every string in the file, wherever it lies: comments, bookmarks, field values, document details.
    /// An annotation or a field whose words hold them goes altogether, for its appearance shows them too; a phrase the person named also goes
    /// from a field's name, and from streams of plain words (XMP, XFA).
    /// </summary>
    private static void Purge(PdfDocument document, List<string> needles, List<string> phrases)
    {
        bool Holds(string? text, IEnumerable<string> what) => what.Any(n => PdfTextReader.Holds(text, n));
        foreach (PdfPage page in document.Pages)
        {
            if (page.Elements.GetArray("/Annots") is not { } annots)
                continue;
            foreach (var annotation in annots.Elements.Select(PdfWalk.Dictionary).OfType<PdfDictionary>().ToList())
            {
                bool holds = Holds(PdfWalk.Text(annotation.Elements["/Contents"]), needles) || Holds(PdfWalk.Text(annotation.Elements["/RC"]), needles)
                    || Holds(PdfWalk.Text(annotation.Elements["/V"]), needles) || Holds(PdfWalk.Text(annotation.Elements["/DV"]), needles)
                    || (annotation.Elements.GetDictionary("/Parent") is { } parent && (Holds(PdfWalk.Text(parent.Elements["/V"]), needles) || Holds(PdfWalk.Text(parent.Elements["/DV"]), needles)));
                if (holds)
                    RemoveAnnotation(document, annots, annotation);
            }
        }
        var visited = new HashSet<PdfObject>(ReferenceEqualityComparer.Instance);
        int renamed = 0;
        void Strings(PdfItem? item, Action<PdfItem?> replace, bool isName)
        {
            switch (item)
            {
                case PdfString text when Holds(text.Value, needles) && !(isName && !Holds(text.Value, phrases)):
                    replace(isName ? new PdfString("Field" + ++renamed) : new PdfString(""));
                    break;
                case PdfStringObject text when Holds(text.Value, needles) && !(isName && !Holds(text.Value, phrases)):
                    text.Value = isName ? "Field" + ++renamed : "";
                    break;
                case PdfDictionary dictionary when visited.Add(dictionary):
                    foreach (var key in dictionary.Elements.Keys.ToList())
                        Strings(dictionary.Elements[key], value => dictionary.Elements[key] = value!, key == "/T");
                    if (dictionary.Stream is { } stream && IsPlainWords(dictionary) && Holds(Encoding.UTF8.GetString(stream.UnfilteredValue), needles))
                        dictionary.Stream.Value = [];
                    break;
                case PdfArray array when visited.Add(array):
                    for (int i = 0; i < array.Elements.Count; i++)
                    {
                        int at = i;
                        Strings(array.Elements[at], value => array.Elements[at] = value!, false);
                    }
                    break;
            }
        }
        foreach (var obj in document.Internals.GetAllObjects())
            Strings(obj, _ => { }, false);
        Strings(document.Info, _ => { }, false);
    }

    private static bool IsPlainWords(PdfDictionary dictionary) =>
        dictionary.Elements.GetName("/Type") == "/Metadata" || dictionary.Elements.GetName("/Subtype") == "/XML";

    /// <summary>What a file says about itself without showing it: its details, XMP, attachments, scripts and private application data.</summary>
    internal static void RemoveHiddenDetails(PdfDocument document)
    {
        PdfMetadataEditor.StripMetadata(document);
        var catalog = document.Internals.Catalog.Elements;
        catalog.Remove("/OpenAction");
        catalog.Remove("/AA");
        catalog.Remove("/SpiderInfo");
        catalog.Remove("/Collection");
        if (catalog.GetDictionary("/Names") is { } names)
        {
            names.Elements.Remove("/EmbeddedFiles");
            names.Elements.Remove("/JavaScript");
        }
        catalog.Remove("/AF");
        foreach (PdfPage page in document.Pages)
        {
            page.Elements.Remove("/AA");
            page.Elements.Remove("/Thumb");
        }
    }

    /// <summary>The last word: nothing is saved with a letter still in a box.</summary>
    private static void Check(byte[] pdf, Dictionary<int, List<PdfArea>> boxes)
    {
        using var pig = PigDocument.Open(pdf, new ParsingOptions { UseLenientParsing = true, SkipMissingFonts = true });
        foreach (var (index, areas) in boxes)
        {
            if (index < 0 || index >= pig.NumberOfPages)
                continue;
            if (PlacedLetter.Of(pig.GetPage(index + 1)).Any(l => l.IsIn(areas)))
                throw new InvalidOperationException("A page could not be redacted reliably, so nothing was saved.");
        }
    }
}

/// <summary>A page's own words with their places, for finding them and for retyping them.</summary>
internal static class PdfTextReader
{
    /// <summary>One line of a page as a string with a rectangle for each of its characters.</summary>
    internal sealed record TextRun(string Text, PdfArea[] Places);

    /// <summary>The words that run across the page as displayed, as <see cref="PdfContent"/> reads them.</summary>
    public static PdfPageContent Horizontal(UglyToad.PdfPig.Content.Page page, int index) => PdfContent.ReadPageWords(page, index);

    public static List<PdfTextLine> Lines(PdfPageContent page)
    {
        var lines = new List<PdfTextLine>();
        if (page.Words.Count == 0)
            return lines;
        foreach (var row in PdfLayout.BuildRows(page))
        {
            foreach (var segment in row.Segments)
            {
                var words = segment.Ordered.Select(w => new PdfTextWord(w.Text, new PdfArea(w.Left, w.Top, w.Right - w.Left, w.Height))).ToList();
                var colour = segment.Words.Where(w => w.Color is not null).GroupBy(w => w.Color).OrderByDescending(g => g.Sum(w => w.Text.Length)).FirstOrDefault()?.Key ?? 0;
                lines.Add(new PdfTextLine(segment.Text, PdfArea.Between(segment.Left, segment.Top, segment.Right, segment.Bottom), words, segment.FontSize, segment.Bold, segment.Italic, segment.Rtl, colour,
                    segment.Words.FirstOrDefault(w => w.FontName is not null)?.FontName));
            }
        }
        return lines;
    }

    /// <summary>
    /// Every line on a page, sideways ones too, for searching: nothing readable may be missed by a search for what is to go.
    /// A character's place is its letter's when letters and characters correspond, and its whole word's when they don't (Arabic ligatures).
    /// </summary>
    public static List<TextRun> Runs(UglyToad.PdfPig.Content.Page page, int index)
    {
        var runs = new List<TextRun>();
        var content = Horizontal(page, index);
        var letters = PlacedLetter.Of(page);
        foreach (var row in PdfLayout.BuildRows(content))
        {
            foreach (var segment in row.Segments)
            {
                var text = new StringBuilder();
                var places = new List<PdfArea>();
                foreach (var word in segment.Ordered)
                {
                    if (text.Length > 0)
                    {
                        text.Append(' ');
                        places.Add(default);
                    }
                    var area = new PdfArea(word.Left, word.Top, word.Right - word.Left, word.Height);
                    var own = letters.Where(l => area.Inflated(0.5).Contains(l.Area.Left + l.Area.Width / 2, l.Area.Top + l.Area.Height / 2)).ToList();
                    if (own.Count == word.Text.Length && !word.IsRtl)
                        places.AddRange(own.OrderBy(l => l.Area.Left).Select(l => l.Area));
                    else
                        places.AddRange(Enumerable.Repeat(area, word.Text.Length));
                    text.Append(word.Text);
                }
                runs.Add(new TextRun(text.ToString(), places.ToArray()));
            }
        }
        // Sideways and upside-down words, which the layout leaves out.
        foreach (var word in page.GetWords())
        {
            if (word.TextOrientation is UglyToad.PdfPig.Content.TextOrientation.Horizontal or UglyToad.PdfPig.Content.TextOrientation.Other)
                continue;
            var areas = word.Letters.Where(l => !string.IsNullOrWhiteSpace(l.Value)).Select(l => PlacedLetter.AreaOf(l, page.Height)).ToList();
            if (areas.Count == 0)
                continue;
            var box = areas.Aggregate((a, b) => a.Union(b));
            runs.Add(new TextRun(word.Text, Enumerable.Repeat(box, word.Text.Length).ToArray()));
        }
        return runs;
    }

    /// <summary>A scanned page read with OCR: its words as runs, whole words as the places.</summary>
    public static async Task<List<TextRun>> ReadScanAsync(byte[] pdf, int index, double pageWidth, CancellationToken cancellationToken)
    {
        var runs = new List<TextRun>();
        var size = PdfRaster.PageSizes(pdf)[index];
        var maxSide = Math.Min(OcrService.MaxImageDimension, 4200);
        var dpi = (int)Math.Max(72, Math.Min(300, maxSide / (Math.Max(size.Width, size.Height) / 72)));
        using var bitmap = PdfRaster.Render(pdf, index, dpi, annotations: false);
        var words = await OcrService.RecognizeAsync(bitmap, null, cancellationToken).ConfigureAwait(false);
        double scale = bitmap.Width / Math.Max(1, pageWidth);
        foreach (var line in words.Where(w => !string.IsNullOrWhiteSpace(w.Text)).GroupBy(w => w.Line))
        {
            var text = new StringBuilder();
            var places = new List<PdfArea>();
            foreach (var word in line)
            {
                if (text.Length > 0)
                {
                    text.Append(' ');
                    places.Add(default);
                }
                places.AddRange(Enumerable.Repeat(new PdfArea(word.X / scale, word.Y / scale, word.Width / scale, word.Height / scale), word.Text.Length));
                text.Append(word.Text);
            }
            runs.Add(new TextRun(text.ToString(), places.ToArray()));
        }
        return runs;
    }

    /// <summary>The rectangles, one per line, of a stretch of a run's characters.</summary>
    public static PdfArea? AreaOf(TextRun run, int start, int length)
    {
        PdfArea? area = null;
        for (int i = start; i < start + length && i < run.Places.Length; i++)
        {
            var place = run.Places[i];
            if (place.Width <= 0)
                continue;
            area = area is { } so ? so.Union(place) : place;
        }
        return area;
    }

    /// <summary>
    /// A string as a search sees it, for nothing to escape it by how it was written or stored. Many PDFs give Persian letters for
    /// Arabic ones that look the same when joined, so letters that differ only by language count as one, with hamza seats and
    /// diacritics not counted at all.
    /// </summary>
    internal static string Folded(string text)
    {
        var folded = new StringBuilder(text.Length);
        foreach (var c in text.Normalize(NormalizationForm.FormKC).ToLowerInvariant())
        {
            switch (c)
            {
                case (char)0x06CC or (char)0x0649 or (char)0x06D2 or (char)0x0626:
                    folded.Append((char)0x064A);
                    break;
                case (char)0x06A9 or (char)0x06AA:
                    folded.Append((char)0x0643);
                    break;
                case (char)0x06BE or (char)0x06C1 or (char)0x06D5:
                    folded.Append((char)0x0647);
                    break;
                case (char)0x0622 or (char)0x0623 or (char)0x0625:
                    folded.Append((char)0x0627);
                    break;
                case (char)0x0624:
                    folded.Append((char)0x0648);
                    break;
                case (char)0x0640 or (>= (char)0x064B and <= (char)0x065F) or (char)0x0670 or (char)0x200C or (char)0x200D or (char)0x200E or (char)0x200F:
                    break;
                default:
                    folded.Append(c);
                    break;
            }
        }
        return folded.ToString();
    }

    /// <summary>Whether a string holds a phrase, as a search sees them.</summary>
    internal static bool Holds(string? text, string phrase) => text is not null && Folded(text).Contains(Folded(phrase), StringComparison.Ordinal);

    /// <summary>
    /// Where a phrase is, whatever spacing its letters are set with and across line ends: spaces count for nothing on either side.
    /// One rectangle per line it covers.
    /// </summary>
    public static List<(PdfArea Area, string Text)> Find(List<TextRun> runs, string phrase)
    {
        var found = new List<(PdfArea, string)>();
        var needle = new string(Folded(phrase).Where(c => !char.IsWhiteSpace(c)).ToArray());
        if (needle.Length == 0)
            return found;
        var letters = new StringBuilder();
        var places = new List<(int Run, int At)>();
        for (int r = 0; r < runs.Count; r++)
        {
            for (int i = 0; i < runs[r].Text.Length; i++)
            {
                var c = runs[r].Text[i];
                if (char.IsWhiteSpace(c))
                    continue;
                // One character at a time, so positions stay; a ligature that folds to two letters keeps both at its place.
                foreach (var folded in Folded(c.ToString()))
                {
                    letters.Append(folded);
                    places.Add((r, i));
                }
            }
        }
        var haystack = letters.ToString();
        for (int at = haystack.IndexOf(needle, StringComparison.Ordinal); at >= 0; at = haystack.IndexOf(needle, at + 1, StringComparison.Ordinal))
        {
            foreach (var group in Enumerable.Range(at, needle.Length).Select(i => places[i]).GroupBy(p => p.Run))
            {
                int first = group.Min(p => p.At), last = group.Max(p => p.At);
                if (AreaOf(runs[group.Key], first, last - first + 1) is { } area)
                    found.Add((area, runs[group.Key].Text.Substring(first, Math.Min(last - first + 1, runs[group.Key].Text.Length - first))));
            }
        }
        return found;
    }
}
