using Petal.Engines.Documents.Model;
using Petal.Engines.Documents.Ocr;
using Petal.Engines.Documents.Text;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using PigDocument = UglyToad.PdfPig.PdfDocument;

namespace Petal.Engines.Documents.Pdf;

/// <summary>
/// A word on a page. Coordinates are points from the top-left corner of the page as displayed;
/// Top/Bottom are normalised from the baseline and font size so lines compare reliably.
/// </summary>
internal sealed record PdfWord(string Text, double Left, double Right, double Top, double Bottom, double FontSize, bool Bold, bool Italic, string? FontName)
{
    public string? Link { get; set; }
    public double CenterY => (Top + Bottom) / 2;
    public double Height => Bottom - Top;
    public bool IsRtl => Bidi.ContainsRtl(Text);
    /// <summary>Fill colour as 0xRRGGBB when known.</summary>
    public int? Color { get; init; }
}

internal sealed record PdfPicture(double Left, double Top, double Right, double Bottom, ImageData Image);

internal sealed class PdfPageContent
{
    public int Index { get; init; }
    public double Width { get; init; }
    public double Height { get; init; }
    public List<PdfWord> Words { get; } = [];
    public List<PdfPicture> Pictures { get; } = [];

    /// <summary>The text came from OCR because the page had no text layer.</summary>
    public bool Scanned { get; set; }
}

internal sealed class PdfContentOptions
{
    public bool Ocr { get; init; } = true;
    public IReadOnlyList<string>? OcrLanguages { get; init; }
    public bool Pictures { get; init; } = true;
}

