using System.Security.Cryptography;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using Petal.Engines.Documents.Util;
using UglyToad.PdfPig;

namespace Petal.Engines.Documents.Pdf;

/// <summary>Where page numbers go.</summary>
public enum PageNumberPlace
{
    BottomCenter,
    BottomRight,
    BottomLeft,
    TopCenter,
    TopRight,
    TopLeft,
}

/// <summary>How a page number reads: "3", "3 / 12" or "- 3 -".</summary>
public enum PageNumberStyle
{
    Number,
    NumberOfTotal,
    Dashes,
}

/// <param name="Start">The number on the first numbered page.</param>
/// <param name="SkipFirstPage">Leave a cover page unnumbered (numbering then starts on page 2).</param>
public sealed record PageNumberOptions(
    PageNumberPlace Place = PageNumberPlace.BottomCenter,
    PageNumberStyle Style = PageNumberStyle.Number,
    double FontSize = 10,
    int Start = 1,
    bool SkipFirstPage = false,
    string? Password = null);

/// <summary>A page as the reader sees it: size in points (rotation applied) and its index.</summary>
public readonly record struct PdfPageFrame(int Index, double Width, double Height);

/// <summary>
/// Draws onto existing pages: transparent images (watermarks, signatures) and page numbers.
/// Everything is drawn upright as the page is displayed, whatever its rotation or crop.
/// </summary>
public static class PdfStamper
{
    /// <summary>
    /// Lays a PNG over each page. <paramref name="overlay"/> gets the displayed page and returns a
    /// PNG covering it exactly (any resolution with that aspect ratio), or null to leave it alone.
    /// </summary>
    public static Task StampAsync(string input, string output, Func<PdfPageFrame, byte[]?> overlay, string? password = null,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            SafeOutput.EnsureNotInput(input, output);
            using var document = PdfSharpIO.Open(input, PdfDocumentOpenMode.Modify, password);
            // Pages with the same overlay share one image object.
            var images = new Dictionary<string, XImage>();
            try
            {
                for (int i = 0; i < document.PageCount; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var page = document.Pages[i];
                    var (w, h) = DisplayedSize(page);
                    var png = overlay(new PdfPageFrame(i, w, h));
                    if (png is not null)
                    {
                        var key = Convert.ToHexString(SHA1.HashData(png));
                        if (!images.TryGetValue(key, out var image))
                            images[key] = image = XImage.FromStream(new MemoryStream(png));
                        using var gfx = Upright(page);
                        gfx.DrawImage(image, 0, 0, w, h);
                    }
                    progress?.Report(0.9 * (i + 1) / document.PageCount);
                }
                SafeOutput.Write(output, temp => document.Save(temp));
            }
            finally
            {
                foreach (var image in images.Values)
                    image.Dispose();
            }
            progress?.Report(1);
        }, cancellationToken);

    /// <summary>Writes page numbers as real text, so they print sharply and can be searched.</summary>
    public static Task NumberPagesAsync(string input, string output, PageNumberOptions options, IProgress<double>? progress = null,
        CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            SafeOutput.EnsureNotInput(input, output);
            PdfFonts.EnsureInitialized();
            using var document = PdfSharpIO.Open(input, PdfDocumentOpenMode.Modify, options.Password);
            int first = options.SkipFirstPage ? 1 : 0;
            int total = options.Start + document.PageCount - first - 1;
            var font = new XFont(PdfFonts.IsAvailable("Segoe UI") ? "Segoe UI" : "Arial", options.FontSize);
            var brush = new XSolidBrush(XColor.FromArgb(255, 60, 60, 64));
            for (int i = first; i < document.PageCount; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var page = document.Pages[i];
                var (w, h) = DisplayedSize(page);
                int n = options.Start + i - first;
                var text = options.Style switch
                {
                    PageNumberStyle.NumberOfTotal => $"{n} / {total}",
                    PageNumberStyle.Dashes => $"- {n} -",
                    _ => n.ToString(System.Globalization.CultureInfo.InvariantCulture),
                };
                // About 1.5 cm from the edges, as word processors do.
                double margin = Math.Clamp(Math.Min(w, h) * 0.07, 18, 42);
                var size = new XSize(w - 2 * margin, options.FontSize * 1.6);
                double top = options.Place is PageNumberPlace.TopCenter or PageNumberPlace.TopLeft or PageNumberPlace.TopRight
                    ? margin - size.Height / 2
                    : h - margin - size.Height / 2;
                var format = new XStringFormat
                {
                    Alignment = options.Place switch
                    {
                        PageNumberPlace.BottomLeft or PageNumberPlace.TopLeft => XStringAlignment.Near,
                        PageNumberPlace.BottomRight or PageNumberPlace.TopRight => XStringAlignment.Far,
                        _ => XStringAlignment.Center,
                    },
                    LineAlignment = XLineAlignment.Center,
                };
                using var gfx = Upright(page);
                gfx.DrawString(text, font, brush, new XRect(margin, top, size.Width, size.Height), format);
                progress?.Report(0.9 * (i + 1) / document.PageCount);
            }
            SafeOutput.Write(output, temp => document.Save(temp));
            progress?.Report(1);
        }, cancellationToken);

    /// <summary>The displayed size of every page, for laying out overlays before stamping.</summary>
    public static IReadOnlyList<PdfPageFrame> Pages(string path, string? password = null)
    {
        using var document = PdfSharpIO.Open(path, PdfDocumentOpenMode.Import, password);
        return Enumerable.Range(0, document.PageCount).Select(i =>
        {
            var (w, h) = DisplayedSize(document.Pages[i]);
            return new PdfPageFrame(i, w, h);
        }).ToList();
    }

    /// <summary>
    /// Saves every distinct picture in the PDF into <paramref name="outputFolder"/>: JPEGs as they
    /// are stored (no re-encoding), everything else as PNG. Repeated pictures, such as a logo on
    /// every page, are saved once.
    /// </summary>
    public static Task<IReadOnlyList<string>> ExtractImagesAsync(string input, string outputFolder, string? password = null,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default) =>
        Task.Run<IReadOnlyList<string>>(() =>
        {
            var parsing = new ParsingOptions { UseLenientParsing = true };
            if (password is not null)
                parsing.Password = password;
            using var pdf = UglyToad.PdfPig.PdfDocument.Open(input, parsing);
            var stem = Path.GetFileNameWithoutExtension(input);
            var seen = new HashSet<string>();
            var written = new List<string>();
            var temp = outputFolder + SafeOutput.TempSuffix;
            if (Directory.Exists(temp))
                Directory.Delete(temp, recursive: true);
            Directory.CreateDirectory(temp);
            try
            {
                for (int p = 1; p <= pdf.NumberOfPages; p++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int n = 0;
                    foreach (var image in pdf.GetPage(p).GetImages())
                    {
                        // Tiny images are usually bullets or spacers, not pictures anyone wants.
                        if (image.IsImageMask || image.WidthInSamples * image.HeightInSamples < 64 * 64)
                            continue;
                        var raw = image.RawMemory.Span;
                        bool jpeg = raw.Length > 3 && raw[0] == 0xFF && raw[1] == 0xD8 && raw[2] == 0xFF;
                        byte[]? bytes = jpeg ? raw.ToArray() : image.TryGetPng(out var png) ? png : null;
                        if (bytes is null || !seen.Add(Convert.ToHexString(SHA1.HashData(bytes))))
                            continue;
                        var name = $"{stem} p{p}-{++n}{(jpeg ? ".jpg" : ".png")}";
                        File.WriteAllBytes(Path.Combine(temp, name), bytes);
                        written.Add(Path.Combine(outputFolder, name));
                    }
                    progress?.Report(0.95 * p / pdf.NumberOfPages);
                }
                if (written.Count == 0)
                    throw new InvalidOperationException("This PDF has no pictures to extract.");
                if (Directory.Exists(outputFolder))
                    Directory.Delete(outputFolder, recursive: true);
                Directory.Move(temp, outputFolder);
            }
            catch
            {
                if (Directory.Exists(temp))
                    Directory.Delete(temp, recursive: true);
                throw;
            }
            progress?.Report(1);
            return written;
        }, cancellationToken);

    private static PdfRectangle Visible(PdfPage page) => page.CropBox.IsZero ? page.MediaBox : page.CropBox;

    private static (double Width, double Height) DisplayedSize(PdfPage page)
    {
        var box = Visible(page);
        return Normal(page.Rotate) is 90 or 270 ? (box.Height, box.Width) : (box.Width, box.Height);
    }

    private static int Normal(int rotate) => ((rotate % 360) + 360) % 360;

    /// <summary>
    /// Graphics whose origin is the top-left of the page as displayed, with x to the right and y
    /// down, whatever the page's /Rotate and crop box.
    /// </summary>
    private static XGraphics Upright(PdfPage page)
    {
        var gfx = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append);
        var media = page.MediaBox;
        var box = Visible(page);
        // XGraphics starts at the media box's top-left, y down; move to the visible area.
        gfx.TranslateTransform(box.X1 - media.X1, media.Y2 - box.Y2);
        double w = box.Width, h = box.Height;
        // Map displayed coordinates back onto the unrotated page.
        var m = Normal(page.Rotate) switch
        {
            90 => new XMatrix(0, -1, 1, 0, 0, h),
            180 => new XMatrix(-1, 0, 0, -1, w, h),
            270 => new XMatrix(0, 1, -1, 0, w, 0),
            _ => XMatrix.Identity,
        };
        if (!m.IsIdentity)
            gfx.MultiplyTransform(m);
        return gfx;
    }
}
