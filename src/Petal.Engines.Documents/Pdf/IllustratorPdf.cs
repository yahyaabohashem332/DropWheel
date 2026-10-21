using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using Petal.Core.Formats;
using Petal.Core.Jobs;
using Petal.Core.Util;
using SkiaSharp;
using F = Petal.Core.Formats.Formats;

namespace Petal.Engines.Documents.Pdf;

/// <summary>
/// Illustrator files saved with PDF content (the default since Illustrator 9) are PDFs carrying
/// Illustrator's own editing data on top. Petal draws them as pictures through PDFium, and makes
/// plain PDFs of them without that data, which is often most of the file.
/// </summary>
public static class IllustratorPdf
{
    /// <summary>Lets the image engine draw Illustrator files (see <see cref="PageRasterizer"/>).</summary>
    public static void Register() => PageRasterizer.Render ??= Render;

    public static byte[] Render(string path, int page, int dpi, bool transparent)
    {
        var bytes = File.ReadAllBytes(path);
        using var bitmap = PdfRaster.Render(bytes, page, dpi, annotations: false, background: transparent ? SKColors.Transparent : SKColors.White);
        using var image = SKImage.FromBitmap(bitmap);
        using var png = image.Encode(SKEncodedImageFormat.Png, 100);
        return png.ToArray();
    }

    public static bool HasPdfContent(string path)
    {
        Span<byte> head = stackalloc byte[5];
        using var fs = File.OpenRead(path);
        return fs.ReadAtLeast(head, 5, throwOnEndOfStream: false) == 5 && head.SequenceEqual("%PDF-"u8);
    }

    /// <summary>
    /// A plain PDF of an Illustrator file: the artwork as it prints, without the editing data
    /// Illustrator keeps for itself (its "PieceInfo" with the AIPrivateData streams).
    /// </summary>
    public static (long Before, long After) Clean(string input, string output)
    {
        using var document = PdfSharpIO.Open(input, PdfDocumentOpenMode.Modify);
        document.Internals.Catalog.Elements.Remove("/PieceInfo");
        foreach (var page in document.Pages)
            page.Elements.Remove("/PieceInfo");
        document.Info.Creator = document.Info.Creator; // keep what made it
        PdfSharpIO.Save(document, output);
        return (new FileInfo(input).Length, new FileInfo(output).Length);
    }
}

/// <summary>Illustrator and EPS files to PDF, kept as vector artwork.</summary>
internal sealed class DesignPdfConverter : IConverter
{
    public string Name => "Illustrator and EPS";

    public bool CanConvert(FileFormat from, FileFormat to) => (from == F.Ai || from == F.Eps) && to == F.Pdf;

    public Task<JobResult> ConvertAsync(ConversionJob job, IProgress<double>? progress, CancellationToken cancellationToken) =>
        Task.Run(async () =>
        {
            progress?.Report(0.05);
            if (job.From == F.Ai && IllustratorPdf.HasPdfContent(job.InputPath))
                IllustratorPdf.Clean(job.InputPath, job.OutputPath);
            else
                await Ghostscript.ToPdfAsync(job.InputPath, job.OutputPath, cancellationToken);
            progress?.Report(1);
            return JobResult.Single(job.OutputPath);
        }, cancellationToken);
}
