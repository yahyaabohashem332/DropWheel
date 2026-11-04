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
    public void Saved_files_lists_each_folder_with_its_real_path() => Run(window =>
    {
        window.GoToPage("Saved Files");
        var shown = window.PlacesShown;
        foreach (var path in new[] { Petal.App.Wheels.Screenshots.Folder, Petal.App.Screen.ScreenRecorder.Folder, Petal.App.Words.MeetingRecorder.Folder,
            Petal.App.Screen.StepGuideWindow.Folder, Petal.App.Actions.PcDrop.Folder, Petal.Core.Models.ModelStore.Folder, Petal.Core.Recording.StudioLibrary.Folder })
            Assert.Contains(path, shown);
    });

    [Fact]
    public void Results_are_said_to_go_where_the_setting_says()
    {
        var settings = new AppSettings { Output = OutputLocation.BesideOriginal, OutputFolder = @"C:\Chosen" };
        Assert.Null(SavedPlaces.Made(settings)[0].Path);
        settings.Output = OutputLocation.Folder;
        Assert.Equal(@"C:\Chosen", SavedPlaces.Made(settings)[0].Path);
    }

    [Fact]
    public void Every_place_says_what_goes_there_and_where()
    {
        foreach (var place in SavedPlaces.Made(new AppSettings()).Skip(1).Concat(SavedPlaces.Own()))
        {
            Assert.False(string.IsNullOrWhiteSpace(place.Title));
            Assert.False(string.IsNullOrWhiteSpace(place.Detail));
            Assert.True(Path.IsPathRooted(place.Path), place.Title);
        }
    }

    [Fact]
    public void An_automation_says_where_it_converts_to()
    {
        var settings = new AppSettings();
        settings.Automations.Add(new Petal.Core.Jobs.WatchRule { Folder = @"C:\In" });
        var last = SavedPlaces.Made(settings)[^1];
        Assert.Equal(settings.Automations[0].OutputFolder, last.Path);
        Assert.Contains("In", last.Detail);
    }

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

/// <summary>Changing the language from the main window: everywhere at once, and nothing else moves.</summary>
[Collection("Language")]
public sealed class LanguageSwitchTests
{
    private static async Task Until(Func<bool> done)
    {
        for (int i = 0; i < 500 && !done(); i++)
            await Task.Delay(20);
        Assert.True(done());
    }

    [Fact]
    public void The_language_changes_at_once_and_the_window_stays_where_it_was() => PdfEditorWindowTests.RunOnInterface(async () =>
    {
        var folder = Path.Combine(Path.GetTempPath(), "petal-language-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var store = SettingsStore.Directory;
        var was = SettingsStore.Current.Language;
        SettingsStore.Directory = folder;
        L.Configure(UiLanguage.English);
        SettingsStore.Current.Language = UiLanguage.English;
        var window = (SettingsWindow)SettingsWindow.ForPage(7);
        window.ShowInTaskbar = false;
        window.ShowActivated = false;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        (window.Left, window.Top, window.Width, window.Height) = (-30000, -30000, 1200, 800);
        window.Show();
        var (page, left) = (window.CurrentPage, window.Left);
        bool closed = false;
        window.Closed += (_, _) => closed = true;
        SettingsWindow? next = null;
        try
        {
            window.ChooseLanguageForTest(UiLanguage.Arabic);
            Assert.Equal("ar", L.Code);
            Assert.Equal(UiLanguage.Arabic, SettingsStore.Current.Language);
            await Until(() => closed);
            next = SettingsWindow.Open;
            Assert.NotNull(next);
            Assert.NotSame(window, next);
            Assert.Equal(FlowDirection.RightToLeft, next.FlowDirection);
            Assert.Equal(page, next.CurrentPage);
            Assert.Equal(UiLanguage.Arabic, next.LanguageShown);
            Assert.Equal((left, 1200d, 800d), (next.Left, next.Width, next.Height));
            Assert.Equal(WindowState.Normal, next.WindowState);

            // And back, as many times as anyone likes.
            bool closedAgain = false;
            next.Closed += (_, _) => closedAgain = true;
            var second = next;
            second.ChooseLanguageForTest(UiLanguage.French);
            Assert.Equal("fr", L.Code);
            await Until(() => closedAgain);
            next = SettingsWindow.Open;
            Assert.NotSame(second, next);
            Assert.Equal(FlowDirection.LeftToRight, next!.FlowDirection);
            Assert.Equal(UiLanguage.French, next.LanguageShown);
        }
        finally
        {
            next?.Close();
            SettingsStore.Current.Language = was;
            SettingsStore.Directory = store;
            L.Configure(UiLanguage.English);
            Directory.Delete(folder, true);
        }
    });
}
