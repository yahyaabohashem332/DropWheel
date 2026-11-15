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

    private static PlacedText Page(int number, string text) => new(PlaceKind.Page, number, text);

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

    private static string Answer(string given) =>
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
        var book = await StudyFile.MakeAsync(plan, chosen, new StudyFile.Options(into, StudyAmount.Normal, plan.Language), Path.GetFileNameWithoutExtension(file), Path.GetFileName(file),
            (a, b) => StudyFileWindow.Place(into, a, b), new Synchronous<StudyProgress>(p => output.WriteLine($"{p.Done}/{p.Total} {p.Part}")));
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
