using System.Diagnostics;
using System.IO;
using Petal.Core.Models;
using Petal.Engines.Documents;
using Petal.Engines.Media;
using Petal.Recipes.Questions;
using Xunit.Abstractions;

namespace Petal.Tests;

/// <summary>Ask a File: passages with their places, finding them by words and meaning, and answers that say where they come from.</summary>
public sealed class AskFileTests(ITestOutputHelper output)
{
    [Fact]
    public void A_page_is_parted_into_passages_at_sentences_each_beginning_with_the_last_one_before()
    {
        var sentence = string.Join(' ', Enumerable.Repeat("word", 29)) + ".";
        var page = new PlacedText(PlaceKind.Page, 7, string.Join(' ', Enumerable.Repeat(sentence, 9)));
        var passages = AskFile.Split([page, new PlacedText(PlaceKind.Page, 8, "Short page.")]);
        Assert.All(passages, p => Assert.InRange(p.Text.Split(' ').Length, 1, 160));
        Assert.Equal(4, passages.Count(p => p.Place.Number == 7));
        // Each after the first begins with the sentence the one before ended with.
        Assert.Equal(sentence, passages[1].Text[..sentence.Length]);
        Assert.Equal("Short page.", Assert.Single(passages, p => p.Place.Number == 8).Text);
        Assert.Equal(Enumerable.Range(0, passages.Count), passages.Select(p => p.Number));
    }

    [Fact]
    public void Rows_stay_whole_and_a_recording_is_parted_by_the_minute()
    {
        var rows = new PlacedText(PlaceKind.Rows, 2, string.Join('\n', Enumerable.Range(1, 30).Select(i => $"Item: thing {i}; Price: {i * 3}"))) { Name = "Prices", Last = 31 };
        var passages = AskFile.Split([rows]);
        Assert.All(passages, p => Assert.DoesNotContain("thing 1 thing", p.Text));
        Assert.Contains(passages, p => p.Text.Contains("Item: thing 30; Price: 90"));
        Assert.StartsWith("Prices: ", AskFile.Indexed(passages[0]));

        var said = Enumerable.Range(0, 30).Select(i => new TranscriptSegment(TimeSpan.FromSeconds(i * 5), TimeSpan.FromSeconds(i * 5 + 4), $"Sentence number {i} is said here.")).ToList();
        var times = AskFile.Times(said);
        Assert.All(times, t => Assert.InRange(t.End - t.Start, 0, 60));
        Assert.Equal(0, times[0].Start);
        Assert.Equal(149, times[^1].End);
        Assert.Equal("at 2:05", AskFile.Where(new PlacedText(PlaceKind.Time, 1, "") { Start = 125.7 }));
        Assert.Equal("Prices, rows 2–31", AskFile.Where(rows));
    }

    [Fact]
    public void Words_are_compared_folded_and_stemmed_without_the_little_ones()
    {
        Assert.Equal(["ticket", "cost", "station"], WordIndex.Terms("What do the tickets cost at the station?").ToArray());
        // "الكتاب" and "كتابها" are one word; "في" and "هل" are left out; أ and ا are one letter.
        Assert.Equal(["كتاب", "كتاب", "اسعار"], WordIndex.Terms("هل في الكتاب كتابها أسعار").ToArray());
        var index = new WordIndex(["The museum opens at nine.", "Tickets cost twenty dollars at the station.", "أسعار التذاكر عشرون دولارًا"]);
        var scores = index.Scores("How much is a ticket?");
        Assert.True(scores[1] > 0 && scores[0] == 0 && scores[2] == 0);
        Assert.True(index.Scores("كم أسعار التذاكر")[2] > 0);
    }

