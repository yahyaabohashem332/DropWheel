using System.Text.Json;
using Petal.Core.Models;
using Petal.Engines.Media;
using Petal.Recipes.Translation;
using Xunit;
using Xunit.Abstractions;

namespace Petal.Tests;

public class DubTranslationTests(ITestOutputHelper output)
{
    [Fact]
    public void The_numbered_lines_of_a_reply_are_read_and_the_rest_left()
    {
        var read = DubTranslation.Parse("1. Hello there.\n2) [4] How are you today?\nNote: none\n3. \"Fine.\"\n2. Again\n9. Too far", 3);
        Assert.Equal("Hello there.", read[1]);
        Assert.Equal("How are you today?", read[2]);
        Assert.Equal("Fine.", read[3]);
        Assert.Equal(3, read.Count);
    }

    [Fact]
    public void A_line_gets_about_as_many_words_as_its_time_allows()
    {
        var line = new DubLine(0, 4, "one two three four five six seven eight nine ten", "");
        // English into Arabic: 4 s at 2.1 words a second, and ten words at 0.8: about 8.
        Assert.Equal(8, DubTranslation.Target(line, "en", "ar"));
        // Arabic into English: 4 s at 2.6, and ten words at 1.25: about 11.
        Assert.Equal(11, DubTranslation.Target(line, "ar", "en"));
        Assert.Equal(1, DubTranslation.Target(new DubLine(0, 0.2, "نعم", ""), "ar", "en"));
    }

    [Fact]
    public void A_translation_in_the_wrong_language_or_far_too_long_is_not_kept()
    {
        var line = new DubLine(0, 2, "قال لا", "");
        Assert.True(DubTranslation.Fits("He said no.", line, "ar", "en"));
        Assert.False(DubTranslation.Fits("قال لا", line, "ar", "en"));
        Assert.False(DubTranslation.Fits(string.Join(' ', Enumerable.Repeat("word", 40)), line, "ar", "en"));
        Assert.True(DubTranslation.Fits("قال لا", new DubLine(0, 2, "He said no", ""), "en", "ar"));
    }

    [Fact]
    public void The_model_sees_the_lines_before_and_the_words_each_line_should_have()
    {
        var lines = new[] { new DubLine(0, 2, "قال لا", ""), new DubLine(2, 6, "قم فصل ركعتين", "") };
        var prompt = DubTranslation.Prompt("A religious lesson.", [("دخل رجل", "A man came in")], lines, "ar", "en");
        Assert.Contains("About the video", prompt);
        Assert.Contains("- دخل رجل => A man came in", prompt);
        Assert.Contains("1. [", prompt);
        Assert.Contains("2. [", prompt);
        Assert.Contains("Arabic into English", DubTranslation.Instruction("ar", "en"));
        Assert.Contains("Modern Standard Arabic", DubTranslation.Instruction("en", "ar"));
        Assert.Contains("never move words or meaning from one line into another", DubTranslation.Instruction("ar", "en"));
    }

    [Fact]
    public void A_line_that_says_again_what_another_says_is_found()
    {
        // The sermon's lines 11 and 14 as Gemma 4 12B once gave them: the later took the earlier's words.
        var lines = new[]
        {
            new DubLine(0, 9, "نهى عليه الصلاة والسلام أن يصلي الرجل وليس على عاتقيه شيء", ""),
            new DubLine(9, 10, "في نهي عمد قلة", ""),
            new DubLine(10, 19, "وليس على عاتقي شيء نرى بعض المحرمين في مكة يطبع طلع الحرام كتف", ""),
        };
        string?[] result =
        [
            "He forbade praying with nothing on one's shoulders. We see some people in Mecca wearing a garment over one shoulder, which is wrong.",
            "It was forbidden for few.",
            "...with nothing on his shoulders. We see some people in Mecca wearing a garment over one shoulder, which is wrong.",
        ];
        Assert.Equal([2], DubTranslation.Repeated(result, 0, 3, lines));
        // Two lines that say the same because their originals do are left as they are.
        var same = new[] { new DubLine(0, 2, "صلوا ركعتين قبل الجلوس", ""), new DubLine(2, 4, "صلوا ركعتين قبل الجلوس", "") };
        Assert.Empty(DubTranslation.Repeated(["Pray two rak'ahs before sitting.", "Pray two rak'ahs before sitting."], 0, 2, same));
        // Only the batch's own lines are asked again, though the line just before it is compared too.
        Assert.Equal([1], DubTranslation.Repeated(result, 1, 2, lines));
    }

    /// <summary>
    /// With PETAL_ASK (the models: Qwen3 4B and llama.cpp) and PETAL_DUB_LINES (a dub's lines as the studio keeps them):
    /// every line translated into the other language, written next to what was there before, for reading.
    /// </summary>
    [Fact]
    public async Task Real_lines_are_translated_by_meaning()
    {
        var folder = Environment.GetEnvironmentVariable("PETAL_ASK");
        var file = Environment.GetEnvironmentVariable("PETAL_DUB_LINES");
        if (folder is null || file is null)
        {
            output.WriteLine("Skipped: PETAL_ASK or PETAL_DUB_LINES isn't set.");
            return;
        }
        var saved = ModelStore.Folder;
        var writer = DubTranslation.Writer;
        ModelStore.Folder = folder;
        // PETAL_DUB_MODEL names another model's file in the folder to translate with.
        if (Environment.GetEnvironmentVariable("PETAL_DUB_MODEL") is { Length: > 0 } other)
            DubTranslation.Writer = new ModelInfo("test-writer", other, other, "", "", new FileInfo(Path.Combine(folder, other)).Length, "");
        try
        {
            using var json = JsonDocument.Parse(File.ReadAllText(file));
            string from = json.RootElement.GetProperty("Language").GetString()!;
            string into = from == "ar" ? "en" : "ar";
            var lines = json.RootElement.GetProperty("Lines").EnumerateArray()
                .Select(l => new DubLine(l.GetProperty("Start").GetDouble(), l.GetProperty("End").GetDouble(), l.GetProperty("Original").GetString()!, l.GetProperty("Translation").GetString()!))
                .ToList();
            int limit = int.TryParse(Environment.GetEnvironmentVariable("PETAL_DUB_LIMIT"), out var n) ? n : lines.Count;
            lines = lines.Take(limit).ToList();
            var clock = System.Diagnostics.Stopwatch.StartNew();
            int fellBack = 0;
            var translated = await DubTranslation.TranslateAsync(lines, from, into, fallback: missing =>
            {
                fellBack += missing.Count;
                return missing.Select(_ => "(none)").ToList();
            });
            output.WriteLine($"{lines.Count} lines in {clock.Elapsed.TotalSeconds:0} s; {fellBack} left to the word-for-word model.");
            for (int i = 0; i < lines.Count; i++)
                output.WriteLine($"{lines[i].Length,4:0.0}s  {lines[i].Original}\n      before: {lines[i].Translation}\n      now:    {translated[i]}");
            Assert.Equal(lines.Count, translated.Count);
            Assert.True(fellBack <= lines.Count / 5, $"{fellBack} lines fell back");
        }
        finally
        {
            ModelStore.Folder = saved;
            DubTranslation.Writer = writer;
        }
    }
}
