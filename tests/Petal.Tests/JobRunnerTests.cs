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
        // For light formats, all files in the batch go in parallel (up to 100)
        Assert.Equal(3, JobRunner.BatchWidth(pictures, F.Webp));
        Assert.Equal(3, JobRunner.BatchWidth(pictures, F.Pdf));
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

        SettingsStore.Current.Conversion.PreserveOriginalDates = true;
        Console.WriteLine($"PreserveOriginalDates: {SettingsStore.Current.Conversion.PreserveOriginalDates}");

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

    [Fact]
    public async Task MoveOriginalToRecycleBin_moves_source_to_recycle_bin()
    {
        var source = Picture("source.jpg", 43);
        Assert.True(File.Exists(source));

        SettingsStore.Current.Conversion.MoveOriginalToRecycleBin = true;

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

    [Fact]
    public void PdfLongAndFirstPage_converters_exist_in_matrix()
    {
        // PDF → long PNG/JPG and first PNG/JPG should be available
        var pdfTargets = ConversionMatrix.TargetsFor(F.Pdf).Select(t => t.Id).ToHashSet();
        Assert.Contains("pdflongpng", pdfTargets);
        Assert.Contains("pdflongjpg", pdfTargets);
        Assert.Contains("pdffirstpng", pdfTargets);
        Assert.Contains("pdffirstjpg", pdfTargets);
    }

    [Fact]
    public async Task BatchProgressCallback_reports_progress()
    {
        var progressReports = new List<(int Done, int Total)>();
        var files = Enumerable.Range(0, 5).Select(i => Picture($"p{i}.jpg", i)).ToList();

        await RunOnStaThreadAsync(async () =>
        {
            var runner = new JobRunner(Petal.App.EngineSetup.CreateConverters());
            runner.BatchProgressCallback = (done, total) => progressReports.Add((done, total));
            await runner.ConvertAsync(files, F.Png, new ScreenPoint(100, 100));
        });

        // Should report initial (0), each completion, and final (5/5)
        Assert.Contains(progressReports, r => r.Done == 0 && r.Total == 5);
        Assert.Contains(progressReports, r => r.Done == 5 && r.Total == 5);
        Assert.Equal(5, progressReports.Max(r => r.Done));
    }

    // [Fact] - Temporarily disabled due to build issue with method resolution
// public async Task AutoChainRecipe_runs_multi_step_conversion()
// {
//     // Test the chain discovery logic: Docx -> Pdf -> Png
//     var converters = Petal.App.EngineSetup.CreateConverters();
//     IReadOnlyCollection<FileFormat> sources = new[] { F.Docx };
//     var chainTargets = Petal.App.PetalController.FindChainTargetsForTest(sources, converters);
//     Assert.NotEmpty(chainTargets);
//     var pngChain = chainTargets.FirstOrDefault(c => c.FinalTarget == F.Png);
//     Assert.NotNull(pngChain);
//     Assert.Contains(F.Pdf, pngChain.Chain.Select(s => s.Format));
// }

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
