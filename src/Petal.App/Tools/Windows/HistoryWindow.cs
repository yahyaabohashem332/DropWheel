using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Petal.App.Hud;
using Petal.App.Input;
using Petal.App.Jobs;
using Petal.App.Localization;
using Petal.App.Settings;
using Petal.App.Shell;

namespace Petal.App.Tools.Windows;

/// <summary>What Petal made lately: open the folder of a result, or take it back.</summary>
public sealed class HistoryWindow : ToolWindow
{
    private static HistoryWindow? _open;
    private readonly StackPanel _list = new();
    private readonly TextBlock _empty;

    public static void ShowSingle()
    {
        if (_open is not null)
        {
            _open.Activate();
            return;
        }
        _open = new HistoryWindow();
        _open.Closed += (_, _) => _open = null;
        _open.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        _open.Show();
        _open.Activate();
    }

    private HistoryWindow() : base("History", 520)
    {
        _empty = Text("Nothing here yet. Finished conversions and tools appear here, newest first.", "Petal.Caption");
        _empty.Margin = new Thickness(0, 20, 0, 20);
        _empty.HorizontalAlignment = HorizontalAlignment.Center;
        var scroll = new ScrollViewer { Content = _list, MaxHeight = 460, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(-6, 0, -6, 0) };
        Body.Children.Add(_empty);
        Body.Children.Add(scroll);
        Body.Children.Add(Text("Undo moves the results to the Recycle Bin (renames are put back). The history stays on this PC.", "Petal.Caption").WithMarginT(10));

        ResetButton.Content = L.T("Clear History");
        ResetButton.Visibility = Visibility.Visible;
        ResetButton.Click += (_, _) => History.Clear();
        PrimaryButton.Content = L.T("Done");
        PrimaryButton.Click += (_, _) => Close();
        FooterNote = SettingsStore.Current.KeepHistory ? "" : L.T("Keeping a history is off in Settings.");

        History.Changed += OnChanged;
        Closed += (_, _) => History.Changed -= OnChanged;
        Fill();
    }

    private void OnChanged() => Dispatcher.BeginInvoke(DispatcherPriority.Background, Fill);

    private void Fill()
    {
        _list.Children.Clear();
        var entries = History.Entries;
        _empty.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ResetButton.IsEnabled = entries.Count > 0;
        foreach (var entry in entries.Take(120))
            _list.Children.Add(Row(entry));
    }

    private UIElement Row(HistoryEntry entry)
    {
        var grid = new Grid { Margin = new Thickness(6, 0, 6, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var text = new StackPanel { Margin = new Thickness(0, 8, 8, 8) };
        var title = Text(L.T(entry.Title), "Petal.Body");
        title.FontWeight = FontWeights.SemiBold;
        if (entry.Undone)
            title.Opacity = 0.55;
        text.Children.Add(title);
        var detail = Text(Describe(entry), "Petal.Caption");
        detail.TextTrimming = TextTrimming.CharacterEllipsis;
        text.Children.Add(detail);
        grid.Children.Add(text);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var existing = History.Existing(entry);
        if (entry.Undone)
        {
            buttons.Children.Add(Text("Undone", "Petal.Caption"));
        }
        else
        {
            var show = IconButton("folderOpen", "Show in File Explorer", 15);
            show.IsEnabled = existing.Count > 0;
            show.Click += (_, _) => ExplorerSelection.Reveal(existing);
            var undo = IconButton("undo", entry.Renames is { Count: > 0 } ? "Put the old names back" : "Undo: move the results to the Recycle Bin", 15);
            undo.IsEnabled = existing.Count > 0;
            undo.Click += (_, _) =>
            {
                int n = History.Undo(entry.Id);
                var at = new ScreenPoint((int)((Left + ActualWidth / 2) * DpiX), (int)((Top + ActualHeight + 30) * DpiY));
                HudCard.ShowAt(at, "Undone", "").Succeed(L.T("Undone"), L.P(n, "{0} item", "{0} items"), null);
            };
            buttons.Children.Add(show);
            buttons.Children.Add(undo);
        }
        Grid.SetColumn(buttons, 1);
        grid.Children.Add(buttons);

        var line = new Border { BorderThickness = new Thickness(0, 0, 0, 1), Child = grid };
        line.SetResourceReference(Border.BorderBrushProperty, "Petal.Divider");
        return line;
    }

    private double DpiX => System.Windows.Media.VisualTreeHelper.GetDpi(this).DpiScaleX;
    private double DpiY => System.Windows.Media.VisualTreeHelper.GetDpi(this).DpiScaleY;

    /// <summary>"Today 14:32 · photo.png" and the like.</summary>
    private static string Describe(HistoryEntry entry)
    {
        // 24-hour clock and ISO dates: the same in both languages, whatever the Windows region.
        var time = entry.Time.ToString("HH:mm", CultureInfo.InvariantCulture);
        var when = entry.Time.Date == DateTime.Today ? L.T("Today") + " " + time
            : entry.Time.Date == DateTime.Today.AddDays(-1) ? L.T("Yesterday") + " " + time
            : entry.Time.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + " " + time;
        var results = entry.Renames is { Count: > 0 } r ? r.Select(x => x.To).ToList() : entry.Outputs.ToList();
        var first = results.Count > 0 ? Path.GetFileName(results[0].TrimEnd('\\')) : "";
        var more = results.Count > 1 ? " " + L.F("and {0} more", results.Count - 1) : "";
        return $"{when} · {first}{more}";
    }
}
