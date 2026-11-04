using System.IO;
using Petal.App.Work;
using Petal.Core.Time;

namespace Petal.Tests;

/// <summary>The time log: only what it is given, kept only here, and it can all be forgotten.</summary>
public sealed class TimeLogTests : IDisposable
{
    private static readonly DateOnly Monday = new(2026, 11, 2);
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "Petal", "time-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
            Directory.Delete(_folder, true);
    }

    private TimeLog New() => new(_folder);

    [Fact]
    public void Time_adds_up_most_first()
    {
        var log = New();
        log.Add(Monday, "Word", 60);
        log.Add(Monday, "Chrome", 100);
        log.Add(Monday, "Word", 90);
        var day = log.Range(Monday, Monday);
        Assert.Equal(["Word", "Chrome"], day.Select(e => e.App));
        Assert.Equal(150, day[0].Seconds);
    }

    [Fact]
    public void It_survives_a_restart()
    {
        var log = New();
        log.Add(Monday, "Word", 60, "Report.docx");
        log.Save();
        var again = New();
        var entry = Assert.Single(again.Range(Monday, Monday));
        Assert.Equal(60, entry.Seconds);
        Assert.Equal(60, entry.Titles["Report.docx"]);
        again.Add(Monday, "Word", 5);
        Assert.Equal(65, Assert.Single(again.Range(Monday, Monday)).Seconds);
    }

    [Fact]
    public void Days_are_kept_apart_and_a_range_sums_them()
    {
        var log = New();
        log.Add(Monday, "Word", 60);
        log.Add(Monday.AddDays(1), "Word", 30);
        log.Add(Monday.AddDays(3), "Chrome", 10);
        log.Save();
        Assert.Equal(60, Assert.Single(New().Range(Monday, Monday)).Seconds);
        var week = New().Range(Monday, Monday.AddDays(6));
        Assert.Equal(90, week.Single(e => e.App == "Word").Seconds);
        Assert.Empty(New().Range(Monday.AddDays(10), Monday.AddDays(12)));
    }

    [Fact]
    public void Without_titles_nothing_about_what_was_done_is_in_the_file()
    {
        var log = New();
        log.Add(Monday, "Word", 60);
        log.Save();
        var file = Assert.Single(Directory.GetFiles(_folder));
        var text = File.ReadAllText(file);
        Assert.Contains("Word", text);
        Assert.Contains("\"Titles\":{}", text);
        Assert.Empty(New().Range(Monday, Monday)[0].Titles);
    }

    [Fact]
    public void A_browser_cant_make_the_file_grow_without_end()
    {
        var log = New();
        for (int i = 0; i < 500; i++)
            log.Add(Monday, "Chrome", 5, "Page " + i);
        var entry = Assert.Single(log.Range(Monday, Monday));
        Assert.Equal(2500, entry.Seconds);
        Assert.Equal(200, entry.Titles.Count);
    }

    [Theory]
    [InlineData("", 5)]
    [InlineData("Word", 0)]
    [InlineData("Word", -5)]
    public void Nothing_isnt_counted(string app, long seconds)
    {
        var log = New();
        log.Add(Monday, app, seconds);
        Assert.Empty(log.Range(Monday, Monday));
    }

    [Fact]
    public void A_damaged_day_starts_again_and_harms_no_other()
    {
        var log = New();
        log.Add(Monday, "Word", 60);
        log.Add(Monday.AddDays(1), "Word", 30);
        log.Save();
        File.WriteAllText(Path.Combine(_folder, "2026-11-02.json"), "{ not json");
        var again = New();
        Assert.Equal(30, Assert.Single(again.Range(Monday, Monday.AddDays(1))).Seconds);
    }

    [Fact]
    public void A_spreadsheet_gets_a_row_a_day_and_program()
    {
        var log = New();
        log.Add(Monday, "Word, the app", 90);
        log.Add(Monday, "=cmd", 60);
        log.Add(Monday.AddDays(1), "Chrome", 30);
        var lines = log.Csv(Monday, Monday.AddDays(1), "Date", "Program", "Minutes").Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("Date,Program,Minutes", lines[0]);
        Assert.Equal("2026-11-02,\"Word, the app\",1.5", lines[1]);
        // A name that looks like a formula stays text.
        Assert.Equal("2026-11-02,'=cmd,1", lines[2]);
        Assert.Equal("2026-11-03,Chrome,0.5", lines[3]);
    }

    [Fact]
    public void Old_days_go_and_all_can_be_forgotten()
    {
        var log = New();
        log.Add(Monday, "Word", 60);
        log.Add(Monday.AddDays(-400), "Old", 60);
        log.Save();
        New().Trim(365, Monday);
        Assert.Empty(New().Range(Monday.AddDays(-400), Monday.AddDays(-400)));
        Assert.Single(New().Range(Monday, Monday));
        var again = New();
        again.Clear();
        Assert.Empty(Directory.GetFiles(_folder));
        Assert.Empty(again.Range(Monday, Monday));
    }

    [Theory]
    [InlineData(30, false)]
    [InlineData(300, true)]
    [InlineData(7500, true)]
    public void A_length_is_said_as_people_say_it(long seconds, bool hasNumber) =>
        Assert.Equal(hasNumber, TimeTrackerWindow.Length(seconds).Any(char.IsDigit));
}
