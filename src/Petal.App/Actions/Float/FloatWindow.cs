using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Petal.App.Input;
using Petal.App.Localization;
using Petal.App.Settings;
using Petal.App.Shell;
using Petal.App.Visuals;
using static Petal.App.Interop.NativeMethods;

namespace Petal.App.Actions.Float;

/// <summary>
/// A file floating above every window, shown by DropWheel itself (its program isn't opened): a picture, a document's
/// pages, a sheet, a video, a text, at the size it is given. It is resized from any edge or corner or to a ready size,
/// folded to its title, seen through (Alt and the wheel), let clicks through to the window under it, tucked into the
/// screen's edge. It never locks the file, and shows it again when it changes. Several can float at once.
/// </summary>
internal sealed class FloatWindow : Window
{
    private const double Grip = 6;
    private static readonly List<FloatWindow> Floating = [];

    private readonly string _path;
    private readonly Border _card = new();
    private readonly Border _stage = new() { ClipToBounds = true };
    private readonly DockPanel _header = new() { Height = FloatMath.Header, LastChildFill = true };
    private readonly TextBlock _name = new() { FontWeight = FontWeights.SemiBold, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _facts = new() { FontSize = 12, Margin = new Thickness(10, 0, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _zoom = new() { Padding = new Thickness(6, 2, 6, 3), FontSize = 12, MinWidth = 48 };
    private readonly Button _find;
    private readonly DockPanel _findBar = new() { Margin = new Thickness(8, 0, 8, 6), Visibility = Visibility.Collapsed, LastChildFill = true };
    private readonly TextBox _sought = new() { Padding = new Thickness(6, 3, 6, 3), VerticalContentAlignment = VerticalAlignment.Center };
    private readonly TextBlock _foundCount = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 4, 0), FontSize = 12, MinWidth = 50 };
    private readonly TextBlock _level = new() { FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, Padding = new Thickness(10, 3, 10, 4) };
    private readonly Border _levelPill;
    private readonly DispatcherTimer _reload = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private FileSystemWatcher? _watcher;
    private FloatView _view;
    private CancellationTokenSource _finding = new();
    private bool _folded;
    private double _unfoldedHeight;
    private bool _through;
    private double _clearOpacity = 1;
    private Window? _throughPill;
    private bool _tucked;
    private double _restLeft;
    private Rect? _beforeFull;
    private Point _dragStart;
    private bool _dragging;

    private FloatWindow(string path, FloatView view)
    {
        _path = path;
        _view = view;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        MinWidth = 240;
        MinHeight = FloatMath.Header + 2 * FloatMath.Shadow;
        Title = L.F("{0} (floating)", System.IO.Path.GetFileName(path));
        FontFamily = UiFonts.Text;
        L.Apply(this);
        SetResourceReference(ForegroundProperty, "Petal.Text");
        _facts.SetResourceReference(TextBlock.ForegroundProperty, "Petal.TextSecondary");
        _name.SetResourceReference(TextBlock.ForegroundProperty, "Petal.Text");
        _zoom.FlowDirection = FlowDirection.LeftToRight;
        _zoom.SetResourceReference(ForegroundProperty, "Petal.Text");

        _find = HeaderButton("search", "Find (Ctrl+F)", () => OpenFind());
        BuildHeader();
        BuildFindBar();
        _levelPill = new Border { Background = new SolidColorBrush(Color.FromArgb(0xC0, 0x10, 0x10, 0x14)), CornerRadius = new CornerRadius(12), Child = _level, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 10, 0, 0), Opacity = 0, IsHitTestVisible = false };
        var body = new Grid { Children = { _stage, _levelPill } };
        var layout = new DockPanel();
        DockPanel.SetDock(_header, Dock.Top);
        DockPanel.SetDock(_findBar, Dock.Top);
        layout.Children.Add(_header);
        layout.Children.Add(_findBar);
        layout.Children.Add(body);
        _card.Child = layout;
        _card.CornerRadius = new CornerRadius(12);
        _card.BorderThickness = new Thickness(1);
        _card.Margin = new Thickness(FloatMath.Shadow);
        _card.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, ShadowDepth = 4, Direction = 270, Opacity = 0.35 };
        _card.SetResourceReference(Border.BackgroundProperty, "Petal.Popup");
        _card.SetResourceReference(Border.BorderBrushProperty, "Petal.ControlBorder");
        // The content inside the rounded card, its corners cut to the card's.
        _stage.Margin = new Thickness(0, 0, 0, 6);
        Content = _card;
        Show(view);

