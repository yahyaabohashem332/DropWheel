using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ImageMagick;
using Petal.Core.Formats;
using Petal.Core.Jobs;
using Petal.Engines.Documents.Pdf;
using Petal.Engines.Documents.Sheets;
using Petal.Engines.Imaging;
using Petal.Engines.Media;
using PageSize = Petal.Engines.Imaging.PdfPageSize;

namespace Petal.Recipes;

public sealed record FolderPdfOptions
{
    public PageSize PageSize { get; init; } = PageSize.FitImage;

    /// <summary>Reads the text of pages that have none (scans, photos of documents) so the PDF can be searched.</summary>
    public bool Searchable { get; init; }

    public string? Title { get; init; }
}

/// <summary>A folder's pictures and PDFs, in the order given, as one PDF.</summary>
public static class FolderPdf
{
    /// <summary>Writes the PDF and returns its page count. Each run of pictures becomes pages; each PDF's pages follow in place.</summary>
    public static async Task<int> CreateAsync(IReadOnlyList<string> files, string output, FolderPdfOptions options,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        if (files.Count == 0)
            throw new ArgumentException("Nothing to put in the PDF.", nameof(files));
        var temp = Directory.CreateTempSubdirectory("petal-folderpdf-").FullName;
        try
        {
            var pictureOptions = new PdfCreateOptions { PageSize = options.PageSize, Title = options.Title };
            bool onlyPictures = files.All(f => Formats.FromPath(f) != Formats.Pdf);
            string joined = options.Searchable ? Path.Combine(temp, "joined.pdf") : output;
            int pages;
            if (onlyPictures)
            {
                pages = await PdfCreator.CreateAsync(files, joined, pictureOptions, Part(progress, 0, options.Searchable ? 0.3 : 1), cancellationToken);
            }
            else
            {
                var parts = new List<string>();
                for (int i = 0; i < files.Count;)
                {
                    if (Formats.FromPath(files[i]) == Formats.Pdf)
                    {
                        parts.Add(files[i++]);
                        continue;
                    }
                    int end = i;
                    while (end < files.Count && Formats.FromPath(files[end]) != Formats.Pdf)
                        end++;
                    var run = Path.Combine(temp, $"pictures {parts.Count}.pdf");
                    await PdfCreator.CreateAsync(files.Skip(i).Take(end - i).ToList(), run, pictureOptions, null, cancellationToken);
                    parts.Add(run);
                    progress?.Report(0.6 * end / files.Count);
                    i = end;
                }
                // The folder's own PDFs take the page size the pictures were given.
                var size = options.PageSize switch { PageSize.A4 => PdfMergePageSize.A4, PageSize.Letter => PdfMergePageSize.Letter, _ => PdfMergePageSize.AsTheyAre };
                pages = await PdfPageTools.MergeAsync(parts, joined, new PdfMergeOptions(PageSize: size), Part(progress, 0.6, options.Searchable ? 0.7 : 1), cancellationToken);
            }
            if (options.Searchable)
            {
                var result = await SearchablePdf.MakeAsync(joined, output, null, Part(progress, 0.7, 1), cancellationToken);
                // Every page already had text: the joined PDF is the result.
                if (!result.Written)
                    File.Copy(joined, output);
            }
            progress?.Report(1);
            return pages;
        }
        finally
        {
            try { Directory.Delete(temp, true); } catch (IOException) { }
        }
    }

    internal static IProgress<double>? Part(IProgress<double>? progress, double from, double to) =>
        progress is null ? null : new Progress<double>(f => progress.Report(from + (to - from) * Math.Clamp(f, 0, 1)));
}

public enum SheetPaper
{
    A4Portrait,
    A4Landscape,
    Letter,
}

public sealed record ContactSheetOptions
{
    public int Columns { get; init; } = 4;

    /// <summary>Each picture's file name under it.</summary>
    public bool Names { get; init; } = true;

    public SheetPaper Paper { get; init; } = SheetPaper.A4Portrait;

    /// <summary>Printed at the top of every page: the folder's name.</summary>
    public string Title { get; init; } = "";

    /// <summary>Pictures run from the right, as an Arabic page reads.</summary>
    public bool RightToLeft { get; init; }

    /// <summary>"Page 2 of 5" in the interface's language.</summary>
    public Func<int, int, string> PageLabel { get; init; } = (page, pages) => $"{page} / {pages}";
}

/// <summary>
/// A contact sheet: every picture of a folder as a small print in a grid, with its name, page after
/// page, saved as a PDF. Drawn with WPF so Arabic names are shaped and run right to left.
/// </summary>
public static class ContactSheet
{
    private const double Dpi = 150;
    private const double Margin = 75, Header = 70, Gap = 18;

