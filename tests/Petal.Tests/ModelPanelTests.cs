using System.Net;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Petal.App.Localization;
using Petal.App.Settings;
using Petal.App.Tools.Parts;
using Petal.App.Tools.Windows;
using Petal.Core.Models;
using Petal.Core.Tools;
using Petal.Recipes;
using Petal.Recipes.Cloud;
using Petal.Recipes.Translation;

namespace Petal.Tests;

/// <summary>
/// The card that asks for an AI model, on no screen and with a stand-in for the provider: for the jobs a key can do both ways stay
/// offered whichever is in use, for others only the download, and a key given there is checked first.
/// </summary>
[Collection("Models")]
public sealed class ModelPanelTests
{
    private static readonly IReadOnlyList<ModelInfo> Writing = AskPetal.Models;
    private static readonly IReadOnlyList<ModelInfo> Seeing = PictureDescriber.Models;
    private static readonly IReadOnlyList<ModelInfo> Speech = [ModelCatalog.WhisperFast];

    /// <summary>A model that isn't on this PC, whatever is in the models folder.</summary>
    private static ModelInfo Absent(ModelInfo model) => model with { Size = model.Size + 1 };

    private static T Inside<T>(DependencyObject from, Func<T, bool> which) where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(from).OfType<DependencyObject>())
        {
            if (child is T found && which(found))
                return found;
            try
            {
                return Inside(child, which);
            }
            catch (InvalidOperationException)
            {
                // Not down this branch.
            }
        }
        throw new InvalidOperationException("Not in the panel.");
    }

    private static Button Button(ModelPanel panel, string words) => Inside<Button>(panel, b => b.Content as string == L.T(words) || b.Content as string == words);

    private static bool Shown(UIElement element)
    {
        for (DependencyObject? at = element; at is not null; at = LogicalTreeHelper.GetParent(at))
            if (at is UIElement { Visibility: not Visibility.Visible })
                return false;
        return true;
    }

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }

    /// <summary>Runs a test on the interface thread with its own settings folder, no job on a key, and a stand-in server.</summary>
    private static void Run(Func<Stub, Task> test, HttpStatusCode status = HttpStatusCode.OK) => PdfEditorWindowTests.RunOnInterface(async () =>
    {
        var folder = Path.Combine(Path.GetTempPath(), "petal-panel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var (settings, store) = (Environment.GetEnvironmentVariable("PETAL_SETTINGS_DIR"), SettingsStore.Directory);
        Environment.SetEnvironmentVariable("PETAL_SETTINGS_DIR", folder);
        SettingsStore.Directory = folder;
        var current = SettingsStore.Current;
        var (w, t, s) = (current.AiWriting, current.AiTranslating, current.AiSeeing);
        current.AiWriting = current.AiTranslating = current.AiSeeing = "";
        AiSetup.Apply(current);
        using var stub = new Stub(status);
        CloudAi.TestServer = stub.Address;
        try
        {
            await test(stub);
        }
        finally
        {
            CloudAi.TestServer = null;
            (current.AiWriting, current.AiTranslating, current.AiSeeing) = (w, t, s);
            AiSetup.Apply(current);
            SettingsStore.Directory = store;
            Environment.SetEnvironmentVariable("PETAL_SETTINGS_DIR", settings);
            Directory.Delete(folder, true);
        }
    });

    private sealed class Stub : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly HttpStatusCode _status;
        public string Address { get; }
        public int Asked { get; private set; }

        public Stub(HttpStatusCode status)
        {
            _status = status;
            int port = Random.Shared.Next(41000, 49000);
            Address = $"http://127.0.0.1:{port}";
            _listener.Prefixes.Add(Address + "/");
            _listener.Start();
            _ = ServeAsync();
        }

        private async Task ServeAsync()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync();
                }
                catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException)
                {
                    return;
                }
                Asked++;
                context.Response.StatusCode = (int)_status;
                var body = Encoding.UTF8.GetBytes("""{"models":[{"name":"models/gemini-test"}],"data":[{"id":"gpt-test"}]}""");
                await context.Response.OutputStream.WriteAsync(body);
                context.Response.Close();
            }
        }

        public void Dispose() => _listener.Close();
    }

    private static async Task Until(Func<bool> done)
    {
        for (int i = 0; i < 300 && !done(); i++)
            await Task.Delay(20);
        Assert.True(done());
    }

    [Fact]
    public void The_key_is_offered_from_the_start_beside_the_download() => Run(_ =>
    {
        var panel = new ModelPanel(Writing.Select(Absent).ToList());
        var own = Button(panel, "Use My API Key");
        var download = Button(panel, "Download");
        Assert.True(Shown(own));
        Assert.True(Shown(download));
        // A button like the Download one, not a line of plain text.
        Assert.Equal(download.Style, own.Style);
        return Task.CompletedTask;
    });

    [Theory]
    [InlineData("writing")]
    [InlineData("translating")]
    [InlineData("seeing")]
    public void Every_job_a_key_can_do_offers_it(string job) => Run(_ =>
    {
        var models = job switch
        {
            "writing" => Writing,
            "seeing" => Seeing,
            _ => Translator.ModelsFor("en", "ar"),
        };
        Assert.True(Shown(Button(new ModelPanel(models.Select(Absent).ToList()), "Use My API Key")));
        return Task.CompletedTask;
    });

    [Fact]
    public void Every_pair_of_languages_can_go_on_a_key()
    {
        foreach (var from in Translator.Languages)
            foreach (var into in Translator.Languages)
                if (from.Code != into.Code)
                    Assert.Equal(AiJob.Translating, AiSetup.JobOf(Translator.ModelsFor(from.Code, into.Code)));
    }

    [Fact]
    public void A_job_a_key_cannot_do_does_not_offer_it() => Run(_ =>
    {
        var panel = new ModelPanel(Speech.Select(Absent).ToList());
        Assert.False(Shown(Button(panel, "Use My API Key")));
        Assert.True(Shown(Button(panel, "Download")));
        return Task.CompletedTask;
    });

    /// <summary>What a translation into the language it is already in asks for: nothing, and that must not break the window.</summary>
    [Fact]
    public void A_panel_asked_for_no_models_hides() => Run(_ =>
    {
        var panel = new ModelPanel(Translator.ModelsFor("en", "ar").Select(Absent).ToList()) { Models = [] };
        Assert.Equal(Visibility.Collapsed, panel.Visibility);
        Assert.True(panel.IsReady);
        return Task.CompletedTask;
    });

    /// <summary>These open with a key translating, with both ways still there.</summary>
    [Fact]
    public void The_windows_that_translate_open_with_a_key_translating() => Run(_ =>
    {
        CloudKeys.Set(CloudProvider.Gemini, "saved");
        AiSetup.Use(AiJob.Translating, CloudProvider.Gemini);
        var folder = Path.Combine(Path.GetTempPath(), "petal-panel-files-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var (subtitles, video, pdf) = (Path.Combine(folder, "talk.srt"), Path.Combine(folder, "talk.mp4"), Path.Combine(folder, "form.pdf"));
        File.WriteAllText(subtitles, "1\n00:00:00,000 --> 00:00:01,000\nHello there.\n");
        File.WriteAllText(video, "");
        Petal.Engines.Documents.Pdf.PdfSampleForm.Write(pdf);
        var jobs = new Petal.App.Jobs.JobRunner(null!);
        var at = new Petal.App.Input.ScreenPoint(0, 0);
        Window[] windows =
        [
            new TranslateSubtitlesWindow(ToolRegistry.Get(ToolId.TranslateSubtitles), [subtitles], jobs, at),
            new TranslateDocumentWindow(ToolRegistry.Get(ToolId.TranslateDocument), [pdf], jobs, at),
            new DubWindow(ToolRegistry.Get(ToolId.Dub), [video], jobs, at),
        ];
        foreach (var window in windows)
            window.Close();
        Directory.Delete(folder, true);
        return Task.CompletedTask;
    });

    [Fact]
    public void The_key_has_its_icon() => Run(_ =>
    {
        Assert.NotNull(Petal.App.Visuals.Icons.Create("key", 14, System.Windows.Media.Brushes.White, 1.9));
        return Task.CompletedTask;
    });

    /// <summary>A model that is on this PC: a small file of its own, in a models folder of the test's.</summary>
    private static ModelInfo Here(string id)
    {
        var name = id + ".gguf";
        File.WriteAllBytes(Path.Combine(ModelStore.Folder, name), new byte[12]);
        return new ModelInfo(id, id, name, "https://example.invalid/" + name, new string('0', 64), 12, "MIT");
    }

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));

    private static async Task WithOwnModels(Func<Task> test)
    {
        var folder = ModelStore.Folder;
        ModelStore.Folder = Path.Combine(Path.GetTempPath(), "petal-panel-models-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(ModelStore.Folder);
        try
        {
            await test();
        }
        finally
        {
            Directory.Delete(ModelStore.Folder, true);
            ModelStore.Folder = folder;
        }
    }

    [Fact]
    public void A_model_on_this_pc_still_offers_the_key() => Run(_ => WithOwnModels(() =>
    {
        var panel = new ModelPanel([Here("qwen-here")]);
        Assert.True(panel.IsReady);
        Assert.Equal(Visibility.Visible, panel.Visibility);
        Assert.True(Shown(Button(panel, "Use My API Key")));
        // Already in use: nothing to press on this side.
        Assert.False(Shown(Button(panel, "Use This PC")));
        return Task.CompletedTask;
    }));

    [Fact]
    public void A_job_on_a_key_goes_back_to_this_pc_with_one_click() => Run(_ => WithOwnModels(() =>
    {
        CloudKeys.Set(CloudProvider.Gemini, "saved");
        AiSetup.Use(AiJob.Writing, CloudProvider.Gemini);
        var panel = new ModelPanel([Here("qwen-here")]);
        int ready = 0, switched = 0;
        panel.Ready += () => ready++;
        panel.Switched += () => switched++;
        Assert.True(CloudAi.CanWrite);
        Click(Button(panel, "Use This PC"));
        Assert.False(CloudAi.CanWrite);
        Assert.Equal("", SettingsStore.Current.AiWriting);
        Assert.Equal((1, 1), (ready, switched));
        // The key stays saved, and stays one click away.
        Assert.True(CloudKeys.Has(CloudProvider.Gemini));
        Assert.Equal(Visibility.Visible, panel.Visibility);
        Assert.True(Shown(Button(panel, "Use My API Key")));
        return Task.CompletedTask;
    }));

    [Fact]
    public void Leaving_the_key_from_settings_with_no_model_here_stops_the_job_waiting() => Run(_ => WithOwnModels(async () =>
    {
        CloudKeys.Set(CloudProvider.Gemini, "saved");
        AiSetup.Use(AiJob.Writing, CloudProvider.Gemini);
        var panel = new ModelPanel(Writing.Select(Absent).ToList());
        var window = new Window { Content = panel, ShowInTaskbar = false, ShowActivated = false, WindowStyle = WindowStyle.None, Width = 1, Height = 1, Left = -30000 };
        window.Show();
        Pump();
        int ready = 0, switched = 0;
        panel.Ready += () => ready++;
        panel.Switched += () => switched++;
        Assert.True(panel.IsReady);
        AiSetup.Use(AiJob.Writing, null);
        await Task.Yield();
        Assert.False(panel.IsReady);
        Assert.Equal((0, 1), (ready, switched));
        Assert.True(Shown(Button(panel, "Download")));
        window.Close();
    }));

    /// <summary>A window waiting on something else is not run again, and its words sent, because of a choice made in another.</summary>
    [Fact]
    public void A_choice_in_one_window_does_not_run_another_again() => Run(_ => WithOwnModels(async () =>
    {
        CloudKeys.Set(CloudProvider.Gemini, "saved");
        var elsewhere = new ModelPanel([Here("qwen-here")]);
        var window = new Window { Content = elsewhere, ShowInTaskbar = false, ShowActivated = false, WindowStyle = WindowStyle.None, Width = 1, Height = 1, Left = -30000 };
        window.Show();
        Pump();
        int ready = 0;
        elsewhere.Ready += () => ready++;
        var here = new ModelPanel(Writing.Select(Absent).ToList());
        Click(Button(here, "Use My API Key"));
        Click(Button(here, L.F("Use {0}", "Gemini")));
        await Until(() => CloudAi.CanWrite);
        Pump();
        Assert.Equal(0, ready);
        window.Close();
    }));

    [Fact]
    public void A_panel_a_window_does_not_need_stays_hidden() => Run(_ => WithOwnModels(() =>
    {
        var panel = new ModelPanel([Here("qwen-here")]) { Wanted = false };
        Assert.Equal(Visibility.Collapsed, panel.Visibility);
        panel.Wanted = true;
        Assert.Equal(Visibility.Visible, panel.Visibility);
        return Task.CompletedTask;
    }));

    [Fact]
    public void Each_translator_keeps_its_own_saved_dub_translation() => Run(_ =>
    {
        // On this PC the names stay as they were, so what was prepared before is still found.
        Assert.Equal("", Dubbing.TranslatedBy(false));
        Assert.Equal("-" + DubTranslation.Writer.Id, Dubbing.TranslatedBy(true));
        CloudKeys.Set(CloudProvider.Gemini, "saved");
        CloudKeys.Set(CloudProvider.Claude, "saved");
        CloudAi.Translating = new CloudChoice(CloudProvider.Gemini, "gemini-a");
        CloudAi.Writing = new CloudChoice(CloudProvider.Claude, "claude/b");
        Assert.Equal("-Gemini-gemini-a", Dubbing.TranslatedBy(false));
        Assert.Equal("-" + DubTranslation.Writer.Id + "-Claude-claude_b", Dubbing.TranslatedBy(true));
        CloudAi.Translating = new CloudChoice(CloudProvider.Gemini, "gemini-other");
        Assert.Equal("-Gemini-gemini-other", Dubbing.TranslatedBy(false));
        return Task.CompletedTask;
    });

    [Fact]
    public void A_good_key_is_used_for_this_job_and_the_download_stays_offered() => Run(async stub =>
    {
        var panel = new ModelPanel(Writing.Select(Absent).ToList());
        var other = new ModelPanel(Translator.ModelsFor("en", "ar").Select(Absent).ToList());
        var host = new StackPanel { Children = { panel, other } };
        var window = new Window { Content = host, ShowInTaskbar = false, ShowActivated = false, WindowStyle = WindowStyle.None, Width = 1, Height = 1, Left = -30000 };
        window.Show();
        Pump();
        int ready = 0;
        panel.Ready += () => ready++;
        Assert.False(panel.IsReady);

        Button(panel, "Use My API Key").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        Inside<PasswordBox>(panel, p => true).Password = " my-key ";
        Button(panel, "Save and Use").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        await Until(() => ready == 1);
        Pump();

        Assert.True(stub.Asked > 0);
        Assert.Equal("my-key", CloudKeys.Get(CloudProvider.Gemini));
        Assert.True(CloudAi.CanWrite);
        Assert.True(panel.IsReady);
        // It is a choice, not the end of the question: the download is still there beside the key in use.
        Assert.Equal(Visibility.Visible, panel.Visibility);
        Assert.True(Shown(Button(panel, "Download")));
        Assert.True(Shown(Button(panel, "Change")));
        // Only the job asked about goes to the company: translation stays on this PC until it too is chosen.
        Assert.False(CloudAi.CanTranslate);
        Assert.Equal(Visibility.Visible, other.Visibility);
        Assert.Equal("Gemini", SettingsStore.Current.AiWriting);
        window.Close();
    });

    [Fact]
    public void A_refused_key_is_not_kept_and_nothing_goes_to_the_company() => Run(async stub =>
    {
        var panel = new ModelPanel(Writing.Select(Absent).ToList());
        int ready = 0;
        panel.Ready += () => ready++;
        Button(panel, "Use My API Key").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        Inside<PasswordBox>(panel, p => true).Password = "wrong";
        Button(panel, "Save and Use").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        await Until(() => stub.Asked > 0 && !CloudKeys.Has(CloudProvider.Gemini));
        Pump();
        Assert.Equal(0, ready);
        Assert.False(CloudAi.CanWrite);
        Assert.Equal("", SettingsStore.Current.AiWriting);
        Assert.Equal(Visibility.Visible, panel.Visibility);
    }, HttpStatusCode.Unauthorized);

    [Fact]
    public void A_key_already_saved_is_used_with_one_click() => Run(async _ =>
    {
        CloudKeys.Set(CloudProvider.ChatGpt, "saved");
        var panel = new ModelPanel(Seeing.Select(Absent).ToList());
        int ready = 0;
        panel.Ready += () => ready++;
        Button(panel, "Use My API Key").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        Assert.False(Shown(Inside<PasswordBox>(panel, p => true)));
        var use = Button(panel, L.F("Use {0}", "ChatGPT"));
        use.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        var window = new Window { Content = panel, ShowInTaskbar = false, ShowActivated = false, WindowStyle = WindowStyle.None, Width = 1, Height = 1, Left = -30000 };
        window.Show();
        await Until(() => CloudAi.CanSee);
        Pump();
        Assert.Equal(1, ready);
        Assert.False(CloudAi.CanWrite);
        window.Close();
    });
}
