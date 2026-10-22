using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Petal.App.Input;
using Petal.App.Localization;
using Petal.App.Visuals;
using static Petal.App.Interop.NativeMethods;

namespace Petal.App.Screen;

public enum DrawTool
{
    Pen,
    Highlighter,
    Line,
    Arrow,
    Rectangle,
    Ellipse,
    Text,
    Eraser,
}

/// <summary>What is under the drawing: the live screen, a still of it, or a white or black board.</summary>
public enum DrawBoard
{
    Live,
    Still,
    White,
    Black,
}

/// <summary>
/// Drawing on the screen: pen, highlighter, lines, arrows, boxes, rings and text over whatever is on the
/// monitor under the pointer, over a still of it, or on a white or black board. Everything drawn can be
/// undone and redone; the drawing can be saved (and opened on the conversion wheel) or copied. With "let
/// clicks through" the drawing stays while the apps under it are used. Esc puts it away.
/// </summary>
internal sealed class DrawOverlay : MonitorWindow
{
    private static DrawOverlay? _open;

    /// <summary>The colours on the strip; red first, as pens usually are.</summary>
    public static readonly Color[] Colours =
    [
        Color.FromRgb(0xEF, 0x44, 0x44), Color.FromRgb(0xF9, 0x73, 0x16), Color.FromRgb(0xFA, 0xCC, 0x15), Color.FromRgb(0x22, 0xC5, 0x5E),
        Color.FromRgb(0x3B, 0x82, 0xF6), Color.FromRgb(0xA8, 0x55, 0xF7), Colors.White, Color.FromRgb(0x11, 0x11, 0x14),
    ];

    public static readonly double[] Sizes = [2.5, 4.5, 9];

    private readonly Grid _root = new();
    private readonly Rectangle _backdrop = new();
    private readonly InkCanvas _ink = new() { Background = Brushes.Transparent };
    private readonly Canvas _preview = new() { IsHitTestVisible = false };
    private readonly Action<string, ScreenPoint>? _openFiles;
    private readonly Stack<Change> _undo = new(), _redo = new();
    private DrawToolbar? _bar;
    private BitmapSource? _still;
    private DrawBoard _board;
    private DrawTool _tool = DrawTool.Pen;
    private Color _colour = Colours[0];
    private int _size = 1;
    private bool _replaying;
    private Point? _shapeStart;
    private System.Windows.Shapes.Path? _shapePreview;
    private TextBox? _editing;

    /// <summary>One step that can be undone: strokes added or taken away, text added or taken away.</summary>
    private sealed record Change(StrokeCollection? Added, StrokeCollection? Removed, List<UIElement>? TextAdded, List<UIElement>? TextRemoved);

    public static bool IsOpen => _open is not null;

    /// <summary>Starts drawing on the monitor under the pointer (or puts the drawing away when it is out).</summary>
    public static void Toggle(DrawBoard board, Action<string, ScreenPoint>? openFiles, BitmapSource? still = null)
    {
        if (_open is not null)
        {
            if (board == DrawBoard.Live)
            {
                _open.Close();
                return;
            }
            _open.SetBoard(board);
            _open.Activate();
            return;
        }
        var monitor = ScreenNative.UnderPointer();
        still ??= board == DrawBoard.Still ? ScreenUi.Still(monitor.Bounds) : null;
        _open = new DrawOverlay(monitor.Bounds, board, still, openFiles);
        _open.Closed += (_, _) => _open = null;
        _open.Show();
        _open.Activate();
    }

    internal static DrawOverlay ForSnapshot() => new(new RECT { Right = 1920, Bottom = 1080 }, DrawBoard.Live, null, null);

