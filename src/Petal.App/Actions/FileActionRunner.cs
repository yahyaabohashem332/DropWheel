using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.VisualBasic.FileIO;
using Microsoft.Win32;
using Petal.App.Hud;
using Petal.App.Input;
using Petal.App.Shell;
using Petal.Core.Actions;
using Petal.Core.Util;
using Petal.App.Localization;

namespace Petal.App.Actions;

/// <summary>The actions wheel: Share, Send to Phone, Clean for Sharing, Float, Copy, Move, Rename, ZIP, Copy Path and Terminal.</summary>
public sealed class FileActionRunner(Jobs.JobRunner jobs)
{
    private FloatShelf? _shelf;

    public Task RunAsync(FileActionInfo action, IReadOnlyList<string> paths, ScreenPoint at)
    {
        switch (action.Id)
        {
            case FileActionId.Copy:
                CopyFiles(paths);
                Toast(at, paths.Count == 1 ? L.T("Copied") : L.P(paths.Count, "Copied {0} item", "Copied {0} items"), L.T("Paste in File Explorer with Ctrl+V"));
                break;
            case FileActionId.CopyPath:
                Clipboard.SetText(string.Join(Environment.NewLine, paths));
                Toast(at, paths.Count == 1 ? L.T("Path copied") : L.P(paths.Count, "{0} path copied", "{0} paths copied"), paths.Count == 1 ? paths[0] : L.T("One path per line"));
                break;
            case FileActionId.Move:
                Move(paths, at);
                break;
            case FileActionId.Terminal:
                OpenTerminal(paths[0]);
                break;
            case FileActionId.Share:
                ShareSheet.Show(paths);
                break;
            case FileActionId.Phone:
                PhoneWindow.ShowFor(paths, at);
                break;
            case FileActionId.Clean:
                return CleanAsync(paths, at);
            case FileActionId.Rename:
                new Tools.Windows.RenameWindow(paths, at).ShowNear(at);
                break;
            case FileActionId.Zip:
                new Tools.Windows.ZipWindow(paths, jobs, at).ShowNear(at);
                break;
            case FileActionId.Ask:
                Tools.Windows.AskWindow.Show(paths, jobs, at);
                break;
            case FileActionId.Duplicates:
                Tools.Windows.DuplicatesWindow.Show(paths, at);
                break;
            case FileActionId.Float:
                _shelf ??= new FloatShelf();
                _shelf.Closed += (_, _) => _shelf = null;
                _shelf.AddAndShow(paths, at);
                break;
        }
        return Task.CompletedTask;
    }

    /// <summary>Clean copies ("photo Clean.jpg") beside each file; folders and unsupported files are skipped.</summary>
    private Task CleanAsync(IReadOnlyList<string> paths, ScreenPoint at)
    {
        var files = paths.Where(File.Exists).ToList();
        var work = files.Where(Petal.Recipes.SharingCleaner.CanClean).Select(file => new Jobs.WorkItem(file, Path.GetFileName(file), async (p, ct) =>
        {
            var output = Petal.Core.Jobs.OutputNaming.WithSuffix(file, "Clean", Petal.Core.Jobs.OutputNaming.ExtensionOf(file));
            await Petal.Recipes.SharingCleaner.CleanAsync(file, output, p, ct);
            return (IReadOnlyList<string>)[output];
        })).ToList();
        if (work.Count == 0)
        {
            Toast(at, L.T("Nothing to clean"), L.T("Photos, videos, audio, PDFs and Office files can be cleaned."));
            return Task.CompletedTask;
        }
        return jobs.RunAsync("Cleaning for sharing", "Cleaned for sharing", work, at);
    }

    private static void Toast(ScreenPoint at, string title, string subtitle)
    {
        var card = HudCard.ShowAt(at, title, subtitle);
        card.Succeed(title, subtitle, null);
    }

    private static void CopyFiles(IReadOnlyList<string> paths)
    {
        var list = new StringCollection();
        list.AddRange(paths.ToArray());
        var data = new DataObject();
        data.SetFileDropList(list);
        // DROPEFFECT_COPY, so pasting copies rather than moves.
        data.SetData("Preferred DropEffect", new MemoryStream([5, 0, 0, 0]));
        Clipboard.SetDataObject(data, copy: true);
    }

    private static void Move(IReadOnlyList<string> paths, ScreenPoint at)
    {
        var dialog = new OpenFolderDialog
        {
            Title = paths.Count == 1 ? L.F("Move “{0}” to", Path.GetFileName(paths[0])) : L.P(paths.Count, "Move {0} item to", "Move {0} items to"),
            InitialDirectory = Path.GetDirectoryName(paths[0]),
        };
        if (dialog.ShowDialog() != true)
            return;
        var destination = dialog.FolderName;
        var moved = new List<string>();
        foreach (var path in paths)
        {
            var target = Path.Combine(destination, Path.GetFileName(path));
            try
            {
                // Windows' own copy engine: progress, conflict prompts and Ctrl+Z support in Explorer.
                if (Directory.Exists(path))
                    FileSystem.MoveDirectory(path, target, UIOption.AllDialogs, UICancelOption.DoNothing);
                else
                    FileSystem.MoveFile(path, target, UIOption.AllDialogs, UICancelOption.DoNothing);
                if (File.Exists(target) || Directory.Exists(target))
                    moved.Add(target);
            }
            catch (Exception ex)
            {
                Log.Error(ex, $"Moving {path}");
            }
        }
        if (moved.Count > 0)
        {
            var card = HudCard.ShowAt(at, "Moved", "");
            card.Succeed(moved.Count == 1 ? L.T("Moved") : L.P(moved.Count, "Moved {0} item", "Moved {0} items"), Path.GetFileName(destination), () => ExplorerSelection.Reveal(moved));
        }
    }

    private static void OpenTerminal(string path)
    {
        var folder = Directory.Exists(path) ? path : Path.GetDirectoryName(path) ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var wt = ToolLocator.Find("wt.exe", @"%LOCALAPPDATA%\Microsoft\WindowsApps\wt.exe");
        try
        {
            if (wt is not null)
            {
                Process.Start(new ProcessStartInfo(wt) { ArgumentList = { "-d", folder }, UseShellExecute = false });
                return;
            }
            var pwsh = ToolLocator.Find("pwsh.exe") ?? "powershell.exe";
            Process.Start(new ProcessStartInfo(pwsh) { ArgumentList = { "-NoExit", "-NoLogo" }, WorkingDirectory = folder, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Opening terminal");
            Process.Start(new ProcessStartInfo("cmd.exe") { WorkingDirectory = folder, UseShellExecute = true });
        }
    }
}
