using System.Windows;
using Microsoft.Win32;
using Petal.App.Input;
using Petal.App.Localization;
using Petal.App.Overlay;
using Petal.App.Wheels;
using Petal.Core.Actions;
using Petal.Core.Tools;
using static Petal.App.Interop.NativeMethods;

namespace Petal.App.Guide;

/// <summary>
/// "Try it" in the Tools Guide: an action of the wheels without files runs at once (as its petal would); a tool or
/// file action asks for the files (or the folder) to work on first, then runs as a drop on its petal.
/// </summary>
public static class GuideRunner
{
    /// <summary>Starts what an entry is about; the reason when it can't, or null.</summary>
    public static async Task<string?> RunAsync(string key, Window? owner)
    {
        var (kind, id) = GuideBook.Split(key);
        if (App.Current?.Controller is not { } controller)
            return null;
        GetCursorPos(out var p);
        var at = new ScreenPoint(p.X, p.Y);
        switch (kind)
        {
            case "do" when WheelActions.Get(id) is not null:
                // The guide steps aside first, as the wheel does, so a screenshot or a timer starts on what was under it.
                owner?.Hide();
                await Task.Delay(180);
                try
                {
                    await EntryRunner.RunAsync(Rings.Item(new WheelEntry { Kind = EntryKind.Action, Action = id }), at, GetForegroundWindow(), controller);
                }
                finally
                {
                    owner?.Show();
                }
                return null;

            case "tool" when Enum.TryParse<ToolId>(id, out var toolId):
            {
                var tool = ToolRegistry.Get(toolId);
                var files = tool.Selection == ToolSelection.Folder ? ChooseFolder(owner) : ChooseFiles(owner, tool.Selection != ToolSelection.Single);
                if (files.Count == 0)
                    return null;
                if (tool.Selection == ToolSelection.MultipleOnly && files.Count < 2)
                    return L.F("{0} needs at least two files.", L.T(tool.Name));
                return controller.TryOnFiles(WheelItem.From(tool), files, at);
            }

            case "action" when Enum.TryParse<FileActionId>(id, out var actionId) && FileActionRegistry.All.FirstOrDefault(a => a.Id == actionId) is { } action:
            {
                var files = ChooseFiles(owner, multiple: true);
                return files.Count == 0 ? null : controller.TryOnFiles(WheelItem.From(action), files, at);
            }

            default:
                return null;
        }
    }

    private static IReadOnlyList<string> ChooseFiles(Window? owner, bool multiple)
    {
        var dialog = new OpenFileDialog { Multiselect = multiple, Title = L.T("Choose the files to work on") };
        return dialog.ShowDialog(owner) == true ? dialog.FileNames : [];
    }

    private static IReadOnlyList<string> ChooseFolder(Window? owner)
    {
        var dialog = new OpenFolderDialog { Title = L.T("Choose the folder to work on") };
        return dialog.ShowDialog(owner) == true ? [dialog.FolderName] : [];
    }
}
