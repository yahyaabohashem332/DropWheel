using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using Petal.Core.Formats;
using Petal.Engines.Documents.Util;
using F = Petal.Core.Formats.Formats;

namespace Petal.Engines.Documents.Office;

/// <summary>
/// Word's, Excel's and PowerPoint's files that differ only by the kind of package they are: documents, templates,
/// shows and the ones with macros. One becomes another by its package type alone, nothing else of it changed;
/// a file that is to have no macros loses its macro project (it is never run).
/// </summary>
internal static class OfficePackages
{
    /// <summary>The plain kind a variant is read as: DOCX for DOCM and DOTX, and so on; null for any other format.</summary>
    public static FileFormat? PlainOf(FileFormat format) => format.Id switch
    {
        "docm" or "dotx" => F.Docx,
        "xlsm" or "xltx" => F.Xlsx,
        "pptm" or "ppsx" or "potx" => F.Pptx,
        _ => null,
    };

    /// <summary>Whether a format is one of Word's, Excel's or PowerPoint's packages, plain or not.</summary>
    public static bool IsPackage(FileFormat format) => format == F.Docx || format == F.Xlsx || format == F.Pptx || PlainOf(format) is not null;

    /// <summary>Whether one becomes the other by its package type: both of the same program.</summary>
    public static bool SameProgram(FileFormat from, FileFormat to) =>
        IsPackage(from) && IsPackage(to) && (PlainOf(from) ?? from) == (PlainOf(to) ?? to);

    /// <summary>Writes <paramref name="input"/> to <paramref name="output"/> as a package of <paramref name="to"/>'s kind.</summary>
    public static void Retype(string input, FileFormat to, string output)
    {
        File.Copy(input, output, overwrite: true);
        File.SetAttributes(output, FileAttributes.Normal);
        switch ((PlainOf(to) ?? to).Id)
        {
            case "docx":
            {
                using var package = WordprocessingDocument.Open(output, true);
                if (package.MainDocumentPart is { } main)
                {
                    if (main.VbaProjectPart is { } vba)
                        main.DeletePart(vba);
                    // Keyboard shortcuts to the macros just taken out.
                    foreach (var keys in main.GetPartsOfType<CustomizationPart>().ToList())
                        main.DeletePart(keys);
                }
                package.ChangeDocumentType(to == F.Dotx ? WordprocessingDocumentType.Template : WordprocessingDocumentType.Document);
                break;
            }
            case "xlsx":
            {
                using var package = SpreadsheetDocument.Open(output, true);
                if (package.WorkbookPart?.VbaProjectPart is { } vba)
                    package.WorkbookPart.DeletePart(vba);
                package.ChangeDocumentType(to == F.Xltx ? SpreadsheetDocumentType.Template : SpreadsheetDocumentType.Workbook);
                break;
            }
            case "pptx":
            {
                using var package = PresentationDocument.Open(output, true);
                if (package.PresentationPart?.VbaProjectPart is { } vba)
                    package.PresentationPart.DeletePart(vba);
                package.ChangeDocumentType(
                    to == F.Ppsx ? PresentationDocumentType.Slideshow :
                    to == F.Potx ? PresentationDocumentType.Template :
                    PresentationDocumentType.Presentation);
                break;
            }
            default:
                throw new NotSupportedException($"Cannot write {to.Label} files.");
        }
    }

    /// <summary>
    /// A variant as a plain file in <paramref name="scratch"/>, which other converters read and Office opens without
    /// macros to run; the file itself and its format when it is plain already.
    /// </summary>
    public static (string Path, FileFormat Format) AsPlain(string path, FileFormat format, ScratchFolder scratch)
    {
        if (PlainOf(format) is not { } plain)
            return (path, format);
        var copy = scratch.File("plain." + plain.WriteExtension);
        Retype(path, plain, copy);
        return (copy, plain);
    }
}
