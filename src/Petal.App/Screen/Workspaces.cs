using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using Petal.App.Localization;
using Petal.App.Settings;
using Petal.App.Tools;
using Petal.App.Tools.Windows;
using Petal.App.Wheels;
using static Petal.App.Interop.NativeMethods;

namespace Petal.App.Screen;

/// <summary>One app's place in a workspace: which program, where its window sat, and whether it filled the screen.</summary>
public sealed class WorkspaceWindow
{
    public string Program { get; set; } = "";
    public string? ExePath { get; set; }
    public string Title { get; set; } = "";
    public int Left { get; set; }
    public int Top { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public bool Maximized { get; set; }
}

/// <summary>A named set of apps and where they go: "Work", "Study", "Editing".</summary>
public sealed class Workspace
{
    public string Id { get; set; } = WheelEntry.NewId();
    public string Name { get; set; } = "";
    public List<WorkspaceWindow> Windows { get; set; } = [];
}

/// <summary>
/// Workspaces: the apps you have open and where their windows sit, saved under a name; one petal opens
/// what isn't running and puts every window back in its place, maximised ones maximised.
/// </summary>
internal static class Workspaces
{
    /// <summary>The app windows open now, as a workspace would keep them.</summary>
    public static List<(RunningWindow Window, WorkspaceWindow Place)> Current()
    {
        var list = new List<(RunningWindow, WorkspaceWindow)>();
        foreach (var w in RunningApps.List())
        {
            if (!WindowArranger.CanArrange(w.Handle) || ScreenNative.IsIconic(w.Handle))
                continue;
            if (ScreenNative.DwmGetWindowAttribute(w.Handle, ScreenNative.DWMWA_EXTENDED_FRAME_BOUNDS, out var rect, Marshal.SizeOf<RECT>()) != 0)
                GetWindowRect(w.Handle, out rect);
            list.Add((w, new WorkspaceWindow
            {
                Program = w.Program,
                ExePath = w.ExePath,
                Title = w.Title,
                Left = rect.Left,
                Top = rect.Top,
                Width = rect.Width,
                Height = rect.Height,
                Maximized = ScreenNative.IsZoomed(w.Handle),
            }));
        }
        return list;
    }

    /// <summary>Opens what isn't running and arranges every window of the workspace.</summary>
    public static async Task OpenAsync(Workspace workspace)
    {
        var used = new HashSet<IntPtr>();
        int missing = 0;
        foreach (var place in workspace.Windows)
        {
            var window = Find(place, used);
            if (window == IntPtr.Zero && place.ExePath is { } exe && File.Exists(exe))
            {
                try
                {
                    Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
                {
                    Log.Error(ex, $"Opening {place.Program} for a workspace");
                }
                // Wait for its window.
                for (int i = 0; i < 40 && window == IntPtr.Zero; i++)
                {
                    await Task.Delay(250);
                    window = Find(place, used);
                }
            }
            if (window == IntPtr.Zero)
            {
                missing++;
                continue;
            }
            used.Add(window);
            WindowArranger.MoveTo(window, new RECT { Left = place.Left, Top = place.Top, Right = place.Left + place.Width, Bottom = place.Top + place.Height });
            if (place.Maximized)
                ScreenNative.ShowWindow(window, ScreenNative.SW_MAXIMIZE);
        }
        if (missing > 0)
            Hud.HudCard.ShowAt(Hud.HudCard.CornerPoint(), "", "").Succeed(L.F("{0} is ready", workspace.Name), L.P(missing, "{0} app couldn't be opened", "{0} apps couldn't be opened"), null);
    }

    /// <summary>A window of the same program not used yet: the one with the same title first.</summary>
    private static IntPtr Find(WorkspaceWindow place, HashSet<IntPtr> used)
    {
        var candidates = RunningApps.List().Where(w => !used.Contains(w.Handle) &&
            (place.ExePath is not null && string.Equals(w.ExePath, place.ExePath, StringComparison.OrdinalIgnoreCase) || string.Equals(w.Program, place.Program, StringComparison.OrdinalIgnoreCase))).ToList();
        return (candidates.FirstOrDefault(w => w.Title == place.Title) ?? candidates.FirstOrDefault())?.Handle ?? IntPtr.Zero;
    }

    /// <summary>The petal that opens a workspace, for a wheel.</summary>
    public static WheelEntry Entry(Workspace workspace) => new() { Kind = EntryKind.Workspace, Path = workspace.Id, Name = workspace.Name };
}

/// <summary>Saves the windows open now as a workspace, with a name, and puts its petal on the Arrange wheel.</summary>
internal sealed class SaveWorkspaceWindow : ToolWindow
{
    private readonly TextBox _name = new();
    private readonly StackPanel _windows = new();
    private readonly List<(CheckBox Box, WorkspaceWindow Place)> _found = [];

