using System.Windows;
using System.Windows.Controls;
using Petal.App.Localization;
using Petal.App.Tools;
using Petal.App.Tools.Windows;
using Petal.Core.Text;

namespace Petal.App.Settings;

/// <summary>Your snippets: each abbreviation and the text it becomes, with the placeholders that can go in it.</summary>
internal sealed class SnippetsWindow : ToolWindow
{
    private readonly StackPanel _list = new();
    private readonly List<(TextBox Abbreviation, TextBox Text, Snippet Snippet)> _rows = [];

    public SnippetsWindow() : base("Snippets", 560)
    {
        Body.Children.Add(Text("Type an abbreviation in any app and it becomes its text. Start abbreviations with a sign such as ; so they don't turn up in ordinary words. They work whichever keyboard language is on.", "Petal.Caption"));
        Body.Children.Add(Text("In the text: {date} today, {time} now, {day} the weekday, {hijri} the Hijri date, {clipboard} what was copied, {cursor} where the cursor ends up.", "Petal.Caption").WithMarginT(6));
        foreach (var snippet in SettingsStore.Current.Snippets)
            AddRow(snippet);
        Body.Children.Add(new ScrollViewer { Content = _list, MaxHeight = 380, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 10, 0, 0) });
        var add = new Button { Content = L.T("Add a Snippet"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 10, 0, 0) };
        add.Click += (_, _) =>
        {
            var row = AddRow(new Snippet { Abbreviation = ";" });
            row.Focus();
            row.CaretIndex = row.Text.Length;
        };
        Body.Children.Add(add);
        PrimaryButton.Content = L.T("Save");
        PrimaryButton.Click += (_, _) => Save();
    }

    private TextBox AddRow(Snippet snippet)
    {
        var abbreviation = new TextBox { Text = snippet.Abbreviation, Width = 120, FlowDirection = FlowDirection.LeftToRight, VerticalAlignment = VerticalAlignment.Top };
        var text = new TextBox { Text = snippet.Text, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 34, MaxHeight = 120, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(8, 0, 8, 0) };
        text.FlowDirection = KeyboardLayout.LooksArabic(text.Text) ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        text.TextChanged += (_, _) => text.FlowDirection = KeyboardLayout.LooksArabic(text.Text) ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        var remove = IconButton("trash", "Delete", 14);
        remove.VerticalAlignment = VerticalAlignment.Top;
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(abbreviation, Dock.Left);
        DockPanel.SetDock(remove, Dock.Right);
        row.Children.Add(abbreviation);
        row.Children.Add(remove);
        row.Children.Add(text);
        var entry = (abbreviation, text, snippet);
        _rows.Add(entry);
        remove.Click += (_, _) =>
        {
            _rows.Remove(entry);
            _list.Children.Remove(row);
        };
        _list.Children.Add(row);
        return abbreviation;
    }

    private void Save()
    {
        var kept = new List<Snippet>();
        foreach (var (abbreviation, text, snippet) in _rows)
        {
            var a = abbreviation.Text.Trim();
            if (a.Length < 2 || text.Text.Length == 0 || kept.Any(k => k.Abbreviation == a))
                continue;
            kept.Add(new Snippet { Id = snippet.Id, Abbreviation = a, Text = text.Text });
        }
        SettingsStore.Current.Snippets = kept;
        SettingsStore.Save();
        Close();
    }
}
