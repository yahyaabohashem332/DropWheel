using System.Diagnostics;
using ImageMagick;
using Petal.App.Util;
using Petal.Recipes;
using Xunit.Abstractions;

namespace Petal.Tests;

public sealed class DuplicateTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("petal-duplicates-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private string Photo(string name, string seed, uint width = 1600, uint height = 1200, string? format = null)
    {
        var path = Path.Combine(_dir, name);
        using var image = new MagickImage($"plasma:{seed}", width, height);
        image.Settings.SetDefine("plasma:seed", "1");
        image.Write(path, format is null ? MagickFormat.Unknown : Enum.Parse<MagickFormat>(format, true));
        return path;
    }

    private string Video(string name, string size, int crf)
    {
        var path = Path.Combine(_dir, name);
        var ffmpeg = Petal.Core.Util.ToolLocator.Ffmpeg ?? throw new InvalidOperationException("ffmpeg");
        using var process = Process.Start(new ProcessStartInfo(ffmpeg, ["-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc2=duration=4:size=640x360:rate=25",
            "-vf", $"scale={size}", "-c:v", "h264_mf", "-rate_control", "quality", "-quality", Math.Clamp(100 - 2 * crf, 1, 100).ToString(), "-pix_fmt", "nv12", "-y", path]) { CreateNoWindow = true })!;
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
        return path;
    }

    [Fact]
    public async Task Identical_files_and_near_copies_are_grouped()
    {
        var original = Photo("beach.jpg", "red-blue");
        File.Copy(original, Path.Combine(_dir, "beach copy.jpg"));
        using (var small = new MagickImage(original))
        {
            small.Resize(800, 600);
            small.Quality = 70;
            small.Write(Path.Combine(_dir, "beach small.jpg"));
            small.Write(Path.Combine(_dir, "beach.png"));
        }
        Photo("forest.jpg", "green-yellow");
        Directory.CreateDirectory(Path.Combine(_dir, "notes"));
        File.WriteAllText(Path.Combine(_dir, "notes", "list.txt"), "Apples, bread and milk.");
        File.WriteAllText(Path.Combine(_dir, "list.txt"), "Apples, bread and milk.");
        File.WriteAllText(Path.Combine(_dir, "other.txt"), "Something else entirely.");
        Video("clip.mp4", "640:360", 20);
        Video("clip small.mp4", "320:180", 32);

        var groups = await DuplicateFinder.FindAsync([_dir]);
        foreach (var g in groups)
            output.WriteLine($"{g.Kind}: {string.Join(", ", g.Files.Select(f => Path.GetFileName(f.Path)))}");

        string Names(DuplicateGroup g) => string.Join("|", g.Files.Select(f => Path.GetFileName(f.Path)).Order());
        var identical = groups.Where(g => g.Kind == DuplicateKind.Identical).Select(Names).ToList();
        Assert.Contains("beach copy.jpg|beach.jpg", identical);
        Assert.Contains("list.txt|list.txt", identical);
        var photos = Assert.Single(groups, g => g.Kind == DuplicateKind.SimilarPhotos);
        Assert.Equal(3, photos.Files.Count);
        Assert.Contains(photos.Files, f => f.Path.EndsWith("beach small.jpg"));
        Assert.Contains(photos.Files, f => f.Path.EndsWith("beach.png"));
        Assert.DoesNotContain(groups.SelectMany(g => g.Files), f => f.Path.EndsWith("forest.jpg") || f.Path.EndsWith("other.txt"));
        // The full-size photo is the one to keep.
        Assert.Equal((1600, 1200), (DuplicateFinder.Keeper(photos).Width, DuplicateFinder.Keeper(photos).Height));
        var videos = Assert.Single(groups, g => g.Kind == DuplicateKind.SimilarVideos);
        Assert.Equal("clip small.mp4|clip.mp4", Names(videos));
        Assert.EndsWith("clip.mp4", DuplicateFinder.Keeper(videos).Path);

        // Without the near-copy searches only identical files remain.
        var exact = await DuplicateFinder.FindAsync([_dir], new DuplicateOptions { SimilarPhotos = false, SimilarVideos = false });
        Assert.All(exact, g => Assert.Equal(DuplicateKind.Identical, g.Kind));
        Assert.Equal(2, exact.Count);
    }

    [Fact]
    public void Recycled_files_come_back_from_the_recycle_bin()
    {
        var path = Path.Combine(_dir, $"recycle me {Guid.NewGuid():N}.txt");
        File.WriteAllText(path, "Kept safe in the Recycle Bin.");

        RecycleBin.Send(path);
        Assert.False(File.Exists(path));

        var restored = RecycleBin.Restore([path]);
        Assert.Equal([path], restored);
        Assert.Equal("Kept safe in the Recycle Bin.", File.ReadAllText(path));
        // Nothing left to restore the second time.
        Assert.Empty(RecycleBin.Restore([path]));
    }
}
