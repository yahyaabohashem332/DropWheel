using Petal.Engines.Media;

namespace Petal.Tests;

public sealed class ReadAloudTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("petal-read-aloud-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task Command_line_reads_a_text_file_into_an_mp3()
    {
        if (!Speech.WindowsVoices().Any(v => v.Speaks("en")))
            return;
        var file = Path.Combine(_dir, "notes.txt");
        await File.WriteAllTextAsync(file, "Shopping list.\n\nApples, bread and milk.");
        var output = new StringWriter();
        var error = new StringWriter();

        int code = await new Petal.Cli.Cli(output, error, showProgress: false).RunAsync(["speak", file, "--speed", "1.25"], CancellationToken.None);

        Assert.True(code == 0, error.ToString());
        var mp3 = output.ToString().Trim();
        Assert.Equal(Path.Combine(_dir, "notes Audio.mp3"), mp3);
        Assert.True(new FileInfo(mp3).Length > 4000);
    }

    [Fact]
    public async Task Command_line_explains_a_missing_voice()
    {
        var file = Path.Combine(_dir, "empty.txt");
        await File.WriteAllTextAsync(file, "   ");
        var error = new StringWriter();
        Assert.NotEqual(0, await new Petal.Cli.Cli(new StringWriter(), error, showProgress: false).RunAsync(["speak", file], CancellationToken.None));
        Assert.Contains("no text", error.ToString());

        Assert.NotEqual(0, await new Petal.Cli.Cli(new StringWriter(), error, showProgress: false).RunAsync(["speak", file, "--voice", "Nobody"], CancellationToken.None));
    }

    [Fact]
    public async Task Documents_give_up_their_text()
    {
        var converters = Petal.App.EngineSetup.CreateConverters();
        var md = Path.Combine(_dir, "readme.md");
        await File.WriteAllTextAsync(md, "# Title\n\nSome **bold** words and a [link](https://example.com).");
        var text = await Petal.Recipes.DocumentText.ReadAsync(md, converters, new Petal.Core.Jobs.ConversionSettings());
        Assert.Contains("Title", text);
        Assert.Contains("bold words", text);
        Assert.DoesNotContain("**", text);
        Assert.DoesNotContain("https://example.com", text);
    }
}
