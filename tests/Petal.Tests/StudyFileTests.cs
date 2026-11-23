using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Petal.App.Localization;
using Petal.App.Tools.Windows;
using Petal.Core.Formats;
using Petal.Engines.Documents;
using Petal.Engines.Documents.Pdf;
using Petal.Recipes.Study;
using PdfSharp.Drawing;
using Xunit.Abstractions;

namespace Petal.Tests;

/// <summary>
/// Study File: a book read with its own chapters, explained a passage at a time (what the model writes read carefully,
/// kept so a long book carries on), and written as one page that opens anywhere, loads nothing and reads in every
/// language of the interface.
/// </summary>
[Collection("Language")]
public sealed partial class StudyFileTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "Petal", "studyfile-test-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        StudyFile.TestWriter = null;
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    internal static PlacedText Page(int number, string text) => new(PlaceKind.Page, number, text);

    private static string Words(int count, string seed) => string.Join(" ", Enumerable.Range(0, count).Select(i => seed + (i % 7 == 6 ? "." : "")));

    // ───────────── reading a book's own structure ─────────────

    /// <summary>A small book: a cover, two chapters, the first with a section that begins half way down a page.</summary>
    private string Book(bool bookmarks)
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, bookmarks ? "marked.pdf" : "plain.pdf");
        PdfFonts.EnsureInitialized();
        using var document = new PdfSharp.Pdf.PdfDocument();
        var body = new XFont("Arial", 11);
        PdfSharp.Pdf.PdfPage NewPage(params (string Text, double Size)[] lines)
        {
            var page = document.AddPage();
            using var gfx = XGraphics.FromPdfPage(page);
            double y = 72;
            foreach (var (text, size) in lines)
            {
                gfx.DrawString(text, size == 11 ? body : new XFont("Arial", size, XFontStyleEx.Bold), XBrushes.Black, 72, y);
                y += size * 1.9;
            }
            return page;
        }
        (string, double)[] Body(string topic, int lines) =>
            Enumerable.Range(0, lines).Select(i => ($"The {topic} line {i} says that cells take in food and turn it into energy they use.", 11.0)).ToArray();
        NewPage(("A Little Biology", 28), ("For schools", 14));
        var chapter1 = NewPage([("Chapter 1 Cells", 20), .. Body("cell", 18)]);
        var section = NewPage([.. Body("wall", 8), ("1.1 Membranes", 15), .. Body("membrane", 10)]);
        NewPage(Body("membrane", 18));
        var chapter2 = NewPage([("Chapter 2 Energy", 20), .. Body("energy", 18)]);
        NewPage([("2.1 Light", 15), .. Body("light", 17)]);
        if (bookmarks)
        {
            var first = document.Outlines.Add("Chapter 1 Cells", chapter1, true);
            first.Outlines.Add("1.1 Membranes", section, true);
            document.Outlines.Add("Chapter 2 Energy", chapter2, true);
        }
        document.Save(path);
        return path;
    }

    [Fact]
    public async Task A_pdf_is_parted_by_its_bookmarks_a_section_beginning_where_its_heading_is()
    {
        var reading = await DocumentOutline.PdfAsync(Book(bookmarks: true));
        var sections = reading.Sections!;
        Assert.Equal(["", "Chapter 1 Cells", "1.1 Membranes", "Chapter 2 Energy"], sections.Select(s => s.Title));
        Assert.Equal(["", "Chapter 1 Cells", "Chapter 1 Cells", ""], sections.Select(s => s.Chapter));
        // Page 3 is shared: its walls stay with the chapter's opening, its membranes go to the section.
        var opening = sections[1];
        var membranes = sections[2];
        Assert.Contains(opening.Places, p => p.Number == 3 && p.Text.Contains("wall line 7") && !p.Text.Contains("membrane"));
        Assert.Equal(3, membranes.Places[0].Number);
        Assert.StartsWith("1.1 Membranes", membranes.Places[0].Text);
        Assert.Equal([3, 4], membranes.Places.Select(p => p.Number));
        // The cover before the first bookmark is a part of its own, with no heading, and isn't ticked at first.
        Assert.Equal([1], sections[0].Places.Select(p => p.Number));
        var plan = StudyFile.Plan(reading, cloud: false);
        Assert.False(plan.Parts[0].Suggested);
        Assert.True(plan.Structured);
    }

    [Fact]
    public async Task Without_bookmarks_a_pdf_is_parted_by_the_headings_its_type_sizes_show()
    {
        var reading = await DocumentOutline.PdfAsync(Book(bookmarks: false));
        var titles = reading.Sections!.Select(s => (s.Chapter, s.Title)).ToList();
        Assert.Contains(("Chapter 1 Cells", "1.1 Membranes"), titles);
        Assert.Contains(("Chapter 2 Energy", "2.1 Light"), titles);
        Assert.Contains(("Chapter 1 Cells", "Chapter 1 Cells"), titles);
    }

    [Fact]
    public async Task A_document_without_pages_is_parted_by_its_headings()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "notes.md");
        var text = new StringBuilder();
        foreach (var (chapter, sections) in new[] { ("Cells", new[] { "Membranes", "Transport" }), ("Energy", new[] { "Light", "Respiration" }) })
        {
            text.Append("# ").AppendLine(chapter).AppendLine().AppendLine(Words(60, "intro")).AppendLine();
            foreach (var section in sections)
                text.Append("## ").AppendLine(section).AppendLine().AppendLine(Words(160, section.ToLowerInvariant())).AppendLine();
        }
        await File.WriteAllTextAsync(path, text.ToString());
        var reading = await DocumentOutline.ReadAsync(path, Formats.Md, Path.Combine(_dir, "laid.pdf"));
        Assert.Equal([("Cells", "Cells"), ("Cells", "Membranes"), ("Cells", "Transport"), ("Energy", "Energy"), ("Energy", "Light"), ("Energy", "Respiration")],
            reading.Sections!.Select(s => (s.Chapter, s.Title)));
        // Without pages, a place is its section's heading.
        Assert.All(reading.Sections!.SelectMany(s => s.Places.Select(p => (s.Title, p))), x => Assert.Equal((PlaceKind.Section, x.Title), (x.p.Kind, x.p.Name)));
    }

    [Theory]
    [InlineData(new[] { 1, 1, 1, 2, 2, 2, 2, 2, 2 }, 5000, 0, 1, 2)]
    [InlineData(new[] { 1, 2, 2, 2, 3, 3, 3, 3, 3, 3 }, 5000, 0, 2, 3)]
    [InlineData(new[] { 1, 1 }, 150, 0, null, null)]
    [InlineData(new[] { 3, 3, 3, 3 }, 2000, 0, 3, null)]
    public void Chapters_are_the_largest_headings_that_part_the_text_and_sections_the_next(int[] levels, int words, int pages, int? chapters, int? sections) =>
        Assert.Equal((chapters, sections), DocumentOutline.Levels(levels, words, pages));

    [Theory]
    [InlineData("3.2 The Cell Membrane", "The Cell Membrane")]
    [InlineData("Chapter 4: How Cells Obtain Energy", "How Cells Obtain Energy")]
    [InlineData("IV. Ecology", "Ecology")]
    [InlineData("الفصل ٣: الخلية", "الخلية")]
    [InlineData("Cells", "Cells")]
    public void A_heading_is_found_with_or_without_its_number(string title, string bare) =>
        Assert.Equal(bare, DocumentOutline.Unnumbered(title));

    [Fact]
    public void A_bookmark_s_heading_is_found_on_its_page_even_broken_over_two_lines()
    {
        PlacedText[] pages = [Page(5, "the end of something before.\nand more words here.\n3.4 The Cell\nMembrane\nThe membrane holds the cell together.")];
        Assert.Equal(2, DocumentOutline.FindLine(pages, new DocumentOutline.Mark(5, -1, false, "3.4 The Cell Membrane")));
        Assert.Equal(0, DocumentOutline.FindLine(pages, new DocumentOutline.Mark(5, -1, false, "Something Else Entirely")));
    }

    // ───────────── planning ─────────────

    [Fact]
    public void Short_sections_join_a_neighbour_in_their_chapter()
    {
        DocumentSection Section(string chapter, string title, string text) => new(chapter, title, [Page(1, text)]);
        var joined = StudyFile.Joined(
        [
            Section("A", "Part I", "Part I"),
            Section("A", "Cells", new string('c', 900)),
            Section("A", "A short note", new string('n', 300)),
            Section("B", "Energy", new string('e', 900)),
        ]);
        // A heading alone opens what follows; a few lines close what came before; chapters stay apart.
        Assert.Equal([("A", "Cells"), ("B", "Energy")], joined.Select(s => (s.Chapter, s.Title)));
        Assert.Equal(3, joined[0].Places.Count);
        Assert.Equal("Part I", joined[0].Places[0].Text);
    }

    [Fact]
    public void A_part_is_given_in_passages_the_model_can_hold_each_knowing_its_pages()
    {
        var places = Enumerable.Range(1, 9).Select(n => Page(n, string.Join(" ", Enumerable.Repeat($"Sentence on page {n} about cells.", 30)))).ToList();
        var units = StudyFile.Units(places, StudyFile.LocalStretch);
        Assert.True(units.Count >= 3);
        Assert.All(units, u => Assert.True(u.Text.Length <= StudyFile.LocalStretch + StudyFile.ShortestPart, u.Text.Length.ToString()));
        Assert.Equal(1, units[0].First.Number);
        Assert.Equal(9, units[^1].Last.Number);
        // Every page is in some passage, in order.
        Assert.Equal(Enumerable.Range(1, 9), units.SelectMany(u => u.Places.Select(p => p.Number)).Distinct());
    }

    [Theory]
    [InlineData("Contents", "", false)]
    [InlineData("Index", "", false)]
    [InlineData("Key Terms", "Chapter 3 Cells", false)]
    [InlineData("Chapter Summary", "Chapter 3 Cells", false)]
    [InlineData("Review Questions", "Chapter 3 Cells", false)]
    [InlineData("About OpenStax", "Preface", false)]
    [InlineData("المراجع", "", false)]
    [InlineData("Kaynakça", "", false)]
    [InlineData("3.4 The Cell Membrane", "Chapter 3 Cells", true)]
    [InlineData("Summary of photosynthesis", "", true)]
    public void Contents_indexes_and_what_ends_a_chapter_start_unticked(string title, string chapter, bool ticked) =>
        Assert.Equal(ticked, StudyFile.Worth(new DocumentSection(chapter, title, [Page(1, "x")]), first: false));

    // ───────────── what the model writes ─────────────

    private const string Reply = """
        **TITLE:** Methods for Studying Cells
        IDEAS:
        - Light microscopes can magnify cells up to about 400 times.
        - Electron microscopes use a beam of electrons instead of light.
        EXPLANATION:
        Cells are studied with **microscopes**, which show what the eye can't.

        Electron microscopes show much more detail, but the preparation kills the cells.
        Example: this line belongs to the explanation.
        EXAMPLE:
        1. A scientist wants to see the surface of a bacterium.
        2. They use a scanning electron microscope.
        TERMS:
        - Electron microscope = A microscope that uses a beam of electrons.
        - Resolving power: how fine a detail can be told apart.
        CARDS:
        - What is the most a light microscope magnifies? - About 400 times
        - Which microscope shows the inside of a cell? = A transmission electron microscope
        QUIZ:
        Q: What is a limitation of electron microscopes?
        A) They can't magnify enough.
        B) The specimen must be alive.
        C) The preparation kills the specimen.
        D) They only show tissues.
        RIGHT: C
        WHY: The passage says preparation kills the specimen.
        Q2: A question with three choices only
        A) one
        B) two
        C) three
        RIGHT: A
        """;

    [Fact]
    public void A_reply_is_read_by_its_headings_whatever_the_model_decorates_them_with()
    {
        var read = StudyFile.Read(Reply)!;
        Assert.Equal("Methods for Studying Cells", read.Title);
        Assert.Equal(2, read.Ideas.Count);
        Assert.Contains("Example: this line belongs to the explanation.", read.Explanation);
        Assert.StartsWith("1. A scientist", read.Example);
        Assert.Equal([("Electron microscope", "A microscope that uses a beam of electrons."), ("Resolving power", "how fine a detail can be told apart.")], read.Terms);
        Assert.Equal(("What is the most a light microscope magnifies?", "About 400 times"), read.Cards[0]);
        Assert.Equal("A transmission electron microscope", read.Cards[1].Back);
        // Written with no sign between them, a card is parted at its question mark.
        Assert.Equal(("How many codons are there?", "64."), StudyFile.Pair("How many codons are there? 64."));
        Assert.Equal(("كم عدد الشفرات؟", "64 شفرة."), StudyFile.Pair("كم عدد الشفرات؟ 64 شفرة."));
        var question = Assert.Single(read.Quiz);
        Assert.Equal(2, question.Correct);
        Assert.Equal("The passage says preparation kills the specimen.", question.Why);
    }

    [Fact]
    public void Choices_lettered_in_arabic_and_an_answer_given_in_words_are_read()
    {
        const string arabic = """
            TITLE: الدورة الدموية
            EXPLANATION:
            يخرج الدم من البطين الأيمن إلى الرئتين ثم يعود إلى القلب غنيًا بالأكسجين، وهذا ما يسمى الدورة الصغرى.
            QUIZ:
            Q: من أين تبدأ الدورة الصغرى؟
            أ) البطين الأيسر
            ب) البطين الأيمن
            ج) الأذين الأيمن
            د) الرئتين
            RIGHT: ب
            Q: Which goes first?
            A) one
            B) two
            C) three
            D) four
            ANSWER: three
            Q: کدام درست است؟
            الف) یک
            ب) دو
            ج) سه
            د) چهار
            RIGHT: الف
            """;
        var read = StudyFile.Read(arabic)!;
        Assert.Equal([1, 2, 0], read.Quiz.Select(q => q.Correct));
        Assert.Equal("البطين الأيمن", read.Quiz[0].Options[1]);
    }

    [Theory]
    [InlineData("NOTHING")]
    [InlineData("**NOTHING**")]
    [InlineData("TITLE: NOTHING")]
    public void A_passage_with_nothing_to_learn_is_said_to_be(string reply) => Assert.True(StudyFile.Read(reply)!.Nothing);

    [Fact]
    public void A_reply_in_no_form_is_none() => Assert.Null(StudyFile.Read("I'm sorry, I can't help with that."));

    [Theory]
    [InlineData("يخرج الدم من البطين الأيمن إلى الرئتين (pulmonary artery) ثم يعود إلى القلب غنيًا بالأكسجين.", "ar", true)]
    [InlineData("Blood leaves the right ventricle for the lungs and comes back to the heart full of oxygen.", "ar", false)]
    [InlineData("Blood leaves the right ventricle for the lungs and comes back to the heart full of oxygen.", "en", true)]
    [InlineData("Kan sağ karıncıktan akciğerlere gider ve oksijenle dolu olarak kalbe geri döner, bu küçük dolaşımdır.", "tr", true)]
    [InlineData("Kan sağ karıncıktan akciğerlere gider ve oksijenle dolu olarak kalbe geri döner, bu küçük dolaşımdır.", "en", false)]
    [InlineData("خون از بطن راست به ریه‌ها می‌رود و پر از اکسیژن به قلب برمی‌گردد، این گردش کوچک است.", "fa", true)]
    public void What_was_written_is_checked_to_be_in_the_language_asked_for(string text, string into, bool right) =>
        Assert.Equal(right, StudyFile.InLanguage(text, into));

    [Fact]
    public void The_teacher_is_told_to_keep_to_the_passage_and_give_the_original_terms_when_translating()
    {
        var told = StudyFile.Instruction("ar", "en", StudyAmount.More);
        Assert.Contains("Write everything in Arabic", told);
        Assert.Contains("passage's own word for it in brackets", told);
        Assert.Contains("add none of your own", told);
        Assert.Contains("up to 6 CARDS and up to 3 QUIZ questions", told);
        Assert.DoesNotContain("brackets", StudyFile.Instruction("en", "en", StudyAmount.Fewer));
    }

    // ───────────── making it ─────────────

    internal static string Answer(string given) =>
        given.Contains("garbage") ? "I can't." :
        given.Contains("nothing here") ? "NOTHING" :
        given.StartsWith("Below are the titles", StringComparison.Ordinal) || given.Contains("## ") || given.Contains("# ") && !given.Contains("passage") ? "OVERVIEW:\nA book about cells.\nGOALS:\n- Explain what a membrane does." :
        $"""
        TITLE: Membranes
        IDEAS:
        - A membrane holds the cell together.
        EXPLANATION:
        The membrane is a thin layer around the cell that lets some things in and keeps others out, so the cell stays the same inside.
        TERMS:
        - Membrane = The thin layer around a cell.
        - Flux capacitor = Not in the passage at all.
        CARDS:
        - What holds the cell together? = The membrane
        QUIZ:
        Q: What does the membrane do?
        A) Holds the cell together
        B) Makes light
        C) Stores sand
        D) Nothing
        RIGHT: A
        WHY: The passage says so.
        """;

    [Fact]
    public async Task A_book_is_explained_part_by_part_and_what_was_made_is_kept_for_the_next_time()
    {
        StudyFile.CacheFolder = Path.Combine(_dir, "cache");
        int asked = 0;
        StudyFile.TestWriter = (instruction, given, _) =>
        {
            Interlocked.Increment(ref asked);
            return Task.FromResult(instruction.StartsWith("You are a patient", StringComparison.Ordinal) ? Answer(given) : Answer("## outline"));
        };
        var sections = new List<DocumentSection>
        {
            new("Chapter 1", "1.1 Membranes", [Page(3, "The membrane holds the cell together. " + new string('m', 2000)), Page(4, "It lets things in. " + new string('n', 2000))]),
            new("Chapter 1", "1.2 Garbage", [Page(5, "garbage " + new string('g', 900))]),
            new("Chapter 2", "2.1 Empty", [Page(6, "nothing here " + new string('x', 900))]),
        };
        var plan = StudyFile.Plan(new DocumentReading(sections.SelectMany(s => s.Places).ToList(), sections, "Cells"), cloud: false);
        var options = new StudyFile.Options("en", StudyAmount.Normal, "en");
        var chosen = plan.Parts.Select(p => p.Number).ToHashSet();
        var reports = new List<StudyProgress>();
        string Where(PlacedText a, PlacedText b) => a.Number == b.Number ? $"Page {a.Number}" : $"Pages {a.Number}–{b.Number}";
        var book = await StudyFile.MakeAsync(plan, chosen, options, "Cells", "cells.pdf", Where, new Synchronous<StudyProgress>(reports.Add));

        // The garbage part couldn't be explained: its words are there as they are. The empty part has nothing to learn.
        Assert.Equal(["1.1 Membranes", "1.2 Garbage"], book.Parts.Select(p => p.Title));
        Assert.Equal("", book.Parts[1].Passages[0].Explanation);
        Assert.StartsWith("garbage", book.Parts[1].Passages[0].Original);
        // A term the passage doesn't use is the model's, not the file's.
        Assert.Equal(["Membrane"], book.Terms.Select(t => t.Term));
        // The same card and question in two passages are one.
        Assert.Single(book.Parts[0].Cards);
        Assert.Single(book.Parts[0].Quiz);
        Assert.Equal("Pages 3–4", book.Parts[0].Where);
        Assert.Single(book.Chapters);
        Assert.Equal("A book about cells.", book.Overview);
        Assert.Equal(["Explain what a membrane does."], book.Goals);
        Assert.Equal(StudyFile.Passages(plan, chosen), reports[^1].Done);
        Assert.True(reports.Zip(reports.Skip(1)).All(p => p.First.Done <= p.Second.Done));

        // Made again: every passage the model answered is kept, and only what it never answered is asked again.
        int before = asked;
        Assert.Equal(StudyFile.Passages(plan, chosen) - 1, StudyFile.Made(plan, chosen, options));
        var again = await StudyFile.MakeAsync(plan, chosen, options, "Cells", "cells.pdf", Where);
        Assert.Equal(2, asked - before);
        Assert.Equal(book.Parts.Select(p => p.Title), again.Parts.Select(p => p.Title));
    }

    [Fact]
    public async Task A_sample_is_one_part_without_an_overview_and_what_it_made_is_kept_for_the_whole_file()
    {
        StudyFile.CacheFolder = Path.Combine(_dir, "cache-sample");
        int asked = 0;
        StudyFile.TestWriter = (instruction, given, _) =>
        {
            Interlocked.Increment(ref asked);
            return Task.FromResult(instruction.StartsWith("You are a patient", StringComparison.Ordinal) ? Answer(given) : Answer("## outline"));
        };
        var sections = new List<DocumentSection>
        {
            new("Chapter 1", "Chapter 1", [Page(2, "Opening words of the chapter. " + new string('o', 900))]),
            new("Chapter 1", "1.1 Membranes", [Page(3, "The membrane holds the cell together. " + new string('m', 2000)), Page(4, "It lets things in. " + new string('n', 2000))]),
            new("Chapter 2", "2.1 Energy", [Page(6, "The membrane needs energy. " + new string('e', 900))]),
        };
        var plan = StudyFile.Plan(new DocumentReading(sections.SelectMany(s => s.Places).ToList(), sections, "Cells"), cloud: false);
        // Without diagrams and activities, each passage is one call to the model: the calls can be counted.
        var options = new StudyFile.Options("en", StudyAmount.Normal, "en", "", StudyActivities.None);
        var chosen = plan.Parts.Select(p => p.Number).ToHashSet();
        string Where(PlacedText a, PlacedText b) => a.Number == b.Number ? $"Page {a.Number}" : $"Pages {a.Number}–{b.Number}";

        // Not a chapter's opening words: its first real part.
        var one = StudyFile.SamplePart(plan, chosen);
        Assert.Equal("1.1 Membranes", plan.Parts.Single(p => p.Number == one).Title);
        Assert.Null(StudyFile.SamplePart(plan, new HashSet<int>()));
        Assert.Equal(plan.Parts[0].Number, StudyFile.SamplePart(plan, new HashSet<int> { plan.Parts[0].Number }));

        var part = new HashSet<int> { one!.Value };
        var sample = await StudyFile.MakeAsync(plan, part, options, "Cells", "cells.pdf", Where, sample: true);
        Assert.True(sample.Sample);
        Assert.Equal(["1.1 Membranes"], sample.Parts.Select(p => p.Title));
        // No overview of a whole it hasn't read: only the passages were asked.
        Assert.Equal("", sample.Overview);
        Assert.Empty(sample.Goals);
        Assert.Equal(StudyFile.Passages(plan, part), asked);
        Assert.Contains(StudyFileWindow.Words("en")["sample"], StudyFileWriter.Html(sample, StudyFileWindow.Words("en")));

        // The whole file afterwards asks only for what the sample didn't make, and has its overview.
        Assert.Equal(StudyFile.Passages(plan, part), StudyFile.Made(plan, chosen, options));
        int before = asked;
        var whole = await StudyFile.MakeAsync(plan, chosen, options, "Cells", "cells.pdf", Where);
        Assert.False(whole.Sample);
        Assert.Equal("A book about cells.", whole.Overview);
        Assert.DoesNotContain("class=\"sample\"", StudyFileWriter.Html(whole, StudyFileWindow.Words("en")));
        Assert.Equal(StudyFile.Passages(plan, chosen) - StudyFile.Passages(plan, part), asked - before - 1 - whole.Chapters.Count(c => c.Summary.Length > 0));

        // Kept apart from the file it was made from.
        Assert.StartsWith(StudyFile.CacheFolder, StudyFile.SamplePath(@"C:\Books\cells.pdf"));
        Assert.EndsWith("cells.html", StudyFile.SamplePath(@"C:\Books\cells.pdf"));
    }

    [Fact]
    public void After_a_sample_the_window_says_how_long_the_rest_takes_as_the_sample_went()
    {
        // In whatever language the interface speaks: the words are the window's own.
        Assert.EndsWith(L.P(12, "The rest takes about {0} minute.", "The rest takes about {0} minutes."), StudyFileWindow.Tried(6, TimeSpan.FromMinutes(2)));
        Assert.EndsWith(L.P(3, "The rest takes about {0} hour on this PC.", "The rest takes about {0} hours on this PC."), StudyFileWindow.Tried(90, TimeSpan.FromMinutes(2)));
        Assert.EndsWith(L.T("The rest takes about a minute."), StudyFileWindow.Tried(2, TimeSpan.FromSeconds(20)));
        Assert.EndsWith(L.P(4, "{0} passage left to explain.", "{0} passages left to explain."), StudyFileWindow.Tried(4, null));
        Assert.Equal(L.T("The sample is open in your browser. Change the request and try again, or make the whole file: the part already made isn't made again."), StudyFileWindow.Tried(0, TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void An_item_points_at_the_page_that_says_it_and_quotes_the_file_when_that_is_clear()
    {
        var unit = new StudyUnit("…", [Page(10, "Plants make food. Water moves up the stem."), Page(11, "Chlorophyll absorbs energy from sunlight in the leaves. The rest is about roots.")]);
        string Where(PlacedText a, PlacedText b) => a.Number == b.Number ? $"p. {a.Number}" : $"pp. {a.Number}–{b.Number}";
        var (where, quote) = StudyFile.Locate(unit, "What absorbs energy from sunlight in the leaves? Chlorophyll", sameLanguage: true, Where);
        Assert.Equal("p. 11", where);
        Assert.Equal("Chlorophyll absorbs energy from sunlight in the leaves.", quote);
        // In another language than the file's, words can't be matched: the passage's pages, no quote.
        Assert.Equal(("pp. 10–11", ""), StudyFile.Locate(unit, "ما الذي يمتص الطاقة؟ اليخضور", sameLanguage: false, Where));
    }

    // ───────────── the page ─────────────

    internal static StudyBook Sample(string language) => SampleBook(language);

    /// <summary>Where the study file's page, style and script are written.</summary>
    internal static string RecipesFolder
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "Petal.Recipes")))
                dir = dir.Parent;
            return Path.Combine(dir!.FullName, "src", "Petal.Recipes", "Study");
        }
    }

    private static StudyBook SampleBook(string language) => new(
        "Cells & <Energy>", "cells.pdf", language, "en", "A book about **cells**.", ["Explain membranes."],
        [new StudyChapter("Chapter 1", "Cells are small.", [1, 2])],
        [
            new StudyPart(1, "Chapter 1", "Chapter 1", "Page 3", [new StudyExplained("Intro", "Page 3", ["الخلايا حية."], "والخلية <script>alert(1)</script> هي وحدة الحياة. الخلية مرة أخرى.\n\n- one\n- two", "1. Look.\n2. See.")], [], []),
            new StudyPart(2, "Chapter 1", "1.1 Membranes", "Pages 4–5",
                [new StudyExplained("Membranes", "Page 4", ["It holds the cell together."], "The membranes of cells hold them together, as *Cells* show.", "")],
                [new StudyCard("What holds a cell?", "Its membrane", "Page 4") { Quote = "The membrane holds the cell together." }],
                [new StudyQuestion("What does a membrane do?", ["Holds the cell", "Makes light", "Stores sand", "Nothing"], 0, "It says so.", "Page 4") { Quote = "The membrane holds the cell together." }]),
        ],
        [new StudyTerm("الخلية (cell)", "The smallest living unit.", "Page 3", 1), new StudyTerm("membrane", "The layer around a cell.", "Page 4", 2)],
        "Gemma 4 E4B", new DateTime(2026, 11, 15), false);

    [Fact]
    public void The_page_loads_nothing_and_runs_only_its_own_script()
    {
        var html = StudyFileWriter.Html(SampleBook("en"), StudyFileWindow.Words("en"));
        // Nothing from elsewhere: no address to load, and a policy that would refuse one.
        Assert.DoesNotMatch(@"(src|href)\s*=\s*""(https?:)?//", html);
        Assert.DoesNotContain("@import", html);
        Assert.DoesNotContain("url(http", html);
        var policy = Regex.Match(html, "script-src 'sha256-([^']+)'").Groups[1].Value;
        var scripts = Regex.Matches(html, @"<script>(.*?)</script>", RegexOptions.Singleline);
        var only = Assert.Single(scripts);
        Assert.Equal(Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(only.Groups[1].Value))), policy);
        Assert.Contains("default-src 'none'", html);
        // What the model wrote is text, never markup: not in the page, not in the script's data.
        Assert.DoesNotContain("<script>alert", html);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", html);
        Assert.Contains("<title>Cells &amp; &lt;Energy&gt;</title>", html);
        var data = Regex.Match(html, "<script type=\"application/json\" id=\"data\">(.*?)</script>", RegexOptions.Singleline).Groups[1].Value;
        Assert.DoesNotContain("<", data);
        using var json = JsonDocument.Parse(data);
        Assert.Equal("What holds a cell?", json.RootElement.GetProperty("cards")[0].GetProperty("f").GetString());
        Assert.Equal(0, json.RootElement.GetProperty("quiz")[0].GetProperty("a").GetInt32());
        Assert.Equal(StudyFileWords.Keys.Length, json.RootElement.GetProperty("w").EnumerateObject().Count());
    }

    [Fact]
    public void Terms_are_marked_where_they_first_come_in_each_part_with_arabic_s_joined_little_words()
    {
        var html = StudyFileWriter.Html(SampleBook("ar"), StudyFileWindow.Words("ar"));
        Assert.Contains("<html lang=\"ar\" dir=\"rtl\">", html);
        // "والخلية" is the term with "و" and the article before it; the second time in the part it isn't marked again.
        Assert.Contains("data-t=\"t1\" title=\"The smallest living unit.\">والخلية</span>", html);
        Assert.Single(Regex.Matches(Regex.Match(html, "<section class=\"view part\" id=\"p1\".*?</section>\\n", RegexOptions.Singleline).Value, "data-t=\"t1\" title"));
        // An English word with its plural ending, in the next part.
        Assert.Contains(">membranes</bdi></span>", html);
        // In a page read right to left, words in Latin letters are isolated so they keep their order, and a short term in
        // brackets stays on one line with its brackets.
        Assert.Contains("<em><bdi>Cells</bdi></em>", html);
        Assert.Equal("الخلية <span class=\"nb\">(<bdi>cell membrane</bdi>)</span> هنا", StudyFileWriterProbe.Written("الخلية (cell membrane) هنا"));
        // Choices lettered as Arabic letters them.
        Assert.Contains("<span class=\"l\">ب</span>", html);
        Assert.Contains("مقدمة", html);
    }

    [Fact]
    public void Without_its_script_the_page_still_shows_every_part_card_and_answer()
    {
        var html = StudyFileWriter.Html(SampleBook("en"), StudyFileWindow.Words("en"));
        Assert.Contains("<body class=\"nojs\"", html);
        Assert.Contains("<details class=\"key\"><summary>The answer</summary>", html);
        Assert.Contains("<span class=\"face back\"><span class=\"t\">Its membrane</span>", html);
        Assert.Contains("In the original:", html);
        Assert.Contains("@media print", html);
        Assert.Contains("id=\"p1\"", html);
        Assert.Contains("id=\"p2\"", html);
        Assert.Contains("id=\"terms\"", html);
    }

    [Theory]
    [InlineData("en")]
    [InlineData("ar")]
    [InlineData("tr")]
    [InlineData("fa")]
    [InlineData("ur")]
    [InlineData("id")]
    [InlineData("fr")]
    [InlineData("es")]
    public void The_page_has_its_words_in_every_language_of_the_interface(string language)
    {
        var words = StudyFileWindow.Words(language);
        Assert.Equal(StudyFileWords.Keys.Order(), words.All.Keys.Order());
        Assert.Equal(4, words["letters"].Split(' ').Length);
        foreach (var key in StudyFileWords.Counted)
            Assert.Contains("12", words.Count(key, 12));
        if (language != "en")
        {
            // Translated: the page's own words are in the file's language, whatever the interface speaks.
            var english = StudyFileWindow.Words("en");
            var same = StudyFileWords.Keys.Where(k => words[k] == english[k] && k is not ("letters" or "intro" or "test")).ToList();
            Assert.True(same.Count <= 2, $"{language}: still English: " + string.Join(", ", same));
        }
    }

    [Fact]
    public void Words_for_another_language_than_the_interface_s_are_looked_up_in_its_table()
    {
        L.Configure(UiLanguage.English);
        Assert.Equal("نظرة عامة", L.In("ar", "Overview"));
        Assert.Equal("Overview", L.In("en", "Overview"));
        Assert.Equal("مصطلح واحد", L.P(1, "{0} term", "{0} terms", "ar"));
        Assert.Equal("3 مصطلحات", L.P(3, "{0} term", "{0} terms", "ar"));
        Assert.Equal("11 مصطلحًا", L.P(11, "{0} term", "{0} terms", "ar"));
        Assert.Equal("1 terme", L.P(1, "{0} term", "{0} terms", "fr"));
        Assert.Equal("2 termes", L.P(2, "{0} term", "{0} terms", "fr"));
        Assert.Equal("5 terms", L.P(5, "{0} term", "{0} terms", "en"));
    }

    [Theory]
    [InlineData("ar", "الصفحة 4", "الصفحات 4–6")]
    [InlineData("tr", "Sayfa 4", "Sayfa 4–6")]
    [InlineData("fr", "Page 4", "Pages 4–6")]
    public void Places_are_said_in_the_study_file_s_language(string language, string one, string several)
    {
        // Written right to left, a range is held in its order by marks either side of its dash ("4–6", not "6–4").
        var mark = ((char)0x200E).ToString();
        bool rtl = language is "ar" or "fa" or "ur";
        Assert.Equal(one, StudyFileWindow.Place(language, Page(4, ""), Page(4, "")));
        var range = StudyFileWindow.Place(language, Page(4, ""), Page(6, ""));
        Assert.Equal(several, range.Replace(mark, ""));
        Assert.Equal(rtl, range.Contains(mark + "–" + mark));
        var time = StudyFileWindow.Place(language, new PlacedText(PlaceKind.Time, 1, "") { Start = 725 }, new PlacedText(PlaceKind.Time, 2, "") { End = 840 });
        Assert.Equal("12:05–14:00", time.Replace(mark, ""));
    }

    // ───────────── what the person asks for ─────────────

    [Theory]
    [InlineData("Explain it simply, in English, for beginners", "en")]
    [InlineData("English please", "en")]
    [InlineData("English", "en")]
    [InlineData("translate it into Arabic and keep the English terms", "ar")]
    [InlineData("اشرحه بالعربي مع الأمثلة", "ar")]
    [InlineData("اكتبه باللغة الإنجليزية", "en")]
    [InlineData("ترجمه إلى الفرنسية", "fr")]
    [InlineData("بالإسبانية", "es")]
    [InlineData("Basitçe İngilizce olarak anlat", "en")]
    [InlineData("Arapçaya çevir", "ar")]
    [InlineData("به انگلیسی و ساده بنویس", "en")]
    [InlineData("اسے انگریزی میں آسان کر کے لکھیں", "en")]
    [InlineData("Jelaskan dalam bahasa Inggris", "en")]
    [InlineData("Explique-le en anglais", "en")]
    [InlineData("Explícalo en inglés, con ejemplos", "es" + "|en")]
    [InlineData("Escríbelo en árabe", "ar")]
    [InlineData("Explain the history of the Arab world simply", null)]
    [InlineData("اشرح تاريخ العالم العربي ببساطة", null)]
    [InlineData("Keep the English terms and make it for children", null)]
    [InlineData("from English, for beginners", null)]
    [InlineData("İngilizceden çevrilmiş metni basitçe anlat", null)]
    [InlineData("in English or in French", null)]
    [InlineData("", null)]
    public void A_request_says_the_language_it_asks_for_only_where_it_asks_for_it(string request, string? expected)
    {
        var want = expected?.Split('|')[^1];
        Assert.Equal(want, StudyRequest.Language(request));
    }

    [Theory]
    [InlineData("en")]
    [InlineData("ar")]
    [InlineData("tr")]
    [InlineData("fa")]
    [InlineData("ur")]
    [InlineData("id")]
    [InlineData("fr")]
    [InlineData("es")]
    public void Every_language_suggestion_is_read_back_as_its_language_in_every_interface_language(string ui)
    {
        foreach (var (code, (_, words)) in StudyFileWindow.InLanguage)
            Assert.True(StudyRequest.Language(L.In(ui, words)) == code, $"{ui}: \"{L.In(ui, words)}\" isn't read as {code}");
    }

    [Theory]
    [InlineData("en")]
    [InlineData("ar")]
    [InlineData("tr")]
    [InlineData("fa")]
    [InlineData("ur")]
    [InlineData("id")]
    [InlineData("fr")]
    [InlineData("es")]
    public void Every_suggestion_is_read_back_as_what_it_aims_at_in_every_interface_language(string ui)
    {
        foreach (var (label, _, words, aim, activities, _) in StudyFileWindow.Suggestions)
        {
            var said = L.In(ui, words);
            var reading = StudyRequest.Read(said);
            Assert.True(reading.Aims.Contains(aim), $"{ui}, {label}: \"{said}\" isn't read as {aim} (read: {string.Join(", ", reading.Aims)})");
            if (activities is StudyActivities.Many)
                Assert.True(reading.AsksActivities, $"{ui}, {label}: \"{said}\" isn't read as asking for activities");
        }
    }

    [Theory]
    [InlineData("Explain it simply for beginners, in English", "en", "Beginners", false)]
    [InlineData("for my kids, with games to drag and match", "", "Children", true)]
    [InlineData("اشرحه ببساطة للمبتدئين مع أنشطة تفاعلية", "", "Beginners", true)]
    [InlineData("لخّصه للامتحان", "", "Exam|Summary", false)]
    [InlineData("Sınav için kısa bir özet", "", "Exam|Summary", false)]
    [InlineData("برای کودکان با نمودار", "", "Children|Visual", true)]
    [InlineData("بچوں کے لیے آسان سبق", "", "Beginners|Children|Teaching", false)]
    [InlineData("untuk ujian, dengan banyak latihan soal", "", "Exam|Problems", false)]
    [InlineData("une leçon pour la classe, avec des schémas", "", "Teaching|Visual", true)]
    [InlineData("un resumen con los términos clave", "", "Summary|Terms", false)]
    [InlineData("in English or in French", "en|fr", "", false)]
    [InlineData("Explain the history of the Arab world", "", "", false)]
    [InlineData("", "", "", false)]
    public void A_request_is_read_back_as_its_languages_aims_and_activities(string request, string languages, string aims, bool activities)
    {
        var reading = StudyRequest.Read(request);
        Assert.Equal(languages, string.Join("|", reading.Languages));
        Assert.Equal(aims, string.Join("|", reading.Aims));
        Assert.Equal(activities, reading.AsksActivities);
    }

    [Fact]
    public void Templates_go_into_a_file_and_come_back_the_same_and_another_file_is_refused()
    {
        var kept = new List<StudyTemplate>
        {
            new("للامتحان", "حضّره للامتحان مع أسئلة أكثر", StudyActivities.Some, StudyAmount.More),
            new("Kids", "Explain it for children", null, null),
        };
        var text = StudyTemplates.Write(kept);
        // Arabic is written as it is, so the file can be read.
        Assert.Contains("حضّره للامتحان", text);
        Assert.Equal(kept, StudyTemplates.Read(text));

        Assert.Null(StudyTemplates.Read("not json"));
        Assert.Null(StudyTemplates.Read("""{"kind":"something-else","version":1,"templates":[]}"""));
        Assert.Null(StudyTemplates.Read("""{"kind":"dropwheel-study-templates","version":1,"templates":[{"name":"x","request":"y","activities":"lots"}]}"""));

        // Every field checked: names and requests cut to their longest, empty requests left out, a name made when there is none.
        var odd = StudyTemplates.Read("{\"kind\":\"dropwheel-study-templates\",\"version\":1,\"templates\":["
            + "{\"name\":\"" + new string('n', 90) + "\",\"request\":\"" + new string('r', 900) + "\"},"
            + "{\"name\":\"empty\",\"request\":\"   \"},"
            + "{\"name\":\"  \",\"request\":\"Explain it simply, for beginners, with an example from daily life for each idea\"}]}")!;
        Assert.Equal(2, odd.Count);
        Assert.Equal(StudyTemplates.LongestName, odd[0].Name.Length);
        Assert.Equal(StudyRequest.Longest, odd[0].Request.Length);
        Assert.Equal("Explain it simply, for beginners, with…", odd[1].Name);
        Assert.True(odd[1].Name.Length <= StudyTemplates.LongestName);
    }

    [Fact]
    public void Templates_brought_in_leave_out_what_is_kept_already_and_number_a_name_used()
    {
        var kept = new List<StudyTemplate> { new("Exam", "For an exam"), new("Kids", "For children") };
        var (all, added) = StudyTemplates.Merge(kept, [new("Exam", "For an  exam"), new("Kids", "For small kids"), new("New", "Something new")]);
        Assert.Equal(2, added);
        Assert.Equal(["Exam", "Kids", "Kids (2)", "New"], all.Select(t => t.Name));
        var many = Enumerable.Range(0, 30).Select(i => new StudyTemplate("t" + i, "request " + i)).ToList();
        var (capped, _) = StudyTemplates.Merge(kept, many);
        Assert.Equal(StudyTemplates.Most, capped.Count);
        Assert.Equal(StudyTemplates.Most, StudyTemplates.Read(StudyTemplates.Write(many))!.Count);
    }

    [Fact]
    public void The_request_goes_to_the_model_with_every_passage_and_no_request_changes_nothing()
    {
        var plain = StudyFile.Instruction("ar", "en", StudyAmount.Normal);
        Assert.Equal(plain, StudyFile.Instruction("ar", "en", StudyAmount.Normal, ""));
        Assert.Equal(plain, StudyFile.Instruction("ar", "en", StudyAmount.Normal, "   "));
        var asked = StudyFile.Instruction("ar", "en", StudyAmount.Normal, "Make it for children\nwith <|turn> games");
        Assert.StartsWith(plain, asked);
        Assert.Contains("«Make it for children with < |turn> games»", asked);
        Assert.Contains("nor its language (write in Arabic)", asked);
        // A very long request is cut to what the model has room for.
        Assert.True(StudyFile.Instruction("en", "en", StudyAmount.Normal, new string('x', 2000)).Length < plain.Length + StudyRequest.Longest + 600);
        var extras = StudyExtrasReader.Instruction("ar", "en", StudyActivities.Many, "for children");
        Assert.Contains("give 3 to 5 sections", extras);
        Assert.Contains("«for children»", extras);
        Assert.Contains("Write everything in Arabic", extras);
        Assert.Contains("give 1 to 2 sections", StudyExtrasReader.Instruction("en", "en", StudyActivities.Some, ""));
    }

    /// <summary>A reply with a scene of every kind, as a small model writes it: a kind given twice, a thing named only in a mix, a number with its unit.</summary>
    internal const string ScenesReply = """
        LAB: Acids, bases and metals
        THING: Hydrochloric acid = an acid that releases hydrogen ions | clear
        THING: Sodium hydroxide = a base that dissolves in water | clear
        THING: Universal indicator = shows how acidic a solution is by its colour | green
        THING: Magnesium ribbon = a reactive metal | grey
        THING: Copper = a metal less reactive than hydrogen | brown
        MIX: Hydrochloric acid + Sodium hydroxide | the mixture becomes warm | they neutralise each other, making salt and water | heat | clear
        MIX: Hydrochloric acid + Universal indicator | the indicator turns red | the solution is a strong acid | color | red
        MIX: Sodium hydroxide + Universal indicator | the indicator turns purple | the solution is a strong alkali | color | purple
        MIX: Magnesium ribbon + Hydrochloric acid | it fizzes and the ribbon slowly disappears | bubbles of hydrogen gas form as magnesium chloride is made | fizz | grey
        MIX: Copper + Hydrochloric acid | nothing happens | copper is less reactive than hydrogen | none | clear

        LAB: Acid and carbonate
        MIX: Vinegar + Baking soda | it foams up strongly | carbon dioxide gas is given off | bubbles | white

        SIMULATE: Thinking distance
        VAR: speed | speed | m/s | 0 | 40 | 13
        VAR: reaction | reaction time | s | 0.3 | 2.1 | 0.7
        RESULT: thinking | thinking distance | m | speed * reaction
        - thinking > 20 = Longer than four cars: tiredness or a phone makes it worse.
        - thinking <= 20 = A short distance, at this speed and reaction time.

        SYSTEM: Water in the air
        - Sun's warmth | RAISES | Water vapour | The sun warms the oceans and water evaporates.
        - Water vapour | RAISES | Clouds | Rising moist air cools and condenses into droplets.
        - Water vapour | RAISES | Heat trapped | Water vapour is a greenhouse gas.
        - Heat trapped | RAISES | Water vapour | Warmer air holds more vapour.
        - Clouds | LOWERS | Sunlight reaching the ground | Clouds reflect sunlight back to space.
        - Forests | RAISES | Water vapour | Trees release water from their leaves.

        SCENARIO: Someone is choking
        SITUATION: A friend is choking but can still cough strongly.
        - Encourage them to keep coughing | GOOD | Coughing is the best way to clear the blockage.
        - Slap them on the back at once | BAD | Not yet: while they can cough, coughing works best.
        SCENARIO: Someone is choking
        SITUATION: They can no longer cough, speak or breathe.
        - Give up to five back blows, then up to five abdominal thrusts | GOOD | This is what clears a blocked airway.
        - Put your fingers into their mouth | BAD | You may push the object further down.
        - Wait to see if it clears | BAD | Act at once: they cannot breathe.

        ESTIMATE: The Solar System in numbers
        - How far is the Moon from the Earth? | 384,000 km | km | 0 | 1,000,000 | The Moon is about 384,000 kilometres away.
        - How long does a day on Jupiter last? | 10 | hours | 0 | 48 | Jupiter spins fast: a day there is about 10 hours, the shortest of any planet.
        - How long does light take from the Sun? | 8 minutes and 20 seconds | time | 0 | 20 | (not one number)
        """;

    internal const string ScenesReplyArabic = """
        LAB: الأحماض والقواعد والفلزات
        THING: حمض الهيدروكلوريك = حمض يطلق أيونات الهيدروجين | clear
        THING: هيدروكسيد الصوديوم = قاعدة تذوب في الماء | clear
        THING: الكاشف العام = يدل بلونه على حموضة المحلول | green
        THING: شريط المغنيسيوم = فلز نشيط | grey
        MIX: حمض الهيدروكلوريك + هيدروكسيد الصوديوم | يسخن المزيج | يتعادلان فيتكوّن ملح وماء | heat | clear
        MIX: حمض الهيدروكلوريك + الكاشف العام | يحمرّ الكاشف | المحلول حمض قوي | color | red
        MIX: شريط المغنيسيوم + حمض الهيدروكلوريك | يفور ويختفي الشريط شيئًا فشيئًا | تتصاعد فقاعات غاز الهيدروجين | fizz | grey

        SIMULATE: مسافة التفكير
        VAR: speed | السرعة | م/ث | 0 | 40 | 13
        VAR: reaction | زمن رد الفعل | ث | 0.3 | 2.1 | 0.7
        RESULT: thinking | مسافة التفكير | م | speed * reaction
        - thinking > 20 = أطول من أربع سيارات: التعب أو الهاتف يزيدانها.

        SYSTEM: الماء في الهواء
        - دفء الشمس | RAISES | بخار الماء | تدفئ الشمس المحيطات فيتبخر الماء.
        - بخار الماء | RAISES | الغيوم | يبرد الهواء الرطب الصاعد فيتكثف.
        - بخار الماء | RAISES | الحرارة المحبوسة | بخار الماء غاز دفيئة.
        - الحرارة المحبوسة | RAISES | بخار الماء | الهواء الأدفأ يحمل بخارًا أكثر.
        - الغيوم | LOWERS | ضوء الشمس على الأرض | تعكس الغيوم ضوء الشمس إلى الفضاء.

        SCENARIO: شخص يختنق
        SITUATION: صديقك يختنق لكنه ما زال يسعل بقوة.
        - شجّعه على مواصلة السعال | GOOD | السعال أفضل طريقة لإزالة الانسداد.
        - اضربه على ظهره فورًا | BAD | ليس بعد: ما دام يسعل فالسعال أنفع.

        ESTIMATE: المجموعة الشمسية بالأرقام
        - كم يبعد القمر عن الأرض؟ | 384000 | كم | 0 | 1000000 | يبعد القمر نحو 384 ألف كيلومتر.
        - كم ساعة يدوم اليوم على المشتري؟ | ١٠ | ساعات | 0 | 48 | يدور المشتري بسرعة: يومه نحو 10 ساعات.
        """;

    private const string ExtrasReply = """
        **STEPS:** How a nerve signal travels
        1. A stimulus reaches a receptor
        2. A sensory neuron carries the signal
        3. The spinal cord passes it on
        4. A motor neuron reaches the muscle
        TIMELINE: Discoveries
        - 1665 = Hooke sees cells in cork
        - 1838 = Schleiden: plants are made of cells
        - 1855 = Virchow: cells come from cells
        MAP: The cell membrane
        - Made of = phospholipids; proteins
        - Job = lets some things in; keeps others out
        - Moves things by = diffusion; active transport
        COMPARE: Plant and animal cells
        | feature | plant cell | animal cell |
        |---|---|---|
        | Cell wall | yes | no |
        | Chloroplasts | yes | no |
        MATCH: Match each part to its job
        - Nucleus = holds the DNA
        - Mitochondrion = releases energy
        - Ribosome = makes proteins
        GROUPS: Sort by where they are found
        - Plant cells only: cell wall; chloroplasts
        - Animal cells only: centrioles; lysosomes
        - Both: nucleus; membrane; nucleus
        BLANKS: The membrane
        - The membrane is made of [phospholipids] and [proteins].
        - Water moves by [osmosis].
        - This line has no blank.
        TRUE OR FALSE: Cells
        - All living things are made of cells. = TRUE
        - Animal cells have a cell wall. = FALSE: only plant cells do.
        - Ribosomes make proteins = **TRUE**
        MATCH: A second match is left out
        - a = b
        - c = d
        - e = f
        """;

    [Fact]
    public void Diagrams_and_activities_are_read_from_the_reply_and_steps_are_also_put_in_order()
    {
        var extras = StudyExtrasReader.Read(ExtrasReply, "Page 4");
        Assert.Equal([StudyVisualKind.Flow, StudyVisualKind.Timeline, StudyVisualKind.Map, StudyVisualKind.Compare], extras.Visuals.Select(v => v.Kind));
        Assert.Equal([StudyActivityKind.Order, StudyActivityKind.Order, StudyActivityKind.MapFill, StudyActivityKind.Match, StudyActivityKind.Sort, StudyActivityKind.Blanks, StudyActivityKind.TrueFalse], extras.Activities.Select(a => a.Kind));
        Assert.All(extras.Visuals, v => Assert.Equal("Page 4", v.Where));
        var flow = extras.Visuals[0];
        Assert.Equal("How a nerve signal travels", flow.Title);
        Assert.Equal("A stimulus reaches a receptor", flow.Items[0].Text);
        Assert.Equal(("1665", "Hooke sees cells in cork"), (extras.Visuals[1].Items[0].Answer, extras.Visuals[1].Items[0].Text));
        // The order of dates hides the dates.
        Assert.Equal("Hooke sees cells in cork", extras.Activities[1].Items[0].Text);
        Assert.Equal(["lets some things in", "keeps others out"], extras.Visuals[2].Items[1].Details);
        Assert.Equal(["plant cell", "animal cell"], extras.Visuals[3].Columns);
        Assert.Equal(["yes", "no"], extras.Visuals[3].Items[0].Details);
        // The map to complete: each branch's name to put back where what it says is.
        var fill = extras.Activities[2];
        Assert.Equal("The cell membrane", fill.Prompt);
        Assert.Equal(("phospholipids; proteins", "Made of"), (fill.Items[0].Text, fill.Items[0].Answer));
        // Steps without what happens in them are a diagram to look at, not one to go through.
        Assert.All(extras.Visuals[0].Items, i => Assert.Empty(i.Details));
        Assert.Equal(("Mitochondrion", "releases energy"), (extras.Activities[3].Items[1].Text, extras.Activities[3].Items[1].Answer));
        // An item said to be in two groups can't be sorted: the twice-named "nucleus" goes.
        var sort = extras.Activities[4];
        Assert.Equal(["Plant cells only", "Animal cells only", "Both"], sort.Groups);
        Assert.DoesNotContain(sort.Items, i => i.Text == "nucleus");
        var blanks = extras.Activities[5];
        Assert.Equal(2, blanks.Items.Count);
        Assert.Equal(["The membrane is made of ", "phospholipids", " and ", "proteins", "."], StudyActivity.Pieces(blanks.Items[0].Text));
        var tf = extras.Activities[6];
        Assert.Equal(["true", "false", "true"], tf.Items.Select(i => i.Answer));
        Assert.Equal("only plant cells do.", tf.Items[1].Note);
        Assert.True(StudyExtrasReader.Read("NONE", "p").IsEmpty);
        Assert.True(StudyExtrasReader.Read("Here are some ideas for you.", "p").IsEmpty);
    }

    [Fact]
    public void Steps_that_say_what_happens_in_each_are_gone_through_one_at_a_time_and_a_map_needs_three_branches_to_complete()
    {
        var walk = StudyExtrasReader.Read("""
            CYCLE: The water cycle
            1. Evaporation = the sun warms the sea and water rises as vapour
            2. Condensation = the vapour cools into clouds
            3. Precipitation = the water falls as rain = or snow
            """, "Page 2");
        var cycle = Assert.Single(walk.Visuals);
        Assert.Equal(StudyVisualKind.Cycle, cycle.Kind);
        Assert.Equal(["Evaporation", "Condensation", "Precipitation"], cycle.Items.Select(i => i.Text));
        Assert.Equal("the water falls as rain = or snow", cycle.Items[2].Details[0]);
        // The order to put them in has the steps alone.
        Assert.Equal(["Evaporation", "Condensation", "Precipitation"], walk.Activities.Single().Items.Select(i => i.Text));
        var html = StudyFileWriter.Html(SampleBook("en") with { Parts = [SampleBook("en").Parts[1] with { Visuals = walk.Visuals }] }, StudyFileWindow.Words("en"));
        Assert.Contains("<figure class=\"vis flow cycle walk\">", html);
        Assert.Contains("<span class=\"d\">the vapour cools into clouds</span>", html);

        // One step without it: none keep it, so no step by step with a gap.
        var some = StudyExtrasReader.Read("STEPS: Making tea\n1. Boil water = heat it until it bubbles\n2. Add the leaves\n3. Wait three minutes = let it steep", "p");
        Assert.All(some.Visuals[0].Items, i => Assert.Empty(i.Details));
        // A map of two branches is too small to complete; one whose branches say nothing has nothing to find them by.
        Assert.DoesNotContain(StudyExtrasReader.Read("MAP: Cells\n- Plant = cell wall\n- Animal = no wall", "p").Activities, a => a.Kind == StudyActivityKind.MapFill);
        Assert.DoesNotContain(StudyExtrasReader.Read("MAP: Cells\n- Plant\n- Animal\n- Fungus", "p").Activities, a => a.Kind == StudyActivityKind.MapFill);
    }

    /// <summary>
    /// Pages with every diagram and activity, in English and Arabic, to open in a browser and try by hand: steps gone
    /// through one at a time, a map to complete, pieces to drag. PETAL_STUDYFILE_PAGE is the folder they go to.
    /// </summary>
    [Fact]
    public void Pages_with_every_diagram_and_activity_are_written_to_try_by_hand()
    {
        var folder = Environment.GetEnvironmentVariable("PETAL_STUDYFILE_PAGE");
        if (string.IsNullOrEmpty(folder))
            return;
        Directory.CreateDirectory(folder);
        var walk = StudyExtrasReader.Read("""
            CYCLE: The water cycle
            1. Evaporation = the sun warms the sea and water rises as vapour
            2. Condensation = the vapour cools into clouds
            3. Precipitation = the water falls as rain or snow
            4. Collection = rivers carry the water back to the sea
            """, "Page 4");
        var extras = StudyExtrasReader.Read(ExtrasReply, "Page 4");
        foreach (var language in new[] { "en", "ar" })
        {
            var sample = SampleBook(language);
            var parts = sample.Parts.ToList();
            parts[1] = parts[1] with { Visuals = [.. walk.Visuals, .. extras.Visuals], Activities = [.. walk.Activities, .. extras.Activities], Scenes = StudySceneReader.Read(language == "ar" ? ScenesReplyArabic : ScenesReply, "Page 4") };
            StudyFileWriter.Write(Path.Combine(folder, $"study-{language}.html"), sample with { Parts = parts, Sample = true }, StudyFileWindow.Words(language));
            StudyFileWriter.Write(Path.Combine(folder, $"explore-{language}.html"), sample with { Parts = parts, Purpose = StudyPurpose.Explore }, StudyFileWindow.Words(language));
        }
    }

    [Fact]
    public void Pieces_are_mixed_the_same_way_every_time_and_never_in_their_own_order()
    {
        Assert.Equal(StudyActivity.Mixed("seed", 5), StudyActivity.Mixed("seed", 5));
        for (int n = 2; n <= 7; n++)
            for (int s = 0; s < 40; s++)
            {
                var order = StudyActivity.Mixed("seed " + s, n);
                Assert.Equal(Enumerable.Range(0, n), order.Order());
                Assert.NotEqual(Enumerable.Range(0, n), order);
            }
    }

    [Fact]
    public async Task Each_passage_gets_its_diagrams_and_activities_asked_apart_and_kept()
    {
        StudyFile.CacheFolder = Path.Combine(_dir, "cache");
        int extrasAsked = 0;
        StudyFile.TestWriter = (instruction, given, _) =>
        {
            if (instruction.StartsWith("You are an experienced teacher", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref extrasAsked);
                Assert.Contains("«Make it visual»", instruction);
                return Task.FromResult(ExtrasReply);
            }
            return Task.FromResult(instruction.StartsWith("You are a patient", StringComparison.Ordinal) ? Answer(given) : Answer("## outline"));
        };
        var sections = new List<DocumentSection> { new("Chapter 1", "1.1 Membranes", [Page(3, "The membrane holds the cell together. " + new string('m', 1200))]) };
        var plan = StudyFile.Plan(new DocumentReading(sections.SelectMany(s => s.Places).ToList(), sections, "Cells"), cloud: false);
        var chosen = plan.Parts.Select(p => p.Number).ToHashSet();
        var options = new StudyFile.Options("en", StudyAmount.Normal, "en", "Make it visual", StudyActivities.Many);
        var book = await StudyFile.MakeAsync(plan, chosen, options, "Cells", "cells.pdf", (a, b) => $"Page {a.Number}");
        var part = Assert.Single(book.Parts);
        Assert.Equal(4, part.Visuals.Count);
        Assert.Equal(7, part.Activities.Count);
        Assert.Equal(1, extrasAsked);
        Assert.Equal(1, StudyFile.Made(plan, chosen, options));
        // Without activities they aren't asked for; another request is another file.
        Assert.Equal(0, StudyFile.Made(plan, chosen, options with { Request = "Make it short" }));
        var none = await StudyFile.MakeAsync(plan, chosen, options with { Activities = StudyActivities.None }, "Cells", "cells.pdf", (a, b) => $"Page {a.Number}");
        Assert.Empty(none.Parts[0].Activities);
        Assert.Equal(1, extrasAsked);
    }

    [Fact]
    public void The_page_shows_diagrams_and_activities_as_worksheets_its_script_makes_pieces_of()
    {
        var extras = StudyExtrasReader.Read(ExtrasReply, "Page 4");
        var sample = SampleBook("en");
        var parts = sample.Parts.Select(p => p.Number == 2 ? p with { } : p).ToList();
        parts[1] = parts[1] with { Visuals = extras.Visuals, Activities = extras.Activities };
        var book = sample with { Parts = parts };
        var html = StudyFileWriter.Html(book, StudyFileWindow.Words("en"));
        Assert.Contains("<section class=\"visuals\">", html);
        Assert.Contains("<figure class=\"vis flow brief\">", html);
        Assert.Contains("<figure class=\"vis timeline\">", html);
        Assert.Contains("<figcaption class=\"hub\">The cell membrane</figcaption>", html);
        Assert.Contains("<th scope=\"col\">plant cell</th>", html);
        Assert.Contains("<section class=\"acts\">", html);
        Assert.Equal(7, Regex.Matches(html, "<div class=\"act\" id=\"a[0-9a-f]{10}\"").Count);
        // The map to complete: without the script, a line for each name under what its branch says, the names below.
        Assert.Contains("<p class=\"kind\">Complete the map</p>", html);
        Assert.Contains("<div class=\"sheet mapsheet\"><p class=\"hub\">The cell membrane</p>", html);
        Assert.Contains("<li><strong>Made of</strong> — phospholipids; proteins</li>", html);
        // Without the script, a worksheet with its answer to open; the blanks as gaps.
        Assert.Contains("<p class=\"kind\">Fill in the blanks</p>", html);
        Assert.Contains("<span class=\"gap\"></span>", html);
        Assert.Contains("<strong>phospholipids</strong>", html);
        Assert.Contains("href=\"#practice\"", html);
        Assert.Contains("7 activities", html);
        // Still only its own script, and what the model wrote is data, not markup.
        Assert.Single(Regex.Matches(html, @"<script>(.*?)</script>", RegexOptions.Singleline));
        var data = Regex.Match(html, "<script type=\"application/json\" id=\"data\">(.*?)</script>", RegexOptions.Singleline).Groups[1].Value;
        using var json = JsonDocument.Parse(data);
        var acts = json.RootElement.GetProperty("acts");
        Assert.Equal(7, acts.GetArrayLength());
        Assert.Equal("mapfill", acts[2].GetProperty("k").GetString());
        Assert.Equal("order", acts[0].GetProperty("k").GetString());
        var order = acts[0].GetProperty("o").EnumerateArray().Select(e => e.GetInt32()).ToList();
        Assert.Equal([0, 1, 2, 3], order.Order());
        Assert.Equal(["Plant cells only", "Animal cells only", "Both"], acts[4].GetProperty("g").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(3, acts[5].GetProperty("o").GetArrayLength());
        Assert.Equal(StudyFileWords.Keys.Length, json.RootElement.GetProperty("w").EnumerateObject().Count());
    }

    /// <summary>
    /// With the real model, to be read and looked at: PETAL_STUDYFILE (a book or a recording), PETAL_STUDYFILE_PARTS
    /// ("17-22"), PETAL_STUDYFILE_INTO (a language, or the file's), PETAL_STUDYFILE_OUT (where the page goes).
    /// </summary>
    [Fact]
    public async Task The_real_model_makes_a_study_file()
    {
        if (Environment.GetEnvironmentVariable("PETAL_STUDYFILE") is not { Length: > 0 } file)
            return;
        var outFolder = Environment.GetEnvironmentVariable("PETAL_STUDYFILE_OUT") ?? _dir;
        Directory.CreateDirectory(outFolder);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var whisper = Petal.Core.Models.ModelStore.Find(Petal.Core.Models.ModelCatalog.WhisperAccurate) ?? Petal.Core.Models.ModelStore.Find(Petal.Core.Models.ModelCatalog.WhisperBalanced);
        var plan = await StudyFile.ReadAsync(file, whisper, cloud: false);
        output.WriteLine($"read in {clock.Elapsed.TotalSeconds:0}s: {plan.Parts.Count} parts, {plan.Parts.Sum(p => p.Units.Count)} passages, structured {plan.Structured}");
        var range = (Environment.GetEnvironmentVariable("PETAL_STUDYFILE_PARTS") ?? "").Split('-');
        int from = range.Length == 2 ? int.Parse(range[0]) : 1, to = range.Length == 2 ? int.Parse(range[1]) : plan.Parts.Count;
        var chosen = plan.Parts.Where(p => p.Number >= from && p.Number <= to && p.Suggested).Select(p => p.Number).ToHashSet();
        var into = Environment.GetEnvironmentVariable("PETAL_STUDYFILE_INTO") is { Length: > 0 } wanted ? wanted : plan.Language;
        clock.Restart();
        var purpose = Enum.TryParse<StudyPurpose>(Environment.GetEnvironmentVariable("PETAL_STUDYFILE_PURPOSE"), true, out var p0) ? p0 : StudyPurpose.Study;
        var scenes = Enum.TryParse<StudyScenes>(Environment.GetEnvironmentVariable("PETAL_STUDYFILE_SCENES"), true, out var s0) ? s0 : StudyScenes.Some;
        var book = await StudyFile.MakeAsync(plan, chosen, new StudyFile.Options(into, StudyAmount.Normal, plan.Language, Environment.GetEnvironmentVariable("PETAL_STUDYFILE_REQUEST") ?? "",
                StudyActivities.Some, purpose, scenes), Path.GetFileNameWithoutExtension(file), Path.GetFileName(file),
            (a, b) => StudyFileWindow.Place(into, a, b), new Synchronous<StudyProgress>(p => output.WriteLine($"{p.Done}/{p.Total} {p.Part}")));
        foreach (var scene in book.Parts.SelectMany(x => x.Scenes))
            output.WriteLine($"scene {scene.Kind} «{scene.Title}»: {scene.Things.Count} things, {scene.Mixes.Count} mixes, {scene.Vars.Count} sliders, {string.Join("; ", scene.Results.Select(r => r.Id + "=" + r.Formula))}, {scene.Links.Count} links, {scene.Stages.Count} situations, {scene.Guesses.Count} guesses");
        var page = Path.Combine(outFolder, Path.GetFileNameWithoutExtension(file) + "-" + into + ".html");
        StudyFileWriter.Write(page, book, StudyFileWindow.Words(into));
        output.WriteLine($"made in {clock.Elapsed.TotalSeconds:0}s: {book.Parts.Count} parts, {book.Terms.Count} terms, {book.Parts.Sum(p => p.Cards.Count)} cards, {book.Parts.Sum(p => p.Quiz.Count)} questions → {page}");
        Assert.NotEmpty(book.Parts);
    }

    /// <summary>The page's way of writing the model's words, as a page read right to left writes them.</summary>
    private static class StudyFileWriterProbe
    {
        public static string Written(string text)
        {
            var book = SampleBook("ar") with { Overview = text };
            var html = StudyFileWriter.Html(book, StudyFileWindow.Words("ar"));
            return Regex.Match(html, "<div class=\"lead\"><p>(.*?)</p></div>", RegexOptions.Singleline).Groups[1].Value;
        }
    }

    /// <summary>Progress reported as it happens, not later on another thread.</summary>
    private sealed class Synchronous<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