    /// <summary>The grid on a page: how many pictures across and down, and each picture's box, in pixels at 150 dpi.</summary>
    public static (int Columns, int Rows, double CellWidth, double PictureHeight, Size Page) Layout(ContactSheetOptions options)
    {
        var page = options.Paper switch
        {
            SheetPaper.A4Landscape => new Size(841.89 / 72 * Dpi, 595.276 / 72 * Dpi),
            SheetPaper.Letter => new Size(612.0 / 72 * Dpi, 792.0 / 72 * Dpi),
            _ => new Size(595.276 / 72 * Dpi, 841.89 / 72 * Dpi),
        };
        int columns = Math.Clamp(options.Columns, 2, 8);
        double cell = (page.Width - 2 * Margin - (columns - 1) * Gap) / columns;
        double picture = cell * 0.75;
        double row = picture + (options.Names ? 32 : 0) + Gap;
        int rows = Math.Max(1, (int)Math.Floor((page.Height - 2 * Margin - Header + Gap) / row));
        return (columns, rows, cell, picture, page);
    }

    /// <summary>Writes the sheet and returns its page count.</summary>
    public static Task<int> CreateAsync(IReadOnlyList<string> pictures, string output, ContactSheetOptions options,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        if (pictures.Count == 0)
            throw new ArgumentException("No pictures for the contact sheet.", nameof(pictures));
        var temp = Directory.CreateTempSubdirectory("petal-sheet-").FullName;
        var task = Sta.RunAsync(() =>
        {
            var (columns, rows, cell, pictureHeight, page) = Layout(options);
            int perPage = columns * rows, pageCount = (pictures.Count + perPage - 1) / perPage;
            var files = new List<string>();
            for (int p = 0; p < pageCount; p++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Decoded a page at a time, so even thousands of pictures need little memory.
                var batch = pictures.Skip(p * perPage).Take(perPage).ToList();
                var thumbs = new BitmapSource?[batch.Count];
                int side = (int)Math.Ceiling(Math.Max(cell, pictureHeight));
                Parallel.For(0, batch.Count, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellationToken }, i =>
                {
                    try
                    {
                        var bitmap = ImagePreview.DecodeBgraAsync(batch[i], side, cancellationToken).GetAwaiter().GetResult();
                        var source = BitmapSource.Create(bitmap.Width, bitmap.Height, 96, 96, PixelFormats.Bgra32, null, bitmap.Pixels, bitmap.Stride);
                        source.Freeze();
                        thumbs[i] = source;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
                    {
                        // An unreadable picture keeps its place, with its name.
                    }
                });
                var image = DrawPage(batch, thumbs, p + 1, pageCount, options, columns, cell, pictureHeight, page);
                var path = Path.Combine(temp, $"page {p + 1:0000}.jpg");
                var encoder = new JpegBitmapEncoder { QualityLevel = 88 };
                encoder.Frames.Add(BitmapFrame.Create(image));
                using (var stream = File.Create(path))
                    encoder.Save(stream);
                files.Add(path);
                progress?.Report(0.9 * (p + 1) / pageCount);
            }
            return files;
        });
        return Finish();

        async Task<int> Finish()
        {
            try
            {
                var pages = await task;
                var paper = options.Paper == SheetPaper.Letter ? PageSize.Letter : PageSize.A4;
                await PdfCreator.CreateAsync(pages, output, new PdfCreateOptions { PageSize = paper, MarginPoints = 0, Title = options.Title }, null, cancellationToken);
                progress?.Report(1);
                return pages.Count;
            }
            finally
            {
                try { Directory.Delete(temp, true); } catch (IOException) { }
            }
        }
    }

