using System.IO;
using System.Net;
using System.Text.RegularExpressions;
using Petal.App.Tools.Windows;
using Petal.Engines.Documents;
using Petal.Recipes.Study;

namespace Petal.Tests;

/// <summary>
/// Scenes the model designs and writes for a part, each in a sandboxed frame; and for the exam: signal words, traps,
/// sentences to write, the file's own words beside the explanation, and my mistakes.
/// </summary>
[Collection("Language")]
public sealed class StudyDesignedTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "petal-designed-" + Guid.NewGuid().ToString("N"));
    private readonly string _cache = StudyFile.CacheFolder;

    public StudyDesignedTests() => Directory.CreateDirectory(_dir);

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

    private static string Scene(string body) => "SCENE: A test\nIDEA: It shows something.\n<html>\n" + body + "\n</html>";

    private const string Good = """
        <div class="t"><p>Press the button.</p><button type="button">Go</button><p class="say"></p></div>
        <style>.t button { padding: 8px; }</style>
        <script>
        (function () {
          'use strict';
          var parent = document.querySelector('.t'), say = parent.querySelector('.say');
          // A comment with a bracket ( in it, and a string with one: ")".
          parent.querySelector('button').addEventListener('click', function () { say.textContent = 'Done: ' + [1, 2].join(')'); });
        })();
        </script>
        """;

    [Fact]
    public void Scenes_are_read_whole_with_their_title_and_idea()
    {
        var scenes = StudyDesigner.Read(StudyDesignedSamples.Reply, "Page 4");
        Assert.Equal(["Give birth to a star", "Two boxes, the same work?"], scenes.Select(s => s.Title));
        Assert.StartsWith("Gravity pulls", scenes[0].Idea);
        Assert.Single(scenes[0].Scripts);
        Assert.Contains("requestAnimationFrame", scenes[0].Scripts[0]);
        Assert.DoesNotContain("<html>", scenes[0].Html);
        // Fenced as code, or with a page around it, it is the same scene.
        Assert.Single(StudyDesigner.Read("SCENE: A test\nIDEA: It shows something.\n```html\n" + Good + "\n```", "Page 1"));
        var whole = Assert.Single(StudyDesigner.Read(Scene("<!doctype html><html><head></head><body>" + Good + "</body></html>"), "Page 1"));
        Assert.DoesNotContain("<body", whole.Html);
    }

    [Theory]
    [InlineData("<img src=\"https://example.com/a.png\">")]
    [InlineData("<script>fetch('/x');</script>")]
    [InlineData("<script>localStorage.setItem('a', 1);</script>")]
    [InlineData("<button onclick=\"go()\">Go</button>")]
    [InlineData("<script src=\"lib.js\"></script>")]
    [InlineData("<iframe></iframe>")]
    [InlineData("<script>new Function('return 1')();</script>")]
    [InlineData("<style>@import 'x.css';</style>")]
    public void A_scene_that_reaches_out_or_runs_outside_its_script_is_refused(string bad)
    {
        Assert.Single(StudyDesigner.Read(Scene(Good), "Page 1"));
        Assert.Empty(StudyDesigner.Read(Scene(Good + "\n" + bad), "Page 1"));
    }

    [Fact]
    public void A_scene_cut_off_in_the_middle_is_refused()
    {
        var cut = Good[..Good.IndexOf("say.textContent", StringComparison.Ordinal)] + "</script>";
        Assert.Empty(StudyDesigner.Read(Scene(cut), "Page 1"));
        Assert.False(StudyDesigner.Balanced("(function () { if (a) { b(); }"));
        Assert.True(StudyDesigner.Balanced("var s = '}'; /* ( */ var t = \"[\"; // {\nf([1, 2]);"));
        Assert.Empty(StudyDesigner.Read("SCENE: A test\nIDEA: Short.\n<html><script>x()</script></html>", "Page 1"));
    }

    [Fact]
    public void The_model_is_told_to_make_a_scene_of_its_own_in_the_page_s_language_and_rules()
    {
        var told = StudyDesigner.Instruction("ar", "tr", StudyScenes.Many, "Make it fun", StudyPurpose.Study);
        Assert.Contains("Make 2 scenes", told);
        Assert.Contains("in Arabic", told);
        Assert.Contains("The part is in Turkish", told);
        Assert.Contains("right to left", told);
        Assert.Contains("«Make it fun»", told);
        Assert.Contains("addEventListener", told);
        Assert.Contains("Make 1 scene,", StudyDesigner.Instruction("en", "en", StudyScenes.Some, "", StudyPurpose.Explore));
    }

    [Fact]
    public void The_page_runs_each_scene_sandboxed_and_lets_in_only_its_scripts()
    {
        var sample = StudyFileTests.Sample("en");
        var parts = sample.Parts.ToList();
        var designed = StudyDesigner.Read(StudyDesignedSamples.Reply, "Page 4");
        parts[1] = parts[1] with { Designed = designed };
        var html = StudyFileWriter.Html(sample with { Parts = parts }, StudyFileWindow.Words("en"));
        var policy = Regex.Match(html, "Content-Security-Policy\" content=\"([^\"]*)\"").Groups[1].Value;
        Assert.Contains("default-src 'none'", policy);
        Assert.Contains(StudyDesigner.Hash(StudyDesigner.Bridge), policy);
        foreach (var script in designed.SelectMany(d => d.Scripts))
            Assert.Contains(StudyDesigner.Hash(script), policy);
        var frames = Regex.Matches(html, "<iframe class=\"designed-frame\" sandbox=\"([^\"]*)\"[^>]*srcdoc=\"([^\"]*)\"");
        Assert.Equal(2, frames.Count);
        foreach (Match frame in frames)
        {
            // Scripts only: nothing of the page's origin, no forms, no pop-ups, no way out.
            Assert.Equal("allow-scripts", frame.Groups[1].Value);
            var document = WebUtility.HtmlDecode(frame.Groups[2].Value);
            Assert.StartsWith("<!doctype html><html lang=\"en\" dir=\"ltr\">", document);
            Assert.Contains("<script>" + StudyDesigner.Bridge + "</script>", document);
        }
        Assert.Contains("id=\"d2-1\"", html);
        Assert.Contains("href=\"#d2-2\"", html);
        // Without them the page lets in only its own script.
        var plain = StudyFileWriter.Html(sample, StudyFileWindow.Words("en"));
        Assert.Matches("script-src 'sha256-[^']+'; base-uri", plain);
    }

    [Fact]
    public void The_exam_box_the_original_beside_and_my_mistakes_are_on_the_page()
    {
        var sample = StudyFileTests.Sample("ar");
        var parts = sample.Parts.ToList();
        parts[1] = parts[1] with
        {
            Signals = [("يمسك الخلية", "الغشاء")],
            Traps = [("غشاء أم جدار؟", "لكل خلية غشاء، والجدار للنبات فقط.")],
            Exam = ["الغشاء يحيط بالخلية ويتحكم فيما يدخلها ويخرج منها."],
            Passages = [.. parts[1].Passages.Select(x => x with { Source = "Hücre zarı hücreyi bir arada tutar." })],
        };
        var html = StudyFileWriter.Html(sample with { Parts = parts }, StudyFileWindow.Words("ar"));
        Assert.Contains("<section class=\"box examprep\">", html);
        Assert.Contains("<span class=\"said\">يمسك الخلية</span><span class=\"arrow\" aria-hidden=\"true\">←</span><strong>الغشاء</strong>", html);
        Assert.Contains("<ul class=\"traps\">", html);
        Assert.Contains("<ul class=\"write\">", html);
        Assert.Contains("class=\"link beside-switch needs-js\"", html);
        Assert.Contains("<div class=\"orig\" lang=\"", html);
        Assert.Contains("Hücre zarı hücreyi bir arada tutar", WebUtility.HtmlDecode(html));
        Assert.Contains("id=\"mistakes\"", html);
        Assert.Contains("href=\"#mistakes\"", html);
        // A part in the page's own language has nothing beside it.
        var same = StudyFileWriter.Html(StudyFileTests.Sample("ar"), StudyFileWindow.Words("ar"));
        Assert.DoesNotContain("class=\"link beside-switch", same);
    }

    [Fact]
    public void Signals_traps_and_sentences_for_the_exam_are_read_from_the_reply()
    {
        var reply = StudyFile.Read("""
            TITLE: Space vehicles
            IDEAS:
            - A probe has no people in it.
            EXPLANATION:
            A probe is a robot sent far away to take pictures and send data back to the Earth, with nobody on board.
            QUIZ:
            Q: What has no people?
            A) A probe
            B) A shuttle
            C) A station
            D) A rocket
            RIGHT: A
            WHY: A probe is unmanned.
            SIGNALS:
            - unmanned, robotic, sends data = a probe
            - goes there and back again, carries astronauts = a shuttle
            TRAPS:
            - Shuttle or probe? = People on board and it comes back: a shuttle; nobody on board and it goes far: a probe.
            EXAM:
            - A probe is an unmanned vehicle that explores planets and sends data to the Earth.
            """)!;
        Assert.Equal([("unmanned, robotic, sends data", "a probe"), ("goes there and back again, carries astronauts", "a shuttle")], reply.Signals);
        Assert.Equal("Shuttle or probe?", Assert.Single(reply.Traps).Which);
        Assert.Single(reply.Exam);
        Assert.Single(reply.Quiz);
        Assert.Contains("SIGNALS:", StudyFile.Instruction("en", "en", StudyAmount.Normal));
        Assert.Contains("TRAPS:", StudyFile.Instruction("en", "en", StudyAmount.Normal, "", StudyPurpose.Teach));
        Assert.DoesNotContain("SIGNALS:", StudyFile.Instruction("en", "en", StudyAmount.Normal, "", StudyPurpose.Explain));
    }

    [Fact]
    public async Task A_part_gets_its_scenes_asked_once_and_kept_and_its_exam_lines_said_once()
    {
        StudyFile.CacheFolder = Path.Combine(_dir, "cache");
        int designed = 0;
        StudyFile.TestWriter = (instruction, given, _) =>
        {
            if (instruction.StartsWith("You are a brilliant teacher", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref designed);
                Assert.Contains("The part's own words:", given);
                Assert.Contains("The part as it is explained on the page:", given);
                return Task.FromResult(StudyDesignedSamples.Reply);
            }
            if (instruction.StartsWith("You are a patient", StringComparison.Ordinal))
                return Task.FromResult(StudyFileTests.Answer(given) + "\nSIGNALS:\n- holds it together = the membrane\n- Holds it together! = the membrane\nEXAM:\n- The membrane holds the cell together and controls what goes in.\n- The membrane holds the cell together, and controls what goes in.");
            return Task.FromResult(instruction.StartsWith("You are an experienced teacher", StringComparison.Ordinal) ? "NONE" : StudyFileTests.Answer("## outline"));
        };
        var sections = new List<DocumentSection>
        {
            new("Chapter 1", "1.1 Membranes", [StudyFileTests.Page(3, "The membrane holds the cell together. " + new string('m', 1200))]),
        };
        var plan = StudyFile.Plan(new DocumentReading(sections.SelectMany(s => s.Places).ToList(), sections, "Cells"), cloud: false);
        var chosen = plan.Parts.Select(p => p.Number).ToHashSet();
        var options = new StudyFile.Options("en", StudyAmount.Normal, "en", "", StudyActivities.Some, StudyPurpose.Study, StudyScenes.Many, Designed: true);
        var book = await StudyFile.MakeAsync(plan, chosen, options, "Cells", "cells.pdf", (a, b) => $"Page {a.Number}");
        var part = Assert.Single(book.Parts);
        Assert.Equal(2, part.Designed.Count);
        Assert.Equal(1, designed);
        // The same said twice: said once.
        Assert.Single(part.Signals);
        Assert.Single(part.Exam);
        await StudyFile.MakeAsync(plan, chosen, options, "Cells", "cells.pdf", (a, b) => $"Page {a.Number}");
        Assert.Equal(1, designed);
        var without = await StudyFile.MakeAsync(plan, chosen, options with { Designed = false }, "Cells", "cells.pdf", (a, b) => $"Page {a.Number}");
        Assert.Empty(without.Parts[0].Designed);
        Assert.Equal(1, designed);
    }
}
