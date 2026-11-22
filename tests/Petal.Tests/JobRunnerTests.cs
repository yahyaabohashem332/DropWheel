using System.Diagnostics;
using System.Windows.Threading;
using ImageMagick;
using Microsoft.VisualBasic.FileIO;
using Petal.App.Input;
using Petal.App.Jobs;
using Petal.App.Settings;
using Petal.App.Util;
using Petal.Core.Formats;
using Petal.Core.Jobs;
using Xunit.Abstractions;
using F = Petal.Core.Formats.Formats;

namespace Petal.Tests;

/// <summary>A drop of several files: which go side by side, and what comes out.</summary>
[Collection("History")]
public class JobRunnerTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _dir = Directory.CreateTempSubdirectory("petal-jobs-").FullName;
    private readonly string _previousHistory = History.FilePath;

    public JobRunnerTests(ITestOutputHelper output)
    {
        _output = output;
        // Finished drops are recorded in the history: a file of this test's own, never the user's.
        History.FilePath = Path.Combine(_dir, "history.json");
        History.Enabled = () => true;
        History.Reload();
    }

    public void Dispose()
    {
        History.FilePath = _previousHistory;
        History.Reload();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private string Picture(string name, int seed)
    {
        using var image = new MagickImage($"plasma:fractal", 1000, 750);
        image.Settings.SetDefine("plasma:seed", seed.ToString());
        image.Quality = 90;
        var path = Path.Combine(_dir, name);
        image.Write(path);
        return path;
    }

    [Fact]
    public void Pictures_and_sounds_go_side_by_side_and_heavier_work_one_at_a_time()
    {
        var pictures = new[] { "a.jpg", "b.png", "c.heic" }.Select(n => Path.Combine(_dir, n)).ToList();
        foreach (var p in pictures)
            File.WriteAllBytes(p, [1, 2, 3]);
        // One per core, up to four: a hundred pictures still go four at a time.
        Assert.Equal(JobRunner.PerCore, JobRunner.BatchWidth(pictures, F.Webp));
        Assert.Equal(JobRunner.PerCore, JobRunner.BatchWidth(pictures, F.Pdf));
        var many = Enumerable.Range(0, 100).Select(i => pictures[i % pictures.Count]).ToList();
        Assert.Equal(JobRunner.PerCore, JobRunner.BatchWidth(many, F.Webp));
        Assert.InRange(JobRunner.PerCore, 1, 4);
        Assert.Equal(1, JobRunner.BatchWidth(pictures[..1], F.Webp));

        var clips = new[] { "a.mp4", "b.mov" }.Select(n => Path.Combine(_dir, n)).ToList();
        foreach (var p in clips)
            File.WriteAllBytes(p, [1, 2, 3]);
        Assert.Equal(1, JobRunner.BatchWidth(clips, F.Mp4));
        // A picture taken from each clip still waits for FFmpeg's turn.
        Assert.Equal(1, JobRunner.BatchWidth(clips, F.Jpg));

        var layered = Path.Combine(_dir, "big.psd");
        File.WriteAllBytes(layered, [1, 2, 3]);
        Assert.Equal(1, JobRunner.BatchWidth([.. pictures, layered], F.Png));
        Assert.Equal(1, JobRunner.BatchWidth(pictures, F.Mp4));
    }

    [Fact]
    public async Task Names_handed_out_in_a_batch_stay_apart_until_it_ends()
    {
        string Name() => Path.GetFileName(Petal.Core.Jobs.OutputNaming.ForConversion(Path.Combine(_dir, "a.jpg"), F.Webp));
        // Working a name out for display doesn't take it.
        Assert.Equal("a.webp", Name());
        Assert.Equal("a.webp", Name());
        using (Petal.Core.Jobs.OutputNaming.KeepApart())
        {
            var names = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(Name)));
            Assert.Equal(["a 2.webp", "a 3.webp", "a 4.webp", "a.webp"], names.Order());
        }
        Assert.Equal("a.webp", Name());
    }

    [Fact]
    public async Task A_drop_converts_together_and_files_that_would_share_a_name_keep_apart()
    {
        // "a.jpg" and "a.png" both become WEBP: one must not overwrite the other.
        var files = new List<string> { Picture("a.jpg", 1), Picture("a.png", 2) };
        for (int i = 0; i < 6; i++)
            files.Add(Picture($"photo {i}.jpg", 10 + i));

        var started = new TaskCompletionSource<Dispatcher>();
        var thread = new Thread(() =>
        {
            started.SetResult(Dispatcher.CurrentDispatcher);
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        var dispatcher = await started.Task;
        try
        {
            var clock = Stopwatch.StartNew();
            await await dispatcher.InvokeAsync(() =>
                new JobRunner(Petal.App.EngineSetup.CreateConverters()).ConvertAsync(files, F.Webp, new ScreenPoint(300, 300)));
            _output.WriteLine($"{files.Count} pictures in {clock.ElapsedMilliseconds} ms, {JobRunner.BatchWidth(files, F.Webp)} at a time");
        }
        finally
        {
            dispatcher.InvokeShutdown();
        }

        var webps = Directory.GetFiles(_dir, "*.webp").Select(Path.GetFileName).Order().ToList();
        Assert.Equal(["a 2.webp", "a.webp", "photo 0.webp", "photo 1.webp", "photo 2.webp", "photo 3.webp", "photo 4.webp", "photo 5.webp"], webps);
        foreach (var webp in webps)
        {
            using var image = new MagickImage(Path.Combine(_dir, webp!));
            Assert.Equal((1000u, 750u), (image.Width, image.Height));
        }
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp*"));
        // One drop, one line in the history, with every file in the order it came.
        var entry = Assert.Single(History.Entries);
        Assert.Equal(files, entry.Inputs);
        Assert.Equal(8, entry.Outputs.Count);
        Assert.Equal("a.webp", Path.GetFileName(entry.Outputs[0]));
    }

    [Fact]
    public async Task PreserveOriginalDates_copies_creation_and_modification_times()
    {
        var source = Picture("source.jpg", 42);
        Console.WriteLine($"Source file: {source}");
        Console.WriteLine($"Source exists: {File.Exists(source)}");

        // Touch the file to have distinct times (simulates a file that was already created earlier)
        Thread.Sleep(10);
        File.SetLastWriteTime(source, DateTime.Now.AddMinutes(-5));

        // Capture the dates AFTER modification, which is when JobRunner will capture them
        var originalCreation = File.GetCreationTime(source);
        var originalWrite = File.GetLastWriteTime(source);

        var originalSetting = SettingsStore.Current.Conversion.PreserveOriginalDates;
        SettingsStore.Current.Conversion.PreserveOriginalDates = true;
        Console.WriteLine($"PreserveOriginalDates: {SettingsStore.Current.Conversion.PreserveOriginalDates}");
        try
        {
            await RunOnStaThreadAsync(async () =>
            {
                Console.WriteLine("Starting conversion...");
                var runner = new JobRunner(Petal.App.EngineSetup.CreateConverters());
                try
                {
                    await runner.ConvertAsync([source], F.Webp, new ScreenPoint(100, 100));
                    Console.WriteLine("Conversion completed");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Conversion failed: {ex.Message}");
                    Console.WriteLine(ex.StackTrace);
                    throw;
                }
            });

            var outputs = Directory.GetFiles(_dir, "*.webp");
            Console.WriteLine($"Outputs found: {outputs.Length}");
            foreach (var o in outputs) Console.WriteLine($"  {o}");
            Assert.Single(outputs);
            var output = outputs[0];
            Assert.Equal(originalCreation, File.GetCreationTime(output));
            Assert.Equal(originalWrite, File.GetLastWriteTime(output));
        }
        finally
        {
            SettingsStore.Current.Conversion.PreserveOriginalDates = originalSetting;
        }
    }

    [Fact]
    public async Task MoveOriginalToRecycleBin_moves_source_to_recycle_bin()
    {
        var source = Picture("source.jpg", 43);
        Assert.True(File.Exists(source));

        var originalSetting = SettingsStore.Current.Conversion.MoveOriginalToRecycleBin;
        SettingsStore.Current.Conversion.MoveOriginalToRecycleBin = true;
        try
        {
            await RunOnStaThreadAsync(async () =>
            {
                await new JobRunner(Petal.App.EngineSetup.CreateConverters())
                    .ConvertAsync([source], F.Webp, new ScreenPoint(100, 100));
            });

            Assert.False(File.Exists(source), "Source file should be moved to Recycle Bin");
            // Verify it's in the recycle bin by trying to restore it
            var restored = RecycleBin.Restore([source]);
            Assert.NotEmpty(restored);
        }
        finally
        {
            SettingsStore.Current.Conversion.MoveOriginalToRecycleBin = originalSetting;
        }
    }

    [Fact]
    public void Only_a_conversion_of_a_file_dropped_on_its_own_may_send_it_to_the_Recycle_Bin()
    {
        var settings = new ConversionSettings { MoveOriginalToRecycleBin = true };
        string Make(string name, int bytes)
        {
            var path = Path.Combine(_dir, name);
            File.WriteAllBytes(path, new byte[bytes]);
            return path;
        }

        // The result is the original itself (a tool that saves over it): the original stays.
        var same = Make("same.jpg", 10);
        JobRunner.AfterConversion(same, ConvertedOriginal.Of(same, true), [same], settings);
        Assert.True(File.Exists(same));

        // A result is missing or empty: the original stays.
        var input = Make("input.jpg", 10);
        JobRunner.AfterConversion(input, ConvertedOriginal.Of(input, true), [Path.Combine(_dir, "never.webp")], settings);
        Assert.True(File.Exists(input));
        JobRunner.AfterConversion(input, ConvertedOriginal.Of(input, true), [Make("empty.webp", 0)], settings);
        Assert.True(File.Exists(input));
        JobRunner.AfterConversion(input, ConvertedOriginal.Of(input, true), [], settings);
        Assert.True(File.Exists(input));

        // From a watched folder or a dropped folder: the original stays.
        JobRunner.AfterConversion(input, ConvertedOriginal.Of(input, false), [Make("kept.webp", 10)], settings);
        Assert.True(File.Exists(input));

        // Every result there and holding something: the original goes, and can be put back.
        JobRunner.AfterConversion(input, ConvertedOriginal.Of(input, true), [Make("done 1.png", 10), Make("done 2.png", 10)], settings);
        Assert.False(File.Exists(input));
        Assert.NotEmpty(RecycleBin.Restore([input]));
    }

    [Fact]
    public void A_conversions_results_take_the_dates_the_original_had_before_it_ran()
    {
        var input = Path.Combine(_dir, "old.jpg");
        File.WriteAllBytes(input, [1, 2, 3]);
        var created = new DateTime(2020, 5, 6, 7, 8, 9);
        var written = new DateTime(2021, 1, 2, 3, 4, 5);
        File.SetCreationTime(input, created);
        File.SetLastWriteTime(input, written);
        var original = ConvertedOriginal.Of(input, true);
        // The original is touched while it converts: its dates from before still go on the results.
        File.SetLastWriteTime(input, DateTime.Now);

        var results = new[] { "old 1.png", "old 2.png" }.Select(n => Path.Combine(_dir, n)).ToList();
        foreach (var r in results)
            File.WriteAllBytes(r, [1]);
        JobRunner.AfterConversion(input, original, results, new ConversionSettings { PreserveOriginalDates = true });
        foreach (var r in results)
        {
            Assert.Equal(created, File.GetCreationTime(r));
            Assert.Equal(written, File.GetLastWriteTime(r));
        }
        Assert.True(File.Exists(input));
    }

    private static async Task RunOnStaThreadAsync(Func<Task> action)
    {
        var tcs = new TaskCompletionSource<Dispatcher>();
        var thread = new Thread(() =>
        {
            tcs.SetResult(Dispatcher.CurrentDispatcher);
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        var dispatcher = await tcs.Task;
        try
        {
            // Double await: first for scheduling, second for the async action's completion
            await await dispatcher.InvokeAsync(action);
        }
        finally
        {
            dispatcher.InvokeShutdown();
        }
    }
}