    private DrawOverlay(RECT bounds, DrawBoard board, BitmapSource? still, Action<string, ScreenPoint>? openFiles) : base(bounds)
    {
        _openFiles = openFiles;
        _still = still;
        Title = L.T("Draw on Screen");
        _root.Children.Add(_backdrop);
        _root.Children.Add(_ink);
        _root.Children.Add(_preview);
        Content = _root;
        _ink.EditingMode = InkCanvasEditingMode.Ink;
        _ink.Strokes.StrokesChanged += OnStrokesChanged;
        _ink.PreviewMouseLeftButtonDown += OnDown;
        _ink.PreviewMouseMove += OnMove;
        _ink.PreviewMouseLeftButtonUp += OnUp;
        _ink.UseCustomCursor = true;
        _ink.Cursor = Cursors.Pen;
        SetBoard(board);
        ApplyTool();
        Loaded += (_, _) =>
        {
            _bar = new DrawToolbar(this) { Owner = this };
            _bar.Show();
            _bar.PlaceOver(Bounds, Scale);
            Activate();
            Keyboard.Focus(_ink);
        };
        Closed += (_, _) => _bar?.Close();
    }

    public DrawTool Tool => _tool;
    public Color Colour => _colour;
    public int Size => _size;
    public DrawBoard Board => _board;
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    // ---------- choices ----------

    public void SetTool(DrawTool tool)
    {
        CommitText();
        _tool = tool;
        if (ClickThrough)
            ClickThrough = false;
        ApplyTool();
        _bar?.Refresh();
    }

    public void SetColour(Color colour)
    {
        _colour = colour;
        if (_editing is not null)
            _editing.Foreground = new SolidColorBrush(colour);
        ApplyTool();
        _bar?.Refresh();
    }

    public void SetSize(int size)
    {
        _size = Math.Clamp(size, 0, Sizes.Length - 1);
        ApplyTool();
        _bar?.Refresh();
    }

