using System.Diagnostics;
using System.Windows.Threading;
using ImageMagick;
using Petal.App.Input;
using Petal.App.Jobs;
using Xunit.Abstractions;
using F = Petal.Core.Formats.Formats;

namespace Petal.Tests;

/// <summary>A drop of several files: which go side by side, and what comes out.</summary>
public class JobRunnerTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("petal-jobs-").FullName;

    public void Dispose()
    {
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
        Assert.Equal(JobRunner.PerCore, JobRunner.BatchWidth(pictures, F.Webp));
        Assert.Equal(JobRunner.PerCore, JobRunner.BatchWidth(pictures, F.Pdf));
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
            output.WriteLine($"{files.Count} pictures in {clock.ElapsedMilliseconds} ms, {JobRunner.BatchWidth(files, F.Webp)} at a time");
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
    }
}
