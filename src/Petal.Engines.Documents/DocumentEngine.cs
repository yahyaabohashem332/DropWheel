using Petal.Core.Jobs;
using Petal.Engines.Documents.Office;
using Petal.Engines.Documents.Pdf;
using Petal.Engines.Documents.Ocr;

namespace Petal.Engines.Documents;

/// <summary>Entry point for the app: registers the document converters and reports what this machine can do.</summary>
public static class DocumentEngine
{
    /// <summary>The document converters, in registration order (Illustrator and EPS to PDF first).</summary>
    public static IReadOnlyList<IConverter> CreateConverters(DocumentEngineOptions? options = null)
    {
        IllustratorPdf.Register();
        return
        [
            new DesignPdfConverter(),
            new OfficePackageConverter(),
            new EbookConverter(),
            new PdfConverter(options),
            new DocumentConverter(options),
            new SpreadsheetConverter(options),
            new SlidesConverter(options),
            new Data.DataConverter(),
        ];
    }

    public static void Register(ConverterRegistry registry, DocumentEngineOptions? options = null)
    {
        foreach (var converter in CreateConverters(options))
            registry.Register(converter);
    }

    /// <summary>
    /// Loads PDFium and its map of the installed fonts by drawing a tiny page of text in a font the
    /// file doesn't carry; otherwise the first PDF page anywhere in the app waits about five seconds.
    /// Best effort, from any thread.
    /// </summary>
    public static void WarmUp()
    {
        const string page = "BT /F1 24 Tf 10 40 Td (Petal) Tj ET";
        var pdf = System.Text.Encoding.ASCII.GetBytes(
            "%PDF-1.4\n1 0 obj<</Type/Catalog/Pages 2 0 R>>endobj\n2 0 obj<</Type/Pages/Kids[3 0 R]/Count 1>>endobj\n" +
            "3 0 obj<</Type/Page/Parent 2 0 R/MediaBox[0 0 200 100]/Resources<</Font<</F1 4 0 R>>>>/Contents 5 0 R>>endobj\n" +
            "4 0 obj<</Type/Font/Subtype/Type1/BaseFont/Helvetica>>endobj\n" +
            $"5 0 obj<</Length {page.Length}>>stream\n{page}\nendstream endobj\ntrailer<</Root 1 0 R>>\n%%EOF\n");
        try
        {
            using var bitmap = PdfRaster.Render(pdf, 0, 36);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Only a head start.
        }
    }

    public static bool IsWordInstalled => OfficeAutomation.IsInstalled(OfficeApp.Word);
    public static bool IsExcelInstalled => OfficeAutomation.IsInstalled(OfficeApp.Excel);
    public static bool IsPowerPointInstalled => OfficeAutomation.IsInstalled(OfficeApp.PowerPoint);
    public static bool IsLibreOfficeInstalled => LibreOffice.IsInstalled;

    /// <summary>True when Windows has at least one OCR language installed.</summary>
    public static bool IsOcrAvailable => OcrService.IsAvailable;

    /// <summary>Installed OCR languages as BCP-47 tags, for a language picker.</summary>
    public static IReadOnlyList<string> OcrLanguages => OcrService.InstalledLanguages;

    /// <summary>
    /// The text in a picture of the screen (BGRA pixels), one line of text per line. Screen text is small,
    /// so short pictures are read at twice their size.
    /// </summary>
    public static async Task<string> ReadTextAsync(byte[] bgra, int width, int height, CancellationToken cancellationToken = default)
    {
        var words = await ReadWordsAsync(bgra, width, height, cancellationToken).ConfigureAwait(false);
        // In reading order: Windows gives an Arabic line's words from the left.
        return ReadingOrder.Text(words);
    }

