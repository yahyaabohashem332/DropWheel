using System.IO;
using System.Net;
using System.Text;
using Petal.Core.Formats;
using Petal.Core.Jobs;
using Petal.Engines.Documents;
using Petal.Engines.Documents.Pdf;
using SkiaSharp;

namespace Petal.Recipes;

/// <summary>The words of a comparison report, in the interface's language.</summary>
public sealed record ComparisonText(
    string Title, string Before, string After, string NoTextChange, string NoPictureChange, string TextHeading, string PagesHeading,
    Func<int, string> AddedWords, Func<int, string> RemovedWords, Func<int, string> UnchangedParagraphs, Func<int, string> Page,
    string OnlyBefore, string OnlyAfter, string MorePages, bool RightToLeft, string Language = "en");

/// <summary>How much differs: words added and removed, and pages (or the picture) that look different.</summary>
public sealed record ComparisonResult(int AddedWords, int RemovedWords, int ChangedPages, bool HasText)
{
    public bool Same => AddedWords == 0 && RemovedWords == 0 && ChangedPages == 0;
}

/// <summary>
/// Two versions of a file side by side, in one page that opens in the browser: for documents (Word, PDF,
/// text, e-books) the words added and removed, marked in place; for PDFs also every page drawn and the parts
/// that look different boxed; for two pictures, where they differ. Everything is read and drawn on this PC.
/// </summary>
public static class FileComparison
{
    /// <summary>Pages drawn and compared at most; text is compared in full.</summary>
    public const int MostPages = 40;

    private const int PageWidth = 820;

    public static bool CanCompare(FileFormat a, FileFormat b, ConverterRegistry converters) =>
        (IsPicture(a) && IsPicture(b)) || (DocumentText.CanRead(a, converters) && DocumentText.CanRead(b, converters) && IsDocument(a) && IsDocument(b));

    private static bool IsPicture(FileFormat f) => f.Family == FormatFamily.Image && f != Formats.Svg;

    private static bool IsDocument(FileFormat f) => f.Family is FormatFamily.Pdf or FormatFamily.Document or FormatFamily.Text or FormatFamily.Ebook;

    public static async Task<ComparisonResult> CompareAsync(string before, string after, string output, ConverterRegistry converters, ConversionSettings settings,
        ComparisonText text, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var formatA = Formats.FromPath(before) ?? throw new InvalidOperationException($"{Path.GetFileName(before)}: DropWheel doesn't know this kind of file.");
        var formatB = Formats.FromPath(after) ?? throw new InvalidOperationException($"{Path.GetFileName(after)}: DropWheel doesn't know this kind of file.");
        List<DiffParagraph>? diff = null;
        var pictures = new List<(int Index, string? Before, string? After, bool Changed)>();
        int pageCount = 0;
        if (IsPicture(formatA) && IsPicture(formatB))
        {
            progress?.Report(0.2);
            var pair = await Task.Run(() => ComparePictures(before, after), cancellationToken);
            pictures.Add((0, pair.Before, pair.After, pair.Changed));
            pageCount = 1;
        }
        else
        {
            var textA = await DocumentText.ReadAsync(before, converters, settings, Part(progress, 0, 0.3), cancellationToken);
            var textB = await DocumentText.ReadAsync(after, converters, settings, Part(progress, 0.3, 0.6), cancellationToken);
            diff = await Task.Run(() => TextDiff.Compare(textA, textB), cancellationToken);
            if (formatA.Family == FormatFamily.Pdf && formatB.Family == FormatFamily.Pdf)
            {
                (pictures, pageCount) = await Task.Run(() => ComparePages(before, after, Part(progress, 0.6, 0.95), cancellationToken), cancellationToken);
            }
        }
        int added = 0, removed = 0;
        foreach (var part in diff?.SelectMany(p => p.Parts) ?? [])
        {
            int words = TextDiff.Tokens(part.Text).Count(t => !string.IsNullOrWhiteSpace(t));
            if (part.Kind == DiffKind.Added)
                added += words;
            else if (part.Kind == DiffKind.Removed)
                removed += words;
        }
        var result = new ComparisonResult(added, removed, pictures.Count(p => p.Changed), diff is not null);
        await File.WriteAllTextAsync(output, Html(before, after, diff, pictures, pageCount, result, text), Encoding.UTF8, cancellationToken);
        progress?.Report(1);
        return result;
    }

    private static IProgress<double>? Part(IProgress<double>? progress, double from, double to) =>
        progress is null ? null : new Progress<double>(f => progress.Report(from + (to - from) * Math.Clamp(f, 0, 1)));

