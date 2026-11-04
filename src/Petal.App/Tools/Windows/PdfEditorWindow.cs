using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using System.Windows.Threading;
using Microsoft.Win32;
using Petal.App.Input;
using Petal.App.Jobs;
using Petal.App.Localization;
using Petal.App.Tools.Parts;
using Petal.App.Visuals;
using Petal.Core.Jobs;
using Petal.Core.Tools;
using Petal.Engines.Documents.Pdf;
using Path = System.IO.Path;

namespace Petal.App.Tools.Windows;

/// <summary>What the mouse does on a page of the PDF editor.</summary>
public enum PdfTool
{
    Select,
    Text,
    Date,
    Check,
    Cross,
    Dot,
    Retype,
    Highlight,
    Underline,
    StrikeOut,
    Note,
    Pen,
    Line,
    Arrow,
    Rectangle,
    Ellipse,
    Cover,
    Picture,
    Signature,
    Redact,
}

/// <summary>
/// Edit PDF, the everyday things Acrobat is used for, on this PC. Forms filled in where their fields are; text and marks put anywhere;
/// a line of the PDF's own text retyped; words highlighted, comments, drawings and signatures; and redaction that really takes
/// things out. Nothing is written until Save, and then to a new file beside the original. Items are kept as immutable records in one
/// list and the fields' values in one table, so undo and redo are copies of those two; pages are drawn by PDFium when they come into view,
/// off the interface thread, and the items over them by WPF. See <see cref="PdfEditSession"/> for what happens to the file.
/// </summary>
public sealed class PdfEditorWindow : ToolWindow
{
    private const int HighlightYellow = 0xFFD23F, Danger = 0xE02020, InkBlue = 0x1D2B6B;
    private const double DefaultMark = 14, HandleSize = 9;
    private const double MinZoom = 0.25, MaxZoom = 5;

    /// <summary>A long page opens as wide as the window allows, up to a little over its printed size.</summary>
    private const double OpeningZoom = 1.6;

    private static readonly (PdfTool Tool, string Icon, string Tip)[][] ToolGroups =
    [
        [(PdfTool.Select, "pointer", "Select and move")],
        [(PdfTool.Text, "type", "Write text"), (PdfTool.Date, "calendar", "Today's date"), (PdfTool.Check, "check", "Tick"), (PdfTool.Cross, "x", "Cross"), (PdfTool.Dot, "record", "Dot")],
        [(PdfTool.Retype, "textSelect", "Edit the PDF's text")],
        [(PdfTool.Highlight, "highlighter", "Highlight"), (PdfTool.Underline, "underline", "Underline"), (PdfTool.StrikeOut, "strikethrough", "Strike out"), (PdfTool.Note, "note", "Comment")],
        [(PdfTool.Pen, "pencil", "Pen"), (PdfTool.Line, "minus", "Line"), (PdfTool.Arrow, "arrowUpRight", "Arrow"), (PdfTool.Rectangle, "square", "Rectangle"), (PdfTool.Ellipse, "circle", "Ellipse")],
        [(PdfTool.Cover, "eraser", "Whiteout"), (PdfTool.Picture, "imagePlus", "Place a picture"), (PdfTool.Signature, "signature", "Signature")],
        [(PdfTool.Redact, "eyeOff", "Redact")],
    ];

    private static readonly Dictionary<PdfTool, string> Hints = new()
    {
        [PdfTool.Select] = "Click an item to select it, drag to move it, drag its corners to resize. Click text to edit it.",
        [PdfTool.Text] = "Click where the text goes, then type.",
        [PdfTool.Date] = "Click to stamp today's date.",
        [PdfTool.Check] = "Click to drop a tick.",
        [PdfTool.Cross] = "Click to drop a cross.",
        [PdfTool.Dot] = "Click to drop a dot.",
        [PdfTool.Retype] = "Click a line of the PDF's text to type it again.",
        [PdfTool.Highlight] = "Drag across words to highlight them.",
        [PdfTool.Underline] = "Drag across words to underline them.",
        [PdfTool.StrikeOut] = "Drag across words to strike them out.",
        [PdfTool.Note] = "Click to add a comment.",
        [PdfTool.Pen] = "Draw freehand.",
        [PdfTool.Line] = "Drag to draw a line.",
        [PdfTool.Arrow] = "Drag from the tail toward the arrowhead.",
        [PdfTool.Rectangle] = "Drag to draw a rectangle.",
        [PdfTool.Ellipse] = "Drag to draw an ellipse.",
        [PdfTool.Cover] = "Drag to paint a rectangle over the page. What it hides stays in the file: to remove it, use Redact.",
        [PdfTool.Picture] = "Click where the picture goes.",
        [PdfTool.Signature] = "Click where your signature goes.",
        [PdfTool.Redact] = "Boxes show red until saved. Saving takes out everything under them for good, and they turn black.",
    };

    private readonly ToolInfo _tool;
    private readonly string _file;
    private readonly JobRunner _jobs;
    private readonly ScreenPoint _at;
    private readonly TaskCompletionSource _opened = new();

