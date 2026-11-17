using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Petal.App.Localization;
using Petal.App.Shell;
using Petal.Core.Formats;
using Petal.Engines.Documents;

namespace Petal.App.Actions.Float;

/// <summary>
/// What a floating window shows of a file, read-only: a picture to zoom about, a document's pages, a sheet's cells, a
/// video or a song playing, a text. Each says its own facts (size, page, length), and may zoom, find, copy and add
/// to the window's menu.
/// </summary>
internal abstract class FloatView : Grid, IDisposable
{
    protected FloatView(string path)
    {
        Path = path;
        ClipToBounds = true;
    }

    public string Path { get; }

    /// <summary>What the header says beside the name: a picture's size, the page shown, a video's length…</summary>
    public string Facts { get; private set; } = "";

    /// <summary>The facts or the zoom changed.</summary>
    public event Action? Changed;

    protected void Tell(string? facts = null)
    {
        if (facts is not null)
            Facts = facts;
        Changed?.Invoke();
    }

    public virtual FloatKind Kind => FloatKind.Other;

    /// <summary>The content's own size in device-independent pixels (a picture's), for the window's first size; empty when it has none.</summary>
    public virtual Size Natural => Size.Empty;

    /// <summary>The zoom as the header shows it ("125%"); null when this view doesn't zoom.</summary>
    public virtual string? ZoomText => null;

    /// <summary>Zooms by a factor about the middle (the + and − keys).</summary>
    public virtual void ZoomBy(double factor)
    {
    }

    /// <summary>Back to all of it in view (Ctrl+0).</summary>
    public virtual void FitAll()
    {
    }

    /// <summary>A picture at its own size, one of its pixels for one of the screen's (Ctrl+1).</summary>
    public virtual void OwnSize()
    {
    }

    public virtual bool CanFind => false;

    /// <summary>The next (or the one before) of what matches; the match shown and how many there are, (0, 0) for none.</summary>
    public virtual Task<(int At, int Of)> FindAsync(string text, bool forward, CancellationToken cancellationToken) => Task.FromResult((0, 0));

    /// <summary>The view's own lines in the window's menu (MenuItems and Separators).</summary>
    public virtual IEnumerable<Control> MenuItems() => [];

    /// <summary>Ctrl+C: copies what the view holds (the picture, the page's text, the cells chosen); false when it has nothing to copy.</summary>
    public virtual bool Copy() => false;

    public virtual void Dispose()
    {
    }

    /// <summary>A line for a menu.</summary>
    protected static MenuItem Item(string text, Action click, bool isChecked = false)
    {
        var item = new MenuItem { Header = L.T(text), IsChecked = isChecked };
        item.Click += (_, _) => click();
        return item;
    }

    /// <summary>A quiet line of text in the middle, while something is being made or when there is nothing to show.</summary>
    protected static TextBlock Note(string text)
    {
        var note = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(24), MaxWidth = 460 };
        note.SetResourceReference(TextBlock.ForegroundProperty, "Petal.TextSecondary");
        return note;
    }
}

/// <summary>Picks the view for a file and makes it.</summary>
internal static class FloatViews
{
    public static FloatKind KindOf(string path)
    {
        if (Viewing.IsSheet(path))
            return FloatKind.Sheet;
        if (Viewing.CanLayOut(path))
            return FloatKind.Pages;
        return QuickLookWindow.KindOf(path) switch
        {
            LookKind.Picture => FloatKind.Picture,
            LookKind.Pdf => FloatKind.Pages,
            LookKind.Video => FloatKind.Video,
            LookKind.Sound => FloatKind.Sound,
            LookKind.Text or LookKind.Document => FloatKind.Text,
            _ => FloatKind.Other,
        };
    }

    /// <summary>True for a Markdown file, shown with its headings, lists and tables.</summary>
    public static bool IsMarkdown(string path) => System.IO.Path.GetExtension(path).ToLowerInvariant() is ".md" or ".markdown";

    /// <summary>The view for a file. A picture is read first (its size decides the window's); the rest show their progress themselves.</summary>
    public static async Task<FloatView> CreateAsync(string path, CancellationToken cancellationToken)
    {
        var kind = KindOf(path);
        try
        {
            return kind switch
            {
                FloatKind.Picture => await PictureView.LoadAsync(path, cancellationToken),
                FloatKind.Pages when Viewing.CanLayOut(path) => new LayoutView(path),
                FloatKind.Pages => new PdfView(path, path),
                FloatKind.Sheet => new SheetView(path),
                FloatKind.Video or FloatKind.Sound => new MediaView(path, kind == FloatKind.Video),
                FloatKind.Text when IsMarkdown(path) => new MarkdownView(path),
                FloatKind.Text => new TextFloatView(path, QuickLookWindow.KindOf(path) == LookKind.Document),
                _ => new OtherView(path),
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException or ImageMagick.MagickException or FileFormatException)
        {
            Log.Info($"float: {System.IO.Path.GetFileName(path)} shown as an icon ({ex.GetType().Name}: {ex.Message})");
            return new OtherView(path);
        }
    }
}

/// <summary>What has no view of its own: a ZIP's contents, a font's letters, a folder's names, or the file's icon.</summary>
internal sealed class OtherView : FloatView
{
    public OtherView(string path) : base(path)
    {
        Tell(QuickLookWindow.Facts(path, null));
        _ = FillAsync();
    }

    private async Task FillAsync()
    {
        try
        {
            if (Directory.Exists(Path))
            {
                var names = await Task.Run(() => new DirectoryInfo(Path).EnumerateFileSystemInfos().Take(500).Select(i => i is DirectoryInfo ? i.Name + "\\" : i.Name).ToList());
                Tell(L.P(names.Count, "{0} item", "{0} items"));
                Children.Add(QuickLookWindow.TextView(string.Join(Environment.NewLine, names), code: true));
                return;
            }
            switch (QuickLookWindow.KindOf(Path))
            {
                case LookKind.Archive:
                    var entries = await Task.Run(() => QuickLookWindow.ZipEntries(Path));
                    Tell(QuickLookWindow.Facts(Path, L.P(entries.Count, "{0} item", "{0} items")));
                    Children.Add(QuickLookWindow.TextView(string.Join(Environment.NewLine, entries.Select(e => e.Name + (e.Name.EndsWith('/') ? "" : "   " + Petal.App.Util.Sizes.Format(e.Size)))), code: true));
                    return;
                case LookKind.Font:
                    Children.Add(Font());
                    return;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or FileFormatException)
        {
            Log.Info($"float: {System.IO.Path.GetFileName(Path)} ({ex.Message})");
        }
        Children.Add(QuickLookWindow.Fallback(Path));
    }

    private UIElement Font()
    {
        var family = Fonts.GetFontFamilies(new Uri(Path)).FirstOrDefault() ?? throw new FileFormatException("No font in the file");
        Tell(QuickLookWindow.Facts(Path, family.Source.Split('#').Last()));
        var panel = new StackPanel { Margin = new Thickness(24, 16, 24, 16) };
        foreach (var (size, sample) in new[] { (48.0, "Aa Bb Cc"), (28.0, "The quick brown fox jumps over the lazy dog"), (28.0, "نص عربي للتجربة ١٢٣"), (18.0, "0123456789 !?&@ ABCDEFGHIJKLMNOPQRSTUVWXYZ") })
        {
            var line = new TextBlock { Text = sample, FontFamily = family, FontSize = size, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) };
            line.SetResourceReference(TextBlock.ForegroundProperty, "Petal.Text");
            panel.Children.Add(line);
        }
        return new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }
}
