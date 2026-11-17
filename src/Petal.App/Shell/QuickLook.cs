using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Petal.App.Input;
using Petal.App.Localization;
using Petal.App.Settings;
using Petal.App.Util;
using Petal.App.Visuals;
using Petal.Core.Formats;
using static Petal.App.Interop.NativeMethods;

namespace Petal.App.Shell;

/// <summary>
/// Quick Look: with a file selected in File Explorer or on the desktop, Space shows it at once (a picture,
/// a Photoshop or camera file, a page of a PDF, a video or a song playing, a document's words, what a ZIP
/// holds, a font) and Space or Esc closes it. Moving through the files with the arrow keys moves the
/// preview along. Space is left alone while a name is being typed or renamed, in the address bar and in
/// search, and with any other key held.
/// </summary>
internal static partial class QuickLook
{
    private const int VK_SPACE = 0x20, VK_ESCAPE = 0x1B;
    private static QuickLookWindow? _open;
    private static Dispatcher? _ui;
    private static long _typedAt;
    private static bool _swallowUp;
    private static bool _spaceDown;
    private static volatile bool _isOpen;
    private static Func<IReadOnlyList<string>, ScreenPoint, bool>? _openWheel;

    /// <summary>Starts listening, on the keyboard hook Petal already has.</summary>
    public static void Start(Dispatcher ui, Func<IReadOnlyList<string>, ScreenPoint, bool> openWheel)
    {
        if (_ui is not null || GlobalInputHook.Current is not { } hook)
            return;
        _ui = ui;
        _openWheel = openWheel;
        hook.AddKeyFilter(OnKey);
    }

    public static bool IsOpen => _isOpen;

    /// <summary>On the hook's thread: Space in a file list opens or closes the preview; Esc closes it.</summary>
    private static bool OnKey(int vk, bool down)
    {
        if (vk == VK_SPACE && !down)
        {
            _spaceDown = false;
            if (_swallowUp)
            {
                _swallowUp = false;
                return true;
            }
            return false;
        }
        if (!down)
            return false;
        // A held Space repeats; the preview already opened (or closed) on the first one.
        if (vk == VK_SPACE && _spaceDown)
            return _swallowUp;
        if (vk == VK_ESCAPE && _isOpen && IsFileList(GetForegroundWindow()))
        {
            _ui?.BeginInvoke(Close);
            return true;
        }
        if (vk != VK_SPACE)
        {
            // Letters and digits: a name is being typed to jump to a file, and its spaces belong to it.
            if (vk is >= 0x30 and <= 0x5A or >= 0x60 and <= 0x6F or >= 0xBA)
                Interlocked.Exchange(ref _typedAt, Environment.TickCount64);
            return false;
        }
        if (!SettingsStore.Current.QuickLook || SettingsStore.Current.Paused || AnyModifier())
            return false;
        if (Environment.TickCount64 - Interlocked.Read(ref _typedAt) < 800)
            return false;
        var front = GetForegroundWindow();
        if (!IsFileList(front))
            return false;
        _swallowUp = true;
        _spaceDown = true;
        _ui?.BeginInvoke(() => _ = ToggleAsync(front));
        return true;
    }

    private static bool AnyModifier() =>
        Down(0x10) || Down(0x11) || Down(0x12) || Down(0x5B) || Down(0x5C);

