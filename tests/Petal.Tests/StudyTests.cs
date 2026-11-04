using System.IO;
using System.IO.Compression;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Petal.Engines.Documents;
using Petal.Recipes.Study;
using Xunit.Abstractions;

namespace Petal.Tests;

/// <summary>Study notes: what the model writes is read carefully, and the deck is one Anki can read.</summary>
public sealed class StudyTests(ITestOutputHelper output)
{
    private static PlacedText Page(int number, string text) => new(PlaceKind.Page, number, text);

    [Fact]
    public void A_file_is_given_a_stretch_at_a_time_knowing_its_pages()
    {
        var places = Enumerable.Range(1, 6).Select(n => Page(n, new string('a', 1500) + ".")).ToList();
        var stretches = StudyMaker.Stretches(places);
        Assert.True(stretches.Count >= 2);
        Assert.All(stretches, s => Assert.True(s.Text.Length <= 3600, s.Text.Length.ToString()));
        Assert.Equal(1, stretches[0].First.Number);
        Assert.Equal(6, stretches[^1].Last.Number);
    }

    [Fact]
    public void A_long_page_is_cut_and_empty_ones_are_passed_over()
    {
        var stretches = StudyMaker.Stretches([Page(1, "   "), Page(2, string.Join(". ", Enumerable.Repeat("A sentence of words", 500)))]);
        Assert.True(stretches.Count >= 2);
        Assert.All(stretches, s => Assert.Equal(2, s.First.Number));
    }

    [Fact]
    public void What_the_model_writes_is_read_around_whatever_it_adds()
    {
        const string reply = """
            Here you are:
            ```json
            {"notes":["Plants make food."],
             "cards":[{"q":"What is photosynthesis?","a":"Making food from light."},{"q":"","a":"no question"},{"q":"no answer"}],
             "quiz":[
               {"q":"Where does it happen?","options":["Chloroplasts","Roots","Soil","Air"],"answer":0,"why":"The text says so."},
               {"q":"Three choices only","options":["a","b","c"],"answer":0,"why":""},
               {"q":"Same choices","options":["a","a","b","c"],"answer":0,"why":""},
               {"q":"Answer out of range","options":["a","b","c","d"],"answer":7,"why":""},
               {"q":"Answer by its words","options":["a","b","c","d"],"answer":"C","why":""}
             ],}
            ```
            """;
        var parsed = StudyMaker.Parse(reply, "Page 3");
        Assert.NotNull(parsed);
        var (notes, cards, quiz) = parsed.Value;
        Assert.Equal("Plants make food.", Assert.Single(notes));
        var card = Assert.Single(cards);
        Assert.Equal(("What is photosynthesis?", "Page 3"), (card.Front, card.Where));
        Assert.Equal(2, quiz.Count);
        Assert.Equal(0, quiz[0].Correct);
        Assert.Equal(2, quiz[1].Correct);
    }

    [Theory]
    [InlineData("I can't do that.")]
    [InlineData("{ not json at all")]
    public void A_reply_that_isnt_what_was_asked_is_said_to_be(string reply) =>
        Assert.Null(StudyMaker.Parse(reply, ""));

    [Fact]
    public void The_same_thing_asked_twice_is_one_card()
    {
        var cards = StudyMaker.Distinct([new StudyCard("What is a cell?", "a", ""), new StudyCard("what is a CELL", "b", ""), new StudyCard("What is DNA?", "c", "")], c => c.Front);
        Assert.Equal(2, cards.Count);
    }

    [Fact]
    public void The_model_is_told_to_keep_to_the_text_and_write_in_the_language_chosen()
    {
        var told = StudyMaker.Instruction("tr", StudyAmount.More);
        Assert.Contains("Turkish", told);
        Assert.Contains("invent nothing", told);
        Assert.Contains("up to 8", told);
        Assert.Contains("up to 3", StudyMaker.Instruction("ar", StudyAmount.Fewer));
    }

    private static readonly StudyCard Card = new("ما هو <التمثيل> الضوئي؟", "صنع الغذاء\nمن الضوء", "Page 3");
    private static readonly StudyQuestion Question = new("Where & when?", ["Roots", "Leaves", "Soil", "Air"], 1, "The text says so.", "Page 4");

