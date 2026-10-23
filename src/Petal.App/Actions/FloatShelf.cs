using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using Petal.App.Input;
using Petal.App.Shell;
using Petal.App.Visuals;
using static Petal.App.Interop.NativeMethods;
using Petal.App.Localization;

namespace Petal.App.Actions;

/// <summary>
/// A floating, always-on-top shelf that holds files while you work. Drag items out of it into
/// any app; drop more onto it; pin it to a screen edge to tuck it away.
/// </summary>
public sealed class FloatShelf : Window
{
    private readonly ObservableCollection<string> _items = [];
    private readonly WrapPanel _grid = new() { Orientation = Orientation.Horizontal };
    private readonly TextBlock _title = new();
    private readonly Border _card = new();
    private bool _tucked;
    private double _restLeft;
    private Point _dragStart;
    private string? _dragCandidate;

    public FloatShelf()
    {
        WindowStyle = WindowStyle.None;
        L.Apply(this);
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        AllowDrop = true;
        Title = L.T("Float");
        Content = Build();
        DragOver += (_, e) =>
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Link : DragDropEffects.None;
            e.Handled = true;
        };
        Drop += (_, e) =>
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
                Add(files);
            e.Handled = true;
        };
        _items.CollectionChanged += (_, _) => Refresh();
        Theme.Changed += () => Dispatcher.BeginInvoke(() => Content = Build());
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        var ex = (long)GetWindowLongPtr(hwnd, GWL_EXSTYLE);
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(ex | WS_EX_TOOLWINDOW));
    }

    public void AddAndShow(IReadOnlyList<string> paths, ScreenPoint at)
    {
        Add(paths);
        if (!IsVisible)
        {
            var (_, work, scale) = MonitorAt(new POINT(at.X, at.Y));
            Show();
            UpdateLayout();
            Left = Math.Clamp(at.X / scale - ActualWidth / 2, work.Left / scale, work.Right / scale - ActualWidth);
            Top = Math.Clamp(at.Y / scale - ActualHeight - 30, work.Top / scale, work.Bottom / scale - ActualHeight);
            var s = new ScaleTransform(0.5, 0.5);
            _card.RenderTransformOrigin = new Point(0.5, 1);
            _card.RenderTransform = s;
            s.BeginAnimation(ScaleTransform.ScaleXProperty, Motion.To(1, 420, Motion.Pop, from: 0.5));
            s.BeginAnimation(ScaleTransform.ScaleYProperty, Motion.To(1, 420, Motion.Pop, from: 0.5));
        }
        if (_tucked)
            Untuck();
    }

    private void Add(IEnumerable<string> paths)
    {
        foreach (var p in paths)
            if (!_items.Contains(p, StringComparer.OrdinalIgnoreCase))
                _items.Add(p);
    }

    private UIElement Build()
    {
        var p = Theme.Current;
        _card.CornerRadius = new CornerRadius(18);
        _card.Background = Palette.Brush(p.IsDark ? Color.FromArgb(0xF0, 0x26, 0x26, 0x2C) : Color.FromArgb(0xF2, 0xFF, 0xFF, 0xFF));
        _card.BorderBrush = Palette.Brush(p.GlassEdge);
        _card.BorderThickness = new Thickness(1);
        _card.Padding = new Thickness(12, 10, 12, 12);
        _card.Margin = new Thickness(20);
        _card.Effect = new DropShadowEffect { BlurRadius = 24, ShadowDepth = 6, Direction = 270, Opacity = p.IsDark ? 0.5 : 0.2 };

        var layout = new DockPanel { LastChildFill = true };
        var header = new DockPanel { Margin = new Thickness(2, 0, 0, 8) };
        DockPanel.SetDock(header, Dock.Top);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        DockPanel.SetDock(buttons, Dock.Right);
        buttons.Children.Add(HeaderButton("pin", "Tuck into the screen edge", Tuck));
        buttons.Children.Add(HeaderButton("trash", "Clear the shelf", () => _items.Clear()));
        buttons.Children.Add(HeaderButton("x", "Close", Close));
        header.Children.Add(buttons);

        _title.FontFamily = UiFonts.Text;
        _title.FontSize = 12.5;
        _title.FontWeight = FontWeights.SemiBold;
        _title.Foreground = Palette.Brush(p.Text);
        _title.VerticalAlignment = VerticalAlignment.Center;
        header.Children.Add(_title);
        Screen.ScreenUi.DragFromBackground(this, header);
        header.Background = Brushes.Transparent;
        header.Cursor = Cursors.SizeAll;

        layout.Children.Add(header);
        _grid.MaxWidth = 92 * 4;
        layout.Children.Add(new ScrollViewer
        {
            Content = _grid,
            MaxHeight = 92 * 3 + 8,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        });
        _card.Child = layout;
        Refresh();
        return _card;
    }

    private Border HeaderButton(string icon, string tip, Action click)
    {
        var p = Theme.Current;
        var b = new Border
        {
            Width = 24,
            Height = 24,
            CornerRadius = new CornerRadius(12),
            Margin = new Thickness(4, 0, 0, 0),
            Background = Brushes.Transparent,
            ToolTip = L.T(tip),
            Cursor = Cursors.Hand,
            Child = Icons.Create(icon, 13, Palette.Brush(p.TextSecondary), 2.2),
        };
        ((FrameworkElement)b.Child).HorizontalAlignment = HorizontalAlignment.Center;
        ((FrameworkElement)b.Child).VerticalAlignment = VerticalAlignment.Center;
        b.MouseEnter += (_, _) => b.Background = Palette.Brush(p.Control);
        b.MouseLeave += (_, _) => b.Background = Brushes.Transparent;
        b.MouseLeftButtonDown += (_, e) => e.Handled = true;
        b.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            click();
        };
        return b;
    }

    private void Refresh()
    {
        var p = Theme.Current;
        _title.Text = _items.Count == 0 ? L.T("Drop files here") : L.P(_items.Count, "{0} item", "{0} items");
        _grid.Children.Clear();
        foreach (var path in _items)
            _grid.Children.Add(Tile(path, p));
    }

    private FrameworkElement Tile(string path, Palette p)
    {
        var stack = new StackPanel { Width = 84, Margin = new Thickness(4) };
        var image = new Image { FlowDirection = FlowDirection.LeftToRight, Width = 48, Height = 48, Source = ShellIcons.Thumbnail(path, 96), Margin = new Thickness(0, 4, 0, 4) };
        stack.Children.Add(image);
        stack.Children.Add(new TextBlock
        {
            Text = Path.GetFileName(path),
            FontFamily = UiFonts.Text,
            FontSize = 11,
            Foreground = Palette.Brush(p.Text),
            TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxHeight = 30,
            TextWrapping = TextWrapping.Wrap,
        });
        var tile = new Border
        {
            Child = stack,
            CornerRadius = new CornerRadius(10),
            Background = Brushes.Transparent,
            ToolTip = path,
            Cursor = Cursors.Hand,
        };
        tile.MouseEnter += (_, _) => tile.Background = Palette.Brush(p.Control);
        tile.MouseLeave += (_, _) => tile.Background = Brushes.Transparent;
        tile.PreviewMouseLeftButtonDown += (_, e) =>
        {
            _dragStart = e.GetPosition(this);
            _dragCandidate = path;
            if (e.ClickCount == 2)
                ExplorerSelection.Reveal([path]);
        };
        tile.PreviewMouseMove += (_, e) =>
        {
            if (e.LeftButton != MouseButtonState.Pressed || _dragCandidate != path)
                return;
            var delta = e.GetPosition(this) - _dragStart;
            if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance)
                return;
            _dragCandidate = null;
            var data = new DataObject(DataFormats.FileDrop, new[] { path });
            var result = DragDrop.DoDragDrop(tile, data, DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link);
            if (result == DragDropEffects.Move || !File.Exists(path) && !Directory.Exists(path))
                _items.Remove(path);
        };
        tile.MouseRightButtonUp += (_, _) => _items.Remove(path);
        return tile;
    }

    private void Tuck()
    {
        if (_tucked)
        {
            Untuck();
            return;
        }
        var work = SystemParameters.WorkArea;
        _restLeft = Left;
        _tucked = true;
        bool right = Left + ActualWidth / 2 > work.Left + work.Width / 2;
        var target = right ? work.Right - 44 : work.Left - ActualWidth + 44;
        BeginAnimation(LeftProperty, Motion.To(target, 260, Motion.Standard));
        MouseEnter += PeekIn;
    }

    private void PeekIn(object sender, MouseEventArgs e) => Untuck();

    private void Untuck()
    {
        if (!_tucked)
            return;
        _tucked = false;
        MouseEnter -= PeekIn;
        BeginAnimation(LeftProperty, Motion.To(_restLeft, 300, Motion.Pop));
    }
}
