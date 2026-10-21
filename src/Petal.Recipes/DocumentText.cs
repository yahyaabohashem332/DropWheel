using System.IO;
using Petal.Core.Formats;
using Petal.Core.Jobs;
using Petal.Engines.Documents;

namespace Petal.Recipes;

/// <summary>The words of a document, for reading aloud: Word, PDF (scans read with OCR), e-books, web pages, text.</summary>
public static class DocumentText
{
    public static bool CanRead(FileFormat format, ConverterRegistry converters) =>
        format == Formats.Txt || converters.CanConvert(format, Formats.Txt);

    public static async Task<string> ReadAsync(string path, ConverterRegistry converters, ConversionSettings settings,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var format = Formats.FromPath(path) ?? throw new InvalidOperationException($"{Path.GetFileName(path)}: Petal doesn't know this kind of file.");
        if (format == Formats.Txt)
            return DocumentEngine.ReadTextFile(path);
        var converter = converters.Find(format, Formats.Txt)
            ?? throw new InvalidOperationException($"Petal can't read the text of {format.Label} files.");
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
}