    // ---------- what is on screen ----------
    private readonly Grid _root = new();
    private readonly Dictionary<PdfTool, Border> _tiles = [];
    private readonly Button _undo, _redo, _delete;
    private readonly StackPanel _options = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _hint = new() { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
    private readonly ScrollViewer _scroll = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FlowDirection = FlowDirection.LeftToRight, Focusable = true };
    private readonly StackPanel _pagesPanel = new() { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(16, 16, 16, 2), FlowDirection = FlowDirection.LeftToRight };
    private readonly ScrollViewer _thumbScroll = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Width = 118 };
    private readonly StackPanel _thumbs = new() { Margin = new Thickness(0, 8, 0, 8) };
    private readonly Grid _pageArea = new();
    private readonly Border _message = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(20), CornerRadius = new CornerRadius(12), MaxWidth = 380, Visibility = Visibility.Collapsed };
    private readonly TextBlock _pageLabel = new() { VerticalAlignment = VerticalAlignment.Center, MinWidth = 90, TextAlignment = TextAlignment.Center };
    private readonly TextBlock _zoomLabel = new() { VerticalAlignment = VerticalAlignment.Center, MinWidth = 46, TextAlignment = TextAlignment.Center };
    private readonly TextBlock _sizeValue = new() { VerticalAlignment = VerticalAlignment.Center, MinWidth = 26, TextAlignment = TextAlignment.Center };
    private readonly TextBlock _weightValue = new() { VerticalAlignment = VerticalAlignment.Center, MinWidth = 26, TextAlignment = TextAlignment.Center };
    private readonly CheckBox _flatten = new() { Content = L.T("Flatten"), VerticalAlignment = VerticalAlignment.Center, ToolTip = L.T("Burn fields and annotations into the pages, so they can't be changed.") };
    private readonly CheckBox _hidden = new() { Content = L.T("Remove the file's hidden details"), IsChecked = true, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0), ToolTip = L.T("Author, dates, XMP, attachments and scripts leave the file with what is redacted."), Visibility = Visibility.Collapsed };
    private readonly ColorWell _colourWell = new(Colors.Black);
    private readonly CheckBox _bold = new() { Content = L.T("Bold"), VerticalAlignment = VerticalAlignment.Center };
    private readonly FrameworkElement _sizeControl, _weightControl, _colourControl;
    private readonly StackPanel _redactBar = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    private readonly Segmented<bool> _redactWords;
    private readonly TextBox _findBox = new() { Width = 150, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _findButton = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
    private readonly Button _privateButton = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
    private readonly List<PdfPageView> _pages = [];
    private readonly List<Border> _thumbCells = [];
    private readonly SemaphoreSlim _renderGate = new(2);
    private readonly DispatcherTimer _settle;

    // ---------- the document ----------
    private PdfEditSession? _session;
    private List<PdfFormField> _fields = [];
    private readonly List<(PdfFormField Field, int Widget)> _tabOrder = [];
    private readonly Dictionary<string, List<FrameworkElement>> _fieldControls = [];

    // ---------- the edit: what undo and redo keep ----------
    private List<PdfPageItem> _items = [];
    private Dictionary<string, string> _values = [];
    private readonly Stack<(List<PdfPageItem> Items, Dictionary<string, string> Values)> _undoStack = new();
    private readonly Stack<(List<PdfPageItem> Items, Dictionary<string, string> Values)> _redoStack = new();
    private readonly HashSet<string> _secrets = new(StringComparer.OrdinalIgnoreCase);
    private string? _editingField;
    private bool _syncing;

    // ---------- the mouse ----------
    private PdfTool _toolNow = PdfTool.Select;
    private PdfPageItem? _selected;
    private PdfPageItem? _draft;
    private int _activePage = -1;
    private Point _pressedAt, _lastAt;
    private bool _moved, _pressed;
    private int _handle = -1;
    private PdfPageItem? _beforeDrag;
    private BitmapSource? _pendingPicture;

    // ---------- defaults for new items ----------
    private double _zoom = 1;
    private double _size = 12, _weight = 2;
    private readonly Dictionary<PdfTool, int> _colours = new()
    {
        [PdfTool.Highlight] = HighlightYellow,
        [PdfTool.Note] = HighlightYellow,
        [PdfTool.Underline] = Danger,
        [PdfTool.StrikeOut] = Danger,
        [PdfTool.Pen] = InkBlue,
        [PdfTool.Line] = Danger,
        [PdfTool.Arrow] = Danger,
        [PdfTool.Rectangle] = Danger,
        [PdfTool.Ellipse] = Danger,
        [PdfTool.Cover] = 0xFFFFFF,
        [PdfTool.Check] = InkBlue,
        [PdfTool.Cross] = InkBlue,
        [PdfTool.Dot] = InkBlue,
        [PdfTool.Text] = 0x000000,
        [PdfTool.Date] = 0x000000,
    };

    // ---------- typing on a page ----------
    private TextBox? _editor;
    private Action<string>? _commitEditor;

    private readonly ConditionalWeakTable<byte[], BitmapSource> _decoded = new();

    public PdfEditorWindow(ToolInfo tool, string file, JobRunner jobs, ScreenPoint at) : base(tool.Name, Math.Min(1180, SystemParameters.WorkArea.Width - 40))
    {
        _tool = tool;
        _file = file;
        _jobs = jobs;
        _at = at;
        SizeToContent = SizeToContent.Manual;
        Height = Math.Min(860, SystemParameters.WorkArea.Height - 40);
        MinWidth = 760;
        MinHeight = 480;
        ResizeMode = ResizeMode.CanResize;
        WindowChrome.GetWindowChrome(this).ResizeBorderThickness = new Thickness(6);
        ((ContentControl)Body.Parent).Content = _root;
        PrimaryButton.IsDefault = false;
        PrimaryButton.Content = L.T("Save");
        PrimaryButton.IsEnabled = false;
        PrimaryButton.Click += async (_, _) => await SaveAsync();
        FooterNote = Path.GetFileName(OutputNaming.ForTool(file, tool));

        _undo = IconButton("undo", "Undo (Ctrl+Z)");
        _undo.Click += (_, _) => Undo();
        _redo = IconButton("redo", "Redo (Ctrl+Y)");
        _redo.Click += (_, _) => Redo();
        _delete = IconButton("trash", "Delete selected (Del)");
        _delete.Click += (_, _) => DeleteSelected();

        _sizeControl = Stepper("Size", _sizeValue, () => ChangeSize(-1), () => ChangeSize(1));
        _weightControl = Stepper("Thickness", _weightValue, () => ChangeWeight(-0.5), () => ChangeWeight(0.5));
        var colourRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 14, 0) };
        colourRow.Children.Add(new TextBlock { Text = L.T("Colour"), Style = (Style)FindResource("Petal.Label"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        colourRow.Children.Add(_colourWell);
        _colourControl = colourRow;
        _colourWell.Changed += c => ChangeColour(Rgb(c));
        _bold.Margin = new Thickness(0, 0, 14, 0);
        _bold.Click += (_, _) => ChangeBold(_bold.IsChecked == true);

        _redactWords = Segments([(false, "Box"), (true, "Words")], false);
        _redactWords.Margin = new Thickness(0, 0, 10, 0);
        _findBox.ToolTip = L.T("A word or phrase to redact on all pages");
        _findBox.KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                await FindAsync(_findBox.Text);
            }
        };
        _findButton.Content = L.T("Find and mark");
        _findButton.Click += async (_, _) => await FindAsync(_findBox.Text);
        _privateButton.Content = L.T("Find private details");
        _privateButton.ToolTip = L.T("E-mail addresses, phone numbers, card numbers, IBANs, IP addresses and passwords; scanned pages are read too.");
        _privateButton.Click += async (_, _) => await FindPrivateAsync();
        _redactBar.Children.Add(_redactWords);
        _redactBar.Children.Add(_findBox);
        _redactBar.Children.Add(_findButton);
        _redactBar.Children.Add(_privateButton);
        _options.Children.Add(_sizeControl);
        _options.Children.Add(_weightControl);
        _options.Children.Add(_colourControl);
        _options.Children.Add(_bold);
        _options.Children.Add(_redactBar);

        BuildLayout();
        _settle = new DispatcherTimer(TimeSpan.FromMilliseconds(120), DispatcherPriority.Background, (_, _) => Settle(), Dispatcher);
        _settle.Stop();
        PreviewKeyDown += OnPreviewKey;
        SelectTool(PdfTool.Select);
        Loaded += async (_, _) => await OpenAsync(null);
        Closing += (_, e) =>
        {
            if (ConfirmClose && IsDirty && !AskToDiscard())
                e.Cancel = true;
        };
        Closed += (_, _) => _session?.Dispose();
    }

    // ---------- for tests and snapshots ----------

    /// <summary>A window that sets up a scene once its file is open, and closes without asking.</summary>
    internal static PdfEditorWindow ForSnapshot(ToolInfo tool, string file, JobRunner jobs, ScreenPoint at, Action<PdfEditorWindow> scene)
    {
        var window = new PdfEditorWindow(tool, file, jobs, at) { ConfirmClose = false };
        window.Opened.ContinueWith(_ => window.Dispatcher.BeginInvoke(() => window.Dispatcher.BeginInvoke(() => scene(window), DispatcherPriority.ContextIdle)), TaskScheduler.Default);
        return window;
    }

    /// <summary>Done when the file has been opened, or has turned out to need a password or not to open.</summary>
    internal Task Opened => _opened.Task;

    internal PdfEditSession? Session => _session;

    internal IReadOnlyList<PdfPageItem> Items => _items;

    internal IReadOnlyDictionary<string, string> Values => _values;

    internal PdfPageItem? Selected => _selected;

    internal PdfTool ToolNow => _toolNow;

    internal TextBox? Editor => _editor;

    internal bool ConfirmClose { get; set; } = true;

    internal bool IsDirty => _undoStack.Count > 0;

    internal double Zoom => _zoom;

    internal PdfPageView PageView(int index) => _pages[index];

    internal bool NeedsPassword => _message.Visibility == Visibility.Visible && _message.Child is FrameworkElement { Tag: "password" };

    internal IReadOnlyList<FrameworkElement> ControlsOf(string field)
    {
        foreach (var (f, widget) in _tabOrder.Where(t => t.Field.Name == field))
            BuildFields(f.Widgets[widget].Page);
        return _fieldControls.TryGetValue(field, out var controls) ? controls : [];
    }

    internal void AddForSnapshot(PdfPageItem item) => Add(item);

    internal void ZoomForSnapshot(double zoom) => SetZoom(zoom);

    /// <summary>What the Esc key does; false means it closes the window.</summary>
    internal bool Escape() => OnEscape();

    internal void SelectForSnapshot(PdfPageItem item) => Select(item);

    internal void SetFlatten(bool on) => _flatten.IsChecked = on;

    internal void SetRemoveHidden(bool on) => _hidden.IsChecked = on;

    internal string HintText => _hint.Text;

    internal string FooterText => FooterNote;

    // ---------- layout ----------

    private void BuildLayout()
    {
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var strip = new DockPanel { Margin = new Thickness(10, 8, 10, 6) };
        var edit = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        edit.Children.Add(_undo);
        edit.Children.Add(_redo);
        edit.Children.Add(_delete);
        DockPanel.SetDock(edit, Dock.Right);
        strip.Children.Add(edit);
        var tools = new WrapPanel();
        foreach (var group in ToolGroups)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 12, 0) };
            foreach (var (tool, icon, tip) in group)
            {
                var glyph = ThemedIcon(icon, 17);
                glyph.HorizontalAlignment = HorizontalAlignment.Center;
                glyph.VerticalAlignment = VerticalAlignment.Center;
                var tile = new Border { Width = 34, Height = 32, CornerRadius = new CornerRadius(8), Margin = new Thickness(1), Cursor = Cursors.Hand, ToolTip = L.T(tip), Child = glyph, Background = Brushes.Transparent };
                System.Windows.Automation.AutomationProperties.SetName(tile, L.T(tip));
                var chosen = tool;
                tile.MouseLeftButtonUp += async (_, _) => await ChooseToolAsync(chosen);
                _tiles[tool] = tile;
                row.Children.Add(tile);
            }
            tools.Children.Add(row);
        }
        strip.Children.Add(tools);
        _root.Children.Add(strip);

        var optionsRow = new DockPanel { Margin = new Thickness(14, 0, 14, 8), MinHeight = 30 };
        _options.SetValue(DockPanel.DockProperty, Dock.Left);
        optionsRow.Children.Add(_options);
        _hint.Style = (Style)FindResource("Petal.Caption");
        optionsRow.Children.Add(_hint);
        Grid.SetRow(optionsRow, 1);
        _root.Children.Add(optionsRow);

        var middle = new Grid();
        middle.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        middle.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _thumbScroll.Content = _thumbs;
        _thumbScroll.SetResourceReference(BackgroundProperty, "Petal.Control");
        _thumbScroll.ScrollChanged += (_, _) => RenderThumbs();
        middle.Children.Add(_thumbScroll);
        _scroll.SetResourceReference(BackgroundProperty, "Petal.Preview");
        _scroll.Content = _pagesPanel;
        _scroll.ScrollChanged += (_, _) => OnPagesScrolled();
        _scroll.SizeChanged += (_, _) =>
        {
            if (_fitWidth)
                Dispatcher.BeginInvoke(FitWidth, DispatcherPriority.Loaded);
        };
        _scroll.PreviewMouseWheel += OnWheel;
        _pageArea.Children.Add(_scroll);
        _message.SetResourceReference(Border.BackgroundProperty, "Petal.Popup");
        _pageArea.Children.Add(_message);
        Grid.SetColumn(_pageArea, 1);
        middle.Children.Add(_pageArea);
        Grid.SetRow(middle, 2);
        _root.Children.Add(middle);

        var bottom = new DockPanel { Margin = new Thickness(14, 6, 14, 2) };
        var zoom = new StackPanel { Orientation = Orientation.Horizontal, FlowDirection = FlowDirection.LeftToRight };
        var zoomOut = IconButton("zoomOut", "Zoom Out");
        zoomOut.Click += (_, _) => SetZoom(_zoom / 1.2);
        var zoomIn = IconButton("zoomIn", "Zoom In");
        zoomIn.Click += (_, _) => SetZoom(_zoom * 1.2);
        var fit = IconButton("maximize", "Fit the width");
        fit.Click += (_, _) =>
        {
            _fitWidth = _fitAsked = true;
            FitWidth();
        };
        _zoomLabel.Style = (Style)FindResource("Petal.Caption");
        zoom.Children.Add(zoomOut);
        zoom.Children.Add(_zoomLabel);
        zoom.Children.Add(zoomIn);
        zoom.Children.Add(fit);
        DockPanel.SetDock(zoom, Dock.Right);
        bottom.Children.Add(zoom);
        _pageLabel.Style = (Style)FindResource("Petal.Caption");
        DockPanel.SetDock(_pageLabel, Dock.Right);
        bottom.Children.Add(_pageLabel);
        var left = new StackPanel { Orientation = Orientation.Horizontal };
        left.Children.Add(_flatten);
        left.Children.Add(_hidden);
        bottom.Children.Add(left);
        Grid.SetRow(bottom, 3);
        _root.Children.Add(bottom);
    }

    private FrameworkElement Stepper(string label, TextBlock value, Action less, Action more)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 14, 0) };
        row.Children.Add(new TextBlock { Text = L.T(label), Style = (Style)FindResource("Petal.Label"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) });
        var minus = IconButton("minus", label, 13);
        minus.Width = minus.Height = 24;
        minus.Click += (_, _) => less();
        var plus = IconButton("plus", label, 13);
        plus.Width = plus.Height = 24;
        plus.Click += (_, _) => more();
        value.Style = (Style)FindResource("Petal.Caption");
        row.Children.Add(minus);
        row.Children.Add(value);
        row.Children.Add(plus);
        return row;
    }

    // ---------- opening ----------

    private async Task OpenAsync(string? password)
    {
        try
        {
            var file = _file;
            var session = await Task.Run(() => PdfEditSession.Open(file, password));
            _session = session;
            _fields = session.Fields.ToList();
            foreach (var field in _fields)
                for (int i = 0; i < field.Widgets.Count; i++)
                    _tabOrder.Add((field, i));
            HideMessage();
            BuildPages();
            ApplyRights(session);
        }
        catch (PdfOpenException ex) when (ex.NeedsPassword)
        {
            AskPassword(password is not null);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Error(ex, "Opening PDF to edit");
            ShowMessage(L.T("Couldn't open this PDF."));
        }
        _opened.TrySetResult();
    }

    private void AskPassword(bool wrong)
    {
        var field = new PasswordField();
        var panel = new StackPanel { Tag = "password" };
        var heading = Text("This PDF is protected by a password.", "Petal.Section");
        heading.TextWrapping = TextWrapping.Wrap;
        panel.Children.Add(heading);
        if (wrong)
        {
            var problem = Text("That password doesn't open this PDF.", "Petal.Caption");
            problem.SetResourceReference(TextBlock.ForegroundProperty, "Petal.Danger");
            problem.Margin = new Thickness(0, 6, 0, 0);
            panel.Children.Add(problem);
        }
        field.Margin = new Thickness(0, 10, 0, 0);
        panel.Children.Add(field);
        var open = new Button { Content = L.T("Open"), Style = (Style)FindResource("Petal.PrimaryButton"), HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0), MinWidth = 88 };
        async Task Try() => await OpenWithPasswordAsync(field.Password);
        open.Click += async (_, _) => await Try();
        field.KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                await Try();
            }
        };
        panel.Children.Add(open);
        _message.Child = panel;
        _message.Visibility = Visibility.Visible;
        field.FocusInput();
    }

    /// <summary>Opens the file with a password, as the Open button does.</summary>
    internal Task OpenWithPasswordAsync(string password) => OpenAsync(password);

    private void ShowMessage(string text)
    {
        var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Style = (Style)FindResource("Petal.Body") };
        _message.Child = block;
        _message.Visibility = Visibility.Visible;
        PrimaryButton.IsEnabled = false;
    }

    private void HideMessage()
    {
        _message.Child = null;
        _message.Visibility = Visibility.Collapsed;
    }

    /// <summary>What the file's author allows decides what can be done here; and what the file is decides what the footer warns of.</summary>
    private void ApplyRights(PdfEditSession session)
    {
        bool Allowed(PdfTool tool) => session.Rights switch
        {
            PdfEditRights.Full => true,
            PdfEditRights.Comment => tool is PdfTool.Select or PdfTool.Highlight or PdfTool.Underline or PdfTool.StrikeOut or PdfTool.Note or PdfTool.Pen or PdfTool.Line or PdfTool.Arrow or PdfTool.Rectangle or PdfTool.Ellipse,
            PdfEditRights.Fill => tool == PdfTool.Select,
            _ => tool == PdfTool.Select,
        };
        foreach (var (tool, tile) in _tiles)
        {
            bool allowed = Allowed(tool);
            tile.IsEnabled = allowed;
            tile.Opacity = allowed ? 1 : 0.35;
        }
        _flatten.IsEnabled = session.Rights == PdfEditRights.Full;
        PrimaryButton.IsEnabled = session.Rights != PdfEditRights.None;
        var note = session.Rights switch
        {
            PdfEditRights.None => "Its author has locked this PDF against changes.",
            PdfEditRights.Fill => "Its author allows filling in this form and nothing else.",
            PdfEditRights.Comment => "Its author allows filling in and comments, and nothing else.",
            _ => session.IsXfaOnly ? "This form is built with XFA, which can't be filled in here. You can still write on its pages."
                : session.IsSigned ? "This PDF is signed: an edited copy no longer carries a valid signature."
                : null,
        };
        FooterNote = note is null ? Path.GetFileName(OutputNaming.ForTool(_file, _tool)) : L.T(note);
    }

    private void BuildPages()
    {
        var session = _session!;
        for (int i = 0; i < session.Pages.Count; i++)
        {
            int index = i;
            var page = new PdfPageView(i, session.Pages[i].Width, session.Pages[i].Height, DrawPage, LayerWants);
            page.MouseLeftButtonDown += (_, e) => OnPress(page, e);
            page.MouseMove += (_, e) => OnMove(page, e);
            page.MouseLeftButtonUp += (_, e) => OnRelease(page, e);
            _pages.Add(page);
            _pagesPanel.Children.Add(page);

            double k = 96.0 / Math.Max(session.Pages[i].Width, session.Pages[i].Height);
            var thumb = new Image { Stretch = Stretch.Fill };
            var paper = new Border { Width = session.Pages[i].Width * k, Height = session.Pages[i].Height * k, Background = Brushes.White, Child = thumb, HorizontalAlignment = HorizontalAlignment.Center, FlowDirection = FlowDirection.LeftToRight };
            var number = new TextBlock { Text = (i + 1).ToString(CultureInfo.CurrentCulture), Style = (Style)FindResource("Petal.Caption"), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 3, 0, 0) };
            var stack = new StackPanel();
            stack.Children.Add(paper);
            stack.Children.Add(number);
            var cell = new Border { Padding = new Thickness(6, 5, 6, 3), Margin = new Thickness(4, 1, 4, 1), CornerRadius = new CornerRadius(6), Cursor = Cursors.Hand, Child = stack, Background = Brushes.Transparent };
            cell.MouseLeftButtonUp += (_, _) => ScrollToPage(index);
            _thumbCells.Add(cell);
            _thumbs.Children.Add(cell);
        }
        _fitWidth = true;
        _thumbScroll.Visibility = session.Pages.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        Dispatcher.BeginInvoke(FitWidth, DispatcherPriority.Loaded);
    }

    // ---------- zoom and scrolling ----------

    private bool _fitWidth, _fitAsked;

    private void FitWidth()
    {
        if (_session is null || _session.Pages.Count == 0)
            return;
        double room = _scroll.ActualWidth - 16 * 2 - SystemParameters.VerticalScrollBarWidth - 4;
        if (room <= 0)
            return;
        double fit = room / _session.Pages.Max(p => p.Width);
        ApplyZoom(_fitAsked ? fit : Math.Min(fit, OpeningZoom));
    }

    private void SetZoom(double zoom)
    {
        _fitWidth = false;
        ApplyZoom(zoom);
    }

    private void ApplyZoom(double zoom)
    {
        zoom = Math.Clamp(zoom, MinZoom, MaxZoom);
        // The same part of the page stays in view.
        double along = _scroll.ExtentHeight > 0 ? _scroll.VerticalOffset / _scroll.ExtentHeight : 0;
        _zoom = zoom;
        foreach (var page in _pages)
        {
            page.SetZoom(zoom);
            if (page.FieldsBuilt)
                PlaceFields(page);
        }
        _zoomLabel.Text = (zoom * 100 * 72 / 96).ToString("0", CultureInfo.CurrentCulture) + "%";
        CommitEditor();
        _pagesPanel.UpdateLayout();
        _scroll.UpdateLayout();
        _scroll.ScrollToVerticalOffset(along * _scroll.ExtentHeight);
        _settle.Stop();
        _settle.Start();
    }

    private void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            return;
        e.Handled = true;
        SetZoom(e.Delta > 0 ? _zoom * 1.1 : _zoom / 1.1);
    }

    private void OnPagesScrolled()
    {
        BuildVisible();
        _settle.Stop();
        _settle.Start();
        ShowCurrentPage();
    }

    private double PageTop(int index)
    {
        double top = _pagesPanel.Margin.Top;
        for (int i = 0; i < index; i++)
            top += _pages[i].ActualHeight + _pages[i].Margin.Bottom;
        return top;
    }

    private (int First, int Last) VisiblePages()
    {
        double top = _scroll.VerticalOffset, bottom = top + _scroll.ViewportHeight, at = _pagesPanel.Margin.Top;
        int first = -1, last = -1;
        for (int i = 0; i < _pages.Count; i++)
        {
            double height = _pages[i].Height + _pages[i].Margin.Bottom;
            if (at + height >= top && at <= bottom)
            {
                if (first < 0)
                    first = i;
                last = i;
            }
            at += height;
        }
        return first < 0 ? (0, Math.Min(0, _pages.Count - 1)) : (first, last);
    }

    private void BuildVisible()
    {
        if (_pages.Count == 0)
            return;
        var (first, last) = VisiblePages();
        for (int i = Math.Max(0, first - 1); i <= Math.Min(_pages.Count - 1, last + 1); i++)
        {
            _pages[i].Build();
            BuildFields(i);
        }
    }

    /// <summary>Once the view has stopped moving: draws what is in view, and lets go of the pictures of pages far from it.</summary>
    private void Settle()
    {
        _settle.Stop();
        if (_session is null || _pages.Count == 0)
            return;
        var (first, last) = VisiblePages();
        for (int i = 0; i < _pages.Count; i++)
        {
            var page = _pages[i];
            if (!page.IsBuilt)
                continue;
            if (i >= first - 1 && i <= last + 1)
                _ = RenderPageAsync(page, first, last);
            else if (i < first - 6 || i > last + 6)
            {
                page.Picture.Source = null;
                page.PictureWidth = 0;
            }
        }
        RenderThumbs();
    }

    private async Task RenderPageAsync(PdfPageView page, int first, int last)
    {
        var session = _session;
        if (session is null)
            return;
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        int width = (int)Math.Ceiling(page.WidthInPoints * _zoom * scale);
        if (page.PictureWidth == width)
            return;
        page.PictureWidth = width;
        await _renderGate.WaitAsync();
        try
        {
            if (page.PictureWidth != width)
                return;
            int index = page.Index;
            var bitmap = await Task.Run(() => session.Render(index, width));
            if (page.PictureWidth != width)
                return;
            var source = BitmapSource.Create(bitmap.Width, bitmap.Height, 96, 96, PixelFormats.Pbgra32, null, bitmap.Pixels, bitmap.Stride);
            source.Freeze();
            page.Picture.Source = source;
            _ = Task.Run(() => session.Lines(index));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Error(ex, "Drawing a PDF page");
        }
        finally
        {
            _renderGate.Release();
        }
    }

    private void RenderThumbs()
    {
        if (_session is null || _thumbScroll.Visibility != Visibility.Visible)
            return;
        double top = _thumbScroll.VerticalOffset - 200, bottom = _thumbScroll.VerticalOffset + _thumbScroll.ViewportHeight + 200, at = 0;
        for (int i = 0; i < _thumbCells.Count; i++)
        {
            double height = _thumbCells[i].ActualHeight + 2;
            if (at + height >= top && at <= bottom && ((Border)((StackPanel)_thumbCells[i].Child).Children[0]).Child is Image { Source: null } image)
                _ = RenderThumbAsync(i, image);
            at += height;
        }
    }

    private async Task RenderThumbAsync(int index, Image image)
    {
        var session = _session;
        if (session is null)
            return;
        // Marked as asked for at once, so a scroll doesn't ask again.
        image.Source = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Pbgra32, null, new byte[] { 255, 255, 255, 255 }, 4);
        await _renderGate.WaitAsync();
        try
        {
            var bitmap = await Task.Run(() => session.Render(index, 180));
            var source = BitmapSource.Create(bitmap.Width, bitmap.Height, 96, 96, PixelFormats.Pbgra32, null, bitmap.Pixels, bitmap.Stride);
            source.Freeze();
            image.Source = source;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Error(ex, "Drawing a PDF thumbnail");
        }
        finally
        {
            _renderGate.Release();
        }
    }

    private void ScrollToPage(int index)
    {
        if (index < 0 || index >= _pages.Count)
            return;
        _scroll.ScrollToVerticalOffset(PageTop(index) - 8);
    }

    private void ShowCurrentPage()
    {
        if (_pages.Count == 0)
            return;
        double middle = _scroll.VerticalOffset + _scroll.ViewportHeight / 2, at = _pagesPanel.Margin.Top;
        int current = _pages.Count - 1;
        for (int i = 0; i < _pages.Count; i++)
        {
            at += _pages[i].Height + _pages[i].Margin.Bottom;
            if (at >= middle)
            {
                current = i;
                break;
            }
        }
        _pageLabel.Text = L.F("Page {0} of {1}", current + 1, _pages.Count);
        for (int i = 0; i < _thumbCells.Count; i++)
        {
            if (i == current)
                _thumbCells[i].SetResourceReference(Border.BackgroundProperty, "Petal.AccentSoft");
            else
                _thumbCells[i].Background = Brushes.Transparent;
        }
    }

    // ---------- tools ----------

    private async Task ChooseToolAsync(PdfTool tool)
    {
        if (!_tiles[tool].IsEnabled)
            return;
        if (tool == PdfTool.Picture && !await PickPictureAsync())
            return;
        if (tool == PdfTool.Signature && !PickSignature())
            return;
        SelectTool(tool);
    }

    /// <summary>Goes to a tool; Esc goes back to Select.</summary>
    internal void SelectTool(PdfTool tool)
    {
        CommitEditor();
        _toolNow = tool;
        foreach (var (t, tile) in _tiles)
        {
            if (t == tool)
                tile.SetResourceReference(Border.BackgroundProperty, "Petal.AccentSoft");
            else
                tile.Background = Brushes.Transparent;
        }
        if (tool != PdfTool.Select)
            Select(null);
        Cursor = null;
        _scroll.Cursor = tool switch
        {
            PdfTool.Select => null,
            PdfTool.Text or PdfTool.Retype or PdfTool.Highlight or PdfTool.Underline or PdfTool.StrikeOut => Cursors.IBeam,
            PdfTool.Pen => Cursors.Pen,
            _ => Cursors.Cross,
        };
        SyncOptions();
        foreach (var page in _pages.Where(p => p.IsBuilt))
            page.Layer.InvalidateVisual();
    }

    /// <summary>The options strip shows what the tool or the selected item has.</summary>
    private void SyncOptions()
    {
        bool text = _selected is PdfTextItem || (_selected is null && _toolNow is PdfTool.Text or PdfTool.Date);
        bool retype = _selected is PdfReplaceItem;
        bool lines = _selected is PdfInkItem or PdfLineItem or PdfShapeItem || (_selected is null && _toolNow is PdfTool.Pen or PdfTool.Line or PdfTool.Arrow or PdfTool.Rectangle or PdfTool.Ellipse);
        bool colour = text || lines || retype || _selected is PdfMarkItem or PdfMarkupItem or PdfNoteItem or PdfCoverItem
            || (_selected is null && _toolNow is PdfTool.Check or PdfTool.Cross or PdfTool.Dot or PdfTool.Highlight or PdfTool.Underline or PdfTool.StrikeOut or PdfTool.Note or PdfTool.Cover);
        bool redact = _toolNow == PdfTool.Redact;
        _sizeControl.Visibility = text || retype ? Visibility.Visible : Visibility.Collapsed;
        _bold.Visibility = text || retype ? Visibility.Visible : Visibility.Collapsed;
        _weightControl.Visibility = lines ? Visibility.Visible : Visibility.Collapsed;
        _colourControl.Visibility = colour ? Visibility.Visible : Visibility.Collapsed;
        _redactBar.Visibility = redact ? Visibility.Visible : Visibility.Collapsed;
        _hidden.Visibility = redact || _items.Any(i => i is PdfRedactItem) ? Visibility.Visible : Visibility.Collapsed;
        double size = _selected switch { PdfTextItem t => t.Size, PdfReplaceItem r => r.Size, _ => _size };
        double weight = _selected switch { PdfInkItem i => i.Weight, PdfLineItem l => l.Weight, PdfShapeItem s => s.Weight, _ => _weight };
        _sizeValue.Text = size.ToString("0.#", CultureInfo.CurrentCulture);
        _weightValue.Text = weight.ToString("0.#", CultureInfo.CurrentCulture);
        _bold.IsChecked = _selected switch { PdfTextItem t => t.Bold, PdfReplaceItem r => r.Bold, _ => _boldNow };
        int? shown = _selected switch
        {
            PdfTextItem t => t.Colour,
            PdfReplaceItem r => r.Colour,
            PdfMarkItem m => m.Colour,
            PdfMarkupItem m => m.Colour,
            PdfNoteItem n => n.Colour,
            PdfInkItem i => i.Colour,
            PdfLineItem l => l.Colour,
            PdfShapeItem s => s.Colour,
            PdfCoverItem c => c.Colour,
            _ => _colours.TryGetValue(_toolNow, out var c) ? c : null,
        };
        if (shown is { } rgb)
            _colourWell.Color = ColourOf(rgb);
        _delete.IsEnabled = _selected is not null;
        _undo.IsEnabled = _undoStack.Count > 0;
        _redo.IsEnabled = _redoStack.Count > 0;
        if (!_findingStatus)
            _hint.Text = L.T(Hints[_toolNow]);
    }

    private bool _boldNow;
    private bool _findingStatus;

    private void ChangeSize(double by)
    {
        if (_selected is PdfTextItem text)
            Replace(text, text with { Size = Math.Clamp(text.Size + by, 5, 96) });
        else if (_selected is PdfReplaceItem retyped)
            Replace(retyped, retyped with { Size = Math.Clamp(retyped.Size + by, 5, 96) });
        else
            _size = Math.Clamp(_size + by, 5, 96);
        SyncOptions();
    }

    private void ChangeWeight(double by)
    {
        double Next(double w) => Math.Clamp(w + by, 0.5, 12);
        switch (_selected)
        {
            case PdfInkItem i: Replace(i, i with { Weight = Next(i.Weight) }); break;
            case PdfLineItem l: Replace(l, l with { Weight = Next(l.Weight) }); break;
            case PdfShapeItem s: Replace(s, s with { Weight = Next(s.Weight) }); break;
            default: _weight = Next(_weight); break;
        }
        SyncOptions();
    }

    private void ChangeBold(bool bold)
    {
        if (_selected is PdfTextItem text)
            Replace(text, text with { Bold = bold });
        else if (_selected is PdfReplaceItem retyped)
            Replace(retyped, retyped with { Bold = bold });
        else
            _boldNow = bold;
    }

    private void ChangeColour(int rgb)
    {
        switch (_selected)
        {
            case PdfTextItem t: Replace(t, t with { Colour = rgb }); break;
            case PdfReplaceItem r: Replace(r, r with { Colour = rgb }); break;
            case PdfMarkItem m: Replace(m, m with { Colour = rgb }); break;
            case PdfMarkupItem m: Replace(m, m with { Colour = rgb }); break;
            case PdfNoteItem n: Replace(n, n with { Colour = rgb }); break;
            case PdfInkItem i: Replace(i, i with { Colour = rgb }); break;
            case PdfLineItem l: Replace(l, l with { Colour = rgb }); break;
            case PdfShapeItem s: Replace(s, s with { Colour = rgb }); break;
            case PdfCoverItem c: Replace(c, c with { Colour = rgb }); break;
            default: _colours[_toolNow] = rgb; break;
        }
    }

    private int ColourNow() => _colours.TryGetValue(_toolNow, out var rgb) ? rgb : 0;

    private static int Rgb(Color c) => (c.R << 16) | (c.G << 8) | c.B;

    private static Color ColourOf(int rgb) => Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

    // ---------- undo ----------

    private void Remember()
    {
        _undoStack.Push((_items.ToList(), new Dictionary<string, string>(_values)));
        _redoStack.Clear();
        _editingField = null;
    }

    internal void Undo()
    {
        CommitEditor();
        if (_undoStack.Count == 0)
            return;
        _redoStack.Push((_items, _values));
        (_items, _values) = _undoStack.Pop();
        AfterHistory();
    }

    internal void Redo()
    {
        CommitEditor();
        if (_redoStack.Count == 0)
            return;
        _undoStack.Push((_items, _values));
        (_items, _values) = _redoStack.Pop();
        AfterHistory();
    }

    private void AfterHistory()
    {
        _selected = null;
        _editingField = null;
        SyncFields();
        Changed();
    }

    /// <summary>One item changed for another; undoable, and still selected if it was.</summary>
    private void Replace(PdfPageItem old, PdfPageItem updated)
    {
        int at = _items.FindIndex(i => ReferenceEquals(i, old));
        if (at < 0)
            return;
        Remember();
        _items[at] = updated;
        if (ReferenceEquals(_selected, old))
            _selected = updated;
        Changed();
    }

    private void Add(PdfPageItem item, bool select = true)
    {
        Remember();
        _items.Add(item);
        if (select)
            _selected = item;
        Changed();
    }

    private void Select(PdfPageItem? item)
    {
        _selected = item;
        SyncOptions();
        foreach (var page in _pages.Where(p => p.IsBuilt))
            page.Layer.InvalidateVisual();
    }

    internal void DeleteSelected()
    {
        if (_selected is null)
            return;
        Remember();
        _items.RemoveAll(i => ReferenceEquals(i, _selected));
        _selected = null;
        Changed();
    }

    private void Changed()
    {
        SyncOptions();
        foreach (var page in _pages.Where(p => p.IsBuilt))
            page.Layer.InvalidateVisual();
    }

    // ---------- keys ----------

    private void OnPreviewKey(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        e.Handled = HandleKey(key, Keyboard.Modifiers, Keyboard.FocusedElement);
    }

    /// <summary>The window's keys. True when one was used here.</summary>
    internal bool HandleKey(Key key, ModifierKeys modifiers, object? focused)
    {
        bool control = modifiers.HasFlag(ModifierKeys.Control);
        // Words being typed on a page keep their own keys, except the ones that end it.
        bool typing = _editor is not null && ReferenceEquals(focused, _editor);
        if (control && key == Key.S)
        {
            CommitEditor();
            if (PrimaryButton.IsEnabled)
                _ = SaveAsync();
            return true;
        }
        if (key == Key.Tab && !typing && TabToField(modifiers.HasFlag(ModifierKeys.Shift), focused))
            return true;
        if (typing)
            return false;
        if (control && key == Key.Z)
        {
            Undo();
            return true;
        }
        if (control && key == Key.Y)
        {
            Redo();
            return true;
        }
        if (key == Key.Delete && focused is not TextBox and not ComboBox)
        {
            if (_selected is null)
                return false;
            DeleteSelected();
            return true;
        }
        return false;
    }

    protected override bool OnEscape()
    {
        if (_editor is null && _toolNow == PdfTool.Select && _selected is null)
            return false;
        CommitEditor();
        SelectTool(PdfTool.Select);
        Select(null);
        return true;
    }

    // ---------- the mouse on a page ----------

    private bool LayerWants(int index, Point p) => _toolNow != PdfTool.Select || ItemAt(index, p) is not null || HandleAt(index, p) >= 0;

    private void OnPress(PdfPageView view, MouseButtonEventArgs e)
    {
        Press(view.Index, view.ToPage(e.GetPosition(view)), e.ClickCount);
        view.CaptureMouse();
    }

    private void OnMove(PdfPageView view, MouseEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed && _pressed)
            Drag(view.ToPage(e.GetPosition(view)));
    }

    private void OnRelease(PdfPageView view, MouseButtonEventArgs e)
    {
        if (view.IsMouseCaptured)
            view.ReleaseMouseCapture();
        Release(view.ToPage(e.GetPosition(view)));
    }

    /// <summary>A press on a page, in points on the page as displayed. What the mouse handlers call, and the tests with them.</summary>
    internal void Press(int index, Point p, int clicks = 1)
    {
        CommitEditor();
        _pages[index].Build();
        _activePage = index;
        _pressedAt = _lastAt = p;
        _moved = false;
        _pressed = true;
        _draft = null;
        _handle = -1;
        _beforeDrag = null;
        double w = _pages[index].WidthInPoints, h = _pages[index].HeightInPoints;
        switch (_toolNow)
        {
            case PdfTool.Select:
                _handle = HandleAt(index, p);
                if (_handle >= 0)
                {
                    _beforeDrag = _selected;
                    return;
                }
                Select(ItemAt(index, p));
                _beforeDrag = _selected;
                if (clicks == 2 && _selected is PdfTextItem or PdfNoteItem or PdfReplaceItem)
                    EditSelected();
                return;
            case PdfTool.Pen:
                _draft = new PdfInkItem(index, [new List<(double, double)> { (p.X, p.Y) }], _weight, ColourNow());
                return;
            case PdfTool.Line or PdfTool.Arrow:
                _draft = new PdfLineItem(index, p.X, p.Y, p.X, p.Y, _toolNow == PdfTool.Arrow, _weight, ColourNow());
                return;
            case PdfTool.Rectangle or PdfTool.Ellipse:
                _draft = new PdfShapeItem(index, new PdfArea(p.X, p.Y, 0, 0), _toolNow == PdfTool.Ellipse, _weight, ColourNow());
                return;
            case PdfTool.Cover:
                _draft = new PdfCoverItem(index, new PdfArea(p.X, p.Y, 0, 0), ColourNow());
                return;
            case PdfTool.Redact when _redactWords.Value == false:
                _draft = new PdfRedactItem(index, new PdfArea(p.X, p.Y, 0, 0));
                return;
            case PdfTool.Highlight or PdfTool.Underline or PdfTool.StrikeOut:
            case PdfTool.Redact:
                _draft = MarkupDraft(index, p, p);
                return;
        }
    }

    /// <summary>The mouse moved with the button down.</summary>
    internal void Drag(Point p)
    {
        if (!_pressed || _activePage < 0)
            return;
        if (Math.Abs(p.X - _pressedAt.X) + Math.Abs(p.Y - _pressedAt.Y) > 2 / _zoom)
            _moved = true;
        var view = _pages[_activePage];
        if (_toolNow == PdfTool.Select)
        {
            if (_selected is not null && _moved)
            {
                _selected = _handle >= 0 ? Resized(_selected, _handle, p) : Moved(_selected, p.X - _lastAt.X, p.Y - _lastAt.Y);
                _lastAt = p;
                view.Layer.InvalidateVisual();
            }
            return;
        }
        switch (_draft)
        {
            case PdfInkItem ink:
                var stroke = (List<(double X, double Y)>)ink.Strokes[0];
                if (Math.Abs(stroke[^1].X - p.X) + Math.Abs(stroke[^1].Y - p.Y) > 1 / _zoom)
                    stroke.Add((p.X, p.Y));
                break;
            case PdfLineItem line:
                _draft = line with { X2 = p.X, Y2 = p.Y };
                break;
            case PdfShapeItem shape:
                _draft = shape with { Area = Dragged(p) };
                break;
            case PdfCoverItem cover:
                _draft = cover with { Area = Dragged(p) };
                break;
            case PdfRedactItem redact:
                _draft = redact with { Area = Dragged(p) };
                break;
            case PdfMarkupItem or null when _toolNow is PdfTool.Highlight or PdfTool.Underline or PdfTool.StrikeOut or PdfTool.Redact:
                _draft = MarkupDraft(_activePage, _pressedAt, p);
                break;
        }
        _lastAt = p;
        view.Layer.InvalidateVisual();
    }

    private PdfArea Dragged(Point p) => PdfArea.Between(_pressedAt.X, _pressedAt.Y, p.X, p.Y);

    /// <summary>The button came up: a drag is finished, or it was a click.</summary>
    internal void Release(Point p)
    {
        if (!_pressed || _activePage < 0)
            return;
        Drag(p);
        _pressed = false;
        int index = _activePage;
        if (_toolNow == PdfTool.Select)
        {
            if (_selected is null || _beforeDrag is null)
                return;
            if (_moved)
            {
                // The drag showed its item as it went; for undo it is one change.
                var moved = _selected;
                _selected = _beforeDrag;
                Replace(_beforeDrag, moved);
            }
            else if (_handle < 0 && _selected is PdfTextItem or PdfNoteItem or PdfReplaceItem)
                EditSelected();
            return;
        }
        var draft = _draft;
        _draft = null;
        switch (_toolNow)
        {
            case PdfTool.Text:
                BeginNewText(index, p);
                return;
            case PdfTool.Date:
                PlaceDate(index, p);
                return;
            case PdfTool.Check or PdfTool.Cross or PdfTool.Dot:
                Add(new PdfMarkItem(index, new PdfArea(p.X - DefaultMark / 2, p.Y - DefaultMark / 2, DefaultMark, DefaultMark),
                    _toolNow == PdfTool.Check ? PdfMark.Check : _toolNow == PdfTool.Cross ? PdfMark.Cross : PdfMark.Dot, ColourNow()));
                return;
            case PdfTool.Retype:
                BeginRetype(index, p);
                return;
            case PdfTool.Note:
                BeginNote(index, p);
                return;
            case PdfTool.Picture or PdfTool.Signature:
                PlacePending(index, p);
                return;
        }
        switch (draft)
        {
            case PdfInkItem ink when ink.Strokes[0].Count > 1:
            case PdfLineItem line when Math.Abs(line.X2 - line.X1) + Math.Abs(line.Y2 - line.Y1) > 4:
            case PdfShapeItem shape when shape.Area.Width >= 3 && shape.Area.Height >= 3:
            case PdfCoverItem cover when cover.Area.Width >= 3 && cover.Area.Height >= 3:
                Add(draft!, select: false);
                break;
            case PdfRedactItem redact when redact.Area.Width >= 3 && redact.Area.Height >= 3:
                Add(draft!, select: false);
                break;
            case PdfMarkupItem markup when markup.Areas.Count > 0:
                if (_toolNow == PdfTool.Redact)
                {
                    Remember();
                    foreach (var area in markup.Areas)
                        _items.Add(new PdfRedactItem(index, area));
                    Changed();
                }
                else
                    Add(draft!, select: false);
                break;
            default:
                _pages[index].Layer.InvalidateVisual();
                break;
        }
    }

    private void EditSelected()
    {
        int page = _selected!.Page;
        switch (_selected)
        {
            case PdfTextItem text:
                OpenEditor(page, text.Area, text.Text, text.Size, text.Colour, text.Bold, true, value => CommitText(text, value));
                break;
            case PdfReplaceItem retyped:
                OpenEditor(page, new PdfArea(retyped.Area.Left, retyped.Area.Top, Math.Max(retyped.Area.Width + 40, 80), retyped.Area.Height), retyped.Text, retyped.Size, retyped.Colour, retyped.Bold, false,
                    value => Replace(retyped, retyped with { Text = value }));
                break;
            case PdfNoteItem note:
                OpenNoteEditor(note.Page, note.Area, note.Text, value => { if (value.Length == 0) { _selected = note; DeleteSelected(); } else Replace(note, note with { Text = value }); });
                break;
        }
    }

    private void CommitText(PdfTextItem item, string value)
    {
        if (value.Trim().Length == 0)
        {
            _selected = item;
            DeleteSelected();
            return;
        }
        Replace(item, item with { Text = value, Area = item.Area with { Height = Math.Max(item.Area.Height, HeightOf(value, item.Size, item.Bold, item.Area.Width)) } });
    }

    private void BeginNewText(int index, Point p)
    {
        double width = Math.Clamp(_pages[index].WidthInPoints - p.X - 6, 40, 280);
        double top = Math.Max(0, p.Y - _size * 0.7);
        var area = new PdfArea(p.X, top, width, _size * PdfEditorDrawing.LineStep + 2);
        int colour = ColourNow();
        bool bold = _boldNow;
        double size = _size;
        OpenEditor(index, area, "", size, colour, bold, true, value =>
        {
            if (value.Trim().Length > 0)
                Add(new PdfTextItem(index, area with { Height = HeightOf(value, size, bold, width) }, value, size, colour, bold));
        });
    }

    private double HeightOf(string text, double size, bool bold, double width)
    {
        var formatted = PdfEditorDrawing.Formatted(text, size, bold, 0, width);
        return Math.Max(size * PdfEditorDrawing.LineStep, formatted.Height) + 1;
    }

    /// <summary>Today, as dates are written in the interface's language.</summary>
    internal static string TodayText() => DateTime.Now.ToString("d", L.Culture);

    private void PlaceDate(int index, Point p)
    {
        var text = TodayText();
        var formatted = PdfEditorDrawing.Formatted(text, _size, _boldNow, 0, 2000);
        double width = Math.Min(formatted.WidthIncludingTrailingWhitespace + 6, _pages[index].WidthInPoints - p.X);
        Add(new PdfTextItem(index, new PdfArea(p.X, Math.Max(0, p.Y - _size * 0.7), Math.Max(20, width), _size * PdfEditorDrawing.LineStep + 1), text, _size, ColourNow(), _boldNow));
    }

    private void BeginRetype(int index, Point p)
    {
        if (_items.OfType<PdfReplaceItem>().LastOrDefault(r => r.Page == index && r.Area.Inflated(1).Contains(p.X, p.Y)) is { } again)
        {
            _selected = again;
            EditSelected();
            return;
        }
        var line = _session?.Lines(index).FirstOrDefault(l => l.Area.Inflated(1.5).Contains(p.X, p.Y));
        if (line is null)
        {
            if (_session is { } session && session.Lines(index).Count == 0)
                ShowStatus(L.T("This page has no text of its own: it is a picture. Use Write text and Whiteout instead."));
            return;
        }
        var background = BackgroundOf(index, line.Area);
        OpenEditor(index, new PdfArea(line.Area.Left, line.Area.Top, Math.Max(line.Area.Width + 40, 80), line.Area.Height), line.Text, line.Size, line.Colour, line.Bold, false, value =>
        {
            if (value == line.Text)
                return;
            Add(new PdfReplaceItem(index, line.Area, line.Words.Select(w => w.Area).ToList(), value, line.Size, line.Colour, line.Bold, line.Italic, line.FontName, line.RightToLeft, background));
        });
    }

    private void ShowStatus(string text)
    {
        _findingStatus = true;
        _hint.Text = text;
        _findingStatus = false;
    }

    /// <summary>The colour behind some words, from the picture on screen.</summary>
    private int BackgroundOf(int index, PdfArea area)
    {
        if (_pages[index].Picture?.Source is not BitmapSource source)
            return 0xFFFFFF;
        try
        {
            double k = source.PixelWidth / _pages[index].WidthInPoints;
            int y = Math.Clamp((int)((area.Top - 1) * k), 0, source.PixelHeight - 1);
            int x = Math.Clamp((int)(area.Left * k), 0, source.PixelWidth - 1);
            int width = Math.Clamp((int)(area.Width * k), 1, source.PixelWidth - x);
            var row = new byte[width * 4];
            source.CopyPixels(new Int32Rect(x, y, width, 1), row, width * 4, 0);
            var blues = new List<byte>();
            var greens = new List<byte>();
            var reds = new List<byte>();
            for (int i = 0; i < width; i++)
            {
                blues.Add(row[i * 4]);
                greens.Add(row[i * 4 + 1]);
                reds.Add(row[i * 4 + 2]);
            }
            blues.Sort();
            greens.Sort();
            reds.Sort();
            return (reds[width / 2] << 16) | (greens[width / 2] << 8) | blues[width / 2];
        }
        catch (ArgumentException)
        {
            return 0xFFFFFF;
        }
    }

    private void BeginNote(int index, Point p)
    {
        const double side = 22;
        var area = new PdfArea(Math.Clamp(p.X - side / 2, 0, Math.Max(0, _pages[index].WidthInPoints - side)), Math.Clamp(p.Y - side / 2, 0, Math.Max(0, _pages[index].HeightInPoints - side)), side, side);
        int colour = ColourNow();
        OpenNoteEditor(index, area, "", value =>
        {
            if (value.Trim().Length > 0)
                Add(new PdfNoteItem(index, area, value, colour));
        });
    }

    private void PlacePending(int index, Point p)
    {
        if (_pendingPicture is not { } picture)
            return;
        PlacePicture(index, p, picture);
    }

    /// <summary>A picture or a signature on a page, its middle at a point, and the tool back to Select so it can be moved and sized.</summary>
    internal void PlacePicture(int index, Point p, BitmapSource picture)
    {
        double pageWidth = _pages[index].WidthInPoints;
        double width = _toolNow == PdfTool.Signature ? Math.Min(150, pageWidth / 3) : Math.Min(200, pageWidth / 2);
        double height = width * picture.PixelHeight / Math.Max(1, picture.PixelWidth);
        var png = Png(picture);
        var item = new PdfPictureItem(index, new PdfArea(p.X - width / 2, p.Y - height / 2, width, height), png);
        _toolNow = PdfTool.Select;
        SelectTool(PdfTool.Select);
        Add(item);
    }

    internal static byte[] Png(BitmapSource picture)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(picture));
        using var memory = new MemoryStream();
        encoder.Save(memory);
        return memory.ToArray();
    }

    private async Task<bool> PickPictureAsync()
    {
        var dialog = new OpenFileDialog { Filter = L.T("Images") + "|*.png;*.jpg;*.jpeg;*.webp;*.bmp;*.gif;*.tif;*.tiff" };
        if (dialog.ShowDialog(this) != true)
            return false;
        try
        {
            _pendingPicture = await ImageLoader.LoadAsync(dialog.FileName, 2400);
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Error(ex, "Loading a picture for a PDF");
            ShowStatus(L.T("Couldn't open this image."));
            return false;
        }
    }

    private bool PickSignature()
    {
        var dialog = new SignatureWindow { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Result is null)
            return false;
        _pendingPicture = dialog.Result;
        return true;
    }

    /// <summary>The words a drag has gone across: from the word at its start to the word at its end, in reading order, as one rectangle per line. On a page without text, its rectangle.</summary>
    private PdfMarkupItem? MarkupDraft(int index, Point from, Point to)
    {
        var kind = _toolNow switch { PdfTool.Underline => PdfMarkup.Underline, PdfTool.StrikeOut => PdfMarkup.StrikeOut, _ => PdfMarkup.Highlight };
        int colour = _toolNow == PdfTool.Redact ? Danger : ColourNow();
        var lines = _session?.Lines(index) ?? [];
        var words = lines.SelectMany((l, i) => l.Words.Select(w => (Line: i, Word: w))).ToList();
        if (words.Count == 0)
        {
            var area = PdfArea.Between(from.X, from.Y, to.X, to.Y);
            return area.Width < 2 || area.Height < 2 ? null : new PdfMarkupItem(index, kind, [area], colour);
        }
        int Nearest(Point p)
        {
            int best = 0;
            double least = double.MaxValue;
            for (int i = 0; i < words.Count; i++)
            {
                var a = words[i].Word.Area;
                double dx = Math.Max(0, Math.Max(a.Left - p.X, p.X - a.Right)), dy = Math.Max(0, Math.Max(a.Top - p.Y, p.Y - a.Bottom));
                double distance = dx + dy * 2;
                if (distance < least)
                {
                    least = distance;
                    best = i;
                }
            }
            return best;
        }
        // Only words actually reached: a click on empty page marks nothing.
        var near = words.Select(w => w.Word.Area.Inflated(3));
        if (!near.Any(a => a.Contains(from.X, from.Y)) && !near.Any(a => a.Contains(to.X, to.Y)))
            return null;
        int a = Nearest(from), b = Nearest(to);
        int first = Math.Min(a, b), last = Math.Max(a, b);
        var areas = new List<PdfArea>();
        foreach (var group in Enumerable.Range(first, last - first + 1).Select(i => words[i]).GroupBy(w => w.Line))
            areas.Add(group.Select(w => w.Word.Area).Aggregate((x, y) => x.Union(y)));
        return new PdfMarkupItem(index, kind, areas, colour);
    }

    private static PdfPageItem Moved(PdfPageItem item, double x, double y) => item switch
    {
        PdfTextItem t => t with { Area = t.Area.Moved(x, y) },
        PdfMarkItem m => m with { Area = m.Area.Moved(x, y) },
        PdfReplaceItem r => r with { Area = r.Area.Moved(x, y) },
        PdfMarkupItem m => m with { Areas = m.Areas.Select(a => a.Moved(x, y)).ToList() },
        PdfNoteItem n => n with { Area = n.Area.Moved(x, y) },
        PdfInkItem i => i with { Strokes = i.Strokes.Select(s => (IReadOnlyList<(double X, double Y)>)s.Select(p => (p.X + x, p.Y + y)).ToList()).ToList() },
        PdfLineItem l => l with { X1 = l.X1 + x, Y1 = l.Y1 + y, X2 = l.X2 + x, Y2 = l.Y2 + y },
        PdfShapeItem s => s with { Area = s.Area.Moved(x, y) },
        PdfCoverItem c => c with { Area = c.Area.Moved(x, y) },
        PdfPictureItem p => p with { Area = p.Area.Moved(x, y) },
        PdfRedactItem r => r with { Area = r.Area.Moved(x, y) },
        _ => item,
    };

    /// <summary>An item's area with a corner dragged; a line's end. Marks and pictures keep their proportions.</summary>
    private static PdfPageItem Resized(PdfPageItem item, int handle, Point p)
    {
        if (item is PdfLineItem line)
            return handle == 0 ? line with { X1 = p.X, Y1 = p.Y } : line with { X2 = p.X, Y2 = p.Y };
        if (AreaOf(item) is not { } area)
            return item;
        double left = handle is 0 or 2 ? p.X : area.Left, right = handle is 1 or 3 ? p.X : area.Right;
        double top = handle is 0 or 1 ? p.Y : area.Top, bottom = handle is 2 or 3 ? p.Y : area.Bottom;
        var next = PdfArea.Between(left, top, right, bottom);
        if (item is PdfMarkItem or PdfPictureItem && area.Width > 0)
        {
            // By the wider of the two moves, from the opposite corner.
            double k = Math.Max(next.Width / area.Width, next.Height / area.Height);
            double w = Math.Max(6, area.Width * k), h = Math.Max(6, area.Height * k);
            next = new PdfArea(handle is 0 or 2 ? area.Right - w : area.Left, handle is 0 or 1 ? area.Bottom - h : area.Top, w, h);
        }
        next = next with { Width = Math.Max(4, next.Width), Height = Math.Max(4, next.Height) };
        return item switch
        {
            PdfTextItem t => t with { Area = next },
            PdfMarkItem m => m with { Area = next },
            PdfShapeItem s => s with { Area = next },
            PdfCoverItem c => c with { Area = next },
            PdfPictureItem pic => pic with { Area = next },
            PdfRedactItem r => r with { Area = next },
            _ => item,
        };
    }

    /// <summary>An item's rectangle on its page.</summary>
    private static PdfArea? AreaOf(PdfPageItem item) => item switch
    {
        PdfTextItem t => t.Area,
        PdfMarkItem m => m.Area,
        PdfReplaceItem r => r.Area,
        PdfMarkupItem m => m.Areas.Count == 0 ? null : m.Areas.Aggregate((a, b) => a.Union(b)),
        PdfNoteItem n => n.Area,
        PdfInkItem i when i.Strokes.SelectMany(s => s).ToList() is { Count: > 0 } points => PdfArea.Between(points.Min(p => p.X), points.Min(p => p.Y), points.Max(p => p.X), points.Max(p => p.Y)),
        PdfLineItem l => PdfArea.Between(l.X1, l.Y1, l.X2, l.Y2),
        PdfShapeItem s => s.Area,
        PdfCoverItem c => c.Area,
        PdfPictureItem p => p.Area,
        PdfRedactItem r => r.Area,
        _ => null,
    };

    private PdfPageItem? ItemAt(int index, Point p)
    {
        for (int i = _items.Count - 1; i >= 0; i--)
        {
            var item = _items[i];
            if (item.Page != index)
                continue;
            double reach = 3 / _zoom + 2;
            bool hit = item switch
            {
                PdfMarkupItem m => m.Areas.Any(a => a.Contains(p.X, p.Y)),
                PdfLineItem l => Distance(p, new Point(l.X1, l.Y1), new Point(l.X2, l.Y2)) <= reach + l.Weight,
                PdfInkItem ink => ink.Strokes.Any(s => s.Zip(s.Skip(1)).Any(z => Distance(p, new Point(z.First.X, z.First.Y), new Point(z.Second.X, z.Second.Y)) <= reach + ink.Weight)),
                PdfShapeItem s => s.Area.Inflated(reach).Contains(p.X, p.Y) && !s.Area.Inflated(-reach).Contains(p.X, p.Y),
                _ => AreaOf(item) is { } area && area.Inflated(1).Contains(p.X, p.Y),
            };
            if (hit)
                return item;
        }
        return null;
    }

    private static double Distance(Point p, Point a, Point b)
    {
        var ab = b - a;
        double t = ab.LengthSquared < 1e-6 ? 0 : Math.Clamp(Vector.Multiply(p - a, ab) / ab.LengthSquared, 0, 1);
        return (p - (a + ab * t)).Length;
    }

    /// <summary>Which corner handle of the selected item a point is on: top-left, top-right, bottom-left, bottom-right; a line's two ends; or -1.</summary>
    private int HandleAt(int index, Point p)
    {
        if (_selected is null || _selected.Page != index)
            return -1;
        var handles = Handles(_selected);
        double reach = (HandleSize / 2 + 2) / _zoom;
        for (int i = 0; i < handles.Count; i++)
            if (Math.Abs(handles[i].X - p.X) <= reach && Math.Abs(handles[i].Y - p.Y) <= reach)
                return i;
        return -1;
    }

    private static List<Point> Handles(PdfPageItem item)
    {
        if (item is PdfLineItem line)
            return [new Point(line.X1, line.Y1), new Point(line.X2, line.Y2)];
        if (item is not (PdfTextItem or PdfMarkItem or PdfShapeItem or PdfCoverItem or PdfPictureItem or PdfRedactItem) || AreaOf(item) is not { } a)
            return [];
        return [new Point(a.Left, a.Top), new Point(a.Right, a.Top), new Point(a.Left, a.Bottom), new Point(a.Right, a.Bottom)];
    }

    // ---------- typing on a page ----------

    private void OpenEditor(int index, PdfArea area, string text, double size, int colour, bool bold, bool multiLine, Action<string> commit)
    {
        var view = _pages[index];
        view.Build();
        var box = new TextBox
        {
            Text = text,
            FontFamily = new FontFamily("Arial"),
            FontSize = size * _zoom,
            FontWeight = bold ? FontWeights.Bold : FontWeights.Normal,
            Foreground = new SolidColorBrush(ColourOf(colour)),
            CaretBrush = Brushes.Black,
            Background = new SolidColorBrush(Color.FromArgb(0xF2, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            BorderBrush = (Brush)FindResource("Petal.Accent"),
            Padding = new Thickness(0),
            MinHeight = 0,
            MinWidth = 40,
            Width = area.Width * _zoom + 4,
            AcceptsReturn = multiLine,
            TextWrapping = multiLine ? TextWrapping.Wrap : TextWrapping.NoWrap,
            FlowDirection = DirectionOf(text),
        };
        box.TextChanged += (_, _) => box.FlowDirection = DirectionOf(box.Text);
        box.LostKeyboardFocus += (_, _) =>
        {
            // An old box losing the focus to a new one has no say over the new one.
            if (ReferenceEquals(_editor, box))
                CommitEditor();
        };
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && !multiLine)
            {
                e.Handled = true;
                CommitEditor();
            }
        };
        Canvas.SetLeft(box, area.Left * _zoom - 2);
        Canvas.SetTop(box, area.Top * _zoom - 2);
        view.Top.Children.Add(box);
        _editor = box;
        _commitEditor = commit;
        box.Focus();
        box.CaretIndex = box.Text.Length;
    }

    private void OpenNoteEditor(int index, PdfArea area, string text, Action<string> commit)
    {
        var view = _pages[index];
        view.Build();
        var box = new TextBox
        {
            Text = text,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 13,
            Foreground = Brushes.Black,
            CaretBrush = Brushes.Black,
            Background = new SolidColorBrush(Color.FromRgb(0xFF, 0xF5, 0xBE)),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x96, 0x82, 0x28)),
            Padding = new Thickness(4),
            Width = 220,
            Height = 96,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            FlowDirection = DirectionOf(text),
        };
        box.TextChanged += (_, _) => box.FlowDirection = DirectionOf(box.Text);
        box.LostKeyboardFocus += (_, _) =>
        {
            // An old box losing the focus to a new one has no say over the new one.
            if (ReferenceEquals(_editor, box))
                CommitEditor();
        };
        Canvas.SetLeft(box, Math.Max(0, Math.Min(area.Right * _zoom + 4, view.Width - 224)));
        Canvas.SetTop(box, Math.Max(0, Math.Min(area.Top * _zoom, view.Height - 100)));
        view.Top.Children.Add(box);
        _editor = box;
        _commitEditor = commit;
        box.Focus();
        box.CaretIndex = box.Text.Length;
    }

    /// <summary>Ends typing on a page: what was typed goes in, and the box goes away.</summary>
    internal void CommitEditor()
    {
        if (_editor is not { } box)
            return;
        var commit = _commitEditor;
        _editor = null;
        _commitEditor = null;
        (box.Parent as Canvas)?.Children.Remove(box);
        commit?.Invoke(box.Text);
    }

    private static FlowDirection DirectionOf(string text)
    {
        foreach (var c in text)
        {
            if (c is >= '֐' and <= 'ࣿ' or >= 'יִ' and <= '﷿' or >= 'ﹰ' and <= '﻿')
                return FlowDirection.RightToLeft;
            if (char.IsLetter(c))
                return FlowDirection.LeftToRight;
        }
        return L.Direction;
    }

    // ---------- the form on a page ----------

    private void BuildFields(int index)
    {
        var view = _pages[index];
        view.Build();
        if (view.FieldsBuilt)
            return;
        view.FieldsBuilt = true;
        foreach (var (field, widget) in _tabOrder.Where(t => t.Field.Widgets[t.Widget].Page == index))
        {
            var control = FieldControl(field, field.Widgets[widget]);
            if (control is null)
                continue;
            control.Tag = (field, widget);
            if (field.Tip is { Length: > 0 } tip)
                control.ToolTip = tip;
            view.Fields.Children.Add(control);
            if (!_fieldControls.TryGetValue(field.Name, out var list))
                _fieldControls[field.Name] = list = [];
            list.Add(control);
        }
        PlaceFields(view);
    }

    private void PlaceFields(PdfPageView view)
    {
        foreach (FrameworkElement control in view.Fields.Children)
        {
            var (field, widget) = ((PdfFormField, int))control.Tag;
            var area = field.Widgets[widget].Area;
            Canvas.SetLeft(control, area.Left * _zoom);
            Canvas.SetTop(control, area.Top * _zoom);
            control.Width = Math.Max(6, area.Width * _zoom);
            control.Height = Math.Max(6, area.Height * _zoom);
            // At the size it will be saved at: the form's own, or as large as fits, up to the same limit.
            double font = (field.FontSize > 0 ? field.FontSize : field.MultiLine || field.Kind == PdfFieldKind.List ? 11 : Math.Clamp((area.Height - 4) * 0.82, 4, 14)) * _zoom;
            switch (control)
            {
                case TextBox box:
                    box.FontSize = font;
                    break;
                case ComboBox combo:
                    combo.FontSize = font;
                    break;
                case ListBox list:
                    list.FontSize = font;
                    break;
            }
        }
    }

    /// <summary>A form's field is often shorter than the margins of the app's own combo box leave room for: its letters would be cut.</summary>
    private static void FitContent(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is ContentPresenter { IsHitTestVisible: false } shown)
                shown.Margin = new Thickness(6, 0, 26, 0);
            else
                FitContent(child);
        }
    }

    private string CurrentValue(PdfFormField field) => _values.TryGetValue(field.Name, out var value) ? value : field.Value;

    private FrameworkElement? FieldControl(PdfFormField field, PdfFieldWidget widget)
    {
        if (field.ReadOnly || field.Kind is PdfFieldKind.Button or PdfFieldKind.Signature || _session?.Rights == PdfEditRights.None)
            return null;
        switch (field.Kind)
        {
            case PdfFieldKind.Text:
                return TextField(field);
            case PdfFieldKind.CheckBox:
            case PdfFieldKind.Radio:
                return ButtonField(field, widget);
            case PdfFieldKind.Combo:
                return ComboField(field);
            case PdfFieldKind.List:
                return ListField(field);
            default:
                return null;
        }
    }

    private static Brush FieldTint => new SolidColorBrush(Color.FromRgb(0xE3, 0xEC, 0xFB));

    private TextBox TextField(PdfFormField field)
    {
        var box = new TextBox
        {
            Text = CurrentValue(field),
            FontFamily = new FontFamily("Arial"),
            Foreground = Brushes.Black,
            CaretBrush = Brushes.Black,
            Background = FieldTint,
            BorderThickness = new Thickness(field.Required ? 1.5 : 0),
            BorderBrush = field.Required ? Brushes.IndianRed : Brushes.Transparent,
            Padding = new Thickness(2, 0, 2, 0),
            MinHeight = 0,
            MinWidth = 0,
            AcceptsReturn = field.MultiLine,
            AcceptsTab = false,
            TextWrapping = field.MultiLine ? TextWrapping.Wrap : TextWrapping.NoWrap,
            VerticalContentAlignment = field.MultiLine ? VerticalAlignment.Top : VerticalAlignment.Center,
            MaxLength = field.MaxLength,
            IsUndoEnabled = false,
            FlowDirection = DirectionOf(CurrentValue(field)),
        };
        box.TextChanged += (_, _) =>
        {
            box.FlowDirection = DirectionOf(box.Text);
            if (!_syncing)
                SetField(field, box.Text);
        };
        box.LostKeyboardFocus += (_, _) => _editingField = null;
        return box;
    }

    /// <summary>A check box or a radio button: a tinted square or circle with its mark, clicked or pressed with Space.</summary>
    private FrameworkElement ButtonField(PdfFormField field, PdfFieldWidget widget)
    {
        var on = widget.OnState ?? "Yes";
        var mark = new TextBlock { Foreground = Brushes.Black, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FontFamily = new FontFamily("Segoe UI Symbol"), FontWeight = FontWeights.Bold };
        var cell = new Border
        {
            Background = FieldTint,
            BorderBrush = field.Required ? Brushes.IndianRed : new SolidColorBrush(Color.FromRgb(0x6E, 0x86, 0xB5)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(field.Kind == PdfFieldKind.Radio ? 100 : 2),
            Child = new Viewbox { Child = mark, Margin = new Thickness(1) },
            Cursor = Cursors.Hand,
            Focusable = true,
            FocusVisualStyle = null,
        };
        void Show()
        {
            bool isOn = CurrentValue(field) == on;
            mark.Text = isOn ? field.Kind == PdfFieldKind.Radio ? "●" : "✓" : " ";
        }
        Show();
        cell.GotKeyboardFocus += (_, _) => cell.BorderThickness = new Thickness(2);
        cell.LostKeyboardFocus += (_, _) => cell.BorderThickness = new Thickness(1);
        cell.DataContext = (Action)Show;
        cell.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            cell.Focus();
            Toggle(field, on);
        };
        cell.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Space)
            {
                e.Handled = true;
                Toggle(field, on);
            }
        };
        return cell;
    }

    /// <summary>A click on a check box or a radio button.</summary>
    internal void Toggle(PdfFormField field, string on)
    {
        var now = CurrentValue(field);
        // A radio button that is on stays on until another is chosen.
        if (now == on && field.Kind == PdfFieldKind.Radio)
            return;
        Remember();
        _values[field.Name] = now == on ? "Off" : on;
        SyncFields();
        SyncOptions();
    }

    private FrameworkElement ComboField(PdfFormField field)
    {
        var combo = new ComboBox { IsEditable = field.Editable, FontFamily = new FontFamily("Arial"), MinHeight = 0, MinWidth = 0, Padding = new Thickness(3, 0, 3, 0), VerticalContentAlignment = VerticalAlignment.Center };
        combo.Loaded += (_, _) => FitContent(combo);
        foreach (var option in field.Options)
            combo.Items.Add(new ComboBoxItem { Content = option.Label, Tag = option.Export, FlowDirection = DirectionOf(option.Label) });
        void Pick()
        {
            var value = CurrentValue(field);
            combo.SelectedItem = combo.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == value);
            if (combo.SelectedItem is null && field.Editable)
                combo.Text = value;
        }
        Pick();
        combo.DataContext = (Action)Pick;
        combo.SelectionChanged += (_, _) =>
        {
            if (!_syncing && combo.SelectedItem is ComboBoxItem { Tag: string export })
                SetField(field, export);
        };
        if (field.Editable)
            combo.AddHandler(System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent, new TextChangedEventHandler((_, _) =>
            {
                if (!_syncing && combo.SelectedItem is null)
                    SetField(field, combo.Text);
            }));
        return combo;
    }

    private FrameworkElement ListField(PdfFormField field)
    {
        var list = new ListBox { Background = FieldTint, Foreground = Brushes.Black, FontFamily = new FontFamily("Arial"), BorderThickness = new Thickness(0), SelectionMode = field.MultiSelect ? SelectionMode.Multiple : SelectionMode.Single, FlowDirection = FlowDirection.LeftToRight };
        foreach (var option in field.Options)
            list.Items.Add(new ListBoxItem { Content = option.Label, Tag = option.Export, Foreground = Brushes.Black, Padding = new Thickness(2, 0, 2, 0), MinHeight = 0, FlowDirection = DirectionOf(option.Label) });
        void Pick()
        {
            var chosen = CurrentValue(field).Split('\n');
            if (!field.MultiSelect)
            {
                list.SelectedItem = list.Items.Cast<ListBoxItem>().FirstOrDefault(i => chosen.Contains((string)i.Tag));
                return;
            }
            list.SelectedItems.Clear();
            foreach (ListBoxItem item in list.Items)
                if (chosen.Contains((string)item.Tag))
                    list.SelectedItems.Add(item);
        }
        Pick();
        list.DataContext = (Action)Pick;
        list.SelectionChanged += (_, _) =>
        {
            if (!_syncing)
                SetField(field, string.Join('\n', list.SelectedItems.Cast<ListBoxItem>().Select(i => (string)i.Tag)));
        };
        return list;
    }

    /// <summary>A field's value changed. A run of typing in one field is one step of undo.</summary>
    private void SetField(PdfFormField field, string value)
    {
        if (_editingField != field.Name)
        {
            Remember();
            _editingField = field.Name;
        }
        _values[field.Name] = value;
        SyncFields(field.Name);
        SyncOptions();
    }

    /// <summary>Every control of a field shows its value: a field can sit in several places, and undo changes them all.</summary>
    private void SyncFields(string? only = null)
    {
        _syncing = true;
        foreach (var (name, controls) in _fieldControls)
        {
            if (only is not null && name != only)
                continue;
            var field = _fields.First(f => f.Name == name);
            var value = CurrentValue(field);
            foreach (var control in controls)
            {
                if (control is TextBox box)
                {
                    if (box.Text != value)
                        box.Text = value;
                }
                else if (control.DataContext is Action show)
                    show();
            }
        }
        _syncing = false;
    }

    /// <summary>Tab goes from field to field in the form's order, across pages.</summary>
    private bool TabToField(bool back, object? focused)
    {
        if (_tabOrder.Count == 0)
            return false;
        int at = focused is FrameworkElement { Tag: ValueTuple<PdfFormField, int> tag } ? _tabOrder.IndexOf(tag) : -1;
        if (at < 0 && focused is DependencyObject inner)
            for (var parent = VisualTreeHelper.GetParent(inner); parent is not null && at < 0; parent = VisualTreeHelper.GetParent(parent))
                if (parent is FrameworkElement { Tag: ValueTuple<PdfFormField, int> upper })
                    at = _tabOrder.IndexOf(upper);
        for (int step = 1; step <= _tabOrder.Count; step++)
        {
            int next = ((at + (back ? -step : step)) % _tabOrder.Count + _tabOrder.Count) % _tabOrder.Count;
            if (at < 0 && back)
                next = _tabOrder.Count - step;
            var (field, widget) = _tabOrder[next];
            if (field.ReadOnly || field.Kind is PdfFieldKind.Button or PdfFieldKind.Signature)
                continue;
            FocusField(field, widget);
            return true;
        }
        return false;
    }

    private void FocusField(PdfFormField field, int widget)
    {
        var place = field.Widgets[widget];
        BuildFields(place.Page);
        var view = _pages[place.Page];
        double top = PageTop(place.Page) + place.Area.Top * _zoom;
        if (top < _scroll.VerticalOffset || top + place.Area.Height * _zoom > _scroll.VerticalOffset + _scroll.ViewportHeight)
            _scroll.ScrollToVerticalOffset(top - 60);
        foreach (FrameworkElement control in view.Fields.Children)
        {
            if (control.Tag is ValueTuple<PdfFormField, int> tag && ReferenceEquals(tag.Item1, field) && tag.Item2 == widget)
            {
                control.Focus();
                if (control is TextBox box)
                    box.SelectAll();
                return;
            }
        }
    }

    // ---------- finding ----------

    internal async Task FindAsync(string phrase)
    {
        if (_session is null || string.IsNullOrWhiteSpace(phrase))
            return;
        phrase = phrase.Trim();
        ShowStatus(L.T("Looking…"));
        _findButton.IsEnabled = false;
        try
        {
            var found = await _session.FindAsync(phrase);
            MarkFound(found);
            if (found.Count > 0)
                _secrets.Add(phrase);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Error(ex, "Finding words in a PDF");
            ShowStatus(L.T("Couldn't search this PDF."));
        }
        _findButton.IsEnabled = true;
    }

    internal async Task FindPrivateAsync()
    {
        if (_session is null)
            return;
        ShowStatus(L.T("Looking…"));
        _privateButton.IsEnabled = false;
        try
        {
            var found = await _session.FindPrivateAsync();
            MarkFound(found);
            foreach (var one in found)
                _secrets.Add(one.Text);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Error(ex, "Finding private details in a PDF");
            ShowStatus(L.T("Couldn't search this PDF."));
        }
        _privateButton.IsEnabled = true;
    }

    private void MarkFound(IReadOnlyList<PdfFound> found)
    {
        var known = _items.OfType<PdfRedactItem>().Select(r => (r.Page, r.Area)).ToHashSet();
        var fresh = found.Where(f => !known.Contains((f.Page, f.Area))).Select(f => new PdfRedactItem(f.Page, f.Area)).ToList();
        if (fresh.Count > 0)
        {
            Remember();
            _items.AddRange(fresh);
            if (_toolNow != PdfTool.Redact)
                SelectTool(PdfTool.Redact);
        }
        Changed();
        ShowStatus(found.Count == 0 ? L.T("Nothing found.") : L.P(found.Count, "{0} place marked", "{0} places marked"));
        if (fresh.Count > 0)
            _scroll.ScrollToVerticalOffset(PageTop(fresh[0].Page) + fresh[0].Area.Top * _zoom - 80);
    }

    // ---------- saving ----------

    /// <summary>What Save writes: the fields the person changed, and everything on the pages.</summary>
    internal PdfEditRequest BuildRequest()
    {
        CommitEditor();
        var original = _fields.ToDictionary(f => f.Name, f => f.Value);
        return new PdfEditRequest
        {
            Items = _items.ToList(),
            Fields = _values.Where(v => !original.TryGetValue(v.Key, out var was) || was != v.Value).ToDictionary(v => v.Key, v => v.Value),
            Secrets = _secrets.ToList(),
            Flatten = _flatten.IsChecked == true,
            RemoveHiddenDetails = _hidden.IsChecked == true,
        };
    }

    private async Task SaveAsync()
    {
        if (_session is not { } session)
            return;
        var request = BuildRequest();
        var output = OutputNaming.ForTool(_file, _tool);
        PdfEditResult? result = null;
        PrimaryButton.IsEnabled = false;
        await _jobs.RunAsync("Saving PDF", "PDF saved", [new WorkItem(_file, Path.GetFileName(_file), async (p, ct) =>
        {
            result = await session.SaveAsync(request, output, p, ct);
            return (IReadOnlyList<string>)[output];
        })], _at, _ => Outcome(result));
        PrimaryButton.IsEnabled = true;
        // Kept open if it didn't work, so nothing typed is lost.
        if (result is not null)
        {
            ConfirmClose = false;
            Close();
        }
    }

    private static string? Outcome(PdfEditResult? result)
    {
        if (result is null)
            return null;
        var notes = new List<string>();
        if (result.PicturePages > 0)
            notes.Add(L.P(result.PicturePages, "{0} page was redrawn as a picture to take out what was under its boxes.", "{0} pages were redrawn as pictures to take out what was under their boxes."));
        if (result.CoveredLines > 0)
            notes.Add(L.P(result.CoveredLines, "{0} retyped line's old words couldn't be taken out: they are covered, but stay in the file.", "{0} retyped lines' old words couldn't be taken out: they are covered, but stay in the file."));
        return notes.Count == 0 ? null : string.Join(" ", notes);
    }

    /// <summary>Saves to a chosen file, as Save does, without the job card.</summary>
    internal Task<PdfEditResult> SaveToAsync(string output) => _session!.SaveAsync(BuildRequest(), output);

    private bool AskToDiscard()
    {
        var ask = new DiscardWindow { Owner = this };
        return ask.ShowDialog() == true;
    }

    private sealed class DiscardWindow : ToolWindow
    {
        public DiscardWindow() : base("Close without saving?", 380)
        {
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Body.Children.Add(Wrapped(Text("Your changes to this PDF haven't been saved.")));
            PrimaryButton.Content = L.T("Close without saving");
            PrimaryButton.Click += (_, _) => DialogResult = true;
        }

        private static TextBlock Wrapped(TextBlock text)
        {
            text.TextWrapping = TextWrapping.Wrap;
            return text;
        }
    }

    // ---------- drawing ----------

    private BitmapSource Decoded(byte[] png) => _decoded.GetValue(png, bytes =>
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = new MemoryStream(bytes);
        image.EndInit();
        image.Freeze();
        return image;
    });

    private bool DrawPage(int index, DrawingContext dc, double zoom)
    {
        var view = _pages[index];
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, view.Width, view.Height));
        dc.PushTransform(new ScaleTransform(zoom, zoom));
        foreach (var item in _items)
        {
            if (item.Page != index)
                continue;
            var shown = ReferenceEquals(item, _beforeDrag) && _selected is not null && _pressed ? _selected : item;
            PdfEditorDrawing.Draw(dc, shown, Decoded);
        }
        if (_draft is { } draft && draft.Page == index)
        {
            if (draft is PdfMarkupItem markup && _toolNow == PdfTool.Redact)
                foreach (var area in markup.Areas)
                    PdfEditorDrawing.Draw(dc, new PdfRedactItem(index, area), Decoded);
            else
                PdfEditorDrawing.Draw(dc, draft, Decoded);
        }
        if (_toolNow == PdfTool.Retype)
            DrawRetypeHints(dc, index, zoom);
        if (_selected is { } selected && selected.Page == index)
            DrawSelection(dc, selected, zoom);
        dc.Pop();
        return true;
    }

    /// <summary>With the retype tool on, each line shows it can be clicked.</summary>
    private void DrawRetypeHints(DrawingContext dc, int index, double zoom)
    {
        var lines = _session?.Lines(index);
        if (lines is null)
            return;
        var pen = new Pen(new SolidColorBrush(Color.FromArgb(0x70, 0x3B, 0x82, 0xF6)), 1 / zoom);
        pen.Freeze();
        foreach (var line in lines)
            dc.DrawRectangle(null, pen, new Rect(line.Area.Left, line.Area.Top, line.Area.Width, line.Area.Height));
    }

    private static void DrawSelection(DrawingContext dc, PdfPageItem item, double zoom)
    {
        if (AreaOf(item) is not { } area)
            return;
        var accent = new SolidColorBrush(Color.FromRgb(0x3B, 0x82, 0xF6));
        var dashed = new Pen(accent, 1.2 / zoom) { DashStyle = new DashStyle([4, 3], 0) };
        dc.DrawRectangle(null, dashed, new Rect(area.Left - 1, area.Top - 1, area.Width + 2, area.Height + 2));
        double side = HandleSize / zoom;
        var border = new Pen(Brushes.White, 1 / zoom);
        foreach (var handle in Handles(item))
            dc.DrawRectangle(accent, border, new Rect(handle.X - side / 2, handle.Y - side / 2, side, side));
    }
}

