using System.Runtime.CompilerServices;
using Petal.App.Localization;

namespace Petal.Tests;

internal static class TestSetup
{
    /// <summary>
    /// Tests expect English interface text whatever the PC's display language; the
    /// localization tests switch to Arabic themselves.
    /// </summary>
    [ModuleInitializer]
    internal static void UseEnglish() => L.Configure(UiLanguage.English);

    /// <summary>
    /// What Petal keeps between runs goes to a folder of the test run's own, never into the user's: the
    /// history, the statistics, what the wheel remembers and the activity log. Tests that check these set their own files too.
    /// </summary>
    [ModuleInitializer]
    internal static void KeepOutOfTheUsersFiles()
    {
        var folder = Path.Combine(Path.GetTempPath(), "petal-tests-" + Environment.ProcessId);
        Directory.CreateDirectory(folder);
        Petal.App.Settings.SettingsStore.Directory = folder;
        Petal.App.Jobs.History.FilePath = Path.Combine(folder, "history.json");
        Petal.App.Stats.Statistics.FilePath = Path.Combine(folder, "statistics.json");
        Petal.App.Overlay.WheelMemory.FilePath = Path.Combine(folder, "wheel-memory.json");
        Petal.App.Log.FilePath = Path.Combine(folder, "petal.log");
        Petal.Recipes.CustomPetal.Folder = Path.Combine(folder, "petals");
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { Directory.Delete(folder, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        };
    }
}
