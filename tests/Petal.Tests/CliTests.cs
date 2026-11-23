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

    [Theory]
    [InlineData("nothing-here.png", "doesn't exist")]
    [InlineData("*.nothing", "Nothing matches")]
    public async Task Nothing_to_work_on_ends_with_3(string name, string message)
    {
        Assert.Equal(3, await Run("convert", Path.Combine(_dir, name), "--to", "jpg"));
        Assert.Contains(message, _err.ToString());
    }

    [Fact]
    public async Task A_folder_gives_what_can_become_the_format_and_keeps_its_subfolders_under_out()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_dir, "shots")).FullName;
        Picture(Path.Combine("shots", "a.png"));
        Picture(Path.Combine("shots", "b.png"));
        File.WriteAllText(Path.Combine(folder, "notes.xyz"), "not a picture");
        Directory.CreateDirectory(Path.Combine(folder, "day 2"));
        Picture(Path.Combine("shots", "day 2", "c.png"));
        var outDir = Path.Combine(_dir, "out");

        Assert.Equal(0, await Run("convert", folder, "--to", "jpg", "--out", outDir));
        Assert.Equal([Path.Combine(outDir, "a.jpg"), Path.Combine(outDir, "b.jpg")], Printed);
        Assert.Contains("Skipped 1 file that can't become JPG", _err.ToString());

        _out.GetStringBuilder().Clear();
        Assert.Equal(0, await Run("convert", folder, "--to", "webp", "--subfolders", "--out", outDir));
        Assert.Contains(Path.Combine(outDir, "day 2", "c.webp"), Printed);
        Assert.True(File.Exists(Path.Combine(outDir, "day 2", "c.webp")));

        // A folder with nothing that can become the format: nothing to work on.
        var texts = Directory.CreateDirectory(Path.Combine(_dir, "texts")).FullName;
        File.WriteAllText(Path.Combine(texts, "a.xyz"), "x");
        Assert.Equal(3, await Run("convert", texts, "--to", "mp3"));
    }

    [Fact]
    public async Task A_favourite_from_settings_converts_with_its_size_names_and_folder()
    {
        var exports = Directory.CreateDirectory(Path.Combine(_dir, "exports")).FullName;
        var settings = Path.Combine(_dir, "settings.json");
        File.WriteAllText(settings, System.Text.Json.JsonSerializer.Serialize(new
        {
            Favourites = new object[]
            {
                new { Name = "Web", Format = "webp", LongestSide = 100, Quality = 70, Folder = exports, NamePattern = "{name} {preset}" },
                new { Name = "Webinar", Format = "mp4" },
                new { Name = "", Format = "jpg", LongestSide = 50 },
            },
        }));
        Petal.Cli.Cli.SettingsFileForTests = settings;
        try
        {
            var png = Picture("photo.png");
            Assert.Equal(0, await Run("convert", png, "--favourite", "web"));
            Assert.Equal(Path.Combine(exports, "photo Web.webp"), Printed.Single());
            using (var image = new MagickImage(Printed.Single()))
                Assert.Equal((100u, 50u), (image.Width, image.Height));

            // An option given as well changes it; an unnamed favourite goes by its format and size.
            _out.GetStringBuilder().Clear();
            Assert.Equal(0, await Run("convert", png, "--favourite", "JPG 50", "--max", "40", "--out", Path.Combine(_dir, "o")));
            using (var image = new MagickImage(Printed.Single()))
                Assert.Equal(40u, image.Width);

            Assert.Equal(2, await Run("convert", png, "--favourite", "Webx"));
            Assert.Contains("No favourite called", _err.ToString());
            Assert.Equal(2, await Run("convert", png, "--favourite", "We"));
            Assert.Contains("matches several", _err.ToString());

            _out.GetStringBuilder().Clear();
            Assert.Equal(0, await Run("favourites"));
            Assert.Equal(3, Printed.Length);
            Assert.StartsWith("Web: WEBP · 100 px · quality 70", Printed[0]);
            Assert.StartsWith("JPG 50: JPG · 50 px", Printed[2]);
        }
        finally
        {
            Petal.Cli.Cli.SettingsFileForTests = null;
        }
    }

    [Fact]
    public async Task Json_tells_what_was_made_and_what_failed()
    {
        var png = Picture("ok.png");
        var text = Path.Combine(_dir, "list.xyz");
        File.WriteAllText(text, "x");
        Assert.Equal(1, await Run("convert", png, text, "--to", "jpg", "--json"));
        using var json = System.Text.Json.JsonDocument.Parse(_out.ToString());
        var root = json.RootElement;
        Assert.Equal("convert", root.GetProperty("command").GetString());
        Assert.Equal(1, root.GetProperty("exitCode").GetInt32());
        Assert.False(root.GetProperty("ok").GetBoolean());
        var made = root.GetProperty("results").EnumerateArray().Single();
        Assert.Equal(png, made.GetProperty("input").GetString());
        Assert.Equal(Path.Combine(_dir, "ok.jpg"), made.GetProperty("outputs")[0].GetString());
        var failed = root.GetProperty("failures").EnumerateArray().Single();
        Assert.Equal(text, failed.GetProperty("input").GetString());
        Assert.Contains("doesn't know", failed.GetProperty("reason").GetString());
        // Nothing but the JSON goes out.
        Assert.Equal("", _err.ToString());

        _out.GetStringBuilder().Clear();
        Assert.Equal(2, await Run("--json", "bogus"));
        using var usage = System.Text.Json.JsonDocument.Parse(_out.ToString());
        Assert.Equal(2, usage.RootElement.GetProperty("exitCode").GetInt32());
        Assert.Contains("Unknown command", usage.RootElement.GetProperty("messages")[0].GetString());
    }

    [Fact]
    public async Task Watch_converts_each_new_file_once_and_leaves_its_own_results_alone()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_dir, "inbox")).FullName;
        Picture(Path.Combine("inbox", "before.png"));
        using var stop = new CancellationTokenSource();
        var watching = new Petal.Cli.Cli(_out, _err, showProgress: false).RunAsync(["watch", folder, "--to", "jpg", "--existing", "--json"], stop.Token);
        // A file arriving as a download does: written under another name, then renamed.
        var part = Path.Combine(folder, "after.png.crdownload");
        File.Move(Picture("after.png"), part);
        await Task.Delay(300);
        File.Move(part, Path.Combine(folder, "after.png"));
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline && !(File.Exists(Path.Combine(folder, "after.jpg")) && File.Exists(Path.Combine(folder, "before.jpg"))))
            await Task.Delay(200);
        await Task.Delay(1500);
        stop.Cancel();
        Assert.Equal(0, await watching);

        // Its own JPGs aren't converted again, and the download in progress wasn't taken.
        Assert.Equal(["after.jpg", "after.png", "before.jpg", "before.png"], Directory.GetFiles(folder).Select(Path.GetFileName).Order());
        var lines = _out.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        var events = lines.Select(l => System.Text.Json.JsonDocument.Parse(l).RootElement).Where(e => e.TryGetProperty("event", out _)).ToList();
        Assert.Equal(2, events.Count(e => e.GetProperty("event").GetString() == "done"));
        var report = System.Text.Json.JsonDocument.Parse(lines[^1]).RootElement;
        Assert.Equal("watch", report.GetProperty("command").GetString());
        Assert.Equal(2, report.GetProperty("results").GetArrayLength());
    }

    [Theory]
    [InlineData(@"C:\in\a.png.crdownload", true)]
    [InlineData(@"C:\in\~$report.docx", true)]
    [InlineData(@"C:\in\photo.jpg.petal-tmp", true)]
    [InlineData(@"C:\in\made.jpg", true)]
    [InlineData(@"C:\in\slides\1.png", true)]
    [InlineData(@"C:\in\new.png", false)]
    public void A_watch_leaves_alone_what_it_made_and_what_is_still_arriving(string file, bool ignored) =>
        Assert.Equal(ignored, Petal.Cli.Cli.Ignored(file, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C:\in\made.jpg", @"C:\in\slides" }));

    [Fact]
    public async Task Help_and_version()
    {
        Assert.Equal(0, await Run());
        Assert.Contains("petal convert", _out.ToString());
        Assert.Contains("petal watch", _out.ToString());
        Assert.Contains("Exit codes:", _out.ToString());
        Assert.Equal(0, await Run("version"));
        Assert.StartsWith("DropWheel ", Printed.Last());
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
