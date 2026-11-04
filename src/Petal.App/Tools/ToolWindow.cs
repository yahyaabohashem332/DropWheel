using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;
using Petal.App.Input;
using Petal.App.Interop;
using Petal.App.Localization;
using Petal.App.Visuals;
using static Petal.App.Interop.NativeMethods;

namespace Petal.App.Tools;

/// <summary>
/// Shared chrome for every tool window: a compact, translucent window with a centred title,
/// a close button, the tool's controls, and a footer with Reset on the left and the primary
/// action on the right. Enter runs the primary action and Esc closes.
/// </summary>
public abstract class ToolWindow : Window
{
    private readonly Grid _layout = new();
    private readonly Border _surface = new();
    private readonly TextBlock _title = new();
    private readonly DockPanel _footer = new();
    private readonly TextBlock _footerNote = new();
    private bool _translucent;
    private bool _full;
    private readonly Grid _header = new() { Height = 44 };
    private readonly Button _close;
    private readonly ContentControl _bodyHost = new() { Focusable = false };
    private readonly Border _footerLine;

    protected ToolWindow(string title, double width = 400)
    {
        Title = L.T(title);
        Width = width;
        L.Apply(this);
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.Manual;
        ShowInTaskbar = true;
        FontFamily = UiFonts.Text;
        UseLayoutRounding = true;
        Background = Brushes.Transparent;
        SetResourceReference(ForegroundProperty, "Petal.Text");
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
        Icon = AppIcon.Source;

        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 44,
            ResizeBorderThickness = new Thickness(0),
            GlassFrameThickness = new Thickness(-1),
            CornerRadius = new CornerRadius(8),
            UseAeroCaptionButtons = false,
        });

        _layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // Header
        var header = _header;
        _title.Text = L.T(title);
        _title.Style = (Style)Application.Current.FindResource("Petal.Title");
        _title.HorizontalAlignment = HorizontalAlignment.Center;
        _title.VerticalAlignment = VerticalAlignment.Center;
        _title.Margin = new Thickness(48, 0, 48, 0);
        header.Children.Add(_title);
        var close = IconButton("x", "Close (Esc)", 15);
        close.HorizontalAlignment = HorizontalAlignment.Right;
        close.Margin = new Thickness(0, 0, 8, 0);
        close.Click += (_, _) => Close();
        _close = close;
        WindowChrome.SetIsHitTestVisibleInChrome(close, true);
        header.Children.Add(close);
        _layout.Children.Add(header);

        var divider = Divider();
        Grid.SetRow(divider, 1);
        _layout.Children.Add(divider);

        Body = new StackPanel { Margin = new Thickness(18, 14, 18, 16) };
        _bodyHost.Content = Body;
        Grid.SetRow(_bodyHost, 2);
        _layout.Children.Add(_bodyHost);

        var footerDivider = Divider();
        _footerLine = footerDivider;
        Grid.SetRow(footerDivider, 3);
        _layout.Children.Add(footerDivider);

        _footer.Margin = new Thickness(14, 10, 14, 12);
        _footer.LastChildFill = true;
        PrimaryButton = new Button { Style = (Style)Application.Current.FindResource("Petal.PrimaryButton"), IsDefault = true, MinWidth = 88 };
        DockPanel.SetDock(PrimaryButton, Dock.Right);
        ResetButton = new Button { Content = L.T("Reset"), Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 10, 0) };
        DockPanel.SetDock(ResetButton, Dock.Left);
        _footerNote.Style = (Style)Application.Current.FindResource("Petal.Caption");
        _footerNote.VerticalAlignment = VerticalAlignment.Center;
        // Up to two lines (a sentence is often longer in other languages), then "…" with the whole note as a tooltip.
        _footerNote.TextTrimming = TextTrimming.CharacterEllipsis;
        _footerNote.TextWrapping = TextWrapping.Wrap;
        _footerNote.MaxHeight = 34;
        _footerNote.Margin = new Thickness(2, 0, 12, 0);
        _footer.Children.Add(PrimaryButton);
        _footer.Children.Add(ResetButton);
        _footer.Children.Add(_footerNote);
        Grid.SetRow(_footer, 4);
        _layout.Children.Add(_footer);

        _surface.Child = _layout;
        Content = _surface;

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                if (!OnEscape())
                    Close();
            }
        };
        SourceInitialized += (_, _) =>
        {
            // Without the system menu, Windows stops drawing its own close glyph in the glass
            // frame under ours. Esc and the header button close the window.
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (!_full)
                SetWindowLongPtr(hwnd, GWL_STYLE, (IntPtr)((long)GetWindowLongPtr(hwnd, GWL_STYLE) & ~WS_SYSMENU));
            ApplyTheme();
        };
        Theme.Changed += OnThemeChanged;
        Closed += (_, _) => Theme.Changed -= OnThemeChanged;
    }

    protected StackPanel Body { get; }

    /// <summary>
    /// Makes this the app's main window rather than a small tool: solid, resizable, with minimize and maximize, its own
    /// name at the top, and <paramref name="content"/> in place of the body and the footer.
    /// </summary>
    protected void BecomeFullWindow(UIElement content)
    {
        _full = true;
        SizeToContent = SizeToContent.Manual;
        ResizeMode = ResizeMode.CanResize;
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 44,
            ResizeBorderThickness = new Thickness(6),
            GlassFrameThickness = new Thickness(0),
            CornerRadius = new CornerRadius(8),
            UseAeroCaptionButtons = false,
        });
        _footer.Visibility = _footerLine.Visibility = Visibility.Collapsed;
        PrimaryButton.IsDefault = false;
        _bodyHost.Content = content;
        _bodyHost.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        _bodyHost.VerticalContentAlignment = VerticalAlignment.Stretch;

        // The name at the start of the bar, the window's buttons at the end.
        _header.Children.Remove(_title);
        var icon = new Image { FlowDirection = FlowDirection.LeftToRight, Width = 22, Height = 22, Source = AppIcon.Large, Margin = new Thickness(0, 0, 10, 0) };
        RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
        _title.Margin = new Thickness(0);
        _title.Text = Title;
        _title.HorizontalAlignment = HorizontalAlignment.Left;
        _header.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(16, 0, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, Children = { icon, _title } });
        var minimize = IconButton("minus", "Minimize", 15);
        minimize.Click += (_, _) => SystemCommands.MinimizeWindow(this);
        var maximize = IconButton("square", "Maximize", 13);
        maximize.Click += (_, _) =>
        {
            if (WindowState == WindowState.Maximized)
                SystemCommands.RestoreWindow(this);
            else
                SystemCommands.MaximizeWindow(this);
        };
        _header.Children.Remove(_close);
        _close.ToolTip = L.T("Close");
        _close.Margin = new Thickness(0);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0), Children = { minimize, maximize, _close } };
        foreach (var button in new[] { minimize, maximize, _close })
        {
            button.Margin = new Thickness(2, 0, 0, 0);
            WindowChrome.SetIsHitTestVisibleInChrome(button, true);
        }
        _header.Children.Add(buttons);

        // A maximized window reaches past the edges of the screen by its frame: the content stays within them.
        void Fit()
        {
            double frame = WindowState == WindowState.Maximized ? Math.Max(0, (SystemParameters.MaximizedPrimaryScreenWidth - SystemParameters.WorkArea.Width) / 2) : 0;
            _surface.Margin = new Thickness(frame);
            maximize.ToolTip = L.T(WindowState == WindowState.Maximized ? "Restore" : "Maximize");
        }
        StateChanged += (_, _) => Fit();
        Loaded += (_, _) => Fit();
    }

    /// <summary>Esc closes the window, unless it has a use for the key first (and says so by returning true).</summary>
    protected virtual bool OnEscape() => false;

    protected Button PrimaryButton { get; }

    protected Button ResetButton { get; }

    /// <summary>Small grey text left of the primary button, e.g. the name of the file that will be saved.</summary>
    protected string FooterNote
    {
        get => _footerNote.Text;
        set
        {
            _footerNote.Text = L.T(value);
            _footerNote.ToolTip = string.IsNullOrEmpty(value) ? null : _footerNote.Text;
        }
    }

    private void OnThemeChanged() => Dispatcher.BeginInvoke(ApplyTheme);

    private void ApplyTheme()
    {
        var p = Theme.Current;
        if (_full)
        {
            Background = Palette.Brush(p.WindowBackground);
            _surface.Background = Palette.Brush(p.WindowBackground);
            return;
        }
        _translucent = Backdrop.Apply(this, p.IsDark);
        // Over a blur the content gets a wash so text stays readable on busy wallpapers: a light one over
        // Windows 11's acrylic, which brings its own tint, and the whole tint over Windows 10's plain blur.
        _surface.Background = !_translucent ? Palette.Brush(p.WindowBackground)
            : Backdrop.IsWindows11 ? Palette.Brush(p.IsDark ? Color.FromArgb(0x50, 0x20, 0x20, 0x24) : Color.FromArgb(0x60, 0xFA, 0xFA, 0xFB))
            : Palette.Brush(p.IsDark ? Color.FromArgb(0xCC, 0x24, 0x24, 0x2A) : Color.FromArgb(0xE6, 0xF6, 0xF6, 0xF8));
    }

    /// <summary>Shows the window centred on a screen point (physical pixels), kept on that monitor.</summary>
    public void ShowNear(ScreenPoint at)
    {
        var (_, work, scale) = MonitorAt(new POINT(at.X, at.Y));
        Opacity = 1;
        Show();
        UpdateLayout();
        double w = ActualWidth * scale, h = ActualHeight * scale;
        double x = Math.Clamp(at.X - w / 2, work.Left + 8, work.Right - w - 8);
        double y = Math.Clamp(at.Y - h / 2, work.Top + 8, work.Bottom - h - 8);
        Left = x / scale;
        Top = y / scale;
        Activate();
        // Bring to the front even though Explorer had focus during the drag.
        Topmost = true;
        Topmost = false;
        Focus();
    }

    // ---------- building blocks ----------

    protected static Border Divider() => new Border
    {
        Height = 1,
        Background = (Brush)Application.Current.FindResource("Petal.Divider"),
        SnapsToDevicePixels = true,
    }.WithDynamic(Border.BackgroundProperty, "Petal.Divider");

    protected static Button IconButton(string icon, string tooltip, double size = 16, bool filled = false)
    {
        var button = new Button
        {
            Style = (Style)Application.Current.FindResource(filled ? "Petal.FilledIconButton" : "Petal.IconButton"),
            ToolTip = L.T(tooltip),
            Content = ThemedIcon(icon, size),
        };
        System.Windows.Automation.AutomationProperties.SetName(button, L.T(tooltip));
        return button;
    }

    /// <summary>An icon whose stroke follows the theme's text colour.</summary>
    public static System.Windows.Shapes.Path ThemedIcon(string icon, double size, string brushKey = "Petal.Text", double stroke = 2)
    {
        var path = Icons.Create(icon, size, Brushes.Black, stroke);
        path.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, brushKey);
        return path;
    }

    /// <summary>The same words as <see cref="Text"/>, for the parts of a window that aren't windows themselves.</summary>
    internal static TextBlock PlainText(string text, string style = "Petal.Body") => Text(text, style);

    protected static TextBlock Text(string text, string style = "Petal.Body") => new()
    {
        Text = L.T(text),
        Style = (Style)Application.Current.FindResource(style),
    };

    protected static TextBlock SectionTitle(string text)
    {
        var t = Text(text, "Petal.Section");
        return t;
    }

    /// <summary>A "label …… control" row, the standard layout of every settings line.</summary>
    protected static Grid Row(string label, UIElement control, double labelWidth = 120, double top = 6)
    {
        var grid = new Grid { Margin = new Thickness(0, top, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(labelWidth) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var l = Text(label, "Petal.Label");
        // A label longer in some language than its column wraps instead of running under the control.
        l.TextWrapping = TextWrapping.Wrap;
        l.Margin = new Thickness(0, 0, 8, 0);
        grid.Children.Add(l);
        Grid.SetColumn(control, 1);
        if (control is FrameworkElement fe && fe.HorizontalAlignment == HorizontalAlignment.Stretch && control is not System.Windows.Controls.Slider)
            fe.HorizontalAlignment = HorizontalAlignment.Stretch;
        grid.Children.Add(control);
        return grid;
    }

    /// <summary>Segmented control: one choice among a few short options.</summary>
    protected static Segmented<T> Segments<T>(IEnumerable<(T Value, string Label)> options, T selected) where T : notnull =>
        new(options, selected);

    /// <summary>A slider with its value shown on the right, e.g. "Padding   ———o———   160 px".</summary>
    protected static LabeledSlider Slider(double min, double max, double value, Func<double, string> format, double step = 1, bool ticks = false) =>
        new(min, max, value, format, step, ticks);

    protected static ComboBox Choice<T>(IEnumerable<(T Value, string Label)> options, T selected) where T : notnull
    {
        var combo = new ComboBox { MinWidth = 160 };
        foreach (var (value, label) in options)
        {
            var item = new ComboBoxItem { Content = L.T(label), Tag = value };
            combo.Items.Add(item);
            if (EqualityComparer<T>.Default.Equals(value, selected))
                combo.SelectedItem = item;
        }
        if (combo.SelectedIndex < 0 && combo.Items.Count > 0)
            combo.SelectedIndex = 0;
        return combo;
    }

    protected static T ValueOf<T>(ComboBox combo) => combo.SelectedItem is ComboBoxItem { Tag: T value } ? value : default!;

    /// <summary>The dark, rounded panel tool windows show media in.</summary>
    protected static Border PreviewPanel(UIElement child, double height)
    {
        var border = new Border
        {
            CornerRadius = new CornerRadius(12),
            Height = height,
            ClipToBounds = true,
            Child = child,
        };
        border.SetResourceReference(Border.BackgroundProperty, "Petal.Preview");
        return border;
    }

    protected static string FileLabel(IReadOnlyList<string> files) =>
        files.Count == 1 ? Path.GetFileName(files[0]) : L.P(files.Count, "{0} file", "{0} files");
}

internal static class ElementExtensions
{
    public static T WithDynamic<T>(this T element, DependencyProperty property, string key) where T : FrameworkElement
    {
        element.SetResourceReference(property, key);
        return element;
    }
}

/// <summary>Petal's icon as an ImageSource for window title bars and the taskbar.</summary>
internal static class AppIcon
{
    private static ImageSource? _source;

    public static ImageSource? Source => _source ??= Load();

    /// <summary>The 256 px artwork, for places that show the icon large.</summary>
    public static ImageSource Large { get; } = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/Petal;component/Assets/Petal-256.png"));

    private static ImageSource? Load()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Assets", "Petal.ico");
            return File.Exists(path) ? System.Windows.Media.Imaging.BitmapFrame.Create(new Uri(path)) : null;
        }
        catch
        {
            return null;
        }
    }
}
