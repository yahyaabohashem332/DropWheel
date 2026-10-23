using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Petal.App.Localization;
using Petal.App.Settings;
using Petal.App.Shell;
using Petal.App.Tools;
using Petal.App.Tools.Parts;
using Petal.App.Util;
using Petal.Engines.Imaging;
using Petal.Recipes.Search;

namespace Petal.App.Search;

/// <summary>
/// Search by Meaning: type what a file is about ("receipt from the pharmacy", "صور البحر") and
/// Petal finds it by the words in it, its name, or what a picture or video shows. Results can be
/// opened, shown in File Explorer, or dragged straight into another app.
/// </summary>
public sealed class SearchWindow : ToolWindow
{
    private static SearchWindow? _open;

    private readonly TextBox _query = new() { FontSize = 16, MinHeight = 38, VerticalContentAlignment = VerticalAlignment.Center };
    private readonly TextBlock _status;
    private readonly Border _off = new();
    private readonly ModelPanel _models = new(Meaning.Models);
    private readonly TextBlock _modelsHint;
    private readonly StackPanel _results = new();
    private readonly DispatcherTimer _typing;
    private readonly DispatcherTimer _refresh;
    private CancellationTokenSource? _search;
    private CancellationTokenSource? _asking;
    private string? _first;
    // Ask your files: the button that shows while a question is typed, and the answer above the results.
    private readonly Button _ask;
    private readonly StackPanel _answer = new() { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 12, 0, 0) };
    private readonly ModelPanel _askModel = new(Petal.Recipes.AskPetal.Models) { Visibility = Visibility.Collapsed };

    /// <summary>Opens the window (or brings it forward); with <paramref name="query"/> typed in, and asked as a question when <paramref name="ask"/>.</summary>
    public static void ShowSingle(string? query = null, bool ask = false)
    {
        if (_open is null)
        {
            _open = new SearchWindow();
            _open.Closed += (_, _) => _open = null;
            _open.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            _open.Show();
        }
        _open.Activate();
        _open._query.Focus();
        if (query is null)
            return;
        _open._query.Text = query;
        _open._query.CaretIndex = query.Length;
        if (ask)
            _ = _open.AskAsync();
    }

    /// <summary>For design snapshots: the window with a few made-up results.</summary>
    internal static SearchWindow ForSnapshot(IReadOnlyList<string> pictures, IReadOnlyList<string> documents)
    {
        var window = new SearchWindow(live: false);
        window._query.Text = L.IsArabic ? "البحر" : "beach";
        window.Show(new SearchResults(
            pictures.Select((p, i) => new SearchHit(p, IndexedKind.Picture, 0.3 - i * 0.01, true)).ToList(),
            documents.Select(d => new SearchHit(d, IndexedKind.Document, 1, false, L.IsArabic ? "رحلة إلى «البحر» في الصيف…" : "…our trip to the «beach» in July…")).ToList()));
        window._status.Text = L.P(1284, "{0} file in the index", "{0} files in the index");
        return window;
    }

    private SearchWindow(bool live = true) : base("Search by Meaning", 620)
    {
        _query.FlowDirection = L.IsRightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        _ask = new Button { Content = L.T("Ask Your Files"), Margin = new Thickness(8, 0, 0, 0), Visibility = Visibility.Collapsed, VerticalAlignment = VerticalAlignment.Stretch };
        _ask.ToolTip = L.T("An answer from what your documents say, with the files it came from.");
        _ask.Click += async (_, _) => await AskAsync();
        var queryRow = new DockPanel();
        DockPanel.SetDock(_ask, Dock.Right);
        queryRow.Children.Add(_ask);
        queryRow.Children.Add(_query);
        Body.Children.Add(queryRow);
        _status = Text("", "Petal.Caption");
        _status.Margin = new Thickness(0, 6, 0, 0);
        var now = new Button { Content = L.T("Index Now"), Style = (Style)FindResource("Petal.SubtleButton"), Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(6, 0, 6, 0) };
        now.SetResourceReference(ForegroundProperty, "Petal.Accent");
        now.Click += (_, _) => { SearchService.Hurry(); UpdateStatus(); };
        var statusRow = new DockPanel();
        DockPanel.SetDock(now, Dock.Right);
        statusRow.Children.Add(now);
        statusRow.Children.Add(_status);
        Body.Children.Add(statusRow);

        // Off: what it would do, and the switch.
        _off.Margin = new Thickness(0, 12, 0, 0);
        _off.CornerRadius = new CornerRadius(10);
        _off.Padding = new Thickness(14, 12, 14, 12);
        _off.SetResourceReference(Border.BackgroundProperty, "Petal.AccentSoft");
        var turnOn = new Button { Content = L.T("Turn On"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 10, 0, 0) };
        turnOn.Click += (_, _) =>
        {
            SettingsStore.Current.SearchEnabled = true;
            SettingsStore.Save();
            if (App.Current?.Controller?.Jobs.Converters is { } converters)
                SearchService.Start(converters);
            SearchService.Hurry();
            Sync();
        };
        var what = Text("Petal reads the files in your Pictures, Documents, Desktop, Downloads and Videos folders while the PC is idle: the words in documents and screenshots, and what photos and videos show. The index stays on this PC.", "Petal.Body");
        what.TextWrapping = TextWrapping.Wrap;
        _off.Child = new StackPanel { Children = { what, turnOn } };
        Body.Children.Add(_off);

        _modelsHint = Text("To find photos and videos by what they show, Petal needs its picture models.", "Petal.Caption");
        _modelsHint.Margin = new Thickness(0, 12, 0, 0);
        Body.Children.Add(_modelsHint);
        Body.Children.Add(_models);
        _models.Ready += () =>
        {
            SearchService.ReadPicturesAgain();
            Sync();
        };

        _askModel.Ready += async () => await AskAsync();
        Body.Children.Add(new ScrollViewer
        {
            MaxHeight = 470,
            Margin = new Thickness(0, 12, 0, 0),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = new StackPanel { Children = { _answer, _askModel, _results } },
        });

        _typing = new DispatcherTimer(TimeSpan.FromMilliseconds(280), DispatcherPriority.Input, (_, _) => { _typing!.Stop(); _ = SearchAsync(); }, Dispatcher);
        _refresh = new DispatcherTimer(TimeSpan.FromSeconds(3), DispatcherPriority.Background, (_, _) => UpdateStatus(), Dispatcher);
        // These constructors start the timers; they run only when wanted.
        _typing.Stop();
        _refresh.Stop();
        if (live)
        {
            _query.TextChanged += (_, _) =>
            {
                _typing.Stop();
                _typing.Start();
                _ask.Visibility = SettingsStore.Current.SearchEnabled && Petal.Recipes.Search.FileQuestions.LooksLikeQuestion(_query.Text) ? Visibility.Visible : Visibility.Collapsed;
            };
            _query.KeyDown += async (_, e) =>
            {
                if (e.Key == Key.Enter && _ask.Visibility == Visibility.Visible)
                    await AskAsync();
                else if (e.Key == Key.Enter && _first is not null)
                    Open(_first);
                else if (e.Key == Key.Escape)
                    Close();
            };
            _refresh.Start();
            Loaded += (_, _) => _query.Focus();
        }
        Closed += (_, _) => { _refresh.Stop(); _search?.Cancel(); _asking?.Cancel(); };
        PrimaryButton.Content = L.T("Done");
        PrimaryButton.Click += (_, _) => Close();
        FooterNote = "Enter opens the first result, or asks your files when you type a question. Drag a result into any app.";
        Sync();
    }

    private void Sync()
    {
        bool on = SettingsStore.Current.SearchEnabled;
        _off.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        _models.Visibility = on && !Meaning.IsReady ? Visibility.Visible : Visibility.Collapsed;
        _modelsHint.Visibility = _models.Visibility;
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        if (!SettingsStore.Current.SearchEnabled)
        {
            _status.Text = L.T("Search by Meaning is off.");
            return;
        }
        var status = SearchService.Status();
        _status.Text = L.P(status.Files, "{0} file in the index", "{0} files in the index")
            + (status.Waiting > 0 ? " · " + L.P(status.Waiting, "{0} waiting to be read", "{0} waiting to be read") : "");
    }

    private async Task SearchAsync()
    {
        var query = _query.Text.Trim();
        _search?.Cancel();
        var search = _search = new CancellationTokenSource();
        if (query.Length == 0)
        {
            _results.Children.Clear();
            _first = null;
            return;
        }
        try
        {
            var results = await Task.Run(() => SearchService.Search(query), search.Token);
            if (search == _search)
                Show(results);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Error(ex, "Searching");
        }
    }

    /// <summary>
    /// Ask your files: the passages that mention the question, and an answer in a sentence from them when the AI
    /// model is on this PC (without it, the passages are the answer to read).
    /// </summary>
    private async Task AskAsync()
    {
        var question = _query.Text.Trim();
        if (question.Length == 0 || !SettingsStore.Current.SearchEnabled)
            return;
        _asking?.Cancel();
        var asking = _asking = new CancellationTokenSource();
        _askModel.Visibility = Visibility.Collapsed;
        _answer.Visibility = Visibility.Visible;
        _answer.Children.Clear();
        _answer.Children.Add(Text("Looking through your files…", "Petal.Caption"));
        _ask.IsEnabled = false;
        try
        {
            var candidates = await Task.Run(() => SearchService.Mentions(Petal.Recipes.Search.FileQuestions.Terms(question)), asking.Token);
            var converters = App.Current.Controller.Jobs.Converters;
            var sources = await Petal.Recipes.Search.FileQuestions.FindAsync(question, candidates, async (path, ct) =>
            {
                // Read again from the file so the passages keep their punctuation; a slow file isn't waited for.
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
                limit.CancelAfter(TimeSpan.FromSeconds(12));
                // Off the interface thread: a plain text file is read at once, however long.
                return await Task.Run(() => Petal.Recipes.DocumentText.ReadAsync(path, converters, SettingsStore.Current.Conversion, null, limit.Token), limit.Token);
            }, asking.Token);
            if (asking != _asking)
                return;
            if (sources.Count == 0)
            {
                ShowAnswer(null, sources);
                return;
            }
            if (Petal.Recipes.AskPetal.ModelReady)
            {
                _answer.Children.Clear();
                _answer.Children.Add(Text("Reading what your files say…", "Petal.Caption"));
            }
            var answer = await Petal.Recipes.Search.FileQuestions.AnswerAsync(question, sources, asking.Token);
            if (asking == _asking)
                ShowAnswer(answer, sources);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or Microsoft.Data.Sqlite.SqliteException)
        {
            Log.Error(ex, "Asking the files");
            _answer.Children.Clear();
            _answer.Children.Add(Text("The question couldn't be answered.", "Petal.Caption"));
        }
        finally
        {
            if (asking == _asking)
                _ask.IsEnabled = true;
        }
    }

    private void ShowAnswer(Petal.Recipes.Search.FileAnswer? answer, IReadOnlyList<Petal.Recipes.Search.FileSource> sources)
    {
        _answer.Children.Clear();
        if (sources.Count == 0)
        {
            _answer.Children.Add(Text("None of your indexed files mention it.", "Petal.Caption"));
            return;
        }
        var card = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(14, 12, 14, 12), Margin = new Thickness(0, 0, 0, 12) };
        card.SetResourceReference(Border.BackgroundProperty, "Petal.AccentSoft");
        var inside = new StackPanel();
        card.Child = inside;
        if (answer is { Found: true })
        {
            inside.Children.Add(Text("Answer", "Petal.Label").WithMargin(0, 0, 0, 4));
            inside.Children.Add(AnswerText(answer.Text, sources));
            inside.Children.Add(Text("From what your files say, read on this PC. Check the source before you rely on it.", "Petal.Caption").WithMargin(0, 6, 0, 0));
        }
        else
        {
            inside.Children.Add(Text(answer is { UsedModel: true } ? "Your files don't seem to say exactly. These passages came closest:" : "These passages mention it:", "Petal.Body"));
            if (!Petal.Recipes.AskPetal.ModelReady)
            {
                _askModel.Visibility = Visibility.Visible;
                inside.Children.Add(Text("With the AI model on this PC, Petal answers in a sentence.", "Petal.Caption").WithMargin(0, 4, 0, 0));
            }
        }
        _answer.Children.Add(card);
        _answer.Children.Add(Text("Sources", "Petal.Label").WithMargin(0, 0, 0, 6));
        foreach (var source in sources)
            _answer.Children.Add(Source(source));
    }

    /// <summary>The answer with its citations ([1], [2]) as links to their files.</summary>
    private TextBlock AnswerText(string text, IReadOnlyList<Petal.Recipes.Search.FileSource> sources)
    {
        var block = Text("", "Petal.Body");
        block.TextWrapping = TextWrapping.Wrap;
        block.FontSize = 15;
        block.FlowDirection = Petal.Core.Text.KeyboardLayout.LooksArabic(text) ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        foreach (var part in System.Text.RegularExpressions.Regex.Split(text, @"(\[\d{1,2}\])"))
        {
            var m = System.Text.RegularExpressions.Regex.Match(part, @"^\[(\d{1,2})\]$");
            if (m.Success && int.TryParse(m.Groups[1].Value, out int n) && sources.FirstOrDefault(s => s.Number == n) is { } source)
            {
                var link = new System.Windows.Documents.Hyperlink(new System.Windows.Documents.Run($"[{n}]")) { ToolTip = source.Path };
                link.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "Petal.Accent");
                link.Click += (_, _) => Open(source.Path);
                block.Inlines.Add(link);
            }
            else
                block.Inlines.Add(new System.Windows.Documents.Run(part));
        }
        return block;
    }

    private FrameworkElement Source(Petal.Recipes.Search.FileSource source)
    {
        var name = Text($"[{source.Number}]  {Path.GetFileName(source.Path)}", "Petal.Body");
        name.FontWeight = FontWeights.SemiBold;
        name.TextTrimming = TextTrimming.CharacterEllipsis;
        var passage = Text(source.Passage.Length > 260 ? source.Passage[..260] + "…" : source.Passage, "Petal.Caption");
        passage.TextWrapping = TextWrapping.Wrap;
        passage.FlowDirection = Petal.Core.Text.KeyboardLayout.LooksArabic(source.Passage) ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        return Interactive(new StackPanel { Margin = new Thickness(0, 0, 0, 10), Children = { name, passage } }, source.Path);
    }

    private void Show(SearchResults results)
    {
        _results.Children.Clear();
        _first = results.ByMeaning.FirstOrDefault()?.Path ?? results.ByWords.FirstOrDefault()?.Path;
        if (results.ByMeaning.Count == 0 && results.ByWords.Count == 0)
        {
            _results.Children.Add(Text("Nothing found.", "Petal.Caption"));
            return;
        }
        if (results.ByMeaning.Count > 0)
        {
            _results.Children.Add(Text("Looks like it", "Petal.Label").WithMargin(0, 0, 0, 6));
            var tiles = new WrapPanel();
            foreach (var hit in results.ByMeaning)
                tiles.Children.Add(Tile(hit));
            _results.Children.Add(tiles);
        }
        if (results.ByWords.Count > 0)
        {
            _results.Children.Add(Text("Mentions it", "Petal.Label").WithMargin(0, results.ByMeaning.Count > 0 ? 12 : 0, 0, 6));
            foreach (var hit in results.ByWords)
                _results.Children.Add(Line(hit));
        }
    }

    private FrameworkElement Tile(SearchHit hit)
    {
        var image = new Image { Width = 132, Height = 100, Stretch = Stretch.UniformToFill };
        Thumbnail(image, hit.Path, 264);
        var name = Text(Path.GetFileName(hit.Path), "Petal.Caption");
        name.TextTrimming = TextTrimming.CharacterEllipsis;
        name.MaxWidth = 132;
        var tile = new StackPanel { Margin = new Thickness(0, 0, 10, 10), Children = { new Border { CornerRadius = new CornerRadius(8), ClipToBounds = true, Child = image }, name } };
        return Interactive(tile, hit.Path);
    }

    private FrameworkElement Line(SearchHit hit)
    {
        var icon = new Image { Width = 32, Height = 32, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Top };
        Thumbnail(icon, hit.Path, 64);
        var name = Text(Path.GetFileName(hit.Path), "Petal.Body");
        name.FontWeight = FontWeights.SemiBold;
        name.TextTrimming = TextTrimming.CharacterEllipsis;
        var folder = Text(Path.GetDirectoryName(hit.Path) ?? "", "Petal.Caption");
        folder.TextTrimming = TextTrimming.CharacterEllipsis;
        folder.FlowDirection = FlowDirection.LeftToRight;
        // Left is the start of the line, mirrored in Arabic: the path stays under the name.
        folder.HorizontalAlignment = HorizontalAlignment.Left;
        var words = new StackPanel { Children = { name, folder } };
        if (hit.Snippet is { } snippet)
            words.Children.Add(Snippet(snippet));
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        DockPanel.SetDock(icon, Dock.Left);
        row.Children.Add(icon);
        row.Children.Add(words);
        return Interactive(row, hit.Path);
    }

    /// <summary>The words around a match, the matched words in bold.</summary>
    private TextBlock Snippet(string snippet)
    {
        var block = Text("", "Petal.Caption");
        block.TextWrapping = TextWrapping.Wrap;
        block.FlowDirection = snippet.Any(c => c is >= '\u0590' and <= '\u08FF') ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        foreach (var (part, i) in snippet.Split('«', '»').Select((p, i) => (p, i)))
            block.Inlines.Add(new System.Windows.Documents.Run(part) { FontWeight = i % 2 == 1 ? FontWeights.SemiBold : FontWeights.Normal });
        return block;
    }

    private static void Thumbnail(Image target, string path, int size) =>
        _ = Task.Run(() => ShellIcons.Thumbnail(path, size)).ContinueWith(t => target.Source = t.Result, TaskScheduler.FromCurrentSynchronizationContext());

    /// <summary>Click shows it in File Explorer, double-click opens it, and it can be dragged out.</summary>
    private FrameworkElement Interactive(FrameworkElement element, string path)
    {
        var frame = new Border { Background = Brushes.Transparent, Cursor = Cursors.Hand, Child = element, ToolTip = path };
        Point? down = null;
        frame.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2)
            {
                Open(path);
                return;
            }
            down = e.GetPosition(frame);
        };
        frame.MouseMove += (_, e) =>
        {
            if (down is not { } start || e.LeftButton != MouseButtonState.Pressed)
                return;
            var at = e.GetPosition(frame);
            if (Math.Abs(at.X - start.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(at.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance)
                return;
            down = null;
            DragDrop.DoDragDrop(frame, new DataObject(DataFormats.FileDrop, new[] { path }), DragDropEffects.Copy | DragDropEffects.Link);
        };
        frame.MouseLeftButtonUp += (_, _) =>
        {
            if (down is not null)
                ExplorerSelection.Reveal([path]);
            down = null;
        };
        return frame;
    }

    private static void Open(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            ExplorerSelection.Reveal([path]);
        }
    }
}
