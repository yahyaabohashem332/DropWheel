using Petal.App.Jobs;

namespace Petal.Tests;

/// <summary>History is static: every test class that touches it joins this collection and runs alone.</summary>
[CollectionDefinition("History", DisableParallelization = true)]
public class HistoryCollection;

[Collection("History")]
public class HistoryTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("petal-history-").FullName;
    private readonly string _previousPath = History.FilePath;

    public HistoryTests()
    {
        History.FilePath = Path.Combine(_dir, "history.json");
        History.Enabled = () => true;
        History.Reload();
    }

    public void Dispose()
    {
        History.FilePath = _previousPath;
        History.Enabled = () => true;
        History.Reload();
        Directory.Delete(_dir, recursive: true);
    }

    private string Make(string name)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, name);
        return path;
    }

    [Fact]
    public void Entries_are_newest_first_and_survive_a_restart()
    {
        History.Add("Converted to PNG", [Make("a.jpg")], [Make("a.png")]);
        History.Add("Compressed", [Make("b.mp4")], [Make("b compressed.mp4")]);

        History.Reload();
        var entries = History.Entries;
        Assert.Equal(2, entries.Count);
        Assert.Equal("Compressed", entries[0].Title);
        Assert.Equal("Converted to PNG", entries[1].Title);
        Assert.Equal(Path.Combine(_dir, "a.png"), entries[1].Outputs[0]);
        Assert.False(entries[0].Undone);
    }

    [Fact]
    public void Nothing_is_kept_when_turned_off_or_when_nothing_was_made()
    {
        History.Add("Converted to PNG", [Make("a.jpg")], []);
        History.Enabled = () => false;
        History.Add("Converted to PNG", [Make("b.jpg")], [Make("b.png")]);
        Assert.Empty(History.Entries);
    }

    [Fact]
    public void Only_the_latest_300_are_kept()
    {
        var output = Make("x.png");
        for (int i = 0; i < 305; i++)
            History.Add($"Job {i}", [], [output]);
        var entries = History.Entries;
        Assert.Equal(300, entries.Count);
        Assert.Equal("Job 304", entries[0].Title);
        Assert.Equal("Job 5", entries[^1].Title);
    }

    [Fact]
    public void Undoing_a_rename_puts_the_old_names_back()
    {
        var from = Make("IMG_001.jpg");
        var to = Path.Combine(_dir, "Trip 1.jpg");
        File.Move(from, to);
        History.Add("Renamed 1 item", [from], [], [new RenamedPath(from, to)]);

        var entry = History.Entries[0];
        Assert.Equal([to], History.Existing(entry));
        Assert.Equal(1, History.Undo(entry.Id));
        Assert.True(File.Exists(from));
        Assert.False(File.Exists(to));
        Assert.True(History.Entries[0].Undone);

        // Undoing twice does nothing.
        Assert.Equal(0, History.Undo(entry.Id));
    }

    [Fact]
    public void Results_that_were_moved_away_are_not_offered()
    {
        var kept = Make("kept.png");
        var gone = Path.Combine(_dir, "gone.png");
        History.Add("Converted to PNG", [], [kept, gone]);
        Assert.Equal([kept], History.Existing(History.Entries[0]));

        // Undo skips what is no longer there, and still marks the entry as done.
        File.Delete(kept);
        Assert.Equal(0, History.Undo(History.Entries[0].Id));
        Assert.True(History.Entries[0].Undone);
    }

    [Fact]
    public void Clear_forgets_everything()
    {
        History.Add("Compressed", [], [Make("c.jpg")]);
        History.Clear();
        History.Reload();
        Assert.Empty(History.Entries);
    }
}