    [Fact]
    public void The_models_reply_is_read_into_an_answer_and_the_passages_it_names()
    {
        string[] subjects = ["apples", "trains", "rivers", "music"];
        var given = Enumerable.Range(0, 4).Select(i => new Passage(i, new PlacedText(PlaceKind.Page, i + 10, ""), $"This passage is about {subjects[i]}.")).ToList();
        var answer = AskFile.Read("Answer: Yes, it is free [2].\nSources: [2], 4, 9", given, given);
        Assert.False(answer.NotFound);
        Assert.Equal("Yes, it is free .".Replace(" .", "."), answer.Text.Replace(" .", "."));
        Assert.Equal([11, 13], answer.Sources.Select(p => p.Place.Number).ToArray());

        var none = AskFile.Read("Answer: NOT FOUND", given, given);
        Assert.True(none.NotFound);
        Assert.Equal(4, none.Found.Count);
        // Named none: the passage sharing most words with the answer.
        Assert.Equal(12, AskFile.Read("Answer: It is about rivers.", given, given).Sources.Single().Place.Number);
        // While it writes, only the answer is shown.
        Assert.Equal("Yes, it is", AskFile.Shown("Answer: Yes, it is"));
        Assert.Equal("", AskFile.Shown("Answer: NOT FO"));
        Assert.Equal("", AskFile.Shown("Answer: NOT FOUND"));
        Assert.Equal("No, it isn't.", AskFile.Shown("Answer: No, it isn't. Sources: 1"));
        // One source, or the sources named in Arabic.
        Assert.Equal([10], AskFile.Read("Answer: It is.\nSource: 1", given, given).Sources.Select(p => p.Place.Number).ToArray());
        Assert.Equal([13], AskFile.Read("Answer: نعم.\nالمصادر: 4", given, given).Sources.Select(p => p.Place.Number).ToArray());
    }

    [Fact]
    public void Arabic_takes_more_of_the_models_pieces_than_English()
    {
        Assert.InRange(AskFile.Pieces("Everyone has the right to education."), 7, 11);
        Assert.True(AskFile.Pieces("لكل شخص الحق في التعلم") > AskFile.Pieces("Everyone has the right"));
    }

    [Fact]
    public void A_files_language_is_told_by_its_letters()
    {
        Assert.Equal("ar", AskFile.LanguageOf(["لكل شخص الحق في التعلم", "Article 26"]));
        Assert.Equal("en", AskFile.LanguageOf(["Everyone has the right to education."]));
        Assert.Equal("ru", AskFile.LanguageOf(["Всеобщая декларация"]));
    }

    /// <summary>With PETAL_ASK pointing at a models folder holding multilingual-e5-small: its pieces and meanings are the reference's.</summary>
    [Fact]
    public void Pieces_and_meanings_match_the_models_own_tokenizer_and_runtime()
    {
        using var models = Models();
        if (models is null)
            return;
        using var model = new PassageModel();
        (string Text, long[] Ids, float[] First)[] cases =
        [
            ("query: هل التعليم الابتدائي إلزامي؟", [0, 41, 1294, 12, 10789, 46780, 206955, 128991, 92480, 179, 1245, 2], [0.06540f, -0.00018f, -0.02823f, -0.06166f, 0.03252f, -0.04106f]),
            ("passage: Everyone has the right to education. Elementary education shall be compulsory.",
                [0, 46692, 12, 191096, 1556, 70, 7108, 47, 53019, 5, 48325, 6635, 53019, 35299, 186, 375, 69035, 30675, 5, 2], [0.02650f, 0.00296f, -0.03465f, -0.03310f, 0.07459f, -0.01711f]),
            ("passage: Ｆｕｌｌ-width ＡＢＣ and café… naïve – “quotes”  and\ttabs",
                [0, 46692, 12, 9312, 9, 146984, 47457, 136, 26216, 27, 24, 9392, 272, 46, 52, 68726, 7, 63, 136, 20928, 7, 2], [0.02588f, -0.01429f, -0.06359f, -0.08460f, 0.06055f, -0.03386f]),
        ];
        foreach (var (text, ids, first) in cases)
        {
            Assert.Equal(ids, model.Ids(text));
            var meaning = model.Query(text["query: ".Length..]) is var q && text.StartsWith("query: ") ? q : model.Passages([text["passage: ".Length..]])[0];
            for (int k = 0; k < first.Length; k++)
                Assert.InRange(meaning[k], first[k] - 2e-4f, first[k] + 2e-4f);
        }
    }

