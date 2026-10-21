using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using Petal.App.Hud;
using Petal.App.Input;
using Petal.App.Jobs;
using Petal.App.Localization;
using Petal.App.Shell;
using Petal.App.Util;
using Petal.Recipes;

namespace Petal.App.Tools.Windows;

/// <summary>
/// Find Duplicates: identical files, and photos or videos that are copies at another size or
/// quality, in the chosen files and folders. All but the best copy of each are selected; they go
/// to the Recycle Bin, and History can put them back.
/// </summary>
public sealed class DuplicatesWindow : ToolWindow
{
    private enum KeepRule
    {
        Best,
        Newest,
        Oldest,
    }

    private static DuplicatesWindow? _open;

    private readonly IReadOnlyList<string> _paths;
    private readonly ScreenPoint _at;
    private readonly CheckBox _photos = new() { Content = L.T("Similar photos"), IsChecked = true, Margin = new Thickness(0, 0, 16, 0) };
    private readonly CheckBox _videos = new() { Content = L.T("Similar videos"), IsChecked = true, Margin = new Thickness(0, 0, 16, 0) };
    private readonly CheckBox _subfolders = new() { Content = L.T("Include subfolders"), IsChecked = true };
    private readonly TextBlock _status;
    private readonly ProgressBar _progress = new() { Height = 4, Margin = new Thickness(0, 8, 0, 0), Maximum = 1 };
    private readonly StackPanel _results = new();
    private readonly ScrollViewer _scroll;
    private readonly ComboBox _keep;
    private readonly List<(DuplicateGroup Group, List<(DuplicateFile File, CheckBox Box)> Rows)> _groups = [];
    private CancellationTokenSource? _search;

    /// <summary>Opens the window for these files and folders (one window at a time).</summary>
    public static void Show(IReadOnlyList<string> paths, ScreenPoint at)
    {
        _open?.Close();
        _open = new DuplicatesWindow(paths, at);
        _open.Closed += (_, _) => _open = null;
        _open.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        _open.Show();
        _open.Activate();
    }

    /// <summary>From the tray: a folder chosen first.</summary>
    public static void ChooseAndShow()
    {
        var dialog = new OpenFolderDialog { Title = L.T("Find duplicates in"), Multiselect = true };
        if (dialog.ShowDialog() == true)
            Show(dialog.FolderNames, new ScreenPoint(0, 0));
    }

    private DuplicatesWindow(IReadOnlyList<string> paths, ScreenPoint at, bool search = true) : base("Find Duplicates", 560)
    {
        _paths = paths;
        _at = at;
        Body.Children.Add(Text(Where(paths), "Petal.Section"));
        var options = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        options.Children.Add(_photos);
        options.Children.Add(_videos);
        options.Children.Add(_subfolders);
        foreach (var box in new[] { _photos, _videos, _subfolders })
            box.Click += (_, _) => _ = SearchAsync();
        Body.Children.Add(options);

        _status = Text("", "Petal.Caption");
        _status.Margin = new Thickness(0, 10, 0, 0);
        _status.TextTrimming = TextTrimming.CharacterEllipsis;
        Body.Children.Add(_status);
        Body.Children.Add(_progress);

        _scroll = new ScrollViewer { MaxHeight = 440, Margin = new Thickness(0, 10, 0, 0), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _results };
        Body.Children.Add(_scroll);

        _keep = Choice([(KeepRule.Best, "Keep the original or best copy"), (KeepRule.Newest, "Keep the newest"), (KeepRule.Oldest, "Keep the oldest")], KeepRule.Best);
        _keep.SelectionChanged += (_, _) => ApplyRule();
        Body.Children.Add(Row("Select", _keep, 70, 12));

        PrimaryButton.Content = L.T("Move to Recycle Bin");
        PrimaryButton.Click += (_, _) => Recycle();
        Closed += (_, _) => _search?.Cancel();
        if (search)
            Loaded += (_, _) => _ = SearchAsync();
        Sync();
    }

    private static string Where(IReadOnlyList<string> paths)
    {
        if (paths.Count == 1)
            return Path.GetFileName(Path.TrimEndingDirectorySeparator(paths[0])) is { Length: > 0 } name ? name : paths[0];
        int folders = paths.Count(Directory.Exists);
        return folders == paths.Count ? L.P(folders, "{0} folder", "{0} folders") : L.P(paths.Count, "{0} item", "{0} items");
    }

