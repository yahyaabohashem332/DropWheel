namespace Petal.Tests;

/// <summary>Nothing a test run writes lands among the user's own files.</summary>
public sealed class TestIsolationTests
{
    [Fact]
    public void Settings_history_statistics_memory_petals_and_the_log_go_to_the_test_folder()
    {
        var temp = Path.GetFullPath(Path.GetTempPath());
        var paths = new[]
        {
            Petal.App.Settings.SettingsStore.Directory,
            Petal.App.Jobs.History.FilePath,
            Petal.App.Stats.Statistics.FilePath,
            Petal.App.Overlay.WheelMemory.FilePath,
            Petal.App.Log.FilePath,
            Petal.Recipes.CustomPetal.Folder,
        };
        Assert.All(paths, p => Assert.StartsWith(temp, Path.GetFullPath(p), StringComparison.OrdinalIgnoreCase));
    }
}
