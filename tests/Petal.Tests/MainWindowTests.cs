using System.Windows;
using Petal.App.Localization;
using Petal.App.Settings;

namespace Petal.Tests;

/// <summary>The main window, built in this process: every card has its page, and finding one, going to one and Esc do what they say.</summary>
[Collection(PdfEditorWindowTests.OneApplication)]
public sealed class MainWindowTests
{
    private static void Run(Action<SettingsWindow> test, int page = 0) => PdfEditorWindowTests.RunOnInterface(() =>
    {
        var window = (SettingsWindow)SettingsWindow.ForPage(page);
        window.ShowInTaskbar = false;
        window.ShowActivated = false;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        (window.Left, window.Top) = (-30000, -30000);
        window.Show();
        try
        {
            test(window);
        }
        finally
        {
            window.Close();
        }
        return Task.CompletedTask;
    });

    [Fact]
    public void Every_card_is_on_a_page() => Run(window =>
    {
        Assert.Empty(window.CardsOffPages);
        Assert.Equal(SettingsWindow.PageCount, window.PageTitles.Count);
    });

    [Fact]
    public void Nothing_that_was_in_the_window_is_lost() => Run(window =>
    {
        foreach (var card in new[] { "Keys", "The Wheel", "Wheels", "Appearance", "Actions Wheel", "Favourites", "Recipes", "Automations", "Your Petals",
            "AI Models", "Search by Meaning", "Results", "Quality", "General", "Helpers", "Backup", "Updates", "Statistics", "Screen & Text", "Work & Sharing" })
            Assert.True(window.HasCard(card), card);
    });

    [Theory]
    [InlineData("AI Models", "AI")]
    [InlineData("Statistics", "Home")]
    [InlineData("Backup", "System")]
    [InlineData("Appearance", "Appearance")]
    public void Going_to_a_card_opens_its_page(string card, string page) => Run(window =>
    {
        window.GoToCard(card);
        Assert.Equal(page, window.CurrentPage);
    }, 1);

    [Fact]
    public void A_setting_is_found_by_the_words_inside_its_card() => Run(window =>
    {
        // A word in a control's own label, not the card's title.
        Assert.Contains(L.T("Quality"), window.FindForTest(L.T("GIF frame rate")));
        Assert.Empty(window.FindForTest("no setting says this"));
        window.FindForTest("");
    });

    [Fact]
    public void The_tour_goes_through_the_pages() => Run(window =>
    {
        var seen = new List<string?>();
        var tour = window.BeginTour();
        do
        {
            window.UpdateLayout();
            seen.Add(window.CurrentPage);
        }
        while (tour.Next());
        Assert.Equal(9, seen.Count);
        Assert.Equal("Keys & Wheels", seen[0]);
        Assert.Equal("Appearance", seen[2]);
        Assert.Contains("Home", seen);
        Assert.Equal("System", seen[^1]);
    });

    [Fact]
    public void Each_page_opens() => Run(window =>
    {
        foreach (var title in window.PageTitles)
        {
            window.GoToPage(title);
            window.UpdateLayout();
            Assert.Equal(title, window.CurrentPage);
        }
    });

    /// <summary>Maximized on the real screen, it flashes up for a moment: set PETAL_SHOW_MAIN to run it. Nothing may reach past the work area.</summary>
    [Fact]
    public void Maximized_it_stays_within_the_screen()
    {
        if (Environment.GetEnvironmentVariable("PETAL_SHOW_MAIN") is not { Length: > 0 })
            return;
        PdfEditorWindowTests.RunOnInterface(async () =>
        {
            var window = (SettingsWindow)SettingsWindow.ForPage(0);
            window.WindowState = WindowState.Maximized;
            window.Show();
            await Task.Delay(500);
            var content = (FrameworkElement)window.Content;
            var scale = System.Windows.Media.VisualTreeHelper.GetDpi(window).DpiScaleX;
            var margin = content.Margin;
            var (left, top) = (window.PointToScreen(new Point(margin.Left, margin.Top)).X / scale, window.PointToScreen(new Point(margin.Left, margin.Top)).Y / scale);
            var (width, height) = (window.ActualWidth - margin.Left - margin.Right, window.ActualHeight - margin.Top - margin.Bottom);
            var work = SystemParameters.WorkArea;
            window.Close();
            Assert.InRange(left, work.Left - 1, work.Left + 1);
            Assert.InRange(top, work.Top - 1, work.Top + 1);
            Assert.InRange(width, work.Width - 2, work.Width + 2);
            Assert.InRange(height, work.Height - 2, work.Height + 2);
        });
    }
}
