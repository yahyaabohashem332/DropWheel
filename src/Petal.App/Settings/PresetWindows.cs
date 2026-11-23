using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Petal.App.Jobs;
using Petal.App.Localization;
using Petal.App.Tools;
using Petal.Core.Jobs;

namespace Petal.App.Settings;

/// <summary>
/// Brings in favourites someone shared, or saved on another PC: a .petalpreset file or the line of text they
/// sent. Before anything changes it shows each one, what it does, and whether it is added: one already here,
/// one DropWheel can't save, a folder this PC doesn't have, no room left.
/// </summary>
internal sealed class ImportPresetsWindow : ToolWindow
{
    private readonly TextBox _code = new() { AcceptsReturn = false, TextWrapping = TextWrapping.Wrap, Height = 64, FlowDirection = FlowDirection.LeftToRight };
    private readonly StackPanel _found = new() { Margin = new Thickness(0, 12, 0, 0) };
    private readonly TextBlock _about;
    private readonly Action _added;
    private List<Favourite> _toAdd = [];

    public ImportPresetsWindow(Action added, string? text = null) : base("Import Favourites", 480)
    {
        _added = added;
        Body.Children.Add(Text("Paste the favourite's text (it starts with PETAL-PRESET:), or open its .petalpreset file.", "Petal.Body"));
        _code.Margin = new Thickness(0, 10, 0, 0);
        System.Windows.Automation.AutomationProperties.SetName(_code, L.T("Favourite's text"));
        Body.Children.Add(_code);
        var open = new Button { Content = L.T("Open File…"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0) };
        open.Click += (_, _) =>
        {
            var dialog = new OpenFileDialog { Filter = L.T("DropWheel favourites") + $"|*{PresetSharing.Extension}" };
            if (Directory.Exists(PresetSharing.GalleryFolder))
                dialog.InitialDirectory = PresetSharing.GalleryFolder;
            if (dialog.ShowDialog(this) == true)
                Check(SafeRead(dialog.FileName));
        };
        Body.Children.Add(open);
        Body.Children.Add(new ScrollViewer { Content = _found, MaxHeight = 300, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        _about = Text("", "Petal.Caption");
        _about.Margin = new Thickness(0, 8, 0, 0);
        _about.TextWrapping = TextWrapping.Wrap;
        Body.Children.Add(_about);
        _code.TextChanged += (_, _) => Check(_code.Text);
        PrimaryButton.Content = L.T("Add");
        PrimaryButton.IsEnabled = false;
        PrimaryButton.Click += (_, _) =>
        {
            if (_toAdd.Count == 0)
                return;
            SettingsStore.Current.Favourites.AddRange(_toAdd);
            SettingsStore.Save();
            _added();
            Close();
        };
        if (text is not null)
            _code.Text = text;
        else if (Clipboard.ContainsText() && Clipboard.GetText().TrimStart().StartsWith(PresetSharing.CodePrefix, StringComparison.OrdinalIgnoreCase))
            _code.Text = Clipboard.GetText().Trim();
    }

    /// <summary>The text of a file, or nothing when it can't be read or is far too large.</summary>
    private static string SafeRead(string path)
    {
        try
        {
            return new FileInfo(path).Length > 1024 * 1024 ? "" : File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }

    private void Check(string text)
    {
        _found.Children.Clear();
        _toAdd = [];
        PrimaryButton.IsEnabled = false;
        if (text.Trim().Length == 0)
        {
            _about.Text = "";
            return;
        }
        var mine = SettingsStore.Current.Favourites;
        var (list, problem) = PresetSharing.Read(text, mine);
        if (list is null)
        {
            _about.Text = L.T(problem ?? "This isn't a DropWheel favourite.");
            return;
        }
        int room = Math.Max(0, FavouriteText.Most - mine.Count);
        foreach (var item in list)
        {
            bool adds = item.Verdict is PresetSharing.Verdict.Add or PresetSharing.Verdict.AddWithoutFolder;
            string note;
            if (adds && _toAdd.Count >= room)
            {
                adds = false;
                note = L.F("Not added: there is room for {0} favourites at most.", FavouriteText.Most);
            }
            else
            {
                note = item.Verdict switch
                {
                    PresetSharing.Verdict.AlreadyThere => L.T("You have this one already."),
                    PresetSharing.Verdict.CantSave => L.F("Not added: DropWheel can't save {0} files.", item.Favourite.Format.ToUpperInvariant()),
                    PresetSharing.Verdict.AddWithoutFolder => L.F("Added. Its folder “{0}” isn't on this PC, so results go where Settings says.", item.Folder!),
                    _ => L.T("Added."),
                };
            }
            if (adds)
                _toAdd.Add(item.Favourite);
            _found.Children.Add(PresetCards.Card(item.Favourite, note, adds ? "star" : "x", null));
        }
        PrimaryButton.IsEnabled = _toAdd.Count > 0;
        PrimaryButton.Content = _toAdd.Count > 0 ? L.P(_toAdd.Count, "Add {0} Favourite", "Add {0} Favourites") : L.T("Add");
        _about.Text = _toAdd.Count == 0 ? L.T("Nothing here to add.") : L.T("Favourites are only settings: nothing in them runs.");
    }

    internal string AboutForTest => _about.Text;

    internal int AddsForTest => _toAdd.Count;

    internal IReadOnlyList<Favourite> ToAddForTest => _toAdd;

    internal void CheckForTest(string text) => Check(text);

    internal void AddForTest() => PrimaryButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
}

/// <summary>
/// The favourites gallery: ready-made favourites for common jobs, and the ones in .petalpreset files kept in
/// the gallery's own folder (saved there from Settings, or put there by hand). Each is added with one click.
/// Nothing is fetched from the internet.
/// </summary>
internal sealed class PresetGalleryWindow : ToolWindow
{
    private readonly Action _added;
    private readonly StackPanel _list = new();
    private readonly List<(Button Add, Favourite Favourite)> _buttons = [];

    public PresetGalleryWindow(Action added) : base("Favourites Gallery", 480)
    {
        _added = added;
        Body.Children.Add(Text("Ready-made favourites, and the .petalpreset files in the gallery folder. Add one with a click; change it afterwards in Settings.", "Petal.Caption"));
        Body.Children.Add(new ScrollViewer { Content = _list, MaxHeight = 460, Margin = new Thickness(0, 10, 0, 0), VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Fill();
        PrimaryButton.Content = L.T("Done");
        PrimaryButton.Click += (_, _) => Close();
        ResetButton.Content = L.T("Open the Gallery Folder");
        ResetButton.Visibility = Visibility.Visible;
        ResetButton.Click += (_, _) =>
        {
            Directory.CreateDirectory(PresetSharing.GalleryFolder);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{PresetSharing.GalleryFolder}\"") { UseShellExecute = true });
        };
        Activated += (_, _) => Fill();
    }

    private void Fill()
    {
        _list.Children.Clear();
        _buttons.Clear();
        Heading("Ready-Made");
        foreach (var ready in PresetSharing.Gallery.Where(r => PresetSharing.CanSave(r.Favourite)))
        {
            var favourite = PresetSharing.Make(ready);
            _list.Children.Add(PresetCards.Card(favourite, L.T(ready.Description), ready.Icon, AddButton(favourite)));
        }
        var own = PresetSharing.FromGalleryFolder();
        Heading("In the Gallery Folder");
        if (own.Count == 0)
        {
            var none = Text("None yet. Save favourites here with Export…, or put .petalpreset files people send you in the folder.", "Petal.Caption");
            none.TextWrapping = TextWrapping.Wrap;
            _list.Children.Add(none);
        }
        foreach (var (file, favourite) in own)
            _list.Children.Add(PresetCards.Card(favourite, Path.GetFileNameWithoutExtension(file), "fileText", AddButton(favourite)));
        Refresh();
    }

    private void Heading(string title)
    {
        var heading = Text(title, "Petal.Label");
        heading.Margin = new Thickness(0, _list.Children.Count == 0 ? 0 : 12, 0, 6);
        _list.Children.Add(heading);
    }

    private Button AddButton(Favourite favourite)
    {
        var add = new Button { VerticalAlignment = VerticalAlignment.Center, MinWidth = 76, Margin = new Thickness(10, 0, 0, 0), Padding = new Thickness(14, 6, 14, 6), BorderThickness = new Thickness(1) };
        add.SetResourceReference(BackgroundProperty, "Petal.Popup");
        add.SetResourceReference(BorderBrushProperty, "Petal.ControlBorder");
        System.Windows.Automation.AutomationProperties.SetName(add, L.F("Add {0}", FavouriteText.Label(favourite)));
        add.Click += (_, _) =>
        {
            var copy = favourite.Copy();
            copy.Id = Guid.NewGuid();
            SettingsStore.Current.Favourites.Add(copy);
            SettingsStore.Save();
            _added();
            Refresh();
        };
        _buttons.Add((add, favourite));
        return add;
    }

    /// <summary>Says which are added already, and keeps the rest from going past the most favourites there can be.</summary>
    private void Refresh()
    {
        var mine = SettingsStore.Current.Favourites;
        bool full = mine.Count >= FavouriteText.Most;
        foreach (var (add, favourite) in _buttons)
        {
            bool there = mine.Any(m => PresetSharing.Same(m, favourite));
            add.Content = L.T(there ? "Added" : "Add");
            add.IsEnabled = !there && !full;
        }
    }

    internal IReadOnlyList<(string Label, bool CanAdd)> CardsForTest => _buttons.Select(b => (FavouriteText.Label(b.Favourite), b.Add.IsEnabled)).ToList();

    internal void AddForTest(int index) => _buttons[index].Add.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
}

/// <summary>A favourite shown as a card: its icon, name, settings and a line about it, with a button if wanted.</summary>
internal static class PresetCards
{
    public static Border Card(Favourite favourite, string note, string iconName, Button? button)
    {
        var icon = ToolWindow.ThemedIcon(iconName, 20, "Petal.Accent");
        icon.Margin = new Thickness(0, 2, 12, 0);
        icon.VerticalAlignment = VerticalAlignment.Top;
        var name = ToolWindow.PlainText("", "Petal.Body");
        name.Text = FavouriteText.Label(favourite);
        name.FontWeight = FontWeights.SemiBold;
        var details = ToolWindow.PlainText("", "Petal.Caption");
        details.Text = FavouriteText.Describe(favourite);
        details.TextWrapping = TextWrapping.Wrap;
        var words = new StackPanel { Children = { name, details } };
        if (note.Length > 0)
        {
            var line = ToolWindow.PlainText("", "Petal.Caption");
            line.Text = note;
            line.TextWrapping = TextWrapping.Wrap;
            line.Opacity = 0.8;
            line.Margin = new Thickness(0, 4, 0, 0);
            words.Children.Add(line);
        }
        var row = new DockPanel();
        DockPanel.SetDock(icon, Dock.Left);
        row.Children.Add(icon);
        if (button is not null)
        {
            DockPanel.SetDock(button, Dock.Right);
            row.Children.Add(button);
        }
        row.Children.Add(words);
        var card = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 10, 12, 10), Margin = new Thickness(0, 0, 0, 8), Child = row };
        card.SetResourceReference(Border.BackgroundProperty, "Petal.Control");
        return card;
    }
}
