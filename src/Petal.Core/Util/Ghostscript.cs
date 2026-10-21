using System.Globalization;

namespace Petal.Core.Util;

/// <summary>
/// Ghostscript draws PostScript: EPS files and Illustrator files from before Illustrator 9.
/// It isn't bundled (AGPL licence); Petal uses an installed copy.
/// </summary>
public static class Ghostscript
{
    public const string Needed = "Drawing EPS files needs Ghostscript, which is free. Install it from ghostscript.com and try again, or use Save Preview for the small picture inside the file.";

    public static bool IsAvailable => ToolLocator.Ghostscript is not null;

    /// <summary>The first page as a PNG with a transparent background, cropped to the drawing.</summary>
    public static async Task RenderPngAsync(string input, string png, int dpi, CancellationToken cancellationToken = default)
    {
        var gs = ToolLocator.Ghostscript ?? throw new InvalidOperationException(Needed);
        var result = await ProcessRunner.RunAsync(gs,
        [
            "-dSAFER", "-dBATCH", "-dNOPAUSE", "-dQUIET", "-sDEVICE=pngalpha",
            "-r" + Math.Clamp(dpi, 18, 1200).ToString(CultureInfo.InvariantCulture),
            "-dEPSCrop", "-dTextAlphaBits=4", "-dGraphicsAlphaBits=4", "-dFirstPage=1", "-dLastPage=1",
            "-sOutputFile=" + png, input,
        ], cancellationToken: cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0 || !File.Exists(png))
            throw new InvalidOperationException("Ghostscript couldn't draw this file: " + LastLine(result.StandardError));
    }

    /// <summary>A PDF of the drawing that stays vector, so it is sharp at any size.</summary>
    public static async Task ToPdfAsync(string input, string pdf, CancellationToken cancellationToken = default)
    {
        var gs = ToolLocator.Ghostscript ?? throw new InvalidOperationException(Needed);
        var result = await ProcessRunner.RunAsync(gs,
            ["-dSAFER", "-dBATCH", "-dNOPAUSE", "-dQUIET", "-sDEVICE=pdfwrite", "-dEPSCrop", "-dAutoRotatePages=/None", "-sOutputFile=" + pdf, input],
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0 || !File.Exists(pdf))
            throw new InvalidOperationException("Ghostscript couldn't convert this file: " + LastLine(result.StandardError));
    }

    private static string LastLine(string text) => text.Trim().Split('\n').LastOrDefault()?.Trim() ?? "";
}
