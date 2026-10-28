namespace Petal.Engines.Media.Tests;

/// <summary>Remove Pauses: the silent stretches go, a little of each stays, picture and sound stay together.</summary>
public sealed class PauseRemoverTests
{
    [Fact]
    public void Pauses_are_read_from_FFmpegs_report_and_one_left_open_runs_to_the_end()
    {
        var report = "[silencedetect @ 0] silence_start: 1.002\n[silencedetect @ 0] silence_end: 3.001 | silence_duration: 2\n[silencedetect @ 0] silence_start: 9.5\n";
        var pauses = PauseRemover.Pauses(report, 10);
        Assert.Equal([(1.002, 3.001), (9.5, 10.0)], pauses);
        Assert.Empty(PauseRemover.Pauses("nothing here", 10));
    }

    [Fact]
    public void Each_cut_keeps_a_little_of_the_pause_beside_the_speech()
    {
        var ranges = PauseRemover.KeepRanges([(0, 0.8), (2, 4), (9, 10)], 10, keep: 0.2);
        // Every pause keeps 0.2 s beside the speech: before the first word, after the last, and both sides between.
        Assert.Equal(2, ranges.Count);
        Assert.Equal(0.6, ranges[0].Start, 6);
        Assert.Equal(2.2, ranges[0].End, 6);
        Assert.Equal(3.8, ranges[1].Start, 6);
        Assert.Equal(9.2, ranges[1].End, 6);
        // A pause too short to shorten stays.
        Assert.Equal([(0.0, 10.0)], PauseRemover.KeepRanges([(4, 4.3)], 10, keep: 0.2));
        Assert.Equal([(0.0, 10.0)], PauseRemover.KeepRanges([], 10, keep: 0.2));
    }

    [Fact]
    public void Cuts_fall_on_the_video_s_frames_and_are_counted_as_made()
    {
        var ranges = PauseRemover.OnFrames([(0.0, 1.013), (2.51, 4.0)], 25);
        Assert.Equal([(0.0, 1.0), (2.52, 4.0)], ranges);
        // The gap between the parts, and the one at the end.
        Assert.Equal(2, PauseRemover.Cuts(ranges, 6));
        Assert.Equal(0, PauseRemover.Cuts([(0.0, 6.0)], 6));
        // A thousand cuts are all made (a filter too long for the command line goes in a file).
        var many = Enumerable.Range(0, 1000).Select(i => (i * 3.123456, i * 3.123456 + 2.5)).ToList();
        Assert.Equal(1000, PauseRemover.Fit(many).Count);
    }

    [Fact]
    public void Hours_of_short_pauses_join_the_closest_parts_to_keep_the_filter_quick()
    {
        var many = Enumerable.Range(0, 12_000).Select(i => (i * 1.0, i + 0.5)).ToList();
        var fitted = PauseRemover.Fit(many);
        Assert.True(PauseRemover.Expression(fitted).Length <= 400_000);
        Assert.True(fitted.Count > 5000);
        Assert.Equal(0, fitted[0].Start);
        Assert.Equal(11_999.5, fitted[^1].End);
    }

    [Fact]
    public void A_filter_too_long_for_the_command_line_is_read_from_a_file()
    {
        var args = new List<string>();
        Internal.MediaEncoder.AddFilter(args, "af", "volume=0.5");
        Assert.Equal(["-af", "volume=0.5"], args);
        var many = PauseRemover.Expression(Enumerable.Range(0, 600).Select(i => (i * 1.0, i + 0.5)).ToList());
        args.Clear();
        Internal.MediaEncoder.AddFilter(args, "vf", $"select='{many}'");
        Assert.Equal("-/vf", args[0]);
        Assert.Equal($"select='{many}'", File.ReadAllText(args[1]));
    }

