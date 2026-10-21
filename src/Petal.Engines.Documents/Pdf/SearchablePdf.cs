using System.Text;
using System.Text.RegularExpressions;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using Petal.Engines.Documents.Ocr;
using Petal.Engines.Documents.Text;
using UglyToad.PdfPig;
using PigDocument = UglyToad.PdfPig.PdfDocument;

namespace Petal.Engines.Documents.Pdf;

public sealed record SearchablePdfOptions
{
    /// <summary>OCR languages (BCP-47 tags); null tries every installed one.</summary>
    public IReadOnlyList<string>? Languages { get; init; }
}

/// <param name="PagesRead">Pages that were read with OCR.</param>
/// <param name="PagesWithText">Pages left alone because they already had text.</param>
/// <param name="Words">Words added to the text layer.</param>
/// <param name="Written">False when every page already had text, so nothing was saved.</param>
public sealed record SearchablePdfResult(int PagesRead, int PagesWithText, int Words, bool Written);

/// <summary>
/// Makes scanned PDFs searchable: each page without text is read with Windows OCR and gets an
/// invisible text layer (text render mode 3) exactly over the words, the way scanners and
/// Acrobat do it. The page itself is not changed; search, selection and copy start to work.
/// </summary>
public static partial class SearchablePdf
{
    /// <summary>A page with fewer letters than this counts as a scan.</summary>
    private const int TextLetters = 8;

    public static bool IsAvailable => OcrService.IsAvailable;

    public static async Task<SearchablePdfResult> MakeAsync(string input, string output, SearchablePdfOptions? options = null,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        options ??= new SearchablePdfOptions();
        if (!OcrService.IsAvailable)
            throw new InvalidOperationException("No Windows OCR language is installed. Add one under Settings › Time & language › Language.");
        var bytes = await File.ReadAllBytesAsync(input, cancellationToken).ConfigureAwait(false);
        // Protected files stop here with a clear message, before any OCR is spent on them.
        using var document = PdfSharpIO.Open(bytes, PdfDocumentOpenMode.Modify, null, Path.GetFileName(input));

        var scans = PagesWithoutText(bytes, document.PageCount);
        int read = 0, words = 0;
        for (int i = 0; i < scans.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var found = await ReadAsync(bytes, scans[i], options.Languages, cancellationToken).ConfigureAwait(false);
            if (found is { Lines.Count: > 0 })
            {
                words += AddTextLayer(document.Pages[scans[i]], found);
                read++;
            }
            progress?.Report((i + 1.0) / Math.Max(1, scans.Count) * 0.97);
        }
        if (scans.Count == 0)
        {
            progress?.Report(1);
            return new SearchablePdfResult(0, document.PageCount, 0, false);
        }
        int withText = document.PageCount - scans.Count;
        PdfSharpIO.Save(document, output);
        progress?.Report(1);
        return new SearchablePdfResult(read, withText, words, true);
    }

