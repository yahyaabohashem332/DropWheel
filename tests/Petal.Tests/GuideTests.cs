using Petal.App.Guide;
using Petal.App.Overlay;
using Petal.App.Wheels;
using Petal.Core.Actions;
using Petal.Core.Tools;

namespace Petal.Tests;

/// <summary>
/// The Tools Guide: an entry for every tool, file action and action of the wheels without files (and for
/// nothing that isn't there), whole in every language; requests in plain words find the right tool; and the
/// AI model answers from the guide only.
/// </summary>
public sealed class GuideTests
{
    private static readonly string[] Languages = ["ar", "tr", "fa", "ur", "id", "fr", "es"];

    [Fact]
    public void Every_tool_and_action_has_an_entry_and_every_entry_is_about_something_there()
    {
        var keys = GuideBook.Table("en").Keys.ToHashSet();
        var wanted = ToolRegistry.All.Select(t => "tool:" + t.Id)
            .Concat(FileActionRegistry.All.Select(a => "action:" + a.Id))
            .Concat(WheelActions.All.Select(a => "do:" + a.Id))
            .ToList();
        Assert.DoesNotContain(wanted, k => !keys.Contains(k));
        var there = wanted.ToHashSet();
        Assert.DoesNotContain(keys, k => !there.Contains(k) && !k.StartsWith("kind:", StringComparison.Ordinal));
        // What a petal says in the centre names a real entry.
        foreach (var key in keys)
            Assert.False(string.IsNullOrWhiteSpace(GuideBook.Name(key)), key);
    }

    [Fact]
    public void English_entries_say_what_how_and_three_examples()
    {
        foreach (var (key, e) in GuideBook.Table("en"))
        {
            Assert.Contains(e.Category, GuideBook.Categories);
            Assert.False(string.IsNullOrWhiteSpace(e.Summary), key);
            Assert.False(string.IsNullOrWhiteSpace(e.Details), key);
            Assert.False(string.IsNullOrWhiteSpace(e.How), key);
            Assert.Equal(3, e.Examples.Count);
            Assert.InRange(e.Keywords.Count, 6, 16);
            Assert.DoesNotContain("angerine", e.Summary + e.Details + e.How + string.Join(' ', e.Examples));
        }
    }

    [Fact]
    public void Every_language_has_the_whole_guide()
    {
        var english = GuideBook.Table("en");
        foreach (var code in Languages)
        {
            var table = GuideBook.Table(code);
            Assert.True(table.Count > 0, $"No guide in {code}");
            foreach (var (key, e) in english)
            {
                Assert.True(table.TryGetValue(key, out var t), $"{code}: {key}");
                Assert.False(string.IsNullOrWhiteSpace(t!.Summary), $"{code}: {key} summary");
                Assert.False(string.IsNullOrWhiteSpace(t.Details), $"{code}: {key} details");
                Assert.False(string.IsNullOrWhiteSpace(t.How), $"{code}: {key} how");
                Assert.Equal(e.Examples.Count, t.Examples.Count);
                Assert.True(t.Keywords.Count >= 5, $"{code}: {key} keywords");
                Assert.True(e.WorksOn.Length == 0 || t.WorksOn.Length > 0, $"{code}: {key} worksOn");
                // Translated, not copied: a long English summary wouldn't read the same in another language.
                Assert.True(t.Summary != e.Summary || e.Summary.Length < 30, $"{code}: {key} summary is English");
            }
            Assert.DoesNotContain(table.Keys, k => !english.ContainsKey(k));
        }
    }

    [Theory]
    [InlineData("countdown", "do:draw.timer")]
    [InlineData("I want a tool that counts down 10 minutes", "do:draw.timer")]
    [InlineData("make a pdf smaller", "tool:Compress")]
    [InlineData("remove the background of a photo", "tool:RemoveBackground")]
    [InlineData("send files to my phone", "action:Phone")]
    [InlineData("typed with the wrong keyboard language", "do:text.fixlayout")]
    [InlineData("turn a video into a tiktok", "tool:Reframe")]
    [InlineData("subtitles for a video", "tool:Transcribe")]
    [InlineData("how do I make a video smaller for whatsapp", "tool:Compress")]
    [InlineData("I want to put my logo on photos", "tool:Watermark")]
    [InlineData("join two pdf files", "tool:MergePdfs")]
    [InlineData("remove the ums from my video", "tool:EditByText")]
    [InlineData("ask questions about a pdf", "tool:AskFile")]
    [InlineData("zoom in on clicks in my screen recording", "tool:RecordingStudio")]
    public void Plain_words_find_the_tool(string request, string key)
    {
        var hits = GuideSearch.Find(request, 3).Select(h => h.Key).ToList();
        Assert.True(hits.Contains(key), string.Join(", ", GuideSearch.Find(request, 6).Select(h => $"{h.Key} {h.Score:0.0}")));
    }

