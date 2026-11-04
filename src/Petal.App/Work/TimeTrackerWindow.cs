using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Petal.App.Localization;
using Petal.App.Settings;
using Petal.App.Tools;
using Petal.App.Tools.Windows;
using Petal.Core.Time;

namespace Petal.App.Work;

/// <summary>Where the time went: a program per row, most time first, for a day or some days. Everything stays on this PC.</summary>
public sealed class TimeTrackerWindow : ToolWindow
{
    private readonly TimeLog _log;
    private readonly Button _switch = new() { HorizontalAlignment = HorizontalAlignment.Left };
    private readonly CheckBox _titles = new() { Content = L.T("Keep window titles too"), Margin = new Thickness(0, 10, 0, 0) };
    private readonly Segmented<int> _range;
    private readonly TextBlock _total = Text("", "Petal.Section");
    private readonly StackPanel _rows = new();
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromSeconds(20) };
    private readonly Button _export = new() { Content = L.T("Save as CSV…") };
    private readonly Button _forget = new() { Content = L.T("Delete All"), Margin = new Thickness(8, 0, 0, 0) };

    public TimeTrackerWindow(TimeLog? log = null) : base(L.T("Time Tracker"), 500)
    {
        _log = log ?? TimeTracker.Log;
        _range = Segments([(0, "Today"), (1, "Yesterday"), (7, "7 days"), (30, "30 days")], 0);
        Body.Children.Add(_switch);
        Body.Children.Add(Text("It notes which program is in front, every few seconds, while you are at the PC. What you type, read or open is never kept, and nothing leaves this PC.", "Petal.Caption").WithMarginT(8));
        _titles.IsChecked = SettingsStore.Current.TimeTrackerTitles;
        Body.Children.Add(_titles);
        Body.Children.Add(Row("Show", _range, 70, 12));
        _total.Margin = new Thickness(0, 12, 0, 6);
        Body.Children.Add(_total);
        Body.Children.Add(new ScrollViewer { Content = _rows, MaxHeight = 340, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0), Children = { _export, _forget } };
        Body.Children.Add(buttons);
        _switch.Click += (_, _) => TimeTracker.Toggle();
        _titles.Click += (_, _) =>
        {
            SettingsStore.Current.TimeTrackerTitles = _titles.IsChecked == true;
            SettingsStore.Save();
        };
        _range.Changed += _ => Show();
        _export.Click += (_, _) => Export();
        _forget.Click += (_, _) => Forget();
        _refresh.Tick += (_, _) => Show();
        _refresh.Start();
        TimeTracker.Changed += Sync;
        Closed += (_, _) =>
        {
            _refresh.Stop();
            TimeTracker.Changed -= Sync;
        };
        PrimaryButton.Content = L.T("Close");
        PrimaryButton.Click += (_, _) => Close();
        Sync();
    }

    private void Sync()
    {
        _switch.Content = L.T(TimeTracker.IsOn ? "Stop Tracking" : "Start Tracking");
        Show();
    }

    private (DateOnly From, DateOnly To) Dates()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        return _range.Value switch
        {
            1 => (today.AddDays(-1), today.AddDays(-1)),
            7 => (today.AddDays(-6), today),
            30 => (today.AddDays(-29), today),
            _ => (today, today),
        };
    }

    /// <summary>A length as people say it: "2 h 05 min", "12 min", "under a minute".</summary>
    internal static string Length(long seconds)
    {
        long minutes = seconds / 60;
        if (minutes == 0)
            return L.T("under a minute");
        return minutes < 60 ? L.F("{0} min", minutes) : L.F("{0} h {1} min", minutes / 60, (minutes % 60).ToString("00"));
    }

    private void Show()
    {
        var (from, to) = Dates();
        var entries = _log.Range(from, to);
        long total = entries.Sum(e => e.Seconds);
        _total.Text = total == 0 ? L.T("Nothing counted yet.") : L.F("In all: {0}", Length(total));
        _rows.Children.Clear();
        long largest = entries.Count == 0 ? 1 : Math.Max(1, entries[0].Seconds);
        foreach (var entry in entries.Take(40))
        {
            var grid = new Grid { Margin = new Thickness(0, 0, 8, 2) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var name = Text(entry.App);
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            var time = Text(Length(entry.Seconds), "Petal.Caption");
            time.Margin = new Thickness(8, 0, 0, 0);
            Grid.SetColumn(time, 1);
            grid.Children.Add(name);
            grid.Children.Add(time);
            var bar = new Border { Height = 5, CornerRadius = new CornerRadius(3), HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 3, 0, 0) };
            bar.SetResourceReference(Border.BackgroundProperty, "Petal.Accent");
            var track = new Grid();
            track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(1, entry.Seconds), GridUnitType.Star) });
            track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0, largest - entry.Seconds), GridUnitType.Star) });
            track.Children.Add(bar);
            var row = new StackPanel { Margin = new Thickness(0, 0, 0, 12), Children = { grid, track } };
            if (entry.Titles.Count > 0)
            {
                var most = string.Join("\n", entry.Titles.OrderByDescending(t => t.Value).Take(8).Select(t => Length(t.Value) + "  " + t.Key));
                row.ToolTip = most;
            }
            _rows.Children.Add(row);
        }
        _export.IsEnabled = total > 0;
    }

    private void Export()
    {
        var (from, to) = Dates();
        TimeTracker.Flush();
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "CSV|*.csv", FileName = $"time {from:yyyy-MM-dd}.csv" };
        if (dialog.ShowDialog(this) != true)
            return;
        try
        {
            // With a byte order mark, as Excel wants it to read Arabic and other languages properly.
            File.WriteAllText(dialog.FileName, _log.Csv(from, to, L.T("Date"), L.T("Program"), L.T("Minutes")), new UTF8Encoding(true));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error(ex, "Saving the time log as CSV");
            _total.Text = L.T("That didn't work.") + " " + ex.Message;
        }
    }

    private void Forget()
    {
        if (MessageBox.Show(this, L.T("Delete all the time counted so far? This can't be undone."), L.T("Time Tracker"), MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
            return;
        _log.Clear();
        Show();
    }
}