        _reload.Tick += async (_, _) =>
        {
            _reload.Stop();
            await ReloadAsync();
        };
        PreviewMouseWheel += (_, e) =>
        {
            if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
                return;
            e.Handled = true;
            SeeThrough(Math.Clamp(Opacity + (e.Delta > 0 ? 0.1 : -0.1), 0.2, 1));
        };
        PreviewKeyDown += OnKey;
        Closed += (_, _) =>
        {
            Floating.Remove(this);
            Remember();
            _watcher?.Dispose();
            _reload.Stop();
            _finding.Cancel();
            _view.Dispose();
            _throughPill?.Close();
        };
        LocationChanged += (_, _) => PlacePill();
        SizeChanged += (_, _) => PlacePill();
        Theme.Changed += OnTheme;
        Closed += (_, _) => Theme.Changed -= OnTheme;
        Watch();
    }

    private void OnTheme() => Dispatcher.BeginInvoke(() => _name.Text = _name.Text);

    /// <summary>What the window shows now.</summary>
    internal FloatView View => _view;

    /// <summary>True while clicks pass through to the window under it.</summary>
    internal bool PassesClicks => ((long)GetWindowLongPtr(new WindowInteropHelper(this).Handle, GWL_EXSTYLE) & WS_EX_TRANSPARENT) != 0;

    /// <summary>The small button that stops clicks passing through, while it is shown.</summary>
    internal Window? StopButton => _throughPill;

    /// <summary>The floating windows now.</summary>
    public static int Count => Floating.Count;

    /// <summary>Puts every floating window away.</summary>
    public static void CloseAll()
    {
        foreach (var window in Floating.ToList())
            window.Close();
    }

    /// <summary>
    /// Floats a file near a point, at a size for what it is; or where another window was (<paramref name="like"/>, Quick
    /// Look's), at its size.
    /// </summary>
    public static async Task<FloatWindow> ShowAsync(string path, ScreenPoint at, Rect? like = null)
    {
        var view = await FloatViews.CreateAsync(path, CancellationToken.None);
        var window = new FloatWindow(path, view);
        window.Place(at, like);
        window.Show();
        window.Activate();
        Floating.Add(window);
        return window;
    }

    /// <summary>For design snapshots: a file floating at a size, its view made once the window is shown.</summary>
    internal static FloatWindow ForSnapshot(string path, double width, double height)
    {
        var window = new FloatWindow(path, new OtherView(path)) { Width = width, Height = height };
        window.Loaded += async (_, _) => window.Show(await FloatViews.CreateAsync(path, CancellationToken.None));
        return window;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr((long)GetWindowLongPtr(hwnd, GWL_EXSTYLE) | WS_EX_TOOLWINDOW));
        HwndSource.FromHwnd(hwnd)?.AddHook(Hook);
    }

    /// <summary>Resizing from every edge and corner: Windows is told which one the pointer is on.</summary>
    private IntPtr Hook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != WM_NCHITTEST || _folded || _through)
            return IntPtr.Zero;
        long value = lParam.ToInt64();
        var screen = new Point((short)(value & 0xFFFF), (short)((value >> 16) & 0xFFFF));
        var at = PointFromScreen(screen);
        var edge = FloatMath.EdgeAt(at, new Size(ActualWidth, ActualHeight), FloatMath.Shadow, Grip);
        if (edge == Edge.None)
            return IntPtr.Zero;
        handled = true;
        return new IntPtr(edge switch
        {
            Edge.Left => 10,
            Edge.Right => 11,
            Edge.Top => 12,
            Edge.TopLeft => 13,
            Edge.TopRight => 14,
            Edge.Bottom => 15,
            Edge.BottomLeft => 16,
            _ => 17,
        });
    }

    // ───────────── the header ─────────────

    private void BuildHeader()
    {
        var icon = new Image { Width = 18, Height = 18, Margin = new Thickness(12, 0, 8, 0), Source = ShellIcons.Thumbnail(_path, 32), Cursor = Cursors.Hand, ToolTip = L.T("Drag the file into another program") };
        icon.MouseLeftButtonDown += (_, e) =>
        {
            _dragStart = e.GetPosition(this);
            _dragging = true;
            e.Handled = true;
        };
        icon.MouseMove += (_, e) =>
        {
            if (!_dragging || e.LeftButton != MouseButtonState.Pressed)
                return;
            var delta = e.GetPosition(this) - _dragStart;
            if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance)
                return;
            _dragging = false;
            DragDrop.DoDragDrop(icon, new DataObject(DataFormats.FileDrop, new[] { _path }), DragDropEffects.Copy | DragDropEffects.Link);
        };
        icon.MouseLeftButtonUp += (_, _) => _dragging = false;
        DockPanel.SetDock(icon, Dock.Left);
        _header.Children.Add(icon);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
        _zoom.Style = (Style)Application.Current.FindResource("Petal.SubtleButton");
        _zoom.ToolTip = L.T("All of it in view (Ctrl+0)");
        _zoom.Click += (_, _) => _view.FitAll();
        buttons.Children.Add(_zoom);
        buttons.Children.Add(_find);
        buttons.Children.Add(HeaderButton("maximize", "Size", () => SizeMenu().IsOpen = true));
        buttons.Children.Add(HeaderButton("minus", "Fold to the title bar", ToggleFold));
        var more = HeaderButton("", "More Choices", () => OpenMenu(null));
        more.Content = new TextBlock { Text = "⋯", FontSize = 16, FontWeight = FontWeights.Bold, Margin = new Thickness(0, -4, 0, 0) };
        buttons.Children.Add(more);
        buttons.Children.Add(HeaderButton("x", "Close (Esc)", Close));
        DockPanel.SetDock(buttons, Dock.Right);
        _header.Children.Add(buttons);
        _name.Text = System.IO.Path.GetFileName(_path.TrimEnd('\\')) is { Length: > 0 } name ? name : _path;
        _name.ToolTip = _path;
        _header.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Children = { _name, _facts } });
        _header.Background = Brushes.Transparent;
        Screen.ScreenUi.DragFromBackground(this, _header);
        _header.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2)
            {
                e.Handled = true;
                ToggleFold();
            }
        };
        _header.MouseRightButtonUp += (_, e) =>
        {
            e.Handled = true;
            OpenMenu(_header);
        };
    }

    private Button HeaderButton(string icon, string tip, Action click)
    {
        var button = new Button { Padding = new Thickness(6), Margin = new Thickness(2, 0, 0, 0), ToolTip = L.T(tip), Style = (Style)Application.Current.FindResource("Petal.SubtleButton"), Focusable = false };
        if (icon.Length > 0)
        {
            var glyph = Icons.Create(icon, 14, Brushes.Gray, 2);
            glyph.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "Petal.Text");
            button.Content = glyph;
        }
        button.Click += (_, _) => click();
        return button;
    }

    /// <summary>The view shown, its facts and zoom in the header.</summary>
    private void Show(FloatView view)
    {
        _view = view;
        _stage.Child = view;
        view.Changed += () => Dispatcher.BeginInvoke(ShowFacts);
        ShowFacts();
        Dispatcher.BeginInvoke(() => view.Focus(), DispatcherPriority.Input);
    }

    private void ShowFacts()
    {
        _facts.Text = _view.Facts;
        _zoom.Content = _view.ZoomText ?? "";
        _zoom.Visibility = _view.ZoomText is null ? Visibility.Collapsed : Visibility.Visible;
        _find.Visibility = _view.CanFind ? Visibility.Visible : Visibility.Collapsed;
    }

    // ───────────── place and size ─────────────

    /// <summary>The screen's work area at a point, in device-independent pixels.</summary>
    private static Rect WorkAt(ScreenPoint at)
    {
        var (_, work, scale) = MonitorAt(new POINT(at.X, at.Y));
        return new Rect(work.Left / scale, work.Top / scale, (work.Right - work.Left) / scale, (work.Bottom - work.Top) / scale);
    }

    private Rect Work => WorkAt(new ScreenPoint((int)((Left + Width / 2) * Dpi), (int)((Top + Height / 2) * Dpi)));

    private double Dpi => VisualTreeHelper.GetDpi(this).DpiScaleX is > 0 and var d ? d : 1;

    private void Place(ScreenPoint at, Rect? like)
    {
        var work = WorkAt(at);
        if (like is { } rect)
        {
            SetRect(FloatMath.Inside(rect, work));
            return;
        }
        var sizes = SettingsStore.Current.FloatSizes;
        Size? kept = sizes.TryGetValue(_view.Kind.ToString(), out var s) && s.Length == 2 ? new Size(s[0], s[1]) : null;
        var size = FloatMath.FirstSize(_view.Kind, _view.Natural, work.Size, kept);
        double scale = VisualTreeHelper.GetDpi(Application.Current.MainWindow ?? this).DpiScaleX is > 0 and var d ? d : 1;
        var centre = new Point(at.X / scale, at.Y / scale);
        SetRect(FloatMath.Inside(new Rect(centre.X - size.Width / 2, centre.Y - size.Height / 2, size.Width, size.Height), work));
    }

    private void SetRect(Rect rect)
    {
        Left = rect.Left;
        Top = rect.Top;
        Width = rect.Width;
        Height = rect.Height;
    }

    /// <summary>The size each kind of file was last given (pictures take their own size each time).</summary>
    private void Remember()
    {
        if (_folded || _beforeFull is not null || _view.Kind == FloatKind.Picture || Width < 100)
            return;
        SettingsStore.Current.FloatSizes[_view.Kind.ToString()] = [Math.Round(Width), Math.Round(Height)];
        SettingsStore.Save();
    }

    private ContextMenu SizeMenu()
    {
        var menu = new ContextMenu { PlacementTarget = this, Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint };
        foreach (var item in SizeItems())
            menu.Items.Add(item);
        return menu;
    }

    private IEnumerable<Control> SizeItems()
    {
        Size? picture = _view.Kind == FloatKind.Picture ? _view.Natural : null;
        foreach (var (share, label) in new[] { (0.25, "Small: a quarter of the screen"), (0.5, "Medium: half the screen"), (0.75, "Large: three quarters of the screen") })
        {
            var item = new MenuItem { Header = L.T(label) };
            item.Click += (_, _) => ToShare(share, picture);
            yield return item;
        }
        var full = new MenuItem { Header = L.T("Fill the Screen"), IsChecked = _beforeFull is not null };
        full.Click += (_, _) => ToggleFull();
        yield return full;
        if (_view.Kind == FloatKind.Picture)
        {
            var own = new MenuItem { Header = L.T("The Picture's Own Size") };
            own.Click += (_, _) =>
            {
                var size = FloatMath.FirstSize(FloatKind.Picture, _view.Natural, new Size(Work.Width / FloatMath.PictureShare, Work.Height / FloatMath.PictureShare), null);
                Resize(size);
                Dispatcher.BeginInvoke(_view.OwnSize, DispatcherPriority.Loaded);
            };
            yield return own;
        }
    }

    private void ToShare(double share, Size? picture)
    {
        _beforeFull = null;
        Resize(FloatMath.ShareOfScreen(share, Work.Size, picture));
        Dispatcher.BeginInvoke(_view.FitAll, DispatcherPriority.Loaded);
    }

    /// <summary>A new size about the window's middle, kept on its screen.</summary>
    private void Resize(Size size)
    {
        if (_folded)
            ToggleFold();
        var work = Work;
        var middle = new Point(Left + Width / 2, Top + Height / 2);
        SetRect(FloatMath.Inside(new Rect(middle.X - size.Width / 2, middle.Y - size.Height / 2, size.Width, size.Height), work));
    }

    private void ToggleFull()
    {
        if (_beforeFull is { } before)
        {
            _beforeFull = null;
            SetRect(before);
        }
        else
        {
            if (_folded)
                ToggleFold();
            _beforeFull = new Rect(Left, Top, Width, Height);
            var work = Work;
            SetRect(new Rect(work.Left - FloatMath.Shadow, work.Top - FloatMath.Shadow, work.Width + 2 * FloatMath.Shadow, work.Height + 2 * FloatMath.Shadow));
        }
        Dispatcher.BeginInvoke(_view.FitAll, DispatcherPriority.Loaded);
    }

    /// <summary>Folded to its title bar, or open again at the height it had.</summary>
    internal void ToggleFold()
    {
        _folded = !_folded;
        if (_folded)
        {
            _unfoldedHeight = Height;
            _stage.Visibility = _findBar.Visibility = Visibility.Collapsed;
            Height = FloatMath.Header + 2 * FloatMath.Shadow + 2;
        }
        else
        {
            _stage.Visibility = Visibility.Visible;
            Height = Math.Max(_unfoldedHeight, 160);
        }
    }

    // ───────────── seeing and clicking through ─────────────

    private void SeeThrough(double opacity)
    {
        Opacity = opacity;
        _level.Text = FloatMath.Percent(opacity);
        _levelPill.BeginAnimation(OpacityProperty, new DoubleAnimationUsingKeyFrames
        {
            KeyFrames = { new DiscreteDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.Zero)), new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(700))), new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1100))) },
        });
    }

    /// <summary>
    /// Clicks pass through to the window under it, which it lets be seen; a small button stays above it, the one way
    /// back, since the window itself no longer takes clicks.
    /// </summary>
    internal void SetThrough(bool through)
    {
        if (through == _through)
            return;
        _through = through;
        var hwnd = new WindowInteropHelper(this).Handle;
        long style = (long)GetWindowLongPtr(hwnd, GWL_EXSTYLE);
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(through ? style | WS_EX_TRANSPARENT | WS_EX_LAYERED : style & ~WS_EX_TRANSPARENT));
        if (through)
        {
            _clearOpacity = Opacity;
            if (Opacity > 0.75)
                Opacity = 0.7;
            _throughPill = ThroughPill();
            _throughPill.Show();
            PlacePill();
        }
        else
        {
            Opacity = _clearOpacity;
            _throughPill?.Close();
            _throughPill = null;
        }
    }

    private Window ThroughPill()
    {
        var stop = new Button { Content = L.T("Stop"), Padding = new Thickness(10, 2, 10, 3), Margin = new Thickness(8, 0, 0, 0), Style = (Style)Application.Current.FindResource("Petal.PrimaryButton") };
        stop.Click += (_, _) => SetThrough(false);
        var said = new TextBlock { Text = L.T("Clicks pass through"), VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
        said.SetResourceReference(TextBlock.ForegroundProperty, "Petal.Text");
        var pill = new Border { CornerRadius = new CornerRadius(14), Padding = new Thickness(12, 5, 6, 5), BorderThickness = new Thickness(1), Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { said, stop } } };
        pill.SetResourceReference(Border.BackgroundProperty, "Petal.Popup");
        pill.SetResourceReference(Border.BorderBrushProperty, "Petal.ControlBorder");
        var window = new Window { WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent, Topmost = true, ShowInTaskbar = false, ShowActivated = false, SizeToContent = SizeToContent.WidthAndHeight, Content = pill, FontFamily = UiFonts.Text };
        L.Apply(window);
        return window;
    }

    private void PlacePill()
    {
        if (_throughPill is not { } pill)
            return;
        pill.UpdateLayout();
        pill.Left = Left + Width - FloatMath.Shadow - pill.ActualWidth - 8;
        pill.Top = Top + FloatMath.Shadow + 4;
    }

    // ───────────── tucked into the edge ─────────────

    private void Tuck()
    {
        if (_tucked)
        {
            Untuck();
            return;
        }
        var work = Work;
        _restLeft = Left;
        _tucked = true;
        bool right = Left + ActualWidth / 2 > work.Left + work.Width / 2;
        BeginAnimation(LeftProperty, Motion.To(right ? work.Right - 44 : work.Left - ActualWidth + 44, 260, Motion.Standard));
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

    // ───────────── the menu ─────────────

    private void OpenMenu(UIElement? at)
    {
        var menu = new ContextMenu { PlacementTarget = at ?? this, Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint };
        var size = new MenuItem { Header = L.T("Size") };
        foreach (var item in SizeItems())
            size.Items.Add(item);
        menu.Items.Add(size);
        var through = new MenuItem { Header = L.T("See Through") };
        foreach (var level in new[] { 1.0, 0.8, 0.6, 0.4 })
        {
            var item = new MenuItem { Header = FloatMath.Percent(level), IsChecked = Math.Abs(Opacity - level) < 0.05 };
            item.Click += (_, _) => SeeThrough(level);
            through.Items.Add(item);
        }
        menu.Items.Add(through);
        menu.Items.Add(Line("Let Clicks Pass Through", () => SetThrough(true)));
        menu.Items.Add(Line(_folded ? "Open It Out Again" : "Fold to the Title Bar", ToggleFold));
        menu.Items.Add(Line(_tucked ? "Bring Back from the Edge" : "Tuck into the Screen Edge", Tuck));
        if (_view.CanFind)
            menu.Items.Add(Line("Find…", OpenFind));
        var own = _view.MenuItems().ToList();
        if (own.Count > 0)
        {
            menu.Items.Add(new Separator());
            foreach (var item in own)
                menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        menu.Items.Add(Line("Open in Its Program", OpenInProgram));
        menu.Items.Add(Line("Show in File Explorer", () => ExplorerSelection.Reveal([_path])));
        menu.Items.Add(Line("Open the Wheel for It", () =>
        {
            GetCursorPos(out var point);
            App.Current.Controller.OpenWheelNow([_path], new ScreenPoint(point.X, point.Y));
        }));
        menu.Items.Add(Line("Copy the File", CopyFile));
        menu.Items.Add(Line("Copy Its Path", () => Clipboard.SetText(_path)));
        menu.Items.Add(Line("Put It on the Shelf", () =>
        {
            GetCursorPos(out var point);
            FloatShelf.Shared.AddAndShow([_path], new ScreenPoint(point.X, point.Y));
            Close();
        }));
        menu.Items.Add(new Separator());
        menu.Items.Add(Line("Close", Close));
        menu.IsOpen = true;
    }

    private static MenuItem Line(string text, Action click)
    {
        var item = new MenuItem { Header = L.T(text) };
        item.Click += (_, _) => click();
        return item;
    }

    private void OpenInProgram()
    {
        try
        {
            Process.Start(new ProcessStartInfo(_path) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Error(ex, "Opening a floating file in its program");
        }
    }

    private void CopyFile()
    {
        var files = new System.Collections.Specialized.StringCollection { _path };
        Clipboard.SetFileDropList(files);
    }

    // ───────────── keys ─────────────

    private void OnKey(object sender, KeyEventArgs e)
    {
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        switch (e.Key)
        {
            case Key.Escape when _findBar.Visibility == Visibility.Visible:
                CloseFind();
                break;
            case Key.Escape:
                Close();
                break;
            case Key.F when ctrl && _view.CanFind:
                OpenFind();
                break;
            case Key.C when ctrl && _sought.IsKeyboardFocusWithin is false:
                if (!_view.Copy())
                    CopyFile();
                break;
            case Key.D0 or Key.NumPad0 when ctrl:
                _view.FitAll();
                break;
            case Key.D1 or Key.NumPad1 when ctrl:
                _view.OwnSize();
                break;
            case Key.OemPlus or Key.Add when !_sought.IsKeyboardFocusWithin:
                _view.ZoomBy(1.25);
                break;
            case Key.OemMinus or Key.Subtract when !_sought.IsKeyboardFocusWithin:
                _view.ZoomBy(1 / 1.25);
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    // ───────────── finding ─────────────

    private void BuildFindBar()
    {
        var next = HeaderButton("", "Next (Enter)", () => _ = FindAsync(true));
        next.Content = new TextBlock { Text = "↓", FontSize = 14 };
        var previous = HeaderButton("", "Previous (Shift+Enter)", () => _ = FindAsync(false));
        previous.Content = new TextBlock { Text = "↑", FontSize = 14 };
        var close = HeaderButton("x", "Close (Esc)", CloseFind);
        foreach (var b in new[] { close, next, previous })
            DockPanel.SetDock(b, Dock.Right);
        DockPanel.SetDock(_foundCount, Dock.Right);
        _findBar.Children.Add(close);
        _findBar.Children.Add(next);
        _findBar.Children.Add(previous);
        _findBar.Children.Add(_foundCount);
        _findBar.Children.Add(_sought);
        _foundCount.SetResourceReference(TextBlock.ForegroundProperty, "Petal.TextSecondary");
        _sought.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                _ = FindAsync(!Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
            }
        };
    }

    private void OpenFind()
    {
        if (!_view.CanFind)
            return;
        if (_folded)
            ToggleFold();
        _findBar.Visibility = Visibility.Visible;
        _sought.Focus();
        _sought.SelectAll();
    }

    private void CloseFind()
    {
        _findBar.Visibility = Visibility.Collapsed;
        _finding.Cancel();
        _ = _view.FindAsync("", true, CancellationToken.None);
        _view.Focus();
    }

    private async Task FindAsync(bool forward)
    {
        _finding.Cancel();
        _finding = new CancellationTokenSource();
        var token = _finding.Token;
        _foundCount.Text = L.T("Finding…");
        try
        {
            var (at, of) = await _view.FindAsync(_sought.Text, forward, token);
            if (!token.IsCancellationRequested)
                _foundCount.Text = of == 0 ? L.T("Not found") : L.F("{0} of {1}", at, of);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            _foundCount.Text = L.T("That didn't work.");
            Log.Info($"float: finding in {System.IO.Path.GetFileName(_path)} ({ex.Message})");
        }
    }

    // ───────────── the file changing ─────────────

    /// <summary>The file watched: when it is saved again (in its own program), it is shown again.</summary>
    private void Watch()
    {
        try
        {
            if (System.IO.Path.GetDirectoryName(_path) is not { Length: > 0 } folder || !Directory.Exists(folder))
                return;
            _watcher = new FileSystemWatcher(folder, System.IO.Path.GetFileName(_path))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                EnableRaisingEvents = true,
            };
            void Changed(object? s, FileSystemEventArgs e) => Dispatcher.BeginInvoke(() =>
            {
                _reload.Stop();
                _reload.Start();
            });
            _watcher.Changed += Changed;
            _watcher.Created += Changed;
            _watcher.Renamed += (s, e) => Changed(s, e);
            _watcher.Deleted += (_, _) => Dispatcher.BeginInvoke(() => _facts.Text = L.T("The file was moved or deleted; what was shown stays."));
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            Log.Info($"float: not watching {_path} ({ex.Message})");
        }
    }

    private async Task ReloadAsync()
    {
        if (!File.Exists(_path) && !Directory.Exists(_path))
            return;
        try
        {
            var view = await FloatViews.CreateAsync(_path, CancellationToken.None);
            var old = _view;
            Show(view);
            old.Dispose();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Still being written: the next change shows it.
            Log.Info($"float: {System.IO.Path.GetFileName(_path)} not shown again yet ({ex.Message})");
        }
    }
}
