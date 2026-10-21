using ImageMagick;
using Petal.App.Shell;
using Petal.Core.Jobs;
using Petal.Engines.Imaging;
using Petal.Recipes;

namespace Petal.Tests;

public class SharingTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("petal-sharing-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void App_limits_become_per_file_targets()
    {
        // WhatsApp: each file under its own limit; small files need no target at all.
        Assert.Equal(15_500_000, ShareTargets.WhatsApp.TargetFor(40_000_000, 40_000_000));
        Assert.Null(ShareTargets.WhatsApp.TargetFor(3_000_000, 3_000_000));

        // Email: one budget for everything, shared out by size.
        Assert.Equal(12_000_000, ShareTargets.Email.TargetFor(20_000_000, 30_000_000));
        Assert.Equal(6_000_000, ShareTargets.Email.TargetFor(10_000_000, 30_000_000));
        Assert.Null(ShareTargets.Email.TargetFor(5_000_000, 10_000_000));

        Assert.Same(ShareTargets.Discord, ShareTargets.Find("discord"));
        Assert.Same(ShareTargets.Upload, ShareTargets.Find("Upload form"));
        Assert.Null(ShareTargets.Find("myspace"));
    }

    [Theory]
    [InlineData("https://example.com/a?b=1", true)]
    [InlineData("mailto:me@example.com", true)]
    [InlineData("tel:+905551234567", true)]
    [InlineData("example.com", false)]
    [InlineData("see https://example.com", false)]
    [InlineData("line one\nhttps://example.com", false)]
    [InlineData("C:\\Users\\me\\file.txt", false)]
    public void Only_a_lone_link_becomes_a_qr_code(string text, bool expected) =>
        Assert.Equal(expected, ClipboardSource.IsLink(text));

    [Fact]
    public async Task Clean_copies_lose_the_location_but_keep_the_picture()
    {
        var photo = Path.Combine(_dir, "photo.jpg");
        using (var image = new MagickImage(new MagickColor(200, 120, 40), 320, 240))
            image.Write(photo);
        var located = Path.Combine(_dir, "located.jpg");
        await ImageMetadata.ApplyAsync(photo, located, [new MetadataChange.SetLocation(new GpsLocation(41.0082, 28.9784))]);
        Assert.True(await SharingCleaner.HasLocationAsync(located));

        var clean = Path.Combine(_dir, "located Clean.jpg");
        Assert.True(SharingCleaner.CanClean(located));
        await SharingCleaner.CleanAsync(located, clean, null, CancellationToken.None);

        Assert.False(await SharingCleaner.HasLocationAsync(clean));
        using var result = new MagickImage(clean);
        Assert.Equal((320u, 240u), (result.Width, result.Height));
        Assert.True(File.Exists(located), "The original stays as it was.");
        Assert.True(await SharingCleaner.HasLocationAsync(located));
    }

    [Fact]
    public async Task Command_line_fits_a_photo_for_an_upload_form()
    {
        var big = Path.Combine(_dir, "scan.jpg");
        using (var noise = new MagickImage("plasma:fractal", 3000, 2200))
        {
            noise.AddNoise(NoiseType.Gaussian);
            noise.Quality = 98;
            noise.Write(big);
        }
        Assert.True(new FileInfo(big).Length > 3_000_000);

        var output = new StringWriter();
        Assert.Equal(0, await new Petal.Cli.Cli(output, new StringWriter(), showProgress: false).RunAsync(["compress", big, "--for", "upload"], CancellationToken.None));
        var result = output.ToString().Trim();
        Assert.True(new FileInfo(result).Length <= 1_900_000, $"{new FileInfo(result).Length} bytes");
        using var image = new MagickImage(result);
        Assert.True(Math.Max(image.Width, image.Height) <= 2048);
    }
}