    /// <summary>Indexes of pages with (almost) no text of their own.</summary>
    internal static List<int> PagesWithoutText(byte[] pdf, int pageCount)
    {
        var scans = new List<int>();
        try
        {
            using var pig = PigDocument.Open(pdf, new ParsingOptions { UseLenientParsing = true, SkipMissingFonts = true });
            for (int n = 1; n <= pig.NumberOfPages; n++)
            {
                int letters;
                try
                {
                    letters = pig.GetPage(n).Letters.Count(l => l.Value.Any(char.IsLetterOrDigit));
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    letters = 0;
                }
                if (letters < TextLetters)
                    scans.Add(n - 1);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // PdfPig couldn't read it at all; PDFsharp and PDFium could, so read every page.
            return Enumerable.Range(0, pageCount).ToList();
        }
        return scans;
    }

    /// <summary>OCR results for one page, in points on the page as displayed.</summary>
    internal sealed record PageText(double Width, double Height, List<List<OcrWordBox>> Lines, double Scale);

    private static async Task<PageText?> ReadAsync(byte[] pdf, int index, IReadOnlyList<string>? languages, CancellationToken cancellationToken)
    {
        var size = PdfRaster.PageSizes(pdf)[index];
        // About 300 DPI, within what the recogniser accepts.
        var maxSide = Math.Min(OcrService.MaxImageDimension, 4200);
        var dpi = (int)Math.Max(72, Math.Min(300, maxSide / (Math.Max(size.Width, size.Height) / 72)));
        SkiaSharp.SKBitmap bitmap;
        try
        {
            bitmap = PdfRaster.Render(pdf, index, dpi, annotations: false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            return null;
        }
        using (bitmap)
        {
            var words = await OcrService.RecognizeAsync(bitmap, languages, cancellationToken).ConfigureAwait(false);
            var lines = words.Where(w => !string.IsNullOrWhiteSpace(w.Text) && w.Width > 0 && w.Height > 0)
                .GroupBy(w => w.Line).Select(g => g.ToList()).ToList();
            return new PageText(size.Width, size.Height, lines, bitmap.Width / size.Width);
        }
    }

    /// <summary>Draws the words invisibly over the page; returns how many were added.</summary>
    internal static int AddTextLayer(PdfPage page, PageText text)
    {
        PdfFonts.EnsureInitialized();
        var (width, height) = PdfStamper.DisplayedSize(page);
        // PDFium and PDFsharp agree on the displayed size; if not, stretch to fit.
        double sx = text.Scale * text.Width / width, sy = text.Scale * text.Height / height;
        int added = 0;
        using (var gfx = PdfStamper.Upright(page))
        {
            foreach (var line in text.Lines)
            {
                bool rtl = line.Count(w => Bidi.ContainsRtl(w.Text)) * 2 > line.Count;
                double top = line.Min(w => w.Y) / sy, bottom = line.Max(w => w.Y + w.Height) / sy;
                // Reading order, so copied text and phrase search follow the line.
                foreach (var word in rtl ? line.OrderByDescending(w => w.X) : line.OrderBy(w => w.X))
                {
                    var font = FontFor(word.Text, bottom - top);
                    var glyphs = Bidi.Reorder(word.Text, rtlParagraph: Bidi.ContainsRtl(word.Text));
                    var measured = gfx.MeasureString(glyphs, font.Font).Width;
                    if (measured <= 0)
                        continue;
                    var state = gfx.Save();
                    gfx.TranslateTransform(word.X / sx, bottom - font.Descent);
                    gfx.ScaleTransform(word.Width / sx / measured, 1);
                    gfx.DrawString(glyphs, font.Font, XBrushes.Black, 0, 0);
                    gfx.Restore(state);
                    added++;
                }
            }
        }
        MakeTextInvisible(page);
        return added;
    }

    private sealed record LayerFont(XFont Font, double Descent);

    /// <summary>A font with the word's script, sized so its ascent and descent span the line.</summary>
    private static LayerFont FontFor(string word, double lineHeight)
    {
        var family = PdfFonts.FallbackFor(word) ?? PdfFonts.Arabic;
        var probe = new XFont(family, 100, XFontStyleEx.Regular, new XPdfFontOptions(PdfFontEncoding.Unicode));
        var metrics = probe.Metrics;
        double em = Math.Max(1, metrics.UnitsPerEm), ascent = Math.Abs(metrics.Ascent) / em, descent = Math.Abs(metrics.Descent) / em;
        var size = Math.Clamp(lineHeight / Math.Max(0.5, ascent + descent), 1, 600);
        return new LayerFont(new XFont(family, size, XFontStyleEx.Regular, new XPdfFontOptions(PdfFontEncoding.Unicode)), descent * size);
    }

    [GeneratedRegex(@"(?<=^|[\r\n\t ])BT(?=[\r\n\t ])")]
    private static partial Regex BeginText();

    /// <summary>
    /// PDFsharp has no invisible text, so its text objects in the content just appended get
    /// render mode 3: present for search and selection, never painted or printed.
    /// </summary>
    private static void MakeTextInvisible(PdfPage page)
    {
        PdfSharp.Pdf.Advanced.PdfContent? appended = null;
        foreach (var content in page.Contents)
            appended = content;
        if (appended?.Stream is null)
            return;
        appended.Stream.TryUnfilter();
        var operators = Encoding.Latin1.GetString(appended.Stream.Value);
        var invisible = BeginText().Replace(operators, "BT\n3 Tr");
        if (invisible != operators)
            appended.Stream.Value = Encoding.Latin1.GetBytes(invisible);
    }
}
