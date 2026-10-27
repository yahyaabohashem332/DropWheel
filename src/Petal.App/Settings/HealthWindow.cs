using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Petal.App.Localization;
using Petal.App.Tools;

namespace Petal.App.Settings;

/// <summary>Petal Health: what Petal found when it looked itself over, with fixes, and a report to save.</summary>
internal sealed class HealthWindow : ToolWindow
{
    private static HealthWindow? _open;
    private readonly StackPanel _list = new();
    private IReadOnlyList<HealthCheck> _checks = [];

    public static void ShowSingle()
    {
        if (_open is not null)
        {
            _open.Activate();
            return;
        }
        _open = new HealthWindow { WindowStartupLocation = WindowStartupLocation.CenterScreen };
        _open.Closed += (_, _) => _open = null;
        _open.Show();
        _open.Activate();
    }

    /// <summary>With checks given rather than run, for design snapshots.</summary>
    internal static HealthWindow ForSnapshot(IReadOnlyList<HealthCheck> checks)
    {
        var window = new HealthWindow(run: false);
        window.Show(checks);
        return window;
    }

    private HealthWindow(bool run = true) : base("DropWheel Health", 600)
    {
        Body.Children.Add(Text("DropWheel looked itself over. Nothing is changed unless you choose a fix.", "Petal.Caption"));
        Body.Children.Add(new ScrollViewer { Content = _list, MaxHeight = 520, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 10, 0, 0) });
        ResetButton.Content = L.T("Save a Report…");
        ResetButton.Visibility = Visibility.Visible;
        ResetButton.Click += (_, _) => SaveReport();
        PrimaryButton.Content = L.T("Check Again");
        PrimaryButton.Click += async (_, _) => await CheckAsync();
        if (run)
            Loaded += async (_, _) => await CheckAsync();
    }

    private async Task CheckAsync()
    {
        PrimaryButton.IsEnabled = false;
        FooterNote = "Checking…";
        try
        {
            Show(await Health.RunAsync());
            FooterNote = "";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "DropWheel Health");
            FooterNote = "DropWheel couldn't finish looking";
        }
        finally
        {
            PrimaryButton.IsEnabled = true;
        }
    }

    private void Show(IReadOnlyList<HealthCheck> checks)
    {
        _checks = checks;
        _list.Children.Clear();
        foreach (var check in checks)
            _list.Children.Add(Row(check));
    }

    private UIElement Row(HealthCheck check)
    {
        var (icon, brush) = check.State switch
        {
            HealthState.Good => ("check", "Petal.Success"),
            HealthState.Note => ("info", "Petal.Accent"),
            _ => ("alert", "Petal.Danger"),
        };
        var mark = ThemedIcon(icon, 18, brush);
        mark.Margin = new Thickness(0, 2, 12, 0);
        mark.VerticalAlignment = VerticalAlignment.Top;
        var area = Text(check.Area, "Petal.Body");
        area.FontWeight = FontWeights.SemiBold;
        var words = new StackPanel { Children = { area } };
        var summary = new TextBlock { Text = check.Summary, TextWrapping = TextWrapping.Wrap, Style = (Style)Application.Current.FindResource("Petal.Body") };
        words.Children.Add(summary);
        if (check.Detail is { Length: > 0 } detail)
        {
            var more = new TextBlock { Text = detail, TextWrapping = TextWrapping.Wrap, Style = (Style)Application.Current.FindResource("Petal.Caption"), Margin = new Thickness(0, 2, 0, 0) };
            words.Children.Add(more);
        }
        if (check.Fix is { } fix && check.FixLabel is { } label)
        {
            var button = new Button { Content = label, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 0) };
            button.Click += async (_, _) =>
            {
                try
                {
                    fix();
                }
                catch (Exception ex)
                {
                    Log.Error(ex, $"Fixing {check.Area}");
                }
                await CheckAsync();
            };
            words.Children.Add(button);
        }
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 14) };
        DockPanel.SetDock(mark, Dock.Left);
        row.Children.Add(mark);
        row.Children.Add(words);
        return row;
    }

    private void SaveReport()
    {
        if (_checks.Count == 0)
            return;
        try
        {
            var path = Health.SaveReport(_checks);
            FooterNote = L.F("Saved on the desktop as {0}", Path.GetFileName(path));
            Process.Start("explorer.exe", $"/select,\"{path}\"");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error(ex, "Saving the health report");
            FooterNote = "The report couldn't be saved";
        }
    }
}
