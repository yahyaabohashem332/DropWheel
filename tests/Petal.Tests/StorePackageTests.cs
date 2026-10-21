using System.Text.RegularExpressions;
using System.Xml.Linq;
using Petal.Core.Formats;

namespace Petal.Tests;

/// <summary>The Store package's manifest, words and tiles agree with the app.</summary>
public sealed class StorePackageTests
{
    private static readonly string Store = Path.Combine(RepoRoot(), "tools", "store");
    private static readonly XNamespace Uap = "http://schemas.microsoft.com/appx/manifest/uap/windows10";
    private static readonly XNamespace Desktop = "http://schemas.microsoft.com/appx/manifest/desktop/windows10";

    private static XDocument Manifest() => XDocument.Load(Path.Combine(Store, "AppxManifest.xml"));

    [Fact]
    public void File_Explorer_offers_Petal_for_every_extension_it_reads()
    {
        var listed = Manifest().Descendants(Uap + "FileType").Select(e => e.Value.TrimStart('.')).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = Formats.All.Where(f => f.CanRead).SelectMany(f => f.Extensions).Where(e => !e.Contains('.') && !listed.Contains(e)).ToList();
        Assert.True(missing.Count == 0, "Run tools/store/make_manifest.py; missing: " + string.Join(", ", missing));
        Assert.All(listed, e => Assert.Matches(@"^[^.\\]+$", e));
    }

    [Fact]
    public void The_startup_task_is_the_one_the_app_asks_for()
    {
        var task = Manifest().Descendants(Desktop + "StartupTask").Single();
        Assert.Equal(Petal.App.Shell.Packaged.StartupTaskId, task.Attribute("TaskId")?.Value);
        Assert.Equal("false", task.Attribute("Enabled")?.Value);
    }

    [Fact]
    public void Every_word_the_manifest_names_is_there_in_English_and_Arabic()
    {
        var text = File.ReadAllText(Path.Combine(Store, "AppxManifest.xml"));
        var names = Regex.Matches(text, @"ms-resource:(\w+)").Select(m => m.Groups[1].Value).ToHashSet();
        Assert.NotEmpty(names);
        foreach (var language in new[] { "en-us", "ar-sa" })
        {
            var words = XDocument.Load(Path.Combine(Store, "Strings", language, "Resources.resw")).Root!.Elements("data")
                .ToDictionary(d => (string)d.Attribute("name")!, d => (string)d.Element("value")!);
            Assert.All(names, n => Assert.True(words.TryGetValue(n, out var value) && value.Trim().Length > 0, $"{language} has no {n}"));
        }
    }

    [Fact]
    public void Every_tile_the_manifest_names_has_its_images()
    {
        var text = File.ReadAllText(Path.Combine(Store, "AppxManifest.xml"));
        var images = Regex.Matches(text, @"Assets\\(\w+)\.png").Select(m => m.Groups[1].Value).Distinct().ToList();
        Assert.Contains("Square44x44Logo", images);
        foreach (var image in images)
            Assert.True(File.Exists(Path.Combine(Store, "Assets", $"{image}.scale-100.png")) && File.Exists(Path.Combine(Store, "Assets", $"{image}.scale-200.png")), image);
        Assert.True(File.Exists(Path.Combine(Store, "Assets", "Square44x44Logo.targetsize-48_altform-unplated.png")));
    }

    [Fact]
    public void The_identity_waits_for_Partner_Centre_values()
    {
        var text = File.ReadAllText(Path.Combine(Store, "AppxManifest.xml"));
        foreach (var placeholder in new[] { "$IDENTITY_NAME$", "$PUBLISHER$", "$PUBLISHER_DISPLAY_NAME$", "$VERSION$" })
            Assert.Contains(placeholder, text);
        Assert.Contains("runFullTrust", text);
        Assert.Contains("unvirtualizedResources", text);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "Petal.App")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("Petal's sources were not found above the test folder.");
    }
}
