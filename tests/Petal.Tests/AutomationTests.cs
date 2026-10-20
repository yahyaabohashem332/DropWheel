using System.Windows.Threading;
using Petal.App.Jobs;
using Petal.Core.Jobs;
using Petal.Engines.Imaging;

namespace Petal.Tests;

public class WatchRuleTests
{
    private const string Root = @"C:\Users\Someone\Downloads";

    [Theory]
    [InlineData(@"C:\Users\Someone\Downloads\photo.png", true)]
    [InlineData(@"C:\Users\Someone\Downloads\PHOTO.HEIC", true)]
    [InlineData(@"C:\Users\Someone\Downloads\song.mp3", false)]
    [InlineData(@"C:\Users\Someone\Downloads\photo.png.crdownload", false)]
    [InlineData(@"C:\Users\Someone\Downloads\photo.part", false)]
    [InlineData(@"C:\Users\Someone\Downloads\~$report.docx", false)]
    [InlineData(@"C:\Users\Someone\Downloads\Petal\photo.jpg", false)]
    [InlineData(@"C:\Users\Someone\Downloads\Trip\photo.png", false)]
    [InlineData(@"C:\Users\Someone\Downloads2\photo.png", false)]
    [InlineData(@"C:\Users\Someone\photo.png", false)]
    public void Takes_finished_pictures_directly_in_the_folder(string path, bool expected) =>
        Assert.Equal(expected, new WatchRule { Folder = Root, Kind = WatchedKind.Pictures }.Wants(path));

    [Fact]
    public void Subfolders_count_when_asked_but_never_the_results_folder()
    {
        var rule = new WatchRule { Folder = Root + @"\", Kind = WatchedKind.Any, IncludeSubfolders = true };
        Assert.True(rule.Wants(Root + @"\Trip\Day 1\clip.mov"));
        Assert.True(rule.Wants(Root + @"\notes.txt"));
        Assert.False(rule.Wants(Root + @"\Petal\clip.mp4"));
        Assert.False(rule.Wants(Root + @"\Petal\Older\clip.mp4"));
        Assert.True(rule.Wants(Root + @"\Petals\clip.mov"));
        Assert.False(rule.Wants(Root + @"\unknown.xyz"));
        Assert.Equal(Root + @"\Petal", rule.OutputFolder.TrimEnd('\\').Replace(@"\\", @"\"));
    }

    [Theory]
    [InlineData(WatchedKind.Videos, "clip.mkv", true)]
    [InlineData(WatchedKind.Videos, "anim.gif", true)]
    [InlineData(WatchedKind.Audio, "voice.m4a", true)]
    [InlineData(WatchedKind.Audio, "clip.mp4", false)]
    [InlineData(WatchedKind.Documents, "report.pdf", true)]
    [InlineData(WatchedKind.Documents, "sheet.xlsx", true)]
    [InlineData(WatchedKind.Documents, "photo.jpg", false)]
    [InlineData(WatchedKind.Pictures, "logo.svg", true)]
    public void Kinds_pick_the_right_files(WatchedKind kind, string name, bool expected) =>
        Assert.Equal(expected, new WatchRule { Folder = Root, Kind = kind }.Wants(Path.Combine(Root, name)));

    [Fact]
    public void An_empty_rule_wants_nothing() =>
        Assert.False(new WatchRule { Folder = "" }.Wants(@"C:\photo.png"));
}

public class FolderWatcherTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("petal-watch-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task Waits_until_a_file_has_finished_arriving()
    {
        var path = Path.Combine(_dir, "big.bin");
        var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        await stream.WriteAsync(new byte[1000]);
        await stream.FlushAsync();

        // Still open for writing: not complete.
        Assert.False(await FolderWatcher.WaitUntilCompleteAsync(path, TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(600), CancellationToken.None));

        var waiting = FolderWatcher.WaitUntilCompleteAsync(path, TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(10), CancellationToken.None);
        await Task.Delay(300);
        await stream.WriteAsync(new byte[1000]);
        await stream.DisposeAsync();
        Assert.True(await waiting);
        Assert.Equal(2000, new FileInfo(path).Length);

        // A file that goes away is not waited for.
        File.Delete(path);
        Assert.False(await FolderWatcher.WaitUntilCompleteAsync(path, TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(5), CancellationToken.None));
    }

    [Fact]
    public async Task New_files_are_gathered_and_handed_over_once()
    {
        var batches = new List<IReadOnlyList<string>>();
        var gotOne = new TaskCompletionSource();
        var started = new TaskCompletionSource<(Dispatcher, FolderWatcher)>();

        // The watcher hands batches over on a dispatcher thread, like the app's UI thread.
        var thread = new Thread(() =>
        {
            var converters = new Petal.Core.Jobs.ConverterRegistry();
            converters.Register(new ImageConverter());
            var watcher = new FolderWatcher(new JobRunner(converters), Dispatcher.CurrentDispatcher, (_, files) =>
            {
                lock (batches)
                    batches.Add(files);
                gotOne.TrySetResult();
            });
            watcher.Apply([new WatchRule { Folder = _dir, Kind = WatchedKind.Pictures, Target = "jpg" }]);
            started.SetResult((Dispatcher.CurrentDispatcher, watcher));
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        var (dispatcher, watcher) = await started.Task;

        try
        {
            var png = Fixture.Png();
            // Two pictures arriving together, one of them as a browser download that gets renamed.
            File.WriteAllBytes(Path.Combine(_dir, "a.png"), png);
            var partial = Path.Combine(_dir, "b.png.crdownload");
            File.WriteAllBytes(partial, png);
            File.Move(partial, Path.Combine(_dir, "b.png"));
            // Not wanted: a song, a result in the Petal folder, and a JPG (already the target format).
            File.WriteAllBytes(Path.Combine(_dir, "song.mp3"), [1, 2, 3]);
            Directory.CreateDirectory(Path.Combine(_dir, "Petal"));
            File.WriteAllBytes(Path.Combine(_dir, "Petal", "c.png"), png);
            File.WriteAllBytes(Path.Combine(_dir, "d.jpg"), png);

            await gotOne.Task.WaitAsync(TimeSpan.FromSeconds(20));
            await Task.Delay(2500);
            lock (batches)
            {
                var batch = Assert.Single(batches);
                Assert.Equal(["a.png", "b.png"], batch.Select(Path.GetFileName).Order());
            }
        }
        finally
        {
            await dispatcher.InvokeAsync(() =>
            {
                watcher.Dispose();
                dispatcher.InvokeShutdown();
            });
        }
    }

    private static class Fixture
    {
        /// <summary>A valid 1 × 1 PNG.</summary>
        public static byte[] Png() => Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");
    }
}
