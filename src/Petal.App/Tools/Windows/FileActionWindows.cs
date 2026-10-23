using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Petal.App.Hud;
using Petal.App.Input;
using Petal.App.Jobs;
using Petal.App.Localization;
using Petal.App.Settings;
using Petal.App.Shell;
using Petal.App.Tools.Parts;
using Petal.App.Util;
using Petal.Core.Actions;
using Petal.Core.Jobs;
using Petal.Engines.Files.Archives;

namespace Petal.App.Tools.Windows;

// ---------------------------------------------------------------------------------------------
// Compress to ZIP — files and folders into one archive, optionally with a password.
// ---------------------------------------------------------------------------------------------

public sealed class ZipWindow : ToolWindow
{
    private readonly IReadOnlyList<string> _paths;
    private readonly JobRunner _jobs;
    private readonly ScreenPoint _at;
    private readonly TextBox _name = new();
    private readonly Segmented<ZipLevel> _level;
    private readonly CheckBox _protect = new() { Content = L.T("Protect with a password"), Margin = new Thickness(0, 14, 0, 0) };
    private readonly PasswordField _password = new();
    private readonly StackPanel _passwordPanel = new() { Visibility = Visibility.Collapsed };
    private readonly string _folder;

    public ZipWindow(IReadOnlyList<string> paths, JobRunner jobs, ScreenPoint at) : base("Compress to ZIP", 420)
    {
        _paths = paths;
        _jobs = jobs;
        _at = at;
        var suggested = ZipCreator.DefaultOutput(paths);
        _folder = Path.GetDirectoryName(suggested)!;
        _name.Text = Path.GetFileNameWithoutExtension(suggested);

        Body.Children.Add(Text(L.P(paths.Count, "{0} item", "{0} items") + " · " + Sizes.Describe(paths), "Petal.Section"));
        var name = new DockPanel();
        var ext = Text(".zip", "Petal.Label");
        ext.Margin = new Thickness(6, 0, 0, 0);
        ext.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(ext, Dock.Right);
        name.Children.Add(ext);
        name.Children.Add(_name);
        Body.Children.Add(Row("Name", name, 110, 14));
        _level = Segments([(ZipLevel.Fast, "Fast"), (ZipLevel.Normal, "Balanced"), (ZipLevel.Best, "Smallest")], ZipLevel.Normal);
        Body.Children.Add(Row("Compression", _level, 110, 10));

        Body.Children.Add(_protect);
        _passwordPanel.Children.Add(Row("Password", _password, 110, 8));
        _passwordPanel.Children.Add(Text("Files are encrypted with AES-256. Windows 11, 7-Zip and WinRAR open them; File Explorer on Windows 10 can't.", "Petal.Caption").WithMarginT(6));
        Body.Children.Add(_passwordPanel);
        _protect.Checked += (_, _) => { _passwordPanel.Visibility = Visibility.Visible; _password.FocusInput(); Sync(); };
        _protect.Unchecked += (_, _) => { _passwordPanel.Visibility = Visibility.Collapsed; Sync(); };
        _password.Changed += Sync;
        _name.TextChanged += (_, _) => Sync();

        PrimaryButton.Content = L.T("Create ZIP");
        PrimaryButton.Click += (_, _) => Apply();
        FooterNote = L.F("In “{0}”", Path.GetFileName(_folder.TrimEnd('\\')) is { Length: > 0 } f ? f : _folder);
        Loaded += (_, _) => { _name.Focus(); _name.SelectAll(); };
        Sync();
    }

    private void Sync() =>
        PrimaryButton.IsEnabled = BatchRename.Clean(_name.Text).Length > 0 && (_protect.IsChecked != true || _password.Password.Length > 0);

    private void Apply()
    {
        var output = OutputNaming.Unique(_folder, BatchRename.Clean(_name.Text), "zip");
        var options = new ZipOptions { Level = _level.Value, Password = _protect.IsChecked == true ? _password.Password : null };
        var paths = _paths;
        Close();
        _ = _jobs.RunAsync("Compressing to ZIP", "ZIP created",
            [new WorkItem(paths[0], Path.GetFileName(output), async (p, ct) => (IReadOnlyList<string>)[await ZipCreator.CreateAsync(paths, output, options, p, ct)])],
            _at, results => results.Count == 1 && File.Exists(results[0]) ? Sizes.Format(new FileInfo(results[0]).Length) : null);
    }
}

// ---------------------------------------------------------------------------------------------
// Rename — a pattern, find and replace, numbers and dates, previewed live; undo from the card.
// ---------------------------------------------------------------------------------------------

