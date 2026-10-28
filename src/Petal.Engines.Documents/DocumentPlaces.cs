using System.Text;
using DocumentFormat.OpenXml.Packaging;
using Petal.Core.Formats;
using Petal.Engines.Documents.Converters;
using Petal.Engines.Documents.Model;
using Petal.Engines.Documents.Pdf;
using Petal.Engines.Documents.Sheets;
using A = DocumentFormat.OpenXml.Drawing;
using F = Petal.Core.Formats.Formats;
using P = DocumentFormat.OpenXml.Presentation;

namespace Petal.Engines.Documents;

/// <summary>What a piece of a file's words is placed by.</summary>
public enum PlaceKind
{
    /// <summary>A page of a PDF, or of a document laid out as one.</summary>
    Page,
    /// <summary>A slide.</summary>
    Slide,
    /// <summary>Rows of a sheet.</summary>
    Rows,
    /// <summary>A part of a document that has no pages, under its heading.</summary>
    Section,
    /// <summary>A stretch of a recording.</summary>
    Time,
}

/// <summary>
/// Words of a file and where they are. <paramref name="Number"/> is the page or slide (from 1), a sheet's first row, or a
/// section's number in order; a recording's stretch is in <see cref="Start"/> and <see cref="End"/> (seconds).
/// </summary>
public sealed record PlacedText(PlaceKind Kind, int Number, string Text)
{
    /// <summary>The sheet's name, or the section's heading.</summary>
    public string Name { get; init; } = "";

    /// <summary>A sheet's last row.</summary>
    public int Last { get; init; }

    public double Start { get; init; }

    public double End { get; init; }
}

/// <summary>
/// The words of a document with where each is, so an answer can say where it was found: a PDF by its pages (scanned
/// pages read with OCR, running heads and feet left out), slides by their numbers with their notes, sheets by their rows
/// (each with its column's heading), and documents without pages by their headings.
/// </summary>
public static class DocumentPlaces
{
    /// <summary>Rows of a sheet together in one place.</summary>
    private const int RowsTogether = 25;

    /// <summary>A section is parted after about this many words.</summary>
    private const int SectionWords = 400;

    public static bool CanRead(FileFormat format) =>
        format == F.Pdf || format == F.Pptx || DocumentIO.CanRead(format) || Sheet(format);

    private static bool Sheet(FileFormat format) => format == F.Xlsx || format == F.Xls || format == F.Ods || format == F.Csv || format == F.Tsv;

    /// <summary>The places of a file of any kind this reads, but a PDF's (<see cref="PdfPagesAsync"/>).</summary>
    public static List<PlacedText> Read(string path, FileFormat format)
    {
        if (format == F.Pptx)
            return Slides(path);
        if (Sheet(format))
            return Sheets(path, format);
        return Sections(DocumentIO.Read(path, format));
    }

    /// <summary>A PDF's pages, each with its words in reading order.</summary>
    public static async Task<List<PlacedText>> PdfPagesAsync(string path, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var (pages, metadata) = await PdfContent.ExtractAsync(path, new PdfContentOptions { Pictures = false }, progress, cancellationToken).ConfigureAwait(false);
        var doc = PdfLayout.Analyze(pages, metadata, new PdfLayoutOptions { KeepPageBreaks = true });
        var result = new List<PlacedText>();
        var blocks = new List<Block>();
        int page = 1;
        void Flush()
        {
            var text = Words(blocks);
            if (text.Length > 0)
                result.Add(new PlacedText(PlaceKind.Page, page, text));
            blocks.Clear();
        }
        foreach (var block in doc.Blocks)
        {
            if (block is PageBreak)
            {
                Flush();
                page++;
            }
            else
                blocks.Add(block);
        }
        Flush();
        return result;
    }

    /// <summary>
    /// A document or a presentation laid out as it prints, by Word, PowerPoint or LibreOffice, into <paramref name="pdf"/>
    /// (kept, so its pages can be shown), and that PDF's pages read; null when none of them is on this PC or all failed.
    /// Pages are the document's own only then: without them a document has no pages to point to.
    /// </summary>
    public static async Task<List<PlacedText>?> LaidOutAsync(string path, FileFormat format, string pdf, IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var options = DocumentEngineOptions.Default;
        if (format.Family is FormatFamily.Spreadsheet || HighFidelity.AppFor(format) is not { } app || !HighFidelity.Available(format, options))
            return null;
        if (!await HighFidelity.TryPdfAsync(path, app, pdf, options, cancellationToken).ConfigureAwait(false))
            return null;
        var pages = await PdfPagesAsync(pdf, progress, cancellationToken).ConfigureAwait(false);
        // A laid-out presentation's pages are its slides.
        return format == F.Pptx || format == F.Ppt ? pages.Select(p => p with { Kind = PlaceKind.Slide }).ToList() : pages;
    }

