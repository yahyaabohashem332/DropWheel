using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Petal.App.Hud;
using Petal.App.Input;
using Petal.App.Jobs;
using Petal.App.Localization;
using Petal.App.Settings;
using Petal.App.Shell;

namespace Petal.App.Tools.Windows;

/// <summary>
/// What Petal made lately: open a result or its folder, or take it back. As a page of the main window it has the
/// window's height, a search over what was done and the files' names, Today and This Week, and the days as headings.
/// </summary>
public sealed class HistoryWindow : ToolWindow
{
    private static HistoryWindow? _open;
    private readonly StackPanel _list = new();
    private readonly TextBlock _empty;
    private readonly TextBox _search = new() { MinHeight = 36, VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(30, 0, 8, 0) };
    private Segmented<int>? _when;
    private readonly TextBlock _count = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0) };

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

    /// <summary>The history as a page of the main window.</summary>
    internal static HistoryWindow ForPage() => new(asPage: true);

    private HistoryWindow(bool asPage = false) : base("History", 520, asPage)
    {
        _empty = Text("Nothing here yet. Finished conversions and tools appear here, newest first.", "Petal.Caption");
        _empty.Margin = new Thickness(0, 20, 0, 20);
        _empty.HorizontalAlignment = HorizontalAlignment.Center;
        var scroll = new ScrollViewer { Content = _list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(-6, 0, -6, 0) };
        var note = Text("Undo moves the results to the Recycle Bin (renames are put back). The history stays on this PC.", "Petal.Caption").WithMarginT(10);
        if (asPage)
            PageRoot = PageLayout(scroll, note);
        else
        {
            scroll.MaxHeight = 460;
            Body.Children.Add(_empty);
            Body.Children.Add(scroll);
            Body.Children.Add(note);
        }

        ResetButton.Content = L.T("Clear History");
        ResetButton.Visibility = Visibility.Visible;
        ResetButton.Click += (_, _) => History.Clear();
        PrimaryButton.Content = L.T("Done");
        PrimaryButton.Click += (_, _) => Close();
        if (asPage)
            PrimaryButton.Visibility = Visibility.Collapsed;
        FooterNote = SettingsStore.Current.KeepHistory ? "" : L.T("Keeping a history is off in Settings.");

        History.Changed += OnChanged;
        Closed += (_, _) => History.Changed -= OnChanged;
        Fill();
    }

    private void OnChanged() => Dispatcher.BeginInvoke(DispatcherPriority.Background, Fill);

    // ---------- as a page ----------

    private FrameworkElement PageLayout(ScrollViewer scroll, TextBlock note)
    {
        _count.Style = (Style)FindResource("Petal.Caption");
        var hint = Text("Search", "Petal.Caption");
        hint.IsHitTestVisible = false;
        hint.VerticalAlignment = VerticalAlignment.Center;
        hint.Margin = new Thickness(32, 0, 0, 0);
        var glass = ThemedIcon("search", 15, "Petal.TextSecondary");
        glass.IsHitTestVisible = false;
        glass.HorizontalAlignment = HorizontalAlignment.Left;
        glass.VerticalAlignment = VerticalAlignment.Center;
        glass.Margin = new Thickness(10, 0, 0, 0);
        _search.FlowDirection = L.Direction;
        System.Windows.Automation.AutomationProperties.SetName(_search, L.T("Search"));
        _search.TextChanged += (_, _) =>
        {
            hint.Visibility = _search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            Fill();
        };
        _when = Segments([(0, "All"), (1, "Today"), (2, "This week")], 0);
        _when.Margin = new Thickness(12, 0, 0, 0);
        _when.Changed += _ => Fill();
        var bar = new Grid { ColumnDefinitions = { new ColumnDefinition { MaxWidth = 460 }, new ColumnDefinition { Width = GridLength.Auto }, new ColumnDefinition { Width = GridLength.Auto } } };
        bar.Children.Add(new Grid { Children = { _search, glass, hint } });
        Grid.SetColumn(_when, 1);
        bar.Children.Add(_when);
        Grid.SetColumn(_count, 2);
        bar.Children.Add(_count);

        var root = new Grid
        {
            RowDefinitions = { new RowDefinition { Height = GridLength.Auto }, new RowDefinition { Height = GridLength.Auto }, new RowDefinition(), new RowDefinition { Height = GridLength.Auto } },
        };
        root.Children.Add(bar);
        Grid.SetRow(_empty, 1);
        root.Children.Add(_empty);
        scroll.Margin = new Thickness(0, 14, 0, 0);
        Grid.SetRow(scroll, 2);
        root.Children.Add(scroll);
        Grid.SetRow(note, 3);
        root.Children.Add(note);
        // Ctrl+F looks for something done.
        root.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
            {
                e.Handled = true;
                _search.Focus();
                _search.SelectAll();
            }
        };
        return root;
    }

    /// <summary>Whether an entry is within the time chosen and holds the words looked for (in what was done or a file's name).</summary>
    private bool Matches(HistoryEntry entry)
    {
        var when = _when?.Value ?? 0;
        if (when == 1 && entry.Time.Date != DateTime.Today || when == 2 && entry.Time.Date < DateTime.Today.AddDays(-6))
            return false;
        var words = _search.Text.Trim();
        if (words.Length == 0)
            return true;
        var names = entry.Inputs.Concat(entry.Outputs).Concat(entry.Renames?.SelectMany(r => new[] { r.From, r.To }) ?? []).Concat(entry.Recycled ?? [])
            .Select(path => Path.GetFileName(path.TrimEnd('\\')));
        return names.Append(L.T(entry.Title)).Append(entry.Title).Any(text => text.Contains(words, StringComparison.CurrentCultureIgnoreCase));
    }

    /// <summary>The page's list: what matches, under its day.</summary>
    private void FillPage(IReadOnlyList<HistoryEntry> entries)
    {
        var shown = entries.Where(Matches).ToList();
        _count.Text = L.P(shown.Count, "{0} item", "{0} items");
        _empty.Text = L.T(entries.Count == 0 ? "Nothing here yet. Finished conversions and tools appear here, newest first." : "Nothing matches.");
        _empty.Visibility = shown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var day in shown.GroupBy(e => e.Time.Date))
        {
            var heading = Text(Day(day.Key), "Petal.Label").WithMargin(6, _list.Children.Count == 0 ? 0 : 14, 6, 6);
            _list.Children.Add(heading);
            foreach (var entry in day)
                _list.Children.Add(PageRow(entry));
        }
    }

    private static string Day(DateTime date) =>
        date == DateTime.Today ? L.T("Today") : date == DateTime.Today.AddDays(-1) ? L.T("Yesterday") : date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>What an entry made (the files there now), or, for a rename or files put in the Recycle Bin, those.</summary>
    private static List<string> Results(HistoryEntry entry) =>
        entry.Renames is { Count: > 0 } r ? [.. r.Select(x => x.To)] : entry.Recycled is { Count: > 0 } bin ? [.. bin] : [.. entry.Outputs];

    private static string KindIcon(HistoryEntry entry, IReadOnlyList<string> results)
    {
        if (entry.Renames is { Count: > 0 })
            return "rename";
        if (entry.Recycled is { Count: > 0 })
            return "trash";
        return results.Count == 0 ? "file" : Petal.Core.Formats.Formats.FromPath(results[0])?.Family switch
        {
            Petal.Core.Formats.FormatFamily.Image or Petal.Core.Formats.FormatFamily.Vector => "image",
            Petal.Core.Formats.FormatFamily.Video => "video",
            Petal.Core.Formats.FormatFamily.Audio => "music",
            Petal.Core.Formats.FormatFamily.Pdf or Petal.Core.Formats.FormatFamily.Document or Petal.Core.Formats.FormatFamily.Text => "fileText",
            Petal.Core.Formats.FormatFamily.Archive => "fileArchive",
            _ => Directory.Exists(results[0]) ? "folder" : "file",
        };
    }

    /// <summary>"photo.heic → photo.jpg", and how many more.</summary>
    private static string FromTo(HistoryEntry entry, IReadOnlyList<string> results)
    {
        // The arrow points the way the line reads; in a right-to-left line it is held between right-to-left marks,
        // so two Latin names stay two names, the first on the right (WPF doesn't know Unicode's isolates).
        string Name(string path) => Path.GetFileName(path.TrimEnd('\\'));
        var from = entry.Renames is { Count: > 0 } r ? r[0].From : entry.Inputs.FirstOrDefault();
        var to = results.FirstOrDefault();
        var arrow = L.IsRightToLeft ? "\u200F←\u200F" : "→";
        var line = from is not null && to is not null && Name(from) != Name(to) ? $"{Name(from)}  {arrow}  {Name(to)}" : Name(to ?? from ?? "");
        return results.Count > 1 ? line + "  " + L.F("and {0} more", results.Count - 1) : line;
    }

    private FrameworkElement PageRow(HistoryEntry entry)
    {
        var results = Results(entry);
        var card = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(14, 10, 10, 10), Margin = new Thickness(6, 0, 6, 6) };
        card.SetResourceReference(Border.BackgroundProperty, "Petal.Control");
        var row = new DockPanel();
        var disc = new Border { Width = 36, Height = 36, CornerRadius = new CornerRadius(18), Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        disc.SetResourceReference(Border.BackgroundProperty, "Petal.AccentSoft");
        var icon = ThemedIcon(KindIcon(entry, results), 17, "Petal.Accent");
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        icon.VerticalAlignment = VerticalAlignment.Center;
        disc.Child = icon;
        DockPanel.SetDock(disc, Dock.Left);
        row.Children.Add(disc);

        var buttons = Actions(entry, withOpen: true);
        DockPanel.SetDock(buttons, Dock.Right);
        row.Children.Add(buttons);
        var time = Text(entry.Time.ToString("HH:mm", CultureInfo.InvariantCulture), "Petal.Caption");
        time.VerticalAlignment = VerticalAlignment.Center;
        time.Margin = new Thickness(12, 0, 12, 0);
        DockPanel.SetDock(time, Dock.Right);
        row.Children.Add(time);

        var title = Text(L.T(entry.Title), "Petal.Body");
        title.FontWeight = FontWeights.SemiBold;
        title.TextTrimming = TextTrimming.CharacterEllipsis;
        var files = Text(FromTo(entry, results), "Petal.Caption");
        files.TextTrimming = TextTrimming.CharacterEllipsis;
        files.ToolTip = string.Join("\n", results);
        var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { title, files } };
        if (entry.Undone)
            words.Opacity = disc.Opacity = 0.55;
        row.Children.Add(words);
        card.Child = row;
        card.MouseEnter += (_, _) => card.SetResourceReference(Border.BackgroundProperty, "Petal.ControlHover");
        card.MouseLeave += (_, _) => card.SetResourceReference(Border.BackgroundProperty, "Petal.Control");
        // Twice to open what it made.
        card.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2 && !entry.Undone)
                OpenResult(entry);
        };
        return card;
    }

    /// <summary>Opens the first result that is still there, in the program Windows opens it with.</summary>
    private static void OpenResult(HistoryEntry entry)
    {
        if (History.Existing(entry).FirstOrDefault() is not { } path)
            return;
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            Log.Error(ex, "Opening a result from the history");
        }
    }

    private void Fill()
    {
        _list.Children.Clear();
        if (IsPage)
        {
            var all = History.Entries;
            ResetButton.IsEnabled = all.Count > 0;
            FillPage(all);
            return;
        }
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

        var buttons = Actions(entry, withOpen: false);
        Grid.SetColumn(buttons, 1);
        grid.Children.Add(buttons);

        var line = new Border { BorderThickness = new Thickness(0, 0, 0, 1), Child = grid };
        line.SetResourceReference(Border.BorderBrushProperty, "Petal.Divider");
        return line;
    }

    /// <summary>Open (on the page), Show in File Explorer and Undo, or "Undone" once it has been.</summary>
    private StackPanel Actions(HistoryEntry entry, bool withOpen)
    {
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (withOpen && !entry.Undone)
        {
            var open = IconButton("externalLink", "Open", 15);
            open.IsEnabled = History.Existing(entry).Count > 0;
            open.Click += (_, _) => OpenResult(entry);
            buttons.Children.Add(open);
        }
        var existing = History.Existing(entry);
        if (entry.Undone)
        {
            buttons.Children.Add(Text("Undone", "Petal.Caption"));
        }
        else
        {
            bool recycled = entry.Recycled is { Count: > 0 };
            var show = IconButton("folderOpen", "Show in File Explorer", 15);
            show.IsEnabled = existing.Count > 0;
            show.Click += (_, _) => ExplorerSelection.Reveal(existing);
            var undo = IconButton("undo", recycled ? "Put the files back from the Recycle Bin"
                : entry.Renames is { Count: > 0 } ? "Put the old names back" : "Undo: move the results to the Recycle Bin", 15);
            undo.IsEnabled = recycled ? History.Restorable(entry).Count > 0 : existing.Count > 0;
            undo.Click += (_, _) =>
            {
                int n = History.Undo(entry.Id);
                var host = Host;
                // On a page, under the button; in the window, under the window.
                var below = undo.PointToScreen(new Point(undo.ActualWidth / 2, undo.ActualHeight + 30));
                var at = IsPage
                    ? new ScreenPoint((int)below.X, (int)below.Y)
                    : new ScreenPoint((int)((host.Left + host.ActualWidth / 2) * DpiX), (int)((host.Top + host.ActualHeight + 30) * DpiY));
                HudCard.ShowAt(at, "Undone", "").Succeed(L.T("Undone"), L.P(n, "{0} item", "{0} items"), null);
            };
            buttons.Children.Add(show);
            buttons.Children.Add(undo);
        }
        return buttons;
    }

    private double DpiX => System.Windows.Media.VisualTreeHelper.GetDpi(Host).DpiScaleX;
    private double DpiY => System.Windows.Media.VisualTreeHelper.GetDpi(Host).DpiScaleY;

    /// <summary>"Today 14:32 · photo.png" and the like.</summary>
    private static string Describe(HistoryEntry entry)
    {
        // 24-hour clock and ISO dates: the same in both languages, whatever the Windows region.
        var time = entry.Time.ToString("HH:mm", CultureInfo.InvariantCulture);
        var when = entry.Time.Date == DateTime.Today ? L.T("Today") + " " + time
            : entry.Time.Date == DateTime.Today.AddDays(-1) ? L.T("Yesterday") + " " + time
            : entry.Time.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + " " + time;
        var results = entry.Renames is { Count: > 0 } r ? r.Select(x => x.To).ToList() : entry.Recycled is { Count: > 0 } bin ? bin.ToList() : entry.Outputs.ToList();
        var first = results.Count > 0 ? Path.GetFileName(results[0].TrimEnd('\\')) : "";
        var more = results.Count > 1 ? " " + L.F("and {0} more", results.Count - 1) : "";
        return $"{when} · {first}{more}";
    }
}