    [Fact]
    public void A_deck_is_a_package_anki_can_read()
    {
        var folder = Path.Combine(Path.GetTempPath(), "Petal", "anki-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, "Biology.apkg");
            AnkiDeck.Write(path, "Biology", [Card, Card with { Front = "Second" }], [Question]);
            using var zip = ZipFile.OpenRead(path);
            Assert.Equal(["collection.anki2", "media"], zip.Entries.Select(e => e.FullName).Order());
            using (var media = new StreamReader(zip.GetEntry("media")!.Open()))
                Assert.Equal("{}", media.ReadToEnd());
            var database = Path.Combine(folder, "c.anki2");
            zip.GetEntry("collection.anki2")!.ExtractToFile(database);

            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Pooling = false }.ToString());
            connection.Open();
            object? One(string sql)
            {
                using var c = connection.CreateCommand();
                c.CommandText = sql;
                return c.ExecuteScalar();
            }
            Assert.Equal(11L, One("select ver from col"));
            Assert.Equal(3L, One("select count(*) from notes"));
            Assert.Equal(3L, One("select count(*) from cards"));
            // Every card belongs to a note, and every note to a note type and every card to a deck that exist.
            Assert.Equal(0L, One("select count(*) from cards where nid not in (select id from notes)"));
            using var models = JsonDocument.Parse((string)One("select models from col")!);
            using var decks = JsonDocument.Parse((string)One("select decks from col")!);
            using (var c = connection.CreateCommand())
            {
                c.CommandText = "select mid from notes";
                using var r = c.ExecuteReader();
                while (r.Read())
                    Assert.True(models.RootElement.TryGetProperty(r.GetInt64(0).ToString(), out _));
            }
            var deckId = (long)One("select distinct did from cards")!;
            Assert.Equal("Biology", decks.RootElement.GetProperty(deckId.ToString()).GetProperty("name").GetString());
            // Fields are separated as Anki separates them, one per field of the note type, and what the text says stays what it says.
            var fields = ((string)One("select flds from notes order by id limit 1")!).Split((char)0x1f);
            Assert.Equal(3, fields.Length);
            Assert.Equal("ما هو &lt;التمثيل&gt; الضوئي؟", fields[0]);
            Assert.Equal("صنع الغذاء<br>من الضوء", fields[1]);
            var quiz = ((string)One("select flds from notes order by id desc limit 1")!).Split((char)0x1f);
            Assert.Equal(5, quiz.Length);
            Assert.Equal("Where &amp; when?", quiz[0]);
            Assert.Contains("<li>Leaves</li>", quiz[1]);
            Assert.Equal("Leaves", quiz[2]);
            // Each note its own id; made again it is the same note to Anki.
            Assert.Equal(3L, One("select count(distinct guid) from notes"));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void The_notes_say_the_answers_and_where_they_come_from()
    {
        var text = StudyMaker.Markdown("Biology", new StudyPack("Overview.", [Card], [Question]), "Cards", "Quiz", "Answer:");
        Assert.Contains("# Biology", text);
        Assert.Contains("Overview.", text);
        Assert.Contains("(Page 3)", text);
        Assert.Contains("B) Leaves: The text says so. (Page 4)", text);
    }

    /// <summary>With the real model, to be read: PETAL_STUDY=1 and the model in its usual place.</summary>
    [Fact]
    public async Task The_real_model_makes_notes()
    {
        if (Environment.GetEnvironmentVariable("PETAL_STUDY") != "1")
            return;
        PlacedText[] english =
        [
            Page(1, "Photosynthesis is the process by which green plants make their own food. It takes place mainly in the leaves, inside small structures called chloroplasts, which contain the green pigment chlorophyll. Chlorophyll absorbs energy from sunlight. Using that energy, the plant turns carbon dioxide from the air and water from the soil into glucose, a sugar, and releases oxygen as a by-product."),
            Page(2, "The process has two stages. In the light-dependent reactions, which happen in the thylakoid membranes, light energy splits water molecules and produces ATP and NADPH. In the Calvin cycle, which happens in the stroma and does not need light directly, ATP and NADPH are used to build glucose from carbon dioxide. Temperature, light intensity and the amount of carbon dioxide all limit the rate of photosynthesis."),
        ];
        PlacedText[] arabic =
        [
            Page(1, "الدورة الدموية الصغرى تنقل الدم من القلب إلى الرئتين وتعود به إلى القلب. يخرج الدم الفقير بالأكسجين من البطين الأيمن عبر الشريان الرئوي إلى الرئتين، وهناك يتخلص من ثاني أكسيد الكربون ويأخذ الأكسجين، ثم يعود عبر الأوردة الرئوية إلى الأذين الأيسر. أما الدورة الدموية الكبرى فتبدأ من البطين الأيسر عبر الأبهر لتوزع الدم الغني بالأكسجين على أنحاء الجسم، ثم يعود الدم عبر الوريدين الأجوفين إلى الأذين الأيمن."),
        ];
        foreach (var (places, into) in new[] { (english, "en"), (arabic, "ar"), (english, "tr") })
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var pack = await StudyMaker.MakeAsync(places, into, StudyAmount.Normal, (a, b) => a.Number == b.Number ? $"Page {a.Number}" : $"Pages {a.Number}-{b.Number}");
            output.WriteLine($"=== into {into}, {clock.Elapsed.TotalSeconds:0}s");
            output.WriteLine(StudyMaker.Markdown("Test", pack, "Cards", "Quiz", "Answer:"));
            Assert.NotEmpty(pack.Cards);
            Assert.NotEmpty(pack.Quiz);
            Assert.NotEmpty(pack.Summary);
        }
    }
}