    [Fact]
    public async Task A_recording_loses_its_pauses()
    {
        var input = await Fixtures.GetAsync("pauses.wav");
        var output = Path.Combine(Fixtures.NewDir(), "pauses No pauses.wav");
        var cut = await PauseRemover.RemoveAsync(input, output, new RemovePausesOptions(-35, 0.6, 0.15));
        Assert.Equal(2, cut.Pauses);
        var info = await MediaProbe.ProbeAsync(output);
        // 3 s of tone and 0.15 s kept on each side of the two cuts.
        Assert.InRange(info.Duration.TotalSeconds, 3.45, 3.8);
        Assert.InRange(cut.Removed.TotalSeconds, 2.7, 3.05);
    }

    [Fact]
    public async Task A_video_keeps_its_picture_and_sound_together()
    {
        var input = await Fixtures.GetAsync("pauses.mp4");
        var output = Path.Combine(Fixtures.NewDir(), "pauses No pauses.mp4");
        var cut = await PauseRemover.RemoveAsync(input, output, new RemovePausesOptions());
        Assert.NotNull(cut.OutputPath);
        var info = await MediaProbe.ProbeAsync(output);
        Assert.NotNull(info.Video);
        Assert.NotNull(info.Audio);
        Assert.InRange(info.Duration.TotalSeconds, 3.3, 3.9);
        var video = info.Streams.First(s => s.Kind == MediaStreamKind.Video).Duration?.TotalSeconds ?? info.Duration.TotalSeconds;
        var audio = info.Streams.First(s => s.Kind == MediaStreamKind.Audio).Duration?.TotalSeconds ?? info.Duration.TotalSeconds;
        Assert.InRange(Math.Abs(video - audio), 0, 0.15);
    }

    [Fact]
    public async Task More_than_a_hundred_pauses_are_all_cut()
    {
        // FFmpeg's parser takes no more than a hundred terms added in a row: 130 parts kept were once refused.
        var input = await Fixtures.GetAsync("many-pauses.mp4");
        var output = Path.Combine(Fixtures.NewDir(), "many No pauses.mp4");
        var cut = await PauseRemover.RemoveAsync(input, output, new RemovePausesOptions(-35, 0.5, 0.1));
        Assert.InRange(cut.Pauses, 125, 130);
        var info = await MediaProbe.ProbeAsync(output);
        // Each 0.7 s pause cut to 0.2 s: 130 s comes to about 65.
        Assert.InRange(info.Duration.TotalSeconds, 60, 70);
        var video = info.Streams.First(s => s.Kind == MediaStreamKind.Video).Duration?.TotalSeconds ?? info.Duration.TotalSeconds;
        var audio = info.Streams.First(s => s.Kind == MediaStreamKind.Audio).Duration?.TotalSeconds ?? info.Duration.TotalSeconds;
        Assert.InRange(Math.Abs(video - audio), 0, 0.15);
    }

    [Fact]
    public void The_expression_searches_the_parts_kept_by_where_they_start()
    {
        Assert.Equal("0", PauseRemover.Expression([]));
        Assert.Equal("gte(t,1)*lt(t,2)", PauseRemover.Expression([(1, 2)]));
        // Out of order and overlapping: put in order and joined.
        Assert.Equal("if(lt(t,4),gte(t,1)*lt(t,3),gte(t,4)*lt(t,5))", PauseRemover.Expression([(4, 5), (2, 3), (1, 2.5)]));
        // Thousands of parts: a few dozen levels deep, not thousands.
        var deep = PauseRemover.Expression(Enumerable.Range(0, 5000).Select(i => (i * 1.0, i + 0.5)).ToList());
        int depth = 0, most = 0;
        foreach (var c in deep)
            most = Math.Max(most, depth += c == '(' ? 1 : c == ')' ? -1 : 0);
        Assert.InRange(most, 10, 40);
    }

    [Fact]
    public async Task Nothing_is_written_when_there_are_no_pauses()
    {
        var input = await Fixtures.GetAsync("music.mp3");
        var output = Path.Combine(Fixtures.NewDir(), "music No pauses.mp3");
        var cut = await PauseRemover.RemoveAsync(input, output, new RemovePausesOptions());
        Assert.Null(cut.OutputPath);
        Assert.False(File.Exists(output));
    }
}
