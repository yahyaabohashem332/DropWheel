using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Petal.App.Localization;
using Petal.App.Tools;
using Petal.App.Visuals;
using Petal.Core.Formats;
using Petal.Core.Tools;

namespace Petal.App.Settings;

/// <summary>Cards side by side while they fit, each going under the shorter column, in order; otherwise one under another, across the page.</summary>
internal sealed class CardColumns : Panel
{
    private const double Gap = 14, TwoFrom = 980, One = 760;

    private (int Count, double Width) Columns(double width)
    {
        if (double.IsInfinity(width))
            width = One;
        return InternalChildren.Count > 1 && width >= TwoFrom ? (2, (width - Gap) / 2) : (1, width);
    }

    private Size Place(Size available, bool arrange)
    {
        var (count, width) = Columns(available.Width);
        var heights = new double[count];
        foreach (UIElement child in InternalChildren)
        {
            int column = count == 2 && heights[1] < heights[0] ? 1 : 0;
            if (arrange)
                child.Arrange(new Rect(column * (width + Gap), heights[column], width, child.DesiredSize.Height));
            else
                child.Measure(new Size(width, double.PositiveInfinity));
            heights[column] += child.DesiredSize.Height;
        }
        return new Size(count * width + (count - 1) * Gap, heights.DefaultIfEmpty(0).Max());
    }

    protected override Size MeasureOverride(Size availableSize) => Place(availableSize, false);

    protected override Size ArrangeOverride(Size finalSize)
    {
        Place(finalSize, true);
        return finalSize;
    }
}

/// <summary>The window as the app's main one: its pages down the side, one page at a time.</summary>
public sealed partial class SettingsWindow
{
    /// <summary>A page; one that is a tool's (the guide, the history…) makes the tool when first opened, into <see cref="Holder"/>.</summary>
    private sealed record Page(string Title, string Icon, FrameworkElement Content, Button Button, Func<ToolWindow>? Make = null, ContentControl? Holder = null, bool Fill = false);

    private static int _lastPage;
    private readonly List<Page> _pages = [];
    private readonly Dictionary<FrameworkElement, Page> _pageOfCard = [];
    private readonly Dictionary<Page, ToolWindow> _tools = [];
    private readonly StackPanel _nav = new() { Margin = new Thickness(12, 4, 12, 8) };
    private readonly StackPanel _found = new() { Margin = new Thickness(12, 4, 12, 8), Visibility = Visibility.Collapsed };
    private readonly TextBox _search = new();
    private readonly StackPanel _placesMade = new();
    private readonly StackPanel _placesOwn = new();
    private ComboBox _sideLanguage = null!;
    private List<(FrameworkElement Card, string Title, string Words)>? _index;
    private Page? _current;