    public void SetBoard(DrawBoard board)
    {
        if (board == DrawBoard.Still && _still is null)
        {
            // A still taken now must not show the drawing or the strip.
            Opacity = 0;
            _bar?.Hide();
            Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
            Thread.Sleep(60);
            _still = ScreenUi.Still(Bounds);
            Opacity = 1;
            _bar?.Show();
        }
        _board = board;
        _backdrop.Fill = board switch
        {
            DrawBoard.Still when _still is not null => new ImageBrush(_still) { Stretch = Stretch.Fill },
            DrawBoard.White => new SolidColorBrush(Color.FromRgb(0xFB, 0xFB, 0xF8)),
            DrawBoard.Black => new SolidColorBrush(Color.FromRgb(0x1B, 0x22, 0x1F)),
            // Almost clear, but there: the pointer draws here instead of clicking the app below.
            _ => new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)),
        };
        // White ink on a white board can't be seen: switch to the dark one, and the other way round.
        if (board == DrawBoard.White && _colour == Colors.White)
            SetColour(Colours[^1]);
        else if (board == DrawBoard.Black && _colour == Colours[^1])
            SetColour(Colors.White);
        _bar?.Refresh();
    }

    /// <summary>Lets clicks reach the apps under the drawing (it stays on the screen), or takes them back.</summary>
    public void ToggleClickThrough()
    {
        CommitText();
        ClickThrough = !ClickThrough;
        _bar?.Refresh();
        if (!ClickThrough)
            Activate();
    }

    private void ApplyTool()
    {
        var size = Sizes[_size];
        switch (_tool)
        {
            case DrawTool.Pen:
                _ink.EditingMode = InkCanvasEditingMode.Ink;
                _ink.DefaultDrawingAttributes = new DrawingAttributes { Color = _colour, Width = size, Height = size, FitToCurve = true, StylusTip = StylusTip.Ellipse };
                _ink.Cursor = Cursors.Pen;
                break;
            case DrawTool.Highlighter:
                _ink.EditingMode = InkCanvasEditingMode.Ink;
                _ink.DefaultDrawingAttributes = new DrawingAttributes
                {
                    Color = _colour == Colours[0] ? Colours[2] : _colour,
                    Width = size * 3.2,
                    Height = size * 4.5,
                    IsHighlighter = true,
                    StylusTip = StylusTip.Rectangle,
                    FitToCurve = true,
                    IgnorePressure = true,
                };
                _ink.Cursor = Cursors.Pen;
                break;
            case DrawTool.Eraser:
                _ink.EditingMode = InkCanvasEditingMode.EraseByStroke;
                _ink.Cursor = Cursors.Cross;
                break;
            default:
                _ink.EditingMode = InkCanvasEditingMode.None;
                _ink.Cursor = _tool == DrawTool.Text ? Cursors.IBeam : Cursors.Cross;
                break;
        }
    }

    // ---------- shapes and text ----------

    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        if (_tool is DrawTool.Pen or DrawTool.Highlighter or DrawTool.Eraser)
            return;
        var p = e.GetPosition(_ink);
        if (_tool == DrawTool.Text)
        {
            if (e.OriginalSource is TextBox)
                return;
            CommitText();
            StartText(p);
            e.Handled = true;
            return;
        }
        _shapeStart = p;
        _shapePreview = new System.Windows.Shapes.Path { Stroke = new SolidColorBrush(_colour), StrokeThickness = Sizes[_size], StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round };
        _preview.Children.Add(_shapePreview);
        _ink.CaptureMouse();
        e.Handled = true;
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        if (_shapeStart is not { } start || _shapePreview is null)
            return;
        var points = ShapePoints(start, Constrain(start, e.GetPosition(_ink)));
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(points[0], false, false);
            ctx.PolyLineTo(points.Skip(1).ToList(), true, true);
        }
        _shapePreview.Data = g;
    }

    private void OnUp(object sender, MouseButtonEventArgs e)
    {
        if (_shapeStart is not { } start)
            return;
        _ink.ReleaseMouseCapture();
        var end = Constrain(start, e.GetPosition(_ink));
        _preview.Children.Clear();
        _shapePreview = null;
        _shapeStart = null;
        if ((end - start).Length < 3)
            return;
        var stroke = new Stroke(new StylusPointCollection(ShapePoints(start, end)))
        {
            DrawingAttributes = new DrawingAttributes { Color = _colour, Width = Sizes[_size], Height = Sizes[_size], FitToCurve = false, IgnorePressure = true },
        };
        _ink.Strokes.Add(stroke);
        e.Handled = true;
    }

    /// <summary>Shift keeps lines at 45° steps and boxes and rings round.</summary>
    private Point Constrain(Point start, Point end)
    {
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            return end;
        var v = end - start;
        if (_tool is DrawTool.Rectangle or DrawTool.Ellipse)
        {
            double side = Math.Max(Math.Abs(v.X), Math.Abs(v.Y));
            return new Point(start.X + Math.Sign(v.X) * side, start.Y + Math.Sign(v.Y) * side);
        }
        double angle = Math.Round(Math.Atan2(v.Y, v.X) / (Math.PI / 4)) * (Math.PI / 4);
        return start + new Vector(Math.Cos(angle), Math.Sin(angle)) * v.Length;
    }

    /// <summary>The line through a shape, as one pen would draw it: arrows double back to make their head.</summary>
    internal List<Point> ShapePoints(Point a, Point b) => ShapeOutline(_tool, a, b, Sizes[_size]);

    internal static List<Point> ShapeOutline(DrawTool tool, Point a, Point b, double size)
    {
        switch (tool)
        {
            case DrawTool.Arrow:
            {
                var v = b - a;
                double length = Math.Max(1, v.Length);
                var dir = v / length;
                double head = Math.Min(length * 0.45, 14 + size * 2.5);
                var back = b - dir * head;
                var side = new Vector(-dir.Y, dir.X) * head * 0.55;
                return [a, b, back + side, b, back - side];
            }
            case DrawTool.Rectangle:
                return [a, new Point(b.X, a.Y), b, new Point(a.X, b.Y), a];
            case DrawTool.Ellipse:
            {
                var c = new Point((a.X + b.X) / 2, (a.Y + b.Y) / 2);
                double rx = Math.Abs(b.X - a.X) / 2, ry = Math.Abs(b.Y - a.Y) / 2;
                var list = new List<Point>();
                for (int i = 0; i <= 72; i++)
                {
                    double t = i * Math.PI * 2 / 72;
                    list.Add(new Point(c.X + rx * Math.Cos(t), c.Y + ry * Math.Sin(t)));
                }
                return list;
            }
            default:
                return [a, b];
        }
    }

    private void StartText(Point at)
    {
        var box = new TextBox
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0, 0, 0, 1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x80, _colour.R, _colour.G, _colour.B)),
            Foreground = new SolidColorBrush(_colour),
            CaretBrush = new SolidColorBrush(_colour),
            FontFamily = UiFonts.Display,
            FontWeight = FontWeights.SemiBold,
            FontSize = 16 + Sizes[_size] * 3,
            AcceptsReturn = true,
            MinWidth = 60,
            Padding = new Thickness(2),
            FlowDirection = L.Direction,
        };
        // Arabic typed into it reads right to left.
        box.TextChanged += (_, _) =>
        {
            var first = box.Text.FirstOrDefault(char.IsLetter);
            box.FlowDirection = first is >= '؀' and <= 'ۿ' ? FlowDirection.RightToLeft : first == default ? L.Direction : FlowDirection.LeftToRight;
        };
        box.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (_tool == DrawTool.Eraser)
            {
                RemoveText(box);
                e.Handled = true;
            }
        };
        InkCanvas.SetLeft(box, at.X - 4);
        InkCanvas.SetTop(box, at.Y - box.FontSize * 0.75);
        _ink.Children.Add(box);
        _editing = box;
        box.Loaded += (_, _) => box.Focus();
    }

    /// <summary>Ends typing: the text stays (and can be undone), or goes when it is empty.</summary>
    private void CommitText()
    {
        if (_editing is not { } box)
            return;
        _editing = null;
        if (string.IsNullOrWhiteSpace(box.Text))
        {
            _ink.Children.Remove(box);
            return;
        }
        box.BorderThickness = new Thickness(0);
        box.IsReadOnly = true;
        box.Cursor = Cursors.Arrow;
        Push(new Change(null, null, [box], null));
        Keyboard.Focus(_ink);
    }

    private void RemoveText(UIElement text)
    {
        _ink.Children.Remove(text);
        Push(new Change(null, null, null, [text]));
    }

    // ---------- undo ----------

    private void OnStrokesChanged(object? sender, StrokeCollectionChangedEventArgs e)
    {
        if (_replaying)
            return;
        Push(new Change(e.Added.Count > 0 ? e.Added : null, e.Removed.Count > 0 ? e.Removed : null, null, null));
    }

    private void Push(Change change)
    {
        _undo.Push(change);
        _redo.Clear();
        _bar?.Refresh();
    }

    public void Undo() => Replay(_undo, _redo, backwards: true);

    public void Redo() => Replay(_redo, _undo, backwards: false);

    private void Replay(Stack<Change> from, Stack<Change> to, bool backwards)
    {
        CommitText();
        if (!from.TryPop(out var change))
            return;
        _replaying = true;
        var (add, remove) = backwards ? (change.Removed, change.Added) : (change.Added, change.Removed);
        var (textAdd, textRemove) = backwards ? (change.TextRemoved, change.TextAdded) : (change.TextAdded, change.TextRemoved);
        if (remove is not null)
            _ink.Strokes.Remove(remove);
        if (add is not null)
            _ink.Strokes.Add(add);
        foreach (var t in textRemove ?? [])
            _ink.Children.Remove(t);
        foreach (var t in textAdd ?? [])
            if (!_ink.Children.Contains(t))
                _ink.Children.Add(t);
        _replaying = false;
        to.Push(change);
        _bar?.Refresh();
    }

    public void Clear()
    {
        CommitText();
        if (_ink.Strokes.Count == 0 && _ink.Children.Count == 0)
            return;
        _replaying = true;
        var strokes = _ink.Strokes.Clone();
        var texts = _ink.Children.Cast<UIElement>().ToList();
        _ink.Strokes.Clear();
        _ink.Children.Clear();
        _replaying = false;
        Push(new Change(null, strokes.Count > 0 ? StrokesOf(_ink.Strokes, strokes) : null, null, texts));
    }

    // The cleared strokes, as the same objects that come back on undo.
    private static StrokeCollection StrokesOf(StrokeCollection _, StrokeCollection cleared) => cleared;

    // ---------- saving ----------

    /// <summary>The drawing with what is under it, at the monitor's own pixels.</summary>
    private BitmapSource Picture()
    {
        CommitText();
        var backdrop = _backdrop.Fill;
        if (_board == DrawBoard.Live)
        {
            // Put the drawing and the strip aside for a moment and take what the screen shows under them.
            Opacity = 0;
            _bar?.Hide();
            Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
            Thread.Sleep(80);
            var under = ScreenUi.Still(Bounds);
            Opacity = 1;
            _bar?.Show();
            if (under is not null)
                _backdrop.Fill = new ImageBrush(under) { Stretch = Stretch.Fill };
        }
        _root.UpdateLayout();
        var picture = ScreenUi.Render(_root, Bounds.Width, Bounds.Height, Scale);
        _backdrop.Fill = backdrop;
        return picture;
    }

    public void Save()
    {
        var picture = Picture();
        var folder = Wheels.Screenshots.Folder;
        Directory.CreateDirectory(folder);
        var stamp = DateTime.Now.ToString("yyyy-MM-dd HH.mm.ss", System.Globalization.CultureInfo.InvariantCulture);
        var path = Petal.Core.Jobs.OutputNaming.Unique(folder, $"Drawing {stamp}", "png");
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(picture));
        using (var stream = File.Create(path))
            encoder.Save(stream);
        var at = new ScreenPoint(Bounds.Left + Bounds.Width / 2, Bounds.Top + Bounds.Height / 2);
        var card = Hud.HudCard.ShowAt(Hud.HudCard.CornerPoint(), L.T("Drawing saved"), L.T("Click to open the wheel for it"));
        card.Succeed(L.T("Drawing saved"), System.IO.Path.GetFileName(path), _openFiles is null ? null : (Action)(() =>
        {
            Close();
            _openFiles(path, at);
        }));
    }

    public void Copy()
    {
        var picture = Picture();
        try
        {
            Clipboard.SetImage(picture);
            var card = Hud.HudCard.ShowAt(Hud.HudCard.CornerPoint(), L.T("Copied"), "");
            card.Succeed(L.T("Drawing copied"), L.T("Paste it in any app"), null);
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            Hud.HudCard.ShowAt(Hud.HudCard.CornerPoint(), "", "").Fail(L.T("Couldn't copy"), L.T("The clipboard is busy. Try again."));
        }
    }

    // ---------- keys ----------

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        if (key == Key.Escape)
        {
            if (_editing is not null)
                CommitText();
            else
                Close();
            e.Handled = true;
            return;
        }
        if (ctrl)
        {
            switch (key)
            {
                case Key.Z when Keyboard.Modifiers.HasFlag(ModifierKeys.Shift): Redo(); break;
                case Key.Z: Undo(); break;
                case Key.Y: Redo(); break;
                case Key.S: Save(); break;
                case Key.C when _editing is null: Copy(); break;
                default: return;
            }
            e.Handled = true;
            return;
        }
        // Letters choose tools, unless they are being typed.
        if (_editing is not null)
            return;
        switch (key)
        {
            case Key.P: SetTool(DrawTool.Pen); break;
            case Key.H: SetTool(DrawTool.Highlighter); break;
            case Key.L: SetTool(DrawTool.Line); break;
            case Key.A: SetTool(DrawTool.Arrow); break;
            case Key.R: SetTool(DrawTool.Rectangle); break;
            case Key.O: SetTool(DrawTool.Ellipse); break;
            case Key.T: SetTool(DrawTool.Text); break;
            case Key.E: SetTool(DrawTool.Eraser); break;
            case Key.D1 or Key.NumPad1: SetSize(0); break;
            case Key.D2 or Key.NumPad2: SetSize(1); break;
            case Key.D3 or Key.NumPad3: SetSize(2); break;
            case Key.W: SetBoard(_board == DrawBoard.White ? DrawBoard.Live : DrawBoard.White); break;
            case Key.K: SetBoard(_board == DrawBoard.Black ? DrawBoard.Live : DrawBoard.Black); break;
            case Key.F: SetBoard(_board == DrawBoard.Still ? DrawBoard.Live : DrawBoard.Still); break;
            case Key.Delete: Clear(); break;
            default: return;
        }
        e.Handled = true;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        // Ctrl and the mouse wheel: a thinner or thicker line.
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            SetSize(_size + (e.Delta > 0 ? 1 : -1));
            e.Handled = true;
        }
    }
}

