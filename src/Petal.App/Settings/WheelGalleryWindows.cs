using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Petal.App.Localization;
using Petal.App.Tools;
using Petal.App.Tools.Windows;
using Petal.App.Wheels;

namespace Petal.App.Settings;

/// <summary>
/// The gallery, open beside the list of wheels: the ready-made wheels, and wheels for apps that have none yet and for
/// kinds of work, each added with one click. What is on the list says so here, and can be added again once it is taken off.
/// </summary>
internal sealed class WheelGalleryList : StackPanel
{
    private readonly Action _added;
    private readonly List<(FrameworkElement Card, Button Add, string Words, Func<bool> Present)> _cards = [];
    private readonly List<(TextBlock Heading, int From, int To)> _groups = [];
    private readonly TextBox _find = new();
    private readonly TextBlock _nothing;

    /// <summary>The list that scrolls by itself, so the gallery and the wheels are seen together.</summary>
    public ScrollViewer Scroller { get; }

    public WheelGalleryList(Action added)
    {
        _added = added;
        var hint = ToolWindow.PlainText("Find a wheel", "Petal.Caption");
        hint.IsHitTestVisible = false;
        hint.VerticalAlignment = VerticalAlignment.Center;
        hint.Margin = new Thickness(10, 0, 10, 0);
        System.Windows.Automation.AutomationProperties.SetName(_find, hint.Text);
        _find.TextChanged += (_, _) =>
        {
            hint.Visibility = _find.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            Filter();
        };
        Children.Add(new Grid { Margin = new Thickness(0, 0, 0, 10), Children = { _find, hint } });

        var list = new StackPanel();
        Group(list, "Ready-Made Wheels", WheelTemplates.All().Select(template =>
        (
            Sample: template,
            Icon: template.Kind switch { WheelKind.Quick => "sparkles", WheelKind.Media => "disc", WheelKind.App => "appWindow", _ => "layers" },
            Description: template.Kind == WheelKind.App ? L.F("First while {0} is in front", string.Join(", ", template.Apps)) : "",
            Make: (Func<UserWheel>)(() => WheelTemplates.Fresh(template.Template) ?? template),
            Present: (Func<bool>)(() => SettingsStore.Current.Wheels.Any(w => w.Template == template.Template))
        )));
        Group(list, "For Apps and Work", WheelGallery.All.Select(entry =>
        {
            var sample = entry.Make();
            return
            (
                Sample: sample,
                Icon: entry.Icon,
                Description: L.T(entry.Description),
                Make: entry.Make,
                Present: (Func<bool>)(() => SettingsStore.Current.Wheels.Any(w => w.Name == sample.Name || L.T(w.Name) == L.T(sample.Name)))
            );
        }));
        _nothing = ToolWindow.PlainText("No wheel matches.", "Petal.Caption");
        _nothing.Visibility = Visibility.Collapsed;
        list.Children.Add(_nothing);
        Scroller = new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Children.Add(Scroller);
        Refresh();
    }

    private void Group(StackPanel list, string title, IEnumerable<(UserWheel Sample, string Icon, string Description, Func<UserWheel> Make, Func<bool> Present)> wheels)
    {
        var heading = ToolWindow.PlainText(title, "Petal.Label");
        heading.Margin = new Thickness(0, list.Children.Count == 0 ? 0 : 10, 0, 6);
        list.Children.Add(heading);
        int from = _cards.Count;
        foreach (var wheel in wheels)
            list.Children.Add(Card(wheel.Sample, wheel.Icon, wheel.Description, wheel.Make, wheel.Present));
        _groups.Add((heading, from, _cards.Count));
    }