    /// <summary>The pages, in their groups, and the side of the window that leads to them.</summary>
    private FrameworkElement Build()
    {
        AddPage("Home", "appWindow", HomePage());
        // What the home page's tiles open: each a page here, not a window of its own.
        AddToolPage("Tools Guide", "book", "Every tool explained, and your questions answered.", Guide.GuideWindow.ForPage, 1760, fill: true);
        AddToolPage("History", "history", "What DropWheel made, and undoing it.", Tools.Windows.HistoryWindow.ForPage, 1320, fill: true);
        Group("The Wheel");
        AddPage("Keys & Behaviour", "keyboard", CardsPage("Keys & Behaviour", "What opens the wheel, and how it behaves.",
            Card("Keys", "Start dragging a file, then tap one of these keys. The wheel stays open until you drop.", KeysSection()),
            Card("The Wheel", "How the wheel behaves under the pointer.", BehaviourSection())));
        AddPage("Wheels", "layers", CardsPage("Wheels", "Your wheels, and more to add from the gallery beside them.",
            Card("Wheels", "Wheels that need no files: quick actions, what's playing, shortcuts for the app in front, and your own. The quick shortcut opens them when nothing is selected; Tab or the mouse wheel goes to the next.", WheelsSection()),
            Card("Wheel Gallery", "Wheels made for apps and for kinds of work. Add one and change it as you like; it doesn't replace yours.", GallerySection())));
        AddPage("Appearance", "palette", CardsPage("Appearance", "Language, theme, colours, and how the wheel looks and sounds.", Split(AppearanceSection(), "Appearance")));
        AddPage("Actions & Favourites", "star", CardsPage("Actions & Favourites", "What comes first on the wheels.",
            Card("Actions Wheel", "The actions on the wheel itself (the rest are under More), and folders that dropped files are copied or moved to.", ActionsWheelSection()),
            Card("Favourites", "Conversions you use often, with their size and quality. They come first on the conversion wheel, with a star.", FavouritesSection())));
        AddPage("Recipes & Automations", "workflow", CardsPage("Recipes & Automations", "Several steps in one petal, folders that convert by themselves, and petals of your own.",
            Card("Recipes", "Several steps in one petal on the tools wheel: remove a background, resize, add a watermark, save as WEBP and zip, in one go.", RecipesSection()),
            Card("Automations", "Watch a folder and convert what arrives in it, such as new pictures in Downloads.", AutomationsSection()),
            Card("Your Petals", "Tools of your own on the tools wheel: a small file says which files a petal takes and which program it runs. DropWheel shows the command before a petal's first run.", PetalsSection())));
        Group("Features");
        AddPage("Screen & Text", "monitor", CardsPage("Screen & Text", "Dictation and live captions, showing clicks and keys, screen recording, and the clipboard history.", Split(ScreenTextSection(), "Screen & Text")));
        AddPage("Work & Sharing", "users", CardsPage("Work & Sharing", "The command palette, snippets, workspaces, and receiving files from other PCs.", Split(WorkSection(), "Work & Sharing")));
        AddPage("AI", "sparkles", CardsPage("AI", "Models on this PC, your own API keys, and searching by meaning.",
            Card("AI Models", "Download a model and run it on this PC, or use your own API key from Gemini, ChatGPT or Claude at your own cost.", AiSection()),
            Card("Search by Meaning", "Find files by what they're about: the words in documents and screenshots, and what photos and videos show. DropWheel reads the folders below while the PC is idle; the index stays on this PC.", SearchSection())));
        AddPage("Conversions", "sliders", CardsPage("Conversions", "Where results go, and their quality.",
            Card("Results", null, OutputSection()),
            Card("Quality", "Defaults used by conversions. Tools have their own settings in their windows.", QualitySection())));
        AddToolPage("Create QR Code", "qr", "From a link or any text.", Tools.Windows.QrWindow.ForPage, 1120);
        Group("App");
        AddPage("Saved Files", "folderOpen", CardsPage("Saved Files", "Where each thing DropWheel makes is saved. Open any folder from here, or copy its path.",
            Card("What You Make", "Change any folder: new files go there, and what is already saved stays where it is.", _placesMade),
            Card("DropWheel's Own Files", null, _placesOwn)));
        AddPage("System", "settings", CardsPage("System", "Starting with Windows, helper programs, backup and updates.",
            Card("General", null, GeneralSection()),
            Card("Helpers", "DropWheel uses these programs when they're installed, on this PC.", EnginesSection()),
            Card("Backup", "All your settings, wheels, favourites, recipes, automations and petals in one file: to keep, or to bring to another PC.", BackupSection()),
            Card("Updates", null, UpdatesSection())));
        Group("Learn");
        AddToolPage("How it works", "info", "A short look at the wheel.", Onboarding.WelcomeWindow.ForPage, 1200);
        AddToolPage("Practice", "target", "Try the wheel on a sample file.", Onboarding.PracticeWindow.ForPage, 1120);
        Closed += (_, _) => EndTools();

        var side = new DockPanel();
        side.SetResourceReference(BackgroundProperty, "Petal.Surface");
        var note = Text(L.F("DropWheel {0} · {1} conversions · {2} tools · {3} actions", Version, ConversionMatrix.TotalPaths, ToolRegistry.All.Count, Petal.Core.Actions.FileActionRegistry.All.Count), "Petal.Caption").WithMargin(20, 8, 16, 14);
        note.FontSize = 11;
        DockPanel.SetDock(note, Dock.Bottom);
        side.Children.Add(note);
        // The language is always here, whichever page is open.
        _sideLanguage = LanguagePicker();
        var globe = ThemedIcon("globe", 17, "Petal.TextSecondary");
        globe.Margin = new Thickness(0, 0, 10, 0);
        globe.VerticalAlignment = VerticalAlignment.Center;
        var languageRow = new DockPanel { Margin = new Thickness(20, 8, 16, 0) };
        DockPanel.SetDock(globe, Dock.Left);
        languageRow.Children.Add(globe);
        languageRow.Children.Add(_sideLanguage);
        DockPanel.SetDock(languageRow, Dock.Bottom);
        side.Children.Add(languageRow);
        var finder = SearchBox();
        DockPanel.SetDock(finder, Dock.Top);
        side.Children.Add(finder);
        side.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = new StackPanel { Children = { _found, _nav } } });

        var edge = new Border { Width = 1, Child = null };
        edge.SetResourceReference(Border.BackgroundProperty, "Petal.Divider");

        var layout = new Grid { ColumnDefinitions = { new ColumnDefinition { Width = new GridLength(248) }, new ColumnDefinition { Width = GridLength.Auto }, new ColumnDefinition() } };
        Grid.SetColumn(edge, 1);
        Grid.SetColumn(_scroll, 2);
        layout.Children.Add(side);
        layout.Children.Add(edge);
        layout.Children.Add(_scroll);
        Go(_pages[Math.Clamp(_lastPage, 0, _pages.Count - 1)]);
        return layout;
    }

    private void Group(string title)
    {
        var heading = Text(title, "Petal.Caption").WithMargin(10, 16, 10, 4);
        heading.FontSize = 11;
        heading.FontWeight = FontWeights.SemiBold;
        _nav.Children.Add(heading);
    }

    /// <summary>A tool as a page: its title and what it is for at the top, the tool under them, no wider than it reads well.</summary>
    /// <summary>
    /// A tool as a page: its title and what it is for at the top, the tool under them, as wide as the window lets it be
    /// up to <paramref name="width"/>. One that fills (the guide, the history) is given the window's height and
    /// scrolls inside itself; the rest scroll with the page.
    /// </summary>
    private void AddToolPage(string title, string icon, string subtitle, Func<ToolWindow> make, double width, bool fill = false)
    {
        var holder = new ContentControl
        {
            Focusable = false, MaxWidth = width, HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = fill ? VerticalAlignment.Stretch : VerticalAlignment.Top,
        };
        FrameworkElement content;
        if (fill)
        {
            var heading = Text(title, "Petal.Title");
            heading.FontSize = 24;
            var grid = new Grid { Margin = new Thickness(28, 22, 28, 18), RowDefinitions = { new RowDefinition { Height = GridLength.Auto }, new RowDefinition { Height = GridLength.Auto }, new RowDefinition() } };
            grid.Children.Add(heading);
            var under = Text(subtitle, "Petal.Caption").WithMargin(0, 4, 0, 16);
            Grid.SetRow(under, 1);
            grid.Children.Add(under);
            Grid.SetRow(holder, 2);
            holder.HorizontalAlignment = HorizontalAlignment.Left;
            grid.Children.Add(holder);
            // As wide as the page is, up to its widest.
            grid.SizeChanged += (_, e) => holder.Width = Math.Min(width, Math.Max(0, e.NewSize.Width - 56));
            content = grid;
        }
        else
        {
            // From the page's start edge, as wide as the page is up to its widest (a box limited in width would centre itself).
            content = Titled(title, subtitle, holder);
            holder.HorizontalAlignment = HorizontalAlignment.Left;
            content.SizeChanged += (_, e) => holder.Width = Math.Min(width, Math.Max(0, e.NewSize.Width - 56));
        }
        AddPage(title, icon, content, make, holder, fill);
    }

    private void AddPage(string title, string icon, FrameworkElement content, Func<ToolWindow>? make = null, ContentControl? holder = null, bool fill = false)
    {
        var glyph = ThemedIcon(icon, 17, "Petal.TextSecondary");
        glyph.Margin = new Thickness(0, 0, 10, 0);
        glyph.VerticalAlignment = VerticalAlignment.Center;
        var words = Text(title);
        words.VerticalAlignment = VerticalAlignment.Center;
        var button = new Button
        {
            Style = (Style)FindResource("Petal.SubtleButton"),
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 0, 0, 2),
            Content = new StackPanel { Orientation = Orientation.Horizontal, Children = { glyph, words } },
        };
        System.Windows.Automation.AutomationProperties.SetName(button, L.T(title));
        var page = new Page(title, icon, content, button, make, holder, fill);
        button.Click += (_, _) => Go(page);
        _pages.Add(page);
        _nav.Children.Add(button);
        foreach (var card in _unplaced)
            _pageOfCard[card] = page;
        _unplaced.Clear();
    }

    private void Go(Page page)
    {
        // A tool's page that is left stops what it does only in view (practice, the welcome's demo).
        if (_current is { } left && left != page && _tools.GetValueOrDefault(left) is { } leaving)
            leaving.PageLeft();
        _current = page;
        _lastPage = _pages.IndexOf(page);
        foreach (var other in _pages)
        {
            bool on = other == page;
            if (on)
                other.Button.SetResourceReference(BackgroundProperty, "Petal.AccentSoft");
            else
                other.Button.SetCurrentValue(BackgroundProperty, Brushes.Transparent);
            var row = (StackPanel)other.Button.Content;
            ((System.Windows.Shapes.Path)row.Children[0]).SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, on ? "Petal.Accent" : "Petal.TextSecondary");
            ((TextBlock)row.Children[1]).FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
        }
        // A page that fills is given the window's height and scrolls inside itself.
        _scroll.VerticalScrollBarVisibility = page.Fill ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
        _scroll.Content = page.Content;
        _scroll.ScrollToTop();
        if (page.Title == "Saved Files")
            FillPlaces();
        if (ToolOf(page) is { } tool)
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
            {
                if (_current == page && IsVisible)
                    tool.PageShown();
            });
    }

    /// <summary>The tool a page shows, made the first time it is asked for.</summary>
    private ToolWindow? ToolOf(Page page)
    {
        if (page.Make is not { } make)
            return null;
        if (!_tools.TryGetValue(page, out var tool))
        {
            _tools[page] = tool = make();
            page.Holder!.Content = tool.PageContent();
        }
        return tool;
    }

    /// <summary>When the window closes: the tool in view stops, and each tool ends as its own window would.</summary>
    private void EndTools()
    {
        foreach (var (page, tool) in _tools)
        {
            if (page == _current)
                tool.PageLeft();
            tool.Close();
        }
        _tools.Clear();
    }

    /// <summary>Opens the main window at a page, by its title.</summary>
    public static void ShowPage(string title)
    {
        ShowSingle();
        _open?.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () => _open?.GoToPage(title));
    }

    /// <summary>Opens the Tools Guide page, at an entry or with a request asked.</summary>
    public static void ShowGuide(string? key = null, string? request = null)
    {
        ShowSingle();
        _open?.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
        {
            if (_open is not { } window)
                return;
            var page = window._pages.First(p => p.Title == "Tools Guide");
            window.Go(page);
            if (window.ToolOf(page) is Guide.GuideWindow guide)
                guide.Open(key, request);
        });
    }

    // ---------- where things are saved ----------

    /// <summary>Filled each time the page opens: the folders chosen on other pages may have changed.</summary>
    private void FillPlaces()
    {
        _placesMade.Children.Clear();
        foreach (var place in SavedPlaces.Made(_s))
            _placesMade.Children.Add(PlaceRow(place));
        _placesOwn.Children.Clear();
        foreach (var place in SavedPlaces.Own())
            _placesOwn.Children.Add(PlaceRow(place));
    }

    private UIElement PlaceRow(SavedPlace place)
    {
        var words = new StackPanel();
        var title = Text(place.Title);
        title.FontWeight = FontWeights.SemiBold;
        words.Children.Add(title);
        var detail = Text(place.Detail, "Petal.Caption").WithMargin(0, 1, 0, 3);
        detail.TextWrapping = TextWrapping.Wrap;
        words.Children.Add(detail);
        var where = Text(place.Path ?? L.T("Beside the original file (you can choose a folder on the Conversions page)."));
        where.TextWrapping = TextWrapping.Wrap;
        if (place.Path is not null)
        {
            // A path reads left to right, whatever the language around it.
            where.FlowDirection = FlowDirection.LeftToRight;
            where.TextAlignment = L.IsRightToLeft ? TextAlignment.Right : TextAlignment.Left;
            where.ToolTip = place.Path;
            where.SetResourceReference(TextBlock.ForegroundProperty, "Petal.Accent");
        }
        words.Children.Add(where);
        var row = new Grid { Margin = new Thickness(0, 0, 0, 14), ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } } };
        row.Children.Add(words);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(10, 0, 0, 0) };
        if (place.Kind is { } kind)
        {
            var change = ChangeButton();
            change.Click += (_, _) => ChangePlace(kind, place);
            buttons.Children.Add(change);
            if (SavedPlaces.Chosen(kind, _s) is not null)
            {
                var usual = ToolWindowParts.IconButton("rotateCcw", "Back to the Usual Folder");
                usual.Click += (_, _) => SetPlace(kind, null);
                buttons.Children.Add(usual);
            }
        }
        else if (place.Title == L.T("Conversions and tools"))
        {
            var change = ChangeButton();
            change.Click += (_, _) => Show(_cards["Results"], mark: true);
            buttons.Children.Add(change);
        }
        if (place.Path is { } path)
        {
            var open = ToolWindowParts.IconButton("folderOpen", "Open Folder");
            open.Click += (_, _) => OpenPlace(path);
            var copy = ToolWindowParts.IconButton("copyPlain", "Copy Path");
            copy.Click += (_, _) =>
            {
                if (!Words.SelectedText.TrySet(path))
                    return;
                var shown = copy.Content;
                copy.Content = ThemedIcon("check", 15, "Petal.Accent");
                var time = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1.2) };
                time.Tick += (_, _) =>
                {
                    time.Stop();
                    copy.Content = shown;
                };
                time.Start();
            };
            buttons.Children.Add(open);
            buttons.Children.Add(copy);
        }
        Grid.SetColumn(buttons, 1);
        row.Children.Add(buttons);
        return row;
    }

    /// <summary>In words, as a button: this is what people come here to do, and an icon alone is easily missed.</summary>
    private static Button ChangeButton()
    {
        var change = new Button { Content = L.T("Change…"), ToolTip = L.T("Change Folder…"), Padding = new Thickness(10, 3, 10, 4), MinHeight = 26, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
        System.Windows.Automation.AutomationProperties.SetName(change, L.T("Change Folder…"));
        return change;
    }

    private void ChangePlace(SavedKind kind, SavedPlace place)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = L.F("Save {0} in", place.Title), InitialDirectory = place.Path };
        if (dialog.ShowDialog(this) == true)
            SetPlace(kind, dialog.FolderName);
    }

    /// <summary>Saves this kind in a folder from now on, or the usual one, and shows it.</summary>
    internal void SetPlace(SavedKind kind, string? folder)
    {
        SavedPlaces.Choose(kind, folder, _s);
        Save();
        if (kind == SavedKind.FromPcs)
            _ = Actions.PcDrop.RestartAsync();
        FillPlaces();
    }

    /// <summary>Opens a folder, made first if nothing has been saved there yet.</summary>
    private static void OpenPlace(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            Log.Error(ex, $"Opening {path}");
        }
    }

    /// <summary>Goes to the page a card is on, and shows the card there.</summary>
    private void Show(FrameworkElement card, bool mark = false)
    {
        if (_pageOfCard.GetValueOrDefault(card) is { } page && page != _current)
            Go(page);
        UpdateLayout();
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
        {
            card.BringIntoView();
            if (mark && card is Border border)
            {
                border.SetResourceReference(Border.BorderBrushProperty, "Petal.Accent");
                var time = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1.6) };
                time.Tick += (_, _) =>
                {
                    time.Stop();
                    border.SetResourceReference(Border.BorderBrushProperty, "Petal.ControlBorder");
                };
                time.Start();
            }
        });
    }

    private readonly List<FrameworkElement> _unplaced = [];

    private FrameworkElement Titled(string title, string? subtitle, FrameworkElement content)
    {
        var heading = Text(title, "Petal.Title");
        heading.FontSize = 24;
        var page = new StackPanel { MaxWidth = 1320, Margin = new Thickness(28, 22, 28, 28) };
        page.Children.Add(heading);
        if (subtitle is not null)
            page.Children.Add(Text(subtitle, "Petal.Caption").WithMargin(0, 4, 0, 0));
        content.Margin = new Thickness(0, 18, 0, 0);
        page.Children.Add(content);
        return page;
    }

    private FrameworkElement CardsPage(string title, string subtitle, params UIElement[] cards)
    {
        var columns = new CardColumns();
        foreach (var card in cards)
        {
            columns.Children.Add(card);
            _unplaced.Add((FrameworkElement)card);
        }
        return Titled(title, subtitle, columns);
    }

    /// <summary>A long section as cards of its own, one for each of its labels. The first also goes by the section's name, for the tour.</summary>
    private UIElement[] Split(UIElement section, string name)
    {
        var panel = (StackPanel)section;
        var label = FindResource("Petal.Label");
        var parts = new List<(string Title, StackPanel Content)>();
        var children = panel.Children.OfType<UIElement>().ToList();
        panel.Children.Clear();
        foreach (var child in children)
        {
            if (child is TextBlock { Text: var title } block && ReferenceEquals(block.Style, label))
                parts.Add((title, new StackPanel()));
            else
                parts[^1].Content.Children.Add(child);
        }
        var cards = parts.Select(part => Card(part.Title, null, part.Content)).ToArray();
        _cards[name] = (FrameworkElement)cards[0];
        return cards;
    }

    // ---------- home ----------

    private FrameworkElement HomePage()
    {
        var hero = new Grid { ColumnDefinitions = { new ColumnDefinition { Width = GridLength.Auto }, new ColumnDefinition() } };
        var icon = new Image { FlowDirection = FlowDirection.LeftToRight, Width = 72, Height = 72, Source = AppIcon.Large, Margin = new Thickness(0, 0, 20, 0) };
        RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
        hero.Children.Add(icon);
        var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(words, 1);
        var title = Text("DropWheel", "Petal.Title");
        title.FontSize = 28;
        words.Children.Add(title);
        words.Children.Add(Text("The zero-click file converter. Drag a file, tap Shift, and drop.").WithMargin(0, 4, 0, 0));
        if (Shell.Packaged.Current is { Trial: true } trial)
        {
            var buy = new Button { Content = L.T("Buy DropWheel"), Style = (Style)FindResource("Petal.SubtleButton") };
            buy.SetResourceReference(ForegroundProperty, "Petal.Accent");
            buy.Click += async (_, _) => await Shell.Packaged.BuyAsync(new System.Windows.Interop.WindowInteropHelper(this).Handle);
            var days = (int)Math.Ceiling(trial.TrialLeft.TotalDays);
            words.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 6, 0, 0),
                Children = { Text(L.P(days, "Trial: {0} day left", "Trial: {0} days left"), "Petal.Caption").WithMargin(0, 0, 12, 0), buy },
            });
        }
        hero.Children.Add(words);

        var tiles = new System.Windows.Controls.Primitives.UniformGrid { Columns = 3, Margin = new Thickness(-6, 22, -6, 0) };
        // Each goes to its page down the side (the tour walks through the pages themselves).
        tiles.Children.Add(Tile("info", "How it works", "A short look at the wheel.", () => GoToPage("How it works")));
        tiles.Children.Add(Tile("book", "Tools Guide", "Every tool explained, and your questions answered.", () => GoToPage("Tools Guide")));
        tiles.Children.Add(Tile("target", "Practice", "Try the wheel on a sample file.", () => GoToPage("Practice")));
        tiles.Children.Add(Tile("arrowRight", "Take the Tour", "A walk through these pages.", StartTour));
        tiles.Children.Add(Tile("qr", "Create QR Code", "From a link or any text.", () => GoToPage("Create QR Code")));
        tiles.Children.Add(Tile("history", "History", "What DropWheel made, and undoing it.", () => GoToPage("History")));

        var page = new StackPanel { MaxWidth = 1320, Margin = new Thickness(28, 26, 28, 28), Children = { hero, tiles } };
        var statistics = Card("Statistics", "What DropWheel has done for you, counted on this PC only.", StatisticsSection());
        ((FrameworkElement)statistics).Margin = new Thickness(0, 20, 0, 0);
        page.Children.Add(statistics);
        _unplaced.Add((FrameworkElement)statistics);
        return page;
    }

    private Button Tile(string icon, string title, string detail, Action click)
    {
        var square = new Border { Width = 40, Height = 40, CornerRadius = new CornerRadius(10), Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Top };
        square.SetResourceReference(Border.BackgroundProperty, "Petal.AccentSoft");
        var glyph = ThemedIcon(icon, 20, "Petal.Accent");
        glyph.HorizontalAlignment = HorizontalAlignment.Center;
        glyph.VerticalAlignment = VerticalAlignment.Center;
        square.Child = glyph;
        var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(words, 1);
        var heading = Text(title);
        heading.FontWeight = FontWeights.SemiBold;
        words.Children.Add(heading);
        var more = Text(detail, "Petal.Caption").WithMargin(0, 2, 0, 0);
        more.TextWrapping = TextWrapping.Wrap;
        words.Children.Add(more);
        var tile = new Button
        {
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Top,
            Padding = new Thickness(16, 14, 16, 14),
            Margin = new Thickness(6),
            BorderThickness = new Thickness(1),
            Content = new Grid { ColumnDefinitions = { new ColumnDefinition { Width = GridLength.Auto }, new ColumnDefinition() }, Children = { square, words } },
        };
        tile.SetResourceReference(BackgroundProperty, "Petal.Surface");
        tile.SetResourceReference(BorderBrushProperty, "Petal.ControlBorder");
        System.Windows.Automation.AutomationProperties.SetName(tile, L.T(title));
        tile.Click += (_, _) => click();
        return tile;
    }

    // ---------- finding a setting ----------

    private FrameworkElement SearchBox()
    {
        var hint = Text("Find a setting", "Petal.Caption");
        hint.IsHitTestVisible = false;
        hint.VerticalAlignment = VerticalAlignment.Center;
        hint.Margin = new Thickness(10, 0, 10, 0);
        System.Windows.Automation.AutomationProperties.SetName(_search, hint.Text);
        _search.TextChanged += (_, _) =>
        {
            hint.Visibility = _search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            Find(_search.Text.Trim());
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
            {
                e.Handled = true;
                _search.Focus();
                _search.SelectAll();
            }
        };
        return new Grid { Margin = new Thickness(14, 14, 14, 6), Children = { _search, hint } };
    }

    protected override bool OnEscape()
    {
        // The main window stays open; Esc only leaves what was being looked for.
        _search.Clear();
        return true;
    }

    private void Find(string words)
    {
        _nav.Visibility = words.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _found.Visibility = words.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        _found.Children.Clear();
        if (words.Length == 0)
            return;
        _index ??= _cards.Values.Distinct().Select(card => (card, System.Windows.Automation.AutomationProperties.GetName(card), string.Join(' ', WordsIn(card)))).ToList();
        var hits = _index.Where(entry => entry.Words.Contains(words, StringComparison.CurrentCultureIgnoreCase)).ToList();
        if (hits.Count == 0)
            _found.Children.Add(Text("Nothing matches.", "Petal.Caption").WithMargin(10, 8, 10, 0));
        foreach (var (card, title, _) in hits)
        {
            var row = new StackPanel { Children = { Text(title) } };
            if (_pageOfCard.GetValueOrDefault(card) is { } page)
                row.Children.Add(Text(page.Title, "Petal.Caption"));
            var hit = new Button
            {
                Style = (Style)FindResource("Petal.SubtleButton"),
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(10, 6, 10, 6),
                Content = row,
            };
            hit.Click += (_, _) => Show(card, mark: true);
            _found.Children.Add(hit);
        }
    }

    private static IEnumerable<string> WordsIn(DependencyObject element)
    {
        if (element is TextBlock { Text: { Length: > 0 } text })
            yield return text;
        if (element is ContentControl { Content: string content })
            yield return content;
        foreach (var child in LogicalTreeHelper.GetChildren(element).OfType<DependencyObject>())
            foreach (var words in WordsIn(child))
                yield return words;
    }

    // ---------- for checking by eye ----------

    internal static int PageCount => 17;

    internal WheelGalleryList GalleryForTest => _gallery!;

    internal int WheelsListed => _s.Wheels.Count;

    internal IReadOnlyList<string> PlacesShown => [.. WordsIn(_placesMade), .. WordsIn(_placesOwn)];

    internal static SettingsWindow? Open => _open;

    internal void ChooseLanguageForTest(UiLanguage language) =>
        _sideLanguage.SelectedItem = _sideLanguage.Items.OfType<ComboBoxItem>().First(item => (UiLanguage)item.Tag == language);

    /// <summary>The language shown as chosen, in each of the two places it can be chosen.</summary>
    internal UiLanguage LanguageShown => ValueOf<UiLanguage>(_sideLanguage);

    internal IReadOnlyList<string> PageTitles => _pages.Select(page => page.Title).ToList();

    internal string? CurrentPage => _current?.Title;

    /// <summary>The tool a page shows (made if it hasn't been yet), for tests.</summary>
    internal ToolWindow? ToolForTest(string title) => ToolOf(_pages.First(page => page.Title == title));

    /// <summary>Cards that no page would show.</summary>
    internal IReadOnlyList<string> CardsOffPages => _cards.Where(card => !_pageOfCard.ContainsKey(card.Value)).Select(card => card.Key).ToList();

    internal bool HasCard(string title) => _cards.ContainsKey(title);

    internal void GoToCard(string title) => Show(_cards[title]);

    internal void GoToPage(string title) => Go(_pages.First(page => page.Title == title));

    /// <summary>The titles of the cards found for these words.</summary>
    internal IReadOnlyList<string> FindForTest(string words)
    {
        _search.Text = words;
        return _found.Children.OfType<Button>().Select(hit => ((TextBlock)((StackPanel)hit.Content).Children[0]).Text).ToList();
    }

    /// <summary>The window at one of its pages, tall enough to show most of it.</summary>
    internal static Window ForPage(int index, double width = 1680, double height = 1040)
    {
        _lastPage = index;
        return new SettingsWindow { Width = width, Height = height, WindowState = WindowState.Normal };
    }
}
