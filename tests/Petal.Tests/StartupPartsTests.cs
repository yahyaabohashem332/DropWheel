using Petal.App;
using Petal.App.Localization;

namespace Petal.Tests;

/// <summary>
/// Start-up goes on part by part: one that fails is kept with its reason for the card and DropWheel Health,
/// and the parts after it still start. Before, the first failure silently left DropWheel without its icon,
/// its keys and its shortcuts.
/// </summary>
[Collection("Language")]
public sealed class StartupPartsTests
{
    [Fact]
    public void APartThatFailsIsKeptAndTheNextStillStarts()
    {
        StartupParts.Forget();
        try
        {
            bool first = StartupParts.Run("Clipboard History", () => throw new FormatException("Unexpected token 'M 8,3,4'\nat somewhere"));
            bool afterIt = false;
            bool second = StartupParts.Run("Quick Look", () => afterIt = true);

            Assert.False(first);
            Assert.True(second);
            Assert.True(afterIt);
            var failure = Assert.Single(StartupParts.Failed);
            Assert.Equal("Clipboard History", failure.Part);
            Assert.Equal("FormatException: Unexpected token 'M 8,3,4'", failure.Reason);
        }
        finally
        {
            StartupParts.Forget();
        }
    }

    [Fact]
    public void PartsAreNamedOnceInTheInterfaceLanguage()
    {
        L.Configure(UiLanguage.English);
        Assert.Equal("Shortcuts, Quick Look", StartupParts.Names(["Shortcuts", "Quick Look", "Shortcuts"]));
        L.Configure(UiLanguage.Arabic);
        try
        {
            Assert.Equal(L.T("Shortcuts") + "، " + L.T("Quick Look"), StartupParts.Names(["Shortcuts", "Quick Look"]));
            Assert.NotEqual("Shortcuts", L.T("Shortcuts"));
        }
        finally
        {
            L.Configure(UiLanguage.English);
        }
    }

    [Fact]
    public void EveryPartStartUpNamesIsTranslated()
    {
        // The names given to StartupParts.Run in App.xaml.cs, which the card and Health show.
        var app = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Petal.App", "App.xaml.cs"));
        var names = System.Text.RegularExpressions.Regex.Matches(app, @"StartupParts\.Run\(""([^""]+)""").Select(m => m.Groups[1].Value).Distinct().ToList();
        Assert.True(names.Count >= 15, $"Only {names.Count} parts were found.");
        Assert.Empty(names.Where(n => !L.HasTranslation(n)));
    }

    [Fact]
    public void TheHeartOfDropWheelFailsPlainlyAndEverythingAfterItIsAPart()
    {
        var app = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Petal.App", "App.xaml.cs"));
        int core = app.IndexOf("_controller.Start();", StringComparison.Ordinal);
        int plain = app.IndexOf("CouldNotStart(ex);", StringComparison.Ordinal);
        int started = app.IndexOf("Log.Info($\"DropWheel started", StringComparison.Ordinal);
        Assert.True(core > 0 && plain > core && started > plain);
        // The icon, the shortcuts and the clipboard history each start on their own after it.
        var after = app[plain..started];
        Assert.Contains("StartupParts.Run(\"The icon by the clock\", CreateTray);", after);
        Assert.Contains("StartupParts.Run(\"Shortcuts\", () =>", after);
        Assert.Contains("StartupParts.Run(\"Clipboard History\", Clips.ClipboardHistory.Start);", after);
        Assert.DoesNotContain("\n        CreateTray();", after);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "Petal.App")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("Petal's sources were not found above the test folder.");
    }
}
