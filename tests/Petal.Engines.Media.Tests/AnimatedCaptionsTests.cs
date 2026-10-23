namespace Petal.Engines.Media.Tests;

/// <summary>Animated captions: words timed within their sentence, short lines, and the captions burned into the picture.</summary>
public sealed class AnimatedCaptionsTests
{
    private static readonly TranscriptSegment[] Speech =
    [
        new(TimeSpan.FromSeconds(0.2), TimeSpan.FromSeconds(1.6), "Hello there, this is Petal."),
        new(TimeSpan.FromSeconds(2.0), TimeSpan.FromSeconds(3.8), "مرحبا بكم في بتل"),
    ];

    [Fact]
    public void Words_share_their_sentences_time_by_their_length()
    {
        var words = AnimatedCaptions.Words(Speech);
        Assert.Equal(["Hello", "there,", "this", "is", "Petal.", "مرحبا", "بكم", "في", "بتل"], words.Select(w => w.Text));
        Assert.Equal(TimeSpan.FromSeconds(0.2), words[0].Start);
        Assert.Equal(1.6, words[4].End.TotalSeconds, 6);
        // One after another, longer words longer.
        for (int i = 1; i < 5; i++)
            Assert.Equal(words[i - 1].End.TotalSeconds, words[i].Start.TotalSeconds, 6);
        Assert.True(words[0].End - words[0].Start > words[3].End - words[3].Start);
    }

    [Fact]
    public void Lines_are_short_and_end_with_sentences_and_pauses()
    {
        var lines = AnimatedCaptions.Lines(AnimatedCaptions.Words(Speech), mostWords: 3);
        Assert.Equal(["Hello there, this", "is Petal.", "مرحبا بكم في", "بتل"], lines.Select(l => string.Join(' ', l.Select(w => w.Text))));
    }

    [Fact]
    public void The_captions_file_lights_one_word_at_a_time()
    {
        var ass = AnimatedCaptions.Ass(Speech, 1280, 720, new AnimatedCaptionOptions(Highlight: "#FF375F", MostWords: 4));
        Assert.Contains("PlayResX: 1280", ass);
        Assert.Contains("Style: Petal,Segoe UI,43,", ass);
        // One event per word, the lit word in the chosen colour (ASS writes it blue-green-red).
        Assert.Equal(9, ass.Split("Dialogue:").Length - 1);
        Assert.Contains(@"{\c&H005F37FF&\fscx112\fscy112}Hello{\c&H00FFFFFF&\fscx100\fscy100} there,", ass);
        Assert.Contains("Dialogue: 0,0:00:00.20,", ass);
        // Plain lines: one event a line.
        var lines = AnimatedCaptions.Ass(Speech, 1280, 720, new AnimatedCaptionOptions(CaptionStyle.Lines));
        Assert.Equal(AnimatedCaptions.Lines(AnimatedCaptions.Words(Speech), 5).Count, lines.Split("Dialogue:").Length - 1);
        Assert.Equal("&H000AD6FF&", AnimatedCaptions.Colour("#FFD60A"));
        Assert.Equal("a(b)c", AnimatedCaptions.Escape(@"a{b}\c"));
        Assert.Equal("1:02:03.45", AnimatedCaptions.Stamp(new TimeSpan(0, 1, 2, 3, 456)));
    }

    [Fact]
    public async Task Captions_are_burned_into_the_picture()
    {
        var input = await Fixtures.GetAsync("hq.mp4");
        var output = Path.Combine(Fixtures.NewDir(), "hq Captioned.mp4");
        await AnimatedCaptions.BurnAsync(input, Speech, output, new AnimatedCaptionOptions(Size: 0.12));
        var info = await MediaProbe.ProbeAsync(output);
        Assert.NotNull(info.Video);
        Assert.NotNull(info.Audio);
        // The bottom of a frame while a word is up differs from the same frame without captions.
        var before = await MediaPreview.ExtractFrameAsync(input, TimeSpan.FromSeconds(1), 0);
        var after = await MediaPreview.ExtractFrameAsync(output, TimeSpan.FromSeconds(1), 0);
        Assert.NotEqual(Convert.ToBase64String(before), Convert.ToBase64String(after));
        var arabicFrame = await MediaPreview.ExtractFrameAsync(output, TimeSpan.FromSeconds(3), 0);
        Assert.NotEqual(Convert.ToBase64String(await MediaPreview.ExtractFrameAsync(input, TimeSpan.FromSeconds(3), 0)), Convert.ToBase64String(arabicFrame));
    }
}
