using PdfSharp.Pdf.Content;
using PdfSharp.Pdf.Content.Objects;
using PdfSharp.Pdf.IO;

namespace Petal.Engines.Documents.Pdf;

/// <summary>
/// Removes text objects (BT…ET) from page content streams so a page can be rendered as a
/// background picture without its text — the text then goes on top as editable text boxes.
/// </summary>
internal static class PdfTextStripper
{
    /// <summary>Returns the PDF without page text, and which pages were stripped successfully.</summary>
    public static (byte[] Pdf, bool[] Stripped) Strip(byte[] pdf)
    {
        using var input = new MemoryStream(pdf);
        PdfSharp.Pdf.PdfDocument document;
        try
        {
            document = PdfReader.Open(input, PdfDocumentOpenMode.Modify);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return (pdf, []);
        }
        using (document)
        {
            var stripped = new bool[document.PageCount];
            for (var i = 0; i < document.PageCount; i++)
            {
                var page = document.Pages[i];
                try
                {
                    var sequence = ContentReader.ReadContent(page);
                    var kept = new CSequence();
                    var depth = 0;
                    foreach (var item in sequence)
                    {
                        if (item is COperator op)
                        {
                            if (op.OpCode.OpCodeName == OpCodeName.BT) { depth++; continue; }
                            if (op.OpCode.OpCodeName == OpCodeName.ET) { depth = Math.Max(0, depth - 1); continue; }
                        }
                        if (depth == 0)
                            kept.Add(item);
                    }
                    page.Contents.ReplaceContent(kept);
                    stripped[i] = true;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    // Unparseable content (inline images, odd operators): leave the page as it is.
                }
            }
            using var output = new MemoryStream();
            document.Save(output, closeStream: false);
            return (output.ToArray(), stripped);
        }
    }
}