    /// <summary>A presentation's slides, each with what its shapes say and its speaker's notes.</summary>
    public static List<PlacedText> Slides(string path)
    {
        var result = new List<PlacedText>();
        using var presentation = PresentationDocument.Open(path, false);
        var part = presentation.PresentationPart;
        var ids = part?.Presentation?.SlideIdList?.Elements<P.SlideId>().ToList() ?? [];
        for (int i = 0; i < ids.Count; i++)
        {
            if (ids[i].RelationshipId?.Value is not { } id || part!.GetPartById(id) is not SlidePart slide)
                continue;
            var text = new StringBuilder();
            foreach (var line in slide.Slide is { } shown ? Lines(shown) : [])
                text.Append(line).Append('\n');
            if (slide.NotesSlidePart?.NotesSlide is { } notes)
            {
                // The notes page shows the slide's number in a placeholder of its own: only the notes' words.
                var said = Lines(notes).Where(l => !int.TryParse(l.Trim(), out _)).ToList();
                if (said.Count > 0)
                    text.Append('\n').Append(string.Join('\n', said)).Append('\n');
            }
            if (text.ToString().Trim().Length > 0)
                result.Add(new PlacedText(PlaceKind.Slide, i + 1, text.ToString().Trim()));
        }
        return result;

        static IEnumerable<string> Lines(DocumentFormat.OpenXml.OpenXmlElement root) =>
            root.Descendants<A.Paragraph>().Select(p => string.Concat(p.Descendants<A.Text>().Select(t => t.Text))).Where(l => l.Trim().Length > 0);
    }

    /// <summary>
    /// A workbook's sheets, some rows at a time, each row as its cells with their columns' headings ("Price: 20"), so a
    /// row read on its own still says what its numbers are.
    /// </summary>
    public static List<PlacedText> Sheets(string path, FileFormat format)
    {
        var workbook = format == F.Xlsx ? ExcelSheets.ReadXlsx(path)
            : format == F.Xls ? ExcelSheets.ReadXls(path)
            : format == F.Ods ? OdsSheets.Read(path)
            : DelimitedText.Read(path, format == F.Tsv ? '\t' : ',');
        var result = new List<PlacedText>();
        foreach (var sheet in workbook.Sheets)
        {
            int top = sheet.Rows.FindIndex(r => r.Any(c => !c.IsEmpty));
            if (top < 0)
                continue;
            var headings = sheet.Rows[top].Select(c => c.DisplayText.Trim()).ToList();
            // A first row of words (not numbers) heads the columns.
            bool headed = headings.Count(h => h.Length > 0) >= 2 && sheet.Rows[top].All(c => !c.IsNumeric);
            var text = new StringBuilder();
            int first = -1, last = -1;
            for (int r = headed ? top + 1 : top; r < sheet.Rows.Count; r++)
            {
                var row = sheet.Rows[r];
                if (row.All(c => c.IsEmpty))
                    continue;
                var cells = row.Select((c, k) => (Heading: headed && k < headings.Count ? headings[k] : "", Value: c.DisplayText.Trim()))
                    .Where(c => c.Value.Length > 0)
                    .Select(c => c.Heading.Length > 0 ? $"{c.Heading}: {c.Value}" : c.Value);
                text.Append(string.Join("; ", cells)).Append('\n');
                if (first < 0)
                    first = r + 1;
                last = r + 1;
                if (last - first + 1 >= RowsTogether)
                    Flush();
            }
            Flush();

            void Flush()
            {
                if (first >= 0 && text.Length > 0)
                    result.Add(new PlacedText(PlaceKind.Rows, first, text.ToString().Trim()) { Name = sheet.Name, Last = last });
                text.Clear();
                first = last = -1;
            }
        }
        return result;
    }