    private static bool Down(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    /// <summary>
    /// File Explorer or the desktop is in front with the keyboard in its list of files (not renaming,
    /// not in the address bar or search).
    /// </summary>
    internal static bool IsFileList(IntPtr front)
    {
        var kind = ClassNameOf(front);
        if (kind is not ("CabinetWClass" or "ExploreWClass" or "Progman" or "WorkerW"))
            return false;
        uint thread = GetWindowThreadProcessId(front, out _);
        var info = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
        if (!GetGUIThreadInfo(thread, ref info) || info.hwndFocus == IntPtr.Zero)
            return false;
        // Renaming puts an edit box in focus; the list itself sits inside SHELLDLL_DefView.
        if ((info.flags & GUI_CARETBLINKING) != 0)
            return false;
        var focus = ClassNameOf(info.hwndFocus);
        var parent = ClassNameOf(GetParent(info.hwndFocus));
        return focus is "DirectUIHWND" or "SysListView32" && parent == "SHELLDLL_DefView";
    }

    private static async Task ToggleAsync(IntPtr front)
    {
        if (_open is not null)
        {
            Close();
            return;
        }
        var files = await ExplorerSelection.SelectionInAsync(front);
        if (files.Count == 0 || _open is not null)
            return;
        _open = new QuickLookWindow(front, files, _openWheel);
        _open.Closed += (_, _) =>
        {
            _open = null;
            _isOpen = false;
        };
        _isOpen = true;
        _open.ShowOver(front);
    }

    public static void Close() => _open?.Close();

    private const int GUI_CARETBLINKING = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct GUITHREADINFO
    {
        public int cbSize;
        public int flags;
        public IntPtr hwndActive, hwndFocus, hwndCapture, hwndMenuOwner, hwndMoveSize, hwndCaret;
        public RECT rcCaret;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetGUIThreadInfo(uint thread, ref GUITHREADINFO info);

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(IntPtr hwnd, out uint process);

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetParent(IntPtr hwnd);
}

/// <summary>What a file looks like inside, for Quick Look; the kinds a preview knows how to show.</summary>
internal enum LookKind
{
    Picture,
    Pdf,
    Video,
    Sound,
    Text,
    Document,
    Archive,
    Font,
    Folder,
    Other,
}

/// <summary>The preview window: over the File Explorer window, never taking the keyboard from it.</summary>
internal sealed class QuickLookWindow : Window
{
    private static readonly string[] TextExtensions = [".txt", ".md", ".markdown", ".csv", ".tsv", ".json", ".xml", ".srt", ".vtt", ".ass", ".log", ".ini", ".cfg", ".conf",
        ".yaml", ".yml", ".toml", ".cs", ".js", ".ts", ".jsx", ".tsx", ".py", ".java", ".c", ".h", ".cpp", ".hpp", ".go", ".rs", ".rb", ".php", ".html", ".htm",
        ".css", ".scss", ".sql", ".ps1", ".psm1", ".bat", ".cmd", ".sh", ".kt", ".swift", ".dart", ".lua", ".r", ".tex", ".gitignore", ".editorconfig", ".reg"];

    private readonly IntPtr _explorer;
    private readonly Func<IReadOnlyList<string>, ScreenPoint, bool>? _openWheel;
    private readonly Border _stage = new() { ClipToBounds = true };
    private readonly TextBlock _name = new() { FontSize = 14, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _facts = new() { FontSize = 12, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly DispatcherTimer _follow = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private CancellationTokenSource _loading = new();
    private MediaElement? _media;
    private string _path = "";
    private IReadOnlyList<string> _files;
    private bool _checking;
    private bool _closed;
    // PDF pages: the open document and the page shown.
    private Petal.Engines.Documents.Pdf.PdfDocumentView? _pdf;
    private int _page;

    public QuickLookWindow(IntPtr explorer, IReadOnlyList<string> files, Func<IReadOnlyList<string>, ScreenPoint, bool>? openWheel)
    {
        _explorer = explorer;
        _files = files;
        _openWheel = openWheel;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.CanResizeWithGrip;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Width = 820;
        Height = 600;
        MinWidth = 360;
        MinHeight = 260;
        Title = L.T("Quick Look");
        FontFamily = UiFonts.Text;
        L.Apply(this);
        SetResourceReference(ForegroundProperty, "Petal.Text");

        _facts.SetResourceReference(TextBlock.ForegroundProperty, "Petal.TextSecondary");
        var open = new Button { Content = L.T("Open"), Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(8, 0, 0, 0) };
        open.Click += (_, _) => OpenWithApp();
        var wheel = new Button { Content = L.T("DropWheel"), Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(8, 0, 0, 0) };
        wheel.Click += (_, _) => OpenWheel();
        var pin = Icons.Create("pin", 14, Brushes.Gray, 2);
        pin.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "Petal.Text");
        var floating = new Button { Content = pin, Padding = new Thickness(6), Margin = new Thickness(8, 0, 0, 0), ToolTip = L.T("Float it: keep it open above the other windows") };
        floating.Click += (_, _) => FloatIt();
        var cross = Icons.Create("x", 14, Brushes.Gray, 2);
        cross.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "Petal.Text");
        var close = new Button { Content = cross, Padding = new Thickness(6), Margin = new Thickness(8, 0, 0, 0), ToolTip = L.T("Close") };
        close.Click += (_, _) => Close();
        var header = new DockPanel { Margin = new Thickness(16, 10, 10, 10), LastChildFill = true };
        DockPanel.SetDock(close, Dock.Right);
        DockPanel.SetDock(floating, Dock.Right);
        DockPanel.SetDock(wheel, Dock.Right);
        DockPanel.SetDock(open, Dock.Right);
        header.Children.Add(close);
        header.Children.Add(floating);
        header.Children.Add(wheel);
        header.Children.Add(open);
        header.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { _name, _facts } });
        Screen.ScreenUi.DragFromBackground(this, header);
        var divider = new Border { Height = 1 };
        divider.SetResourceReference(Border.BackgroundProperty, "Petal.Divider");
        var layout = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(divider, Dock.Top);
        layout.Children.Add(header);
        layout.Children.Add(divider);
        layout.Children.Add(_stage);
        var card = new Border
        {
            CornerRadius = new CornerRadius(14),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(18),
            Child = layout,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 30, ShadowDepth = 8, Direction = 270, Opacity = 0.35 },
        };
        card.SetResourceReference(Border.BackgroundProperty, "Petal.Popup");
        card.SetResourceReference(Border.BorderBrushProperty, "Petal.ControlBorder");
        Content = card;
        _stage.PreviewMouseWheel += OnWheel;
        _follow.Tick += async (_, _) => await FollowSelectionAsync();
        Closed += (_, _) =>
        {
            _closed = true;
            _follow.Stop();
            _loading.Cancel();
            StopMedia();
            _pdf?.Dispose();
        };
    }

    /// <summary>For design snapshots: the first file, shown without File Explorer.</summary>
    internal Task ShowFirstAsync() => ShowFileAsync(_files[0]);

    /// <summary>Shows the window centred on the File Explorer window, without taking the keyboard.</summary>
    public void ShowOver(IntPtr explorer)
    {
        WindowStartupLocation = WindowStartupLocation.Manual;
        if (GetWindowRect(explorer, out var rect) && rect.Width > 200)
        {
            var dpi = VisualTreeHelper.GetDpi(Application.Current.MainWindow ?? this);
            double scale = dpi.DpiScaleX > 0 ? dpi.DpiScaleX : 1;
            Left = (rect.Left + rect.Width / 2.0) / scale - Width / 2;
            Top = (rect.Top + rect.Height / 2.0) / scale - Height / 2;
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            long ex = (long)GetWindowLongPtr(handle, GWL_EXSTYLE) | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
            SetWindowLongPtr(handle, GWL_EXSTYLE, new IntPtr(ex));
        };
        Show();
        _ = ShowFileAsync(_files[0]);
        _follow.Start();
    }

    /// <summary>The arrow keys move the selection in File Explorer; the preview goes with it.</summary>
    private async Task FollowSelectionAsync()
    {
        if (_checking || _closed)
            return;
        var front = GetForegroundWindow();
        if (front != _explorer)
        {
            // Another app came to the front: the preview was for File Explorer only.
            if (front != new WindowInteropHelper(this).Handle)
                Close();
            return;
        }
        _checking = true;
        try
        {
            var files = await ExplorerSelection.SelectionInAsync(_explorer);
            if (_closed)
                return;
            if (files.Count == 0)
            {
                Close();
                return;
            }
            if (!files[0].Equals(_path, StringComparison.OrdinalIgnoreCase))
            {
                _files = files;
                await ShowFileAsync(files[0]);
            }
        }
        finally
        {
            _checking = false;
        }
    }

    internal static LookKind KindOf(string path)
    {
        if (Directory.Exists(path))
            return LookKind.Folder;
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is ".ttf" or ".otf" or ".ttc")
            return LookKind.Font;
        if (ext == ".zip")
            return LookKind.Archive;
        if (TextExtensions.Contains(ext))
            return LookKind.Text;
        return Formats.FromPath(path) switch
        {
            { } f when f == Formats.Pdf => LookKind.Pdf,
            { Family: FormatFamily.Image or FormatFamily.Vector } => LookKind.Picture,
            { Family: FormatFamily.Video } => LookKind.Video,
            { Family: FormatFamily.Audio } => LookKind.Sound,
            { Family: FormatFamily.Document or FormatFamily.Ebook or FormatFamily.Slides or FormatFamily.Spreadsheet } => LookKind.Document,
            { Family: FormatFamily.Text } => LookKind.Text,
            { Family: FormatFamily.Subtitle } => LookKind.Text,
            _ => LookKind.Other,
        };
    }

    private async Task ShowFileAsync(string path)
    {
        if (_closed)
            return;
        _loading.Cancel();
        _loading = new CancellationTokenSource();
        var ct = _loading.Token;
        StopMedia();
        _pdf?.Dispose();
        _pdf = null;
        _path = path;
        _name.Text = Path.GetFileName(path.TrimEnd('\\')) is { Length: > 0 } name ? name : path;
        _facts.Text = Facts(path, null);
        _stage.Child = Waiting();
        var kind = KindOf(path);
        try
        {
            var view = kind switch
            {
                LookKind.Picture => await PictureAsync(path, ct),
                LookKind.Pdf => await PdfAsync(path, 0, ct),
                LookKind.Video or LookKind.Sound => await MediaAsync(path, kind == LookKind.Video, ct),
                LookKind.Text => await TextAsync(path, ct),
                LookKind.Document => await DocumentAsync(path, ct),
                LookKind.Archive => await ArchiveAsync(path, ct),
                LookKind.Font => FontView(path),
                LookKind.Folder => await FolderAsync(path, ct),
                _ => null,
            };
            if (ct.IsCancellationRequested)
                return;
            _stage.Child = view ?? Fallback(path);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (ct.IsCancellationRequested)
                return;
            Log.Info($"quick look: {Path.GetFileName(path)} shown as an icon ({ex.GetType().Name}: {ex.Message})");
            _stage.Child = Fallback(path);
        }
    }

    internal static string Facts(string path, string? more)
    {
        var parts = new List<string>();
        try
        {
            if (File.Exists(path))
            {
                var info = new FileInfo(path);
                parts.Add(Sizes.Format(info.Length));
                parts.Add(info.LastWriteTime.ToString("g", System.Globalization.CultureInfo.CurrentCulture));
            }
        }
        catch (IOException)
        {
        }
        if (more is not null)
            parts.Insert(0, more);
        return string.Join(" · ", parts);
    }

    internal static UIElement Waiting()
    {
        var text = new TextBlock { Text = L.T("Opening…"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        text.SetResourceReference(TextBlock.ForegroundProperty, "Petal.TextSecondary");
        return text;
    }

    private static Image Fit(ImageSource source)
    {
        var image = new Image { Source = source, Stretch = Stretch.Uniform, Margin = new Thickness(12), StretchDirection = StretchDirection.Both };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        // Small pictures stay their own size rather than being blown up.
        if (source is BitmapSource b)
        {
            image.MaxWidth = b.PixelWidth;
            image.MaxHeight = b.PixelHeight;
        }
        return image;
    }

    private async Task<UIElement?> PictureAsync(string path, CancellationToken ct)
    {
        var picture = await Tools.Parts.ImageLoader.LoadAsync(path, 1800);
        ct.ThrowIfCancellationRequested();
        string? size = null;
        try
        {
            var info = await Task.Run(() => new ImageMagick.MagickImageInfo(path), ct);
            size = $"{info.Width} × {info.Height}";
        }
        catch (Exception ex) when (ex is ImageMagick.MagickException or IOException)
        {
            size = $"{picture.PixelWidth} × {picture.PixelHeight}";
        }
        _facts.Text = Facts(path, size);
        return new Border { Background = Checkers(), Child = Fit(picture) };
    }

    /// <summary>A soft checkerboard behind pictures, so transparency shows.</summary>
    internal static Brush Checkers()
    {
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(Theme.Current.IsDark ? new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2E)) : Brushes.White, null, new RectangleGeometry(new Rect(0, 0, 16, 16))));
        var dark = new SolidColorBrush(Theme.Current.IsDark ? Color.FromRgb(0x33, 0x33, 0x38) : Color.FromRgb(0xEE, 0xEE, 0xF0));
        group.Children.Add(new GeometryDrawing(dark, null, new RectangleGeometry(new Rect(0, 0, 8, 8))));
        group.Children.Add(new GeometryDrawing(dark, null, new RectangleGeometry(new Rect(8, 8, 8, 8))));
        var brush = new DrawingBrush(group) { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 16, 16), ViewportUnits = BrushMappingMode.Absolute };
        brush.Freeze();
        return brush;
    }

    private async Task<UIElement?> PdfAsync(string path, int page, CancellationToken ct)
    {
        if (_pdf is null)
        {
            var opened = await Task.Run(() => Petal.Engines.Documents.Pdf.PdfDocumentView.Open(path), CancellationToken.None);
            if (ct.IsCancellationRequested || _closed || _pdf is not null)
            {
                opened.Dispose();
                ct.ThrowIfCancellationRequested();
                if (_closed)
                    return null;
            }
            else
            {
                _pdf = opened;
            }
        }
        var pdf = _pdf!;
        page = Math.Clamp(page, 0, pdf.PageCount - 1);
        _page = page;
        int width = (int)Math.Max(600, ActualWidth * VisualTreeHelper.GetDpi(this).DpiScaleX);
        var png = await Task.Run(() => pdf.RenderPagePng(page, Math.Min(width, 2000)), ct);
        ct.ThrowIfCancellationRequested();
        var image = Decode(png);
        _facts.Text = Facts(path, pdf.PageCount > 1 ? L.F("Page {0} of {1}", page + 1, pdf.PageCount) : L.P(1, "{0} page", "{0} pages"));
        return new Border { Background = Brushes.Gray, Child = Fit(image) };
    }

    /// <summary>The mouse wheel turns a PDF's pages.</summary>
    private async void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (_pdf is not { } pdf || pdf.PageCount < 2)
            return;
        e.Handled = true;
        int next = Math.Clamp(_page + (e.Delta < 0 ? 1 : -1), 0, pdf.PageCount - 1);
        if (next == _page)
            return;
        var ct = _loading.Token;
        try
        {
            var view = await PdfAsync(_path, next, ct);
            if (!ct.IsCancellationRequested && view is not null)
                _stage.Child = view;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Error(ex, "Turning a page in Quick Look");
        }
    }

    private async Task<UIElement?> MediaAsync(string path, bool video, CancellationToken ct)
    {
        var info = await Petal.Engines.Media.MediaProbe.ProbeAsync(path, ct);
        ct.ThrowIfCancellationRequested();
        var length = Petal.Core.Text.Durations.Format(info.Duration);
        _facts.Text = Facts(path, video && info.Width > 0 ? $"{info.Width} × {info.Height} · {length}" : length);
        var media = new MediaElement { LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Close, Stretch = Stretch.Uniform, Source = new Uri(path), Volume = 0.8 };
        _media = media;
        var grid = new Grid { Background = video ? Brushes.Black : null };
        if (!video)
        {
            var waveform = await Tools.Parts.Peaks.LoadAsync(path);
            ct.ThrowIfCancellationRequested();
            grid.Children.Add(WaveformPicture(waveform));
        }
        grid.Children.Add(media);
        var hint = new TextBlock { Text = L.T("Click to pause or play"), FontSize = 11, Margin = new Thickness(0, 0, 0, 8), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Opacity = 0.75 };
        if (video) hint.Foreground = Brushes.White; else hint.SetResourceReference(TextBlock.ForegroundProperty, "Petal.TextSecondary");
        grid.Children.Add(hint);
        bool playing = true;
        grid.MouseLeftButtonUp += (_, _) =>
        {
            if (playing) media.Pause(); else media.Play();
            playing = !playing;
        };
        media.MediaEnded += (_, _) =>
        {
            media.Position = TimeSpan.Zero;
            media.Play();
        };
        media.MediaFailed += async (_, _) =>
        {
            // A format Windows can't play: a still from the middle instead.
            if (!video || ct.IsCancellationRequested)
                return;
            try
            {
                var frame = await Petal.Engines.Media.MediaPreview.ExtractFrameAsync(path, info.Duration / 2, 1280, ct);
                if (!ct.IsCancellationRequested)
                    _stage.Child = new Border { Background = Brushes.Black, Child = Fit(Decode(frame)) };
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _stage.Child = Fallback(path);
            }
        };
        media.Play();
        return grid;
    }

    internal static UIElement WaveformPicture(float[] peaks)
    {
        var canvas = new Canvas { Height = 160, Margin = new Thickness(24), VerticalAlignment = VerticalAlignment.Center };
        canvas.SizeChanged += (_, _) =>
        {
            canvas.Children.Clear();
            double w = canvas.ActualWidth, h = canvas.ActualHeight;
            if (peaks.Length == 0 || w <= 0)
                return;
            var geometry = new StreamGeometry();
            using (var g = geometry.Open())
            {
                for (int i = 0; i < peaks.Length; i++)
                {
                    double x = i * w / peaks.Length, half = Math.Max(1, peaks[i] * h / 2);
                    g.BeginFigure(new Point(x, h / 2 - half), false, false);
                    g.LineTo(new Point(x, h / 2 + half), true, false);
                }
            }
            var path = new System.Windows.Shapes.Path { Data = geometry, StrokeThickness = Math.Max(1, w / peaks.Length * 0.6) };
            path.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "Petal.Accent");
            canvas.Children.Add(path);
        };
        return canvas;
    }

    private void StopMedia()
    {
        if (_media is { } media)
        {
            media.Stop();
            media.Close();
            _media = null;
        }
    }

    internal static UIElement TextView(string text, bool code)
    {
        var box = new TextBox
        {
            Text = text,
            IsReadOnly = true,
            TextWrapping = code ? TextWrapping.NoWrap : TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Padding = new Thickness(18, 12, 18, 12),
            FontFamily = code ? UiFonts.Mono : UiFonts.Text,
            FontSize = code ? 12.5 : 14,
            FlowDirection = Petal.Core.Text.KeyboardLayout.LooksArabic(text.Length > 2000 ? text[..2000] : text) ? FlowDirection.RightToLeft : FlowDirection.LeftToRight,
        };
        box.SetResourceReference(Control.ForegroundProperty, "Petal.Text");
        return box;
    }

    /// <summary>The first part of a text file (a quarter of a megabyte), in its own encoding.</summary>
    internal static async Task<string> ReadTextAsync(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var buffer = new byte[Math.Min(stream.Length, 256 * 1024)];
        int read = await stream.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, ct);
        using var reader = new StreamReader(new MemoryStream(buffer, 0, read), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var text = await reader.ReadToEndAsync(ct);
        if (stream.Length > read)
            text += Environment.NewLine + "…";
        return text;
    }

    private async Task<UIElement?> TextAsync(string path, CancellationToken ct)
    {
        var text = await ReadTextAsync(path, ct);
        var ext = Path.GetExtension(path).ToLowerInvariant();
        _facts.Text = Facts(path, L.P(text.Count(c => c == '\n') + 1, "{0} line", "{0} lines"));
        return TextView(text, ext is not (".txt" or ".md" or ".markdown" or ".srt" or ".vtt"));
    }

    private async Task<UIElement?> DocumentAsync(string path, CancellationToken ct)
    {
        var converters = App.Current.Controller.Jobs.Converters;
        var text = await Petal.Recipes.DocumentText.ReadAsync(path, converters, SettingsStore.Current.Conversion, null, ct);
        if (string.IsNullOrWhiteSpace(text))
            return null;
        _facts.Text = Facts(path, L.P(Petal.Core.Text.TextTools.Count(text).Words, "{0} word", "{0} words"));
        return TextView(text.Length > 200_000 ? text[..200_000] + "…" : text, code: false);
    }

    /// <summary>A ZIP's contents: folders and files with their sizes.</summary>
    internal static IReadOnlyList<(string Name, long Size)> ZipEntries(string path, int most = 500)
    {
        using var zip = ZipFile.OpenRead(path);
        return zip.Entries.Take(most).Select(e => (e.FullName, e.Length)).ToList();
    }

    private async Task<UIElement?> ArchiveAsync(string path, CancellationToken ct)
    {
        var entries = await Task.Run(() => ZipEntries(path), ct);
        _facts.Text = Facts(path, L.P(entries.Count, "{0} item", "{0} items"));
        var list = new StringBuilder();
        foreach (var (name, size) in entries)
            list.Append(name).Append(name.EndsWith('/') ? "" : "   " + Sizes.Format(size)).AppendLine();
        return TextView(list.ToString(), code: true);
    }

    private UIElement FontView(string path)
    {
        var families = Fonts.GetFontFamilies(new Uri(path));
        var family = families.FirstOrDefault() ?? throw new FileFormatException("No font in the file");
        _facts.Text = Facts(path, family.Source.Split('#').Last());
        var panel = new StackPanel { Margin = new Thickness(24, 16, 24, 16) };
        foreach (var (size, sample) in new[] { (48.0, "Aa Bb Cc"), (28.0, "The quick brown fox jumps over the lazy dog"), (28.0, "نص عربي للتجربة ١٢٣"), (18.0, "0123456789 !?&@ ABCDEFGHIJKLMNOPQRSTUVWXYZ") })
        {
            var line = new TextBlock { Text = sample, FontFamily = family, FontSize = size, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) };
            line.SetResourceReference(TextBlock.ForegroundProperty, "Petal.Text");
            panel.Children.Add(line);
        }
        return new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    private async Task<UIElement?> FolderAsync(string path, CancellationToken ct)
    {
        var contents = await Task.Run(() => Petal.Core.Jobs.FolderContents.Read([path], Jobs.FolderDrop.QuickLook), ct);
        var hub = Jobs.FolderDrop.Hub(contents);
        _facts.Text = hub.Title;
        var names = await Task.Run(() => new DirectoryInfo(path).EnumerateFileSystemInfos().Take(300).Select(i => i is DirectoryInfo ? i.Name + "\\" : i.Name).ToList(), ct);
        return TextView(string.Join(Environment.NewLine, names), code: true);
    }

    internal static UIElement Fallback(string path)
    {
        var panel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        if (ShellIcons.Thumbnail(path, 256) is { } picture)
            panel.Children.Add(new Image { Source = picture, Width = 192, Height = 192 });
        var kind = new TextBlock { Text = L.T("No preview for this kind of file"), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 12, 0, 0) };
        kind.SetResourceReference(TextBlock.ForegroundProperty, "Petal.TextSecondary");
        panel.Children.Add(kind);
        return panel;
    }

    internal static BitmapSource Decode(byte[] encoded)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = new MemoryStream(encoded);
        image.EndInit();
        image.Freeze();
        return image;
    }

    private void OpenWithApp()
    {
        try
        {
            Process.Start(new ProcessStartInfo(_path) { UseShellExecute = true });
            Close();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Error(ex, "Opening a file from Quick Look");
        }
    }

    /// <summary>The file shown floats in its own window, where this one was and at its size, and stays when File Explorer is left.</summary>
    private void FloatIt()
    {
        if (_path.Length == 0 || !File.Exists(_path))
            return;
        GetCursorPos(out var at);
        var where = new Rect(Left, Top, Width, Height);
        var path = _path;
        Close();
        _ = Actions.Float.FloatWindow.ShowAsync(path, new ScreenPoint(at.X, at.Y), where);
    }

    private void OpenWheel()
    {
        GetCursorPos(out var at);
        var files = _files;
        Close();
        _openWheel?.Invoke(files, new ScreenPoint(at.X, at.Y));
    }
}
