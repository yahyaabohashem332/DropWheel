using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
using Petal.App.Localization;

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
    private readonly Path _hubShape = new();
    private readonly RectangleGeometry _hubOutline = new();
    private readonly Canvas _hubText = new();
    private readonly List<(InkText Line, bool IsTitle)> _hubLines = [];
    private readonly Border _hint = new();
    private readonly Border _emptyCard = new();
    private readonly TextBlock _emptyText = new();
    private readonly DispatcherTimer _probeTimer = new() { Interval = TimeSpan.FromMilliseconds(140) };
    private readonly DispatcherTimer _releaseTimer = new() { Interval = TimeSpan.FromMilliseconds(450) };
    private readonly DragDropHelper _dragHelper = new();
    private readonly List<Path> _petalPaths = [];
    private readonly List<(List<InkText> Lines, Path? Icon)> _petalLabels = [];
    private readonly List<PetalLabel> _petalLayouts = [];
    private InkFont? _labelFont;
    private InkFont? _hubTitleFont;
    private InkFont? _hubSubtitleFont;

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
    private long _openedAt;

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
        Title = L.T("Petal wheel");
        // The ring is drawn in screen geometry, so it stays left-to-right; its texts set their own direction.
        L.Apply(_root, mirror: false);
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

    /// <summary>The centre's second line, when how many items were dropped won't do (what a folder holds).</summary>
    public Func<IReadOnlyList<string>, string?>? HubSubtitleProvider { get; set; }

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

    /// <summary>Show the "Shift Convert · Ctrl Tools · Alt Actions" strip above the ring.</summary>
    public bool ShowHints { get; set; } = true;

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
        // Build every kind of wheel once while hidden so the first real one opens without JIT delay.
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
        {
            try
            {
                _files = [System.IO.Path.Combine(System.IO.Path.GetTempPath(), "petal-warmup.jpg")];
                foreach (var mode in Enum.GetValues<WheelMode>())
                {
                    _mode = mode;
                    Render(animate: false);
                    SetHighlight(0, null);
                }
                _root.Measure(new Size(Side, Side));
                _root.Arrange(new Rect(0, 0, Side, Side));
            }
            catch
            {
                // Warm-up only.
            }
            finally
            {
                _files = null;
                _content = null;
                _highlight = null;
                Park();
            }
        });
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

        var (left, top, px) = Place(cursor);

        // Invisible but hit-testable everywhere, so Explorer's drag loop picks us as the target.
        _root.Visibility = Visibility.Visible;
        _root.Background = HitBrush;
        _stage.Opacity = 0;
        _hint.Opacity = 0;
        _emptyCard.Visibility = Visibility.Collapsed;
        _probing = true;
        _probeTicks = 0;

        SetWindowPos(_hwnd, HWND_TOPMOST, left, top, px, px, SWP_NOACTIVATE | SWP_SHOWWINDOW);
        _openedAt = Environment.TickCount64;
        Log.Info($"wheel: probe at {_anchor.X},{_anchor.Y} mode {mode}");
        // Nudge the drag loop: it re-evaluates the drop target on the next mouse move.
        InjectZeroMouseMove();
        _probeTimer.Start();
    }

    /// <summary>Centres the wheel on the cursor, kept on screen, and captures the glass behind it.</summary>
    private (int Left, int Top, int Size) Place(ScreenPoint cursor)
    {
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

        ApplyGlass(GlassCapture.CaptureBlurred(left, top, px, px, scale));
        return (left, top, px);
    }

    // ---------- click & keyboard mode (Explorer's context menu) ----------

    private bool _interactive;
    // The window that had the keyboard before the keyboard-driven wheel took it.
    private IntPtr _returnFocusTo;

    /// <summary>Maps a modifier key to the wheel it opens (follows the user's key settings).</summary>
    public Func<Key, WheelMode?>? ModeForKey { get; set; }

    /// <summary>
    /// Opens the wheel for files chosen without dragging (e.g. "Convert with Petal" in the context
    /// menu). Petals are clicked, or picked with the arrow keys and Enter; Shift, Ctrl and Alt switch wheels.
    /// </summary>
    public void OpenInteractive(IReadOnlyList<string> files, ScreenPoint cursor, WheelMode mode)
    {
        if (_open) CloseWheel();
        _open = true;
        _interactive = true;
        _mode = mode;
        _files = files;
        _highlight = null;
        _palette = Theme.Current;
        Sound.Warm();
        var (left, top, px) = Place(cursor);
        _root.Visibility = Visibility.Visible;
        _emptyCard.Visibility = Visibility.Collapsed;
        Render(animate: true);
        _root.Background = HitBrush;

        // Allow activation so the keyboard reaches us.
        var exStyle = (long)GetWindowLongPtr(_hwnd, GWL_EXSTYLE);
        SetWindowLongPtr(_hwnd, GWL_EXSTYLE, new IntPtr(exStyle & ~WS_EX_NOACTIVATE));
        SetWindowPos(_hwnd, HWND_TOPMOST, left, top, px, px, SWP_SHOWWINDOW);
        Focusable = true;
        var front = GetForegroundWindow();
        _returnFocusTo = front != _hwnd ? front : IntPtr.Zero;
        SetForegroundWindow(_hwnd);
        Activate();
        Focus();
        Keyboard.Focus(this);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_interactive || _content is null) return;
        var p = e.GetPosition(_stage);
        var index = WheelGeometry.HitTest(new Vector(p.X - Centre, p.Y - Centre), _content.Items.Count);
        if (index is not null) SetHighlight(index, null);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_interactive || _content is null) return;
        var p = e.GetPosition(_stage);
        var v = new Vector(p.X - Centre, p.Y - Centre);
        if (WheelGeometry.HitTest(v, _content.Items.Count) is { } index)
            Choose(index);
        else if (!WheelGeometry.IsOverHub(v))
            CloseWheel();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (!_interactive) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        int count = _content?.Items.Count ?? 0;
        switch (key)
        {
            case Key.Escape:
                CloseWheel();
                break;
            case Key.Right or Key.Down when count > 0:
                SetHighlight(((_highlight ?? -1) + 1) % count, null);
                break;
            case Key.Left or Key.Up when count > 0:
                SetHighlight(((_highlight ?? 0) - 1 + count) % count, null);
                break;
            case Key.Home when count > 0:
                SetHighlight(0, null);
                break;
            case Key.End when count > 0:
                SetHighlight(count - 1, null);
                break;
            case Key.Enter or Key.Space when _highlight is { } i && !e.IsRepeat:
                Choose(i);
                break;
            case Key.Tab:
                SwitchMode(_mode switch { WheelMode.Conversions => WheelMode.Tools, WheelMode.Tools => WheelMode.Actions, _ => WheelMode.Conversions });
                break;
            case Key.LeftShift or Key.RightShift or Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt when !e.IsRepeat:
                if (ModeForKey?.Invoke(key) is { } m) SwitchMode(m);
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);
        // Something else took the keyboard; leave it there.
        if (_interactive) CloseWheel(returnFocus: false);
    }

    private void Choose(int index)
    {
        if (_content is null || _files is null || index >= _content.Items.Count) return;
        var item = _content.Items[index];
        var files = _files;
        var at = _anchor;
        Sound.Drop();
        // A tool window, the share sheet or a folder picker takes the keyboard next; otherwise
        // (conversions, copying) it goes back to File Explorer.
        bool opensWindow = item.Payload is Petal.Core.Tools.ToolInfo { HasWindow: true }
            || item.Payload is Petal.Core.Actions.FileActionInfo { Id: Petal.Core.Actions.FileActionId.Share or Petal.Core.Actions.FileActionId.Phone or Petal.Core.Actions.FileActionId.Float or Petal.Core.Actions.FileActionId.Move or Petal.Core.Actions.FileActionId.Terminal };
        CloseWheel(returnFocus: !opensWindow);
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () => ItemDropped?.Invoke(item, files, at));
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

    private void CloseWheel(bool returnFocus = true)
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
        if (_interactive)
        {
            _interactive = false;
            Focusable = false;
            var exStyle = (long)GetWindowLongPtr(_hwnd, GWL_EXSTYLE);
            SetWindowLongPtr(_hwnd, GWL_EXSTYLE, new IntPtr(exStyle | WS_EX_NOACTIVATE));
            if (returnFocus && _returnFocusTo != IntPtr.Zero)
                SetForegroundWindow(_returnFocusTo);
            _returnFocusTo = IntPtr.Zero;
        }
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
            Log.Info($"wheel: DragEnter after {Environment.TickCount64 - _openedAt} ms, {files.Count} file(s)");
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

        // Let the petal stay lit for a couple of frames, then vanish instantly. Only then run the
        // action: opening a tool window can keep the UI thread busy, and the wheel must not linger.
        var drop = new ScreenPoint(point.X, point.Y);
        var close = new DispatcherTimer(DispatcherPriority.Send) { Interval = TimeSpan.FromMilliseconds(50) };
        close.Tick += (_, _) =>
        {
            close.Stop();
            CloseWheel();
            Dispatcher.BeginInvoke(DispatcherPriority.Background, () => ItemDropped?.Invoke(item, files, drop));
        };
        close.Start();
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
            var (lines, icon) = _petalLabels[i];
            var fg = on ? Palette.Brush(p.TextOnAccent) : Palette.Brush(p.Text);
            foreach (var line in lines)
                line.Foreground = fg;
            if (icon is not null)
                icon.Stroke = fg;
        }

        if (index is { } i2 && _content is not null && i2 < _content.Items.Count)
        {
            var item = _content.Items[i2];
            _lastHubLabel = item.Payload is Petal.Core.Formats.FileFormat or Petal.Core.Jobs.Favourite or Petal.Recipes.Recipe ? item.Label : L.T(item.Name);
            SetHub(_lastHubLabel, Subtitle(item), animate: IsVisible);
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
        Petal.Core.Formats.FileFormat => L.T("Convert"),
        // A favourite's petal shows its name; the centre adds what it does ("WEBP · 1600 px").
        Petal.Core.Jobs.Favourite => item.Name,
        Petal.Recipes.Recipe => L.T("Recipe"),
        Petal.Core.Tools.ToolInfo => L.T("Tool"),
        _ => L.T("Action"),
    };

    private static string Description(WheelItem item) => item.Payload switch
    {
        Petal.Core.Formats.FileFormat f => L.F("Convert to {0}", f.Label),
        _ => L.T(item.Name),
    };

    /// <summary>The labels of the petals last rendered, for design snapshots.</summary>
    internal IReadOnlyList<string> PetalLabels => _content?.Items.Select(i => L.T(i.Label)).ToList() ?? [];

    /// <summary>How each of those labels was laid out, for design snapshots.</summary>
    internal IReadOnlyList<PetalLabel> PetalLayouts => _petalLayouts;

    /// <summary>
    /// Renders the wheel over a background image without a screen, for design reviews and tests.
    /// </summary>
    internal BitmapSource Snapshot(IReadOnlyList<string> files, WheelMode mode, int? highlight, BitmapSource background, bool hintBelow = false)
    {
        _palette = Theme.Current;
        _files = files;
        _mode = mode;
        _open = true;
        var crop = new CroppedBitmap(background, new Int32Rect(
            Math.Max(0, (background.PixelWidth - (int)Side) / 2),
            Math.Max(0, (background.PixelHeight - (int)Side) / 2),
            Math.Min((int)Side, background.PixelWidth),
            Math.Min((int)Side, background.PixelHeight)));
        ApplyGlass(GlassCapture.Blur(crop));
        Canvas.SetTop(_hint, hintBelow ? Centre + WheelGeometry.OuterRadius + 12 : Centre - WheelGeometry.OuterRadius - 12 - 30);
        _root.Visibility = Visibility.Visible;
        Render(animate: false);
        SetHighlight(highlight, null);

        var frame = new Grid { Width = Side, Height = Side };
        frame.Children.Add(new Image { Source = crop, Stretch = Stretch.UniformToFill });
        Content = null;
        frame.Children.Add(_root);
        frame.Measure(new Size(Side, Side));
        frame.Arrange(new Rect(0, 0, Side, Side));
        frame.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Side * 2, (int)Side * 2, 192, 192, PixelFormats.Pbgra32);
        bitmap.Render(frame);
        bitmap.Freeze();
        frame.Children.Remove(_root);
        Content = _root;
        _open = false;
        return bitmap;
    }

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
        _petalLayouts.Clear();
        CreateFonts();
        UpdateHint();

        // The safe zone around the ring swallows near-miss drops instead of moving files.
        _root.Background = null;
        _catcher.Fill = HitBrush;

        if (_content.IsEmpty)
        {
            _ring.Visibility = Visibility.Collapsed;
            _emptyCard.Visibility = Visibility.Visible;
            _emptyText.Text = _content.EmptyMessage;
            _emptyText.FlowDirection = L.Direction;
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
                AddLabel(item, i, n, tools || item.Payload is Petal.Core.Jobs.Favourite, p);
            }
        }

        SetHub(HubTextProvider?.Invoke(_files) ?? "", HubSubtitleProvider?.Invoke(_files) ?? (_files.Count == 1 ? "" : L.P(_files.Count, "{0} item", "{0} items")), animate: false);
        ApplyPaletteToChrome(p);

        if (animate)
        {
            _stage.Opacity = 0;
            _stage.BeginAnimation(OpacityProperty, Motion.To(1, 90, Motion.Standard, from: 0));
            _stageScale.BeginAnimation(ScaleTransform.ScaleXProperty, Motion.To(1, 190, Motion.Bloom, from: 0.38));
            _stageScale.BeginAnimation(ScaleTransform.ScaleYProperty, Motion.To(1, 190, Motion.Bloom, from: 0.38));
            _labels.BeginAnimation(OpacityProperty, Motion.To(1, 90, Motion.Standard, from: 0, delayMs: 55));
            _hint.BeginAnimation(OpacityProperty, Motion.To(1, 160, Motion.Standard, from: 0, delayMs: 120));
            _hint.Visibility = ShowHints ? Visibility.Visible : Visibility.Collapsed;
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

    /// <summary>
    /// Fonts measured at this window's DPI and in the language's direction. They keep what they measured
    /// while neither changes, so each name is measured once.
    /// </summary>
    private void CreateFonts()
    {
        double dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        if (_labelFont is { } font && font.PixelsPerDip == dip && font.Direction == L.Direction)
            return;
        _labelFont = new InkFont(UiFonts.Display, FontWeights.SemiBold, L.Direction, dip);
        _hubTitleFont = new InkFont(UiFonts.Display, FontWeights.SemiBold, L.Direction, dip);
        _hubSubtitleFont = new InkFont(UiFonts.Text, FontWeights.Normal, L.Direction, dip);
    }

    /// <summary>
    /// A petal's label, laid out by hand: <see cref="PetalLayout"/> picks its lines and size and keeps it
    /// inside the petal, then the ink of the icon and of every line is centred on one point, so what the
    /// eye sees is centred rather than text boxes and trailing spaces.
    /// </summary>
    private void AddLabel(WheelItem item, int index, int count, bool withIcon, Palette p)
    {
        double size = withIcon ? (L.IsArabic ? (count > 8 ? 10 : 10.5) : count > 8 ? 8 : 8.5) : (count > 9 ? 11.5 : count > 7 ? 12.5 : 13.5);
        // Baseline to baseline; under an icon the lines sit close, as a caption.
        double step = withIcon ? (L.IsArabic ? 13 : 9.5) : size * 1.3;
        double iconSize = count > 8 ? 16 : 18;
        var brush = Palette.Brush(p.Text);
        Path? icon = null;
        IconInk? iconInk = null;
        if (withIcon && item.Icon is not null)
        {
            icon = Icons.Create(item.Icon, iconSize, brush, 2);
            var outline = Icons.InkGeometry(item.Icon, iconSize, 2);
            iconInk = new IconInk(outline.Bounds, InkShape.Strips(outline, 3));
        }

        var font = _labelFont!;
        var label = PetalLayout.Fit(L.T(item.Label), font, size, size - 1.5, step, iconInk, iconSize, index, count);
        var lines = label.Lines.Select(t => font.Create(t, label.Size, brush)).ToList();
        if (icon is not null && iconInk is not null)
        {
            Canvas.SetLeft(icon, label.Centre.X - iconInk.Box.X - iconInk.Box.Width / 2);
            Canvas.SetTop(icon, label.Centre.Y + label.IconTop);
            _labels.Children.Add(icon);
        }
        for (int i = 0; i < lines.Count; i++)
        {
            lines[i].Place(label.Centre.X, label.Centre.Y + label.Baselines[i]);
            _labels.Children.Add(lines[i]);
        }
        _petalLabels.Add((lines, icon));
        _petalLayouts.Add(label);
    }

    /// <summary>
    /// Sets the centre's name and what it is. The lines and their outline come from <see cref="HubLayout"/>;
    /// while the wheel is on screen the outline glides between a pill and a disc as names change.
    /// </summary>
    private void SetHub(string title, string subtitle, bool animate)
    {
        _hubText.Children.Clear();
        _hubLines.Clear();
        _hubShape.Visibility = string.IsNullOrEmpty(title) ? Visibility.Hidden : Visibility.Visible;
        if (string.IsNullOrEmpty(title))
            return;
        if (_hubTitleFont is null)
            CreateFonts();

        var p = _palette;
        var fit = HubLayout.Fit(title, subtitle, _hubTitleFont!, _hubSubtitleFont!, L.IsArabic);
        foreach (var row in fit.Rows)
        {
            var font = row.IsTitle ? _hubTitleFont! : _hubSubtitleFont!;
            var line = font.Create(row.Text, row.Size, Palette.Brush(row.IsTitle ? p.Text : p.TextSecondary));
            line.Place(0, row.Baseline);
            _hubText.Children.Add(line);
            _hubLines.Add((line, row.IsTitle));
        }

        var outline = new Rect(-fit.HalfWidth, -fit.HalfHeight, 2 * fit.HalfWidth, 2 * fit.HalfHeight);
        double radius = Math.Min(fit.HalfWidth, fit.HalfHeight);
        if (animate)
        {
            var duration = TimeSpan.FromMilliseconds(140);
            _hubOutline.BeginAnimation(RectangleGeometry.RectProperty, new RectAnimation(outline, duration) { EasingFunction = Motion.Standard });
            _hubOutline.BeginAnimation(RectangleGeometry.RadiusXProperty, Motion.To(radius, 140));
            _hubOutline.BeginAnimation(RectangleGeometry.RadiusYProperty, Motion.To(radius, 140));
            _hubText.BeginAnimation(OpacityProperty, Motion.To(1, 120, from: 0.35));
        }
        else
        {
            _hubOutline.BeginAnimation(RectangleGeometry.RectProperty, null);
            _hubOutline.BeginAnimation(RectangleGeometry.RadiusXProperty, null);
            _hubOutline.BeginAnimation(RectangleGeometry.RadiusYProperty, null);
            _hubText.BeginAnimation(OpacityProperty, null);
            _hubOutline.Rect = outline;
            _hubOutline.RadiusX = _hubOutline.RadiusY = radius;
            _hubText.Opacity = 1;
        }
    }

    private void UpdateHint()
    {
        var p = _palette;
        var row = new StackPanel { Orientation = Orientation.Horizontal, FlowDirection = L.Direction };
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
                Text = L.T(label),
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
        _groove.Stroke = Palette.Brush(p.IsDark ? Color.FromArgb(0x2A, 255, 255, 255) : Color.FromArgb(0x90, 255, 255, 255));
        _shadow.Fill = new RadialGradientBrush(
        [
            new GradientStop(p.Shadow, 0.78),
            new GradientStop(Color.FromArgb(0, p.Shadow.R, p.Shadow.G, p.Shadow.B), 1),
        ]);
        _hubShape.Fill = Palette.Brush(p.PillFill);
        _hubShape.Stroke = Palette.Brush(p.GlassEdge);
        foreach (var (line, isTitle) in _hubLines)
            line.Foreground = Palette.Brush(isTitle ? p.Text : p.TextSecondary);
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

        _glassRing.Data = WheelGeometry.Ring(WheelGeometry.HubRadius, WheelGeometry.OuterRadius);
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

        double grooveR = WheelGeometry.HubRadius + 0.5;
        _groove.Width = _groove.Height = grooveR * 2;
        Canvas.SetLeft(_groove, -grooveR);
        Canvas.SetTop(_groove, -grooveR);
        _groove.StrokeThickness = 1;
        _groove.IsHitTestVisible = false;
        _ring.Children.Add(_groove);

        _ring.Children.Add(_petals);
        _ring.Children.Add(_labels);

        // The centre's outline is a rounded rectangle so it can glide between a pill and a disc.
        _hubShape.Data = _hubOutline;
        _hubShape.StrokeThickness = 0.75;
        _hubShape.IsHitTestVisible = false;
        _hubText.IsHitTestVisible = false;
        _ring.Children.Add(_hubShape);
        _ring.Children.Add(_hubText);

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