    private async Task SearchAsync()
    {
        _search?.Cancel();
        var search = _search = new CancellationTokenSource();
        _groups.Clear();
        _results.Children.Clear();
        _progress.Visibility = Visibility.Visible;
        _progress.Value = 0;
        _status.Text = L.T("Looking…");
        Sync();
        var options = new DuplicateOptions
        {
            SimilarPhotos = _photos.IsChecked == true,
            SimilarVideos = _videos.IsChecked == true,
            IncludeSubfolders = _subfolders.IsChecked == true,
        };
        var progress = new Progress<DuplicateProgress>(p =>
        {
            if (search != _search)
                return;
            _progress.Value = p.Fraction;
            _status.Text = L.P(p.Files, "{0} file looked at", "{0} files looked at") + (p.Current.Length > 0 ? " · " + p.Current : "");
        });
        try
        {
            var groups = await Task.Run(() => DuplicateFinder.FindAsync(_paths, options, progress, search.Token));
            if (search != _search)
                return;
            Show(groups);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Error(ex, "Finding duplicates");
            _status.Text = L.T("The search stopped: ") + L.T(ex.Message);
        }
        finally
        {
            if (search == _search)
                _progress.Visibility = Visibility.Collapsed;
        }
    }

    private void Show(IReadOnlyList<DuplicateGroup> groups)
    {
        _results.Children.Clear();
        _groups.Clear();
        if (groups.Count == 0)
        {
            _status.Text = L.T("No duplicates found.");
            Sync();
            return;
        }
        foreach (var group in groups)
        {
            var rows = new List<(DuplicateFile, CheckBox)>();
            var card = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
            var title = group.Kind switch
            {
                DuplicateKind.Identical => L.T("Identical files"),
                DuplicateKind.SimilarPhotos => L.T("Similar photos"),
                _ => L.T("Similar videos"),
            };
            var count = L.P(group.Files.Count, "{0} file", "{0} files");
            var free = L.F("{0} to free", Sizes.Format(group.Spare));
            var header = Text($"{title} · {count} · {free}", "Petal.Label");
            header.Margin = new Thickness(0, 0, 0, 4);
            card.Children.Add(header);
            foreach (var file in group.Files)
            {
                var box = new CheckBox { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
                box.Click += (_, _) => Sync();
                rows.Add((file, box));
                card.Children.Add(FileRow(file, box));
            }
            var frame = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 8, 10, 6), Margin = new Thickness(0, 0, 0, 8), Child = card };
            frame.SetResourceReference(Border.BackgroundProperty, "Petal.Control");
            _results.Children.Add(frame);
            _groups.Add((group, rows));
        }
        _status.Text = L.P(groups.Count, "{0} set of duplicates", "{0} sets of duplicates") + " · " + L.F("{0} to free", Sizes.Format(groups.Sum(g => g.Spare)));
        ApplyRule();
    }

    private UIElement FileRow(DuplicateFile file, CheckBox box)
    {
        var thumb = new Image { Width = 44, Height = 44, Stretch = Stretch.Uniform, Margin = new Thickness(0, 0, 10, 0) };
        _ = Task.Run(() => ShellIcons.Thumbnail(file.Path, 88)).ContinueWith(t => thumb.Source = t.Result, TaskScheduler.FromCurrentSynchronizationContext());
        var name = Text(Path.GetFileName(file.Path), "Petal.Body");
        name.TextTrimming = TextTrimming.CharacterEllipsis;
        name.Cursor = Cursors.Hand;
        name.ToolTip = L.T("Show in File Explorer");
        name.MouseLeftButtonUp += (_, _) => ExplorerSelection.Reveal([file.Path]);
        var detail = new List<string> { Sizes.Format(file.Size), file.Modified.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) };
        if (file.Width > 0)
            detail.Add($"{file.Width} × {file.Height}");
        var info = Text(string.Join(" · ", detail), "Petal.Caption");
        var folder = Text(Path.GetDirectoryName(file.Path) ?? "", "Petal.Caption");
        folder.TextTrimming = TextTrimming.CharacterEllipsis;
        folder.ToolTip = file.Path;
        folder.FlowDirection = FlowDirection.LeftToRight;
        // Left is the start of the line, mirrored in Arabic: the path stays under the name.
        folder.HorizontalAlignment = HorizontalAlignment.Left;
        var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { name, folder, info } };
        var row = new DockPanel { Margin = new Thickness(0, 4, 0, 4) };
        DockPanel.SetDock(box, Dock.Left);
        DockPanel.SetDock(thumb, Dock.Left);
        row.Children.Add(box);
        row.Children.Add(thumb);
        row.Children.Add(words);
        return row;
    }

    /// <summary>Ticks every copy but the one to keep, by the chosen rule.</summary>
    private void ApplyRule()
    {
        var rule = ValueOf<KeepRule>(_keep);
        foreach (var (group, rows) in _groups)
        {
            var keep = rule switch
            {
                KeepRule.Newest => group.Files.MaxBy(f => f.Modified)!,
                KeepRule.Oldest => group.Files.MinBy(f => f.Modified)!,
                _ => DuplicateFinder.Keeper(group),
            };
            foreach (var (file, box) in rows)
                box.IsChecked = !ReferenceEquals(file, keep);
        }
        Sync();
    }

    private List<string> Selected => _groups.SelectMany(g => g.Rows).Where(r => r.Box.IsChecked == true).Select(r => r.File.Path).ToList();

    private void Sync()
    {
        var selected = _groups.SelectMany(g => g.Rows).Where(r => r.Box.IsChecked == true).ToList();
        bool everyCopy = _groups.Any(g => g.Rows.All(r => r.Box.IsChecked == true));
        PrimaryButton.IsEnabled = selected.Count > 0 && !everyCopy;
        _keep.IsEnabled = _groups.Count > 0;
        FooterNote = everyCopy
            ? L.T("Keep at least one copy in each set.")
            : selected.Count > 0 ? L.P(selected.Count, "{0} file selected", "{0} files selected") + " · " + Sizes.Format(selected.Sum(r => r.File.Size)) : "";
    }

    private void Recycle()
    {
        var paths = Selected;
        if (paths.Count == 0)
            return;
        var moved = new List<string>();
        foreach (var path in paths)
        {
            try
            {
                if (File.Exists(path))
                {
                    RecycleBin.Send(path);
                    moved.Add(path);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
            {
                Log.Error(ex, $"Recycling {path}");
            }
        }
        var title = L.P(moved.Count, "Moved {0} duplicate to the Recycle Bin", "Moved {0} duplicates to the Recycle Bin");
        History.AddRecycled(title, moved);
        var at = new ScreenPoint((int)((Left + ActualWidth / 2) * Dpi.X), (int)((Top + ActualHeight / 2) * Dpi.Y));
        var card = HudCard.ShowAt(at, title, "");
        if (moved.Count == paths.Count)
            card.Succeed(title, L.T("History can put them back."), null);
        else
            card.Fail(title, L.F("{0} couldn't be moved", paths.Count - moved.Count));
        Close();
    }

    private (double X, double Y) Dpi
    {
        get
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            return (dpi.DpiScaleX, dpi.DpiScaleY);
        }
    }

    /// <summary>For design snapshots: the window with made-up results, no search.</summary>
    internal static DuplicatesWindow ForSnapshot(IReadOnlyList<string> files)
    {
        var window = new DuplicatesWindow(files, new ScreenPoint(0, 0), search: false);
        window._progress.Visibility = Visibility.Collapsed;
        var now = DateTime.Now;
        var groups = new List<DuplicateGroup>
        {
            new(DuplicateKind.Identical, files.Take(2).Select((f, i) => new DuplicateFile(f, 2_400_000, now.AddDays(-30 + i * 10))).ToList()),
            new(DuplicateKind.SimilarPhotos, files.Skip(2).Take(2).Select((f, i) => new DuplicateFile(f, 3_100_000 - i * 1_900_000, now.AddDays(-i)) { Width = 4032 - i * 2016, Height = 3024 - i * 1512 }).ToList()),
        };
        window.Show(groups);
        return window;
    }
}
