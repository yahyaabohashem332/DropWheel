using System.Globalization;
using System.Text;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Annotations;
using PdfSharp.Pdf.IO;
using Petal.Engines.Documents.Util;

namespace Petal.Engines.Documents.Pdf;

/// <summary>One page of an organized PDF: which source page, and how much extra clockwise rotation (0, 90, 180, 270).</summary>
public sealed record PdfPageOperation(int SourcePageIndex, int Rotation = 0);

/// <param name="UniformWidth">"Make all pages the same width": pages are scaled proportionally to match the narrowest; heights may still differ.</param>
public sealed record PdfOrganizeOptions(bool UniformWidth = false, string? Password = null);

/// <param name="KeepBookmarks">Carry each file's bookmarks over, pointing at the merged pages.</param>
/// <param name="BookmarkEachFile">Add a top-level bookmark per source file (its bookmarks nest under it).</param>
public sealed record PdfMergeOptions(bool KeepBookmarks = true, bool BookmarkEachFile = false);

/// <summary>Split, merge and organize. Inputs are never modified; outputs appear only when complete.</summary>
public static class PdfPageTools
{
    /// <summary>Saves every page as its own PDF ("report 1.pdf", "report 2.pdf"…) in <paramref name="outputFolder"/>, which is created.</summary>
    /// <returns>The files written, in page order.</returns>
    public static Task<IReadOnlyList<string>> SplitAsync(string input, string outputFolder, IProgress<double>? progress = null,
        CancellationToken cancellationToken = default, string? password = null) =>
        Task.Run<IReadOnlyList<string>>(() =>
        {
            using var source = PdfSharpIO.Open(input, PdfDocumentOpenMode.Import, password);
            var stem = Petal.Core.Formats.Formats.BaseName(input);
            var count = source.PageCount;
            var existing = Directory.Exists(outputFolder);
            var target = existing ? outputFolder : outputFolder + SafeOutput.TempSuffix;
            if (!existing)
            {
                SafeOutput.TryDeleteDirectory(target);
                Directory.CreateDirectory(target);
            }
            var names = new List<string>(count);
            try
            {
                for (var i = 0; i < count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    using var single = new PdfDocument();
                    single.Version = Math.Max(single.Version, source.Version);
                    CopyInfo(source, single);
                    single.AddPage(source.Pages[i]);
                    var name = Petal.Core.Jobs.OutputNaming.Unique(target, $"{stem} {i + 1}", "pdf");
                    SafeOutput.Write(name, temp => PdfSharpIO.Save(single, temp));
                    names.Add(Path.GetFileName(name));
                    progress?.Report((i + 1.0) / count);
                }
                if (!existing)
                    Directory.Move(target, outputFolder);
            }
            finally
            {
                if (!existing)
                    SafeOutput.TryDeleteDirectory(target);
            }
            return names.Select(n => Path.Combine(outputFolder, n)).ToList();
        }, cancellationToken);

