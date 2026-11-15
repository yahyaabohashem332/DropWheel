using System.Text.RegularExpressions;
using DocumentFormat.OpenXml.Packaging;
using Petal.Core.Formats;
using Petal.Engines.Documents.Converters;
using Petal.Engines.Documents.Model;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Outline;
using F = Petal.Core.Formats.Formats;
using P = DocumentFormat.OpenXml.Presentation;
using P14 = DocumentFormat.OpenXml.Office2010.PowerPoint;

namespace Petal.Engines.Documents;

/// <summary>A heading found on a PDF page: the page (from 1), the line of the page's text it begins, its level (1 the largest) and its words.</summary>
internal sealed record PageHeading(int Page, int Line, int Level, string Text);

/// <summary>
/// A part of a document under its own heading: the chapter it is in ("" for one that stands on its own), its heading
/// ("" for what comes before the first), and its words by place. A page shared by two parts is in both, each with its share.
/// </summary>
public sealed record DocumentSection(string Chapter, string Title, IReadOnlyList<PlacedText> Places);

/// <summary>A document read for studying: its words by place, its parts when it shows them, and the title it gives itself.</summary>
public sealed record DocumentReading(IReadOnlyList<PlacedText> Places, IReadOnlyList<DocumentSection>? Sections, string? Title);

/// <summary>
/// How a document is built: its chapters and the sections in them, each with its words and where they are. A PDF by its
/// bookmarks (Word puts its headings there when it saves one), else by the headings its type sizes show; a document
/// without pages by its headings; a presentation by the sections PowerPoint keeps. Two levels at most: what is deeper
/// stays in the words of the section it is in.
/// </summary>
public static partial class DocumentOutline
{
    /// <summary>A mark where a part begins: a page, its line (-1: find the heading's words on the page), chapter or section, and its heading.</summary>
    internal sealed record Mark(int Page, int Line, bool Chapter, string Title);

    /// <summary>
    /// A file read for studying: a PDF, a document or presentation as Word, PowerPoint or LibreOffice lays it out
    /// (into <paramref name="laidOut"/>, when one of them is here), a presentation's slides with their notes, or a
    /// document by its headings.
    /// </summary>
    public static async Task<DocumentReading> ReadAsync(string path, FileFormat format, string laidOut, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        if (format == F.Pdf)
            return await PdfAsync(path, progress, cancellationToken).ConfigureAwait(false);
        if (format == F.Pptx)
        {
            // The slides' own words and their speaker's notes, where a teacher's explanation often is.
            var slides = await Task.Run(() => DocumentPlaces.Slides(path), cancellationToken).ConfigureAwait(false);
            progress?.Report(1);
            return new DocumentReading(slides, SlideSections(path, slides), null);
        }
        var options = DocumentEngineOptions.Default;
        if (HighFidelity.AppFor(format) is { } app && format.Family is not FormatFamily.Spreadsheet && HighFidelity.Available(format, options)
            && await HighFidelity.TryPdfAsync(path, app, laidOut, options, cancellationToken).ConfigureAwait(false))
        {
            var reading = await PdfAsync(laidOut, progress, cancellationToken).ConfigureAwait(false);
            // A laid-out presentation's pages are its slides.
            return format == F.Ppt
                ? reading with
                {
                    Places = reading.Places.Select(p => p with { Kind = PlaceKind.Slide }).ToList(),
                    Sections = reading.Sections?.Select(s => s with { Places = s.Places.Select(p => p with { Kind = PlaceKind.Slide }).ToList() }).ToList(),
                    Title = null,
                }
                : reading with { Title = null };
        }
        if (format == F.Ppt)
            throw new InvalidOperationException("Reading a PowerPoint 97–2003 file needs Microsoft PowerPoint or LibreOffice.");
        var doc = await Task.Run(() => DocumentIO.Read(path, format), cancellationToken).ConfigureAwait(false);
        progress?.Report(1);
        var sections = Headed(doc);
        return new DocumentReading(sections?.SelectMany(s => s.Places).ToList() ?? DocumentPlaces.Read(path, format), sections, null);
    }

    /// <summary>A PDF's pages, and its parts: by its bookmarks, else by its headings, else none.</summary>
    public static async Task<DocumentReading> PdfAsync(string path, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var (pages, headings, title) = await DocumentPlaces.PdfReadAsync(path, progress, cancellationToken).ConfigureAwait(false);
        var marks = Bookmarks(path, pages.Count == 0 ? 0 : pages[^1].Number);
        if (marks.Count < 2)
            marks = ByHeadings(pages, headings);
        return new DocumentReading(pages, marks.Count < 2 ? null : Assemble(pages, marks), Titled(title));
    }