    // ───────────────────────────── pictures ─────────────────────────────

    private static (string? Before, string? After, bool Changed) ComparePictures(string before, string after)
    {
        using var a = Decode(before);
        using var b = Decode(after);
        return Mark(a, b);
    }

    /// <summary>A picture at most <see cref="PageWidth"/> wide (bigger ones are compared at that size).</summary>
    private static SKBitmap Decode(string path)
    {
        // Through the picture reader every tool uses, so HEIC, TIFF, RAW and the rest open too.
        var png = Petal.Engines.Imaging.ImagePreview.DecodePngAsync(path, PageWidth * 2).GetAwaiter().GetResult();
        using var raw = SKBitmap.Decode(png) ?? throw new InvalidOperationException($"{Path.GetFileName(path)} can't be opened.");
        int w = Math.Min(raw.Width, PageWidth * 2);
        int h = Math.Max(1, (int)Math.Round(raw.Height * (w / (double)raw.Width)));
        var info = new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
        var bitmap = new SKBitmap(info);
        using var canvas = new SKCanvas(bitmap);
        // Transparent parts compare as white, the way they are usually seen.
        canvas.Clear(SKColors.White);
        using var image = SKImage.FromBitmap(raw);
        canvas.DrawImage(image, SKRect.Create(0, 0, w, h), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
        return bitmap;
    }

    private static (List<(int, string?, string?, bool)>, int) ComparePages(string before, string after, IProgress<double>? progress, CancellationToken ct)
    {
        using var a = PdfDocumentView.Open(before);
        using var b = PdfDocumentView.Open(after);
        int pages = Math.Max(a.PageCount, b.PageCount);
        int drawn = Math.Min(pages, MostPages);
        var result = new List<(int, string?, string?, bool)>();
        for (int i = 0; i < drawn; i++)
        {
            ct.ThrowIfCancellationRequested();
            using var pa = i < a.PageCount ? ToBitmap(a.RenderPage(i, PageWidth)) : null;
            using var pb = i < b.PageCount ? ToBitmap(b.RenderPage(i, PageWidth)) : null;
            if (pa is null || pb is null)
                result.Add((i, pa is null ? null : Png(pa), pb is null ? null : Png(pb), true));
            else
            {
                var (x, y, changed) = Mark(pa, pb);
                result.Add((i, x, y, changed));
            }
            progress?.Report((i + 1.0) / drawn);
        }
        return (result, pages);
    }

    private static SKBitmap ToBitmap(PdfPageBitmap page)
    {
        var bitmap = new SKBitmap(new SKImageInfo(page.Width, page.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        var pixels = bitmap.GetPixelSpan();
        for (int y = 0; y < page.Height; y++)
            page.Pixels.AsSpan(y * page.Stride, page.Width * 4).CopyTo(pixels.Slice(y * bitmap.RowBytes, page.Width * 4));
        return bitmap;
    }

    /// <summary>
    /// The two pictures (the second scaled to the first's width) with the places that differ boxed: red on the
    /// old one, green on the new one. A difference is a small square where several pixels clearly changed,
    /// so smoothing and compression don't count.
    /// </summary>
    internal static (string Before, string After, bool Changed) Mark(SKBitmap a, SKBitmap b)
    {
        using var scaled = b.Width == a.Width ? null : b.Resize(new SKImageInfo(a.Width, Math.Max(1, (int)Math.Round(b.Height * (a.Width / (double)b.Width))), SKColorType.Bgra8888, SKAlphaType.Premul), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
        var other = scaled ?? b;
        var cells = ChangedCells(a, other, out int cell);
        bool changed = cells.Count > 0;
        return (Png(a, cells, cell, new SKColor(0xE5, 0x39, 0x35)), Png(other, cells, cell, new SKColor(0x16, 0xA3, 0x4A)), changed);
    }

    /// <summary>The squares (by column and row) where the pictures differ; past the shorter one's end everything differs.</summary>
    internal static HashSet<(int X, int Y)> ChangedCells(SKBitmap a, SKBitmap b, out int cell)
    {
        cell = Math.Max(8, a.Width / 80);
        int width = Math.Min(a.Width, b.Width), height = Math.Max(a.Height, b.Height), common = Math.Min(a.Height, b.Height);
        var pa = a.GetPixelSpan();
        var pb = b.GetPixelSpan();
        var counts = new Dictionary<(int, int), int>();
        for (int y = 0; y < common; y++)
        {
            int ra = y * a.RowBytes, rb = y * b.RowBytes;
            for (int x = 0; x < width; x++)
            {
                int i = ra + x * 4, j = rb + x * 4;
                int d = Math.Max(Math.Abs(pa[i] - pb[j]), Math.Max(Math.Abs(pa[i + 1] - pb[j + 1]), Math.Abs(pa[i + 2] - pb[j + 2])));
                if (d > 48)
                {
                    var key = (x / cell, y / cell);
                    counts[key] = counts.GetValueOrDefault(key) + 1;
                }
            }
        }
        var cells = counts.Where(c => c.Value >= 3).Select(c => c.Key).ToHashSet();
        for (int y = common; y < height; y += cell)
            for (int x = 0; x < width; x += cell)
                cells.Add((x / cell, y / cell));
        return cells;
    }

    private static string Png(SKBitmap bitmap, HashSet<(int X, int Y)>? cells = null, int cell = 0, SKColor colour = default)
    {
        using var surface = SKSurface.Create(new SKImageInfo(bitmap.Width, bitmap.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.White);
        canvas.DrawBitmap(bitmap, 0, 0);
        if (cells is { Count: > 0 })
        {
            using var fill = new SKPaint { Color = colour.WithAlpha(60) };
            using var edge = new SKPaint { Color = colour.WithAlpha(200), Style = SKPaintStyle.Stroke, StrokeWidth = 2 };
            using var path = new SKPathBuilder();
            foreach (var (x, y) in cells)
                path.AddRect(SKRect.Create(x * cell - 2, y * cell - 2, cell + 4, cell + 4));
            using var region = path.Detach();
            // The squares joined into one outline, so a changed line reads as one box.
            using var simple = region.Simplify() ?? region;
            canvas.DrawPath(simple, fill);
            canvas.DrawPath(simple, edge);
        }
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
        return "data:image/png;base64," + Convert.ToBase64String(data.Span);
    }

    // ───────────────────────────── the page ─────────────────────────────

    private static string Html(string before, string after, List<DiffParagraph>? diff, List<(int Index, string? Before, string? After, bool Changed)> pictures,
        int pageCount, ComparisonResult result, ComparisonText text)
    {
        static string E(string s) => WebUtility.HtmlEncode(s);
        var sb = new StringBuilder();
        sb.Append("<!doctype html><html lang=\"").Append(text.Language).Append("\" dir=\"").Append(text.RightToLeft ? "rtl" : "ltr").Append("\"><head><meta charset=\"utf-8\">");
        sb.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\"><title>").Append(E(text.Title)).Append("</title><style>");
        sb.Append(Style);
        sb.Append("</style></head><body><header><h1>").Append(E(text.Title)).Append("</h1><div class=\"files\">");
        sb.Append("<div><span class=\"tag old\">").Append(E(text.Before)).Append("</span> <bdi>").Append(E(Path.GetFileName(before))).Append("</bdi></div>");
        sb.Append("<div><span class=\"tag new\">").Append(E(text.After)).Append("</span> <bdi>").Append(E(Path.GetFileName(after))).Append("</bdi></div></div>");
        sb.Append("<div class=\"counts\">");
        if (result.HasText)
        {
            sb.Append("<span class=\"count new\">").Append(E(text.AddedWords(result.AddedWords))).Append("</span>");
            sb.Append("<span class=\"count old\">").Append(E(text.RemovedWords(result.RemovedWords))).Append("</span>");
        }
        sb.Append("</div></header><main>");

        if (diff is not null)
        {
            sb.Append("<h2>").Append(E(text.TextHeading)).Append("</h2>");
            if (!diff.Any(p => p.Changed))
                sb.Append("<p class=\"note\">").Append(E(text.NoTextChange)).Append("</p>");
            else
                AppendText(sb, diff, text);
        }
        if (pictures.Count > 0)
        {
            if (diff is not null)
                sb.Append("<h2>").Append(E(text.PagesHeading)).Append("</h2>");
            if (pictures.All(p => !p.Changed))
                sb.Append("<p class=\"note\">").Append(E(text.NoPictureChange)).Append("</p>");
            foreach (var (index, a, b, changed) in pictures.Where(p => p.Changed))
            {
                if (pageCount > 1)
                    sb.Append("<h3>").Append(E(text.Page(index + 1))).Append("</h3>");
                sb.Append("<div class=\"pair\">");
                sb.Append(a is null ? "<div class=\"missing\">" + E(text.OnlyAfter) + "</div>" : "<figure><img alt=\"\" src=\"" + a + "\"><figcaption>" + E(text.Before) + "</figcaption></figure>");
                sb.Append(b is null ? "<div class=\"missing\">" + E(text.OnlyBefore) + "</div>" : "<figure><img alt=\"\" src=\"" + b + "\"><figcaption>" + E(text.After) + "</figcaption></figure>");
                sb.Append("</div>");
            }
            if (pageCount > MostPages)
                sb.Append("<p class=\"note\">").Append(E(text.MorePages)).Append("</p>");
        }
        sb.Append("</main></body></html>");
        return sb.ToString();
    }

    /// <summary>The text with changes marked; long unchanged stretches folded away, a little kept around each change.</summary>
    private static void AppendText(StringBuilder sb, List<DiffParagraph> diff, ComparisonText text)
    {
        static string E(string s) => WebUtility.HtmlEncode(s);
        const int Around = 1;
        var keep = new bool[diff.Count];
        for (int i = 0; i < diff.Count; i++)
        {
            if (!diff[i].Changed)
                continue;
            for (int j = Math.Max(0, i - Around); j <= Math.Min(diff.Count - 1, i + Around); j++)
                keep[j] = true;
        }
        int at = 0;
        while (at < diff.Count)
        {
            if (keep[at])
            {
                sb.Append("<p dir=\"auto\"").Append(diff[at].Changed ? " class=\"changed\"" : "").Append('>');
                foreach (var part in diff[at].Parts)
                {
                    sb.Append(part.Kind switch
                    {
                        DiffKind.Added => "<ins>" + E(part.Text) + "</ins>",
                        DiffKind.Removed => "<del>" + E(part.Text) + "</del>",
                        _ => E(part.Text),
                    });
                }
                sb.Append("</p>");
                at++;
                continue;
            }
            int end = at;
            while (end < diff.Count && !keep[end])
                end++;
            sb.Append("<details><summary>").Append(E(text.UnchangedParagraphs(end - at))).Append("</summary>");
            for (int i = at; i < end; i++)
                sb.Append("<p dir=\"auto\">").Append(E(diff[i].Parts[0].Text)).Append("</p>");
            sb.Append("</details>");
            at = end;
        }
    }

    private const string Style = """
        :root{--bg:#f6f6f8;--card:#fff;--text:#1c1c22;--muted:#6b6b76;--line:#e3e3e8;--add:#d7f5df;--addText:#0b5d2a;--del:#ffe0e0;--delText:#9f1d1d}
        @media (prefers-color-scheme:dark){:root{--bg:#16161a;--card:#202026;--text:#ececf1;--muted:#a0a0ab;--line:#33333b;--add:#173d25;--addText:#9ee6b4;--del:#4a1f22;--delText:#ffb4b4}}
        *{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--text);font:15px/1.65 "Segoe UI",system-ui,sans-serif}
        header{background:var(--card);border-bottom:1px solid var(--line);padding:24px 32px}h1{margin:0 0 12px;font-size:22px}
        .files{display:grid;gap:4px;color:var(--muted)}.tag{display:inline-block;min-width:64px;font-size:12px;font-weight:600;border-radius:6px;padding:1px 8px;text-align:center}
        .tag.old{background:var(--del);color:var(--delText)}.tag.new{background:var(--add);color:var(--addText)}
        .counts{display:flex;gap:10px;margin-top:14px;flex-wrap:wrap}.count{font-weight:600;border-radius:8px;padding:4px 12px}.count.new{background:var(--add);color:var(--addText)}.count.old{background:var(--del);color:var(--delText)}
        main{max-width:1100px;margin:0 auto;padding:16px 32px 48px}h2{font-size:17px;margin:28px 0 10px}h3{font-size:14px;color:var(--muted);margin:22px 0 8px}
        p{background:var(--card);border:1px solid var(--line);border-radius:10px;padding:10px 14px;margin:8px 0;white-space:pre-wrap;overflow-wrap:anywhere}
        p.changed{border-color:#c9a227}ins{background:var(--add);color:var(--addText);text-decoration:none;border-radius:3px}
        del{background:var(--del);color:var(--delText);border-radius:3px}
        details{margin:8px 0}summary{cursor:pointer;color:var(--muted);font-size:13px;padding:4px 2px}.note{color:var(--muted);background:none;border:0;padding:0}
        .pair{display:grid;grid-template-columns:1fr 1fr;gap:14px}figure{margin:0}img{width:100%;border:1px solid var(--line);border-radius:8px;background:#fff}
        figcaption{font-size:12px;color:var(--muted);text-align:center;margin-top:4px}.missing{display:flex;align-items:center;justify-content:center;border:1px dashed var(--line);border-radius:8px;color:var(--muted);min-height:160px}
        @media (max-width:700px){header,main{padding-left:16px;padding-right:16px}.pair{grid-template-columns:1fr}}
        """;
}
