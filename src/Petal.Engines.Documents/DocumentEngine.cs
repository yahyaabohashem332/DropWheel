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

    /// <summary>A text file's contents, whatever its encoding (UTF-8/16 with or without a BOM, or the system code page).</summary>
    public static string ReadTextFile(string path) => Text.TextFile.ReadAllText(path);
}
