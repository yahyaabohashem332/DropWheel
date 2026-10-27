using System.IO;
using Petal.Core.Formats;
using Petal.Core.Jobs;
using Petal.Engines.Documents;

namespace Petal.Recipes;

/// <summary>The words of a document, for reading aloud: Word, PDF (scans read with OCR), e-books, web pages, text.</summary>
public static class DocumentText
{
    public static bool CanRead(FileFormat format, ConverterRegistry converters) =>
        format == Formats.Txt || format == Formats.Psd || format == Formats.Psb || format == Formats.Ai || converters.CanConvert(format, Formats.Txt);

    public static async Task<string> ReadAsync(string path, ConverterRegistry converters, ConversionSettings settings,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var format = Formats.FromPath(path) ?? throw new InvalidOperationException($"{Path.GetFileName(path)}: DropWheel doesn't know this kind of file.");
        if (format == Formats.Txt)
            return DocumentEngine.ReadTextFile(path);
        if (format == Formats.Psd || format == Formats.Psb)
            return PhotoshopText(path);
        // An Illustrator file's artwork is a PDF: its words are read as a PDF's.
        if (format == Formats.Ai)
        {
            if (!Petal.Engines.Documents.Pdf.IllustratorPdf.HasPdfContent(path))
                return "";
            format = Formats.Pdf;
        }
        var converter = converters.Find(format, Formats.Txt)
            ?? throw new InvalidOperationException($"DropWheel can't read the text of {format.Label} files.");
        var folder = Path.Combine(Path.GetTempPath(), "Petal", $"text-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            var result = await converter.ConvertAsync(new ConversionJob(path, format, Formats.Txt, Path.Combine(folder, "text.txt"), settings), progress, cancellationToken);
            return string.Join("\n\n", result.OutputPaths.Where(File.Exists).Select(DocumentEngine.ReadTextFile));
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>A Photoshop document's words: what its text layers say, and its layer names.</summary>
    private static string PhotoshopText(string path)
    {
        var psd = Petal.Engines.Imaging.PsdFile.Read(path);
        var text = psd.Layers.Where(l => l.Text is { Length: > 0 }).Select(l => l.Text!);
        var names = psd.Layers.Where(l => l.Kind != Petal.Engines.Imaging.PsdLayerKind.Empty).Select(l => l.Name).Where(n => n.Length > 0 && !n.StartsWith('<'));
        return string.Join("\n", text.Concat(names).Distinct());
    }
}
