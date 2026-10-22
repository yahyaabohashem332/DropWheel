using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Petal.App.Localization;
using Petal.App.Tools;
using Petal.App.Tools.Windows;
using Petal.App.Wheels;

namespace Petal.App.Settings;

/// <summary>The gallery: wheels for apps that have none yet and for kinds of work, each added with one click.</summary>
internal sealed class WheelGalleryWindow : ToolWindow
{
    private readonly Action _added;

    public WheelGalleryWindow(Action added) : base("Wheel Gallery", 560)
    {
        _added = added;
        Body.Children.Add(Text("Wheels made for apps and for kinds of work. Add one and change it as you like; it doesn't replace yours.", "Petal.Caption"));
        var list = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        foreach (var entry in WheelGallery.All)
            list.Children.Add(Card(entry));
        Body.Children.Add(new ScrollViewer { Content = list, MaxHeight = 470, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        PrimaryButton.Content = L.T("Done");
        PrimaryButton.Click += (_, _) => Close();
    }

    private Border Card(WheelGallery.Entry entry)
    {
        var sample = entry.Make();
        var icon = ThemedIcon(entry.Icon, 20, "Petal.Accent");
        icon.Margin = new Thickness(0, 2, 12, 0);
        icon.VerticalAlignment = VerticalAlignment.Top;
        var name = Text(entry.Name, "Petal.Body");
        name.FontWeight = FontWeights.SemiBold;
        var words = new StackPanel { Children = { name, Text(entry.Description, "Petal.Caption") } };
        var petals = Text(string.Join(" · ", sample.Items.Select(Label)), "Petal.Caption");
        petals.TextWrapping = TextWrapping.Wrap;
        petals.Opacity = 0.75;
        petals.Margin = new Thickness(0, 4, 0, 0);
        words.Children.Add(petals);
        bool present = SettingsStore.Current.Wheels.Any(w => w.Name == sample.Name || L.T(w.Name) == L.T(sample.Name));
        var add = new Button { Content = L.T(present ? "Added" : "Add"), IsEnabled = !present, VerticalAlignment = VerticalAlignment.Center, MinWidth = 76, Padding = new Thickness(14, 6, 14, 6), BorderThickness = new Thickness(1) };
        // Lighter than the card it sits on, so it reads as a button.
        add.SetResourceReference(BackgroundProperty, "Petal.Popup");
        add.SetResourceReference(BorderBrushProperty, "Petal.ControlBorder");
        add.Click += (_, _) =>
        {
            SettingsStore.Current.Wheels.Add(entry.Make());
            SettingsStore.Save();
            add.Content = L.T("Added");
            add.IsEnabled = false;
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
        return card;
    }

    /// <summary>A petal's short name, as the wheel shows it.</summary>
    private static string Label(WheelEntry e) => e.Kind == EntryKind.Action && WheelActions.Get(e.Action) is { } a ? L.T(a.Name) : L.T(e.Name ?? e.Label ?? "");
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
            var dialog = new OpenFileDialog { Filter = L.T("Petal wheel") + $"|*{WheelSharing.Extension}" };
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
            _about.Text = L.T(problem ?? "This isn't a Petal wheel.");
            return;
        }
        var (petals, scripts, unknown) = WheelSharing.Summary(wheel);
        var lines = new List<string> { L.F("“{0}”: {1}", wheel.Name, L.P(petals, "{0} petal", "{0} petals")) };
        if (wheel.Kind == WheelKind.App)
            lines.Add(L.F("First while {0} is in front", string.Join(", ", wheel.Apps)));
        if (scripts > 0)
            lines.Add(L.P(scripts, "It has {0} script; Petal shows it before its first run.", "It has {0} scripts; Petal shows each before its first run."));
        if (unknown > 0)
            lines.Add(L.P(unknown, "{0} petal is from a newer Petal and stays dim.", "{0} petals are from a newer Petal and stay dim."));
        _about.Text = string.Join(Environment.NewLine, lines);
    }
}
