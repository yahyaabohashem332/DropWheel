using Petal.Core.Util;

namespace Petal.Engines.Documents.Office;

/// <summary>LibreOffice in headless mode, the fallback when Microsoft Office is not installed.</summary>
internal static class LibreOffice
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static string? Executable => ToolLocator.LibreOffice;

    public static bool IsInstalled => Executable is not null;

    /// <summary>Converts <paramref name="input"/> to PDF and writes it to <paramref name="outputPdf"/>.</summary>
    public static Task ExportPdfAsync(string input, string outputPdf, TimeSpan timeout, CancellationToken ct) =>
        ConvertAsync(input, "pdf", outputPdf, timeout, ct);

    /// <summary>Converts <paramref name="input"/> to another format LibreOffice writes ("pdf", "docx"…).</summary>
    public static async Task ConvertAsync(string input, string extension, string output, TimeSpan timeout, CancellationToken ct)
    {
        var soffice = Executable ?? throw new InvalidOperationException("LibreOffice is not installed.");
        using var scratch = Util.SafeOutput.CreateScratch();
        // A private profile: a LibreOffice window the user has open would otherwise swallow the request.
        var profile = Path.Combine(Path.GetTempPath(), "Petal", "libreoffice-profile");
        Directory.CreateDirectory(profile);
        var profileUrl = new Uri(profile + Path.DirectorySeparatorChar).AbsoluteUri;

        // Copy under a plain name: LibreOffice names its output after the input and dislikes some characters.
        var ext = Path.GetExtension(input);
        var source = scratch.File("input" + ext);
        File.Copy(input, source);

        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            ProcessResult result;
            try
            {
                result = await ProcessRunner.RunAsync(soffice,
                [
                    "--headless", "--invisible", "--nologo", "--norestore", "--nodefault", "--nolockcheck", "--nofirststartwizard",
                    "-env:UserInstallation=" + profileUrl,
                    "--convert-to", extension, "--outdir", scratch.Path, source,
                ], workingDirectory: scratch.Path, cancellationToken: cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException($"LibreOffice did not finish within {timeout.TotalSeconds:0} seconds.");
            }
            var produced = scratch.File("input." + extension);
            if (!result.Succeeded || !File.Exists(produced))
                throw new ExternalToolException("LibreOffice", result);
            File.Copy(produced, output, overwrite: true);
        }
        finally
        {
            Gate.Release();
        }
    }
}