    /// <summary>A document without pages, by its headings: each section under its heading, a long one in parts.</summary>
    private static List<PlacedText> Sections(DocModel doc)
    {
        var result = new List<PlacedText>();
        var blocks = new List<Block>();
        string heading = "";
        int words = 0;
        foreach (var block in doc.Blocks)
        {
            if (block is Heading h && h.Level <= 3 || block is PageBreak || words >= SectionWords)
            {
                Flush();
                if (block is Heading next)
                    heading = next.Inlines.PlainText().Trim();
            }
            if (block is PageBreak)
                continue;
            blocks.Add(block);
            words += Words([block]).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        }
        Flush();
        return result;

        void Flush()
        {
            var text = Words(blocks);
            if (text.Length > 0)
                result.Add(new PlacedText(PlaceKind.Section, result.Count + 1, text) { Name = heading });
            blocks.Clear();
            words = 0;
        }
    }

    /// <summary>
    /// Where a quote's words are on a page of a PDF (from 1): a box for each line of them, as shares of the page's width and
    /// height from its top left. The page's words are searched for the stretch as long as the quote that holds most of
    /// its words, compared folded; none when that is under half of them, or the page has no words of its own (a scan).
    /// </summary>
    public static IReadOnlyList<(double Left, double Top, double Right, double Bottom)> Marks(string pdf, int page, string quote)
    {
        var content = PdfContent.ReadOne(pdf, page - 1);
        var words = content.Words;
        var wanted = quote.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(Fold).Where(w => w.Length > 0).ToList();
        if (words.Count == 0 || wanted.Count == 0 || content.Width <= 0 || content.Height <= 0)
            return [];
        var folded = words.Select(w => Fold(w.Text)).ToList();
        int span = Math.Min(words.Count, wanted.Count + wanted.Count / 5 + 1);
        int best = -1, bestFound = 0;
        for (int from = 0; from + span <= words.Count; from++)
        {
            var left = wanted.GroupBy(w => w).ToDictionary(g => g.Key, g => g.Count());
            int found = 0;
            for (int i = from; i < from + span; i++)
                if (left.TryGetValue(folded[i], out int n) && n > 0)
                {
                    left[folded[i]] = n - 1;
                    found++;
                }
            if (found > bestFound)
                (best, bestFound) = (from, found);
        }
        if (best < 0 || bestFound * 2 < wanted.Count)
            return [];
        var set = wanted.ToHashSet();
        var marked = Enumerable.Range(best, span).Where(i => set.Contains(folded[i])).Select(i => words[i]).ToList();
        // Words on one line become one box.
        var boxes = new List<(double Left, double Top, double Right, double Bottom)>();
        foreach (var line in marked.GroupBy(w => Math.Round(w.CenterY / Math.Max(1, w.Height))))
        {
            double l = line.Min(w => w.Left), r = line.Max(w => w.Right), t = line.Min(w => w.Top), b = line.Max(w => w.Bottom);
            boxes.Add((l / content.Width, t / content.Height, r / content.Width, b / content.Height));
        }
        return boxes;
    }

    /// <summary>A word as compared: letters and digits only, lower case, Arabic letters in one form, no vowel marks.</summary>
    private static string Fold(string word)
    {
        var sb = new StringBuilder(word.Length);
        foreach (var c in word.Normalize(NormalizationForm.FormC))
        {
            if (!char.IsLetterOrDigit(c))
                continue;
            sb.Append(c switch
            {
                'أ' or 'إ' or 'آ' or 'ٱ' => 'ا',
                'ة' => 'ه',
                'ى' => 'ي',
                _ => char.ToLowerInvariant(c),
            });
        }
        return sb.ToString();
    }

    /// <summary>Blocks as lines of words: headings, paragraphs, list items ("• "), table rows (cells parted by " | ").</summary>
    private static string Words(IEnumerable<Block> blocks)
    {
        var sb = new StringBuilder();
        Append(blocks, "");
        return sb.ToString().Trim();

        void Append(IEnumerable<Block> items, string lead)
        {
            foreach (var block in items)
            {
                switch (block)
                {
                    case Heading h:
                        Line(lead + h.Inlines.PlainText());
                        break;
                    case Paragraph p:
                        Line(lead + p.Inlines.PlainText());
                        break;
                    case CodeBlock c:
                        Line(c.Text);
                        break;
                    case Quote q:
                        Append(q.Blocks, lead);
                        break;
                    case ListBlock l:
                        foreach (var item in l.Items)
                            Append(item.Blocks, "• ");
                        break;
                    case Table t:
                        foreach (var row in t.Rows)
                            Line(string.Join(" | ", row.Cells.Select(c => c.Blocks.PlainText().Replace('\n', ' ').Trim())));
                        break;
                }
            }
        }

        void Line(string line)
        {
            line = line.Replace('\n', ' ').Trim();
            if (line.Length > 0 && line != "•")
                sb.Append(line).Append('\n');
        }
    }
}