/// <summary>The items as they will come out, drawn with WPF in points on the page as displayed.</summary>
internal static class PdfEditorDrawing
{
    public const double LineStep = 1.15;

    private static Brush Brush(int rgb, byte alpha = 255) => new SolidColorBrush(Color.FromArgb(alpha, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));

    /// <summary>Set the way the file will have it: Arial, its lines as far apart, and from the side its language starts on.</summary>
    public static FormattedText Formatted(string text, double size, bool bold, int colour, double width, bool italic = false, string family = "Arial")
    {
        bool rtl = text.Any(c => c is >= '֐' and <= 'ࣿ' or >= 'יִ' and <= '﷿' or >= 'ﹰ' and <= '﻿') && IsRtlFirst(text);
        var formatted = new FormattedText(text.Length == 0 ? " " : text, CultureInfo.InvariantCulture, rtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight,
            new Typeface(new FontFamily(family), italic ? FontStyles.Italic : FontStyles.Normal, bold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal), Math.Max(1, size), Brush(colour), 1.0)
        {
            LineHeight = size * LineStep,
        };
        if (width > 0)
            formatted.MaxTextWidth = Math.Max(1, width);
        return formatted;
    }

    private static bool IsRtlFirst(string text)
    {
        foreach (var c in text)
        {
            if (c is >= '֐' and <= 'ࣿ' or >= 'יִ' and <= '﷿' or >= 'ﹰ' and <= '﻿')
                return true;
            if (char.IsLetter(c))
                return false;
        }
        return false;
    }