/// <summary>
/// The strip over a drawing: tools, colours, sizes, undo, what is underneath, let clicks through, save,
/// copy and close. It never takes the keyboard, so the drawing keeps its shortcuts.
/// </summary>
internal sealed class DrawToolbar : Window
{
    private readonly DrawOverlay _draw;
    private readonly Dictionary<DrawTool, StripButton> _tools = [];
    private readonly List<(Color Colour, StripButton Button)> _colours = [];
    private readonly List<StripButton> _sizes = [];
    private readonly Dictionary<DrawBoard, StripButton> _boards = [];
    private StripButton _undo = null!, _redo = null!, _through = null!;

    public DrawToolbar(DrawOverlay draw)
    {
        _draw = draw;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        Title = L.T("Drawing tools");
        L.Apply(this, mirror: false);
        Content = Build();
        SourceInitialized += (_, _) =>
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            long ex = (long)GetWindowLongPtr(hwnd, GWL_EXSTYLE) | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
            SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(ex));
        };
        Refresh();
    }

    /// <summary>Top middle of the drawing's monitor.</summary>
    public void PlaceOver(RECT bounds, double scale)
    {
        UpdateLayout();
        Left = bounds.Left / scale + (bounds.Width / scale - ActualWidth) / 2;
        Top = bounds.Top / scale + 14;
    }

    private UIElement Build()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var grip = new Border { Width = 14, Cursor = Cursors.SizeAll, Background = Brushes.Transparent, Child = Icons.Create("grip", 14, new SolidColorBrush(Color.FromArgb(0x90, 0xFF, 0xFF, 0xFF)), 1.8) };
        grip.MouseLeftButtonDown += (_, _) => DragMove();
        row.Children.Add(grip);
        foreach (var (tool, icon, tip) in new[]
        {
            (DrawTool.Pen, "pen", "Pen (P)"), (DrawTool.Highlighter, "highlighter", "Highlighter (H)"), (DrawTool.Line, "minus", "Line (L)"),
            (DrawTool.Arrow, "arrowUpRight", "Arrow (A)"), (DrawTool.Rectangle, "square", "Box (R)"), (DrawTool.Ellipse, "circle", "Ring (O)"),
            (DrawTool.Text, "type", "Text (T)"), (DrawTool.Eraser, "eraser", "Eraser (E)"),
        })
        {
            var button = new StripButton(icon, tip, () => _draw.SetTool(tool));
            _tools[tool] = button;
            row.Children.Add(button);
        }
        row.Children.Add(ScreenUi.Divider());
        foreach (var colour in DrawOverlay.Colours)
        {
            var dot = new Border
            {
                Width = 16,
                Height = 16,
                CornerRadius = new CornerRadius(8),
                Background = new SolidColorBrush(colour),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x70, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(1),
            };
            var button = new StripButton(null, ColourName(colour), () => _draw.SetColour(colour), dot, 28);
            _colours.Add((colour, button));
            row.Children.Add(button);
        }
        row.Children.Add(ScreenUi.Divider());
        for (int i = 0; i < DrawOverlay.Sizes.Length; i++)
        {
            int size = i;
            double d = 4 + i * 4;
            var dot = new Border { Width = d, Height = d, CornerRadius = new CornerRadius(d / 2), Background = Brushes.White };
            var button = new StripButton(null, new[] { "Thin (1)", "Medium (2)", "Thick (3)" }[i], () => _draw.SetSize(size), dot, 28);
            _sizes.Add(button);
            row.Children.Add(button);
        }
        row.Children.Add(ScreenUi.Divider());
        _undo = new StripButton("undo", "Undo (Ctrl+Z)", _draw.Undo);
        _redo = new StripButton("redo", "Redo (Ctrl+Y)", _draw.Redo);
        row.Children.Add(_undo);
        row.Children.Add(_redo);
        row.Children.Add(new StripButton("trash", "Clear all (Delete)", _draw.Clear));
        row.Children.Add(ScreenUi.Divider());
        foreach (var (board, icon, tip) in new[]
        {
            (DrawBoard.Live, "monitor", "Over the screen"), (DrawBoard.Still, "snowflake", "Over a still of the screen (F)"),
            (DrawBoard.White, "presentation", "Whiteboard (W)"), (DrawBoard.Black, "presentation", "Blackboard (K)"),
        })
        {
            UIElement? face = board == DrawBoard.Black ? BoardChip(Color.FromRgb(0x1B, 0x22, 0x1F)) : board == DrawBoard.White ? BoardChip(Colors.White) : null;
            var button = new StripButton(face is null ? icon : null, tip, () => _draw.SetBoard(board), face);
            _boards[board] = button;
            row.Children.Add(button);
        }
        _through = new StripButton("hand", "Let clicks through to the apps", _draw.ToggleClickThrough);
        row.Children.Add(_through);
        row.Children.Add(ScreenUi.Divider());
        row.Children.Add(new StripButton("download", "Save (Ctrl+S)", _draw.Save));
        row.Children.Add(new StripButton("copyPlain", "Copy (Ctrl+C)", _draw.Copy));
        row.Children.Add(new StripButton("x", "Close (Esc)", _draw.Close));
        return ScreenUi.Strip(row, 22);
    }

    private static Border BoardChip(Color colour) => new()
    {
        Width = 18,
        Height = 13,
        CornerRadius = new CornerRadius(2.5),
        Background = new SolidColorBrush(colour),
        BorderBrush = new SolidColorBrush(Color.FromArgb(0x90, 0xFF, 0xFF, 0xFF)),
        BorderThickness = new Thickness(1),
    };

    private static string ColourName(Color c) =>
        Array.IndexOf(DrawOverlay.Colours, c) switch
        {
            0 => "Red",
            1 => "Orange",
            2 => "Yellow",
            3 => "Green",
            4 => "Blue",
            5 => "Purple",
            6 => "White",
            _ => "Black",
        };

    public void Refresh()
    {
        foreach (var (tool, button) in _tools)
            button.IsOn = tool == _draw.Tool && !_draw.ClickThrough;
        foreach (var (colour, button) in _colours)
        {
            button.Accent = Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF);
            button.IsOn = colour == _draw.Colour;
        }
        for (int i = 0; i < _sizes.Count; i++)
        {
            _sizes[i].Accent = Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF);
            _sizes[i].IsOn = i == _draw.Size;
        }
        foreach (var (board, button) in _boards)
            button.IsOn = board == _draw.Board;
        _through.IsOn = _draw.ClickThrough;
        _undo.Opacity = _draw.CanUndo ? 1 : 0.35;
        _redo.Opacity = _draw.CanRedo ? 1 : 0.35;
    }
}
