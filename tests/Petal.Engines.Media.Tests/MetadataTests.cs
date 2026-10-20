namespace Petal.Engines.Media.Tests;

public class MetadataTests
{
    [Fact]
    public async Task Reads_grouped_tags_and_facts()
    {
        var groups = await MediaMetadata.ReadAsync(await Fixtures.GetAsync("tagged.mp3"));

        var file = groups.Single(g => g.Scope == MetadataScope.File);
        Assert.Contains(file.Entries, e => e.Key == "title" && e.Value == "Petal Test" && e.Editable);
        Assert.Contains(file.Entries, e => e.Key == "artist" && e.Value == "Petal Artist");

        var info = groups.Single(g => g.Scope == MetadataScope.Info);
        Assert.All(info.Entries, e => Assert.False(e.Editable));
        Assert.Contains(info.Entries, e => e.Key == "Duration");

        var streams = groups.Where(g => g.Scope == MetadataScope.Stream).ToList();
        Assert.Equal(2, streams.Count);
        Assert.Contains(streams, g => g.Name.Contains("Audio") && g.Entries.Any(e => e.Key == "Sample Rate" && e.Value == "44100 Hz"));
        Assert.Contains(streams, g => g.Name.Contains("Cover Art"));
    }

    [Fact]
    public async Task Writes_edited_and_removed_tags()
    {
        var input = await Fixtures.GetAsync("tagged.mp3");
        var output = Path.Combine(Fixtures.NewDir(), "edited.mp3");
        var edits = new MetadataEdits(new Dictionary<string, string?>
        {
            ["title"] = "New Title",
            ["artist"] = null,
            ["comment"] = "Saved by Petal",
        });
        await MediaMetadata.WriteAsync(input, output, edits);

        var info = await MediaAssert.ValidAsync(output, 1.0, 0.1);
        Assert.Equal("New Title", info.Tags["title"]);
        Assert.False(info.Tags.ContainsKey("artist"));
        Assert.Equal("Fixtures", info.Tags["album"]);
        Assert.Equal("Saved by Petal", info.Tags["comment"]);
        Assert.NotNull(info.CoverArt);
        Assert.Equal("mp3", info.AudioCodec);
    }

    [Fact]
    public async Task Writes_mp4_and_stream_tags()
    {
        var input = await Fixtures.GetAsync("tagged.mp4");
        var source = await MediaProbe.ProbeAsync(input);
        var output = Path.Combine(Fixtures.NewDir(), "edited.mp4");
        var edits = new MetadataEdits(
            new Dictionary<string, string?> { ["title"] = "Holiday" },
            new Dictionary<int, IReadOnlyDictionary<string, string?>> { [source.Audio!.Index] = new Dictionary<string, string?> { ["language"] = "fra" } });
        await MediaMetadata.WriteAsync(input, output, edits);

        var info = await MediaAssert.ValidAsync(output, 1.0);
        Assert.Equal("Holiday", info.Tags["title"]);
        Assert.Equal("Hello", info.Tags["comment"]);
        Assert.Equal("fra", info.Audio!.Language);
        Assert.Equal(source.VideoBitRate, info.VideoBitRate);
    }

    [Theory]
    [InlineData("tagged.mp3")]
    [InlineData("tagged.mp4")]
    [InlineData("video.gif")]
    [InlineData("video.mkv")]
    [InlineData("audio.flac")]
    public async Task Remove_all_strips_tags_but_keeps_streams(string fixture)
    {
        var input = await Fixtures.GetAsync(fixture);
        var source = await MediaProbe.ProbeAsync(input);
        var output = Path.Combine(Fixtures.NewDir(), "clean" + Path.GetExtension(input));
        await MediaMetadata.RemoveAllAsync(input, output);

        var info = await MediaAssert.ValidAsync(output, source.DurationSeconds, 0.1);
        Assert.False(info.Tags.ContainsKey("title"));
        Assert.False(info.Tags.ContainsKey("comment"));
        // Matroska always records a writing application; bitexact reduces it to a bare "Lavf".
        if (info.Tags.TryGetValue("encoder", out var encoder))
            Assert.Equal("Lavf", encoder);
        Assert.Equal(source.HasVideo, info.HasVideo);
        Assert.Equal(source.HasAudio, info.HasAudio);
        Assert.Equal(source.CoverArt is null, info.CoverArt is null);
    }

    [Fact]
    public async Task Remove_all_keeps_rotation()
    {
        var input = await Fixtures.GetAsync("rotated.mp4");
        var output = Path.Combine(Fixtures.NewDir(), "clean.mp4");
        await MediaMetadata.RemoveAllAsync(input, output);
        var info = await MediaAssert.ValidAsync(output, 1.0);
        Assert.Equal(270, info.Rotation);
    }
}