public sealed class RenameWindow : ToolWindow
{
    private readonly IReadOnlyList<string> _paths;
    private readonly ScreenPoint _at;
    private readonly TextBox _pattern = new() { Text = "{name}" };
    private readonly TextBox _find = new();
    private readonly TextBox _replace = new();
    private readonly TextBox _start = new() { Text = "1", Width = 70, FlowDirection = FlowDirection.LeftToRight };
    private readonly Segmented<NameCase> _case;
    private readonly Grid _startRow;
    private readonly StackPanel _list = new();
    private readonly Dictionary<string, DateTime> _taken = new(StringComparer.OrdinalIgnoreCase);
    // Names from what each file holds, for {content}, as they are worked out.
    private readonly Dictionary<string, string?> _content = new(StringComparer.OrdinalIgnoreCase);
    private readonly TextBlock _reading;
    private CancellationTokenSource? _readingContent;
    private IReadOnlyList<RenameItem> _plan = [];

    public RenameWindow(IReadOnlyList<string> paths, ScreenPoint at) : base("Rename", 470)
    {
        _paths = paths;
        _at = at;
        Body.Children.Add(Text(L.P(paths.Count, "{0} item", "{0} items"), "Petal.Section"));

        Body.Children.Add(Row("New name", _pattern, 110, 12));
        var tokens = new WrapPanel { Margin = new Thickness(110, 6, 0, 0) };
        foreach (var (token, label) in new[] { ("{name}", "Name"), ("{n}", "Number"), ("{date}", "Date"), ("{folder}", "Folder"), ("{content}", "What's inside") })
        {
            var b = new Button { Content = "+ " + L.T(label), Style = (Style)FindResource("Petal.SubtleButton"), Margin = new Thickness(0, 0, 6, 0), Padding = new Thickness(8, 2, 8, 2) };
            b.Click += (_, _) => Insert(token);
            tokens.Children.Add(b);
        }
        Body.Children.Add(tokens);
        Body.Children.Add(Text("Name is the current name, Number counts up, Date is when the photo was taken (or the file last changed).", "Petal.Caption").WithMargin(110, 4, 0, 0));
        Body.Children.Add(Text("What's inside reads each file on this PC: a document's title, an invoice's sender, a screenshot's words, what a photo shows.", "Petal.Caption").WithMargin(110, 2, 0, 0));
        _reading = Text("", "Petal.Caption");
        _reading.Margin = new Thickness(110, 4, 0, 0);
        _reading.Visibility = Visibility.Collapsed;
        Body.Children.Add(_reading);

        Body.Children.Add(Row("Find", _find, 110, 12));
        Body.Children.Add(Row("Replace with", _replace, 110, 6));
        _case = Segments([(NameCase.Unchanged, "As is"), (NameCase.Lower, "lower"), (NameCase.Upper, "UPPER"), (NameCase.Title, "Title Case")], NameCase.Unchanged);
        Body.Children.Add(Row("Letters", _case, 110, 10));
        _startRow = Row("Start at", _start, 110, 8);
        Body.Children.Add(_startRow);

        var scroll = new ScrollViewer { Content = _list, MaxHeight = 210, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(10, 6, 10, 6) };
        var frame = new Border { CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Margin = new Thickness(0, 14, 0, 0), Child = scroll };
        frame.SetResourceReference(Border.BackgroundProperty, "Petal.Surface");
        frame.SetResourceReference(Border.BorderBrushProperty, "Petal.ControlBorder");
        Body.Children.Add(frame);

        foreach (var box in new[] { _pattern, _find, _replace, _start })
            box.TextChanged += (_, _) => Refresh();
        _case.Changed += _ => Refresh();

        PrimaryButton.Content = L.T("Rename");
        PrimaryButton.Click += (_, _) => Apply();
        Loaded += async (_, _) =>
        {
            _pattern.Focus();
            _pattern.SelectAll();
            await LoadPhotoDatesAsync();
            Refresh();
        };
        Refresh();
    }

    private void Insert(string token)
    {
        var caret = _pattern.CaretIndex;
        _pattern.Text = _pattern.Text.Insert(caret, token);
        _pattern.CaretIndex = caret + token.Length;
        _pattern.Focus();
    }

    private RenameRule Rule() => new()
    {
        Pattern = _pattern.Text,
        Find = _find.Text,
        Replace = _replace.Text,
        Case = _case.Value,
        Start = int.TryParse(_start.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out var s) ? s : 1,
    };

    private void Refresh()
    {
        _startRow.Visibility = _pattern.Text.Contains("{n}", StringComparison.Ordinal) ? Visibility.Visible : Visibility.Collapsed;
        if (_pattern.Text.Contains("{content}", StringComparison.Ordinal) && _readingContent is null)
            _ = ReadContentAsync();
        _plan = BatchRename.Plan(_paths, Rule(), p => _taken.TryGetValue(p, out var d) ? d : null, p => _content.GetValueOrDefault(p));
        _list.Children.Clear();
        foreach (var item in _plan.Take(300))
            _list.Children.Add(PreviewRow(item));
        if (_plan.Count > 300)
            _list.Children.Add(Text(L.F("…and {0} more", _plan.Count - 300), "Petal.Caption"));

        int changes = _plan.Count(i => i.Changes);
        int problems = _plan.Count(i => i.Problem != RenameProblem.None);
        PrimaryButton.IsEnabled = changes > 0 && problems == 0;
        FooterNote = problems > 0
            ? L.P(problems, "{0} name needs fixing", "{0} names need fixing")
            : L.F("{0} of {1} will change", changes, _plan.Count);
    }

