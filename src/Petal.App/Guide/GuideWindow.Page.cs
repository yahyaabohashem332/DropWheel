using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Petal.App.Localization;
using Petal.App.Settings;
using Petal.App.Visuals;

namespace Petal.App.Guide;

/// <summary>
/// The guide as a page of the main window: the whole height of the window, in three panes. The sections (with how
/// many each holds) down the side, their entries in a list beside them, and the chosen entry read in full in the
/// third pane, with Try It and its own question box. Up and Down go through the list, Enter tries the entry. When the
/// page is narrow, the sections become a list to choose from above the entries.
/// </summary>
public sealed partial class GuideWindow
{
    private readonly StackPanel _detail = new() { Margin = new Thickness(26, 22, 26, 26) };
    private ScrollViewer? _detailScroll;
    private ComboBox? _sectionPicker;
    private bool _picking;

    /// <summary>Narrower than this, the sections go into a list above the entries.</summary>
    private const double ThreePanes = 1000;

    private FrameworkElement PageLayout(FrameworkElement requestRow, ScrollViewer sectionsScroll)
    {
        var root = new Grid { RowDefinitions = { new RowDefinition { Height = GridLength.Auto }, new RowDefinition { Height = GridLength.Auto }, new RowDefinition() } };
        root.Children.Add(requestRow);
        var top = new StackPanel { Children = { _answer, _model } };
        Grid.SetRow(top, 1);
        root.Children.Add(top);

        var panes = new Grid { Margin = new Thickness(0, 16, 0, 0) };
        var sectionsColumn = new ColumnDefinition { Width = new GridLength(220) };
        panes.ColumnDefinitions.Add(sectionsColumn);
        panes.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.95, GridUnitType.Star), MinWidth = 280 });
        panes.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.45, GridUnitType.Star), MinWidth = 300 });
        sectionsScroll.Margin = new Thickness(0, 0, 14, 0);
        panes.Children.Add(sectionsScroll);

        // The entries, under their section's name (or, when narrow, a list of the sections).
        _sectionPicker = new ComboBox { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 0, 10), MinHeight = 34 };
        foreach (Button b in _sections.Children)
        {
            var section = (string)b.Tag;
            var count = GuideBook.Keys.Count(k => GuideBook.Get(k)?.Category == section);
            _sectionPicker.Items.Add(new ComboBoxItem { Content = $"{GuideBook.CategoryName(section)}  ·  {count.ToString(L.Culture)}", Tag = section });
        }
        _sectionPicker.SelectionChanged += (_, _) =>
        {
            if (!_picking && _sectionPicker.SelectedItem is ComboBoxItem { Tag: string section })
            {
                _request.Text = "";
                ShowSection(section);
            }
        };
        var listPane = new DockPanel();
        DockPanel.SetDock(_sectionPicker, Dock.Top);
        DockPanel.SetDock(_listTitle, Dock.Top);
        listPane.Children.Add(_sectionPicker);
        listPane.Children.Add(_listTitle);
        listPane.Children.Add(_listScroll);
        Grid.SetColumn(listPane, 1);
        panes.Children.Add(listPane);

        // The entry, read in full.
        _detailScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _detail };
        var reading = new Border { CornerRadius = new CornerRadius(12), Margin = new Thickness(14, 0, 0, 0), BorderThickness = new Thickness(1), Child = _detailScroll };
        reading.SetResourceReference(Border.BackgroundProperty, "Petal.Surface");
        reading.SetResourceReference(Border.BorderBrushProperty, "Petal.ControlBorder");
        Grid.SetColumn(reading, 2);
        panes.Children.Add(reading);

        panes.SizeChanged += (_, e) =>
        {
            var narrow = e.NewSize.Width < ThreePanes;
            sectionsScroll.Visibility = narrow ? Visibility.Collapsed : Visibility.Visible;
            sectionsColumn.Width = new GridLength(narrow ? 0 : 220);
            _sectionPicker.Visibility = narrow ? Visibility.Visible : Visibility.Collapsed;
        };
        Grid.SetRow(panes, 2);
        root.Children.Add(panes);

        // Up and Down through the entries from anywhere but a box being typed in (the request box hands them on), Enter tries.
        root.PreviewKeyDown += async (_, e) =>
        {
            var typing = Keyboard.FocusedElement is TextBox box && box != _request;
            if (typing || Keyboard.FocusedElement is ComboBox)
                return;
            if (e.Key is Key.Down or Key.Up)
            {
                e.Handled = true;
                Step(e.Key == Key.Down ? 1 : -1);
            }
            else if (e.Key == Key.Enter && Keyboard.FocusedElement is not TextBox && _expanded is { } key && GuideBook.CanRun(key))
            {
                e.Handled = true;
                await TryAsync(key, _tryStatus ?? new TextBlock());
            }
        };
        return root;
    }

    /// <summary>The next or previous entry in the list.</summary>
    private void Step(int by)
    {
        var keys = _rows.Keys.ToList();
        if (keys.Count == 0)
            return;
        var at = _expanded is { } key ? keys.IndexOf(key) : -1;
        _expanded = keys[Math.Clamp(at + by, 0, keys.Count - 1)];
        Refold();
        if (_rows.TryGetValue(_expanded, out var row))
            row.BringIntoView();
    }

    /// <summary>A section's button on the page: its name, and how many entries it holds.</summary>
    private static TextBlock SectionLabel(string section)
    {
        var count = GuideBook.Keys.Count(k => GuideBook.Get(k)?.Category == section);
        var label = new TextBlock { TextWrapping = TextWrapping.Wrap };
        label.Inlines.Add(new Run(GuideBook.CategoryName(section)));
        var number = new Run("  " + count.ToString(L.Culture));
        number.SetResourceReference(TextElement.ForegroundProperty, "Petal.TextSecondary");
        label.Inlines.Add(number);
        return label;
    }

    /// <summary>An entry in the page's list: its icon, name and what it does, with Try It at its end when it can run.</summary>
    private FrameworkElement CompactRow(string key)
    {
        var e = GuideBook.Get(key)!;
        var card = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 9, 10, 9), Margin = new Thickness(0, 0, 6, 4), Tag = key, Cursor = Cursors.Hand };
        card.SetResourceReference(Border.BackgroundProperty, "Petal.Control");
        var row = new DockPanel();
        var icon = Icons.Create(GuideBook.Icon(key), 18, Palette.Brush(Theme.Current.Accent), 2);
        icon.VerticalAlignment = VerticalAlignment.Top;
        icon.Margin = new Thickness(0, 2, 10, 0);
        DockPanel.SetDock(icon, Dock.Left);
        row.Children.Add(icon);
        if (GuideBook.CanRun(key))
        {
            var run = IconButton("play", L.T(GuideBook.NeedsFiles(key) ? "Try It on Files…" : "Try It"), 14);
            run.VerticalAlignment = VerticalAlignment.Center;
            run.Margin = new Thickness(6, 0, 0, 0);
            run.Click += async (_, _) =>
            {
                _expanded = key;
                Refold();
                await TryAsync(key, _tryStatus ?? new TextBlock());
            };
            DockPanel.SetDock(run, Dock.Right);
            row.Children.Add(run);
        }
        var name = Text("", "Petal.Body");
        name.Text = GuideBook.Name(key);
        name.FontWeight = FontWeights.SemiBold;
        name.TextTrimming = TextTrimming.CharacterEllipsis;
        var summary = Text("", "Petal.Caption");
        summary.Text = e.Summary;
        summary.TextWrapping = TextWrapping.Wrap;
        summary.TextTrimming = TextTrimming.CharacterEllipsis;
        summary.MaxHeight = 36;
        row.Children.Add(new StackPanel { Children = { name, summary } });
        card.Child = row;
        card.MouseLeftButtonUp += (_, _) =>
        {
            _expanded = key;
            Refold();
        };
        card.MouseEnter += (_, _) => { if (_expanded != key) card.SetResourceReference(Border.BackgroundProperty, "Petal.ControlHover"); };
        card.MouseLeave += (_, _) => { if (_expanded != key) card.SetResourceReference(Border.BackgroundProperty, "Petal.Control"); };
        return card;
    }

    private TextBlock? _tryStatus;

    /// <summary>The chosen entry marked in the list and read in full beside it; the first when none is chosen.</summary>
    private void PageRefold()
    {
        if ((_expanded is null || !_rows.ContainsKey(_expanded)) && _rows.Count > 0)
            _expanded = _rows.Keys.First();
        foreach (var (key, row) in _rows)
        {
            if (row is Border card)
                card.SetResourceReference(Border.BackgroundProperty, key == _expanded ? "Petal.AccentSoft" : "Petal.Control");
        }
        ShowDetail(_expanded);
    }

    private string? _shown;

    private void ShowDetail(string? key)
    {
        if (key == _shown && _detail.Children.Count > 0)
            return;
        _shown = key;
        _detail.Children.Clear();
        if (key is null || GuideBook.Get(key) is not { } e)
        {
            _detail.Children.Add(Text("Choose a tool to read about it here.", "Petal.Caption"));
            return;
        }
        _detail.Children.Add(GuideViews.Header(key, GuideBook.Name(key), 26));
        var where = Text("", "Petal.Caption").WithMargin(0, -4, 0, 10);
        where.Text = GuideBook.CategoryName(e.Category);
        _detail.Children.Add(where);
        _detail.Children.Add(GuideViews.Paragraph(e.Summary, bold: true));
        _detail.Children.Add(GuideViews.Explanation(key, withHeader: false, withSummary: false).WithMargin(0, 8, 0, 0));
        var actions = Actions(key);
        _tryStatus = actions is Panel panel ? panel.Children.OfType<TextBlock>().FirstOrDefault() : null;
        _detail.Children.Add(actions);
        _detailScroll?.ScrollToTop();
    }

    /// <summary>Keeps the narrow page's list of sections on the section shown.</summary>
    private void PickSection(string section)
    {
        if (_sectionPicker is null)
            return;
        _picking = true;
        _sectionPicker.SelectedItem = _sectionPicker.Items.OfType<ComboBoxItem>().FirstOrDefault(i => Equals(i.Tag, section));
        _picking = false;
    }

    /// <summary>For tests: the entries listed, and the one read in full.</summary>
    internal IReadOnlyList<string> ListedForTest => [.. _rows.Keys];

    internal string? ShownForTest => _shown;

    internal void StepForTest(int by) => Step(by);

    internal void FindForTest(string request) => ShowFound(GuideSearch.Find(request, 12));
}
