using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Petal.App.Localization;
using Petal.App.Shell;
using Petal.Engines.Documents;

namespace Petal.App.Actions.Float;

/// <summary>
/// A spreadsheet's cells as a table: columns lettered, rows numbered, every cell as Excel shows it, the sheets as tabs
/// below. Many thousands of rows stay quick (only those in view are drawn). Cells are chosen and copied, and found.
/// </summary>
internal sealed class SheetView : FloatView
{
    private readonly DataGrid _grid = new()
    {
        AutoGenerateColumns = false,
        IsReadOnly = true,
        CanUserAddRows = false,
        CanUserDeleteRows = false,
        CanUserReorderColumns = false,
        CanUserSortColumns = false,
        SelectionUnit = DataGridSelectionUnit.CellOrRowHeader,
        SelectionMode = DataGridSelectionMode.Extended,
        ClipboardCopyMode = DataGridClipboardCopyMode.ExcludeHeader,
        EnableRowVirtualization = true,
        EnableColumnVirtualization = true,
        HeadersVisibility = DataGridHeadersVisibility.All,
        GridLinesVisibility = DataGridGridLinesVisibility.All,
        RowHeaderWidth = 46,
        BorderThickness = new Thickness(0),
        FontSize = 13,
    };
    private readonly StackPanel _tabs = new() { Orientation = Orientation.Horizontal };
    private IReadOnlyList<PreviewSheet> _sheets = [];
    private int _sheet;
    private (int Row, int Column) _foundAt = (0, -1);
    private string _sought = "";

    public SheetView(string path) : base(path)
    {
        RowDefinitions.Add(new RowDefinition());
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _grid.SetResourceReference(Control.BackgroundProperty, "Petal.Popup");
        _grid.SetResourceReference(DataGrid.RowBackgroundProperty, "Petal.Popup");
        _grid.SetResourceReference(Control.ForegroundProperty, "Petal.Text");
        _grid.SetResourceReference(DataGrid.HorizontalGridLinesBrushProperty, "Petal.Divider");
        _grid.SetResourceReference(DataGrid.VerticalGridLinesBrushProperty, "Petal.Divider");
        _grid.LoadingRow += (_, e) => e.Row.Header = (e.Row.GetIndex() + 1).ToString(System.Globalization.CultureInfo.CurrentCulture);
        var tabs = new ScrollViewer { Content = _tabs, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(6, 4, 6, 4) };
        tabs.SetResourceReference(Control.BackgroundProperty, "Petal.Control");
        Grid.SetRow(tabs, 1);
        Children.Add(Note(L.T("Opening…")));
        Children.Add(tabs);
        _ = ReadAsync();
    }

    public override FloatKind Kind => FloatKind.Sheet;

    public override bool CanFind => _sheets.Count > 0;

    private async Task ReadAsync()
    {
        try
        {
            _sheets = await Task.Run(() => Viewing.ReadSheets(Path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or InvalidDataException or System.Xml.XmlException or FormatException)
        {
            Log.Info($"float: {System.IO.Path.GetFileName(Path)} not read as a sheet ({ex.Message})");
            Children.Clear();
            Children.Add(QuickLookWindow.Fallback(Path));
            Tell(QuickLookWindow.Facts(Path, null));
            return;
        }
        Children.RemoveAt(0);
        Children.Insert(0, _grid);
        _tabs.Children.Clear();
        for (int i = 0; i < _sheets.Count; i++)
        {
            int index = i;
            var tab = new Button { Content = _sheets[i].Name, Padding = new Thickness(12, 3, 12, 4), Margin = new Thickness(0, 0, 4, 0), Style = (Style)Application.Current.FindResource("Petal.SubtleButton"), BorderThickness = new Thickness(1) };
            tab.Click += (_, _) => ShowSheet(index);
            _tabs.Children.Add(tab);
        }
        ((FrameworkElement)_tabs.Parent).Visibility = _sheets.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        ShowSheet(0);
    }

    /// <summary>A column's letters, as Excel writes them: A … Z, AA, AB …</summary>
    internal static string Letters(int column)
    {
        var letters = "";
        for (int n = column + 1; n > 0; n = (n - 1) / 26)
            letters = (char)('A' + (n - 1) % 26) + letters;
        return letters;
    }

    private void ShowSheet(int index)
    {
        if (_sheets.Count == 0)
            return;
        _sheet = index;
        _foundAt = (0, -1);
        var sheet = _sheets[index];
        foreach (var (tab, i) in _tabs.Children.OfType<Button>().Select((t, i) => (t, i)))
        {
            tab.FontWeight = i == index ? FontWeights.SemiBold : FontWeights.Normal;
            tab.SetResourceReference(Control.BorderBrushProperty, i == index ? "Petal.Accent" : "Petal.ControlBorder");
        }
        _grid.Columns.Clear();
        for (int c = 0; c < Math.Max(1, sheet.Columns); c++)
            _grid.Columns.Add(new DataGridTextColumn { Header = Letters(c), Binding = new Binding($"[{c}]"), MaxWidth = 420 });
        // Each row as many cells as the widest, so every column has a cell to show.
        _grid.ItemsSource = sheet.Rows.Select(r => r.Concat(Enumerable.Repeat("", Math.Max(0, sheet.Columns - r.Count))).ToArray()).ToList();
        // A sheet written in Arabic, Persian or Urdu reads from the right.
        var sample = string.Concat(sheet.Rows.Take(40).SelectMany(r => r.Take(12)));
        _grid.FlowDirection = Petal.Core.Text.KeyboardLayout.LooksArabic(sample) ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        var facts = L.P(sheet.Rows.Count, "{0} row", "{0} rows") + " · " + L.P(sheet.Columns, "{0} column", "{0} columns");
        if (sheet.Cut)
            facts += " · " + L.T("only the first 100,000 rows are shown");
        Tell(QuickLookWindow.Facts(Path, facts));
    }

    public override async Task<(int At, int Of)> FindAsync(string text, bool forward, CancellationToken cancellationToken)
    {
        if (_sheets.Count == 0 || text.Trim().Length == 0)
            return (0, 0);
        var rows = _sheets[_sheet].Rows;
        var hits = await Task.Run(() =>
        {
            var found = new List<(int, int)>();
            for (int r = 0; r < rows.Count; r++)
                for (int c = 0; c < rows[r].Count; c++)
                    if (rows[r][c].Contains(text.Trim(), StringComparison.CurrentCultureIgnoreCase))
                        found.Add((r, c));
            return found;
        }, cancellationToken);
        if (hits.Count == 0)
            return (0, 0);
        int at = text != _sought ? (forward ? 0 : hits.Count - 1) : (hits.IndexOf(_foundAt) is var i and >= 0 ? (i + (forward ? 1 : -1) + hits.Count) % hits.Count : 0);
        _sought = text;
        _foundAt = hits[at];
        var (row, column) = _foundAt;
        var item = _grid.Items[row];
        _grid.ScrollIntoView(item, _grid.Columns[column]);
        _grid.SelectedCells.Clear();
        _grid.CurrentCell = new DataGridCellInfo(item, _grid.Columns[column]);
        _grid.SelectedCells.Add(_grid.CurrentCell);
        return (at + 1, hits.Count);
    }

    public override bool Copy()
    {
        if (_grid.SelectedCells.Count == 0)
            return false;
        ApplicationCommands.Copy.Execute(null, _grid);
        return true;
    }
}
