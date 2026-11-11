using System.IO;
using System.Windows;
using Petal.App.Localization;
using Petal.App.Settings;
using Petal.App.Util;
using Petal.Core.Models;

namespace Petal.App.Tools.Parts;

/// <summary>
/// Choosing the folder downloaded AI models are kept in: Windows' folder dialog, whether the folder can take them, and
/// moving those already downloaded there, after which the choice is kept in the settings.
/// </summary>
internal static class ModelFolderChooser
{
    /// <summary>Asks for a folder, starting at the one in use; null when none was chosen.</summary>
    public static string? Ask(Window? owner)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = L.T("Choose where AI models are kept"),
            InitialDirectory = Directory.Exists(ModelStore.Folder) ? ModelStore.Folder : Path.GetDirectoryName(ModelStore.Folder) ?? "",
        };
        return dialog.ShowDialog(owner) == true ? dialog.FolderName : null;
    }

    /// <summary>What stands in the way of keeping models in a folder, in words; null when nothing does.</summary>
    public static string? Problem(string folder) => ModelStore.Check(folder) switch
    {
        null => null,
        { Kind: ModelFolderProblemKind.NoSpace } p => L.F("That drive hasn't room for the models: they take {0}, and {1} is free.", Sizes.Format(p.Needed), Sizes.Format(p.Free)),
        _ => L.T("DropWheel can't keep models in that folder. Choose another one."),
    };

    /// <summary>
    /// Moves the models already downloaded to <paramref name="folder"/> and keeps them there from now on. Throws what
    /// the move throws, having moved nothing.
    /// </summary>
    public static async Task MoveAsync(string folder, IProgress<double> progress, CancellationToken cancellationToken)
    {
        await Task.Run(() => ModelStore.MoveAsync(folder, progress, cancellationToken), cancellationToken);
        var settings = SettingsStore.Current;
        settings.ModelsFolder = string.Equals(ModelStore.Folder, ModelStore.DefaultFolder, StringComparison.OrdinalIgnoreCase) ? "" : ModelStore.Folder;
        SettingsStore.Save();
    }

    /// <summary>
    /// A long folder shortened in the middle, where it says least: its drive and its last two folders
    /// (C:\…\Petal\models), as the end of a path is what tells it apart.
    /// </summary>
    public static string Short(string path, int longest = 44)
    {
        if (path.Length <= longest)
            return path;
        var root = Path.GetPathRoot(path) ?? "";
        var parts = path[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length <= 2 ? path : root + "…" + Path.DirectorySeparatorChar + string.Join(Path.DirectorySeparatorChar, parts[^2..]);
    }

    /// <summary>Words for a move that failed (and so moved nothing).</summary>
    public static string Failure(Exception error) => error switch
    {
        IOException or UnauthorizedAccessException => L.T("The models weren't moved: one of them may be in use. Close the AI tools and try again."),
        _ => L.T(error.Message),
    };
}
