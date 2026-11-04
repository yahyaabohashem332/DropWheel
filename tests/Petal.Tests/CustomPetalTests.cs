using System.Security.Cryptography;
using Petal.Recipes;

namespace Petal.Tests;

[Collection("Models")]
public sealed class CustomPetalTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("petal-custom-").FullName;
    private readonly string _saved = CustomPetal.Folder;

    public CustomPetalTests() => CustomPetal.Folder = Path.Combine(_dir, "petals");

    public void Dispose()
    {
        CustomPetal.Folder = _saved;
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task The_example_petal_saves_a_hex_dump()
    {
        var manifest = CustomPetal.WriteExample();
        var petal = Assert.Single(CustomPetal.LoadAll());
        Assert.Null(petal.Problem);
        Assert.Equal(manifest, petal.File);
        Assert.Equal("عرض البايتات", petal.DisplayName(arabic: true));

        // A name a shell would take for two commands is just a name here.
        var file = Path.Combine(_dir, "report & calc.txt");
        File.WriteAllText(file, "Petal");
        Assert.True(petal.Takes([file]));
        var run = Assert.Single(petal.Plan([file]));
        Assert.Equal(Path.Combine(_dir, "report & calc hex.txt"), run.Stdout);
        Assert.Contains("\"" + file + "\"", run.CommandLine);

        var made = await CustomPetal.RunAsync(run);
        Assert.Contains("50 65 74 61 6c", File.ReadAllText(Assert.Single(made)).ToLowerInvariant());
    }

    [Fact]
    public void Broken_petals_say_whats_wrong()
    {
        Directory.CreateDirectory(CustomPetal.Folder);
        File.WriteAllText(Path.Combine(CustomPetal.Folder, "a.json"), """{ "name": "No program", "accepts": ["png"] }""");
        File.WriteAllText(Path.Combine(CustomPetal.Folder, "b.json"), """{ "name": "Missing", "accepts": ["png"], "program": "C:\\nowhere\\tool.exe" }""");
        File.WriteAllText(Path.Combine(CustomPetal.Folder, "c.json"), "{ not json");
        File.WriteAllText(Path.Combine(CustomPetal.Folder, "d.json"), """{ "name": "Mixed", "accepts": ["any"], "program": "certutil", "each": false, "arguments": ["{input}"] }""");
        var petals = CustomPetal.LoadAll();
        Assert.Equal(4, petals.Count);
        Assert.All(petals, p => Assert.NotNull(p.Problem));
        Assert.Contains("program", petals[0].Problem);
        Assert.Contains("wasn't found", petals[1].Problem);
        Assert.False(petals[0].Takes([Path.Combine(_dir, "x.png")]));
    }

    [Fact]
    public void All_files_go_to_one_run_when_asked()
    {
        Directory.CreateDirectory(CustomPetal.Folder);
        var manifest = Path.Combine(CustomPetal.Folder, "join.json");
        File.WriteAllText(manifest, """
            // Comments are allowed.
            { "name": "List", "accepts": ["image", "pdf"], "program": "certutil", "each": false,
              "arguments": ["-dump", "{inputs}"], "stdout": "{name} list.txt" }
            """);
        var petal = CustomPetal.Load(manifest);
        Assert.Null(petal.Problem);
        var files = new[] { Path.Combine(_dir, "a.jpg"), Path.Combine(_dir, "b.pdf") };
        Assert.True(petal.Takes(files));
        Assert.False(petal.Takes([Path.Combine(_dir, "song.mp3")]));
        var run = Assert.Single(petal.Plan(files));
        Assert.Equal(["-dump", files[0], files[1]], run.Arguments);
        // Changing the file changes its fingerprint, so a first-run approval doesn't carry over.
        File.AppendAllText(manifest, " ");
        Assert.NotEqual(petal.Fingerprint, CustomPetal.Load(manifest).Fingerprint);
    }
}