    public static void Draw(DrawingContext dc, PdfPageItem item, Func<byte[], BitmapSource> decode)
    {
        switch (item)
        {
            case PdfCoverItem cover:
                dc.DrawRectangle(Brush(cover.Colour), null, Rect(cover.Area));
                break;
            case PdfTextItem text:
                DrawTyped(dc, text);
                break;
            case PdfMarkItem mark:
                DrawMark(dc, mark);
                break;
            case PdfReplaceItem replaced:
                DrawReplacement(dc, replaced);
                break;
            case PdfMarkupItem markup:
                DrawMarkup(dc, markup);
                break;
            case PdfNoteItem note:
                DrawNote(dc, note);
                break;
            case PdfInkItem ink:
                DrawInk(dc, ink);
                break;
            case PdfLineItem line:
                DrawLine(dc, line);
                break;
            case PdfShapeItem shape:
                var pen = new Pen(Brush(shape.Colour), shape.Weight);
                if (shape.Round)
                    dc.DrawEllipse(null, pen, new Point(shape.Area.Left + shape.Area.Width / 2, shape.Area.Top + shape.Area.Height / 2), shape.Area.Width / 2, shape.Area.Height / 2);
                else
                    dc.DrawRectangle(null, pen, Rect(shape.Area));
                break;
            case PdfPictureItem picture:
                dc.DrawImage(decode(picture.Png), Rect(picture.Area));
                break;
            case PdfRedactItem redact:
                dc.DrawRectangle(Brush(0xE02020, 0x55), new Pen(Brush(0xE02020), 1), Rect(redact.Area));
                break;
        }
    }

