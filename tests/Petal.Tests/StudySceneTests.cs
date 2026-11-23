using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Petal.App.Tools.Windows;
using Petal.Engines.Documents;
using Petal.Recipes.Study;

namespace Petal.Tests;

/// <summary>
/// What an interactive file is for, and its hands-on scenes: read from what a small model writes (forgiving its habits,
/// refusing what can't be played), asked once per passage and kept, and drawn on the page with what shows without its
/// script and in print. The page itself is tried by hand from the pages PETAL_STUDYFILE_PAGE writes.
/// </summary>
[Collection("Language")]
public sealed class StudySceneTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "petal-scenes-" + Guid.NewGuid().ToString("N"));
    private readonly string _cache = StudyFile.CacheFolder;

    public StudySceneTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        StudyFile.TestWriter = null;
        StudyFile.CacheFolder = _cache;
        try
        {
            Directory.Delete(_dir, true);
        }
        catch (IOException)
        {
        }
    }

    private static IReadOnlyList<StudyScene> Scenes => StudySceneReader.Read(StudyFileTests.ScenesReply, "Page 4");

    private static StudyScene Of(StudySceneKind kind) => Assert.Single(Scenes, s => s.Kind == kind);

    [Fact]
    public void A_lab_gathers_its_things_from_every_heading_and_every_mix()
    {
        var lab = Of(StudySceneKind.Lab);
        Assert.Equal("Acids, bases and metals", lab.Title);
        // The second LAB heading carries on the first, and its mix puts vinegar and baking soda on the shelf.
        Assert.Equal(["Hydrochloric acid", "Sodium hydroxide", "Universal indicator", "Magnesium ribbon", "Copper", "Vinegar", "Baking soda"], lab.Things.Select(t => t.Name));
        Assert.Equal(6, lab.Mixes.Count);
        var fizz = lab.Mixes.Single(m => m.Effect == "fizz");
        Assert.Equal([0, 3], fizz.Things);
        Assert.Equal("grey", fizz.Color);
        Assert.Equal("none", lab.Mixes.Single(m => m.Things.Contains(4)).Effect);
        Assert.Equal("green", lab.Things[2].Color);
    }

    [Fact]
    public void A_lab_needs_things_named_as_they_are_and_mixes_that_say_what_happens()
    {
        // "Dilute hydrochloric acid" is the acid already there; a mix of one thing, or one that says nothing, isn't one.
        var lab = Assert.Single(StudySceneReader.Read("""
            LAB: Metals in acid
            THING: Hydrochloric acid = an acid | clear
            THING: Copper = a metal | brown
            MIX: Copper + Dilute hydrochloric acid | nothing happens | copper is less reactive than hydrogen | none | none
            MIX: Copper | it shines | | spark | yellow
            MIX: Copper + Hydrochloric acid | ok | | none | clear
            """, "Page 1"));
        Assert.Equal(2, lab.Things.Count);
        var mix = Assert.Single(lab.Mixes);
        Assert.Equal([0, 1], mix.Things);
        Assert.Equal("", mix.Color);
        Assert.Empty(StudySceneReader.Read("LAB: Nothing\nTHING: Water = water | clear", "Page 1"));
    }

    [Fact]
    public void A_simulation_keeps_only_formulas_that_work_and_move()
    {
        var sim = Of(StudySceneKind.Simulate);
        Assert.Equal(["speed", "reaction"], sim.Vars.Select(v => v.Id));
        var result = Assert.Single(sim.Results);
        Assert.Equal("speed * reaction", result.Formula);
        Assert.Equal(2, sim.Levels.Count);
        Assert.Equal((">", 20.0), (sim.Levels[0].Op, sim.Levels[0].Value));

        // A formula of names that aren't sliders, one that never moves, and one with a sentence for a start.
        Assert.Empty(StudySceneReader.Read("""
            SIMULATE: Distance
            VAR: d | distance | m | 0 | 1000 | 0
            VAR: s | speed | m/s | 0 | 100 | 0
            RESULT: dist | distance | m | s * d
            """, "Page 1"));
        Assert.Empty(StudySceneReader.Read("SIMULATE: Speed\nVAR: d | distance | m | 0 | 100 | 50\nRESULT: v | speed | m/s | d / t", "Page 1"));
        var mid = Assert.Single(StudySceneReader.Read("""
            SIMULATE: Area
            VAR: side | side | cm | 1 | 9 | about five centimetres
            RESULT: area | area | cm² | side²
            RESULT: double = area × 2
            """, "Page 1"));
        Assert.Equal(5, mid.Vars[0].Start);
        Assert.Equal("side^2", mid.Results[0].Formula);
    }

    [Fact]
    public void A_formula_is_read_as_the_page_reads_it_and_nothing_else()
    {
        var known = new HashSet<string> { "x", "y", "سرعة" };
        double At(string formula) => SceneFormula.Parse(SceneFormula.Tidy(formula), known)!.Value(new Dictionary<string, double> { ["x"] = 3, ["y"] = 16, ["سرعة"] = 2 });
        Assert.Equal(3 * 9 + 4, At("x * x^2 + sqrt(y)"), 9);
        Assert.Equal(-9, At("-x^2"), 9);
        Assert.Equal(16, At("max(x, y, 2)"), 9);
        Assert.Equal(6, At("سرعة × x"), 9);
        Assert.Equal(Math.PI * 9, At("pi * x²"), 9);
        Assert.Equal(1.5, At("y = x ÷ 2"), 9);
        foreach (var bad in new[] { "2x", "x + z", "alert(1)", "x;y", "x +", "sqrt()", "(x", "x y", "window.close()" })
            Assert.Null(SceneFormula.Parse(SceneFormula.Tidy(bad), known));
    }

    [Fact]
    public void A_formula_with_numbers_the_passage_never_gives_is_made_up()
    {
        const string passage = "The thinking distance equals speed × reaction time. A typical reaction time is 0.7 seconds, and kinetic energy is ½mv².";
        Assert.True(StudySceneReader.FromPassage("speed * reaction", passage));
        Assert.True(StudySceneReader.FromPassage("speed * 0.7", passage));
        Assert.True(StudySceneReader.FromPassage("m * v^2", passage));
        Assert.False(StudySceneReader.FromPassage("a * 2 + 5", passage));
        Assert.False(StudySceneReader.FromPassage("speed", passage));
        // Read with its passage, a simulation whose rule is made up isn't one.
        const string reply = "SIMULATE: Heat\nVAR: a | acid | g | 1 | 10 | 5\nRESULT: t | temperature | °C | a * 2 + 5";
        Assert.Single(StudySceneReader.Read(reply, "Page 1"));
        Assert.Empty(StudySceneReader.Read(reply, "Page 1", "Acids release hydrogen ions; the mixture becomes warm."));
    }

    [Fact]
    public void A_system_names_each_part_once_and_reads_its_pushes()
    {
        var system = Of(StudySceneKind.System);
        Assert.Equal(["Sun's warmth", "Water vapour", "Clouds", "Heat trapped", "Sunlight reaching the ground", "Forests"], system.Parts);
        Assert.Equal(6, system.Links.Count);
        var clouds = system.Links.Single(l => system.Parts[l.From] == "Clouds");
        Assert.False(clouds.Raises);
        Assert.Equal("Clouds reflect sunlight back to space.", clouds.Why);
        // Written without cells, or with the verb in other words.
        var plain = Assert.Single(StudySceneReader.Read("""
            SYSTEM: A market
            - Price LOWERS Demand: people buy less when it costs more
            - Demand | increases | Price | sellers ask more
            - Supply | LOWERS | Price | more for sale brings the price down
            """, "Page 1"));
        Assert.Equal(3, plain.Parts.Count);
        Assert.Equal([false, true, false], plain.Links.Select(l => l.Raises));
    }

    [Fact]
    public void Situations_under_repeated_headings_are_one_scenario()
    {
        var scenario = Of(StudySceneKind.Scenario);
        Assert.Equal(2, scenario.Stages.Count);
        Assert.Equal([1, -1, -1], scenario.Stages[1].Choices.Select(c => c.Grade));
        // A situation with no good choice teaches nothing.
        Assert.Empty(StudySceneReader.Read("SCENARIO: X\nSITUATION: A fire starts in the kitchen.\n- Run | BAD | no\n- Shout | BAD | no", "Page 1"));
    }

    [Fact]
    public void Numbers_to_guess_are_numbers_with_their_unit()
    {
        var estimate = Of(StudySceneKind.Estimate);
        // "8 minutes and 20 seconds" isn't one number.
        Assert.Equal(2, estimate.Guesses.Count);
        var moon = estimate.Guesses[0];
        Assert.Equal((384000.0, "km", 0.0, 1000000.0), (moon.Answer, moon.Unit, moon.Lowest, moon.Highest));
        var arabic = Assert.Single(StudySceneReader.Read(StudyFileTests.ScenesReplyArabic, "صفحة 4"), s => s.Kind == StudySceneKind.Estimate);
        Assert.Equal(10, arabic.Guesses[1].Answer);
        // A range that doesn't hold the answer is made around it.
        var odd = Assert.Single(StudySceneReader.Read("ESTIMATE: Heat\n- How hot is Venus? | 465 | °C | 0 | 100 | Venus is the hottest planet.", "Page 1"));
        Assert.True(odd.Guesses[0].Lowest < 465 && odd.Guesses[0].Highest > 465);
    }

    [Fact]
    public void Titles_copied_from_the_instruction_are_none()
    {
        var scene = Assert.Single(StudySceneReader.Read("SCENARIO: the situation\nSITUATION: Someone faints in class.\n- Call for help | GOOD | help comes\n- Walk away | BAD | no one helps", "Page 1"));
        Assert.Equal("", scene.Title);
        Assert.Equal("", Assert.Single(StudySceneReader.Read("ESTIMATE: <title>\n- How many legs has a spider? | 8 | legs | 0 | 20 | Spiders have eight legs.", "Page 1")).Title);
    }

    [Fact]
    public void Each_purpose_has_its_own_voice_and_only_study_asks_for_cards_and_questions()
    {
        var study = StudyFile.Instruction("en", "en", StudyAmount.Normal);
        Assert.StartsWith("You are a patient, experienced teacher", study);
        Assert.Equal(study, StudyFile.Instruction("en", "en", StudyAmount.Normal, "", StudyPurpose.Study));
        foreach (var purpose in Enum.GetValues<StudyPurpose>().Where(p => p != StudyPurpose.Study))
        {
            var told = StudyFile.Instruction("ar", "en", StudyAmount.Normal, "", purpose);
            Assert.DoesNotContain("patient, experienced teacher", told);
            Assert.Contains("Write everything in Arabic", told);
            Assert.Contains("TITLE:", told);
            Assert.Equal(StudyPurposes.Asks(purpose), told.Contains("QUIZ:", StringComparison.Ordinal));
            Assert.Equal(StudyPurposes.Asks(purpose), told.Contains("CARDS:", StringComparison.Ordinal));
        }
        Assert.Contains("children of about 8 to 12", StudyFile.Instruction("en", "en", StudyAmount.More, "", StudyPurpose.Children));
        Assert.Contains("up to 1 QUIZ question.", StudyFile.Instruction("en", "en", StudyAmount.More, "", StudyPurpose.Children));
        // The diagrams for study are asked for as they always were; for another purpose, for its reader.
        Assert.DoesNotContain("The page", StudyExtrasReader.Instruction("en", "en", StudyActivities.Some, ""));
        Assert.Contains("The page is shown to an audience", StudyExtrasReader.Instruction("en", "en", StudyActivities.Some, "", StudyPurpose.Present));
        var scenes = StudySceneReader.Instruction("tr", "en", StudyScenes.Many, "Make it fun", StudyPurpose.Explore);
        Assert.Contains("in Turkish", scenes);
        Assert.Contains("«Make it fun»", scenes);
        Assert.Contains("give 1 to 3 scenes", scenes);
    }

    [Fact]
    public async Task Each_passage_gets_its_scenes_asked_once_and_kept()
    {
        StudyFile.CacheFolder = Path.Combine(_dir, "cache");
        int asked = 0;
        StudyFile.TestWriter = (instruction, given, _) =>
        {
            if (instruction.StartsWith("You design hands-on scenes", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref asked);
                Assert.Contains("The page is for a curious reader", instruction);
                return Task.FromResult(StudyFileTests.ScenesReply);
            }
            if (instruction.StartsWith("You are an experienced teacher", StringComparison.Ordinal))
                return Task.FromResult("NONE");
            return Task.FromResult(instruction.StartsWith("You make subjects fun", StringComparison.Ordinal) ? StudyFileTests.Answer(given) : StudyFileTests.Answer("## outline"));
        };
        var sections = new List<DocumentSection> { new("Chapter 1", "1.1 Acids", [StudyFileTests.Page(3, "Acids release hydrogen ions in water. " + new string('a', 1200))]) };
        var plan = StudyFile.Plan(new DocumentReading(sections.SelectMany(s => s.Places).ToList(), sections, "Science"), cloud: false);
        var chosen = plan.Parts.Select(p => p.Number).ToHashSet();
        var options = new StudyFile.Options("en", StudyAmount.Normal, "en", "", StudyActivities.Some, StudyPurpose.Explore, StudyScenes.Many);
        var book = await StudyFile.MakeAsync(plan, chosen, options, "Science", "science.pdf", (a, b) => $"Page {a.Number}");
        Assert.Equal(StudyPurpose.Explore, book.Purpose);
        Assert.Equal(5, Assert.Single(book.Parts).Scenes.Count);
        Assert.Equal(1, asked);
        Assert.Equal(1, StudyFile.Made(plan, chosen, options));
        await StudyFile.MakeAsync(plan, chosen, options, "Science", "science.pdf", (a, b) => $"Page {a.Number}");
        Assert.Equal(1, asked);
        // Without scenes they aren't asked for.
        var none = await StudyFile.MakeAsync(plan, chosen, options with { Scenes = StudyScenes.None }, "Science", "science.pdf", (a, b) => $"Page {a.Number}");
        Assert.Empty(none.Parts[0].Scenes);
        Assert.Equal(1, asked);
    }

    [Fact]
    public void The_page_draws_its_scenes_and_has_no_study_parts_when_it_is_for_something_else()
    {
        foreach (var language in new[] { "en", "ar" })
        {
            var sample = StudyFileTests.Sample(language);
            var parts = sample.Parts.ToList();
            parts[1] = parts[1] with { Scenes = StudySceneReader.Read(language == "ar" ? StudyFileTests.ScenesReplyArabic : StudyFileTests.ScenesReply, "Page 4") };
            var studying = StudyFileWriter.Html(sample with { Parts = parts }, StudyFileWindow.Words(language));
            var exploring = StudyFileWriter.Html(sample with { Parts = parts, Purpose = StudyPurpose.Explore }, StudyFileWindow.Words(language));

            foreach (var html in new[] { studying, exploring })
            {
                Assert.Equal(5, Regex.Matches(html, "<div class=\"scene\" id=\"s[0-9a-f]{10}\"").Count);
                // Without the script: what each mix does, the formula, what pushes what, the choices, the answers.
                Assert.Contains("<dl class=\"mixes\">", html);
                Assert.Contains("<code dir=\"ltr\">thinking = speed * reaction</code>", html);
                Assert.Contains("<ul class=\"links\">", html);
                Assert.Contains("<ol class=\"stages\">", html);
                Assert.Contains("<ol class=\"guesses\">", html);
                Assert.Contains("href=\"#play\"", html);
                var data = JsonDocument.Parse(Regex.Match(html, "<script type=\"application/json\" id=\"data\">(.*?)</script>", RegexOptions.Singleline).Groups[1].Value).RootElement;
                Assert.Equal(5, data.GetProperty("scenes").GetArrayLength());
                Assert.Equal(["lab", "sim", "sys", "sce", "est"], data.GetProperty("scenes").EnumerateArray().Select(s => s.GetProperty("k").GetString()));
            }
            Assert.Contains("class=\"meter", studying);
            Assert.Contains("class=\"btn mark\"", studying);
            Assert.Contains("id=\"progress\"", studying);
            Assert.DoesNotContain("class=\"meter", exploring);
            Assert.DoesNotContain("class=\"btn mark\"", exploring);
            Assert.DoesNotContain("id=\"progress\"", exploring);
            Assert.DoesNotContain("class=\"how", exploring);
            Assert.Contains("<body class=\"nojs free\"", exploring);
        }
        // Every word the scenes use is in the page's words.
        var script = File.ReadAllText(Path.Combine(StudyFileTests.RecipesFolder, "StudyFile.js"));
        foreach (Match m in Regex.Matches(script, @"\bW\.(\w+)"))
            Assert.Contains(m.Groups[1].Value, StudyFileWords.Keys);
    }
}

