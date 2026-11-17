using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Petal.App.Localization;
using Petal.App.Shell;
using Petal.Engines.Documents;
using Petal.Engines.Documents.Pdf;

namespace Petal.App.Actions.Float;

/// <summary>
/// A document's pages one under another, as wide as the window (Ctrl and the wheel zoom them). Pages are drawn as they
/// come into view and let go of when far away, so a book of a thousand pages opens at once. It finds words (each
/// found shown on its page), copies a page's text, reads at night in light letters on dark, and asks for a password
/// when the file has one.
/// </summary>
internal sealed class PdfView : FloatView
{
    private const double Gap = 12, Side = 16;

    private readonly string _pdfPath;
    private readonly ScrollViewer _scroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = true, PanningMode = PanningMode.Both };
    private readonly StackPanel _pages = new() { Margin = new Thickness(Side, Gap, Side, Gap) };
    private readonly List<Slot> _slots = [];
    private readonly DispatcherTimer _later = new() { Interval = TimeSpan.FromMilliseconds(60) };
    private readonly SemaphoreSlim _drawing = new(1, 1);
    private readonly CancellationTokenSource _closing = new();
    private PdfDocumentView? _pdf;
    private string? _password;
    private double _zoom = 1;
    private bool _night;
    private int _page;
    private List<PageWords>? _words;
    private List<(int Page, Rect Box)> _found = [];
    private int _foundAt = -1;
    private string _sought = "";

    /// <summary>A page: its place in the column, the picture of it, and the marks of what was found on it.</summary>
    private sealed class Slot
    {
        public required Border Frame;
        public required Image Picture;
        public required Canvas Marks;
        public required PdfPageSize Size;
        public int Drawn;
    }

    /// <summary>A page's words with their boxes, in the page's own points (origin bottom left).</summary>
    private sealed record PageWords(double Width, double Height, int Rotation, List<(string Text, Rect Box)> Words);

    /// <param name="pdfPath">The PDF to show: the file itself, or the one Office laid it out as.</param>
    public PdfView(string path, string pdfPath) : base(path)
    {
        _pdfPath = pdfPath;
        _scroll.Content = _pages;
        _scroll.SetResourceReference(Control.BackgroundProperty, "Petal.Control");
        Children.Add(_scroll);
        _later.Tick += (_, _) =>
        {
            _later.Stop();
            _ = DrawVisibleAsync();
        };
        _scroll.ScrollChanged += (_, _) =>
        {
            ShowPage();
            _later.Stop();
            _later.Start();
        };
        _scroll.SizeChanged += (_, e) =>
        {
            if (e.WidthChanged || e.HeightChanged)
                Lay();
        };
        _scroll.PreviewMouseWheel += (_, e) =>
        {
            if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
                return;
            e.Handled = true;
            ZoomBy(Math.Pow(1.12, e.Delta / 120.0));
        };
        _ = OpenAsync();
    }

    public override FloatKind Kind => FloatKind.Pages;

    public override string? ZoomText => _pdf is null ? null : FloatMath.Percent(_zoom);

    public override bool CanFind => _pdf is not null;

    private async Task OpenAsync()
    {
        Children.Remove(_scroll);
        var waiting = Note(L.T("Opening…"));
        Children.Add(waiting);
        try
        {
            var password = _password;
            _pdf = await Task.Run(() => PdfDocumentView.Open(_pdfPath, password));
        }
        catch (PdfOpenException ex) when (ex.NeedsPassword)
        {
            Children.Remove(waiting);
            AskPassword(_password is not null);
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Children.Remove(waiting);
            Children.Add(QuickLookWindow.Fallback(Path));
            Tell(QuickLookWindow.Facts(Path, null));
            return;
        }
        Children.Remove(waiting);
        Children.Add(_scroll);
        _slots.Clear();
        _pages.Children.Clear();
        foreach (var size in _pdf.PageSizes)
        {
            var picture = new Image { Stretch = Stretch.Fill, FlowDirection = FlowDirection.LeftToRight };
            var marks = new Canvas { IsHitTestVisible = false, FlowDirection = FlowDirection.LeftToRight };
            var frame = new Border
            {
                Background = Brushes.White,
                Margin = new Thickness(0, 0, 0, Gap),
                HorizontalAlignment = HorizontalAlignment.Center,
                Child = new Grid { Children = { picture, marks } },
                Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 8, ShadowDepth = 1, Opacity = 0.25 },
            };
            _slots.Add(new Slot { Frame = frame, Picture = picture, Marks = marks, Size = size });
            _pages.Children.Add(frame);
        }
        // Laid out once the column has its width.
        Dispatcher.BeginInvoke(() =>
        {
            Lay();
            ShowPage();
            _ = DrawVisibleAsync();
        }, DispatcherPriority.Loaded);
    }

    /// <summary>A file with a password: a box for it, under the file's icon.</summary>
    private void AskPassword(bool wrong)
    {
        var box = new PasswordBox { Width = 240, Margin = new Thickness(0, 10, 0, 0), Padding = new Thickness(6, 4, 6, 4) };
        var open = new Button { Content = L.T("Open"), Margin = new Thickness(0, 10, 0, 0), Padding = new Thickness(16, 4, 16, 5), HorizontalAlignment = HorizontalAlignment.Center, IsDefault = true };
        var said = Note(wrong ? L.T("That password didn't open it. Try again.") : L.T("This PDF has a password. Type it to see the pages; it isn't kept."));
        var panel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Children = { said, box, open } };
        void Go()
        {
            _password = box.Password;
            Children.Remove(panel);
            _ = OpenAsync();
        }
        open.Click += (_, _) => Go();
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
                Go();
        };
        Children.Add(panel);
        Tell(QuickLookWindow.Facts(Path, L.T("Locked with a password")));
        Dispatcher.BeginInvoke(() => box.Focus(), DispatcherPriority.Input);
    }

    /// <summary>Each page's size for the window's width and the zoom; the pages drawn again at the new size.</summary>
    private void Lay()
    {
        if (_slots.Count == 0 || _scroll.ActualWidth <= 0)
            return;
        double ratio = _scroll.ExtentHeight > 0 ? _scroll.VerticalOffset / _scroll.ExtentHeight : 0;
        double width = Math.Max(120, (_scroll.ActualWidth - 2 * Side - SystemParameters.VerticalScrollBarWidth) * _zoom);
        foreach (var slot in _slots)
        {
            slot.Frame.Width = width;
            slot.Frame.Height = width * slot.Size.Height / slot.Size.Width;
        }
        _pages.UpdateLayout();
        _scroll.ScrollToVerticalOffset(ratio * _scroll.ExtentHeight);
        DrawMarks();
        _later.Stop();
        _later.Start();
    }

    public override void ZoomBy(double factor)
    {
        _zoom = Math.Clamp(_zoom * factor, 0.25, 4);
        Lay();
        Tell();
    }

    public override void FitAll()
    {
        _zoom = 1;
        Lay();
        Tell();
    }

    /// <summary>The page in the middle of the window, said in the header.</summary>
    private void ShowPage()
    {
        if (_pdf is null || _slots.Count == 0 || double.IsNaN(_slots[0].Frame.Height))
            return;
        double middle = _scroll.VerticalOffset + _scroll.ViewportHeight / 2, top = Gap;
        int page = 0;
        for (int i = 0; i < _slots.Count; i++)
        {
            top += _slots[i].Frame.Height + Gap;
            if (top >= middle)
            {
                page = i;
                break;
            }
            page = i;
        }
        _page = page;
        Tell(QuickLookWindow.Facts(Path, _slots.Count > 1 ? L.F("Page {0} of {1}", page + 1, _slots.Count) : L.P(1, "{0} page", "{0} pages")));
    }

    /// <summary>
    /// The pages in view (and the screen's worth either side) drawn sharp for their size; pages three screens away
    /// let go of, so memory stays small.
    /// </summary>
    private async Task DrawVisibleAsync()
    {
        if (_pdf is not { } pdf || _closing.IsCancellationRequested)
            return;
        double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX, from = _scroll.VerticalOffset - _scroll.ViewportHeight, to = _scroll.VerticalOffset + 2 * _scroll.ViewportHeight;
        double far = 3 * _scroll.ViewportHeight, top = Gap;
        var wanted = new List<(Slot Slot, int Index, int Width)>();
        for (int i = 0; i < _slots.Count; i++)
        {
            var slot = _slots[i];
            double bottom = top + slot.Frame.Height;
            int width = (int)Math.Min(3000, Math.Round(slot.Frame.Width * dpi));
            if (bottom >= from && top <= to)
            {
                if (slot.Drawn != width)
                    wanted.Add((slot, i, width));
            }
            else if (slot.Drawn != 0 && (bottom < _scroll.VerticalOffset - far || top > _scroll.VerticalOffset + _scroll.ViewportHeight + far))
            {
                slot.Picture.Source = null;
                slot.Drawn = 0;
            }
            top = bottom + Gap;
        }
        if (wanted.Count == 0)
            return;
        await _drawing.WaitAsync();
        try
        {
            bool night = _night;
            foreach (var (slot, index, width) in wanted)
            {
                if (_closing.IsCancellationRequested)
                    return;
                var bitmap = await Task.Run(() => pdf.RenderPage(index, width));
                if (night)
                    Invert(bitmap.Pixels);
                var source = BitmapSource.Create(bitmap.Width, bitmap.Height, 96, 96, PixelFormats.Pbgra32, null, bitmap.Pixels, bitmap.Stride);
                source.Freeze();
                slot.Picture.Source = source;
                slot.Frame.Background = night ? Brushes.Black : Brushes.White;
                slot.Drawn = width;
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
        {
            Log.Info($"float: a page of {System.IO.Path.GetFileName(Path)} wasn't drawn ({ex.Message})");
        }
        finally
        {
            _drawing.Release();
        }
    }

    /// <summary>Light letters on dark: each colour turned to its opposite (premultiplied, so only where the page is drawn).</summary>
    internal static void Invert(byte[] pixels)
    {
        for (int i = 0; i < pixels.Length; i += 4)
        {
            byte a = pixels[i + 3];
            pixels[i] = (byte)(a - pixels[i]);
            pixels[i + 1] = (byte)(a - pixels[i + 1]);
            pixels[i + 2] = (byte)(a - pixels[i + 2]);
        }
    }

    private void Redraw()
    {
        foreach (var slot in _slots)
            slot.Drawn = 0;
        _ = DrawVisibleAsync();
    }

    public override IEnumerable<Control> MenuItems() =>
    [
        Item("Read at Night", () =>
        {
            _night = !_night;
            Redraw();
        }, _night),
        Item("Copy This Page's Text", () => Copy()),
    ];

    // ───────────── words ─────────────

    /// <summary>The words of every page with their boxes, read once, the first time they are needed.</summary>
    private async Task<List<PageWords>> WordsAsync(CancellationToken cancellationToken)
    {
        if (_words is not null)
            return _words;
        var path = _pdfPath;
        var password = _password;
        _words = await Task.Run(() =>
        {
            byte[] bytes;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                bytes = new byte[stream.Length];
                stream.ReadExactly(bytes);
            }
            using var document = UglyToad.PdfPig.PdfDocument.Open(bytes, new UglyToad.PdfPig.ParsingOptions { Password = password ?? "", UseLenientParsing = true });
            var pages = new List<PageWords>();
            foreach (var page in document.GetPages())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var words = page.GetWords().Select(w => (w.Text, new Rect(w.BoundingBox.Left, w.BoundingBox.Bottom, Math.Max(0, w.BoundingBox.Width), Math.Max(0, w.BoundingBox.Height)))).ToList();
                pages.Add(new PageWords(page.Width, page.Height, page.Rotation.Value, words));
            }
            return pages;
        }, cancellationToken);
        return _words;
    }

    public override async Task<(int At, int Of)> FindAsync(string text, bool forward, CancellationToken cancellationToken)
    {
        text = text.Trim();
        if (text.Length == 0)
        {
            _found = [];
            DrawMarks();
            return (0, 0);
        }
        if (text != _sought)
        {
            var pages = await WordsAsync(cancellationToken);
            _found = Find(pages.Select(p => p.Words.Select(w => (w.Text, w.Box)).ToList()).ToList(), text);
            _sought = text;
            // From the page in view onwards.
            _foundAt = _found.FindIndex(f => f.Page >= _page) is var first and >= 0 ? (forward ? first - 1 : first) : -1;
        }
        if (_found.Count == 0)
        {
            DrawMarks();
            return (0, 0);
        }
        _foundAt = ((forward ? _foundAt + 1 : _foundAt - 1) % _found.Count + _found.Count) % _found.Count;
        BringIntoView(_found[_foundAt].Page);
        DrawMarks();
        return (_foundAt + 1, _found.Count);
    }

    /// <summary>
    /// Where a text is in the pages' words: one word or several in a row, letters compared without case or accents; each
    /// with the box around its words.
    /// </summary>
    internal static List<(int Page, Rect Box)> Find(IReadOnlyList<List<(string Text, Rect Box)>> pages, string text)
    {
        var sought = Fold(text);
        var found = new List<(int, Rect)>();
        if (sought.Length == 0)
            return found;
        int span = Math.Max(1, sought.Count(c => c == ' ') + 1);
        for (int p = 0; p < pages.Count; p++)
        {
            var words = pages[p];
            for (int i = 0; i < words.Count; i++)
            {
                var joined = new StringBuilder();
                var box = Rect.Empty;
                for (int k = i; k < Math.Min(words.Count, i + span); k++)
                {
                    if (joined.Length > 0)
                        joined.Append(' ');
                    joined.Append(Fold(words[k].Text));
                    box.Union(words[k].Box);
                    if (joined.ToString().Contains(sought, StringComparison.Ordinal))
                    {
                        found.Add((p, box));
                        break;
                    }
                }
            }
        }
        return found;
    }

    private static string Fold(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text.Normalize(NormalizationForm.FormD))
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark)
                sb.Append(char.ToLowerInvariant(c));
        return string.Join(' ', sb.ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    private void BringIntoView(int page)
    {
        double top = Gap;
        for (int i = 0; i < page; i++)
            top += _slots[i].Frame.Height + Gap;
        _scroll.ScrollToVerticalOffset(Math.Max(0, top - 24));
    }

    /// <summary>What was found, on each page: the one shown strong, the others light. Pages turned by their file aren't marked.</summary>
    private void DrawMarks()
    {
        foreach (var slot in _slots)
            slot.Marks.Children.Clear();
        if (_words is null)
            return;
        for (int f = 0; f < _found.Count; f++)
        {
            var (page, box) = _found[f];
            var words = _words[page];
            if (words.Rotation != 0 || page >= _slots.Count)
                continue;
            var slot = _slots[page];
            double sx = slot.Frame.Width / words.Width, sy = slot.Frame.Height / words.Height;
            var mark = new Rectangle
            {
                Width = box.Width * sx + 4,
                Height = box.Height * sy + 4,
                Fill = new SolidColorBrush(f == _foundAt ? Color.FromArgb(0x70, 0xFF, 0x8A, 0x00) : Color.FromArgb(0x50, 0xFF, 0xD4, 0x00)),
                RadiusX = 2,
                RadiusY = 2,
            };
            Canvas.SetLeft(mark, box.Left * sx - 2);
            Canvas.SetTop(mark, (words.Height - box.Bottom) * sy - 2);
            slot.Marks.Children.Add(mark);
        }
    }

    /// <summary>The page in view's text, its lines as they are on the page.</summary>
    public override bool Copy()
    {
        if (_pdf is null)
            return false;
        _ = CopyAsync();
        return true;
    }

    private async Task CopyAsync()
    {
        try
        {
            var pages = await WordsAsync(_closing.Token);
            if (_page >= pages.Count)
                return;
            var text = Lines(pages[_page].Words);
            if (text.Length > 0)
                Clipboard.SetText(text);
            Tell(text.Length > 0 ? L.F("The text of page {0} copied", _page + 1) : L.T("This page has no text to copy: it may be a scan."));
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            Tell(L.T("That didn't work.") + " " + ex.Message);
        }
    }

    /// <summary>Words into lines: those at about the same height on one line, left to right, lines from the top down.</summary>
    internal static string Lines(List<(string Text, Rect Box)> words)
    {
        var lines = new List<List<(string Text, Rect Box)>>();
        foreach (var word in words.OrderByDescending(w => w.Box.Top).ThenBy(w => w.Box.Left))
        {
            var line = lines.FirstOrDefault(l => Math.Abs(l[0].Box.Top - word.Box.Top) < Math.Max(2, word.Box.Height * 0.5));
            if (line is null)
                lines.Add([word]);
            else
                line.Add(word);
        }
        return string.Join(Environment.NewLine, lines.Select(l => string.Join(' ', l.OrderBy(w => w.Box.Left).Select(w => w.Text))));
    }

    public override void Dispose()
    {
        _closing.Cancel();
        _later.Stop();
        _pdf?.Dispose();
        _pdf = null;
    }
}

