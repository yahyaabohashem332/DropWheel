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
            new PdfConverter(options),
            new DocumentConverter(options),
            new SpreadsheetConverter(options),
            new SlidesConverter(options),
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
        using var bitmap = new SkiaSharp.SKBitmap(new SkiaSharp.SKImageInfo(width, height, SkiaSharp.SKColorType.Bgra8888, SkiaSharp.SKAlphaType.Premul));
        System.Runtime.InteropServices.Marshal.Copy(bgra, 0, bitmap.GetPixels(), Math.Min(bgra.Length, width * height * 4));
        SkiaSharp.SKBitmap? larger = null;
        int max = OcrService.MaxImageDimension;
        if (Math.Max(width, height) * 2 <= max && height < 900)
            larger = bitmap.Resize(new SkiaSharp.SKImageInfo(width * 2, height * 2, bitmap.ColorType, bitmap.AlphaType), new SkiaSharp.SKSamplingOptions(SkiaSharp.SKCubicResampler.Mitchell));
        else if (Math.Max(width, height) > max)
        {
            double s = (double)max / Math.Max(width, height);
            larger = bitmap.Resize(new SkiaSharp.SKImageInfo((int)(width * s), (int)(height * s), bitmap.ColorType, bitmap.AlphaType), new SkiaSharp.SKSamplingOptions(SkiaSharp.SKCubicResampler.Mitchell));
        }
        try
        {
            var words = await OcrService.RecognizeAsync(larger ?? bitmap, null, cancellationToken).ConfigureAwait(false);
            return string.Join(Environment.NewLine, words.GroupBy(w => w.Line).OrderBy(g => g.Key).Select(g => string.Join(" ", g.Select(w => w.Text))));
        }
        finally
        {
            larger?.Dispose();
        }
    }

    /// <summary>A text file's contents, whatever its encoding (UTF-8/16 with or without a BOM, or the system code page).</summary>
    public static string ReadTextFile(string path) => Text.TextFile.ReadAllText(path);
}
