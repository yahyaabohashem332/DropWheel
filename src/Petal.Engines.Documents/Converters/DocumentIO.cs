using Petal.Core.Formats;
using Petal.Core.Jobs;
using Petal.Engines.Documents.IO;
using Petal.Engines.Documents.Model;
using Petal.Engines.Documents.Office;
using Petal.Engines.Documents.Pdf;
using Petal.Engines.Documents.Util;
using F = Petal.Core.Formats.Formats;

namespace Petal.Engines.Documents.Converters;

/// <summary>Format dispatch for the document model.</summary>
internal static class DocumentIO
{
    public static bool CanRead(FileFormat f) => f == F.Docx || f == F.Odt || f == F.Rtf || f == F.Md || f == F.Html || f == F.Txt || f == F.Epub;

    public static bool CanWrite(FileFormat f) => f == F.Docx || f == F.Odt || f == F.Rtf || f == F.Md || f == F.Html || f == F.Txt || f == F.Epub;

    public static DocModel Read(string path, FileFormat format) => format.Id switch
    {
        "docx" => DocxReader.Read(path),
        "odt" => OdtReader.Read(path),
        "rtf" => RtfReader.Read(path),
        "md" => MarkdownReader.Read(path),
        "html" => HtmlReader.Read(path),
        "txt" => PlainTextIO.Read(path),
        "epub" => Epub.Read(path),
        _ => throw new NotSupportedException($"Cannot read {format.Label} documents."),
    };

    /// <summary>Writes the model; returns every path produced (Markdown may add an images folder).</summary>
    public static IReadOnlyList<string> Write(DocModel doc, FileFormat format, string outputPath)
    {
        string? extra = null;
        SafeOutput.Write(outputPath, temp =>
        {
            switch (format.Id)
            {
                case "docx": DocxWriter.Write(doc, temp); break;
                case "odt": OdtWriter.Write(doc, temp); break;
                case "rtf": RtfWriter.Write(doc, temp); break;
                case "md": extra = MarkdownWriter.Write(doc, temp, outputPath); break;
                case "html": HtmlWriter.Write(doc, temp); break;
                case "txt": PlainTextIO.Write(doc, temp); break;
                case "epub":
                    doc.Metadata.Title ??= doc.EffectiveTitle() ?? F.BaseName(outputPath);
                    Epub.Write(doc, temp);
                    break;
                case "pdf": PdfDocWriter.Write(doc, temp); break;
                default: throw new NotSupportedException($"Cannot write {format.Label} documents.");
            }
        });
        return extra is null ? [outputPath] : [outputPath, extra];
    }
}

/// <summary>Exact-layout PDF through Word/Excel/PowerPoint or LibreOffice, when available.</summary>
internal static class HighFidelity
{
    public static OfficeApp? AppFor(FileFormat from) =>
        from == F.Docx || from == F.Odt || from == F.Rtf ? OfficeApp.Word :
        from == F.Xlsx || from == F.Xls || from == F.Ods ? OfficeApp.Excel :
        from == F.Pptx || from == F.Ppt ? OfficeApp.PowerPoint : null;

    public static bool OfficeAvailable(OfficeApp app, DocumentEngineOptions options) =>
        options.UseMicrosoftOffice && OfficeAutomation.IsInstalled(app);

    public static bool LibreOfficeAvailable(DocumentEngineOptions options) =>
        options.UseLibreOffice && LibreOffice.IsInstalled;

    public static bool Available(FileFormat from, DocumentEngineOptions options) =>
        AppFor(from) is { } app && (OfficeAvailable(app, options) || LibreOfficeAvailable(options));

    /// <summary>Exports <paramref name="input"/> to <paramref name="outputPdf"/>. False when no engine is available or all failed.</summary>
    public static async Task<bool> TryPdfAsync(string input, OfficeApp app, string outputPdf, DocumentEngineOptions options, CancellationToken ct, bool throwOnFailure = false)
    {
        Exception? last = null;
        if (OfficeAvailable(app, options))
        {
            try
            {
                await OfficeAutomation.ExportPdfAsync(app, Path.GetFullPath(input), outputPdf, options.OfficeTimeout, ct).ConfigureAwait(false);
                if (File.Exists(outputPdf) && new FileInfo(outputPdf).Length > 0)
                    return true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                last = ex;
            }
        }
        if (LibreOfficeAvailable(options))
        {
            try
            {
                await LibreOffice.ExportPdfAsync(input, outputPdf, options.OfficeTimeout, ct).ConfigureAwait(false);
                if (File.Exists(outputPdf) && new FileInfo(outputPdf).Length > 0)
                    return true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                last = ex;
            }
        }
        if (throwOnFailure && last is not null)
            throw new InvalidOperationException($"Could not convert {Path.GetFileName(input)}: {last.Message}", last);
        return false;
    }
}

/// <summary>PDF pages as PNG or JPEG files: one file, or a folder for several pages.</summary>
internal static class PageImages
{
    public static IReadOnlyList<string> Write(byte[] pdf, string inputPath, string outputPath, bool jpeg, ConversionSettings settings,
        IProgress<double>? progress, double progressStart, CancellationToken ct)
    {
        var count = PdfRaster.PageSizes(pdf).Count;
        return SafeOutput.WritePages(inputPath, outputPath, count, (i, path) =>
        {
            ct.ThrowIfCancellationRequested();
            using var bitmap = PdfRaster.Render(pdf, i, settings.RenderDpi);
            ImageTools.Save(bitmap, path, jpeg, settings.ImageQuality);
            progress?.Report(progressStart + (1 - progressStart) * (i + 1) / count);
        });
    }
}
