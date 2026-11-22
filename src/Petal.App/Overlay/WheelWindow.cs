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
using Petal.Core.Util;
using static Petal.App.Interop.NativeMethods;
using Petal.App.Localization;

namespace Petal.App.Overlay;

/// <summary>
/// The radial menu. It is an always-created, never-activated, transparent topmost window that
/// is a normal OLE drop target: while the user is still dragging a file out of Explorer it
/// slides under the cursor, Explorer hands it the dragged data, and dropping on a petal runs
/// the petal's action. Nothing is intercepted: dropping elsewhere behaves exactly as usual.
/// </summary>
public sealed partial class WheelWindow : Window
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
    // The mark under a petal's name in the centre: its explanation, beside the wheel (also F1).
    private readonly Canvas _infoMark = new();
    private readonly ScaleTransform _infoScale = new();
    private const double InfoRadius = 8;
    private Point _infoAt;
    private WheelItem? _hubAbout;
    private readonly List<(InkText Line, bool IsTitle)> _hubLines = [];
    private readonly Border _hint = new();
    private readonly Border _emptyCard = new();
    private readonly TextBlock _emptyText = new();
    private readonly DispatcherTimer _probeTimer = new() { Interval = TimeSpan.FromMilliseconds(140) };
    private readonly DispatcherTimer _releaseTimer = new() { Interval = TimeSpan.FromMilliseconds(450) };
    private readonly DispatcherTimer _dwellTimer = new() { Interval = TimeSpan.FromMilliseconds(420) };
    // A short rest on a petal before its result is worked out, so passing over petals costs nothing.
    private readonly DispatcherTimer _previewTimer = new() { Interval = TimeSpan.FromMilliseconds(220) };
    private CancellationTokenSource? _previewCancel;
    private readonly DragDropHelper _dragHelper = new();
    private readonly List<PetalVisual> _visuals = [];
    private readonly List<PetalLabel> _petalLayouts = [];
    // Groups opened on the way here, innermost last: what to go back to.
    private readonly Stack<(WheelContent Content, string Title)> _groups = new();
    private readonly Ellipse _hubPicture = new();
    private readonly RotateTransform _hubPictureTurn = new();
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
    // Files read from a drag that had none on disk (Outlook, a browser): kept only if dropped on a petal.
    private IReadOnlyList<string>? _staged;

    /// <summary>A dragged link's file coming down while the wheel is open; its path is in <see cref="_staged"/> once named.</summary>
    private Shell.LinkDownload? _link;

    /// <summary>The files the wheel is open for (empty for the wheels without files).</summary>
    public IReadOnlyList<string> CurrentFiles => _files ?? [];
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
        Title = L.T("DropWheel wheel");
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
        _dwellTimer.Tick += OnDwell;
        _previewTimer.Tick += OnPreviewTick;
        Theme.Changed += () => Dispatcher.BeginInvoke(() => _palette = PaletteFor(_options));
    }

    public FeedbackSound Sound { get; }

    /// <summary>Supplies the petals for the dragged files in a mode.</summary>
    public Func<WheelMode, IReadOnlyList<string>, WheelContent>? ContentProvider { get; set; }

    /// <summary>What a file wheel's petal will make, for the centre while the pointer rests on it.</summary>
    public Func<WheelItem, IReadOnlyList<string>, CancellationToken, Task<PetalPreview?>>? PreviewProvider { get; set; }

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

    /// <summary>Open for dragged files (not still finding out, not the wheels without files).</summary>
    public bool ShowsFiles => _open && _files is not null && _pages is null;

    /// <summary>Show the "Shift Convert · Ctrl Tools · Alt Actions" strip above the ring.</summary>
    public bool ShowHints { get; set; } = true;

    public WheelMode Mode => _mode;

    /// <summary>How the wheel looks and behaves; read each time it opens.</summary>
    public Func<WheelOptions> OptionsProvider { get; set; } = () => WheelOptions.Default;

    private WheelOptions _options = WheelOptions.Default;

    /// <summary>
    /// The petal to light up first when the wheel opens for the keyboard (the one chosen last time), by
    /// page ("files:Conversions", "ring:quick").
    /// </summary>
    public Func<string, WheelContent, int?>? Remembered { get; set; }

    /// <summary>A petal was chosen on a page: for remembering it.</summary>
    public event Action<string, WheelItem>? Chose;

    /// <summary>
    /// Raised when a petal of a wheel without files is chosen, after the wheel has gone; with the window
    /// that was in front when it opened (where keystrokes go).
    /// </summary>
    public event Action<WheelItem, ScreenPoint, IntPtr>? EntryChosen;

    /// <summary>A petal that can't run was chosen: say why at this point.</summary>
    public event Action<WheelItem, ScreenPoint>? UnavailableChosen;

    /// <summary>The wheel went away, whether a petal was chosen (<see cref="Chose"/> came first then) or not.</summary>
    public event Action? Closed;

    /// <summary>The page shown now, for memory and statistics.</summary>
    public string PageKey => _pages is { } pages ? "ring:" + pages[_page].Key : "files:" + _mode;

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
        _pages = null;
        _groups.Clear();
        _options = OptionsProvider();
        _releaseTimer.Stop();
        _palette = PaletteFor(_options);
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
        var onScreen = new Rect((work.Left - left) / scale - Centre, (work.Top - top) / scale - Centre,
            (work.Right - work.Left) / scale, (work.Bottom - work.Top) / scale);
        onScreen.Intersect(new Rect(-Centre, -Centre, Side, Side));
        _visible = onScreen.IsEmpty ? new Rect(-Centre, -Centre, Side, Side) : onScreen;

        // Hint goes below the ring when there is no room above it.
        bool hintBelow = cy - work.Top < (WheelGeometry.OuterRadius + 64) * scale;
        // The keys 1 to 9 sit just outside the ring when the keyboard can use them; the hint keeps clear of them.
        double gap = 12 + (_interactive && _options.ShowNumbers ? 10 : 0);
        Canvas.SetTop(_hint, hintBelow ? Centre + WheelGeometry.OuterRadius + gap : Centre - WheelGeometry.OuterRadius - gap - 30);

        // A solid wheel has nothing behind it to see through, so nothing is captured.
        ApplyGlass(_options.Style == WheelStyle.Solid ? null : GlassCapture.CaptureBlurred(left, top, px, px, scale));
        return (left, top, px);
    }

    // ---------- click & keyboard mode (Explorer's context menu) ----------

    private bool _interactive;
    // The window that had the keyboard before the keyboard-driven wheel took it.
    private IntPtr _returnFocusTo;
    // Wheels without files, when that is what is open; null for a file's wheels.
    private IReadOnlyList<RingPage>? _pages;
    private int _page;

    private double CancelRadius => WheelGeometry.CancelRadius(_options.CancelZone);

    private int? HitTestAt(Point p) =>
        _content is null ? null : WheelGeometry.HitTest(new Vector(p.X - Centre, p.Y - Centre), _content.Items.Count, CancelRadius);

    /// <summary>Maps a modifier key to the wheel it opens (follows the user's key settings).</summary>
    public Func<Key, WheelMode?>? ModeForKey { get; set; }

    /// <summary>
    /// Opens the wheel for files chosen without dragging (e.g. "Convert with Petal" in the context
    /// menu). Petals are clicked, or picked with the arrow keys and Enter; Shift, Ctrl and Alt switch wheels.
    /// </summary>
    public void OpenInteractive(IReadOnlyList<string> files, ScreenPoint cursor, WheelMode mode)
    {
        if (_open) CloseWheel();
        _pages = null;
        _files = files;
        _mode = mode;
        ShowInteractive(cursor);
    }

    /// <summary>
    /// Opens wheels that need no files (quick actions, media, the app in front, the user's own) for the
    /// keyboard and mouse, starting at <paramref name="start"/>; Tab and the mouse wheel go to the next one.
    /// </summary>
    public void OpenRings(IReadOnlyList<RingPage> pages, int start, ScreenPoint cursor)
    {
        if (pages.Count == 0) return;
        if (_open) CloseWheel();
        _pages = pages;
        _page = Math.Clamp(start, 0, pages.Count - 1);
        _files = [];
        ShowInteractive(cursor);
    }

    private void ShowInteractive(ScreenPoint cursor)
    {
        _open = true;
        _interactive = true;
        _highlight = null;
        _groups.Clear();
        _options = OptionsProvider();
        _palette = PaletteFor(_options);
        // Before the wheel takes the keyboard: where keystrokes go back to.
        var front = GetForegroundWindow();
        _returnFocusTo = front != _hwnd ? front : IntPtr.Zero;
        Sound.Warm();
        var (left, top, px) = Place(cursor);
        _root.Visibility = Visibility.Visible;
        _emptyCard.Visibility = Visibility.Collapsed;
        Render(animate: true);
        if (_content is { IsEmpty: false } content && Remembered?.Invoke(PageKey, content) is { } remembered && remembered < content.Items.Count)
            SetHighlight(remembered, null);
        _root.Background = HitBrush;

        // Allow activation so the keyboard reaches us.
        var exStyle = (long)GetWindowLongPtr(_hwnd, GWL_EXSTYLE);
        SetWindowLongPtr(_hwnd, GWL_EXSTYLE, new IntPtr(exStyle & ~WS_EX_NOACTIVATE));
        SetWindowPos(_hwnd, HWND_TOPMOST, left, top, px, px, SWP_SHOWWINDOW);
        Focusable = true;
        SetForegroundWindow(_hwnd);
        Activate();
        Focus();
        Keyboard.Focus(this);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_interactive || _content is null || _calc is not null) return;
        var p = e.GetPosition(_stage);
        var v = new Vector(p.X - Centre, p.Y - Centre);
        HoverInfoMark(OverInfoMark(v));
        if (_orbit is { } o)
        {
            // Among the bubbles, or on the way to them from their petal: the orbit stays.
            if (OrbitBubbleAt(v) is { } k)
            {
                SetOrbitHighlight(k, null);
                return;
            }
            if (HitTestAt(p) == o.Group || InOrbitZone(o, v))
            {
                SetOrbitHighlight(null, null);
                return;
            }
            CloseOrbit();
        }
        var index = HitTestAt(p);
        if (index is not null && index != _highlight)
        {
            SetHighlight(index, null);
            // Pointing at a group for a moment opens it, as clicking does.
            _dwellTimer.Stop();
            _dwellOnHub = false;
            if (_content.Items[index.Value].IsGroup)
            {
                _dwellTimer.Interval = TimeSpan.FromMilliseconds(240);
                _dwellTimer.Start();
            }
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_interactive || _content is null) return;
        var p = e.GetPosition(_stage);
        var v = new Vector(p.X - Centre, p.Y - Centre);
        if (OverInfoMark(v))
        {
            OpenGuideCard();
            return;
        }
        if (_orbit is { } o)
        {
            if (OrbitBubbleAt(v) is { } k)
            {
                ChooseItem(o.Bubbles[k].Item);
                return;
            }
            // The group petal again, or the middle, takes the bubbles back in.
            if (HitTestAt(p) == o.Group || v.Length < CancelRadius)
            {
                CloseOrbit();
                return;
            }
            if (InOrbitZone(o, v))
                return;
        }
        if (HitTestAt(p) is { } index)
            Choose(index);
        else if (v.Length < CancelRadius && _groups.Count > 0)
            CloseGroup();
        else if (!WheelGeometry.IsOverHub(v) && v.Length > CancelRadius)
            CloseWheel();
    }

    /// <summary>The mouse wheel goes to the next wheel, as Tab does (and back, turned the other way).</summary>
    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (!_interactive || !_options.ScrollSwitches) return;
        Step(e.Delta < 0 ? 1 : -1);
        e.Handled = true;
    }

    /// <summary>Next (1) or previous (-1) wheel: the file's three, or the wheels without files.</summary>
    public void Step(int direction)
    {
        if (!_open) return;
        if (_pages is { } pages)
        {
            if (pages.Count < 2) return;
            _page = (_page + direction + pages.Count) % pages.Count;
            _groups.Clear();
            Render(animate: false);
            if (_content is { IsEmpty: false } content && Remembered?.Invoke(PageKey, content) is { } remembered && remembered < content.Items.Count)
                SetHighlight(remembered, null);
            Sound.Tick();
            return;
        }
        var modes = Enum.GetValues<WheelMode>();
        SwitchMode(modes[(Array.IndexOf(modes, _mode) + direction + modes.Length) % modes.Length]);
        Sound.Tick();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (!_interactive) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (CalcKey(key, e))
            return;
        if (OrbitKey(key, e.IsRepeat))
        {
            e.Handled = true;
            return;
        }
        if (key == Key.F1 && !e.IsRepeat && _hubAbout is not null)
        {
            OpenGuideCard();
            e.Handled = true;
            return;
        }
        int count = _content?.Items.Count ?? 0;
        // 1 to 9 choose a petal straight away, clockwise from the top.
        int digit = key is >= Key.D1 and <= Key.D9 ? key - Key.D1 : key is >= Key.NumPad1 and <= Key.NumPad9 ? key - Key.NumPad1 : -1;
        if (digit >= 0 && _options.ShowNumbers && Keyboard.Modifiers == ModifierKeys.None)
        {
            if (digit < count && !e.IsRepeat)
                Choose(digit, keyboard: true);
            e.Handled = true;
            return;
        }
        switch (key)
        {
            case Key.Escape when _groups.Count > 0:
            case Key.Back when _groups.Count > 0:
                CloseGroup();
                break;
            case Key.Back when _chain.Count > 0 && _pages is null:
                RemoveStep();
                break;
            case Key.OemPlus or Key.Add when _pages is null && !e.IsRepeat:
                AddStep();
                break;
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
                Choose(i, keyboard: true);
                break;
            case Key.Tab:
                Step(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1);
                break;
            case Key.LeftShift or Key.RightShift or Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt when !e.IsRepeat && _pages is null:
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

    private void Choose(int index, bool keyboard = false)
    {
        if (_content is null || _files is null || index >= _content.Items.Count) return;
        var item = _content.Items[index];
        if (item.IsGroup)
        {
            // Its petals come out around it; one too big for an orbit opens in place.
            if (CanOrbit(item))
                OpenOrbit(index, keyboard);
            else
                OpenGroup(item);
            return;
        }
        ChooseItem(item);
    }

    /// <summary>Runs a petal of the wheel or of an orbit, once the wheel has gone.</summary>
    private void ChooseItem(WheelItem item)
    {
        if (_files is null) return;
        if (item.IsGroup)
        {
            // A group inside a group opens in place of the wheel.
            CloseOrbit(animate: false);
            OpenGroup(item);
            return;
        }
        var files = _files;
        var at = _anchor;
        var page = PageKey;
        if (item.Unavailable is not null && item.Blocked)
        {
            Sound.Tick();
            CloseWheel();
            Dispatcher.BeginInvoke(DispatcherPriority.Background, () => UnavailableChosen?.Invoke(item, at));
            return;
        }
        Sound.Drop();
        Chose?.Invoke(page, item);
        if (_pages is not null)
        {
            var target = _returnFocusTo;
            CloseWheel(returnFocus: true);
            Dispatcher.BeginInvoke(DispatcherPriority.Background, () => EntryChosen?.Invoke(item, at, target));
            return;
        }
        // A tool window, the share sheet or a folder picker takes the keyboard next; otherwise
        // (conversions, copying) it goes back to File Explorer.
        bool opensWindow = item.Payload is Petal.Core.Tools.ToolInfo { HasWindow: true }
            || item.Payload is Petal.Core.Actions.FileActionInfo
            {
                Id: Petal.Core.Actions.FileActionId.Share or Petal.Core.Actions.FileActionId.Phone or Petal.Core.Actions.FileActionId.Float
                    or Petal.Core.Actions.FileActionId.Move or Petal.Core.Actions.FileActionId.Terminal or Petal.Core.Actions.FileActionId.OpenWith
                    or Petal.Core.Actions.FileActionId.OpenInCode
            };
        var chain = _chain.ToList();
        CloseWheel(returnFocus: !opensWindow);
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            if (chain.Count > 0)
                ChainDropped?.Invoke(chain, item, files, at);
            else
                ItemDropped?.Invoke(item, files, at);
        });
    }

    /// <summary>Opens a group's petals in place of the wheel's; the centre (or Backspace) goes back.</summary>
    private void OpenGroup(WheelItem group)
    {
        if (_content is null || group.Children is null) return;
        _dwellTimer.Stop();
        ResetOrbit();
        _groups.Push((_content, L.T(group.Name)));
        _content = new WheelContent(group.Children, "");
        Sound.Tick();
        Draw(animate: true);
    }

    private void CloseGroup()
    {
        _dwellTimer.Stop();
        if (!_groups.TryPop(out var outer)) return;
        _content = outer.Content;
        Sound.Tick();
        Draw(animate: true);
    }

    /// <summary>
    /// While dragging, holding over a group for a moment opens it, and holding over the centre goes back;
    /// a drop can't open anything, so the wheel waits for the pointer instead.
    /// </summary>
    private void OnDwell(object? sender, EventArgs e)
    {
        _dwellTimer.Stop();
        if (!_open || _content is null) return;
        if (_dwellOnHub)
            CloseGroup();
        else if (_highlight is { } i && i < _content.Items.Count && _content.Items[i].IsGroup)
        {
            if (CanOrbit(_content.Items[i]))
                OpenOrbit(i, keyboard: false);
            else
                OpenGroup(_content.Items[i]);
        }
    }

    private bool _dwellOnHub;

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
        if (!_open || mode == _mode || _pages is not null)
            return;
        _mode = mode;
        _groups.Clear();
        _dwellTimer.Stop();
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
        Guide.GuideCard.CloseCard();
        _probeTimer.Stop();
        _releaseTimer.Stop();
        _dwellTimer.Stop();
        CancelPreview();
        _chain.Clear();
        if (_link is { } link)
        {
            // The download stops, then its folder goes (the file is still open until it has).
            _link = null;
            _staged = null;
            link.Cancel();
            _ = link.Done.ContinueWith(_ => Shell.VirtualDrop.ForgetFolder(link.Folder), TaskScheduler.Default);
        }
        if (_staged is { } staged)
        {
            _staged = null;
            Shell.VirtualDrop.Forget(staged);
        }
        if (!_open)
            return;
        _open = false;
        _probing = false;
        _files = null;
        _content = null;
        _highlight = null;
        _groups.Clear();
        ResetOrbit();
        EndCalc();
        _pages = null;
        _hubPictureTurn.BeginAnimation(RotateTransform.AngleProperty, null);
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
        Closed?.Invoke();
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

        if (_files is null && _link is null)
        {
            var files = FilesOf(e);
            // A link from a browser or a chat: its file comes down while the wheel is open.
            if (files is null && LinkOf(e.Data) is { } address)
            {
                StartLink(address);
                _dragHelper.Enter(_hwnd, e.Data, CursorPoint(e), DragDropEffects.Copy);
                if (_files is not null)
                    UpdatePointer(e);
                else
                    e.Effects = DragDropEffects.None;
                return;
            }
            // An attachment, a picture from a browser, a file from a chat: read now, while the drag can still give it.
            if (files is null && Shell.VirtualDrop.Offers(e.Data) && Shell.VirtualDrop.Read(e.Data) is { } read)
            {
                files = read;
                _staged = read;
                Log.Info($"wheel: read {read.Count} item(s) from a drag without files");
            }
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

    /// <summary>
    /// The address the drag carries, when that is all it carries: a file the drag holds (a picture from a
    /// page) is read instead, but the shortcut a browser offers beside a link is not a file to work on.
    /// </summary>
    private static Uri? LinkOf(IDataObject data)
    {
        if (Shell.LinkDrop.LinkOf(data) is not { } address)
            return null;
        try
        {
            if (Shell.VirtualDrop.Describe(data) is { Count: > 0 } described
                && !described.All(f => !f.IsFolder && f.Name.EndsWith(".url", StringComparison.OrdinalIgnoreCase)))
                return null;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException or OutOfMemoryException)
        {
        }
        return address;
    }

    private void StartLink(Uri address)
    {
        var folder = System.IO.Path.Combine(Shell.VirtualDrop.StagingRoot, DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", System.Globalization.CultureInfo.InvariantCulture));
        var link = new Shell.LinkDownload(address, folder);
        _link = link;
        _probing = false;
        _probeTimer.Stop();
        link.Named += () =>
        {
            if (_link != link || !_open || link.Path is not { } path)
                return;
            _files = [path];
            _staged = [path];
            Render(animate: true);
        };
        link.Changed += () =>
        {
            if (_link == link && _open && _files is not null && _highlight is null && _orbit is null && _groups.Count == 0 && _chain.Count == 0)
                ShowDefaultHub(animate: false);
        };
        Log.Info($"wheel: downloading a dragged link from {address.Host}");
        link.Start();

        // Check for fake link warning immediately
        if (link.LinkAnalysis.IsSuspicious)
        {
            ShowLinkWarning(link.LinkAnalysis);
        }

        if (link.Path is { } named)
        {
            _files = [named];
            _staged = [named];
            Render(animate: true);
        }
        else
        {
            // Only the server can say what it is: the wheel waits for its answer.
            _content = new WheelContent([], L.T("Reading the link…"));
            Draw(animate: true);
        }
    }

    private void ShowLinkWarning(FakeLinkDetector.AnalysisResult analysis)
    {
        if (!analysis.IsSuspicious) return;

        var topWarning = analysis.Warnings.OrderByDescending(w => GetWarningSeverity(w.Type)).First();
        var title = L.T("⚠ Suspicious link detected");
        var subtitle = L.F("{0} (Risk: {1}%)", topWarning.Message, analysis.RiskScore);

        _content = new WheelContent([], title);
        Draw(animate: true);

        // Show detailed warning in the hub after a moment
        _ = Task.Delay(2000).ContinueWith(_ =>
        {
            if (!_open || _link is null) return;
            Dispatcher.BeginInvoke(() =>
            {
                if (_link != _link || !_open) return;
                SetHub(title, subtitle + "\n\n" + string.Join("\n", analysis.Warnings.Select(w => w.Detail)), animate: true);
            });
        });
    }

    private static int GetWarningSeverity(FakeLinkDetector.WarningType type) => type switch
    {
        FakeLinkDetector.WarningType.MismatchedDomain => 10,
        FakeLinkDetector.WarningType.BrandImpersonation => 9,
        FakeLinkDetector.WarningType.HomographAttack => 8,
        FakeLinkDetector.WarningType.UrlShortener => 7,
        FakeLinkDetector.WarningType.PunycodeDomain => 7,
        FakeLinkDetector.WarningType.IpAddress => 6,
        FakeLinkDetector.WarningType.SuspiciousTld => 5,
        FakeLinkDetector.WarningType.ExcessiveSubdomains => 4,
        FakeLinkDetector.WarningType.SuspiciousPath => 3,
        FakeLinkDetector.WarningType.MissingHttps => 2,
        _ => 1
    };

    private static string LinkTitle(Shell.LinkDownload link)
    {
        if (link.Done.IsFaulted || link.Done.IsCanceled)
            return L.T("Couldn't download");
        if (link.Received == 0 && !link.IsDone)
            return L.T("Reading the link…");
        return link.Total is > 0 and var total && !link.IsDone
            ? L.F("{0}%", (int)(100.0 * link.Received / total))
            : Util.Sizes.Format(link.Received);
    }

    private static string LinkSubtitle(Shell.LinkDownload link)
    {
        var name = link.Name ?? link.Address.Host;
        if (link.Received == 0 || link.Done.IsCompleted)
            return name;
        // A long name would leave no room for the rest: the petals say what kind of file it is anyway.
        var speed = L.F("{0}/s", Util.Sizes.Format((long)link.Speed));
        return name.Length <= 18 ? speed + " · " + name : speed;
    }

    /// <summary>How far a link's file has come, how fast and what it is, on the card that waits for it.</summary>
    internal static string LinkStatus(Shell.LinkDownload link)
    {
        var parts = new List<string> { LinkTitle(link) };
        if (link.Received > 0 && !link.Done.IsCompleted)
            parts.Add(L.F("{0}/s", Util.Sizes.Format((long)link.Speed)));
        if (link.Name is { } name)
            parts.Add(name);
        return string.Join(" · ", parts);
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
        var at = e.GetPosition(_stage);
        // A bubble of an open group takes the drop before the ring.
        var bubble = _open && files is not null && _orbit is { } orbit && OrbitBubbleAt(new Vector(at.X - Centre, at.Y - Centre)) is { } k
            ? orbit.Bubbles[k].Item : null;
        int? index = bubble is null && _open && files is not null ? HitTest(e) : null;
        var point = CursorPoint(e);
        _dragHelper.Drop(e.Data, point, index is null && (bubble is null || bubble.IsGroup) ? DragDropEffects.None : DragDropEffects.Copy);

        if (bubble is null && (index is not { } i || content is null || files is null || i >= content.Items.Count || content.Items[i].IsGroup)
            || bubble is { IsGroup: true } || files is null)
        {
            // Dropped on the hub, a gap, the margin or a group that hadn't opened: do nothing, never move the files.
            e.Effects = DragDropEffects.None;
            CloseWheel();
            return;
        }

        // Copy (never Move) so Explorer leaves the originals alone.
        e.Effects = DragDropEffects.Copy;
        var item = bubble ?? content!.Items[index!.Value];
        var page = PageKey;
        if (item.Unavailable is not null && item.Blocked)
        {
            var where = new ScreenPoint(point.X, point.Y);
            CloseWheel();
            Dispatcher.BeginInvoke(DispatcherPriority.Background, () => UnavailableChosen?.Invoke(item, where));
            return;
        }
        Sound.Drop();
        Chose?.Invoke(page, item);
        // Read files are the petal's now: they move to Downloads instead of being forgotten with the wheel.
        var staged = _staged;
        _staged = null;
        var link = _link;
        _link = null;
        var chain = _chain.ToList();

        // Let the petal stay lit for a couple of frames, then vanish instantly. Only then run the
        // action: opening a tool window can keep the UI thread busy, and the wheel must not linger.
        var drop = new ScreenPoint(point.X, point.Y);
        var close = new DispatcherTimer(DispatcherPriority.Send) { Interval = TimeSpan.FromMilliseconds(50) };
        close.Tick += (_, _) =>
        {
            close.Stop();
            CloseWheel();
            if (link is not null)
            {
                Dispatcher.BeginInvoke(DispatcherPriority.Background, () => DropLink(link, item, chain, drop));
                return;
            }
            Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
            {
                var kept = files;
                if (staged is not null)
                {
                    try
                    {
                        kept = Shell.VirtualDrop.Keep(staged);
                    }
                    catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
                    {
                        Log.Error(ex, "Keeping the files of a drag");
                        return;
                    }
                }
                if (kept.Count == 0)
                    return;
                if (chain.Count > 0)
                    ChainDropped?.Invoke(chain, item, kept, drop);
                else
                    ItemDropped?.Invoke(item, kept, drop);
            });
        };
        close.Start();
    }

    /// <summary>
    /// A link dropped on a petal: once its file is whole (a card shows it coming down when it isn't yet),
    /// it moves to Downloads and the petal works on it there.
    /// </summary>
    private async void DropLink(Shell.LinkDownload link, WheelItem item, List<WheelItem> chain, ScreenPoint drop)
    {
        try
        {
            if (!link.Done.IsCompleted)
            {
                var card = Hud.HudCard.ShowAt(drop, L.T("Downloading…"), LinkStatus(link));
                card.CancelRequested += link.Cancel;
                void Update()
                {
                    card.SetText(L.T("Downloading…"), LinkStatus(link));
                    card.SetProgress(link.Total is > 0 and var total ? (double)link.Received / total : null);
                }
                link.Changed += Update;
                try
                {
                    await link.Done;
                    card.Dismiss();
                }
                catch (OperationCanceledException)
                {
                    card.Fail(L.T("Cancelled"), L.T("Nothing was saved"));
                    return;
                }
                catch (Shell.LinkDownloadException ex)
                {
                    card.Fail(L.T("Couldn't download"), LinkProblem(ex));
                    return;
                }
                finally
                {
                    link.Changed -= Update;
                }
            }
            else if (!link.Done.IsCompletedSuccessfully)
            {
                var why = link.Done.Exception?.InnerException as Shell.LinkDownloadException;
                Hud.HudCard.ShowAt(drop, L.T("Couldn't download"), "").Fail(L.T("Couldn't download"), why is null ? L.T("Nothing was saved") : LinkProblem(why));
                return;
            }
            var kept = Shell.VirtualDrop.Keep([await link.Done]);
            if (kept.Count == 0)
                return;
            if (chain.Count > 0)
                ChainDropped?.Invoke(chain, item, kept, drop);
            else
                ItemDropped?.Invoke(item, kept, drop);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            Log.Error(ex, "Keeping a dragged link's file");
        }
        finally
        {
            Shell.VirtualDrop.ForgetFolder(link.Folder);
        }
    }

    /// <summary>Why a link's file couldn't be downloaded, in words.</summary>
    internal static string LinkProblem(Shell.LinkDownloadException ex) => ex.Problem switch
    {
        Shell.LinkDownloadProblem.Refused when ex.Status is 404 or 410 => L.F("Nothing is at this address any more ({0}).", ex.Status),
        Shell.LinkDownloadProblem.Refused => L.F("The site refused the download ({0}). Save it from the browser, then drag the file.", ex.Status),
        Shell.LinkDownloadProblem.TooLarge => L.F("It is larger than {0}. Save it from the browser, then drag the file.", Util.Sizes.Format(Shell.LinkDownload.MostBytes)),
        Shell.LinkDownloadProblem.Stalled => L.T("Nothing came for 30 seconds."),
        Shell.LinkDownloadProblem.Cut => L.T("The download broke off before the end."),
        _ => L.T("Couldn't reach the site. Check the connection."),
    };

    private int? HitTest(DragEventArgs e)
    {
        if (_content is null || _content.IsEmpty)
            return null;
        return HitTestAt(e.GetPosition(_stage));
    }

    private void UpdatePointer(DragEventArgs e)
    {
        if (_orbit is { } o)
        {
            var at = e.GetPosition(_stage);
            var v = new Vector(at.X - Centre, at.Y - Centre);
            if (OrbitBubbleAt(v) is { } k)
            {
                var item = o.Bubbles[k].Item;
                e.Effects = item.IsGroup || item.Unavailable is not null && item.Blocked ? DragDropEffects.None : DragDropEffects.Copy;
                SetOrbitHighlight(k, e.Data);
                return;
            }
            if (HitTest(e) == o.Group || InOrbitZone(o, v))
            {
                e.Effects = DragDropEffects.None;
                SetOrbitHighlight(null, e.Data);
                return;
            }
            CloseOrbit();
        }
        var index = HitTest(e);
        bool group = index is { } g && _content is not null && g < _content.Items.Count && _content.Items[g].IsGroup;
        e.Effects = index is null || group ? DragDropEffects.None : DragDropEffects.Copy;
        // Holding over a group opens it; holding over the centre of an open group goes back.
        var p = e.GetPosition(_stage);
        bool onHub = index is null && _groups.Count > 0 && new Vector(p.X - Centre, p.Y - Centre).Length < CancelRadius;
        if (index != _highlight || onHub != _dwellOnHub)
        {
            _dwellTimer.Stop();
            _dwellOnHub = onHub;
            if (group || onHub)
            {
                _dwellTimer.Interval = TimeSpan.FromMilliseconds(group ? 340 : 420);
                _dwellTimer.Start();
            }
        }
        SetHighlight(index, e.Data);
    }

    private void SetHighlight(int? index, IDataObject? data)
    {
        if (index == _highlight)
            return;
        int? previous = _highlight;
        _highlight = index;
        var p = _palette;
        for (int i = 0; i < _visuals.Count; i++)
            _visuals[i].Paint(p, i == index);
        if (previous is { } was && was < _visuals.Count)
            _visuals[was].Grow(false, _options);
        if (index is { } now && now < _visuals.Count)
            _visuals[now].Grow(true, _options);

        CancelPreview();
        if (index is { } i2 && _content is not null && i2 < _content.Items.Count)
        {
            var item = _content.Items[i2];
            _lastHubLabel = HubTitle(item);
            SetHub(_lastHubLabel, _chain.Count > 0 && !item.IsGroup && item.Unavailable is null ? L.F("Step {0}", _chain.Count + 1) : Subtitle(item), animate: IsVisible, about: item);
            if (_open && _chain.Count == 0 && _pages is null && _files is not null && (_link is null || _link.IsDone) && PreviewProvider is not null && !item.IsGroup && item.Unavailable is null && PresentationSource.FromVisual(_root) is not null)
                _previewTimer.Start();
            Sound.Tick();
            if (data is not null)
                DragDropHelper.SetDescription(data, item.IsGroup || item.Unavailable is not null && item.Blocked ? null : Description(item), null);
        }
        else
        {
            if (data is not null)
                DragDropHelper.SetDescription(data, null);
            // Back in the middle: the centre says what the wheel is about again.
            if (previous is not null && index is null && _content is not null)
                ShowDefaultHub(animate: IsVisible);
        }
    }

    private void CancelPreview()
    {
        _previewTimer.Stop();
        _previewCancel?.Cancel();
        _previewCancel = null;
        if (_pages is null && _hubPicture.Visibility == Visibility.Visible)
            _hubPicture.Visibility = Visibility.Collapsed;
    }

    /// <summary>The pointer has rested on a petal: work out what it makes, and show it if the pointer is still there.</summary>
    private async void OnPreviewTick(object? sender, EventArgs e)
    {
        _previewTimer.Stop();
        if (_highlight is not { } index || _content is not { } content || index >= content.Items.Count || _files is not { } files || PreviewProvider is not { } provider)
            return;
        var item = content.Items[index];
        var cancel = new CancellationTokenSource();
        _previewCancel = cancel;
        PetalPreview? preview;
        try
        {
            preview = await Task.Run(() => provider(item, files, cancel.Token), cancel.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Previewing a petal");
            return;
        }
        if (cancel.IsCancellationRequested || preview is null || !_open || _highlight != index || !ReferenceEquals(_content, content))
            return;
        SetHub(_lastHubLabel, preview.Text, animate: IsVisible, about: item);
        if (preview.Picture is { } picture)
        {
            _hubPicture.Fill = new ImageBrush(picture) { Stretch = Stretch.Uniform };
            _hubPicture.Visibility = Visibility.Visible;
        }
    }

    /// <summary>The centre's name for a petal: a format's or favourite's own label, otherwise its name.</summary>
    private static string HubTitle(WheelItem item) =>
        item.Payload is Petal.Core.Formats.FileFormat or Petal.Core.Jobs.Favourite or Petal.Recipes.Recipe ? item.Label : L.T(item.Name);

    /// <summary>The centre's second line for a petal: why it can't run, its state, what it does, or what kind it is.</summary>
    private string Subtitle(WheelItem item)
    {
        if (item.Unavailable is { } why)
            return L.T(why);
        if (item.IsGroup)
            return _interactive ? L.P(item.Children!.Count, "{0} more", "{0} more") : L.T("Hold here to open");
        if (item.State is { } on)
            return L.T(on ? "On" : "Off");
        if (item.Note is { } note)
            return L.T(note);
        return item.Payload switch
        {
            Petal.Core.Formats.FileFormat => L.T("Convert"),
            // A favourite's petal shows its name; the centre adds what it does ("WEBP · 1600 px").
            Petal.Core.Jobs.Favourite => item.Name,
            Petal.Recipes.Recipe => L.T("Recipe"),
            Petal.Core.Tools.ToolInfo => L.T("Tool"),
            _ => L.T("Action"),
        };
    }

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
    internal BitmapSource Snapshot(IReadOnlyList<string> files, WheelMode mode, int? highlight, BitmapSource background, bool hintBelow = false,
        IReadOnlyList<RingPage>? pages = null, int page = 0, bool keyboard = false, int? openGroup = null, double orbit = 1, string? calc = null,
        Shell.LinkDownload? link = null)
    {
        _visible = new Rect(-Centre, -Centre, Side, Side);
        _link = link;
        _options = OptionsProvider();
        _palette = PaletteFor(_options);
        _files = files;
        _mode = mode;
        _pages = pages;
        _page = page;
        _interactive = keyboard;
        _groups.Clear();
        _open = true;
        var crop = new CroppedBitmap(background, new Int32Rect(
            Math.Max(0, (background.PixelWidth - (int)Side) / 2),
            Math.Max(0, (background.PixelHeight - (int)Side) / 2),
            Math.Min((int)Side, background.PixelWidth),
            Math.Min((int)Side, background.PixelHeight)));
        ApplyGlass(_options.Style == WheelStyle.Solid ? null : GlassCapture.Blur(crop));
        double gap = 12 + (keyboard && _options.ShowNumbers ? 10 : 0);
        Canvas.SetTop(_hint, hintBelow ? Centre + WheelGeometry.OuterRadius + gap : Centre - WheelGeometry.OuterRadius - gap - 30);
        _root.Visibility = Visibility.Visible;
        Render(animate: false);
        if (openGroup is { } g && _content is not null && g < _content.Items.Count && CanOrbit(_content.Items[g]))
        {
            OpenOrbit(g, keyboard: false, animate: false);
            if (highlight is { } h)
                SetOrbitHighlight(h, null);
            SettleOrbit(orbit);
        }
        else
        {
            if (openGroup is { } g2 && _content is not null && g2 < _content.Items.Count && _content.Items[g2].IsGroup)
                OpenGroup(_content.Items[g2]);
            SetHighlight(highlight, null);
        }
        if (calc is not null)
        {
            _calc = calc;
            ShowCalc();
        }
        _interactive = false;

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
        _pages = null;
        _calc = null;
        _groups.Clear();
        ResetOrbit();
        foreach (var visual in _visuals)
            visual.Recede(false, animate: false);
        return bitmap;
    }

    // ---------- rendering ----------

    private void Render(bool animate)
    {
        if (_files is null)
            return;
        if (_pages is { } pages)
            _content = pages[_page].Content();
        else if (ContentProvider is not null)
            _content = ContentProvider(_mode, ContentFiles(_files));
        else
            return;
        _groups.Clear();
        Draw(animate);
    }

    /// <summary>Draws <see cref="_content"/>: the petals, their labels, the centre and the hint above.</summary>
    private void Draw(bool animate)
    {
        if (_content is null)
            return;
        var p = _palette;
        _highlight = null;
        ResetOrbit();
        _petals.Children.Clear();
        _labels.Children.Clear();
        _visuals.Clear();
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
            bool tools = _mode != WheelMode.Conversions || _pages is not null || _groups.Count > 0;
            for (int i = 0; i < n; i++)
                AddPetal(_content.Items[i], i, n, tools || _content.Items[i].Payload is not Petal.Core.Formats.FileFormat, p);
        }

        ShowDefaultHub(animate: false);
        ApplyPaletteToChrome(p);

        if (animate && _options.Motion != WheelMotion.None && SystemParameters.ClientAreaAnimation)
            Animate(_options.Motion);
        else
            Settle();
    }

    /// <summary>The wheel without animation: everything where it ends up.</summary>
    private void Settle()
    {
        _stage.BeginAnimation(OpacityProperty, null);
        _stage.Opacity = 1;
        _stageScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        _stageScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        _stageScale.ScaleX = _stageScale.ScaleY = 1;
        _labels.BeginAnimation(OpacityProperty, null);
        _labels.Opacity = 1;
        _hint.BeginAnimation(OpacityProperty, null);
        _hint.Opacity = 1;
        _hint.Visibility = ShowHints ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// How the wheel arrives: blooming out of the pointer (the usual), popping with a little overshoot,
    /// fading in where it is, or unfolding petal by petal clockwise.
    /// </summary>
    private void Animate(WheelMotion motion)
    {
        Settle();
        switch (motion)
        {
            case WheelMotion.Pop:
                _stage.BeginAnimation(OpacityProperty, Motion.To(1, 80, Motion.Standard, from: 0));
                _stageScale.BeginAnimation(ScaleTransform.ScaleXProperty, Motion.To(1, 320, Motion.Pop, from: 0.6));
                _stageScale.BeginAnimation(ScaleTransform.ScaleYProperty, Motion.To(1, 320, Motion.Pop, from: 0.6));
                _labels.BeginAnimation(OpacityProperty, Motion.To(1, 90, Motion.Standard, from: 0, delayMs: 70));
                break;
            case WheelMotion.Fade:
                _stage.BeginAnimation(OpacityProperty, Motion.To(1, 140, Motion.Standard, from: 0));
                break;
            case WheelMotion.Unfold:
                _stage.BeginAnimation(OpacityProperty, Motion.To(1, 60, Motion.Standard, from: 0));
                _stageScale.BeginAnimation(ScaleTransform.ScaleXProperty, Motion.To(1, 200, Motion.Bloom, from: 0.85));
                _stageScale.BeginAnimation(ScaleTransform.ScaleYProperty, Motion.To(1, 200, Motion.Bloom, from: 0.85));
                for (int i = 0; i < _visuals.Count; i++)
                    _visuals[i].Unfold(i * Math.Min(22, 220.0 / _visuals.Count));
                break;
            default:
                _stage.BeginAnimation(OpacityProperty, Motion.To(1, 90, Motion.Standard, from: 0));
                _stageScale.BeginAnimation(ScaleTransform.ScaleXProperty, Motion.To(1, 190, Motion.Bloom, from: 0.38));
                _stageScale.BeginAnimation(ScaleTransform.ScaleYProperty, Motion.To(1, 190, Motion.Bloom, from: 0.38));
                _labels.BeginAnimation(OpacityProperty, Motion.To(1, 90, Motion.Standard, from: 0, delayMs: 55));
                break;
        }
        _hint.BeginAnimation(OpacityProperty, Motion.To(1, 160, Motion.Standard, from: 0, delayMs: 120));
    }

    /// <summary>What the centre says with no petal under the pointer: the files, the open group, or the wheel's own news.</summary>
    private void ShowDefaultHub(bool animate)
    {
        _hubPicture.Visibility = Visibility.Collapsed;
        _hubPictureTurn.BeginAnimation(RotateTransform.AngleProperty, null);
        if (_groups.Count > 0)
        {
            SetHub(_groups.Peek().Title, L.T(_interactive ? "Centre or Backspace: back" : "Hold in the centre to go back"), animate);
            return;
        }
        if (_pages is { } pages)
        {
            var hub = pages[_page].Hub?.Invoke();
            SetHub(hub?.Title ?? L.T(pages[_page].Title), hub?.Subtitle ?? "", animate);
            if (hub?.Picture is { } picture)
            {
                _hubPicture.Fill = new ImageBrush(picture) { Stretch = Stretch.UniformToFill };
                _hubPicture.Visibility = Visibility.Visible;
                if (hub.Spin && SystemParameters.ClientAreaAnimation)
                    _hubPictureTurn.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromSeconds(9)) { RepeatBehavior = RepeatBehavior.Forever });
            }
            return;
        }
        if (_files is null) return;
        if (_chain.Count > 0)
        {
            SetHub(ChainTitle(), ChainHint(), animate);
            return;
        }
        if (_link is { } link)
        {
            // As for files the centre gives the size: here how much has come, then how fast and what.
            SetHub(LinkTitle(link), LinkSubtitle(link), animate);
            return;
        }
        SetHub(HubTextProvider?.Invoke(_files) ?? "", HubSubtitleProvider?.Invoke(_files) ?? (_files.Count == 1 ? "" : L.P(_files.Count, "{0} item", "{0} items")), animate);
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
    /// One petal and its label. The label is laid out by hand: <see cref="PetalLayout"/> picks its lines and
    /// size and keeps it inside the petal, then the ink of the icon and of every line is centred on one point,
    /// so what the eye sees is centred rather than text boxes and trailing spaces. The petal and its label
    /// share a transform, so the petal under the pointer can grow towards it with its label.
    /// </summary>
    private void AddPetal(WheelItem item, int index, int count, bool withIcon, Palette p)
    {
        var visual = new PetalVisual(item, index, count);
        var path = new Path
        {
            Data = WheelGeometry.Petal(index, count),
            StrokeThickness = 0.75,
            IsHitTestVisible = false,
        };
        visual.Path = path;
        visual.PetalLayer.Children.Add(path);
        _petals.Children.Add(visual.PetalLayer);
        _labels.Children.Add(visual.LabelLayer);

        bool hasIcon = withIcon && (item.Icon is not null || item.Picture is not null);
        // Icons only, when names are hidden: what has an icon shows it larger and alone.
        bool iconOnly = hasIcon && !_options.ShowNames;
        double size = withIcon ? (L.IsRightToLeft ? (count > 8 ? 10 : 10.5) : count > 8 ? 8 : 8.5) : (count > 9 ? 11.5 : count > 7 ? 12.5 : 13.5);
        // Baseline to baseline; under an icon the lines sit close, as a caption.
        double step = withIcon ? (L.IsRightToLeft ? 13 : 9.5) : size * 1.3;
        double iconSize = iconOnly ? (count > 8 ? 22 : 26) : count > 8 ? 16 : 18;
        var brush = Palette.Brush(p.Text);
        IconInk? iconInk = null;
        if (hasIcon && item.Picture is { } picture)
        {
            var image = new Image { Source = picture, Width = iconSize, Height = iconSize, IsHitTestVisible = false };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            visual.Icon = image;
            iconInk = new IconInk(new Rect(0, 0, iconSize, iconSize), [new Rect(0, 0, iconSize, iconSize)]);
        }
        else if (hasIcon && item.Icon is { } iconKey)
        {
            visual.Icon = Icons.Create(iconKey, iconSize, brush, 2);
            var outline = Icons.InkGeometry(iconKey, iconSize, 2);
            iconInk = new IconInk(outline.Bounds, InkShape.Strips(outline, 3));
        }

        var font = _labelFont!;
        var label = PetalLayout.Fit(iconOnly ? "" : L.T(item.Label), font, size, size - 1.5, step, iconInk, iconSize, index, count);
        visual.Lines = label.Lines.Where(t => t.Length > 0).Select(t => font.Create(t, label.Size, brush)).ToList();
        if (visual.Icon is not null && iconInk is not null)
        {
            Canvas.SetLeft(visual.Icon, label.Centre.X - iconInk.Box.X - iconInk.Box.Width / 2);
            Canvas.SetTop(visual.Icon, label.Centre.Y + (iconOnly ? -iconInk.Box.Y - iconInk.Box.Height / 2 : label.IconTop));
            visual.LabelLayer.Children.Add(visual.Icon);
        }
        for (int i = 0; i < visual.Lines.Count; i++)
        {
            visual.Lines[i].Place(label.Centre.X, label.Centre.Y + label.Baselines[i]);
            visual.LabelLayer.Children.Add(visual.Lines[i]);
        }
        visual.AddMarks(_interactive && index < 9 && _options.ShowNumbers, _labelFont);
        visual.Paint(p, false);
        _visuals.Add(visual);
        _petalLayouts.Add(label);
    }

    /// <summary>
    /// Sets the centre's name and what it is. The lines and their outline come from <see cref="HubLayout"/>;
    /// while the wheel is on screen the outline glides between a pill and a disc as names change.
    /// </summary>
    private void SetHub(string title, string subtitle, bool animate, WheelItem? about = null)
    {
        _hubText.Children.Clear();
        _hubLines.Clear();
        _hubShape.Visibility = string.IsNullOrEmpty(title) ? Visibility.Hidden : Visibility.Visible;
        if (string.IsNullOrEmpty(title))
        {
            ShowInfoMark(null, 0);
            return;
        }
        if (_hubTitleFont is null)
            CreateFonts();

        var p = _palette;
        var fit = HubLayout.Fit(title, subtitle, _hubTitleFont!, _hubSubtitleFont!, L.IsRightToLeft);
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
        ShowInfoMark(about, fit.HalfHeight);
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

    // ---------- the mark under a petal's name ----------

    /// <summary>A small round mark with "!": the petal's explanation, from the guide.</summary>
    private void BuildInfoMark()
    {
        _infoMark.IsHitTestVisible = false;
        _infoMark.Visibility = Visibility.Collapsed;
        _infoMark.RenderTransform = _infoScale;
        _infoMark.Children.Add(new Ellipse { Width = 2 * InfoRadius, Height = 2 * InfoRadius });
        var bang = new Path
        {
            Data = new LineGeometry(new Point(InfoRadius, InfoRadius - 4.6), new Point(InfoRadius, InfoRadius + 1.2)),
            StrokeThickness = 2,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
        };
        _infoMark.Children.Add(bang);
        var dot = new Ellipse { Width = 2.4, Height = 2.4 };
        Canvas.SetLeft(dot, InfoRadius - 1.2);
        Canvas.SetTop(dot, InfoRadius + 3.1);
        _infoMark.Children.Add(dot);
        System.Windows.Automation.AutomationProperties.SetName(_infoMark, L.T("What it does (F1)"));
    }

    /// <summary>The mark for the petal the centre now names (when the guide has it and the wheel is clicked, not dragged), at the foot of the name.</summary>
    private void ShowInfoMark(WheelItem? about, double halfHeight)
    {
        var key = about is not null && _interactive ? Guide.GuideBook.KeyFor(about) : null;
        _hubAbout = key is null ? null : about;
        if (key is null)
        {
            _infoMark.Visibility = Visibility.Collapsed;
            return;
        }
        var p = _palette;
        ((Ellipse)_infoMark.Children[0]).Fill = Palette.Brush(about!.Tint ?? p.Accent);
        var mark = Palette.Brush(Readable(about.Tint ?? p.Accent, p));
        ((Path)_infoMark.Children[1]).Stroke = mark;
        ((Ellipse)_infoMark.Children[2]).Fill = mark;
        // On the centre's rim, under the name and clear of the petals; a short name's pill lets it sit just below.
        double y = Math.Min(Math.Max(halfHeight + InfoRadius - 2, 24), WheelGeometry.HubRadius - 2.5);
        _infoAt = new Point(0, y);
        Canvas.SetLeft(_infoMark, -InfoRadius);
        Canvas.SetTop(_infoMark, y - InfoRadius);
        _infoScale.CenterX = _infoScale.CenterY = InfoRadius;
        _infoMark.Visibility = Visibility.Visible;
        if (Guide.GuideCard.IsOpen)
            Guide.GuideCard.Follow(key, HubTitle(about), OwnText(about));
    }

    private bool OverInfoMark(Vector v) =>
        _infoMark.Visibility == Visibility.Visible && (v - new Vector(_infoAt.X, _infoAt.Y)).Length <= InfoRadius + 5;

    private void HoverInfoMark(bool over)
    {
        double to = over ? 1.2 : 1;
        if (Math.Abs(_infoScale.ScaleX - to) > 0.01)
        {
            _infoScale.BeginAnimation(ScaleTransform.ScaleXProperty, Motion.To(to, 120));
            _infoScale.BeginAnimation(ScaleTransform.ScaleYProperty, Motion.To(to, 120));
        }
        Cursor = over ? Cursors.Hand : null;
    }

    /// <summary>The explanation of the petal the centre names, beside the wheel; the wheel keeps the keyboard.</summary>
    private void OpenGuideCard()
    {
        if (_hubAbout is not { } item || Guide.GuideBook.KeyFor(item) is not { } key)
            return;
        GetWindowRect(_hwnd, out var r);
        Guide.GuideCard.Show(key, HubTitle(item), OwnText(item), new Int32Rect(r.Left, r.Top, r.Width, r.Height));
        Sound.Tick();
    }

    /// <summary>What this one petal does, over the guide's general words: a format's conversion, a favourite's or recipe's steps.</summary>
    private static string? OwnText(WheelItem item) => item.Payload switch
    {
        Petal.Core.Formats.FileFormat => item.Note is { Length: > 0 } note ? item.Name + " · " + L.T(note) : item.Name,
        Petal.Core.Jobs.Favourite or Petal.Recipes.Recipe or Petal.Recipes.CustomPetal => item.Name,
        _ => null,
    };

    private void UpdateHint()
    {
        var p = _palette;
        var row = new StackPanel { Orientation = Orientation.Horizontal, FlowDirection = L.Direction };
        if (_pages is { } pages)
        {
            // Wheels without files: their names, the one shown lit; Tab and the mouse wheel move along.
            int first = pages.Count <= 5 ? 0 : Math.Clamp(_page - 2, 0, pages.Count - 5);
            for (int i = first; i < Math.Min(pages.Count, first + 5); i++)
            {
                bool active = i == _page;
                row.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(7, 1, 7, 2),
                    Margin = new Thickness(0, 0, 4, 0),
                    Background = active ? Palette.Brush(p.Accent) : Brushes.Transparent,
                    Child = new TextBlock
                    {
                        Text = L.T(pages[i].Title),
                        FontFamily = UiFonts.Text,
                        FontSize = 11,
                        FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal,
                        Foreground = active ? Palette.Brush(p.TextOnAccent) : Palette.Brush(p.TextSecondary),
                    },
                    VerticalAlignment = VerticalAlignment.Center,
                });
            }
        }
        else
        {
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
        }
        if (row.Children.Count > 0 && row.Children[^1] is FrameworkElement last)
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

    private static Brush AccentFill(Palette p) => AccentFill(p, p.Accent);

    private static Brush AccentFill(Palette p, Color accent)
    {
        var b = new RadialGradientBrush
        {
            MappingMode = BrushMappingMode.Absolute,
            Center = new Point(0, 0),
            GradientOrigin = new Point(0, 0),
            RadiusX = WheelGeometry.PetalOuter,
            RadiusY = WheelGeometry.PetalOuter,
            GradientStops = [new GradientStop(Mix(accent, Colors.White, 0.14), 0.4), new GradientStop(Mix(accent, Colors.Black, 0.1), 1)],
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

        // A picture for the centre, such as the album of the song playing; it turns while it plays.
        double pictureR = hubR - 2;
        _hubPicture.Width = _hubPicture.Height = pictureR * 2;
        Canvas.SetLeft(_hubPicture, -pictureR);
        Canvas.SetTop(_hubPicture, -pictureR);
        _hubPicture.RenderTransformOrigin = new Point(0.5, 0.5);
        _hubPicture.RenderTransform = _hubPictureTurn;
        _hubPicture.Opacity = 0.85;
        _hubPicture.IsHitTestVisible = false;
        _hubPicture.Visibility = Visibility.Collapsed;
        _ring.Children.Add(_hubPicture);

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
        // A group's petals, out on their orbit around the ring.
        _ring.Children.Add(_orbitLayer);

        // The centre's outline is a rounded rectangle so it can glide between a pill and a disc.
        _hubShape.Data = _hubOutline;
        _hubShape.StrokeThickness = 0.75;
        _hubShape.IsHitTestVisible = false;
        _hubText.IsHitTestVisible = false;
        _ring.Children.Add(_hubShape);
        _ring.Children.Add(_hubText);

        BuildInfoMark();
        _ring.Children.Add(_infoMark);

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

    /// <summary>
    /// A petal on screen: its shape on one layer and its label on another, both moved by one transform so
    /// the petal under the pointer can grow out towards it, label and all.
    /// </summary>
    private sealed class PetalVisual
    {
        private readonly ScaleTransform _scale;
        private readonly TranslateTransform _shift = new();
        private readonly Vector _axis;
        private readonly int _index;
        private readonly int _count;
        private Path? _edge;

        public PetalVisual(WheelItem item, int index, int count)
        {
            Item = item;
            _index = index;
            _count = count;
            var angle = WheelGeometry.Angle(index, count);
            var mid = WheelGeometry.At((WheelGeometry.PetalInner + WheelGeometry.PetalOuter) / 2, angle);
            _axis = count == 1 ? new Vector(0, 0) : new Vector(mid.X, mid.Y) / ((WheelGeometry.PetalInner + WheelGeometry.PetalOuter) / 2);
            _scale = new ScaleTransform(1, 1, count == 1 ? 0 : mid.X, count == 1 ? 0 : mid.Y);
            var transform = new TransformGroup { Children = { _scale, _shift } };
            PetalLayer.RenderTransform = transform;
            LabelLayer.RenderTransform = transform;
        }

        public WheelItem Item { get; }
        public Canvas PetalLayer { get; } = new() { IsHitTestVisible = false };
        public Canvas LabelLayer { get; } = new() { IsHitTestVisible = false };
        public Path Path { get; set; } = null!;
        public FrameworkElement? Icon { get; set; }
        public List<InkText> Lines { get; set; } = [];
        private InkText? _number;
        private Path? _sparkle;

        /// <summary>
        /// A switch that is on gets a lit inner edge; a petal that can't run is drawn dashed and dim. With
        /// <paramref name="number"/>, its key (1 to 9) sits just outside the ring.
        /// </summary>
        public void AddMarks(bool number, InkFont? font = null)
        {
            if (Item.State is not null && _count > 1)
            {
                double half = Math.PI / _count;
                double angle = WheelGeometry.Angle(_index, _count);
                double r = WheelGeometry.PetalInner + 3.5;
                double inset = Math.Min(half * 0.55, half - 0.12);
                var g = new StreamGeometry();
                using (var ctx = g.Open())
                {
                    ctx.BeginFigure(WheelGeometry.At(r, angle - inset), false, false);
                    ctx.ArcTo(WheelGeometry.At(r, angle + inset), new Size(r, r), 0, false, SweepDirection.Counterclockwise, true, false);
                }
                g.Freeze();
                _edge = new Path { Data = g, StrokeThickness = 2.5, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, IsHitTestVisible = false };
                PetalLayer.Children.Add(_edge);
            }
            if (Item.Suggested && _count > 1)
            {
                // Your usual for this kind of file: a small four-point star at the petal's outer corner.
                double angle = WheelGeometry.Angle(_index, _count) + Math.PI / _count * 0.62;
                var c = WheelGeometry.At(WheelGeometry.PetalOuter - 10, angle);
                const double r = 4.6, k = 1.1;
                var g = Geometry.Parse(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                    $"M {c.X},{c.Y - r} Q {c.X + k},{c.Y - k} {c.X + r},{c.Y} Q {c.X + k},{c.Y + k} {c.X},{c.Y + r} Q {c.X - k},{c.Y + k} {c.X - r},{c.Y} Q {c.X - k},{c.Y - k} {c.X},{c.Y - r} Z"));
                g.Freeze();
                _sparkle = new Path { Data = g, IsHitTestVisible = false };
                PetalLayer.Children.Add(_sparkle);
            }
            if (number && font is not null)
            {
                var at = WheelGeometry.At(WheelGeometry.OuterRadius + 9, WheelGeometry.Angle(_index, _count));
                _number = font.Create(((char)('1' + _index)).ToString(), 8.5, Brushes.Gray);
                _number.Place(at.X, at.Y + 3);
                LabelLayer.Children.Add(_number);
            }
        }

        public void Paint(Palette p, bool on)
        {
            var accent = Item.Tint ?? p.Accent;
            bool dim = Item.Unavailable is not null;
            Path.Fill = on ? AccentFill(p, accent) : Item.Tint is { } tint ? TintFill(p, tint) : PetalFill(p);
            Path.Stroke = on ? Palette.Brush(Color.FromArgb(0x50, accent.R, accent.G, accent.B)) : Palette.Brush(p.PetalStroke);
            Path.StrokeDashArray = dim && !on ? DashedStroke : null;
            Path.StrokeThickness = dim && !on ? 1 : 0.75;
            var fg = on ? Palette.Brush(Readable(accent, p)) : Palette.Brush(p.Text);
            foreach (var line in Lines)
                line.Foreground = fg;
            if (Icon is Path icon)
                icon.Stroke = on ? fg : Item.Tint is { } t ? Palette.Brush(Legible(t, p)) : fg;
            _labelOpacity = dim && !on ? 0.45 : 1;
            _petalOpacity = dim && !on ? 0.7 : 1;
            LabelLayer.Opacity = _labelOpacity * (_receded ? 0.38 : 1);
            PetalLayer.Opacity = _petalOpacity * (_receded ? 0.38 : 1);
            if (_edge is not null)
                _edge.Stroke = Item.State == true
                    ? Palette.Brush(on ? Readable(accent, p) : accent)
                    : Palette.Brush(Color.FromArgb(0x40, p.Text.R, p.Text.G, p.Text.B));
            if (_number is not null)
                _number.Foreground = Palette.Brush(on ? p.Text : p.TextSecondary);
            if (_sparkle is not null)
                _sparkle.Fill = Palette.Brush(on ? Readable(accent, p) : accent);
        }

        /// <summary>The petal under the pointer swells a little and moves out from the centre; the others settle back.</summary>
        public void Grow(bool on, WheelOptions options)
        {
            if (!options.Grow || _count == 1)
                return;
            double scale = on ? 1.045 : 1, push = on ? 4 : 0;
            if (!SystemParameters.ClientAreaAnimation)
            {
                _scale.ScaleX = _scale.ScaleY = scale;
                (_shift.X, _shift.Y) = (_axis.X * push, _axis.Y * push);
                return;
            }
            var ease = on ? Motion.Pop : Motion.Standard;
            double ms = on ? 260 : 140;
            _scale.BeginAnimation(ScaleTransform.ScaleXProperty, Motion.To(scale, ms, ease));
            _scale.BeginAnimation(ScaleTransform.ScaleYProperty, Motion.To(scale, ms, ease));
            _shift.BeginAnimation(TranslateTransform.XProperty, Motion.To(_axis.X * push, ms, ease));
            _shift.BeginAnimation(TranslateTransform.YProperty, Motion.To(_axis.Y * push, ms, ease));
        }

        private bool _receded;
        private double _petalOpacity = 1, _labelOpacity = 1;

        /// <summary>
        /// While a group's petals are out on their orbit, the rest of the ring steps back (its key numbers
        /// give way to the bubbles'); then forward again.
        /// </summary>
        public void Recede(bool back, bool animate)
        {
            if (_receded == back)
                return;
            _receded = back;
            Fade(PetalLayer, _petalOpacity, animate);
            Fade(LabelLayer, _labelOpacity, animate);
            if (_number is not null)
                _number.Visibility = back ? Visibility.Hidden : Visibility.Visible;
        }

        private void Fade(UIElement layer, double rest, bool animate)
        {
            double from = layer.Opacity, to = rest * (_receded ? 0.38 : 1);
            layer.BeginAnimation(UIElement.OpacityProperty, null);
            layer.Opacity = to;
            if (animate && Math.Abs(from - to) > 0.01)
                layer.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(_receded ? 220 : 180))
                {
                    EasingFunction = Motion.Standard,
                    FillBehavior = FillBehavior.Stop,
                });
        }

        /// <summary>For the unfolding entrance: the petal appears in its turn, from a little smaller.</summary>
        public void Unfold(double delayMs)
        {
            PetalLayer.BeginAnimation(UIElement.OpacityProperty, Motion.To(PetalLayer.Opacity, 120, Motion.Standard, from: 0, delayMs: delayMs));
            LabelLayer.BeginAnimation(UIElement.OpacityProperty, Motion.To(LabelLayer.Opacity, 120, Motion.Standard, from: 0, delayMs: delayMs + 30));
            _scale.BeginAnimation(ScaleTransform.ScaleXProperty, Motion.To(1, 220, Motion.Bloom, from: 0.7, delayMs: delayMs));
            _scale.BeginAnimation(ScaleTransform.ScaleYProperty, Motion.To(1, 220, Motion.Bloom, from: 0.7, delayMs: delayMs));
        }

        private static readonly DoubleCollection DashedStroke = Freeze(new DoubleCollection { 3, 2.5 });
    }

    /// <summary>White or near-black, whichever reads on the colour.</summary>
    private static Color Readable(Color background, Palette p)
    {
        double luminance = 0.2126 * background.R + 0.7152 * background.G + 0.0722 * background.B;
        return luminance > 165 ? Color.FromRgb(0x16, 0x16, 0x1A) : p.TextOnAccent;
    }

    /// <summary>A petal's colour on its icon: as it is, a little deeper in the light theme and lighter in the dark one.</summary>
    private static Color Legible(Color tint, Palette p) => p.IsDark ? Mix(tint, Colors.White, 0.25) : Mix(tint, Colors.Black, 0.15);

    private static Brush TintFill(Palette p, Color tint)
    {
        var b = new RadialGradientBrush
        {
            MappingMode = BrushMappingMode.Absolute,
            Center = new Point(0, 0),
            GradientOrigin = new Point(0, -WheelGeometry.PetalOuter * 0.6),
            RadiusX = WheelGeometry.PetalOuter,
            RadiusY = WheelGeometry.PetalOuter,
            GradientStops =
            [
                new GradientStop(Color.FromArgb((byte)Math.Max(p.PetalTop.A, (byte)0x60), tint.R, tint.G, tint.B), 0.35),
                new GradientStop(Color.FromArgb((byte)Math.Max(p.PetalBottom.A, (byte)0x30), tint.R, tint.G, tint.B), 1),
            ],
        };
        b.Freeze();
        return b;
    }

    /// <summary>The theme's colours, or their grey version for a monochrome wheel.</summary>
    private static Palette PaletteFor(WheelOptions options) => options.Style == WheelStyle.Mono ? Theme.Mono(Theme.Current) : Theme.Current;

    private static T Freeze<T>(T f) where T : Freezable
    {
        f.Freeze();
        return f;
    }
}