    /// <summary>
    /// The words in a picture of the screen (BGRA pixels), each with its box in the picture's own pixels and the line it
    /// belongs to, in the order Windows reads them. Short pictures are read at twice their size, very large ones at the
    /// largest size Windows reads; the boxes are given back at the picture's size either way. With <paramref name="everyLanguage"/>,
    /// every installed language reads it and each line is kept from the one that read it best (for screens that mix scripts).
    /// </summary>
    public static async Task<IReadOnlyList<PictureWord>> ReadWordsAsync(byte[] bgra, int width, int height, CancellationToken cancellationToken = default, bool everyLanguage = false)
    {
        using var bitmap = new SkiaSharp.SKBitmap(new SkiaSharp.SKImageInfo(width, height, SkiaSharp.SKColorType.Bgra8888, SkiaSharp.SKAlphaType.Premul));
        System.Runtime.InteropServices.Marshal.Copy(bgra, 0, bitmap.GetPixels(), Math.Min(bgra.Length, width * height * 4));
        SkiaSharp.SKBitmap? larger = null;
        double scale = 1;
        int max = OcrService.MaxImageDimension;
        if (Math.Max(width, height) * 2 <= max && height < 900)
        {
            larger = bitmap.Resize(new SkiaSharp.SKImageInfo(width * 2, height * 2, bitmap.ColorType, bitmap.AlphaType), new SkiaSharp.SKSamplingOptions(SkiaSharp.SKCubicResampler.Mitchell));
            scale = 2;
        }
        else if (Math.Max(width, height) > max)
        {
            double s = (double)max / Math.Max(width, height);
            larger = bitmap.Resize(new SkiaSharp.SKImageInfo((int)(width * s), (int)(height * s), bitmap.ColorType, bitmap.AlphaType), new SkiaSharp.SKSamplingOptions(SkiaSharp.SKCubicResampler.Mitchell));
            scale = (double)larger.Width / width;
        }
        try
        {
            var words = everyLanguage
                ? await OcrService.RecognizeEveryLanguageAsync(larger ?? bitmap, cancellationToken).ConfigureAwait(false)
                : await OcrService.RecognizeAsync(larger ?? bitmap, null, cancellationToken).ConfigureAwait(false);
            return words.Select(w => new PictureWord(w.Text, w.X / scale, w.Y / scale, w.Width / scale, w.Height / scale, w.Line)).ToList();
        }
        finally
        {
            larger?.Dispose();
        }
    }

    /// <summary>
    /// The words on a picture given in several versions of the same size (BGRA pixels), read as carefully as Windows can:
    /// short pictures at twice their size, very large ones at the largest it reads; boxes are in the picture's own pixels.
    /// </summary>
    public static async Task<IReadOnlyList<PictureWord>> ReadPictureAsync(IReadOnlyList<byte[]> versions, int width, int height, CancellationToken cancellationToken = default)
    {
        int max = OcrService.MaxImageDimension;
        double scale = Math.Max(width, height) * 2 <= max && height < 1300 ? 2 : Math.Max(width, height) > max ? (double)max / Math.Max(width, height) : 1;
        int w = Math.Max(1, (int)(width * scale)), h = Math.Max(1, (int)(height * scale));
        var bitmaps = new List<SkiaSharp.SKBitmap>();
        try
        {
            foreach (var bgra in versions)
            {
                var bitmap = new SkiaSharp.SKBitmap(new SkiaSharp.SKImageInfo(width, height, SkiaSharp.SKColorType.Bgra8888, SkiaSharp.SKAlphaType.Premul));
                System.Runtime.InteropServices.Marshal.Copy(bgra, 0, bitmap.GetPixels(), Math.Min(bgra.Length, width * height * 4));
                if (scale == 1)
                {
                    bitmaps.Add(bitmap);
                    continue;
                }
                bitmaps.Add(bitmap.Resize(new SkiaSharp.SKImageInfo(w, h, bitmap.ColorType, bitmap.AlphaType), new SkiaSharp.SKSamplingOptions(SkiaSharp.SKCubicResampler.Mitchell)));
                bitmap.Dispose();
            }
            double back = (double)w / width;
            var words = await OcrService.RecognizeVersionsAsync(bitmaps, cancellationToken).ConfigureAwait(false);
            return words.Select(o => new PictureWord(o.Text, o.X / back, o.Y / back, o.Width / back, o.Height / back, o.Line)).ToList();
        }
        finally
        {
            foreach (var bitmap in bitmaps)
                bitmap.Dispose();
        }
    }

    /// <summary>A text file's contents, whatever its encoding (UTF-8/16 with or without a BOM, or the system code page).</summary>
    public static string ReadTextFile(string path) => Text.TextFile.ReadAllText(path);
}