    /// <summary>The title a file gives itself, when it is one a person wrote ("Microsoft Word - notes.docx" isn't).</summary>
    internal static string? Titled(string? title)
    {
        var t = title?.Trim() ?? "";
        if (t.Length is < 3 or > 140 || !t.Any(char.IsLetter) || UntitledTitle().IsMatch(t))
            return null;
        return t;
    }

    [GeneratedRegex(@"^(untitled|document\d*|microsoft (word|powerpoint)|presentation\d*|slide \d+)\b|\.(docx?|pptx?|pdf|odt|rtf|tex|dvi|indd|qxd)\b", RegexOptions.IgnoreCase)]
    private static partial Regex UntitledTitle();

    // ───────────── bookmarks ─────────────

    /// <summary>
    /// The PDF's bookmarks as the parts' marks: the top level as chapters, the one below as their sections. A single
    /// bookmark holding all the rest (the book's own title) is gone through; bookmarks that only name pages are no parts.
    /// </summary>
    internal static List<Mark> Bookmarks(string path, int lastPage)
    {
        try
        {
            using var pdf = PdfDocument.Open(path, new ParsingOptions { UseLenientParsing = true, SkipMissingFonts = true });
            if (!pdf.TryGetBookmarks(out var bookmarks) || bookmarks is null)
                return [];
            var roots = bookmarks.Roots.ToList();
            for (int i = 0; i < 2 && roots.Count == 1 && roots[0].Children.Count >= 2; i++)
                roots = roots[0].Children.ToList();
            var marks = new List<Mark>();
            foreach (var root in roots)
            {
                if (PageOf(root) is not { } page || Clean(root.Title) is not { Length: > 0 } title)
                    continue;
                marks.Add(new Mark(page, -1, true, title));
                foreach (var child in root.Children)
                    if (PageOf(child) is { } at && Clean(child.Title) is { Length: > 0 } heading)
                        marks.Add(new Mark(at, -1, false, heading));
            }
            marks = marks.Where(m => m.Page >= 1 && (lastPage == 0 || m.Page <= lastPage)).ToList();
            // "Page 1", "Page 2": a bookmark for every page tells nothing of the parts.
            if (marks.Count(m => PageOnlyTitle().IsMatch(m.Title)) * 2 > marks.Count)
                return [];
            return marks;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
        {
            return [];
        }

        static int? PageOf(BookmarkNode node) => node switch
        {
            DocumentBookmarkNode d when d.PageNumber >= 1 => d.PageNumber,
            // A bookmark that only holds others begins where the first of them does.
            _ => node.Children.Select(PageOf).FirstOrDefault(p => p is not null),
        };
    }

    [GeneratedRegex(@"^\W*(page|p\.|seite|página|sayfa|halaman|صفحة|الصفحة|صفحه)?\s*\d+\W*$", RegexOptions.IgnoreCase)]
    private static partial Regex PageOnlyTitle();

    private static string? Clean(string? title) => title is null ? null : Regex.Replace(title, @"\s+", " ").Trim();

    // ───────────── headings ─────────────

    /// <summary>
    /// Marks from the headings the type sizes show, when they look like a book's: of the levels found, the largest one
    /// that comes at least twice (and not on every other page) gives the chapters, and a smaller one that comes more
    /// often gives their sections; parts of fewer than about a hundred words a time mean they aren't parts at all.
    /// </summary>
    internal static List<Mark> ByHeadings(IReadOnlyList<PlacedText> pages, IReadOnlyList<PageHeading> headings)
    {
        var usable = headings.Where(h => h.Text.Length is >= 2 and <= 150 && h.Text.Any(char.IsLetter) && !PageOnlyTitle().IsMatch(h.Text)).ToList();
        int words = pages.Sum(p => Count(p.Text));
        var (chapters, sections) = Levels(usable.Select(h => h.Level).ToList(), words, pages.Count);
        if (chapters is null)
            return [];
        return usable.Where(h => h.Level == chapters || h.Level == sections)
            .Select(h => new Mark(h.Page, h.Line, h.Level == chapters, h.Text)).ToList();
    }

    /// <summary>The levels that make chapters and sections, of headings at <paramref name="levels"/> in a text of <paramref name="words"/> words.</summary>
    internal static (int? Chapters, int? Sections) Levels(IReadOnlyList<int> levels, int words, int pages)
    {
        // Pages are counted only for a PDF, whose headings are guessed from type sizes.
        bool ChapterLike(int count) => count >= 2 && words / (count + 1) >= 150 && (pages == 0 || count <= Math.Max(40, pages / 3));
        bool SectionLike(int count) => count >= 2 && words / (count + 1) >= 80 && (pages == 0 || count <= Math.Max(80, pages * 2));
        var counts = levels.GroupBy(l => l).OrderBy(g => g.Key).Select(g => (Level: g.Key, Count: g.Count())).ToList();
        int? chapters = null, sections = null;
        int chapterCount = 0;
        foreach (var (level, count) in counts)
        {
            if (chapters is null)
            {
                if (ChapterLike(count))
                    (chapters, chapterCount) = (level, count);
                // A level too fine for chapters may still part the text: the parts stand on their own.
                else if (SectionLike(count))
                    return (level, null);
            }
            else if (count >= chapterCount && SectionLike(count + chapterCount))
            {
                sections = level;
                break;
            }
        }
        return (chapters, sections);
    }

    private static int Count(string text) => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    // ───────────── parts from marks ─────────────

    /// <summary>
    /// The parts the marks begin, in order, each with its share of the pages: a part begins at its heading's line when
    /// its words are found on the page, otherwise at the top of it. What comes before the first mark is a part with no
    /// heading. A chapter with sections keeps the words before the first of them as a part of its own, under its name.
    /// </summary>
    internal static List<DocumentSection> Assemble(IReadOnlyList<PlacedText> pages, IReadOnlyList<Mark> marks)
    {
        var ordered = marks.Select(m => m with { Line = m.Line >= 0 ? m.Line : FindLine(pages, m) })
            .Select((m, i) => (Mark: m, Index: i))
            .OrderBy(x => x.Mark.Page).ThenBy(x => x.Mark.Line).ThenBy(x => x.Index)
            .Select(x => x.Mark).ToList();
        // Which chapters have sections of their own, by where each chapter begins.
        var parented = new HashSet<int>();
        int open = -1;
        for (int i = 0; i < ordered.Count; i++)
        {
            if (ordered[i].Chapter)
                open = i;
            else if (open >= 0)
                parented.Add(open);
        }

        var sections = new List<DocumentSection>();
        string chapter = "", title = "";
        var places = new List<PlacedText>();
        void Close()
        {
            if (places.Count > 0 || title.Length > 0)
                sections.Add(new DocumentSection(chapter, title, places.ToList()));
            places.Clear();
        }
        void Begin(int index)
        {
            Close();
            var mark = ordered[index];
            if (mark.Chapter)
                chapter = parented.Contains(index) ? mark.Title : "";
            title = mark.Title;
        }

        int next = 0;
        foreach (var page in pages)
        {
            // Marks on pages with no words begin at the top of the next page that has some.
            while (next < ordered.Count && ordered[next].Page < page.Number)
                Begin(next++);
            var lines = page.Text.Split('\n');
            int from = 0;
            while (next < ordered.Count && ordered[next].Page == page.Number)
            {
                int at = Math.Clamp(ordered[next].Line, from, lines.Length);
                if (at > from)
                    places.Add(page with { Text = string.Join('\n', lines[from..at]).Trim() });
                Begin(next++);
                from = at;
            }
            if (from < lines.Length && string.Join('\n', lines[from..]).Trim() is { Length: > 0 } rest)
                places.Add(page with { Text = rest });
        }
        while (next < ordered.Count)
            Begin(next++);
        Close();
        return sections.Where(s => s.Places.Count > 0 || s.Title.Length > 0).ToList();
    }

    /// <summary>The line of its page a bookmark's heading is on: its words, with or without its number; the top of the page when they aren't there.</summary>
    internal static int FindLine(IReadOnlyList<PlacedText> pages, Mark mark)
    {
        var page = pages.FirstOrDefault(p => p.Number == mark.Page);
        if (page is null)
            return 0;
        var lines = page.Text.Split('\n');
        var wanted = new[] { Folded(mark.Title), Folded(Unnumbered(mark.Title)) }.Where(w => w.Length > 0).Distinct().ToList();
        if (wanted.Count == 0)
            return 0;
        for (int i = 0; i < lines.Length; i++)
        {
            var line = Folded(lines[i]);
            if (line.Length == 0)
                continue;
            var bare = Folded(Unnumbered(lines[i]));
            // A heading may be broken over two lines.
            var after = i + 1 < lines.Length ? Folded(lines[i + 1]) : "";
            foreach (var w in wanted)
            {
                if (line == w || bare == w || line + after == w || bare.Length > 0 && bare + after == w)
                    return i;
                // The heading's words and a little more on its line ("Cells 23", a page number), or its start on this line and the rest on the next.
                if (w.Length >= 6 && (line.StartsWith(w, StringComparison.Ordinal) && line.Length <= w.Length * 2
                    || line.Length >= 4 && w.StartsWith(line, StringComparison.Ordinal) && (line + after).StartsWith(w, StringComparison.Ordinal)))
                    return i;
            }
        }
        return 0;
    }

    private static string Folded(string text) => string.Concat(text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(DocumentPlaces.Fold));

    /// <summary>A heading without the number it begins with ("3.2 ", "Chapter 3: ", "IV. ").</summary>
    internal static string Unnumbered(string title) => LeadingNumber().Replace(title, "").Trim();

    [GeneratedRegex(@"^\s*(?:(?:chapter|part|section|unit|lesson|lecture|chapitre|partie|capítulo|parte|lección|unidad|bölüm|kısım|ünite|ders|bab|bagian|pelajaran|الفصل|الباب|الجزء|الدرس|الوحدة|المحاضرة|فصل|بخش|درس|باب|حصہ)\s+)?(?:[0-9٠-٩۰-۹]+(?:[.\-–][0-9٠-٩۰-۹]+)*|(?-i:[IVXLC]+))\s*[.:)\-–]?\s+", RegexOptions.IgnoreCase)]
    private static partial Regex LeadingNumber();

    // ───────────── documents without pages ─────────────

    /// <summary>A document's parts by its headings, two levels at most; null when it has none to go by.</summary>
    internal static List<DocumentSection>? Headed(DocModel doc)
    {
        var headings = doc.Blocks.OfType<Heading>().Where(h => h.Inlines.PlainText().Trim().Length > 0).ToList();
        int words = Count(DocumentPlaces.Words(doc.Blocks));
        var (chapters, sections) = Levels(headings.Select(h => h.Level).ToList(), words, 0);
        if (chapters is null)
            return null;
        var result = new List<DocumentSection>();
        string chapter = "", title = "";
        var blocks = new List<Block>();
        var places = new List<PlacedText>();
        int number = 0, inPlace = 0;
        // Which chapters have sections, to know whether a chapter groups parts or is one.
        var parented = new HashSet<Heading>();
        Heading? open = null;
        foreach (var h in headings)
        {
            if (h.Level == chapters)
                open = h;
            else if (h.Level == sections && open is not null)
                parented.Add(open);
        }
        void Place()
        {
            var text = DocumentPlaces.Words(blocks);
            if (text.Length > 0)
                places.Add(new PlacedText(PlaceKind.Section, ++number, text) { Name = title });
            blocks.Clear();
            inPlace = 0;
        }
        void Close()
        {
            Place();
            if (places.Count > 0 || title.Length > 0)
                result.Add(new DocumentSection(chapter, title, places.ToList()));
            places.Clear();
        }
        foreach (var block in doc.Blocks)
        {
            if (block is Heading h && (h.Level == chapters || h.Level == sections) && h.Inlines.PlainText().Trim() is { Length: > 0 } words2)
            {
                Close();
                if (h.Level == chapters)
                    chapter = parented.Contains(h) ? words2 : "";
                title = words2;
            }
            if (block is PageBreak)
                continue;
            blocks.Add(block);
            // A long section is placed some four hundred words at a time, all under its heading.
            inPlace += Count(DocumentPlaces.Words([block]));
            if (inPlace >= 400)
                Place();
        }
        Close();
        return result.Count >= 2 ? result : null;
    }

    // ───────────── presentations ─────────────

    /// <summary>The sections PowerPoint keeps (named runs of slides), each with its slides' words; null when there are fewer than two.</summary>
    internal static List<DocumentSection>? SlideSections(string path, IReadOnlyList<PlacedText> slides)
    {
        try
        {
            using var presentation = PresentationDocument.Open(path, false);
            var part = presentation.PresentationPart;
            var ids = part?.Presentation?.SlideIdList?.Elements<P.SlideId>().Select(s => s.Id?.Value ?? 0).ToList() ?? [];
            var named = part?.Presentation?.Descendants<P14.Section>().ToList() ?? [];
            if (named.Count < 2)
                return null;
            var result = new List<DocumentSection>();
            foreach (var section in named)
            {
                var numbers = section.Descendants<P14.SectionSlideIdListEntry>().Select(e => ids.IndexOf(e.Id?.Value ?? 0) + 1).Where(n => n > 0).ToHashSet();
                var places = slides.Where(s => numbers.Contains(s.Number)).ToList();
                if (places.Count > 0)
                    result.Add(new DocumentSection("", Clean(section.Name?.Value) ?? "", places));
            }
            return result.Count >= 2 ? result : null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }
}
