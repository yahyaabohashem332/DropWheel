using System.Text;
using Petal.Engines.Documents.IWork;
using Xunit;
using Xunit.Abstractions;

namespace Petal.Engines.Documents.Tests;

/// <summary>Real files, to be read by someone: PETAL_IWORK is a folder of them.</summary>
public sealed class IWorkBench(ITestOutputHelper output)
{
    [Fact]
    public void Real_files_for_reading()
    {
        if (Environment.GetEnvironmentVariable("PETAL_IWORK") is not { Length: > 0 } folder)
            return;
        foreach (var file in Directory.EnumerateFiles(folder))
        {
            var ext = Path.GetExtension(file).ToLowerInvariant();
            if (ext is not (".pages" or ".key" or ".numbers"))
                continue;
            try
            {
                var text = IWorkFile.Text(file);
                output.WriteLine($"=== {Path.GetFileName(file)}: pdf {IWorkFile.Pdf(file)?.Length ?? 0}, picture {IWorkFile.Picture(file)?.Length ?? 0}, text {text.Length}");
                output.WriteLine(text.Length > 400 ? text[..400] : text);
            }
            catch (Exception ex)
            {
                output.WriteLine($"=== {Path.GetFileName(file)}: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }
}
