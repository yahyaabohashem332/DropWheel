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

namespace Petal.App.Actions;

/// <summary>The actions wheel: Share, Float, Copy, Move, Copy Path and Terminal.</summary>
public sealed class FileActionRunner
{
    private FloatShelf? _shelf;

    public Task RunAsync(FileActionInfo action, IReadOnlyList<string> paths, ScreenPoint at)
    {
        switch (action.Id)
        {
            case FileActionId.Copy:
                CopyFiles(paths);
                Toast(at, paths.Count == 1 ? "Copied" : $"Copied {paths.Count} items", "Paste in File Explorer with Ctrl+V");
                break;
            case FileActionId.CopyPath:
                Clipboard.SetText(string.Join(Environment.NewLine, paths));
                Toast(at, paths.Count == 1 ? "Path copied" : $"{paths.Count} paths copied", paths.Count == 1 ? paths[0] : "One path per line");
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
            case FileActionId.Float:
                _shelf ??= new FloatShelf();
                _shelf.Closed += (_, _) => _shelf = null;
                _shelf.AddAndShow(paths, at);
                break;
        }
        return Task.CompletedTask;
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
            Title = paths.Count == 1 ? $"Move “{Path.GetFileName(paths[0])}” to" : $"Move {paths.Count} items to",
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
            card.Succeed(moved.Count == 1 ? "Moved" : $"Moved {moved.Count} items", Path.GetFileName(destination), () => ExplorerSelection.Reveal(moved));
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