    /// <summary>Joins PDFs in the given order into <paramref name="output"/>.</summary>
    /// <returns>The number of pages written.</returns>
    public static Task<int> MergeAsync(IReadOnlyList<string> inputs, string output, PdfMergeOptions? options = null,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            if (inputs.Count == 0)
                throw new ArgumentException("Nothing to merge.", nameof(inputs));
            SafeOutput.EnsureNotInput(inputs, output);
            options ??= new PdfMergeOptions();
            using var merged = new PdfDocument();
            for (var f = 0; f < inputs.Count; f++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var source = PdfSharpIO.Open(inputs[f], PdfDocumentOpenMode.Import);
                if (f == 0)
                    CopyInfo(source, merged);
                merged.Version = Math.Max(merged.Version, source.Version);
                var map = new Dictionary<PdfPage, PdfPage>(ReferenceEqualityComparer.Instance);
                for (var i = 0; i < source.PageCount; i++)
                    map[source.Pages[i]] = merged.AddPage(source.Pages[i]);

                if (source.PageCount > 0)
                {
                    Func<PdfOutlineCollection> parent = () => merged.Outlines;
                    if (options.BookmarkEachFile)
                    {
                        var fileBookmark = merged.Outlines.Add(Petal.Core.Formats.Formats.BaseName(inputs[f]), map[source.Pages[0]], true);
                        parent = () => fileBookmark.Outlines;
                    }
                    if (options.KeepBookmarks)
                        CopyOutlines(source.Outlines, parent, map);
                }
                progress?.Report((f + 1.0) / inputs.Count);
            }
            // PDFsharp locks a document once saved, so count first.
            var count = merged.PageCount;
            SafeOutput.Write(output, temp => PdfSharpIO.Save(merged, temp));
            return count;
        }, cancellationToken);

    /// <summary>
    /// Copies the bookmarks whose page is in the new document. <paramref name="to"/> is only called
    /// when a bookmark is added: PDFsharp writes a reference to an empty outline tree but not the
    /// tree itself, a dangling object that strict readers (qpdf, Acrobat's repair) report as damage.
    /// </summary>
    private static void CopyOutlines(PdfOutlineCollection from, Func<PdfOutlineCollection> to, Dictionary<PdfPage, PdfPage> map)
    {
        foreach (var outline in from)
        {
            try
            {
                var page = outline.DestinationPage is { } dest && map.TryGetValue(dest, out var mapped) ? mapped : null;
                if (page is null)
                {
                    // Bookmarks without a resolvable page still keep their children.
                    if (outline.HasChildren)
                        CopyOutlines(outline.Outlines, to, map);
                    continue;
                }
                var copy = to().Add(outline.Title, page, outline.Opened);
                if (outline.HasChildren)
                    CopyOutlines(outline.Outlines, () => copy.Outlines, map);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // A malformed bookmark is skipped, not fatal.
            }
        }
    }

    /// <summary>
    /// Writes a new PDF with the pages in the given order. Pages can repeat; pages left out are
    /// removed. Rotation is added to each page's own rotation.
    /// </summary>
    public static Task OrganizeAsync(string input, string output, IReadOnlyList<PdfPageOperation> pages, PdfOrganizeOptions? options = null,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            SafeOutput.EnsureNotInput(input, output);
            if (pages.Count == 0)
                throw new ArgumentException("A PDF needs at least one page.", nameof(pages));
            options ??= new PdfOrganizeOptions();
            using var source = PdfSharpIO.Open(input, PdfDocumentOpenMode.Import, options.Password);
            using var result = new PdfDocument();
            result.Version = Math.Max(result.Version, source.Version);
            CopyInfo(source, result);
            var map = new Dictionary<PdfPage, PdfPage>(ReferenceEqualityComparer.Instance);
            for (var i = 0; i < pages.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var op = pages[i];
                if (op.SourcePageIndex < 0 || op.SourcePageIndex >= source.PageCount)
                    throw new ArgumentOutOfRangeException(nameof(pages), $"Page {op.SourcePageIndex + 1} does not exist; the PDF has {source.PageCount}.");
                if (op.Rotation % 90 != 0)
                    throw new ArgumentException("Rotation must be a multiple of 90 degrees.", nameof(pages));
                var sourcePage = source.Pages[op.SourcePageIndex];
                var page = result.AddPage(sourcePage);
                map.TryAdd(sourcePage, page);
                page.Rotate = (((page.Rotate + op.Rotation) % 360) + 360) % 360;
                progress?.Report(0.8 * (i + 1) / pages.Count);
            }
            if (options.UniformWidth)
                MakeUniformWidth(result);
            CopyOutlines(source.Outlines, () => result.Outlines, map);
            SafeOutput.Write(output, temp => PdfSharpIO.Save(result, temp));
            progress?.Report(1);
        }, cancellationToken);

    /// <summary>Scales pages proportionally so their displayed widths all match the narrowest page.</summary>
    internal static void MakeUniformWidth(PdfDocument document)
    {
        static double DisplayedWidth(PdfPage p)
        {
            var box = PdfSharpIO.VisibleBox(p);
            return p.Rotate is 90 or 270 ? box.Height : box.Width;
        }
        var target = document.Pages.Cast<PdfPage>().Min(DisplayedWidth);
        foreach (PdfPage page in document.Pages)
        {
            var width = DisplayedWidth(page);
            if (width <= 0 || width - target < 0.5)
                continue;
            ScalePage(page, target / width);
        }
    }

    private static void ScalePage(PdfPage page, double s)
    {
        var inv = CultureInfo.InvariantCulture;
        var prefix = page.Contents.PrependContent();
        prefix.CreateStream(Encoding.ASCII.GetBytes($"q {s.ToString("0.######", inv)} 0 0 {s.ToString("0.######", inv)} 0 0 cm\n"));
        var suffix = page.Contents.AppendContent();
        suffix.CreateStream(Encoding.ASCII.GetBytes("\nQ\n"));

        foreach (var key in new[] { "/MediaBox", "/CropBox", "/BleedBox", "/TrimBox", "/ArtBox" })
        {
            if (page.Elements.GetRectangle(key) is { IsZero: false } box && page.Elements.ContainsKey(key))
                page.Elements.SetRectangle(key, Scale(box, s));
        }
        // Only pages that have annotations: reading page.Annotations would add an empty array object.
        if (!page.Elements.ContainsKey("/Annots"))
            return;
        foreach (var annotation in page.Annotations.OfType<PdfAnnotation>())
        {
            if (annotation.Elements.ContainsKey("/Rect"))
                annotation.Rectangle = Scale(annotation.Rectangle, s);
        }
        // Text and graphics shrink with the content matrix; resources are untouched.
    }

    private static PdfRectangle Scale(PdfRectangle r, double s) =>
        new(new XPoint(r.X1 * s, r.Y1 * s), new XPoint(r.X2 * s, r.Y2 * s));

    private static void CopyInfo(PdfDocument from, PdfDocument to)
    {
        foreach (var key in new[] { "/Title", "/Author", "/Subject", "/Keywords", "/Creator" })
        {
            var value = from.Info.Elements.GetString(key);
            if (!string.IsNullOrEmpty(value))
                to.Info.Elements.SetString(key, value);
        }
    }
}
