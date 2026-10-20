using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Petal.App.Input;
using Petal.App.Interop;
using Petal.App.Visuals;
using static Petal.App.Interop.NativeMethods;

namespace Petal.App.Overlay;

/// <summary>
/// The radial menu. It is an always-created, never-activated, transparent topmost window that
/// is a normal OLE drop target: while the user is still dragging a file out of Explorer it
/// slides under the cursor, Explorer hands it the dragged data, and dropping on a petal runs
/// the petal's action. Nothing is intercepted: dropping elsewhere behaves exactly as usual.
/// </summary>
public sealed class WheelWindow : Window
{
    public const double Side = 420;
    private const double Centre = Side / 2;
    private const double CatchRadius = WheelGeometry.OuterRadius + 36;

    private static readonly Brush HitBrush = Freeze(new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)));

    private readonly Grid _root = new();
    private readonly Canvas _stage = new() { Width = Side, Height = Side };
    private readonly ScaleTransform _stageScale = new(1, 1, Centre, Centre);
    private readonly Canvas _ring = new();
    private readonly Canvas _petals = new();
    private readonly Canvas _labels = new();
    private readonly Ellipse _catcher = new();
    private readonly Path _glassRing = new();
    private readonly Ellipse _glassHub = new();
    private readonly Path _tintRing = new();
    private readonly Ellipse _tintHub = new();
    private readonly Ellipse _rim = new();
    private readonly Ellipse _groove = new();
    private readonly Ellipse _shadow = new();
    private readonly Border _hubPill = new();
    private readonly TextBlock _hubText = new();
    private readonly TextBlock _hubSubtext = new();
    private readonly Border _hint = new();
    private readonly Border _emptyCard = new();
    private readonly TextBlock _emptyText = new();
    private readonly DispatcherTimer _probeTimer = new() { Interval = TimeSpan.FromMilliseconds(140) };
    private readonly DispatcherTimer _releaseTimer = new() { Interval = TimeSpan.FromMilliseconds(450) };
    private readonly DragDropHelper _dragHelper = new();
    private readonly List<Path> _petalPaths = [];
    private readonly List<(TextBlock Text, Path? Icon)> _petalLabels = [];

    private IntPtr _hwnd;
    private bool _open;
    private bool _probing;
    private int _probeTicks;
    private bool _sessionIgnored;
    private WheelMode _mode;
    private IReadOnlyList<string>? _files;
    private WheelContent? _content;
    private int? _highlight;
    private string _lastHubLabel = "";
    private ScreenPoint _anchor;
    private Palette _palette = Theme.Current;
    private ImageBrush? _glass;

    public WheelWindow(FeedbackSound sound)
    {
        Sound = sound;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Focusable = false;
        Width = Side;
        Height = Side;
        Left = -32000;
        Top = -32000;
        Title = "Petal wheel";
        AllowDrop = true;
        UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);

        BuildVisuals();
        Content = _root;

        DragEnter += OnDragEnter;
        DragOver += OnDragOver;
        DragLeave += OnDragLeave;
        Drop += OnDrop;
        _probeTimer.Tick += OnProbeTick;
        _releaseTimer.Tick += (_, _) => CloseWheel();
        Theme.Changed += () => Dispatcher.BeginInvoke(() => _palette = Theme.Current);
    }

    public FeedbackSound Sound { get; }

    /// <summary>Supplies the petals for the dragged files in a mode.</summary>
    public Func<WheelMode, IReadOnlyList<string>, WheelContent>? ContentProvider { get; set; }

    /// <summary>Text for the centre of the wheel, e.g. "2.4 MB" or "3 files · 8 MB".</summary>
    public Func<IReadOnlyList<string>, string>? HubTextProvider { get; set; }

    /// <summary>Key caption for a mode, e.g. "Shift".</summary>
    public Func<WheelMode, string> KeyLabel { get; set; } = m => m switch
    {
        WheelMode.Conversions => "Shift",
        WheelMode.Tools => "Ctrl",
        _ => "Alt",
    };

    /// <summary>Raised after a drop on a petal, on the UI thread, once Explorer's drag has finished.</summary>
    public event Action<WheelItem, IReadOnlyList<string>, ScreenPoint>? ItemDropped;

    public bool IsOpen => _open;

    public WheelMode Mode => _mode;

    // ---------- lifetime ----------

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        var ex = (long)GetWindowLongPtr(_hwnd, GWL_EXSTYLE);
        ex |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST;
        SetWindowLongPtr(_hwnd, GWL_EXSTYLE, new IntPtr(ex));
        HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
    }

    private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_MOUSEACTIVATE)
        {
            handled = true;
            return new IntPtr(MA_NOACTIVATE);
        }
        return IntPtr.Zero;
    }

    /// <summary>Creates the window once, parked off-screen, so it is registered as a drop target.</summary>
    public void Prepare()
    {
        Show();
        Park();
    }

    private void Park()
    {
        _root.Visibility = Visibility.Hidden;
        _catcher.Fill = null;
        _root.Background = null;
        if (_hwnd != IntPtr.Zero)
            SetWindowPos(_hwnd, HWND_TOPMOST, -32000, -32000, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE);
    }

    /// <summary>A new mouse press starts a new drag session.</summary>
    public void BeginSession() => _sessionIgnored = false;

    // ---------- opening ----------

    /// <summary>
    /// Slides the (still invisible) wheel under the cursor. It becomes visible once Explorer
    /// reports that files are being dragged; any other kind of drag leaves it hidden.
    /// </summary>
    public void Open(ScreenPoint cursor, WheelMode mode)
    {
        if (_sessionIgnored)
            return;
        if (_open)
        {
            SwitchMode(mode);
            return;
        }

        _open = true;
        _mode = mode;
        _files = null;
        _content = null;
        _highlight = null;
        _lastHubLabel = "";
        _releaseTimer.Stop();
        _palette = Theme.Current;
        Sound.Warm();

        var (_, work, scale) = MonitorAt(new POINT(cursor.X, cursor.Y));
        double margin = (WheelGeometry.OuterRadius + 18) * scale;
        int cx = (int)Math.Clamp(cursor.X, work.Left + margin, Math.Max(work.Left + margin, work.Right - margin));
        int cy = (int)Math.Clamp(cursor.Y, work.Top + margin, Math.Max(work.Top + margin, work.Bottom - margin));
        _anchor = new ScreenPoint(cx, cy);
        int px = (int)Math.Round(Side * scale);
        int left = cx - px / 2, top = cy - px / 2;

        // Hint goes below the ring when there is no room above it.
        bool hintBelow = cy - work.Top < (WheelGeometry.OuterRadius + 64) * scale;
        Canvas.SetTop(_hint, hintBelow ? Centre + WheelGeometry.OuterRadius + 12 : Centre - WheelGeometry.OuterRadius - 12 - 30);

        var capture = GlassCapture.CaptureBlurred(left, top, px, px, scale);
        ApplyGlass(capture);

        // Invisible but hit-testable everywhere, so Explorer's drag loop picks us as the target.
        _root.Visibility = Visibility.Visible;
        _root.Background = HitBrush;
        _stage.Opacity = 0;
        _hint.Opacity = 0;
        _emptyCard.Visibility = Visibility.Collapsed;
        _probing = true;
        _probeTicks = 0;

        SetWindowPos(_hwnd, HWND_TOPMOST, left, top, px, px, SWP_NOACTIVATE | SWP_SHOWWINDOW);
        // Nudge the drag loop: it re-evaluates the drop target on the next mouse move.
        InjectZeroMouseMove();
        _probeTimer.Start();
    }

    private void OnProbeTick(object? sender, EventArgs e)
    {
        if (!_probing)
        {
            _probeTimer.Stop();
            return;
        }
        // Keep nudging for a while; give up if this turns out not to be a file drag at all.
        if (++_probeTicks > 10)
        {
            _probeTimer.Stop();
            CloseWheel();
            return;
        }
        InjectZeroMouseMove();
    }

    public void SwitchMode(WheelMode mode)
    {
        if (!_open || mode == _mode)
            return;
        _mode = mode;
        if (_files is not null)
            Render(animate: false);
    }

    /// <summary>The mouse button was released somewhere. Give Explorer a moment to deliver the drop.</summary>
    public void PointerReleased()
    {
        if (_open)
            _releaseTimer.Start();
    }

    public void Cancel() => CloseWheel();

    private void CloseWheel()
    {
        _probeTimer.Stop();
        _releaseTimer.Stop();
        if (!_open)
            return;
        _open = false;
        _probing = false;
        _files = null;
        _content = null;
        _highlight = null;
        _stage.BeginAnimation(OpacityProperty, null);
        _stageScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        _stageScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        _labels.BeginAnimation(OpacityProperty, null);
        _hint.BeginAnimation(OpacityProperty, null);
        Park();
        Sound.Cool();
    }

    // ---------- drag and drop ----------

    private static IReadOnlyList<string>? FilesOf(DragEventArgs e)
    {
        try
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files)
                return files;
        }
        catch
        {
            // Some sources throw for formats they advertise.
        }
        return null;
    }

    private POINT CursorPoint(DragEventArgs e)
    {
        GetCursorPos(out var pt);
        return pt;
    }

    private void OnDragEnter(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (!_open)
        {
            e.Effects = DragDropEffects.None;
            return;
        }

        if (_files is null)
        {
            var files = FilesOf(e);
            if (files is null)
            {
                // Text, links or anything else that is not a file: get out of the way for this drag.
                _sessionIgnored = true;
                e.Effects = DragDropEffects.None;
                CloseWheel();
                return;
            }
            _files = files;
            _probing = false;
            _probeTimer.Stop();
            Render(animate: true);
        }

        _dragHelper.Enter(_hwnd, e.Data, CursorPoint(e), DragDropEffects.Copy);
        UpdatePointer(e);
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (!_open || _files is null)
        {
            e.Effects = DragDropEffects.None;
            return;
        }
        UpdatePointer(e);
        _dragHelper.Over(CursorPoint(e), e.Effects);
    }

    private void OnDragLeave(object sender, DragEventArgs e)
    {
        e.Handled = true;
        SetHighlight(null, e.Data);
        _dragHelper.Leave();
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        var files = _files;
        var content = _content;
        int? index = _open && files is not null ? HitTest(e) : null;
        var point = CursorPoint(e);
        _dragHelper.Drop(e.Data, point, index is null ? DragDropEffects.None : DragDropEffects.Copy);

        if (index is not { } i || content is null || files is null || i >= content.Items.Count)
        {
            // Dropped on the hub, a gap or the margin: do nothing, never move the files.
            e.Effects = DragDropEffects.None;
            CloseWheel();
            return;
        }

        // Copy (never Move) so Explorer leaves the originals alone.
        e.Effects = DragDropEffects.Copy;
        var item = content.Items[i];
        Sound.Drop();

        // Let the petal stay lit for a couple of frames, then vanish instantly.
        var close = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(55) };
        close.Tick += (_, _) =>
        {
            close.Stop();
            CloseWheel();
        };
        close.Start();

        // Run the action after returning from Drop, so Explorer's drag loop finishes first.
        var drop = new ScreenPoint(point.X, point.Y);
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () => ItemDropped?.Invoke(item, files, drop));
    }

    private int? HitTest(DragEventArgs e)
    {
        if (_content is null || _content.IsEmpty)
            return null;
        var p = e.GetPosition(_stage);
        return WheelGeometry.HitTest(new Vector(p.X - Centre, p.Y - Centre), _content.Items.Count);
    }

    private void UpdatePointer(DragEventArgs e)
    {
        var index = HitTest(e);
        e.Effects = index is null ? DragDropEffects.None : DragDropEffects.Copy;
        SetHighlight(index, e.Data);
    }

    private void SetHighlight(int? index, IDataObject? data)
    {
        if (index == _highlight)
            return;
        _highlight = index;
        var p = _palette;
        for (int i = 0; i < _petalPaths.Count; i++)
        {
            bool on = i == index;
            _petalPaths[i].Fill = on ? AccentFill(p) : PetalFill(p);
            _petalPaths[i].Stroke = on ? Palette.Brush(Color.FromArgb(0x50, p.Accent.R, p.Accent.G, p.Accent.B)) : Palette.Brush(p.PetalStroke);
            var (text, icon) = _petalLabels[i];
            var fg = on ? Palette.Brush(p.TextOnAccent) : Palette.Brush(p.Text);
            text.Foreground = fg;
            if (icon is not null)
                icon.Stroke = fg;
        }

        if (index is { } i2 && _content is not null && i2 < _content.Items.Count)
        {
            var item = _content.Items[i2];
            _lastHubLabel = item.Payload is Petal.Core.Formats.FileFormat ? item.Label : item.Name;
            SetHub(_lastHubLabel, Subtitle(item));
            Sound.Tick();
            if (data is not null)
                DragDropHelper.SetDescription(data, Description(item), null);
        }
        else if (data is not null)
        {
            DragDropHelper.SetDescription(data, null);
        }
    }

    private static string Subtitle(WheelItem item) => item.Payload switch
    {
        Petal.Core.Formats.FileFormat => "Convert",
        Petal.Core.Tools.ToolInfo => "Tool",
        _ => "Action",
    };

    private static string Description(WheelItem item) => item.Payload switch
    {
        Petal.Core.Formats.FileFormat f => $"Convert to {f.Label}",
        _ => item.Name,
    };

    // ---------- rendering ----------

    private void Render(bool animate)
    {
        if (_files is null || ContentProvider is null)
            return;
        var p = _palette;
        _content = ContentProvider(_mode, _files);
        _highlight = null;
        _petals.Children.Clear();
        _labels.Children.Clear();
        _petalPaths.Clear();
        _petalLabels.Clear();
        UpdateHint();

        // The safe zone around the ring swallows near-miss drops instead of moving files.
        _root.Background = null;
        _catcher.Fill = HitBrush;

        if (_content.IsEmpty)
        {
            _ring.Visibility = Visibility.Collapsed;
            _emptyCard.Visibility = Visibility.Visible;
            _emptyText.Text = _content.EmptyMessage;
            _emptyText.Foreground = Palette.Brush(p.Text);
            _emptyCard.Background = Palette.Brush(p.PillFill);
            _emptyCard.Child = _emptyText;
            _emptyCard.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(_emptyCard, Centre - _emptyCard.DesiredSize.Width / 2);
            Canvas.SetTop(_emptyCard, Centre - _emptyCard.DesiredSize.Height / 2);
        }
        else
        {
            _ring.Visibility = Visibility.Visible;
            _emptyCard.Visibility = Visibility.Collapsed;
            int n = _content.Items.Count;
            bool tools = _mode != WheelMode.Conversions;
            for (int i = 0; i < n; i++)
            {
                var item = _content.Items[i];
                var path = new Path
                {
                    Data = WheelGeometry.Petal(i, n),
                    Fill = PetalFill(p),
                    Stroke = Palette.Brush(p.PetalStroke),
                    StrokeThickness = 0.75,
                    IsHitTestVisible = false,
                };
                _petals.Children.Add(path);
                _petalPaths.Add(path);
                AddLabel(item, i, n, tools, p);
            }
        }

        SetHub(HubTextProvider?.Invoke(_files) ?? "", _files.Count == 1 ? "" : $"{_files.Count} items");
        ApplyPaletteToChrome(p);

        if (animate)
        {
            _stage.Opacity = 0;
            _stage.BeginAnimation(OpacityProperty, Motion.To(1, 90, Motion.Standard, from: 0));
            _stageScale.BeginAnimation(ScaleTransform.ScaleXProperty, Motion.To(1, 190, Motion.Bloom, from: 0.38));
            _stageScale.BeginAnimation(ScaleTransform.ScaleYProperty, Motion.To(1, 190, Motion.Bloom, from: 0.38));
            _labels.BeginAnimation(OpacityProperty, Motion.To(1, 90, Motion.Standard, from: 0, delayMs: 55));
            _hint.BeginAnimation(OpacityProperty, Motion.To(1, 160, Motion.Standard, from: 0, delayMs: 120));
        }
        else
        {
            _stage.BeginAnimation(OpacityProperty, null);
            _stage.Opacity = 1;
            _labels.BeginAnimation(OpacityProperty, null);
            _labels.Opacity = 1;
            _hint.BeginAnimation(OpacityProperty, null);
            _hint.Opacity = 1;
        }
    }

    private void AddLabel(WheelItem item, int index, int count, bool withIcon, Palette p)
    {
        var pos = WheelGeometry.LabelPosition(index, count);
        double chord = 2 * WheelGeometry.LabelRadius * Math.Sin(Math.PI / Math.Max(count, 2)) - 10;
        double maxWidth = Math.Clamp(chord, 34, 78);

        var text = new TextBlock
        {
            Text = item.Label,
            FontFamily = UiFonts.Display,
            FontWeight = FontWeights.SemiBold,
            FontSize = withIcon ? (count > 8 ? 8 : 8.5) : (count > 9 ? 11.5 : count > 7 ? 12.5 : 13.5),
            Foreground = Palette.Brush(p.Text),
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = maxWidth,
            LineHeight = withIcon ? 9.5 : double.NaN,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            IsHitTestVisible = false,
        };
        if (!withIcon)
            text.LineStackingStrategy = LineStackingStrategy.MaxHeight;

        FrameworkElement element;
        Path? icon = null;
        if (withIcon && item.Icon is not null)
        {
            icon = Icons.Create(item.Icon, count > 8 ? 16 : 18, Palette.Brush(p.Text), 2);
            var stack = new StackPanel { Orientation = Orientation.Vertical, IsHitTestVisible = false };
            icon.HorizontalAlignment = HorizontalAlignment.Center;
            icon.Margin = new Thickness(0, 0, 0, 3);
            stack.Children.Add(icon);
            stack.Children.Add(text);
            element = stack;
        }
        else
        {
            element = text;
        }

        element.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = element.DesiredSize;
        Canvas.SetLeft(element, pos.X - size.Width / 2);
        Canvas.SetTop(element, pos.Y - size.Height / 2);
        _labels.Children.Add(element);
        _petalLabels.Add((text, icon));
    }

    private void SetHub(string title, string subtitle)
    {
        _hubText.Text = title;
        _hubSubtext.Text = subtitle;
        _hubSubtext.Visibility = string.IsNullOrEmpty(subtitle) ? Visibility.Collapsed : Visibility.Visible;
        _hubPill.Visibility = string.IsNullOrEmpty(title) ? Visibility.Hidden : Visibility.Visible;
        _hubPill.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(_hubPill, -_hubPill.DesiredSize.Width / 2);
        Canvas.SetTop(_hubPill, -_hubPill.DesiredSize.Height / 2);
    }

    private void UpdateHint()
    {
        var p = _palette;
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (mode, label) in new[] { (WheelMode.Conversions, "Convert"), (WheelMode.Tools, "Tools"), (WheelMode.Actions, "Actions") })
        {
            bool active = mode == _mode;
            var key = new Border
            {
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(5, 1, 5, 2),
                Margin = new Thickness(0, 0, 5, 0),
                Background = active ? Palette.Brush(p.Accent) : Palette.Brush(p.IsDark ? Color.FromArgb(0x30, 255, 255, 255) : Color.FromArgb(0x14, 0, 0, 0)),
                Child = new TextBlock
                {
                    Text = KeyLabel(mode),
                    FontFamily = UiFonts.Text,
                    FontSize = 10.5,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = active ? Palette.Brush(p.TextOnAccent) : Palette.Brush(p.TextSecondary),
                },
                VerticalAlignment = VerticalAlignment.Center,
            };
            var caption = new TextBlock
            {
                Text = label,
                FontFamily = UiFonts.Text,
                FontSize = 11.5,
                FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = active ? Palette.Brush(p.Text) : Palette.Brush(p.TextSecondary),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 12, 0),
            };
            row.Children.Add(key);
            row.Children.Add(caption);
        }
        if (row.Children[^1] is FrameworkElement last)
            last.Margin = new Thickness(0);
        _hint.Child = row;
        _hint.Background = Palette.Brush(p.PillFill);
        _hint.BorderBrush = Palette.Brush(p.GlassEdge);
        _hint.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(_hint, Centre - _hint.DesiredSize.Width / 2);
    }

    private void ApplyGlass(BitmapSource? capture)
    {
        if (capture is null)
        {
            _glass = null;
            _glassRing.Fill = Palette.Brush(_palette.WindowBackground);
            _glassHub.Fill = Palette.Brush(_palette.WindowBackground);
            return;
        }
        // The capture covers the whole window; map it in window coordinates around the centre.
        _glass = new ImageBrush(capture)
        {
            Stretch = Stretch.Fill,
            ViewportUnits = BrushMappingMode.Absolute,
            Viewport = new Rect(-Centre, -Centre, Side, Side),
        };
        _glass.Freeze();
        _glassRing.Fill = _glass;
        _glassHub.Fill = _glass;
    }

    private void ApplyPaletteToChrome(Palette p)
    {
        _tintRing.Fill = Palette.Brush(p.GlassTint);
        _tintHub.Fill = Palette.Brush(p.HubFill);
        _rim.Stroke = Palette.Brush(p.GlassEdge);
        _groove.Stroke = Palette.Brush(p.IsDark ? Color.FromArgb(0x22, 255, 255, 255) : Color.FromArgb(0x16, 0, 0, 0));
        _shadow.Fill = new RadialGradientBrush(
        [
            new GradientStop(p.Shadow, 0.78),
            new GradientStop(Color.FromArgb(0, p.Shadow.R, p.Shadow.G, p.Shadow.B), 1),
        ]);
        _hubPill.Background = Palette.Brush(p.PillFill);
        _hubPill.BorderBrush = Palette.Brush(p.GlassEdge);
        _hubText.Foreground = Palette.Brush(p.Text);
        _hubSubtext.Foreground = Palette.Brush(p.TextSecondary);
        _emptyCard.BorderBrush = Palette.Brush(p.GlassEdge);
    }

    private static Brush PetalFill(Palette p)
    {
        var b = new RadialGradientBrush
        {
            MappingMode = BrushMappingMode.Absolute,
            Center = new Point(0, 0),
            GradientOrigin = new Point(0, -WheelGeometry.PetalOuter * 0.6),
            RadiusX = WheelGeometry.PetalOuter,
            RadiusY = WheelGeometry.PetalOuter,
            GradientStops = [new GradientStop(p.PetalTop, 0.35), new GradientStop(p.PetalBottom, 1)],
        };
        b.Freeze();
        return b;
    }

    private static Brush AccentFill(Palette p)
    {
        var b = new RadialGradientBrush
        {
            MappingMode = BrushMappingMode.Absolute,
            Center = new Point(0, 0),
            GradientOrigin = new Point(0, 0),
            RadiusX = WheelGeometry.PetalOuter,
            RadiusY = WheelGeometry.PetalOuter,
            GradientStops = [new GradientStop(Mix(p.Accent, Colors.White, 0.14), 0.4), new GradientStop(Mix(p.Accent, Colors.Black, 0.1), 1)],
        };
        b.Freeze();
        return b;
    }

    private static Color Mix(Color a, Color b, double t) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

    private void BuildVisuals()
    {
        _root.Children.Add(_stage);
        _stage.RenderTransform = _stageScale;

        // Near-miss catcher, a little larger than the ring.
        _catcher.Width = _catcher.Height = CatchRadius * 2;
        Canvas.SetLeft(_catcher, Centre - CatchRadius);
        Canvas.SetTop(_catcher, Centre - CatchRadius);
        _stage.Children.Add(_catcher);

        // Everything round is drawn around (0, 0) inside a canvas placed at the centre.
        Canvas.SetLeft(_ring, Centre);
        Canvas.SetTop(_ring, Centre);
        _stage.Children.Add(_ring);

        double shadowR = WheelGeometry.OuterRadius + 14;
        _shadow.Width = _shadow.Height = shadowR * 2;
        Canvas.SetLeft(_shadow, -shadowR);
        Canvas.SetTop(_shadow, -shadowR + 7);
        _shadow.IsHitTestVisible = false;
        _ring.Children.Add(_shadow);

        _glassRing.Data = WheelGeometry.Ring(WheelGeometry.HubRadius + 3, WheelGeometry.OuterRadius);
        _glassRing.IsHitTestVisible = false;
        _ring.Children.Add(_glassRing);
        _tintRing.Data = _glassRing.Data;
        _tintRing.IsHitTestVisible = false;
        _ring.Children.Add(_tintRing);

        double hubR = WheelGeometry.HubRadius;
        foreach (var e in new[] { _glassHub, _tintHub })
        {
            e.Width = e.Height = hubR * 2;
            Canvas.SetLeft(e, -hubR);
            Canvas.SetTop(e, -hubR);
            e.IsHitTestVisible = false;
            _ring.Children.Add(e);
        }

        double rimR = WheelGeometry.OuterRadius - 0.5;
        _rim.Width = _rim.Height = rimR * 2;
        Canvas.SetLeft(_rim, -rimR);
        Canvas.SetTop(_rim, -rimR);
        _rim.StrokeThickness = 1;
        _rim.IsHitTestVisible = false;
        _ring.Children.Add(_rim);

        double grooveR = WheelGeometry.HubRadius + 3;
        _groove.Width = _groove.Height = grooveR * 2;
        Canvas.SetLeft(_groove, -grooveR);
        Canvas.SetTop(_groove, -grooveR);
        _groove.StrokeThickness = 1;
        _groove.IsHitTestVisible = false;
        _ring.Children.Add(_groove);

        _ring.Children.Add(_petals);
        _ring.Children.Add(_labels);

        _hubText.FontFamily = UiFonts.Display;
        _hubText.FontWeight = FontWeights.SemiBold;
        _hubText.FontSize = 11.5;
        _hubText.TextAlignment = TextAlignment.Center;
        _hubText.TextTrimming = TextTrimming.CharacterEllipsis;
        _hubText.MaxWidth = 64;
        _hubSubtext.FontFamily = UiFonts.Text;
        _hubSubtext.FontSize = 8.5;
        _hubSubtext.TextAlignment = TextAlignment.Center;
        var hubStack = new StackPanel();
        hubStack.Children.Add(_hubText);
        hubStack.Children.Add(_hubSubtext);
        _hubPill.Child = hubStack;
        _hubPill.CornerRadius = new CornerRadius(13);
        _hubPill.Padding = new Thickness(9, 4, 9, 5);
        _hubPill.BorderThickness = new Thickness(0.75);
        _hubPill.MinWidth = 52;
        _hubPill.IsHitTestVisible = false;
        _ring.Children.Add(_hubPill);

        _hint.CornerRadius = new CornerRadius(12);
        _hint.Padding = new Thickness(8, 5, 10, 5);
        _hint.BorderThickness = new Thickness(0.75);
        _hint.IsHitTestVisible = false;
        _stage.Children.Add(_hint);

        _emptyText.FontFamily = UiFonts.Text;
        _emptyText.FontSize = 12.5;
        _emptyText.TextWrapping = TextWrapping.Wrap;
        _emptyText.TextAlignment = TextAlignment.Center;
        _emptyText.LineHeight = 19;
        _emptyCard.CornerRadius = new CornerRadius(18);
        _emptyCard.Padding = new Thickness(22, 18, 22, 18);
        _emptyCard.MaxWidth = 250;
        _emptyCard.BorderThickness = new Thickness(1);
        _emptyCard.Visibility = Visibility.Collapsed;
        _stage.Children.Add(_emptyCard);
    }

    private static T Freeze<T>(T f) where T : Freezable
    {
        f.Freeze();
        return f;
    }
}