    private static Rect Rect(PdfArea a) => new(a.Left, a.Top, Math.Max(0, a.Width), Math.Max(0, a.Height));

    private static void DrawTyped(DrawingContext dc, PdfTextItem item)
    {
        // Set within its box's width: a right-to-left line starts at the box's right side by itself.
        dc.DrawText(Formatted(item.Text, item.Size, item.Bold, item.Colour, item.Area.Width), new Point(item.Area.Left, item.Area.Top));
    }

    private static void DrawMark(DrawingContext dc, PdfMarkItem item)
    {
        var a = item.Area;
        double side = Math.Min(a.Width, a.Height);
        var pen = new Pen(Brush(item.Colour), side * 0.14) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        Point At(double x, double y) => new(a.Left + a.Width * x, a.Top + a.Height * y);
        switch (item.Mark)
        {
            case PdfMark.Check:
                dc.DrawLine(pen, At(0.14, 0.55), At(0.40, 0.80));
                dc.DrawLine(pen, At(0.40, 0.80), At(0.86, 0.20));
                break;
            case PdfMark.Cross:
                dc.DrawLine(pen, At(0.18, 0.18), At(0.82, 0.82));
                dc.DrawLine(pen, At(0.82, 0.18), At(0.18, 0.82));
                break;
            default:
                dc.DrawEllipse(Brush(item.Colour), null, At(0.5, 0.5), a.Width * 0.36, a.Height * 0.36);
                break;
        }
    }