    public static void ShowSingle()
    {
        var window = new SaveWorkspaceWindow(Workspaces.Current().Select(w => w.Place).ToList()) { WindowStartupLocation = WindowStartupLocation.CenterScreen };
        window.Show();
        window.Activate();
    }

    /// <summary>With made-up windows, so a snapshot never shows what the user has open.</summary>
    internal static SaveWorkspaceWindow ForSnapshot(IReadOnlyList<WorkspaceWindow> windows) => new(windows);

    private SaveWorkspaceWindow(IReadOnlyList<WorkspaceWindow> windows) : base("Save Workspace", 460)
    {
        Body.Children.Add(Text("The apps open now and where their windows are. Open the workspace later and DropWheel opens what isn't running and puts every window back.", "Petal.Body"));
        _name.Text = L.F("Workspace {0}", SettingsStore.Current.Workspaces.Count + 1);
        Body.Children.Add(Row("Name", _name, 90, 14));
        foreach (var place in windows)
        {
            // Window titles read in their own direction, whatever the language of Petal.
            var line = place.Program + " — " + Short(place.Title);
            var box = new CheckBox
            {
                Content = new TextBlock { Text = line, FlowDirection = Petal.Core.Text.KeyboardLayout.LooksArabic(place.Title) ? FlowDirection.RightToLeft : FlowDirection.LeftToRight, TextTrimming = TextTrimming.CharacterEllipsis },
                IsChecked = true,
                Margin = new Thickness(0, 6, 0, 0),
            };
            _found.Add((box, place));
            _windows.Children.Add(box);
        }
        if (_found.Count == 0)
            _windows.Children.Add(Text("No app windows are open.", "Petal.Caption"));
        Body.Children.Add(new ScrollViewer { Content = _windows, MaxHeight = 240, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 10, 0, 0) });
        Body.Children.Add(Text("It appears under Workspaces on the Work wheel, and in the command palette.", "Petal.Caption").WithMarginT(10));
        PrimaryButton.Content = L.T("Save");
        PrimaryButton.IsEnabled = _found.Count > 0;
        PrimaryButton.Click += (_, _) => Save();
        Loaded += (_, _) =>
        {
            _name.Focus();
            _name.SelectAll();
        };
    }

    private static string Short(string title) => title.Length > 50 ? title[..50] + "…" : title;

    private void Save()
    {
        var places = _found.Where(f => f.Box.IsChecked == true).Select(f => f.Place).ToList();
        if (places.Count == 0 || _name.Text.Trim().Length == 0)
            return;
        var settings = SettingsStore.Current;
        var workspace = new Workspace { Name = _name.Text.Trim(), Windows = places };
        settings.Workspaces.Add(workspace);
        SettingsStore.Save();
        Close();
        Hud.HudCard.ShowAt(Hud.HudCard.CornerPoint(), "", "").Succeed(L.F("{0} saved", workspace.Name), L.P(places.Count, "{0} window", "{0} windows"), null);
    }
}
