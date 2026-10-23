using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Petal.App.Localization;
using Petal.App.Settings;
using Petal.App.Visuals;

namespace Petal.App.Guide;

/// <summary>An entry laid out to read: its name, what it does, how to use it, examples and what it works on.</summary>
internal static class GuideViews
{
    /// <summary>
    /// The whole explanation. <paramref name="title"/> replaces the entry's name (a favourite's or format's own
    /// label); <paramref name="own"/> is what this one petal does, above the general text (a recipe's steps).
    /// </summary>
    public static StackPanel Explanation(string key, string? title = null, string? own = null, bool withHeader = true, bool withSummary = true)
    {
        var panel = new StackPanel();
        if (GuideBook.Get(key) is not { } e)
            return panel;
        if (withHeader)
            panel.Children.Add(Header(key, title ?? GuideBook.Name(key), 20));
        if (!string.IsNullOrWhiteSpace(own))
            panel.Children.Add(Paragraph(own, bold: true).WithMargin(0, 0, 0, 6));
        if (withSummary)
            panel.Children.Add(Paragraph(e.Summary, bold: string.IsNullOrWhiteSpace(own)));
        panel.Children.Add(Paragraph(e.Details).WithMargin(0, withSummary ? 8 : 0, 0, 0));
        panel.Children.Add(Label("How to use it"));
        panel.Children.Add(Paragraph(e.How));
        if (e.Examples.Count > 0)
        {
            panel.Children.Add(Label("Examples"));
            foreach (var example in e.Examples)
                panel.Children.Add(Bullet(example));
        }
        if (e.WorksOn.Length > 0)
        {
            panel.Children.Add(Label("Works on"));
            panel.Children.Add(Paragraph(e.WorksOn));
        }
        return panel;
    }

    /// <summary>The entry's icon in a soft disc, and its name.</summary>
    public static FrameworkElement Header(string key, string name, double size)
    {
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        var disc = new Border { Width = size + 18, Height = size + 18, CornerRadius = new CornerRadius((size + 18) / 2), Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Top };
        disc.SetResourceReference(Border.BackgroundProperty, "Petal.AccentSoft");
        var icon = Icons.Create(GuideBook.Icon(key), size, Palette.Brush(Theme.Current.Accent), 2);
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        icon.VerticalAlignment = VerticalAlignment.Center;
        disc.Child = icon;
        DockPanel.SetDock(disc, Dock.Left);
        row.Children.Add(disc);
        var title = new TextBlock { Text = name, FontFamily = UiFonts.Display, FontSize = 17, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        title.SetResourceReference(TextBlock.ForegroundProperty, "Petal.Text");
        row.Children.Add(title);
        return row;
    }

    public static TextBlock Paragraph(string text, bool bold = false)
    {
        var block = new TextBlock
        {
            Text = text,
            FontFamily = UiFonts.Text,
            FontSize = 13.5,
            LineHeight = 20,
            TextWrapping = TextWrapping.Wrap,
            FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
        };
        block.SetResourceReference(TextBlock.ForegroundProperty, "Petal.Text");
        return block;
    }

    private static TextBlock Label(string text)
    {
        var label = new TextBlock { Text = L.T(text), FontFamily = UiFonts.Text, FontSize = 12, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 4) };
        label.SetResourceReference(TextBlock.ForegroundProperty, "Petal.Accent");
        return label;
    }

    private static FrameworkElement Bullet(string text)
    {
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 5) };
        var dot = new System.Windows.Shapes.Ellipse { Width = 5, Height = 5, Margin = new Thickness(2, 8, 9, 0), VerticalAlignment = VerticalAlignment.Top };
        dot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "Petal.Accent");
        DockPanel.SetDock(dot, Dock.Left);
        row.Children.Add(dot);
        row.Children.Add(Paragraph(text));
        return row;
    }
}
