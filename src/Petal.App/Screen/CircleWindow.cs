using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Petal.App.Input;
using Petal.App.Localization;
using Petal.App.Overlay;
using Petal.App.Visuals;
using Petal.Engines.Documents;
using Petal.Engines.Documents.Ocr;
using static Petal.App.Interop.NativeMethods;

namespace Petal.App.Screen;

/// <summary>
/// Circle Anything: the screen stands still, and whatever is circled, scribbled over or clicked (a sentence, a number, a
/// date, a picture, a QR code) gets DropWheel's wheel with what can be done with it, and the answer beside it on the
/// screen: a translation laid over the words, an explanation, conversions. All on this PC.
/// </summary>
internal sealed partial class CircleWindow : Window
{
    private enum Phase
    {
        /// <summary>Waiting for a circle, a stroke or a click.</summary>
        Drawing,

        /// <summary>Reading what was circled.</summary>
        Reading,

        /// <summary>The wheel is open on it.</summary>
        Choosing,

        /// <summary>A card shows the answer; a new circle starts over.</summary>
        Showing,
    }

    private static CircleWindow? _open;

    private readonly BitmapSource _still;
    private readonly byte[] _pixels;
    private readonly RECT _bounds;
    private readonly int _width, _height;
    private readonly Canvas _root = new() { ClipToBounds = true };
    private readonly Canvas _world = new();
    private readonly ScaleTransform _toLayout = new();
    private readonly Path _dim = new() { Fill = new SolidColorBrush(Color.FromArgb(0x24, 0, 0, 0)), IsHitTestVisible = false };
    private readonly Border _edge;
    private readonly Border _hint;
    private readonly TextBlock _hintText = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center };
    private readonly Rectangle _hover = new() { RadiusX = 6, RadiusY = 6, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
    private readonly Path _strokeGlow = new() { IsHitTestVisible = false, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round };
    private readonly Path _strokeLine = new() { IsHitTestVisible = false, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round, Stroke = Brushes.White };
    private readonly Rectangle _frame = new() { RadiusX = 12, RadiusY = 12, IsHitTestVisible = false, Visibility = Visibility.Collapsed, Stroke = Brushes.White };
    private readonly Rectangle _shimmer = new() { RadiusX = 12, RadiusY = 12, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
    private readonly TranslateTransform _shimmerMove = new();
    private readonly Canvas _translation = new() { IsHitTestVisible = false };
    private readonly List<Point> _points = [];
    private readonly CancellationTokenSource _life = new();
    private readonly Task<IReadOnlyList<PictureWord>> _screenWords;
    private IReadOnlyList<ScreenLine> _lines = [];
    private IReadOnlyList<ScreenBlock> _blocks = [];
    private Phase _phase = Phase.Drawing;
    private bool _drawing;
    private CircleCard? _card;
    private CircleContent? _content;
    private CancellationTokenSource? _work;
    private int _reading;

    public static bool IsOpen => _open is not null;

    private static Shell.GlobalHotkey? _hotkey;

    /// <summary>Whether the shortcut is DropWheel's now (false when another program has it, or there is none).</summary>
    public static bool ShortcutHeld => _hotkey?.Current is not null;

    /// <summary>Registers the shortcut from the settings; false when another program owns it.</summary>
    public static bool RegisterShortcut()
    {
        _hotkey ??= new Shell.GlobalHotkey();
        _hotkey.Pressed -= Toggle;
        _hotkey.Pressed += Toggle;
        var text = Settings.SettingsStore.Current.CircleShortcut;
        return _hotkey.Register(string.IsNullOrWhiteSpace(text) ? null : Shortcut.Parse(text));
    }

    public static void DisposeShortcut() => _hotkey?.Dispose();

    /// <summary>Stills the screen under the pointer and waits for a circle; pressed again, puts it away.</summary>
    public static void Toggle()
    {
        if (_open is { } open)
        {
            open.Close();
            return;
        }
        if (Wheels.Screenshots.CaptureScreenUnderPointer() is not { } shot)
            return;
        var window = new CircleWindow(shot.Picture, shot.Bounds);
        window.Show();
    }

    private readonly bool _snapshot;

    /// <summary>For the design snapshots: laid out off the screen at the still's size, never taking the keyboard.</summary>
    internal static CircleWindow ForSnapshot(BitmapSource still) => new(still, new RECT { Left = 0, Top = 0, Right = still.PixelWidth, Bottom = still.PixelHeight }, snapshot: true);

    private CircleWindow(BitmapSource still, RECT bounds, bool snapshot = false)
    {
        _snapshot = snapshot;
        if (!snapshot)
            _open = this;
        _still = still;
        _bounds = bounds;
        _width = still.PixelWidth;
        _height = still.PixelHeight;
        _pixels = new byte[_width * _height * 4];
        still.CopyPixels(_pixels, _width * 4, 0);
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        Title = L.T("Circle Anything");
        L.Apply(this, mirror: false);
        Background = new ImageBrush(still) { Stretch = Stretch.Fill };
        Cursor = Cursors.Pen;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);

        var accent = Theme.Current.Accent;
        _world.RenderTransform = _toLayout;
        _world.Children.Add(_dim);
        _world.Children.Add(_translation);
        _hover.Fill = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));
        _hover.Stroke = new SolidColorBrush(Color.FromArgb(0xC0, accent.R, accent.G, accent.B));
        _world.Children.Add(_hover);
        _shimmer.Fill = new LinearGradientBrush(
            [new GradientStop(Color.FromArgb(0, 0xFF, 0xFF, 0xFF), 0.3), new GradientStop(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF), 0.5), new GradientStop(Color.FromArgb(0, 0xFF, 0xFF, 0xFF), 0.7)],
            new Point(0, 0), new Point(1, 1)) { RelativeTransform = _shimmerMove };
        _world.Children.Add(_shimmer);
        _frame.Effect = new DropShadowEffect { Color = accent, BlurRadius = 22, ShadowDepth = 0, Opacity = 0.95 };
        _world.Children.Add(_frame);
        _strokeGlow.Stroke = new SolidColorBrush(Color.FromArgb(0x90, accent.R, accent.G, accent.B));
        _strokeGlow.Effect = new BlurEffect { Radius = 10 };
        _world.Children.Add(_strokeGlow);
        _strokeLine.Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 6, ShadowDepth = 1, Opacity = 0.35 };
        _world.Children.Add(_strokeLine);
        _root.Children.Add(_world);

        // A glowing line round the screen says it is standing still.
        _edge = new Border
        {
            BorderThickness = new Thickness(3),
            IsHitTestVisible = false,
            BorderBrush = new LinearGradientBrush(
                [new GradientStop(accent, 0), new GradientStop(Color.FromRgb(0xFF, 0x6F, 0xD8), 0.33), new GradientStop(Color.FromRgb(0x3B, 0xC9, 0xFF), 0.66), new GradientStop(accent, 1)],
                new Point(0, 0), new Point(1, 1)) { RelativeTransform = new RotateTransform(0, 0.5, 0.5) },
        };
        _root.Children.Add(_edge);

        _hint = ScreenUi.Strip(_hintText, 18);
        _hint.Padding = new Thickness(16, 8, 16, 9);
        _hint.MaxWidth = 640;
        _hint.IsHitTestVisible = false;
        _hintText.FlowDirection = L.Direction;
        _root.Children.Add(_hint);
        Content = _root;
        Hint("Circle or scribble over anything · Click a paragraph or a picture · Esc closes");

        if (snapshot)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = -20000;
            Top = -20000;
            Width = _width;
            Height = _height;
            ShowActivated = false;
            Topmost = false;
            _root.Background = new ImageBrush(still) { Stretch = Stretch.Fill };
        }
        else
            SourceInitialized += (_, _) =>
                SetWindowPos(new WindowInteropHelper(this).Handle, new IntPtr(-1), _bounds.Left, _bounds.Top, _bounds.Width, _bounds.Height, SWP_SHOWWINDOW);
        Loaded += (_, _) =>
        {
            Layout();
            if (_snapshot)
                return;
            Activate();
            Focus();
            if (SystemParameters.ClientAreaAnimation)
            {
                ((RotateTransform)((LinearGradientBrush)_edge.BorderBrush).RelativeTransform).BeginAnimation(RotateTransform.AngleProperty,
                    new DoubleAnimation(0, 360, TimeSpan.FromSeconds(6)) { RepeatBehavior = RepeatBehavior.Forever });
                _edge.BeginAnimation(OpacityProperty, Motion.To(1, 260, Motion.Standard, from: 0));
            }
        };
        SizeChanged += (_, _) => Layout();
        Closed += (_, _) =>
        {
            if (ReferenceEquals(_open, this))
                _open = null;
            _life.Cancel();
            _work?.Cancel();
            StopReading();
            if (App.Current.Controller is { } controller && controller.IsWheelOpen)
                controller.CloseWheel();
        };

        // The whole screen is read once, quietly, so a click can take a paragraph and the pointer show what it would take.
        _screenWords = Task.Run(async () =>
        {
            if (!DocumentEngine.IsOcrAvailable)
                return (IReadOnlyList<PictureWord>)[];
            try
            {
                return await DocumentEngine.ReadWordsAsync(_pixels, _width, _height, _life.Token, everyLanguage: true);
            }
            catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException or System.Runtime.InteropServices.COMException)
            {
                return [];
            }
        });
        _screenWords.ContinueWith(t =>
        {
            if (t.IsCompletedSuccessfully)
                Dispatcher.BeginInvoke(() =>
                {
                    _lines = CircleText.Lines(t.Result);
                    _blocks = CircleText.Blocks(_lines);
                });
        }, TaskScheduler.Default);
    }

    /// <summary>The still's pixels in the window's layout units, and the dimming over all of it.</summary>
    private void Layout()
    {
        if (ActualWidth <= 0)
            return;
        _toLayout.ScaleX = ActualWidth / _width;
        _toLayout.ScaleY = ActualHeight / _height;
        _edge.Width = ActualWidth;
        _edge.Height = ActualHeight;
        _hint.Measure(new Size(ActualWidth, ActualHeight));
        Canvas.SetLeft(_hint, (ActualWidth - _hint.DesiredSize.Width) / 2);
        Canvas.SetTop(_hint, 22);
        double px = _width / ActualWidth;
        _strokeGlow.StrokeThickness = 16 * px;
        _strokeLine.StrokeThickness = 4.5 * px;
        _frame.StrokeThickness = 2.5 * px;
        _hover.StrokeThickness = 2 * px;
        if (_frame.Visibility != Visibility.Visible)
            _dim.Data = new RectangleGeometry(new Rect(0, 0, _width, _height));
    }

    private void Hint(string text)
    {
        _hintText.Text = L.T(text);
        _hint.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (ActualWidth > 0)
            Layout();
    }

    /// <summary>A point of the screen (in pixels of the still) as a point on the screen itself.</summary>
    private ScreenPoint OnScreen(Point p) => new(_bounds.Left + (int)Math.Round(p.X), _bounds.Top + (int)Math.Round(p.Y));

    private ScreenPoint Middle => _content is { } c
        ? OnScreen(new Point(c.Area.X + c.Area.Width / 2.0, c.Area.Y + c.Area.Height / 2.0))
        : OnScreen(new Point(_width / 2.0, _height / 2.0));

    // ---------- drawing ----------

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (_card is not null && _card.IsMouseOver)
            return;
        if (_phase == Phase.Reading)
            return;
        // A new circle: whatever was shown goes.
        Reset();
        _drawing = true;
        _points.Clear();
        _points.Add(e.GetPosition(_world));
        _hover.Visibility = Visibility.Collapsed;
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var p = e.GetPosition(_world);
        if (_drawing)
        {
            if ((p - _points[^1]).Length >= 1.5)
            {
                _points.Add(p);
                DrawStroke(closed: false);
            }
            return;
        }
        if (_phase != Phase.Drawing || (_card?.IsMouseOver ?? false))
        {
            _hover.Visibility = Visibility.Collapsed;
            return;
        }
        Hover(p);
    }

    /// <summary>What a click would take: the paragraph under the pointer, lit softly.</summary>
    private void Hover(Point p)
    {
        if (CircleText.LineAt(p, _lines) is not null && CircleText.BlockAt(p, _blocks) is { } block)
        {
            var box = block.Box;
            box.Inflate(5, 4);
            Canvas.SetLeft(_hover, box.X);
            Canvas.SetTop(_hover, box.Y);
            _hover.Width = box.Width;
            _hover.Height = box.Height;
            _hover.Visibility = Visibility.Visible;
            Cursor = Cursors.Hand;
        }
        else
        {
            _hover.Visibility = Visibility.Collapsed;
            Cursor = Cursors.Pen;
        }
    }

    protected override async void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_drawing)
            return;
        _drawing = false;
        ReleaseMouseCapture();
        e.Handled = true;
        await FinishAsync(e.GetPosition(_world));
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        if (_card is not null && _card.IsMouseOver)
            return;
        Close();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
        }
        else if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control && _content is { HasText: true } c && !(_card?.IsKeyboardFocusWithin ?? false))
        {
            e.Handled = true;
            _ = RunAsync("copy");
        }
    }

    private void DrawStroke(bool closed)
    {
        var smooth = CircleText.Smooth(_points, closed);
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(smooth[0], false, closed);
            g.PolyLineTo(smooth.Skip(1).ToList(), true, true);
        }
        geometry.Freeze();
        _strokeLine.Data = geometry;
        _strokeGlow.Data = geometry;
        _strokeLine.Opacity = _strokeGlow.Opacity = 1;
        _strokeLine.BeginAnimation(OpacityProperty, null);
        _strokeGlow.BeginAnimation(OpacityProperty, null);
    }

    /// <summary>The stroke is done: what it meant, read, then the wheel on it.</summary>
    private async Task FinishAsync(Point end)
    {
        var words = _screenWords.IsCompletedSuccessfully ? _screenWords.Result : [];
        var bounds = CircleText.Bounds(_points);
        CircleArea? selection;
        IReadOnlyList<Point>? stroke = null;
        if (bounds.Width < 6 && bounds.Height < 6)
        {
            // A click: the paragraph under it, or the picture.
            ClearStroke();
            if (CircleText.BlockAt(end, _blocks) is { } block)
            {
                var box = block.Box;
                box.Inflate(6, 5);
                selection = new CircleArea(CircleText.Clamp(box, _width, _height), block.Words.ToList(), false);
            }
            else if (PictureRegions.Around(_pixels, _width, _height, (int)end.X, (int)end.Y) is { } region)
                selection = new CircleArea(region, words.Where(w => region.X <= w.CentreX && w.CentreX <= region.X + region.Width && region.Y <= w.CentreY && w.CentreY <= region.Y + region.Height).ToList(), false);
            else
            {
                Hint("Nothing there to take: circle what you want");
                return;
            }
        }
        else
        {
            bool closed = CircleText.Encloses(_points);
            DrawStroke(closed);
            stroke = _points.ToList();
            selection = CircleText.Select(stroke, words, _width, _height);
        }
        await ReadAsync(selection, stroke, end);
    }

    private async Task ReadAsync(CircleArea selection, IReadOnlyList<Point>? stroke, Point end)
    {
        _phase = Phase.Reading;
        Hint("");
        ShowArea(selection.Area);
        StartReading();
        int reading = ++_reading;
        CircleContent content;
        try
        {
            content = await CircleReader.ReadAsync(_pixels, _width, selection, stroke, _life.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (reading != _reading || !IsLoaded)
            return;
        StopReading();
        _content = content;
        FadeStroke();
        OpenWheel(OnScreen(end));
    }

    // ---------- the wheel ----------

    private void OpenWheel(ScreenPoint at)
    {
        if (_content is not { } content || App.Current.Controller is not { } controller)
            return;
        _phase = Phase.Choosing;
        controller.OpenCircleWheel(Page(content, Items(content)), at, () =>
        {
            // Put away without a choice: circle again.
            if (IsLoaded && _phase == Phase.Choosing)
            {
                Reset();
                Activate();
            }
        });
    }

    /// <summary>The petals for what was circled; past twelve, the rest under MORE.</summary>
    private List<WheelItem> Items(CircleContent content)
    {
        var items = CircleChoices.For(content, Settings.SettingsStore.Current.SearchEnabled).Select(Item).ToList();
        if (items.Count > CircleChoices.MostPetals)
        {
            var more = CircleChoices.Get("more");
            items = [.. items.Take(CircleChoices.MostPetals - 1), WheelItem.Group("circle-more", more.Label, more.Name, more.Icon, items.Skip(CircleChoices.MostPetals - 1).ToList())];
        }
        return items;
    }

    private static RingPage Page(CircleContent content, IReadOnlyList<WheelItem> items)
    {
        var hub = CircleChoices.Hub(content, Petal.Recipes.ExchangeRateService.Cached, Petal.Core.Text.Money.HomeCurrency);
        return new RingPage("circle", "Circle Anything", () => new WheelContent(items, ""), () => new RingHub(hub.Title, hub.Subtitle));
    }

    private WheelItem Item(string id)
    {
        var (_, label, name, icon) = CircleChoices.Get(id);
        if (id == "picturegroup")
            return WheelItem.Group("circle-picture", label, name, icon, CircleChoices.PictureGroup.Select(Item).ToList());
        return new WheelItem(label, name, icon, new CircleChoice(id, () => RunAsync(id)));
    }

    // ---------- what shows on the still ----------

    private void ShowArea(Int32Rect area)
    {
        var rect = new Rect(area.X, area.Y, area.Width, area.Height);
        _dim.Fill = new SolidColorBrush(Color.FromArgb(0x70, 0, 0, 0));
        _dim.Data = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(new Rect(0, 0, _width, _height)), new RectangleGeometry(rect, 12, 12));
        foreach (var shape in new[] { _frame, _shimmer })
        {
            Canvas.SetLeft(shape, rect.X);
            Canvas.SetTop(shape, rect.Y);
            shape.Width = rect.Width;
            shape.Height = rect.Height;
            shape.Visibility = Visibility.Visible;
        }
        _frame.BeginAnimation(OpacityProperty, Motion.To(1, 180, Motion.Standard, from: 0));
    }

    private void StartReading()
    {
        _shimmer.Visibility = Visibility.Visible;
        if (SystemParameters.ClientAreaAnimation)
            _shimmerMove.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(-1, 1, TimeSpan.FromMilliseconds(900)) { RepeatBehavior = RepeatBehavior.Forever });
    }

    private void StopReading()
    {
        _shimmerMove.BeginAnimation(TranslateTransform.XProperty, null);
        _shimmer.Visibility = Visibility.Collapsed;
    }

    private void FadeStroke()
    {
        _strokeLine.BeginAnimation(OpacityProperty, Motion.To(0, 220, Motion.Standard));
        _strokeGlow.BeginAnimation(OpacityProperty, Motion.To(0, 220, Motion.Standard));
    }

    private void ClearStroke()
    {
        _strokeLine.Data = null;
        _strokeGlow.Data = null;
    }

    /// <summary>Back to waiting for a circle: no area, no card, no translation.</summary>
    private void Reset()
    {
        _reading++;
        _work?.Cancel();
        _work = null;
        StopReading();
        StopSpeaking();
        ClearStroke();
        _content = null;
        _frame.Visibility = Visibility.Collapsed;
        _dim.Fill = new SolidColorBrush(Color.FromArgb(0x24, 0, 0, 0));
        _dim.Data = new RectangleGeometry(new Rect(0, 0, _width, _height));
        _translation.Children.Clear();
        _translation.Visibility = Visibility.Visible;
        CloseCard();
        _phase = Phase.Drawing;
        Hint("Circle or scribble over anything · Click a paragraph or a picture · Esc closes");
    }

    // ---------- the card ----------

    /// <summary>Shows a card beside the circled area (to its side when there is room, otherwise under or over it).</summary>
    private CircleCard ShowCard(string icon, string title)
    {
        CloseCard();
        _phase = Phase.Showing;
        Hint("");
        var card = new CircleCard(icon, title);
        card.CloseRequested += () =>
        {
            _work?.Cancel();
            StopSpeaking();
            CloseCard();
            _phase = Phase.Showing;
            Hint("Circle something else, or press Esc");
        };
        _card = card;
        _root.Children.Add(card);
        card.Loaded += (_, _) => Place(card);
        card.SizeChanged += (_, _) => Place(card);
        Activate();
        return card;
    }

    private void Place(CircleCard card)
    {
        if (_content is not { } c || ActualWidth <= 0)
            return;
        double sx = ActualWidth / _width, sy = ActualHeight / _height;
        var area = new Rect(c.Area.X * sx, c.Area.Y * sy, c.Area.Width * sx, c.Area.Height * sy);
        double w = card.ActualWidth > 0 ? card.ActualWidth : CircleCard.CardWidth, h = card.ActualHeight, gap = 16, edge = 12;
        double x, y;
        bool rtl = L.IsRightToLeft;
        if ((!rtl && area.Right + gap + w <= ActualWidth - edge) || (rtl && area.Left - gap - w < edge && area.Right + gap + w <= ActualWidth - edge))
            (x, y) = (area.Right + gap, area.Top);
        else if (area.Left - gap - w >= edge)
            (x, y) = (area.Left - gap - w, area.Top);
        else if (area.Bottom + gap + h <= ActualHeight - edge)
            (x, y) = (area.Left, area.Bottom + gap);
        else
            (x, y) = (area.Left, area.Top - gap - h);
        Canvas.SetLeft(card, Math.Clamp(x, edge, Math.Max(edge, ActualWidth - w - edge)));
        Canvas.SetTop(card, Math.Clamp(y, edge, Math.Max(edge, ActualHeight - h - edge)));
    }

    private void CloseCard()
    {
        if (_card is null)
            return;
        _root.Children.Remove(_card);
        _card = null;
    }

    /// <summary>
    /// Puts the still away and says what was done in a card where the circle was (Text copied, Link opened).
    /// <paramref name="subtitle"/> is already in the reader's language.
    /// </summary>
    private void Leave(string title, string subtitle = "")
    {
        var at = Middle;
        Close();
        Hud.HudCard.ShowAt(at, "", "").Succeed(L.T(title), subtitle, null);
    }
}
