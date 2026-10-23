using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Petal.App.Localization;
using Petal.App.Settings;
using Petal.App.Tools;
using Petal.App.Tools.Parts;
using Petal.App.Visuals;
using Petal.Recipes;

namespace Petal.App.Guide;

/// <summary>
/// The Tools Guide: every tool, file action and action of the wheels without files, by section, each with what
/// it does, how to use it, examples, "Try it" and a question box of its own. At the top, say what you want to do
/// in your own words ("a countdown", "أريد أداة للعدّ التنازلي", "make this PDF smaller"): the closest tools come
/// at once, and Enter asks the AI model on this PC, which answers from the guide and links the tools it names.
/// </summary>
public sealed class GuideWindow : ToolWindow
{
    private static GuideWindow? _open;

    private readonly TextBox _request = new() { FontSize = 15, MinHeight = 38, VerticalContentAlignment = VerticalAlignment.Center };
    private readonly TextBlock _placeholder;
    private readonly Button _askButton;
    private readonly StackPanel _answer = new() { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 12, 0, 0) };
    private readonly ModelPanel _model = new(AskPetal.Models) { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 8, 0, 0) };
    private readonly StackPanel _sections = new();
    private readonly StackPanel _list = new();
    private readonly ScrollViewer _listScroll;
    private readonly TextBlock _listTitle;
    private readonly DispatcherTimer _typing;
    private readonly Dictionary<string, FrameworkElement> _rows = [];
    private string _section = GuideBook.Categories[0];
    private string? _expanded;
    private bool _searching;
    private CancellationTokenSource? _asking;

    /// <summary>Opens the guide (or brings it forward), at an entry or with a request asked.</summary>
    public static void ShowSingle(string? key = null, string? request = null)
    {
        if (_open is null)
        {
            _open = new GuideWindow();
            _open.Closed += (_, _) => _open = null;
            _open.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            _open.Show();
        }
        _open.Activate();
        if (key is not null && GuideBook.Get(key) is not null)
            _open.Reveal(key);
        if (request is { Length: > 0 })
        {
            _open._request.Text = request;
            _open._request.CaretIndex = request.Length;
            _ = _open.AskAsync();
        }
        else
            _open._request.Focus();
    }

    /// <summary>For design snapshots: the guide at an entry, opened.</summary>
    internal static GuideWindow ForSnapshot(string? key, string? request)
    {
        var window = new GuideWindow(live: false);
        if (key is not null)
            window.Reveal(key);
        if (request is not null)
        {
            window._request.Text = request;
            window.ShowFound(GuideSearch.Find(request));
        }
        return window;
    }

    private GuideWindow(bool live = true) : base("Tools Guide", 760)
    {
        _request.FlowDirection = L.Direction;
        _placeholder = new TextBlock
        {
            Text = L.T("Say what you want to do: a countdown, a smaller PDF, the text in a picture…"),
            FontSize = 14,
            IsHitTestVisible = false,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 10, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        _placeholder.SetResourceReference(TextBlock.ForegroundProperty, "Petal.TextSecondary");
        _askButton = new Button { Content = L.T("Ask"), Margin = new Thickness(8, 0, 0, 0), MinWidth = 80, Style = (Style)FindResource("Petal.PrimaryButton") };
        _askButton.Click += async (_, _) => await AskAsync();
        var requestRow = new DockPanel();
        DockPanel.SetDock(_askButton, Dock.Right);
        requestRow.Children.Add(_askButton);
        requestRow.Children.Add(new Grid { Children = { _request, _placeholder } });
        Body.Children.Add(requestRow);
        Body.Children.Add(_answer);
        Body.Children.Add(_model);
        _model.Ready += async () => await AskAsync();

        // The sections down the side, their entries beside them.
        var columns = new Grid { Margin = new Thickness(0, 14, 0, 0), Height = 470 };
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(178) });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var sectionsScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _sections, Margin = new Thickness(0, 0, 12, 0) };
        columns.Children.Add(sectionsScroll);
        _listTitle = Text("", "Petal.Label");
        _listTitle.Margin = new Thickness(2, 0, 0, 8);
        _listScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _list };
        var right = new DockPanel();
        DockPanel.SetDock(_listTitle, Dock.Top);
        right.Children.Add(_listTitle);
        right.Children.Add(_listScroll);
        Grid.SetColumn(right, 1);
        columns.Children.Add(right);
        Body.Children.Add(columns);

        foreach (var section in GuideBook.Categories.Where(c => GuideBook.Keys.Any(k => GuideBook.Get(k)?.Category == c)))
            _sections.Children.Add(SectionButton(section));

        _typing = new DispatcherTimer(TimeSpan.FromMilliseconds(180), DispatcherPriority.Input, (_, _) => { _typing!.Stop(); Filter(); }, Dispatcher);
        _typing.Stop();
        _request.TextChanged += (_, _) =>
        {
            _placeholder.Visibility = _request.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (live)
            {
                _typing.Stop();
                _typing.Start();
            }
        };
        _request.KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                await AskAsync();
            }
        };
        Closed += (_, _) => { _typing.Stop(); _asking?.Cancel(); };
        PrimaryButton.Content = L.T("Done");
        PrimaryButton.IsDefault = false;
        PrimaryButton.Click += (_, _) => Close();
        FooterNote = "Answers come from Petal's own guide, worked out on this PC.";
        ShowSection(_section);
    }

    // ---------- the sections ----------

    private Button SectionButton(string section)
    {
        var button = new Button
        {
            Style = (Style)FindResource("Petal.SubtleButton"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(10, 6, 10, 6),
            Margin = new Thickness(0, 0, 0, 2),
            Tag = section,
            Content = new TextBlock { Text = GuideBook.CategoryName(section), TextWrapping = TextWrapping.Wrap },
        };
        button.Click += (_, _) =>
        {
            _request.Text = "";
            ShowSection(section);
        };
        return button;
    }

    private void ShowSection(string section)
    {
        _section = section;
        _searching = false;
        foreach (Button b in _sections.Children)
        {
            bool on = (string)b.Tag == section;
            if (on)
                b.SetResourceReference(BackgroundProperty, "Petal.AccentSoft");
            else
                b.ClearValue(BackgroundProperty);
            if (b.Content is TextBlock t)
                t.FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
        }
        _listTitle.Text = GuideBook.CategoryName(section);
        FillList(GuideBook.Keys.Where(k => GuideBook.Get(k)?.Category == section).OrderBy(GuideBook.Name, StringComparer.CurrentCulture));
    }

    private void FillList(IEnumerable<string> keys)
    {
        _list.Children.Clear();
        _rows.Clear();
        foreach (var key in keys)
        {
            var row = Row(key);
            _rows[key] = row;
            _list.Children.Add(row);
        }
        _listScroll.ScrollToTop();
    }

    /// <summary>Opens an entry where it is listed: its section, unfolded, scrolled to.</summary>
    private void Reveal(string key)
    {
        if (!_rows.ContainsKey(key) && GuideBook.Get(key) is { } e)
            ShowSection(e.Category);
        _expanded = key;
        Refold();
        if (_rows.TryGetValue(key, out var row))
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => row.BringIntoView(new Rect(0, 0, 1, 1)));
    }

    // ---------- an entry ----------

    private FrameworkElement Row(string key)
    {
        var e = GuideBook.Get(key)!;
        var card = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 10, 12, 10), Margin = new Thickness(0, 0, 4, 6), Tag = key };
        card.SetResourceReference(Border.BackgroundProperty, "Petal.Control");
        var inside = new StackPanel();
        card.Child = inside;

        var head = new DockPanel { Background = Brushes.Transparent, Cursor = Cursors.Hand };
        var icon = Icons.Create(GuideBook.Icon(key), 18, Palette.Brush(Theme.Current.Accent), 2);
        icon.VerticalAlignment = VerticalAlignment.Top;
        icon.Margin = new Thickness(0, 2, 10, 0);
        DockPanel.SetDock(icon, Dock.Left);
        head.Children.Add(icon);
        var chevron = Icons.Create("chevronDown", 14, Palette.Brush(Theme.Current.TextSecondary), 2);
        chevron.VerticalAlignment = VerticalAlignment.Center;
        chevron.Margin = new Thickness(8, 0, 0, 0);
        chevron.RenderTransformOrigin = new Point(0.5, 0.5);
        DockPanel.SetDock(chevron, Dock.Right);
        head.Children.Add(chevron);
        var name = Text("", "Petal.Body");
        name.Text = GuideBook.Name(key);
        name.FontWeight = FontWeights.SemiBold;
        var summary = Text("", "Petal.Caption");
        summary.Text = e.Summary;
        summary.TextWrapping = TextWrapping.Wrap;
        head.Children.Add(new StackPanel { Children = { name, summary } });
        head.MouseLeftButtonUp += (_, _) =>
        {
            _expanded = _expanded == key ? null : key;
            Refold();
        };
        inside.Children.Add(head);

        var more = new StackPanel { Margin = new Thickness(28, 10, 0, 2), Visibility = Visibility.Collapsed, Tag = "more" };
        more.Children.Add(GuideViews.Explanation(key, withHeader: false, withSummary: false));
        more.Children.Add(Actions(key));
        inside.Children.Add(more);
        return card;
    }

    /// <summary>Unfolds the open entry and folds the rest.</summary>
    private void Refold()
    {
        foreach (var (key, row) in _rows)
        {
            if (row is not Border { Child: StackPanel inside })
                continue;
            bool open = key == _expanded;
            foreach (var child in inside.Children.OfType<FrameworkElement>().Where(c => Equals(c.Tag, "more")))
                child.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            if (inside.Children[0] is DockPanel head && head.Children[1] is FrameworkElement chevron)
                chevron.RenderTransform = new RotateTransform(open ? 180 : 0);
        }
    }

    /// <summary>"Try it", and a question box for this one entry with its answer under it.</summary>
    private FrameworkElement Actions(string key)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        var buttons = new WrapPanel();
        var status = Text("", "Petal.Caption");
        status.TextWrapping = TextWrapping.Wrap;
        status.Visibility = Visibility.Collapsed;
        if (GuideBook.CanRun(key))
        {
            var tryIt = new Button { Content = L.T(GuideBook.NeedsFiles(key) ? "Try It on Files…" : "Try It"), Margin = new Thickness(0, 0, 8, 6), Style = (Style)FindResource("Petal.PrimaryButton") };
            tryIt.Click += async (_, _) => await TryAsync(key, status);
            buttons.Children.Add(tryIt);
        }
        var question = new TextBox { MinWidth = 240, MinHeight = 32, VerticalContentAlignment = VerticalAlignment.Center, FlowDirection = L.Direction, Margin = new Thickness(0, 0, 8, 6) };
        var hint = new TextBlock { Text = L.T("Ask about it…"), IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 8, 6) };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "Petal.TextSecondary");
        question.TextChanged += (_, _) => hint.Visibility = question.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        var askAbout = new Button { Content = L.T("Ask"), Margin = new Thickness(0, 0, 0, 6) };
        var reply = new StackPanel();
        async Task Ask()
        {
            if (question.Text.Trim() is not { Length: > 0 } q)
                return;
            await AskAboutAsync(key, q, reply);
        }
        askAbout.Click += async (_, _) => await Ask();
        question.KeyDown += async (_, ev) =>
        {
            if (ev.Key == Key.Enter)
            {
                ev.Handled = true;
                await Ask();
            }
        };
        buttons.Children.Add(new Grid { Children = { question, hint } });
        buttons.Children.Add(askAbout);
        panel.Children.Add(buttons);
        panel.Children.Add(status);
        panel.Children.Add(reply);
        return panel;
    }

    private async Task TryAsync(string key, TextBlock status)
    {
        status.Visibility = Visibility.Collapsed;
        try
        {
            if (await GuideRunner.RunAsync(key, this) is { } why)
            {
                status.Text = why;
                status.Visibility = Visibility.Visible;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.IO.IOException or System.ComponentModel.Win32Exception)
        {
            Log.Error(ex, "Trying a tool from the guide");
            status.Text = L.T("It couldn't be started.");
            status.Visibility = Visibility.Visible;
        }
    }

    private async Task AskAboutAsync(string key, string question, StackPanel reply)
    {
        reply.Children.Clear();
        if (!AskPetal.ModelReady)
        {
            reply.Children.Add(Text("Questions are answered by the AI model on this PC, downloaded once.", "Petal.Caption").WithMargin(0, 6, 0, 6));
            var model = new ModelPanel(AskPetal.Models);
            model.Ready += async () => await AskAboutAsync(key, question, reply);
            reply.Children.Add(model);
            return;
        }
        reply.Children.Add(Text("Thinking…", "Petal.Caption").WithMargin(0, 6, 0, 0));
        _asking?.Cancel();
        var asking = _asking = new CancellationTokenSource();
        try
        {
            var answer = await GuideAnswer.AboutAsync(key, question, asking.Token);
            if (asking != _asking)
                return;
            reply.Children.Clear();
            reply.Children.Add(answer.Found
                ? AnswerCard(answer, withTools: false)
                : Text("The guide doesn't say. Read how to use it above, or try it.", "Petal.Caption").WithMargin(0, 6, 0, 0));
        }
        catch (OperationCanceledException)
        {
        }
        catch (InvalidOperationException ex)
        {
            Log.Error(ex, "Asking about a tool");
            reply.Children.Clear();
            reply.Children.Add(Text("The question couldn't be answered.", "Petal.Caption").WithMargin(0, 6, 0, 0));
        }
    }

    // ---------- a request at the top ----------

    /// <summary>While typing: the closest entries at once, in place of the section.</summary>
    private void Filter()
    {
        var request = _request.Text.Trim();
        if (request.Length == 0)
        {
            _answer.Visibility = Visibility.Collapsed;
            _model.Visibility = Visibility.Collapsed;
            if (_searching)
                ShowSection(_section);
            return;
        }
        ShowFound(GuideSearch.Find(request, 12));
    }

    private void ShowFound(IReadOnlyList<GuideHit> hits)
    {
        _searching = true;
        foreach (Button b in _sections.Children)
        {
            b.ClearValue(BackgroundProperty);
            if (b.Content is TextBlock t)
                t.FontWeight = FontWeights.Normal;
        }
        _listTitle.Text = L.T(hits.Count == 0 ? "Nothing matches those words. Try others, or ask." : "Closest to what you said");
        FillList(hits.Select(h => h.Key));
        if (hits.Count > 0 && _expanded is null)
        {
            _expanded = hits[0].Key;
            Refold();
        }
    }

    /// <summary>Enter: the closest entries, then the AI model's answer from them (when it is on this PC).</summary>
    private async Task AskAsync()
    {
        var request = _request.Text.Trim();
        if (request.Length == 0)
            return;
        _typing.Stop();
        var hits = GuideSearch.Find(request, 12);
        _expanded = null;
        ShowFound(hits);
        _answer.Visibility = Visibility.Visible;
        _answer.Children.Clear();
        _model.Visibility = Visibility.Collapsed;
        if (!AskPetal.ModelReady)
        {
            _answer.Children.Add(Text(hits.Count > 0
                ? "These come closest to what you said. With the AI model on this PC, Petal also answers in your words."
                : "Nothing in Petal matches those words. With the AI model on this PC, Petal can understand other ways of saying it.", "Petal.Caption"));
            _model.Visibility = Visibility.Visible;
            return;
        }
        if (hits.Count == 0)
        {
            _answer.Children.Add(Text("Nothing in Petal matches those words. Try saying it another way.", "Petal.Caption"));
            return;
        }
        _answer.Children.Add(Text("Thinking…", "Petal.Caption"));
        _askButton.IsEnabled = false;
        _asking?.Cancel();
        var asking = _asking = new CancellationTokenSource();
        try
        {
            var reply = await GuideAnswer.FindAsync(request, hits, asking.Token);
            if (asking != _asking)
                return;
            _answer.Children.Clear();
            if (reply.Found)
            {
                _answer.Children.Add(AnswerCard(reply, withTools: true));
                // The tools it named, first in the list and the first of them open.
                var named = reply.Cited.Concat(hits.Select(h => h.Key).Where(k => !reply.Cited.Contains(k))).ToList();
                _expanded = named[0];
                FillList(named);
                Refold();
            }
            else
                _answer.Children.Add(Text("Petal has no tool for exactly that. These come closest:", "Petal.Caption"));
        }
        catch (OperationCanceledException)
        {
        }
        catch (InvalidOperationException ex)
        {
            Log.Error(ex, "Asking the guide");
            _answer.Children.Clear();
            _answer.Children.Add(Text("The question couldn't be answered. The closest tools are below.", "Petal.Caption"));
        }
        finally
        {
            if (asking == _asking)
                _askButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// The model's answer: for a request, the tools chosen (each a link to its entry, the best first) and how to use the
    /// first for it; for a question about one tool, the answer alone.
    /// </summary>
    private Border AnswerCard(GuideReply reply, bool withTools)
    {
        var card = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(14, 12, 14, 12), Margin = new Thickness(0, 6, 0, 0) };
        card.SetResourceReference(Border.BackgroundProperty, "Petal.AccentSoft");
        var inside = new StackPanel();
        card.Child = inside;
        if (withTools && reply.Cited.Count > 0)
        {
            inside.Children.Add(Text("Best for this", "Petal.Label").WithMargin(0, 0, 0, 4));
            var tools = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 15, Margin = new Thickness(0, 0, 0, 8) };
            for (int i = 0; i < reply.Cited.Count; i++)
            {
                var key = reply.Cited[i];
                if (i > 0)
                    tools.Inlines.Add(new Run("  ·  "));
                var link = new Hyperlink(new Run(GuideBook.Name(key))) { ToolTip = GuideBook.Get(key)?.Summary, FontWeight = i == 0 ? FontWeights.SemiBold : FontWeights.Normal };
                link.SetResourceReference(TextElement.ForegroundProperty, "Petal.Accent");
                link.Click += (_, _) => Reveal(key);
                tools.Inlines.Add(link);
            }
            inside.Children.Add(tools);
        }
        else
            inside.Children.Add(Text("Answer", "Petal.Label").WithMargin(0, 0, 0, 4));
        if (reply.Text.Length > 0)
        {
            var text = Text("", "Petal.Body");
            text.Text = reply.Text;
            text.TextWrapping = TextWrapping.Wrap;
            text.FontSize = 14;
            text.LineHeight = 21;
            text.FlowDirection = Petal.Core.Text.KeyboardLayout.LooksArabic(reply.Text) ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
            inside.Children.Add(text);
        }
        inside.Children.Add(Text("From Petal's guide, by the AI model on this PC.", "Petal.Caption").WithMargin(0, 6, 0, 0));
        return card;
    }
}
