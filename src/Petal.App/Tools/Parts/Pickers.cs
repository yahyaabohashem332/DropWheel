using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Petal.App.Shell;
using Petal.App.Visuals;

namespace Petal.App.Tools.Parts;

/// <summary>Reorderable list of files: index, thumbnail, name, and up/down buttons (or drag a row).</summary>
public sealed class OrderList : StackPanel
{
    private readonly ObservableCollection<string> _items;
    private int _dragFrom = -1;

    public event Action? Changed;

    public OrderList(IEnumerable<string> files)
    {
        _items = new ObservableCollection<string>(files);
        _items.CollectionChanged += (_, _) =>
        {
            Rebuild();
            Changed?.Invoke();
        };
        Rebuild();
    }

    public IReadOnlyList<string> Items => _items;

    private void Rebuild()
    {
        Children.Clear();
        for (int i = 0; i < _items.Count; i++)
            Children.Add(Row(i));
    }

    private UIElement Row(int index)
    {
        var path = _items[index];
        var row = new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10, 6, 6, 6),
            Margin = new Thickness(0, 0, 0, 6),
            AllowDrop = true,
            Cursor = Cursors.SizeNS,
        };
        row.SetResourceReference(Border.BackgroundProperty, "Petal.Control");
        var dock = new DockPanel();
        var number = new TextBlock { Text = (index + 1).ToString(CultureInfo.CurrentCulture), Width = 20, VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold };
        number.SetResourceReference(TextBlock.ForegroundProperty, "Petal.TextSecondary");
        DockPanel.SetDock(number, Dock.Left);
        dock.Children.Add(number);
        var thumb = new Image { FlowDirection = FlowDirection.LeftToRight, Width = 36, Height = 28, Stretch = Stretch.UniformToFill, Source = ShellIcons.Thumbnail(path, 64), Margin = new Thickness(0, 0, 10, 0) };
        thumb.Clip = new RectangleGeometry(new Rect(0, 0, 36, 28), 4, 4);
        DockPanel.SetDock(thumb, Dock.Left);
        dock.Children.Add(thumb);

        var buttons = new StackPanel { Orientation = Orientation.Vertical };
        DockPanel.SetDock(buttons, Dock.Right);
        var up = Chevron("chevronUp", index > 0, () => Move(index, index - 1));
        var down = Chevron("chevronDown", index < _items.Count - 1, () => Move(index, index + 1));
        buttons.Children.Add(up);
        buttons.Children.Add(down);
        dock.Children.Add(buttons);

        var name = new TextBlock { Text = System.IO.Path.GetFileName(path), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        name.SetResourceReference(TextBlock.ForegroundProperty, "Petal.Text");
        dock.Children.Add(name);
        row.Child = dock;

        row.PreviewMouseLeftButtonDown += (_, _) => _dragFrom = index;
        row.MouseMove += (_, e) =>
        {
            if (_dragFrom == index && e.LeftButton == MouseButtonState.Pressed)
                DragDrop.DoDragDrop(row, new DataObject("PetalRow", index), DragDropEffects.Move);
        };
        row.Drop += (_, e) =>
        {
            if (e.Data.GetData("PetalRow") is int from && from != index)
                Move(from, index);
            _dragFrom = -1;
        };
        return row;
    }

    private static Button Chevron(string icon, bool enabled, Action click)
    {
        var b = new Button
        {
            Style = (Style)Application.Current.FindResource("Petal.IconButton"),
            Width = 22,
            Height = 16,
            IsEnabled = enabled,
            Content = ToolWindow.ThemedIcon(icon, 12, "Petal.TextSecondary", 2.4),
        };
        b.Click += (_, _) => click();
        return b;
    }

    private void Move(int from, int to)
    {
        if (to < 0 || to >= _items.Count) return;
        _items.Move(from, to);
    }
}

/// <summary>A capsule showing a colour; click for swatches and a custom hex value.</summary>
public sealed class ColorWell : Button
{
    private static readonly Color[] Swatches =
    [
        Colors.Black, Color.FromRgb(0x40, 0x40, 0x48), Color.FromRgb(0x8E, 0x8E, 0x96), Colors.White,
        Color.FromRgb(0xEB, 0x4D, 0x47), Color.FromRgb(0xFF, 0x8A, 0x30), Color.FromRgb(0xFF, 0xD2, 0x3F), Color.FromRgb(0x34, 0xC7, 0x59),
        Color.FromRgb(0x2E, 0xB8, 0xC4), Color.FromRgb(0x3B, 0x82, 0xF6), Color.FromRgb(0x7B, 0x5C, 0xFF), Color.FromRgb(0xE8, 0x4F, 0xB0),
    ];

    private readonly Border _chip = new() { CornerRadius = new CornerRadius(8), Width = 38, Height = 18, BorderThickness = new Thickness(1) };
    private readonly Popup _popup = new() { StaysOpen = false, AllowsTransparency = true, Placement = PlacementMode.Bottom, VerticalOffset = 4 };
    private Color _color;

    public event Action<Color>? Changed;