    private static BitmapSource DrawPage(IReadOnlyList<string> names, BitmapSource?[] thumbs, int page, int pages, ContactSheetOptions options,
        int columns, double cell, double pictureHeight, Size size)
    {
        var face = new Typeface(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var bold = new Typeface(face.FontFamily, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        var ink = new SolidColorBrush(Color.FromRgb(0x22, 0x24, 0x2A));
        var grey = new SolidColorBrush(Color.FromRgb(0x6E, 0x72, 0x7C));
        var frame = new Pen(new SolidColorBrush(Color.FromRgb(0xE2, 0xE4, 0xE9)), 1.5);
        var shade = new SolidColorBrush(Color.FromRgb(0xF4, 0xF5, 0xF7));
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(size));
            var title = Text(options.Title, bold, 26, ink, size.Width - 2 * Margin - 200);
            var number = Text(options.PageLabel(page, pages), face, 18, grey, 200);
            double titleX = options.RightToLeft ? size.Width - Margin - title.Width : Margin;
            double numberX = options.RightToLeft ? Margin : size.Width - Margin - number.Width;
            dc.DrawText(title, new Point(titleX, Margin + (32 - title.Height) / 2));
            dc.DrawText(number, new Point(numberX, Margin + (32 - number.Height) / 2));
            dc.DrawLine(frame, new Point(Margin, Margin + Header - 22), new Point(size.Width - Margin, Margin + Header - 22));

            double rowHeight = pictureHeight + (options.Names ? 32 : 0) + Gap;
            for (int i = 0; i < names.Count; i++)
            {
                int column = i % columns, row = i / columns;
                if (options.RightToLeft)
                    column = columns - 1 - column;
                double x = Margin + column * (cell + Gap), y = Margin + Header + row * rowHeight;
                var box = new Rect(x, y, cell, pictureHeight);
                dc.DrawRectangle(shade, frame, box);
                if (thumbs[i] is { } thumb)
                {
                    double scale = Math.Min((box.Width - 8) / thumb.PixelWidth, (box.Height - 8) / thumb.PixelHeight);
                    double w = thumb.PixelWidth * scale, h = thumb.PixelHeight * scale;
                    dc.DrawImage(thumb, new Rect(box.X + (box.Width - w) / 2, box.Y + (box.Height - h) / 2, w, h));
                }
                if (options.Names)
                {
                    var name = Text(Path.GetFileName(names[i]), face, 15, grey, cell);
                    dc.DrawText(name, new Point(x + (cell - name.Width) / 2, y + pictureHeight + 6));
                }
            }
        }
        var bitmap = new RenderTargetBitmap((int)Math.Round(size.Width), (int)Math.Round(size.Height), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    // File names are shown as they are: Western digits even in an Arabic line.
    private static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("en-US");
    private static readonly NumberSubstitution Digits = new(NumberCultureSource.Override, Culture, NumberSubstitutionMethod.European);

    /// <summary>
    /// One line, cut short with an ellipsis, running the way its first letter does. Its box is as wide
    /// as the text, so placing the box places the text whichever way it runs.
    /// </summary>
    private static FormattedText Text(string text, Typeface face, double size, Brush brush, double width)
    {
        bool rtl = text.FirstOrDefault(c => char.IsLetter(c)) is var first && first is >= '\u0590' and <= '\u08FF';
        var formatted = new FormattedText(text, Culture, rtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight, face, size, brush, Digits, TextFormattingMode.Ideal, 1)
        {
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis,
        };
        formatted.MaxTextWidth = Math.Max(1, Math.Min(formatted.WidthIncludingTrailingWhitespace + 1, width));
        return formatted;
    }
}

/// <summary>Every file of a folder's tree copied into one folder; a name met twice is numbered.</summary>
public static class Gatherer
{
    public static Task<IReadOnlyList<string>> CopyAsync(IReadOnlyList<string> files, string destination,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default) =>
        Task.Run<IReadOnlyList<string>>(() =>
        {
            Directory.CreateDirectory(destination);
            var copied = new List<string>(files.Count);
            var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < files.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var target = OutputNaming.Unique(destination, Formats.BaseName(files[i]), OutputNaming.ExtensionOf(files[i]), p => taken.Contains(p) || File.Exists(p));
                taken.Add(target);
                File.Copy(files[i], target);
                copied.Add(target);
                progress?.Report((i + 1.0) / files.Count);
            }
            return copied;
        }, cancellationToken);
}

/// <summary>The words of a folder report, in the interface's language.</summary>
public sealed record FolderReportText(
    string FilesSheet, string TypesSheet, string FoldersSheet, string LargestSheet,
    string Name, string Folder, string Type, string Size, string Modified, string Created,
    string Width, string Height, string Duration, string Pages, string Count);

public sealed record FolderReportOptions
{
    /// <summary>Picture and video sizes, durations and page counts: read from every file, so slower.</summary>
    public bool Details { get; init; } = true;

    /// <summary>CSV (the file list only) instead of an Excel workbook.</summary>
    public bool Csv { get; init; }

    public bool RightToLeft { get; init; }
}

/// <summary>
/// A report of a folder: every file with its folder, kind, size and dates (and when asked, picture
/// sizes, durations and pages), then what takes the space: by kind, the largest folders and files.
/// </summary>
public static class FolderReport
{
    /// <summary>Sizes stay numbers (they sort and add up) but read as KB, MB or GB.</summary>
    public const string SizeFormat = "[<1000000]#,##0.0,\" KB\";[<1000000000]#,##0.0,,\" MB\";#,##0.00,,,\" GB\"";

