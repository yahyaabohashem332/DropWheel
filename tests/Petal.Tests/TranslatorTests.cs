using Petal.Core.Models;
using Petal.Recipes.Translation;
using Xunit.Abstractions;

namespace Petal.Tests;

/// <summary>With PETAL_OPUS pointing at a folder holding the Opus-MT files (as ModelStore names them).</summary>
[Collection("Models")]
public sealed class TranslatorTests(ITestOutputHelper output)
{
    private static string? Folder => Environment.GetEnvironmentVariable("PETAL_OPUS");

    private static T With<T>(Func<T> act)
    {
        var saved = ModelStore.Folder;
        ModelStore.Folder = Folder!;
        try
        {
            return act();
        }
        finally
        {
            ModelStore.Folder = saved;
        }
    }

    [Theory]
    // Reference ids from SentencePiece itself (Python) with the model's vocabulary, ">>ara<<" first and </s> last.
    [InlineData("Welcome back to the channel, everyone.", new long[] { 11320, 503, 9, 3, 17773, 5, 3458, 2 })]
    [InlineData("Don't worry if it looks sticky at first.", new long[] { 1095, 16, 55, 4845, 252, 44, 4368, 12703, 652, 60, 420, 2 })]
    [InlineData("Petal converts HEIC photos — 100% offline!", new long[] { 51512, 673, 29583, 18, 22800, 8922, 23494, 1043, 39083, 859, 7026, 65 })]
    public void Words_split_as_SentencePiece_splits_them(string text, long[] expected)
    {
        if (Folder is null)
            return;
        var ids = With(() => Translator.Encode(text));
        Assert.Equal(expected, ids[1..^1]);
        Assert.Equal(0, ids[^1]);
    }

    [Fact]
    public void Subtitles_come_out_in_arabic_with_their_timing()
    {
        if (Folder is null)
        {
            output.WriteLine("Skipped: PETAL_OPUS isn't set.");
            return;
        }
        const string srt = """
            1
            00:00:01,000 --> 00:00:03,500
            Welcome back to the channel, everyone.

            2
            00:00:03,600 --> 00:00:06,000
            Today we're going to bake bread
            at home.

            3
            00:00:06,100 --> 00:00:09,000
            <i>You only need flour, water, salt and yeast.</i>
            """;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var arabic = With(() => Translator.TranslateSubtitles(srt, vtt: false));
        output.WriteLine($"{clock.Elapsed.TotalSeconds:0.0} s\n{arabic}");
        var cues = Petal.Engines.Files.Subtitles.SubtitleConverter.Parse(arabic);
        Assert.Equal(3, cues.Count);
        Assert.Equal(TimeSpan.FromSeconds(3.6), cues[1].Start);
        Assert.All(cues, c => Assert.Contains(c.Text, ch => ch is >= '؀' and <= 'ۿ'));
        Assert.Contains("الخبز", cues[1].Text);
        Assert.Contains("الملح", cues[2].Text);
        Assert.DoesNotContain("<i>", arabic);
    }
}