    /// <summary>
    /// With PETAL_ASK (the models) and PETAL_ASK_DOCS (the Universal Declaration of Human Rights in English and Arabic,
    /// udhr_booklet_en_web.pdf and udhr_booklet_ar_web.pdf, and the Constitution of the United States, CDOC-110hdoc50.pdf):
    /// questions in either language answered from the right page, and one the file can't answer said so.
    /// </summary>
    [Fact]
    public async Task Real_documents_are_answered_from_the_right_page()
    {
        var docs = Environment.GetEnvironmentVariable("PETAL_ASK_DOCS");
        using var models = Models();
        if (docs is null || models is null || !AskFile.CanAnswer)
        {
            output.WriteLine("Skipped: PETAL_ASK or PETAL_ASK_DOCS isn't set.");
            return;
        }
        var before = AskFile.CacheFolder;
        AskFile.CacheFolder = Path.Combine(Path.GetTempPath(), "petal-ask-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            (string File, string Question, string[] Expected)[] cases =
            [
                ("udhr_booklet_en_web.pdf", "Is elementary education compulsory?", ["compulsory"]),
                ("udhr_booklet_en_web.pdf", "Can I choose my own job?", ["employment"]),
                ("udhr_booklet_en_web.pdf", "Do I have the right to leave my country?", ["leave any country"]),
                ("udhr_booklet_en_web.pdf", "هل يجوز استعباد أي شخص؟", ["slavery"]),
                ("udhr_booklet_en_web.pdf", "What is the capital of France?", []),
                ("udhr_booklet_ar_web.pdf", "هل التعليم الابتدائي إلزامي؟", ["الزامي"]),
                ("udhr_booklet_ar_web.pdf", "ماذا تقول الوثيقة عن الزواج؟", ["الزواج", "يتزوج", "تزوج"]),
                ("CDOC-110hdoc50.pdf", "How old must the President be?", ["thirty five"]),
                ("CDOC-110hdoc50.pdf", "How long is a Senator's term?", ["six years"]),
                ("CDOC-110hdoc50.pdf", "Who has the power to declare war?", ["declare war"]),
            ];
            int right = 0, found = 0;
            foreach (var (name, question, expected) in cases)
            {
                var clock = Stopwatch.StartNew();
                var file = await AskFile.ReadAsync(Path.Combine(docs, name));
                double reading = clock.Elapsed.TotalSeconds;
                clock.Restart();
                var passages = AskFile.Find(file, question);
                double finding = clock.Elapsed.TotalSeconds;
                clock.Restart();
                var answer = await AskFile.AnswerAsync(question, passages);
                double answering = clock.Elapsed.TotalSeconds;
                bool Holds(Passage p) => expected.Any(e => Petal.Recipes.AskPetal.Fold(p.Text).Contains(Petal.Recipes.AskPetal.Fold(e), StringComparison.Ordinal));
                bool inFound = passages.Take(5).Any(Holds);
                bool ok = expected.Length == 0 ? answer.NotFound : !answer.NotFound && answer.Sources.Any(Holds);
                right += ok ? 1 : 0;
                found += expected.Length == 0 || inFound ? 1 : 0;
                output.WriteLine($"{(ok ? "RIGHT" : "WRONG")} {name}: {question}\n  {(answer.NotFound ? "NOT FOUND" : answer.Text)}\n  from {string.Join(", ", answer.Sources.Select(s => AskFile.Where(s.Place)))}; " +
                    $"found {string.Join(", ", passages.Take(5).Select(s => AskFile.Where(s.Place)))} (in the first five: {inFound})\n  " +
                    $"{file.Passages.Count} passages, read in {reading:0.0} s, found in {finding:0.00} s, answered in {answering:0.0} s");
            }
            output.WriteLine($"{right} of {cases.Length} right; the passage was found for {found}.");
            Assert.True(found >= cases.Length - 1, "passages not found");
            Assert.True(right >= cases.Length - 1, "answers wrong");
        }
        finally
        {
            try { Directory.Delete(AskFile.CacheFolder, recursive: true); } catch (IOException) { }
            AskFile.CacheFolder = before;
        }
    }

