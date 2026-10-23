using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Petal.App.Localization;
using Petal.App.Settings;
using Petal.App.Tools;
using Petal.App.Visuals;

namespace Petal.App.Stats;

/// <summary>
/// What Petal has done for you, counted on this PC only: files handled, space saved, the petals used most,
/// the days in a row, a calendar of the last weeks, and milestones. Counting can be paused or wiped.
/// </summary>
public sealed class StatsWindow : ToolWindow
{
    private static StatsWindow? _open;
    private readonly StackPanel _page = new();

    public static void ShowSingle()
    {
        if (_open is { IsLoaded: true })
        {
            _open.Activate();
            return;
        }
        _open = new StatsWindow();
        _open.Closed += (_, _) => _open = null;
        _open.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        _open.Show();
        _open.Activate();
    }

    private StatsWindow() : base("Your Statistics", 520)
    {
        Body.Children.Add(_page);
        PrimaryButton.Content = L.T("Done");
        PrimaryButton.Click += (_, _) => Close();
        ResetButton.Content = L.T("Start Over…");
        ResetButton.Visibility = Visibility.Visible;
        ResetButton.Click += (_, _) =>
        {
            if (MessageBox.Show(this, L.T("Forget everything counted so far?"), L.T("Your Statistics"), MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
                return;
            Statistics.Reset();
            Fill();
        };
        void Changed() => Dispatcher.BeginInvoke(Fill);
        Statistics.Changed += Changed;
        Closed += (_, _) => Statistics.Changed -= Changed;
        Fill();
    }

    private void Fill()
    {
        _page.Children.Clear();
        var data = Statistics.Snapshot();
        var s = SettingsStore.Current;

        // The big numbers.
        var numbers = new UniformGrid4();
        numbers.Add(Big(data.Files.ToString("N0", System.Globalization.CultureInfo.InvariantCulture), L.P(data.Files, "file handled", "files handled")));
        numbers.Add(Big(Util.Sizes.Format(data.Saved), L.T("saved")));
        numbers.Add(Big(data.Uses.ToString("N0", System.Globalization.CultureInfo.InvariantCulture), L.P(data.Uses, "petal used", "petals used")));
        var streak = Statistics.Streak(data);
        numbers.Add(Big(streak.ToString(System.Globalization.CultureInfo.InvariantCulture), L.P(streak, "day in a row", "days in a row")));
        _page.Children.Add(numbers.Panel);

        _page.Children.Add(Text(L.F("Since {0} · longest run {1}", data.Since.ToString("d MMM yyyy", L.Culture), L.P(Statistics.LongestStreak(data), "{0} day", "{0} days")), "Petal.Caption").WithMargin(0, 8, 0, 0));

        // Twelve weeks of days, oldest on the left, today at the bottom right.
        _page.Children.Add(Heading("The last twelve weeks"));
        _page.Children.Add(Calendar(data));

        _page.Children.Add(Heading("Used most"));
        var top = data.Petals.OrderByDescending(p => p.Value).Take(5).ToList();
        if (top.Count == 0)
            _page.Children.Add(Text("Nothing yet. Drag a file and tap Shift to begin.", "Petal.Caption"));
        int most = top.Count > 0 ? top[0].Value : 1;
        foreach (var (key, count) in top)
            _page.Children.Add(Bar(NameOf(key, data.Names.GetValueOrDefault(key)), count, most));

        var milestones = Statistics.Milestones(data);
        if (milestones.Count > 0)
        {
            _page.Children.Add(Heading("Milestones"));
            var wrap = new WrapPanel();
            foreach (var m in milestones)
                wrap.Children.Add(Chip(Milestone(m)));
            _page.Children.Add(wrap);
        }

        var counting = new CheckBox
        {
            Content = L.T("Keep counting (on this PC only; nothing is sent anywhere)"),
            IsChecked = s.KeepStatistics,
            Margin = new Thickness(0, 16, 0, 0),
        };
        counting.Click += (_, _) =>
        {
            s.KeepStatistics = counting.IsChecked == true;
            SettingsStore.Save();
        };
        _page.Children.Add(counting);
    }

    /// <summary>
    /// A petal's name in today's language, from its key ("format:webp", "tool:Compress"); the name kept when it
    /// was used stands in for petals that are gone.
    /// </summary>
    internal static string NameOf(string key, string? kept)
    {
        var (kind, id) = key.IndexOf(':') is int at and > 0 ? (key[..at], key[(at + 1)..]) : ("", key);
        switch (kind)
        {
            case "format" when Petal.Core.Formats.Formats.FromId(id) is { } format:
                return L.F("Convert to {0}", format.Label);
            case "tool" when Enum.TryParse<Petal.Core.Tools.ToolId>(id, out var tool):
                return L.T(Petal.Core.Tools.ToolRegistry.Get(tool).Name);
            case "action" when Enum.TryParse<Petal.Core.Actions.FileActionId>(id, out var action):
                return L.T(Petal.Core.Actions.FileActionRegistry.All.First(a => a.Id == action).Name);
            case "favourite" when SettingsStore.Current.Favourites.FirstOrDefault(f => f.Id.ToString() == id) is { } favourite:
                return Jobs.FavouriteText.Label(favourite);
            case "recipe" when SettingsStore.Current.Recipes.FirstOrDefault(r => r.Id.ToString() == id) is { } recipe:
                return recipe.Name;
            case "entry" when Find(SettingsStore.Current.Wheels.SelectMany(w => w.Items), id) is { } entry:
                return L.T(Wheels.Rings.Item(entry).Name);
        }
        return L.T(kept ?? key);

        static Wheels.WheelEntry? Find(IEnumerable<Wheels.WheelEntry> entries, string id)
        {
            foreach (var e in entries)
            {
                if (e.Id == id) return e;
                if (e.Children is { } children && Find(children, id) is { } inner) return inner;
            }
            return null;
        }
    }

    private static string Milestone(string key)
    {
        var (kind, value) = (key.Split(':')[0], int.Parse(key.Split(':')[1], System.Globalization.CultureInfo.InvariantCulture));
        return kind switch
        {
            "files" => L.P(value, "{0} file", "{0} files"),
            "saved" => L.F("{0} saved", Util.Sizes.Format((long)value * 1000 * 1000)),
            _ => L.P(value, "{0} days in a row", "{0} days in a row"),
        };
    }

    private static TextBlock Heading(string text)
    {
        var t = Text(text, "Petal.Section");
        t.FontSize = 13;
        t.Margin = new Thickness(0, 18, 0, 8);
        return t;
    }

    private static StackPanel Big(string number, string caption)
    {
        // A number reads left to right, unless its unit is written in a right-to-left language ("3.3 گیگابایت").
        bool rightToLeftUnit = number.Any(c => c is >= '\u0600' and <= '\u06FF');
        var value = new TextBlock { Text = number, FontSize = 24, FontWeight = FontWeights.SemiBold, FlowDirection = rightToLeftUnit ? FlowDirection.RightToLeft : FlowDirection.LeftToRight };
        value.SetResourceReference(TextBlock.ForegroundProperty, "Petal.Text");
        var label = Text(caption, "Petal.Caption");
        // Shrinks, rather than being cut, when a long unit doesn't fit the column.
        var fit = new Viewbox { Child = value, StretchDirection = StretchDirection.DownOnly, HorizontalAlignment = HorizontalAlignment.Left, Height = 32 };
        return new StackPanel { Margin = new Thickness(0, 0, 12, 0), Children = { fit, label } };
    }

    private static Grid Bar(string name, int count, int most)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
        var label = Text(name, "Petal.Body");
        label.TextTrimming = TextTrimming.CharacterEllipsis;
        grid.Children.Add(label);
        var track = new Grid { Height = 8, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 8, 0) };
        track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(count, GridUnitType.Star) });
        track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0, most - count), GridUnitType.Star) });
        var fill = new Border { CornerRadius = new CornerRadius(4) };
        fill.SetResourceReference(Border.BackgroundProperty, "Petal.Accent");
        track.Children.Add(fill);
        Grid.SetColumn(track, 1);
        grid.Children.Add(track);
        var number = new TextBlock { Text = count.ToString(System.Globalization.CultureInfo.InvariantCulture), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
        number.SetResourceReference(TextBlock.ForegroundProperty, "Petal.TextSecondary");
        Grid.SetColumn(number, 2);
        grid.Children.Add(number);
        return grid;
    }

    private static Border Chip(string text)
    {
        var chip = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 3, 10, 4), Margin = new Thickness(0, 0, 6, 6), Child = new TextBlock { Text = text, FontSize = 12 } };
        chip.SetResourceReference(Border.BackgroundProperty, "Petal.AccentSoft");
        return chip;
    }

    /// <summary>Days as squares, darker for busier days, in seven rows (Monday at the top) and twelve columns.</summary>
    private static UIElement Calendar(StatsData data)
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        int monday = ((int)today.DayOfWeek + 6) % 7;
        var start = today.AddDays(-monday - 7 * 11);
        int busiest = Math.Max(1, data.Days.Values.Select(d => d.Uses + d.Files).DefaultIfEmpty(0).Max());
        var canvas = new Canvas { Width = 12 * 16, Height = 7 * 16, FlowDirection = FlowDirection.LeftToRight, HorizontalAlignment = HorizontalAlignment.Left };
        var accent = Theme.Current.Accent;
        for (int week = 0; week < 12; week++)
        {
            for (int day = 0; day < 7; day++)
            {
                var date = start.AddDays(week * 7 + day);
                if (date > today) continue;
                double level = data.Days.TryGetValue(Statistics.Key(date), out var count) ? Math.Min(1, (count.Uses + count.Files) / (double)busiest) : 0;
                var square = new Rectangle
                {
                    Width = 12,
                    Height = 12,
                    RadiusX = 3,
                    RadiusY = 3,
                    Fill = level == 0
                        ? new SolidColorBrush(Theme.Current.IsDark ? Color.FromArgb(0x22, 255, 255, 255) : Color.FromArgb(0x14, 0, 0, 0))
                        : new SolidColorBrush(Color.FromArgb((byte)(70 + 185 * level), accent.R, accent.G, accent.B)),
                    ToolTip = date.ToString("d MMM", L.Culture),
                };
                Canvas.SetLeft(square, week * 16);
                Canvas.SetTop(square, day * 16);
                canvas.Children.Add(square);
            }
        }
        return canvas;
    }

    /// <summary>Four numbers side by side.</summary>
    private sealed class UniformGrid4
    {
        public System.Windows.Controls.Primitives.UniformGrid Panel { get; } = new() { Columns = 4 };

        public void Add(UIElement element) => Panel.Children.Add(element);
    }
}