    private static void DrawReplacement(DrawingContext dc, PdfReplaceItem item)
    {
        foreach (var old in item.Old)
            dc.DrawRectangle(Brush(item.Background), null, new Rect(old.Left - 0.5, old.Top, old.Width + 1, old.Height));
        if (string.IsNullOrWhiteSpace(item.Text))
            return;
        // From the page's left edge to the line's right end, so that a right-to-left line ends where the old one started.
        var line = item.Text.Replace('\n', ' ');
        var formatted = Formatted(line, item.Size, item.Bold, item.Colour, 0, item.Italic);
        bool rtl = formatted.FlowDirection == FlowDirection.RightToLeft;
        if (rtl)
            formatted.MaxTextWidth = Math.Max(1, item.Area.Right);
        dc.DrawText(formatted, new Point(rtl ? 0 : item.Area.Left, item.Area.Bottom - 0.22 * item.Size - formatted.Baseline));
    }

    private static void DrawMarkup(DrawingContext dc, PdfMarkupItem item)
    {
        foreach (var a in item.Areas)
        {
            switch (item.Kind)
            {
                case PdfMarkup.Highlight:
                    dc.DrawRectangle(Brush(item.Colour, 0x66), null, Rect(a));
                    break;
                case PdfMarkup.Underline:
                    dc.DrawLine(new Pen(Brush(item.Colour), Math.Max(0.6, a.Height * 0.07)), new Point(a.Left, a.Bottom - a.Height * 0.12), new Point(a.Right, a.Bottom - a.Height * 0.12));
                    break;
                default:
                    dc.DrawLine(new Pen(Brush(item.Colour), Math.Max(0.6, a.Height * 0.07)), new Point(a.Left, a.Bottom - a.Height * 0.55), new Point(a.Right, a.Bottom - a.Height * 0.55));
                    break;
            }
        }
    }

