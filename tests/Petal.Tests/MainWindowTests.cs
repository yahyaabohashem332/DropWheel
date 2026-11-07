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
        Assert.Equal("Keys & Behaviour", seen[0]);
        Assert.Equal("Wheels", seen[1]);
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
    public void A_folder_can_be_changed_and_put_back()
    {
        var settings = new AppSettings();
        var usual = SavedPlaces.Folder(SavedKind.Screenshots, settings);
        var mine = Path.GetTempPath().TrimEnd('\\') + @"\My Shots";
        SavedPlaces.Choose(SavedKind.Screenshots, mine, settings);
        Assert.Equal(mine, SavedPlaces.Folder(SavedKind.Screenshots, settings));
        Assert.Equal(mine, SavedPlaces.Made(settings).First(place => place.Kind == SavedKind.Screenshots).Path);
        // Only that kind.
        Assert.NotEqual(mine, SavedPlaces.Folder(SavedKind.Recordings, settings));
        SavedPlaces.Choose(SavedKind.Screenshots, null, settings);
        Assert.Equal(usual, SavedPlaces.Folder(SavedKind.Screenshots, settings));
    }

    [Fact]
    public void Scans_share_the_folder_their_own_window_chooses()
    {
        var settings = new AppSettings();
        var mine = Path.GetTempPath().TrimEnd('\\') + @"\My Scans";
        SavedPlaces.Choose(SavedKind.Scans, mine, settings);
        Assert.Equal(mine, settings.ScanFolder);
        Assert.Equal(mine, SavedPlaces.Folder(SavedKind.Scans, settings));
    }

    [Fact]
    public void A_folder_on_a_drive_that_is_gone_falls_back_to_the_usual_one()
    {
        var settings = new AppSettings();
        var gone = new[] { "Q", "X", "Y", "Z" }.Select(d => d + @":\\").First(root => !Directory.Exists(root)) + "Shots";
        SavedPlaces.Choose(SavedKind.Screenshots, gone, settings);
        Assert.Equal(SavedPlaces.Usual(SavedKind.Screenshots), SavedPlaces.Folder(SavedKind.Screenshots, settings));
    }

    [Fact]
    public void Saved_files_offers_to_change_what_can_be_changed() => Run(window =>
    {
        window.GoToPage("Saved Files");
        var was = SettingsStore.Current.SavedFolders.ToDictionary();
        var store = SettingsStore.Directory;
        SettingsStore.Directory = Path.Combine(Path.GetTempPath(), "petal-places-" + Guid.NewGuid().ToString("N"));
        try
        {
            var mine = Path.GetTempPath().TrimEnd('\\') + @"\My Recordings";
            window.SetPlace(SavedKind.Recordings, mine);
            Assert.Equal(mine, Petal.App.Screen.ScreenRecorder.Folder);
            Assert.Contains(mine, window.PlacesShown);
            window.SetPlace(SavedKind.Recordings, null);
            Assert.Equal(SavedPlaces.Usual(SavedKind.Recordings), Petal.App.Screen.ScreenRecorder.Folder);
        }
        finally
        {
            SettingsStore.Current.SavedFolders = was;
            SettingsStore.Directory = store;
        }
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
    public void The_gallery_is_open_beside_the_wheels() => Run(window =>
    {
        window.GoToPage("Wheels");
        window.UpdateLayout();
        Assert.True(window.GalleryForTest.IsVisible);
        var cards = window.GalleryForTest.CardsForTest;
        // The ready-made wheels are there too, and every one of the gallery's.
        Assert.True(cards.Count >= Petal.App.Wheels.WheelGallery.All.Count + Petal.App.Wheels.WheelTemplates.All().Count);
    });

    [Fact]
    public void A_wheel_from_the_gallery_goes_on_the_list_and_can_come_back() => Run(window =>
    {
        var wheels = SettingsStore.Current.Wheels;
        var kept = wheels.ToList();
        var store = SettingsStore.Directory;
        SettingsStore.Directory = Path.Combine(Path.GetTempPath(), "petal-gallery-" + Guid.NewGuid().ToString("N"));
        try
        {
            wheels.Clear();
            var gallery = window.GalleryForTest;
            gallery.Refresh();
            Assert.True(gallery.CardsForTest[0].CanAdd);
            gallery.AddForTest(0);
            Assert.Single(wheels);
            Assert.False(gallery.CardsForTest[0].CanAdd);
            wheels.Clear();
            gallery.Refresh();
            Assert.True(gallery.CardsForTest[0].CanAdd);
        }
        finally
        {
            wheels.Clear();
            wheels.AddRange(kept);
            SettingsStore.Directory = store;
        }
    });

    [Fact]
    public void The_gallery_finds_a_wheel_by_a_word_in_it() => Run(window =>
    {
        var gallery = window.GalleryForTest;
        var first = gallery.CardsForTest[0].Words;
        gallery.FindForTest(first.Split(' ')[0]);
        Assert.True(gallery.CardsForTest[0].Shown);
        gallery.FindForTest("no wheel says this");
        Assert.DoesNotContain(gallery.CardsForTest, card => card.Shown);
        gallery.FindForTest("");
        Assert.All(gallery.CardsForTest, card => Assert.True(card.Shown));
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

/// <summary>Changing the language from the main window: everywhere at once, and nothing else moves.</summary>
[Collection("Language")]
public sealed class LanguageSwitchTests
{
    [Fact]
    public void The_usual_folders_have_the_same_english_names_in_every_language()
    {
        try
        {
            foreach (var language in L.Languages.Select(l => l.Language))
            {
                L.Configure(language);
                foreach (var kind in Enum.GetValues<SavedKind>())
                    Assert.All(SavedPlaces.Usual(kind).Split('\\').TakeLast(1), name => Assert.DoesNotMatch(@"\p{IsArabic}", kind == SavedKind.Tables || kind == SavedKind.FromPhone ? "" : name));
                Assert.Equal("Scans", Path.GetFileName(SavedPlaces.Usual(SavedKind.Scans)));
            }
        }
        finally
        {
            L.Configure(UiLanguage.English);
        }
    }

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
        // As Windows placed it: on a screen smaller than 1200 by 800 it keeps the window inside the screen.
        var (page, left, width, height) = (window.CurrentPage, window.Left, window.Width, window.Height);
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
            Assert.Equal((left, width, height), (next.Left, next.Width, next.Height));
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

/// <summary>The bar that ends a recording: other programs can't take it away, and only finishing closes it.</summary>
[Collection(PdfEditorWindowTests.OneApplication)]
public sealed class KeepOnTopTests
{
    private static System.Windows.Window Bar() => new()
    {
        Width = 120,
        Height = 40,
        WindowStyle = WindowStyle.None,
        ShowInTaskbar = false,
        ShowActivated = false,
        Topmost = true,
        WindowStartupLocation = WindowStartupLocation.Manual,
        Left = -30000,
        Top = -30000,
    };

    private static bool OnTop(System.Windows.Window window)
    {
        var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        return Petal.App.Screen.ScreenNative.IsWindowVisible(handle) && !Petal.App.Screen.ScreenNative.IsIconic(handle)
            && ((long)Petal.App.Interop.NativeMethods.GetWindowLongPtr(handle, Petal.App.Interop.NativeMethods.GWL_EXSTYLE) & Petal.App.Interop.NativeMethods.WS_EX_TOPMOST) != 0;
    }

    [Fact]
    public void A_bar_that_lost_its_place_comes_back() => PdfEditorWindowTests.RunOnInterface(() =>
    {
        var bar = Bar();
        bar.Show();
        var handle = new System.Windows.Interop.WindowInteropHelper(bar).Handle;
        var keep = Petal.App.Screen.KeepOnTop.Start(bar);
        try
        {
            Assert.True(OnTop(bar));

            // Another program took "always on top" away.
            Petal.App.Interop.NativeMethods.SetWindowPos(handle, new IntPtr(-2), 0, 0, 0, 0, Petal.App.Interop.NativeMethods.SWP_NOMOVE | Petal.App.Interop.NativeMethods.SWP_NOSIZE | Petal.App.Interop.NativeMethods.SWP_NOACTIVATE);
            Assert.False(OnTop(bar));
            keep.Check();
            Assert.True(OnTop(bar));

            // Show Desktop or a minimize.
            Petal.App.Screen.ScreenNative.ShowWindow(handle, Petal.App.Screen.ScreenNative.SW_MINIMIZE);
            Assert.False(OnTop(bar));
            keep.Check();
            Assert.True(OnTop(bar));

            // Hidden outright.
            bar.Hide();
            Assert.False(OnTop(bar));
            keep.Check();
            Assert.True(OnTop(bar));
        }
        finally
        {
            bar.Close();
        }
        return Task.CompletedTask;
    });

    [Fact]
    public void Windows_under_the_bar_come_back_and_the_bar_stays_over_them() => PdfEditorWindowTests.RunOnInterface(() =>
    {
        var (bar, frame) = (Bar(), Bar());
        frame.Show();
        bar.Show();
        var keep = Petal.App.Screen.KeepOnTop.Start(bar, () => [frame]);
        try
        {
            var handle = new System.Windows.Interop.WindowInteropHelper(frame).Handle;
            Petal.App.Screen.ScreenNative.ShowWindow(handle, Petal.App.Screen.ScreenNative.SW_MINIMIZE);
            keep.Check();
            Assert.True(OnTop(frame));
            Assert.True(OnTop(bar));
        }
        finally
        {
            bar.Close();
            frame.Close();
        }
        return Task.CompletedTask;
    });

    [Fact]
    public void A_bar_on_no_monitor_is_put_back() => PdfEditorWindowTests.RunOnInterface(() =>
    {
        var bar = Bar();
        bar.Show();
        int lost = 0;
        var keep = Petal.App.Screen.KeepOnTop.Start(bar, lost: () => lost++);
        try
        {
            keep.Check();
            Assert.Equal(1, lost);
        }
        finally
        {
            bar.Close();
        }
        return Task.CompletedTask;
    });

    [Fact]
    public void Only_finishing_closes_the_bar() => PdfEditorWindowTests.RunOnInterface(() =>
    {
        var bar = Bar();
        bar.Show();
        bool finished = false, closed = false;
        bar.Closed += (_, _) => closed = true;
        Petal.App.Screen.KeepOnTop.CloseOnlyWhen(bar, () => finished);
        bar.Close();
        Assert.False(closed);
        Assert.True(bar.IsVisible);
        finished = true;
        bar.Close();
        Assert.True(closed);
        return Task.CompletedTask;
    });
}