    public ColorWell(Color initial)
    {
        Style = (Style)Application.Current.FindResource("Petal.IconButton");
        Width = 48;
        Height = 28;
        _chip.SetResourceReference(Border.BorderBrushProperty, "Petal.ControlBorder");
        Content = _chip;
        Color = initial;
        _popup.PlacementTarget = this;
        _popup.Child = BuildPalette();
        Click += (_, _) => _popup.IsOpen = !_popup.IsOpen;
        HorizontalAlignment = HorizontalAlignment.Left;
    }

    public Color Color
    {
        get => _color;
        set
        {
            _color = value;
            _chip.Background = Palette.Brush(value);
        }
    }

    private UIElement BuildPalette()
    {
        var grid = new UniformGrid { Columns = 4, Margin = new Thickness(0, 0, 0, 8) };
        foreach (var c in Swatches)
        {
            var swatch = new Border
            {
                Width = 26,
                Height = 26,
                Margin = new Thickness(3),
                CornerRadius = new CornerRadius(13),
                Background = Palette.Brush(c),
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
            };
            swatch.SetResourceReference(Border.BorderBrushProperty, "Petal.ControlBorder");
            var color = c;
            swatch.MouseLeftButtonUp += (_, _) => Pick(color);
            grid.Children.Add(swatch);
        }
        var hex = new TextBox { Text = Hex(_color), Width = 130 };
        hex.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && TryParse(hex.Text, out var c))
                Pick(c);
        };
        var panel = new StackPanel();
        panel.Children.Add(grid);
        panel.Children.Add(hex);
        var border = new Border
        {
            Padding = new Thickness(10),
            Margin = new Thickness(8, 0, 8, 14),
            CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(1),
            Child = panel,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, ShadowDepth = 4, Direction = 270, Opacity = 0.2 },
        };
        border.SetResourceReference(Border.BackgroundProperty, "Petal.Popup");
        border.SetResourceReference(Border.BorderBrushProperty, "Petal.ControlBorder");
        return border;
    }

    private void Pick(Color c)
    {
        Color = c;
        _popup.IsOpen = false;
        Changed?.Invoke(c);
    }

    public static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    public static bool TryParse(string text, out Color color)
    {
        color = default;
        try
        {
            var t = text.Trim();
            if (!t.StartsWith('#')) t = "#" + t;
            color = (Color)ColorConverter.ConvertFromString(t);
            return true;
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>A redaction rectangle in source pixels.</summary>
public sealed class RedactionBox(Rect rect)
{
    public Rect Rect { get; set; } = rect;
    public int Number { get; set; }
}

/// <summary>
/// Image with movable, resizable redaction boxes: drag on empty space to draw one, drag a box to
/// move it, drag its corner to resize, Delete removes the selected box.
/// </summary>
public sealed class RedactionCanvas : Grid
{
    private readonly Image _image = new() { FlowDirection = FlowDirection.LeftToRight, Stretch = Stretch.Uniform };
    private readonly Canvas _canvas = new() { Background = Brushes.Transparent, ClipToBounds = true, Cursor = Cursors.Cross };
    private readonly List<(RedactionBox Box, Border Visual)> _boxes = [];
    private Size _source;
    private RedactionBox? _selected;
    private int _mode; // 1 draw, 2 move, 3 resize
    private Point _start;
    private Rect _startRect;
    private int _counter;

    public event Action? Changed;
    public event Action<RedactionBox?>? SelectionChanged;

    public RedactionCanvas()
    {
        FlowDirection = FlowDirection.LeftToRight;
        Children.Add(_image);
        Children.Add(_canvas);
        Focusable = true;
        _canvas.MouseLeftButtonDown += OnDown;
        _canvas.MouseMove += OnMove;
        _canvas.MouseLeftButtonUp += (_, _) => Finish();
        SizeChanged += (_, _) => Layout();
        KeyDown += (_, e) =>
        {
            if (e.Key is Key.Delete or Key.Back && _selected is not null)
            {
                Remove(_selected);
                e.Handled = true;
            }
        };
    }

    public ImageSource? Source
    {
        get => _image.Source;
        set
        {
            _image.Source = value;
            Layout();
        }
    }

    public Size SourceSize
    {
        get => _source;
        set
        {
            _source = value;
            Layout();
        }
    }

    /// <summary>Fill used to show boxes: a solid colour, or a frosted look for blur.</summary>
    public Brush BoxFill { get; set; } = Brushes.Black;

    public IReadOnlyList<RedactionBox> Boxes => _boxes.Select(b => b.Box).ToList();

    public RedactionBox? Selected => _selected;

    public RedactionBox Add(Rect rect)
    {
        var box = new RedactionBox(rect) { Number = ++_counter };
        var visual = new Border { BorderThickness = new Thickness(0), Cursor = Cursors.SizeAll, CornerRadius = new CornerRadius(2) };
        _boxes.Add((box, visual));
        _canvas.Children.Add(visual);
        Select(box);
        Layout();
        Changed?.Invoke();
        return box;
    }

    public void Remove(RedactionBox box)
    {
        var i = _boxes.FindIndex(b => b.Box == box);
        if (i < 0) return;
        _canvas.Children.Remove(_boxes[i].Visual);
        _boxes.RemoveAt(i);
        Select(_boxes.Count > 0 ? _boxes[^1].Box : null);
        Changed?.Invoke();
    }

    public void Clear()
    {
        foreach (var (_, v) in _boxes) _canvas.Children.Remove(v);
        _boxes.Clear();
        _counter = 0;
        Select(null);
        Changed?.Invoke();
    }

    public void Refresh() => Layout();

    private void Select(RedactionBox? box)
    {
        _selected = box;
        Layout();
        SelectionChanged?.Invoke(box);
    }

    private Rect ImageRect()
    {
        if (_source.Width <= 0 || ActualWidth <= 0) return Rect.Empty;
        double s = Math.Min(ActualWidth / _source.Width, ActualHeight / _source.Height);
        return new Rect((ActualWidth - _source.Width * s) / 2, (ActualHeight - _source.Height * s) / 2, _source.Width * s, _source.Height * s);
    }

    private double Scale => _source.Width > 0 ? ImageRect().Width / _source.Width : 1;

    private Point ToSource(Point p)
    {
        var img = ImageRect();
        return new Point(Math.Clamp((p.X - img.X) / Scale, 0, _source.Width), Math.Clamp((p.Y - img.Y) / Scale, 0, _source.Height));
    }

    private void Layout()
    {
        var img = ImageRect();
        if (img.IsEmpty) return;
        double s = Scale;
        var accent = (Brush)FindResource("Petal.Accent");
        foreach (var (box, visual) in _boxes)
        {
            Canvas.SetLeft(visual, img.X + box.Rect.X * s);
            Canvas.SetTop(visual, img.Y + box.Rect.Y * s);
            visual.Width = Math.Max(4, box.Rect.Width * s);
            visual.Height = Math.Max(4, box.Rect.Height * s);
            visual.Background = BoxFill;
            bool sel = box == _selected;
            visual.BorderBrush = sel ? accent : null;
            visual.BorderThickness = new Thickness(sel ? 2 : 0);
            // Resize grip in the corner of the selected box.
            visual.Child = sel ? new Border
            {
                Width = 11,
                Height = 11,
                CornerRadius = new CornerRadius(3),
                Background = Brushes.White,
                BorderBrush = accent,
                BorderThickness = new Thickness(2),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, -6, -6),
                Cursor = Cursors.SizeNWSE,
            } : null;
        }
    }

    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        Focus();
        var p = e.GetPosition(_canvas);
        var src = ToSource(p);
        var img = ImageRect();
        var hit = _boxes.LastOrDefault(b =>
        {
            var r = new Rect(img.X + b.Box.Rect.X * Scale, img.Y + b.Box.Rect.Y * Scale, b.Box.Rect.Width * Scale, b.Box.Rect.Height * Scale);
            r.Inflate(6, 6);
            return r.Contains(p);
        });
        if (hit.Box is not null)
        {
            Select(hit.Box);
            var screen = new Rect(img.X + hit.Box.Rect.X * Scale, img.Y + hit.Box.Rect.Y * Scale, hit.Box.Rect.Width * Scale, hit.Box.Rect.Height * Scale);
            _mode = (p - screen.BottomRight).Length < 14 ? 3 : 2;
        }
        else
        {
            Add(new Rect(src, new Size(1, 1)));
            _mode = 1;
        }
        _start = src;
        _startRect = _selected!.Rect;
        _canvas.CaptureMouse();
        e.Handled = true;
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        if (_mode == 0 || _selected is null || e.LeftButton != MouseButtonState.Pressed) return;
        var p = ToSource(e.GetPosition(_canvas));
        var r = _startRect;
        switch (_mode)
        {
            case 1:
                _selected.Rect = new Rect(_start, p);
                break;
            case 2:
                var d = p - _start;
                _selected.Rect = new Rect(Math.Clamp(r.X + d.X, 0, _source.Width - r.Width), Math.Clamp(r.Y + d.Y, 0, _source.Height - r.Height), r.Width, r.Height);
                break;
            case 3:
                _selected.Rect = new Rect(r.TopLeft, new Point(Math.Max(r.X + 6, p.X), Math.Max(r.Y + 6, p.Y)));
                break;
        }
        Layout();
        Changed?.Invoke();
    }

    private void Finish()
    {
        if (_mode == 1 && _selected is { } box && (box.Rect.Width < 6 || box.Rect.Height < 6))
        {
            // A click instead of a drag: drop a reasonable default box there.
            double w = _source.Width * 0.18, h = _source.Height * 0.1;
            box.Rect = new Rect(Math.Clamp(box.Rect.X - w / 2, 0, _source.Width - w), Math.Clamp(box.Rect.Y - h / 2, 0, _source.Height - h), w, h);
            Layout();
        }
        _mode = 0;
        if (_canvas.IsMouseCaptured) _canvas.ReleaseMouseCapture();
        Changed?.Invoke();
    }
}