    private static void DrawNote(DrawingContext dc, PdfNoteItem item)
    {
        var a = item.Area;
        var line = new Pen(new SolidColorBrush(Color.FromRgb(0x59, 0x4D, 0x1A)), 0.6);
        dc.DrawRectangle(Brush(item.Colour), line, Rect(a));
        for (int i = 1; i <= 3; i++)
            dc.DrawLine(line, new Point(a.Left + a.Width * 0.2, a.Top + a.Height * i / 4), new Point(a.Left + a.Width * 0.8, a.Top + a.Height * i / 4));
    }

    private static void DrawInk(DrawingContext dc, PdfInkItem item)
    {
        var pen = new Pen(Brush(item.Colour), item.Weight) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        foreach (var stroke in item.Strokes)
        {
            if (stroke.Count < 2)
                continue;
            var geometry = new StreamGeometry();
            using (var g = geometry.Open())
            {
                g.BeginFigure(new Point(stroke[0].X, stroke[0].Y), false, false);
                for (int i = 1; i < stroke.Count; i++)
                    g.LineTo(new Point(stroke[i].X, stroke[i].Y), true, true);
            }
            dc.DrawGeometry(null, pen, geometry);
        }
    }

    private static void DrawLine(DrawingContext dc, PdfLineItem item)
    {
        var from = new Point(item.X1, item.Y1);
        var to = new Point(item.X2, item.Y2);
        var pen = new Pen(Brush(item.Colour), item.Weight) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        if (!item.Arrow)
        {
            dc.DrawLine(pen, from, to);
            return;
        }
        var direction = to - from;
        if (direction.Length < 0.01)
            return;
        direction.Normalize();
        double head = Math.Max(6, item.Weight * 4);
        var normal = new Vector(-direction.Y, direction.X);
        var back = to - direction * head;
        dc.DrawLine(pen, from, to - direction * head * 0.6);
        var tip = new StreamGeometry();
        using (var g = tip.Open())
        {
            g.BeginFigure(to, true, true);
            g.LineTo(back + normal * head * 0.5, true, true);
            g.LineTo(back - normal * head * 0.5, true, true);
        }
        dc.DrawGeometry(Brush(item.Colour), null, tip);
    }
}