    /// <summary>
    /// With PETAL_ASK (the models, Whisper small among them) and PETAL_ASK_KINDS (handbook.docx, a handbook with a
    /// section on each page; sales.xlsx, a table of sales; launch.pptx, a launch plan; nasa-150.webm, an interview from
    /// the International Space Station): each kind of file answered from its own place.
    /// </summary>
    [Fact]
    public async Task Each_kind_of_file_is_answered_from_its_own_place()
    {
        var kinds = Environment.GetEnvironmentVariable("PETAL_ASK_KINDS");
        using var models = Models();
        if (kinds is null || models is null || !AskFile.CanAnswer)
        {
            output.WriteLine("Skipped: PETAL_ASK or PETAL_ASK_KINDS isn't set.");
            return;
        }
        var before = AskFile.CacheFolder;
        AskFile.CacheFolder = Path.Combine(Path.GetTempPath(), "petal-ask-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            // The place must hold what the answer says; on a laid-out page the answer's words must be found where the page is
            // marked, so the page number is the printed one.
            (string File, string Question, string Expected, string Said, PlaceKind Kind)[] cases =
            [
                ("handbook.docx", "How many days of annual leave do I get?", "24", "24 working days", PlaceKind.Page),
                ("handbook.docx", "How long is the notice period after probation?", "three months", "three months after it", PlaceKind.Page),
                ("sales.xlsx", "How many Heron units did the East region sell in March?", "731", "731", PlaceKind.Rows),
                ("launch.pptx", "When does the lamp go on sale?", "14 March 2027", "14 March 2027", PlaceKind.Slide),
                ("launch.pptx", "What is the marketing budget?", "250,000", "250,000 euros", PlaceKind.Slide),
                ("nasa-150.webm", "What do astronauts call the sickness they feel when they become weightless?", "space adaptation syndrome", "space adaptation syndrome", PlaceKind.Time),
                ("nasa-150.webm", "ما اسم الفريق الذي تلعب له مادي؟", "", "", PlaceKind.Time),
            ];
            int right = 0;
            foreach (var (name, question, expected, said, kind) in cases)
            {
                var clock = Stopwatch.StartNew();
                var file = await AskFile.ReadAsync(Path.Combine(kinds, name), ModelStore.Find(ModelCatalog.WhisperBalanced));
                double reading = clock.Elapsed.TotalSeconds;
                clock.Restart();
                var found = AskFile.Find(file, question);
                var answer = await AskFile.AnswerAsync(question, found);
                double answering = clock.Elapsed.TotalSeconds;
                bool Holds(Passage p) => p.Place.Kind == kind && p.Text.Contains(said, StringComparison.OrdinalIgnoreCase)
                    && (file.PagesPdf is not { } pdf || DocumentPlaces.Marks(pdf, p.Place.Number, AskFile.Quote(p, answer.Text)).Count > 0);
                // Asked what it doesn't say (this part of the interview never names her team), it says so.
                bool ok = expected.Length == 0 ? answer.NotFound
                    : !answer.NotFound && answer.Text.Contains(expected, StringComparison.OrdinalIgnoreCase) && answer.Sources.Any(Holds);
                right += ok ? 1 : 0;
                output.WriteLine($"{(ok ? "RIGHT" : "WRONG")} {name}: {question}\n  {(answer.NotFound ? "NOT FOUND" : answer.Text)}\n  from " +
                    $"{string.Join(", ", answer.Sources.Select(s => AskFile.Where(s.Place)).Distinct())}; {file.Passages.Count} passages" +
                    $"{(file.PagesPdf is null ? "" : ", laid out")}, read in {reading:0.0} s, answered in {answering:0.0} s");
                if (!ok)
                    foreach (var p in found.Take(5))
                        output.WriteLine($"    {AskFile.Where(p.Place)}: {p.Text[..Math.Min(160, p.Text.Length)]}");
            }
            output.WriteLine($"{right} of {cases.Length} right.");
            Assert.Equal(cases.Length, right);
        }
        finally
        {
            try { Directory.Delete(AskFile.CacheFolder, recursive: true); } catch (IOException) { }
            AskFile.CacheFolder = before;
        }
    }

    /// <summary>The models folder PETAL_ASK names, used until disposed (null when it isn't set or lacks the passage model).</summary>
    private static IDisposable? Models()
    {
        var folder = Environment.GetEnvironmentVariable("PETAL_ASK");
        if (folder is null)
            return null;
        var saved = ModelStore.Folder;
        ModelStore.Folder = folder;
        if (PassageModel.Ready)
            return new Restore(() => ModelStore.Folder = saved);
        ModelStore.Folder = saved;
        return null;
    }

    private sealed class Restore(Action undo) : IDisposable
    {
        public void Dispose() => undo();
    }
}
