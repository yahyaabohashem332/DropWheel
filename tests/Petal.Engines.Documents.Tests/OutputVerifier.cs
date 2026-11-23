using System.IO.Compression;
using ClosedXML.Excel;
using DocumentFormat.OpenXml.Packaging;
using Petal.Core.Formats;
using Petal.Engines.Documents.IO;
using Petal.Engines.Documents.Model;
using Petal.Engines.Documents.Sheets;
using SkiaSharp;

namespace Petal.Engines.Documents.Tests;

/// <summary>Opens an output with an independent reader and returns its text, proving the file is valid.</summary>
internal static class OutputVerifier
{
    public static string TextOf(string path, FileFormat format)
    {
        Assert.True(File.Exists(path), $"Missing output {path}");
        Assert.True(new FileInfo(path).Length > 0, $"Empty output {path}");
        switch (format.Id)
        {
            case "pdf":
            {
                using var pdf = UglyToad.PdfPig.PdfDocument.Open(path);
                Assert.True(pdf.NumberOfPages > 0);
                return string.Join("\n", pdf.GetPages().Select(p => string.Join(" ", p.GetWords().Select(w => w.Text))));
            }
            case "dotx":
            {
                using (var template = WordprocessingDocument.Open(path, false))
                    Assert.Equal(DocumentFormat.OpenXml.WordprocessingDocumentType.Template, template.DocumentType);
                return TextOf(path, Petal.Core.Formats.Formats.Docx);
            }
            case "ppsx" or "potx":
            {
                using (var show = PresentationDocument.Open(path, false))
                    Assert.Equal(format.Id == "ppsx" ? DocumentFormat.OpenXml.PresentationDocumentType.Slideshow : DocumentFormat.OpenXml.PresentationDocumentType.Template, show.DocumentType);
                return TextOf(path, Petal.Core.Formats.Formats.Pptx);
            }
            case "xltx":
            {
                using (var template = SpreadsheetDocument.Open(path, false))
                    Assert.Equal(DocumentFormat.OpenXml.SpreadsheetDocumentType.Template, template.DocumentType);
                var copy = Path.Combine(Path.GetTempPath(), "petal-xltx-" + Guid.NewGuid().ToString("N") + ".xlsx");
                try
                {
                    Petal.Engines.Documents.Office.OfficePackages.Retype(path, Petal.Core.Formats.Formats.Xlsx, copy);
                    return TextOf(copy, Petal.Core.Formats.Formats.Xlsx);
                }
                finally
                {
                    File.Delete(copy);
                }
            }
            case "docx":
            {
                using var doc = WordprocessingDocument.Open(path, false);
                return string.Join("\n", doc.MainDocumentPart!.Document!.Body!.Descendants<DocumentFormat.OpenXml.Wordprocessing.Paragraph>().Select(p => p.InnerText));
            }
            case "pptx":
            {
                using var doc = PresentationDocument.Open(path, false);
                var slides = doc.PresentationPart!.SlideParts.ToList();
                Assert.NotEmpty(slides);
                return string.Join("\n", slides.SelectMany(s => s.Slide!.Descendants<DocumentFormat.OpenXml.Drawing.Text>()).Select(t => t.Text));
            }
            case "xlsx":
            {
                using var wb = new XLWorkbook(path);
                return string.Join("\n", wb.Worksheets.SelectMany(ws => ws.CellsUsed()).Select(c => c.GetFormattedString()));
            }
            case "ods":
                return string.Join("\n", OdsSheets.Read(path).Sheets.SelectMany(s => s.Rows).SelectMany(r => r).Select(c => c.RawText));
            case "odt":
                return OdtReader.Read(path).PlainText();
            case "rtf":
            {
                var head = File.ReadAllText(path)[..5];
                Assert.Equal("{\\rtf", head);
                return RtfReader.Read(path).PlainText();
            }
            case "epub":
            {
                using (var zip = ZipFile.OpenRead(path))
                {
                    Assert.Equal("mimetype", zip.Entries[0].FullName);
                    Assert.Equal(0, zip.Entries[0].CompressedLength - zip.Entries[0].Length);
                }
                return Epub.Read(path).PlainText();
            }
            case "png" or "jpg":
            {
                using var bitmap = SKBitmap.Decode(path);
                Assert.NotNull(bitmap);
                Assert.True(bitmap.Width > 100 && bitmap.Height > 100, $"Tiny image {bitmap.Width}x{bitmap.Height}");
                return "";
            }
            default:
                return Text.TextFile.ReadAllText(path);
        }
    }

    /// <summary>Squashes whitespace so text split across lines or runs still matches.</summary>
    public static string Normalize(string text) => new string(text.Where(c => !char.IsWhiteSpace(c)).ToArray()).ToLowerInvariant();

    public static void ContainsText(string haystack, string needle) =>
        Assert.True(Normalize(haystack).Contains(Normalize(needle)), $"Expected to find \"{needle}\" in:\n{(haystack.Length > 1500 ? haystack[..1500] + "…" : haystack)}");
}