    private Border Card(UserWheel sample, string iconName, string description, Func<UserWheel> make, Func<bool> present)
    {
        var icon = ToolWindow.ThemedIcon(iconName, 20, "Petal.Accent");
        icon.Margin = new Thickness(0, 2, 12, 0);
        icon.VerticalAlignment = VerticalAlignment.Top;
        var name = ToolWindow.PlainText(sample.Name, "Petal.Body");
        name.FontWeight = FontWeights.SemiBold;
        var words = new StackPanel { Children = { name } };
        if (description.Length > 0)
            words.Children.Add(ToolWindow.PlainText(description, "Petal.Caption"));
        var labels = string.Join(" · ", sample.Items.Select(Label));
        var petals = ToolWindow.PlainText(labels, "Petal.Caption");
        petals.TextWrapping = TextWrapping.Wrap;
        petals.Opacity = 0.75;
        petals.Margin = new Thickness(0, 4, 0, 0);
        words.Children.Add(petals);
        var add = new Button { VerticalAlignment = VerticalAlignment.Center, MinWidth = 76, Margin = new Thickness(10, 0, 0, 0), Padding = new Thickness(14, 6, 14, 6), BorderThickness = new Thickness(1) };
        // Lighter than the card it sits on, so it reads as a button.
        add.SetResourceReference(Control.BackgroundProperty, "Petal.Popup");
        add.SetResourceReference(Control.BorderBrushProperty, "Petal.ControlBorder");
        System.Windows.Automation.AutomationProperties.SetName(add, L.F("Add {0}", L.T(sample.Name)));
        add.Click += (_, _) =>
        {
            SettingsStore.Current.Wheels.Add(make());
            SettingsStore.Save();
            _added();
        };
        var row = new DockPanel();
        DockPanel.SetDock(icon, Dock.Left);
        DockPanel.SetDock(add, Dock.Right);
        row.Children.Add(icon);
        row.Children.Add(add);
        row.Children.Add(words);
        var card = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 10, 12, 10), Margin = new Thickness(0, 0, 0, 8), Child = row };
        card.SetResourceReference(Border.BackgroundProperty, "Petal.Control");
        _cards.Add((card, add, $"{L.T(sample.Name)} {sample.Name} {description} {labels}", present));
        return card;
    }

    /// <summary>Says again what is on the list, after a wheel is added or taken off.</summary>
    public void Refresh()
    {
        foreach (var (_, add, _, present) in _cards)
        {
            bool on = present();
            add.Content = L.T(on ? "Added" : "Add");
            add.IsEnabled = !on;
        }
    }

    private void Filter()
    {
        var words = _find.Text.Trim();
        bool any = false;
        foreach (var (card, _, text, _) in _cards)
        {
            bool shown = words.Length == 0 || text.Contains(words, StringComparison.CurrentCultureIgnoreCase);
            card.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
            any |= shown;
        }
        foreach (var (heading, from, to) in _groups)
            heading.Visibility = _cards.Skip(from).Take(to - from).Any(c => c.Card.Visibility == Visibility.Visible) ? Visibility.Visible : Visibility.Collapsed;
        _nothing.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>A petal's short name, as the wheel shows it.</summary>
    private static string Label(WheelEntry e) => e.Kind == EntryKind.Action && WheelActions.Get(e.Action) is { } a ? L.T(a.Name) : L.T(e.Name ?? e.Label ?? "");

    internal void FindForTest(string words) => _find.Text = words;

    internal IReadOnlyList<(string Words, bool Shown, bool CanAdd)> CardsForTest =>
        _cards.Select(c => (c.Words, c.Card.Visibility == Visibility.Visible, c.Add.IsEnabled)).ToList();

    internal void AddForTest(int index) => _cards[index].Add.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
}

/// <summary>Brings in a wheel someone shared: a .petalwheel file or the line of text they sent.</summary>
internal sealed class ImportWheelWindow : ToolWindow
{
    private readonly TextBox _code = new() { AcceptsReturn = false, TextWrapping = TextWrapping.Wrap, Height = 70, FlowDirection = FlowDirection.LeftToRight };
    private readonly TextBlock _about;
    private readonly Action _added;
    private UserWheel? _wheel;

    public ImportWheelWindow(Action added) : base("Import a Wheel", 460)
    {
        _added = added;
        Body.Children.Add(Text("Paste the wheel's text (it starts with PETAL-WHEEL:), or open its .petalwheel file.", "Petal.Body"));
        _code.Margin = new Thickness(0, 10, 0, 0);
        Body.Children.Add(_code);
        var open = new Button { Content = L.T("Open File…"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0) };
        open.Click += (_, _) =>
        {
            var dialog = new OpenFileDialog { Filter = L.T("DropWheel wheel") + $"|*{WheelSharing.Extension}" };
            if (dialog.ShowDialog(this) == true)
                Check(File.ReadAllText(dialog.FileName));
        };
        Body.Children.Add(open);
        _about = Text("", "Petal.Caption");
        _about.Margin = new Thickness(0, 12, 0, 0);
        _about.TextWrapping = TextWrapping.Wrap;
        Body.Children.Add(_about);
        _code.TextChanged += (_, _) => Check(_code.Text);
        PrimaryButton.Content = L.T("Add the Wheel");
        PrimaryButton.IsEnabled = false;
        PrimaryButton.Click += (_, _) =>
        {
            if (_wheel is null)
                return;
            SettingsStore.Current.Wheels.Add(_wheel);
            SettingsStore.Save();
            _added();
            Close();
        };
        if (Clipboard.ContainsText() && Clipboard.GetText().TrimStart().StartsWith("PETAL-WHEEL:", StringComparison.OrdinalIgnoreCase))
            _code.Text = Clipboard.GetText().Trim();
    }

    private void Check(string text)
    {
        if (text.Trim().Length == 0)
        {
            _about.Text = "";
            PrimaryButton.IsEnabled = false;
            return;
        }
        var (wheel, problem) = WheelSharing.Read(text);
        _wheel = wheel;
        PrimaryButton.IsEnabled = wheel is not null;
        if (wheel is null)
        {
            _about.Text = L.T(problem ?? "This isn't a DropWheel wheel.");
            return;
        }
        var (petals, scripts, unknown) = WheelSharing.Summary(wheel);
        var lines = new List<string> { L.F("“{0}”: {1}", wheel.Name, L.P(petals, "{0} petal", "{0} petals")) };
        if (wheel.Kind == WheelKind.App)
            lines.Add(L.F("First while {0} is in front", string.Join(", ", wheel.Apps)));
        if (scripts > 0)
            lines.Add(L.P(scripts, "It has {0} script; DropWheel shows it before its first run.", "It has {0} scripts; DropWheel shows each before its first run."));
        if (unknown > 0)
            lines.Add(L.P(unknown, "{0} petal is from a newer DropWheel and stays dim.", "{0} petals are from a newer DropWheel and stay dim."));
        _about.Text = string.Join(Environment.NewLine, lines);
    }
}