    [Theory]
    [InlineData("أريد أداة تقوم بمهمة العداد", "do:draw.timer")]
    [InlineData("مؤقت", "do:draw.timer")]
    [InlineData("إزالة خلفية الصورة", "tool:RemoveBackground")]
    [InlineData("تصغير حجم ملف pdf", "tool:Compress")]
    [InlineData("كتبت بلغة لوحة المفاتيح الخطأ", "do:text.fixlayout")]
    [InlineData("حذف كلمات الحشو من الفيديو", "tool:EditByText")]
    [InlineData("أسئلة عن ملف pdf", "tool:AskFile")]
    [InlineData("تكبير تلقائي عند النقر في تسجيل الشاشة", "tool:RecordingStudio")]
    public void Arabic_words_find_the_tool_whatever_language_Petal_speaks(string request, string key)
    {
        var hits = GuideSearch.Find(request, 3).Select(h => h.Key).ToList();
        Assert.Contains(key, hits);
    }

    [Fact]
    public void Little_words_alone_find_nothing()
    {
        Assert.Empty(GuideSearch.Find("I want a tool"));
        Assert.Empty(GuideSearch.Find("أريد أداة"));
    }

    [Fact]
    public void Petals_name_their_entries()
    {
        Assert.Equal("tool:Compress", GuideBook.KeyFor(WheelItem.From(ToolRegistry.Get(ToolId.Compress))));
        Assert.Equal("action:Zip", GuideBook.KeyFor(WheelItem.From(FileActionRegistry.All.First(a => a.Id == FileActionId.Zip))));
        Assert.Equal("do:draw.timer", GuideBook.KeyFor(Rings.Item(new WheelEntry { Kind = EntryKind.Action, Action = "draw.timer" })));
        Assert.Equal("kind:app", GuideBook.KeyFor(Rings.Item(new WheelEntry { Kind = EntryKind.App, Path = @"C:\Windows\notepad.exe" })));
        Assert.Equal("kind:format", GuideBook.KeyFor(WheelItem.From(Petal.Core.Formats.Formats.Webp)));
        // A petal the guide has nothing on: no mark.
        Assert.Null(GuideBook.KeyFor(new WheelItem("?", "?", null, "something else")));
    }

    [Fact]
    public void The_models_choice_keeps_only_notes_that_were_given()
    {
        string[] notes = ["tool:Compress", "do:draw.timer", "tool:Watermark"];
        Assert.Equal(["do:draw.timer", "tool:Compress"], GuideAnswer.Numbers("2, 1", notes));
        Assert.Empty(GuideAnswer.Numbers("0", notes));
        // A number of no note, and the same one twice, are left out.
        Assert.Equal(["tool:Watermark"], GuideAnswer.Numbers("7, 3, 3", notes));
    }

    [Theory]
    [InlineData("I need to count down 10 minutes for a quiz", "do:draw.timer")]
    [InlineData("أريد أداة تقوم بمهمة العداد", "do:draw.timer")]
    [InlineData("how do I make a video smaller for whatsapp", "tool:Compress")]
    public async Task The_AI_model_answers_from_the_guide_when_it_is_on_this_PC(string request, string wanted)
    {
        if (!Petal.Recipes.AskPetal.ModelReady)
            return;
        var reply = await GuideAnswer.FindAsync(request, GuideSearch.Find(request), CancellationToken.None);
        Assert.True(reply.Found, reply.Text);
        Assert.True(reply.Cited.Contains(wanted), reply.Text + " | " + string.Join(", ", reply.Cited));
    }
}
