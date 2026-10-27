using System.IO;
using Petal.Engines.Media;
using Petal.Recipes;
using Petal.Recipes.Translation;

namespace Petal.Tests;

/// <summary>Make Shorts: what was heard kept between runs, the model's titles cleaned, captions and titles in the language wanted, and file names.</summary>
public sealed class ShortsMakerTests
{
    [Fact]
    public void What_was_heard_is_kept_and_read_back_whole()
    {
        var folder = Path.Combine(Path.GetTempPath(), "petal-shorts-cache-" + Guid.NewGuid().ToString("N")[..8]);
        var video = Path.Combine(folder, "talk.mp4");
        var before = ShortsMaker.CacheFolder;
        try
        {
            Directory.CreateDirectory(folder);
            File.WriteAllBytes(video, [1, 2, 3]);
            ShortsMaker.CacheFolder = Path.Combine(folder, "cache");
            var levels = new SoundLevels([-60f, -20.5f, -21.25f, -59.9f]);
            var segment = new TranscriptSegment(TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(1.7), "مرحبا بكم.")
            {
                Words = [new SpokenWord("مرحبا", 0.5, 0.9), new SpokenWord("بكم.", 1.0, 1.7)],
            };
            var key = ShortsMaker.CacheKey(video, @"C:\models\ggml-small-q5_1.bin", "auto");
            Assert.False(ShortsMaker.IsKnown(video, @"C:\models\ggml-small-q5_1.bin", "auto"));
            ShortsMaker.Write(key, new ShortsSource(video, "ar", [segment], levels));
            Assert.True(ShortsMaker.IsKnown(video, @"C:\models\ggml-small-q5_1.bin", "auto"));
            // Another model or language is another hearing.
            Assert.False(ShortsMaker.IsKnown(video, @"C:\models\ggml-base-q5_1.bin", "auto"));
            Assert.False(ShortsMaker.IsKnown(video, @"C:\models\ggml-small-q5_1.bin", "ar"));

            var read = ShortsMaker.Read(key, video)!;
            Assert.Equal("ar", read.Language);
            var words = Assert.Single(read.Segments).Words;
            Assert.Equal(["مرحبا", "بكم."], words.Select(w => w.Text).ToArray());
            Assert.Equal(1.0, words[1].Start, 3);
            Assert.Equal(4, read.Levels.Count);
            Assert.Equal(-20.5, read.Levels.Values[1], 2);
            Assert.True(read.HasSpeech);

            // A changed file is heard again.
            File.WriteAllBytes(video, [1, 2, 3, 4]);
            Assert.False(ShortsMaker.IsKnown(video, @"C:\models\ggml-small-q5_1.bin", "auto"));
        }
        finally
        {
            ShortsMaker.CacheFolder = before;
            try { Directory.Delete(folder, recursive: true); } catch (IOException) { }
        }
    }

    [Theory]
    [InlineData("\"Why Space Food Tastes Bland\"", "Why Space Food Tastes Bland")]
    [InlineData("Title: The truth about space sickness.", "The truth about space sickness")]
    [InlineData("«لماذا يفقد رواد الفضاء حاسة التذوق؟»", "لماذا يفقد رواد الفضاء حاسة التذوق؟")]
    [InlineData("A good title\nAnd an explanation of why it is good", "A good title")]
    [InlineData("Space #astronaut #nasa", "")]
    [InlineData("One", "")]
    [InlineData("This title goes on and on and on for far more words than any headline should", "")]
    public void The_models_title_is_cleaned_or_left_out(string written, string kept) => Assert.Equal(kept, ShortsMaker.Clean(written));

    [Fact]
    public async Task Words_as_spoken_keep_their_own_times_and_titles_stay_as_they_are()
    {
        var words = new List<SpokenWord> { new("Can", 10, 10.2), new("you", 10.25, 10.4), new("fly?", 10.45, 10.9) };
        var moment = new Moment(9.9, 11, 80, [MomentReason.Question], words);
        var source = new ShortsSource("talk.mp4", "en", [], new SoundLevels([]));
        var captions = await ShortsMaker.CaptionsAsync(source, moment, SpeechInto.Spoken);
        Assert.Equal(["Can", "you", "fly?"], captions.Select(c => c.Text).ToArray());
        Assert.Equal(10.25, captions[1].Start.TotalSeconds, 3);
        // English wanted from English: the same words.
        Assert.Equal(3, (await ShortsMaker.CaptionsAsync(source, moment, SpeechInto.English)).Count);
        Assert.Equal("Can you fly?", await ShortsMaker.TitleInAsync("Can you fly?", "en", SpeechInto.Spoken));
        Assert.Equal("Can you fly?", await ShortsMaker.TitleInAsync("Can you fly?", "en", SpeechInto.English));
    }

    [Theory]
    [InlineData(1, "Can you take me to space?", "01 Can you take me to space")]
    [InlineData(12, "Mission: \"Earth\" / Moon", "12 Mission Earth  Moon")]
    [InlineData(3, "هل يمكنك أن تأخذني إلى الفضاء؟", "03 هل يمكنك أن تأخذني إلى الفضاء؟")]
    [InlineData(4, "", "04")]
    public void Each_short_is_numbered_and_titled_as_a_file_name_allows(int number, string title, string name) =>
        Assert.Equal(name, Petal.App.Tools.Windows.MakeShortsWindow.FileName(number, title));

    [Fact]
    public void A_long_title_is_cut_for_its_file_name()
    {
        var name = Petal.App.Tools.Windows.MakeShortsWindow.FileName(2, new string('a', 100));
        Assert.Equal(63, name.Length);
    }
}