/// <summary>Pulls words (with fonts and positions), pictures and links out of every page.</summary>
internal static class PdfContent
{
    public static async Task<(List<PdfPageContent> Pages, DocMetadata Metadata)> ExtractAsync(
        string path, PdfContentOptions options, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        var pages = new List<PdfPageContent>();
        var metadata = new DocMetadata();
        var needsOcr = new List<int>();

        using (var pdf = PigDocument.Open(bytes, new ParsingOptions { UseLenientParsing = true, SkipMissingFonts = true }))
        {
            var info = pdf.Information;
            metadata.Title = Clean(info.Title);
            metadata.Author = Clean(info.Author);
            metadata.Subject = Clean(info.Subject);
            metadata.Keywords = Clean(info.Keywords);

            var count = pdf.NumberOfPages;
            for (var n = 1; n <= count; n++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Page page;
                try
                {
                    page = pdf.GetPage(n);
                }
                catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
                {
                    // A broken page should not sink the whole document; OCR may still read it.
                    pages.Add(new PdfPageContent { Index = n - 1, Width = 612, Height = 792 });
                    needsOcr.Add(n - 1);
                    continue;
                }
                var content = ReadPage(page, n - 1, options);
                pages.Add(content);
                var letters = content.Words.Sum(w => w.Text.Count(char.IsLetterOrDigit));
                if (letters < 8)
                    needsOcr.Add(n - 1);
                progress?.Report(0.5 * n / count);
            }
        }

        if (options.Ocr && needsOcr.Count > 0 && OcrService.IsAvailable)
        {
            var done = 0;
            foreach (var index in needsOcr)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await OcrPageAsync(bytes, pages[index], options.OcrLanguages, cancellationToken).ConfigureAwait(false);
                progress?.Report(0.5 + 0.5 * ++done / needsOcr.Count);
            }
        }
        progress?.Report(1);
        return (pages, metadata);
    }

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static PdfPageContent ReadPage(Page page, int index, PdfContentOptions options)
    {
        // PdfPig already gives positions on the page as displayed (its /Rotate applied, the
        // visible box's bottom-left at the origin, y up), with Width and Height to match.
        double width = page.Width, height = page.Height;
        var content = new PdfPageContent { Index = index, Width = width, Height = height };

        // Maps a PdfPig point to top-left display coordinates.
        (double X, double Y) Map(double x, double y) => (x, height - y);

        IEnumerable<Word> words;
        try
        {
            words = page.GetWords();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            words = [];
        }

        foreach (var word in words)
        {
            var letters = word.Letters.Where(l => !string.IsNullOrWhiteSpace(l.Value)).ToList();
            if (letters.Count == 0)
                continue;
            // Sideways or upside-down as displayed (a vertical label, say): not part of the flow.
            if (word.TextOrientation is not (TextOrientation.Horizontal or TextOrientation.Other))
                continue;

            var size = Median(letters.Select(l => l.PointSize > 0.5 ? l.PointSize : l.FontSize));
            if (size <= 0.5)
                size = Math.Max(1, word.BoundingBox.Height);

            // Right-to-left words may be stored in visual order; positions tell the logical one.
            var rtl = letters.Count(l => l.Value.Length > 0 && Bidi.IsRtlChar(l.Value[0])) * 2 > letters.Count;
            var ordered = rtl ? letters.OrderByDescending(l => l.StartBaseLine.X).ToList() : letters;
            var text = string.Concat(ordered.Select(l => l.Value));
            if (rtl)
                text = Bidi.NormalizeExtracted(text);
            text = text.Replace('\u00A0', ' ').Trim();
            if (text.Length == 0)
                continue;

            var baseline = height - Median(letters.Select(l => l.StartBaseLine.Y));
            double left = letters.Min(l => Math.Min(l.StartBaseLine.X, l.BoundingBox.Left));
            double right = letters.Max(l => Math.Max(l.EndBaseLine.X, l.BoundingBox.Right));
            double top = baseline - 0.78 * size, bottom = baseline + 0.22 * size;

            var font = letters[0].FontDetails;
            var name = letters[0].FontName ?? font?.Name ?? "";
            var bold = font?.IsBold == true || HasWeight(name);
            var italic = font?.IsItalic == true || name.Contains("Italic", StringComparison.OrdinalIgnoreCase) || name.Contains("Oblique", StringComparison.OrdinalIgnoreCase);
            content.Words.Add(new PdfWord(text, left, right, top, bottom, size, bold, italic, StripSubset(name)) { Color = ColorOf(letters[0]) });
        }

        try
        {
            foreach (var link in page.GetHyperlinks())
            {
                if (string.IsNullOrWhiteSpace(link.Uri))
                    continue;
                var a = Map(link.Bounds.Left, link.Bounds.Bottom);
                var b = Map(link.Bounds.Right, link.Bounds.Top);
                double l = Math.Min(a.X, b.X), r = Math.Max(a.X, b.X), t = Math.Min(a.Y, b.Y), bt = Math.Max(a.Y, b.Y);
                foreach (var w in content.Words)
                {
                    var cx = (w.Left + w.Right) / 2;
                    if (cx >= l && cx <= r && w.CenterY >= t - 1 && w.CenterY <= bt + 1)
                        w.Link = link.Uri;
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Links are a nicety.
        }

        if (options.Pictures)
            ReadPictures(page, content, Map);
        return content;
    }

    private static void ReadPictures(Page page, PdfPageContent content, Func<double, double, (double X, double Y)> map)
    {
        IEnumerable<IPdfImage> images;
        try
        {
            images = page.GetImages().ToList();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return;
        }
        foreach (var image in images)
        {
            try
            {
                if (image.IsImageMask || image.WidthInSamples < 8 || image.HeightInSamples < 8)
                    continue;
                var a = map(image.BoundingBox.Left, image.BoundingBox.Bottom);
                var b = map(image.BoundingBox.Right, image.BoundingBox.Top);
                double l = Math.Min(a.X, b.X), r = Math.Max(a.X, b.X), t = Math.Min(a.Y, b.Y), bt = Math.Max(a.Y, b.Y);
                if (r - l < 16 || bt - t < 16)
                    continue;
                byte[]? bytes = null;
                var raw = image.RawBytes.ToArray();
                if (Util.ImageTools.IsJpeg(raw) && IsDct(image))
                    bytes = raw;
                else if (image.TryGetPng(out var png))
                    bytes = png;
                if (bytes is null || ImageData.Create(bytes) is not { } data)
                    continue;
                data.WidthPt = r - l;
                data.HeightPt = bt - t;
                content.Pictures.Add(new PdfPicture(l, t, r, bt, data));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Unsupported colour spaces or filters: skip the picture.
            }
        }
    }

    private static bool IsDct(IPdfImage image)
    {
        var filter = image.ImageDictionary?.Data.TryGetValue("Filter", out var f) == true ? f?.ToString() ?? "" : "";
        return filter.Contains("DCT", StringComparison.OrdinalIgnoreCase) && !filter.Contains("Flate", StringComparison.OrdinalIgnoreCase);
    }

    private static int? ColorOf(Letter letter)
    {
        try
        {
            var (r, g, b) = letter.Color?.ToRGBValues() ?? (0, 0, 0);
            return ((int)Math.Round(r * 255) << 16) | ((int)Math.Round(g * 255) << 8) | (int)Math.Round(b * 255);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }

    private static bool HasWeight(string fontName) =>
        fontName.Contains("Bold", StringComparison.OrdinalIgnoreCase) || fontName.Contains("Black", StringComparison.OrdinalIgnoreCase) ||
        fontName.Contains("Heavy", StringComparison.OrdinalIgnoreCase) || fontName.Contains("Semibold", StringComparison.OrdinalIgnoreCase) ||
        fontName.Contains("Demi", StringComparison.OrdinalIgnoreCase) || fontName.EndsWith(",B", StringComparison.Ordinal);

    /// <summary>"ABCDEF+Calibri-Bold" → "Calibri-Bold".</summary>
    internal static string StripSubset(string name)
    {
        var plus = name.IndexOf('+');
        return plus == 6 ? name[(plus + 1)..] : name;
    }

    private static double Median(IEnumerable<double> values)
    {
        var list = values.Order().ToList();
        return list.Count == 0 ? 0 : list[list.Count / 2];
    }

    /// <summary>Renders the page and reads it with Windows OCR, replacing whatever text was found.</summary>
    private static async Task OcrPageAsync(byte[] pdf, PdfPageContent page, IReadOnlyList<string>? languages, CancellationToken cancellationToken)
    {
        // About 300 DPI, but within the recogniser's size limit.
        var maxSide = Math.Min(OcrService.MaxImageDimension, 4200);
        var dpi = Math.Min(300, maxSide / (Math.Max(page.Width, page.Height) / 72));
        SkiaSharp.SKBitmap bitmap;
        try
        {
            bitmap = PdfRaster.Render(pdf, page.Index, (int)Math.Max(72, dpi));
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            return;
        }
        using (bitmap)
        {
            var words = await OcrService.RecognizeAsync(bitmap, languages, cancellationToken).ConfigureAwait(false);
            if (words.Count == 0)
                return;
            var scale = bitmap.Width / page.Width;
            page.Words.Clear();
            page.Pictures.Clear();
            page.Scanned = true;
            // A line's font size is estimated from its tallest word (ink height is ~0.9 em with descenders).
            foreach (var line in words.GroupBy(w => w.Line))
            {
                var size = line.Max(w => w.Height) / scale / 0.9;
                var top = line.Min(w => w.Y) / scale;
                var bottom = line.Max(w => w.Y + w.Height) / scale;
                var center = (top + bottom) / 2;
                foreach (var w in line)
                {
                    var left = w.X / scale;
                    page.Words.Add(new PdfWord(w.Text, left, left + w.Width / scale, center - size / 2, center + size / 2, size, false, false, null));
                }
            }
        }
    }
}
