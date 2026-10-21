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