    public static async Task CreateAsync(string root, IReadOnlyList<FolderFile> files, string output, FolderReportOptions options, FolderReportText text,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var rootName = Path.GetFileName(root);
        string FolderOf(FolderFile f) => f.RelativeDirectory.Length == 0 ? rootName : Path.Combine(rootName, f.RelativeDirectory);
        string TypeOf(FolderFile f) => f.Format?.Label ?? (Path.GetExtension(f.Path) is { Length: > 1 } e ? e[1..].ToUpperInvariant() : "—");

        var details = new (int Width, int Height, TimeSpan? Duration, int Pages)[files.Count];
        if (options.Details)
        {
            int done = 0;
            await Parallel.ForEachAsync(Enumerable.Range(0, files.Count), new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellationToken }, async (i, ct) =>
            {
                details[i] = await DetailsAsync(files[i], ct);
                progress?.Report(0.85 * Interlocked.Increment(ref done) / files.Count);
            });
        }

        var headers = new List<string> { text.Name, text.Folder, text.Type, text.Size, text.Modified, text.Created };
        if (options.Details)
            headers.AddRange([text.Width, text.Height, text.Duration, text.Pages]);
        var rows = new List<IReadOnlyList<object?>>(files.Count);
        for (int i = 0; i < files.Count; i++)
        {
            var f = files[i];
            var row = new List<object?> { Path.GetFileName(f.Path), FolderOf(f), TypeOf(f), f.Length, Time(() => File.GetLastWriteTime(f.Path)), Time(() => File.GetCreationTime(f.Path)) };
            if (options.Details)
            {
                var d = details[i];
                row.AddRange([d.Width > 0 ? d.Width : null, d.Height > 0 ? d.Height : null, d.Duration, d.Pages > 0 ? d.Pages : null]);
            }
            rows.Add(row);
        }
        var list = new SheetTable(text.FilesSheet, headers, rows, new Dictionary<int, string> { [3] = SizeFormat });
        if (options.Csv)
        {
            Spreadsheets.WriteCsv(output, list);
            progress?.Report(1);
            return;
        }

        var byType = files.GroupBy(TypeOf).Select(g => (Type: g.Key, Count: g.Count(), Size: g.Sum(f => f.Length))).OrderByDescending(g => g.Size)
            .Select(g => (IReadOnlyList<object?>)[g.Type, g.Count, g.Size]).ToList();
        // A folder's size includes everything below it.
        var folders = new Dictionary<string, (int Count, long Size)>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in files)
        {
            var path = FolderOf(f);
            while (true)
            {
                var (count, size) = folders.GetValueOrDefault(path);
                folders[path] = (count + 1, size + f.Length);
                var up = Path.GetDirectoryName(path);
                if (string.IsNullOrEmpty(up))
                    break;
                path = up;
            }
        }
        var largestFolders = folders.OrderByDescending(p => p.Value.Size).Take(25)
            .Select(p => (IReadOnlyList<object?>)[p.Key, p.Value.Count, p.Value.Size]).ToList();
        var largestFiles = files.OrderByDescending(f => f.Length).Take(25)
            .Select(f => (IReadOnlyList<object?>)[Path.GetFileName(f.Path), FolderOf(f), f.Length]).ToList();
        var sizes = new Dictionary<int, string> { [2] = SizeFormat };
        Spreadsheets.WriteXlsx(output,
        [
            list,
            new SheetTable(text.TypesSheet, [text.Type, text.Count, text.Size], byType, sizes),
            new SheetTable(text.FoldersSheet, [text.Folder, text.Count, text.Size], largestFolders, sizes),
            new SheetTable(text.LargestSheet, [text.Name, text.Folder, text.Size], largestFiles, sizes),
        ], options.RightToLeft, rootName);
        progress?.Report(1);
    }

    private static DateTime? Time(Func<DateTime> read)
    {
        try { return read(); } catch (IOException) { return null; } catch (UnauthorizedAccessException) { return null; }
    }

    private static async Task<(int Width, int Height, TimeSpan? Duration, int Pages)> DetailsAsync(FolderFile file, CancellationToken ct)
    {
        try
        {
            switch (file.Kind)
            {
                case FormatFamily.Image:
                    var info = new MagickImageInfo(file.Path);
                    return ((int)info.Width, (int)info.Height, null, 0);
                case FormatFamily.Video or FormatFamily.Audio:
                    var media = await MediaProbe.ProbeAsync(file.Path, ct);
                    return (media.Width, media.Height, media.Duration > TimeSpan.Zero ? media.Duration : null, 0);
                case FormatFamily.Pdf:
                    return (0, 0, null, PdfInfo.GetPageCount(file.Path));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            // What can't be read stays empty.
        }
        return (0, 0, null, 0);
    }
}

/// <summary>Runs WPF drawing on a thread of its own in a single-threaded apartment, as WPF needs.</summary>
internal static class Sta
{
    public static Task<T> RunAsync<T>(Func<T> work)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { done.SetResult(work()); }
            catch (Exception ex) { done.SetException(ex); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        return done.Task;
    }
}