    /// <summary>Works out a name from each file's contents, one after another, showing them as they come.</summary>
    private async Task ReadContentAsync()
    {
        var cts = _readingContent = new CancellationTokenSource();
        Closed += (_, _) => cts.Cancel();
        _reading.Visibility = Visibility.Visible;
        var converters = App.Current.Controller.Jobs.Converters;
        int done = 0;
        foreach (var path in _paths.Where(p => !Directory.Exists(p)))
        {
            if (cts.IsCancellationRequested)
                return;
            _reading.Text = L.F("Reading what's inside… {0} of {1}", done + 1, _paths.Count);
            try
            {
                _content[path] = await Petal.Recipes.SmartNamer.NameAsync(path, converters, L.IsArabic, useModel: true, cts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            done++;
            Refresh();
        }
        int named = _content.Values.Count(v => v is not null);
        _reading.Text = named == 0
            ? L.T("Nothing could be read from these files; their names stay.")
            : L.P(named, "A name was found for {0} file.", "Names were found for {0} files.");
    }

    private UIElement PreviewRow(RenameItem item)
    {
        var grid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var old = new TextBlock { Text = item.OldName, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = item.OldName };
        old.SetResourceReference(TextBlock.ForegroundProperty, "Petal.TextSecondary");
        var arrow = new TextBlock { Text = L.IsRightToLeft ? "←" : "→", HorizontalAlignment = HorizontalAlignment.Center };
        arrow.SetResourceReference(TextBlock.ForegroundProperty, "Petal.TextSecondary");
        var problem = item.Problem switch
        {
            RenameProblem.Empty => L.T("name would be empty"),
            RenameProblem.Duplicate => L.T("same name twice"),
            RenameProblem.Exists => L.T("already exists"),
            _ => null,
        };
        var fresh = new TextBlock { Text = problem is null ? item.NewName : item.NewName + "  · " + problem, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = item.NewName };
        if (problem is not null)
            fresh.SetResourceReference(TextBlock.ForegroundProperty, "Petal.Danger");
        else if (item.Changes)
            fresh.FontWeight = FontWeights.SemiBold;
        Grid.SetColumn(arrow, 1);
        Grid.SetColumn(fresh, 2);
        grid.Children.Add(old);
        grid.Children.Add(arrow);
        grid.Children.Add(fresh);
        return grid;
    }

    /// <summary>Reads when photos were taken (EXIF), in the background, for the {date} token.</summary>
    private async Task LoadPhotoDatesAsync()
    {
        var photos = _paths.Where(p => Path.GetExtension(p).ToLowerInvariant() is ".jpg" or ".jpeg" or ".tif" or ".tiff" or ".heic" or ".png").Take(1000).ToList();
        var found = await Task.Run(() =>
        {
            var dates = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in photos)
            {
                try
                {
                    using var stream = File.OpenRead(path);
                    var frame = BitmapFrame.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                    if (frame.Metadata is BitmapMetadata { DateTaken: { Length: > 0 } text } && DateTime.TryParse(text, CultureInfo.CurrentCulture, DateTimeStyles.None, out var taken))
                        dates[path] = taken;
                }
                catch
                {
                    // No readable date; the file's own date is used.
                }
            }
            return dates;
        });
        foreach (var (k, v) in found)
            _taken[k] = v;
    }

    private void Apply()
    {
        var plan = _plan;
        Close();
        try
        {
            var done = BatchRename.Apply(plan);
            Jobs.History.Add(L.P(done.Count, "Renamed {0} item", "Renamed {0} items"), done.Select(d => d.From).ToList(), [],
                done.Select(d => new Jobs.RenamedPath(d.From, d.To)).ToList());
            var card = HudCard.ShowAt(_at, "Renamed", "");
            card.Succeed(L.P(done.Count, "Renamed {0} item", "Renamed {0} items"), L.T("Click to undo"), () =>
            {
                var undone = BatchRename.Undo(done);
                var back = HudCard.ShowAt(_at, "Renaming undone", "");
                back.Succeed(L.T("Renaming undone"), L.P(undone.Count, "{0} item", "{0} items"), null);
            });
            ExplorerSelection.SelectIfVisible(done.Select(d => d.To).ToList());
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Renaming");
            HudCard.ShowAt(_at, "Couldn't rename", "").Fail(L.T("Couldn't rename"), ex.Message);
        }
    }
}
