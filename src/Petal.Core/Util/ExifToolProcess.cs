using System.Text;

namespace Petal.Core.Util;

/// <summary>
/// Runs ExifTool with its arguments in a UTF-8 argument file: on the command line, Perl receives
/// file names in the ANSI code page and loses Arabic and other non-Latin characters.
/// </summary>
public static class ExifToolProcess
{
    public static async Task<ProcessResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
    {
        var exe = ToolLocator.ExifTool ?? throw new InvalidOperationException("ExifTool isn't installed.");
        var argFile = Path.Combine(Path.GetTempPath(), $"petal-exiftool-{Guid.NewGuid():N}.args");
        await File.WriteAllLinesAsync(argFile, arguments, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        try
        {
            return await ProcessRunner.RunAsync(exe, ["-charset", "filename=utf8", "-charset", "utf8", "-@", argFile], cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try { File.Delete(argFile); } catch (IOException) { }
        }
    }
}
