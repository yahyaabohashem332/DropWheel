using ImageMagick;
using Petal.Cli;

namespace Petal.Tests;

public class CliTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("petal-cli-").FullName;
    private readonly StringWriter _out = new(), _err = new();

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private Task<int> Run(params string[] args) => new Cli.Cli(_out, _err, showProgress: false).RunAsync(args, CancellationToken.None);

    private string Picture(string name, uint width = 400, uint height = 200)
    {
        var path = Path.Combine(_dir, name);
        using var image = new MagickImage(new MagickColor(30, 120, 220), width, height);
        image.Write(path);
        return path;
    }

    private string[] Printed => _out.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

    [Fact]
    public async Task Converts_with_wildcards_and_prints_each_result()
    {
        Picture("a.png");
        Picture("b.png");
        var outDir = Path.Combine(_dir, "out");

        Assert.Equal(0, await Run("convert", Path.Combine(_dir, "*.png"), "--to", "jpeg", "--max", "100", "--out", outDir));

        Assert.Equal([Path.Combine(outDir, "a.jpg"), Path.Combine(outDir, "b.jpg")], Printed);
        using var small = new MagickImage(Path.Combine(outDir, "a.jpg"));
        Assert.Equal((100u, 50u), (small.Width, small.Height));
        Assert.Equal("", _err.ToString());
    }

    [Fact]
    public async Task A_smaller_copy_in_the_same_format_needs_a_size_or_quality()
    {
        var png = Picture("same.png");
        Assert.Equal(1, await Run("convert", png, "--to", "png"));
        Assert.Contains("already PNG", _err.ToString());

        Assert.Equal(0, await Run("convert", png, "--to", "png", "--max", "50"));
        Assert.Equal(Path.Combine(_dir, "same 50px.png"), Printed.Single());
    }

    [Fact]
    public async Task Resizes_and_zips()
    {
        var png = Picture("photo.png");
        Assert.Equal(0, await Run("resize", png, "--percent", "50", "--to", "webp"));
        var resized = Printed.Single();
        Assert.EndsWith(".webp", resized);
        using (var image = new MagickImage(resized))
            Assert.Equal((200u, 100u), (image.Width, image.Height));

        Assert.Equal(0, await Run("zip", png, resized, "-o", Path.Combine(_dir, "both")));
        Assert.Equal(Path.Combine(_dir, "both.zip"), Printed.Last());
        using var zip = System.IO.Compression.ZipFile.OpenRead(Path.Combine(_dir, "both.zip"));
        Assert.Equal(2, zip.Entries.Count);
    }

    [Fact]
    public async Task A_qr_code_reads_back()
    {
        var code = Path.Combine(_dir, "code.png");
        Assert.Equal(0, await Run("qr", "https://example.com/صفحة", "-o", code));
        Assert.Equal(0, await Run("codes", code));
        Assert.Equal("https://example.com/صفحة", Printed.Last());

        // Without a file it is drawn in the terminal: two rows of modules per line.
        var drawn = Petal.Cli.Cli.Terminal(Petal.Engines.Imaging.QrGenerator.Encode("hi"));
        var lines = drawn.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(21 + 4, lines[0].Length);
        Assert.Equal((21 + 4 + 1) / 2, lines.Length);
    }

    [Theory]
    [InlineData(new[] { "bogus" }, "Unknown command")]
    [InlineData(new[] { "convert", "nothing-here.png", "--to", "jpg" }, "doesn't exist")]
    [InlineData(new[] { "convert", "--to" }, "--to needs a value")]
    [InlineData(new[] { "resize", "x.png", "--max", "10", "--width", "5" }, "exactly one of")]
    [InlineData(new[] { "convert", "x.png", "--to", "jpg", "--qualty", "5" }, "Unknown option --qualty")]
    [InlineData(new[] { "convert", "x.png", "--to", "nope" }, "isn't a format")]
    public async Task Typing_mistakes_explain_themselves(string[] args, string message)
    {
        Picture("x.png");
        var full = args.Select(a => a == "x.png" ? Path.Combine(_dir, a) : a).ToArray();
        Assert.Equal(2, await Run(full));
        Assert.Contains(message, _err.ToString());
    }

    [Fact]
    public async Task Help_and_version()
    {
        Assert.Equal(0, await Run());
        Assert.Contains("petal convert", _out.ToString());
        Assert.Equal(0, await Run("version"));
        Assert.StartsWith("Petal ", Printed.Last());
    }

    [Fact]
    public void Uses_the_quality_chosen_in_the_app()
    {
        var settings = Path.Combine(_dir, "settings.json");
        File.WriteAllText(settings, """{ "Theme": "Dark", "Conversion": { "ImageQuality": 72, "VideoCrf": 26 } }""");
        var defaults = Petal.Cli.Cli.UserDefaults(settings);
        Assert.Equal((72, 26), (defaults.ImageQuality, defaults.VideoCrf));

        File.WriteAllText(settings, "{ not json");
        Assert.Equal(90, Petal.Cli.Cli.UserDefaults(settings).ImageQuality);
        Assert.Equal(90, Petal.Cli.Cli.UserDefaults(Path.Combine(_dir, "missing.json")).ImageQuality);
    }
}