/// <summary>
/// A Word, PowerPoint or OpenDocument file as its pages: laid out once by Office or LibreOffice (invisibly) and kept
/// in the cache by its place, size and time, then shown as a PDF is. Without either, its words.
/// </summary>
internal sealed class LayoutView : FloatView
{
    private readonly CancellationTokenSource _closing = new();
    private FloatView? _inner;

    public LayoutView(string path) : base(path)
    {
        Tell(QuickLookWindow.Facts(path, null));
        _ = LayOutAsync();
    }

    public override FloatKind Kind => FloatKind.Pages;

    public override string? ZoomText => _inner?.ZoomText;

    public override bool CanFind => _inner?.CanFind == true;

    /// <summary>Where a file's pages are kept: by its place, size and time, so a changed file is laid out again.</summary>
    internal static string CachePath(string path)
    {
        var info = new FileInfo(path);
        var key = System.IO.Path.GetFullPath(path).ToUpperInvariant() + "|" + info.Length + "|" + info.LastWriteTimeUtc.Ticks;
        var name = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..24].ToLowerInvariant();
        return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Petal", "cache", "float", name + ".pdf");
    }

    private async Task LayOutAsync()
    {
        try
        {
            var pdf = CachePath(Path);
            if (!File.Exists(pdf))
            {
                if (!Viewing.CanLayOutHere(Path))
                {
                    await WordsAsync(L.T("Microsoft Office or LibreOffice would show its pages as they are laid out; here are its words."));
                    return;
                }
                Children.Add(Note(L.T("Laying out the pages… Office works on it unseen; the first time can take a little while.")));
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(pdf)!);
                var temporary = pdf + ".tmp.pdf";
                bool made = await Viewing.LayOutAsync(Path, temporary, _closing.Token);
                if (!made)
                {
                    Children.Clear();
                    await WordsAsync(L.T("The pages couldn't be laid out; here are its words."));
                    return;
                }
                File.Move(temporary, pdf, true);
            }
            Show(new PdfView(Path, pdf));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Log.Info($"float: {System.IO.Path.GetFileName(Path)} not laid out ({ex.Message})");
            Children.Clear();
            await WordsAsync(L.T("The pages couldn't be laid out; here are its words."));
        }
    }

    private async Task WordsAsync(string why)
    {
        var inner = new TextFloatView(Path, document: true) { Explained = why };
        Show(inner);
        await Task.CompletedTask;
    }

    private void Show(FloatView inner)
    {
        Children.Clear();
        _inner = inner;
        inner.Changed += () => Tell(inner.Facts);
        Children.Add(inner);
        Tell(inner.Facts);
    }

    public override void ZoomBy(double factor) => _inner?.ZoomBy(factor);

    public override void FitAll() => _inner?.FitAll();

    public override Task<(int At, int Of)> FindAsync(string text, bool forward, CancellationToken cancellationToken) =>
        _inner?.FindAsync(text, forward, cancellationToken) ?? Task.FromResult((0, 0));

    public override IEnumerable<Control> MenuItems() => _inner?.MenuItems() ?? [];

    public override bool Copy() => _inner?.Copy() == true;

    public override void Dispose()
    {
        _closing.Cancel();
        _inner?.Dispose();
    }
}