/// <summary>A signature to place: drawn on a pad, typed, or a scanned picture, as Sign PDF takes them.</summary>
internal sealed class SignatureWindow : ToolWindow
{
    private readonly Segmented<int> _source;
    private readonly InkCanvas _ink = new() { Height = 150, FlowDirection = FlowDirection.LeftToRight, Background = Brushes.White, Cursor = Cursors.Pen };
    private readonly TextBox _name = new();
    private readonly Button _choose = new() { HorizontalAlignment = HorizontalAlignment.Left };
    private readonly Grid _drawPanel = new(), _typePanel, _picturePanel;
    private BitmapSource? _picture;
    private string? _picturePath;

    public SignatureWindow() : base("Signature", 460)
    {
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _source = Segments([(0, "Draw"), (1, "Type"), (2, "Picture")], 0);
        _source.Changed += _ => Sync();
        Body.Children.Add(_source);
        _ink.DefaultDrawingAttributes = new DrawingAttributes { Color = Signatures.Ink, Width = 2.6, Height = 2.6, FitToCurve = true };
        _ink.StrokeCollected += (_, _) => Sync();
        var frame = new Border { CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Child = _ink, ClipToBounds = true };
        frame.SetResourceReference(Border.BorderBrushProperty, "Petal.ControlBorder");
        var clear = new Button { Content = L.T("Clear"), Style = (Style)FindResource("Petal.SubtleButton"), HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 4, -8, 0) };
        clear.Click += (_, _) => { _ink.Strokes.Clear(); Sync(); };
        _drawPanel.Margin = new Thickness(0, 10, 0, 0);
        _drawPanel.RowDefinitions.Add(new RowDefinition());
        _drawPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(clear, 1);
        _drawPanel.Children.Add(frame);
        _drawPanel.Children.Add(clear);
        Body.Children.Add(_drawPanel);
        _name.TextChanged += (_, _) => Sync();
        _typePanel = Row("Your name", _name, 100, 10);
        Body.Children.Add(_typePanel);
        _choose.Click += async (_, _) => await ChooseAsync();
        _picturePanel = Row("Picture", _choose, 100, 10);
        Body.Children.Add(_picturePanel);
        var caption = Text("This places your signature on the page as a picture. It isn't a certificate-based digital signature.", "Petal.Caption");
        caption.TextWrapping = TextWrapping.Wrap;
        caption.Margin = new Thickness(0, 10, 0, 0);
        Body.Children.Add(caption);
        PrimaryButton.Content = L.T("Sign");
        PrimaryButton.Click += (_, _) =>
        {
            Result = Signature();
            DialogResult = Result is not null;
        };
        Sync();
    }

    public BitmapSource? Result { get; private set; }

    private BitmapSource? Signature() => _source.Value switch
    {
        0 when _ink.Strokes.Count > 0 => Signatures.Render(_ink.Strokes),
        1 when _name.Text.Trim().Length > 0 => Signatures.Render(_name.Text.Trim()),
        2 => _picture,
        _ => null,
    };

    private void Sync()
    {
        _drawPanel.Visibility = _source.Value == 0 ? Visibility.Visible : Visibility.Collapsed;
        _typePanel.Visibility = _source.Value == 1 ? Visibility.Visible : Visibility.Collapsed;
        _picturePanel.Visibility = _source.Value == 2 ? Visibility.Visible : Visibility.Collapsed;
        _choose.Content = _picturePath is null ? L.T("Choose picture…") : Path.GetFileName(_picturePath);
        PrimaryButton.IsEnabled = Signature() is not null;
    }

    private async Task ChooseAsync()
    {
        var dialog = new OpenFileDialog { Filter = L.T("Images") + "|*.png;*.jpg;*.jpeg;*.webp;*.bmp;*.gif;*.tif;*.tiff" };
        if (dialog.ShowDialog(this) != true)
            return;
        try
        {
            _picture = await ImageLoader.LoadAsync(dialog.FileName, 1600);
            _picturePath = dialog.FileName;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Error(ex, "Loading signature picture");
            FooterNote = "Couldn't open this image.";
        }
        Sync();
    }
}
