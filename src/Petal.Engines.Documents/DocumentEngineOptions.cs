using Petal.Engines.Documents.Pdf;

namespace Petal.Engines.Documents;

/// <summary>How PDF → EPUB lays out the book.</summary>
public enum PdfToEpubLayout
{
    /// <summary>Text that reflows to the reader's screen and font size.</summary>
    Reflowable,

    /// <summary>Every page kept exactly as it looks, as a picture.</summary>
    FixedLayout,
}

/// <summary>Choices for the documents engine. The defaults favour fidelity.</summary>
public sealed record DocumentEngineOptions
{
    /// <summary>Use Word, Excel and PowerPoint (COM) when installed, for exact-layout PDFs and slides.</summary>
    public bool UseMicrosoftOffice { get; init; } = true;

    /// <summary>Use LibreOffice (headless) when installed and Office is not.</summary>
    public bool UseLibreOffice { get; init; } = true;

    /// <summary>How long one Office or LibreOffice job may take before it is stopped.</summary>
    public TimeSpan OfficeTimeout { get; init; } = TimeSpan.FromMinutes(3);

    /// <summary>Read pages without a text layer (scans) with Windows OCR.</summary>
    public bool OcrScannedPages { get; init; } = true;

    /// <summary>OCR languages as BCP-47 tags ("en-US", "ar-SA"); null tries the user's languages, then every installed one.</summary>
    public IReadOnlyList<string>? OcrLanguages { get; init; }

    public PdfToEpubLayout PdfToEpubLayout { get; init; } = PdfToEpubLayout.Reflowable;

    public PdfToPptxMode PdfToPptxMode { get; init; } = PdfToPptxMode.EditableText;

    /// <summary>Keep the PDF's page breaks in Word/ODT output instead of letting text flow.</summary>
    public bool PdfKeepPageBreaks { get; init; }

    public static DocumentEngineOptions Default { get; } = new();
}
