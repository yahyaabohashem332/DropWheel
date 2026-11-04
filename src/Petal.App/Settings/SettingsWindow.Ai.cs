using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using Petal.App.Localization;
using Petal.Recipes.Cloud;

namespace Petal.App.Settings;

/// <summary>Tells the AI layer what the settings chose: this PC, or a cloud model on the user's own key.</summary>
internal static class AiSetup
{
    /// <summary>What a tool that writes says while it works: the words given when it is this PC, or the cloud model it has gone to.</summary>
    public static string Writing(string onThisPc) =>
        CloudAi.CanWrite ? L.F("Writing with {0}, on your own key…", CloudAi.Name(CloudAi.Writing!.Provider)) : L.T(onThisPc);

    /// <summary>What a tool that looks at a picture says while it works.</summary>
    public static string Looking =>
        CloudAi.CanSee ? L.F("Looking at the picture with {0}, on your own key…", CloudAi.Name(CloudAi.Seeing!.Provider)) : L.T("Looking at the picture on this PC…");

    /// <summary>The caveat under what a model wrote or saw.</summary>
    public static string WorkedOut(CloudChoice? cloud) =>
        cloud is null ? L.T("Worked out on this PC; it can be wrong.") : L.F("Worked out by {0}, on your own key; it can be wrong.", CloudAi.Name(cloud.Provider));

    public static void Apply(AppSettings settings)
    {
        CloudAi.Writing = Choice(settings, settings.AiWriting);
        CloudAi.Translating = Choice(settings, settings.AiTranslating);
        CloudAi.Seeing = Choice(settings, settings.AiSeeing);
    }

    private static CloudChoice? Choice(AppSettings settings, string name) =>
        Enum.TryParse<CloudProvider>(name, out var provider) ? new CloudChoice(provider, settings.AiModels.GetValueOrDefault(name) is { Length: > 0 } model ? model : CloudAi.DefaultModel(provider)) : null;
}

public sealed partial class SettingsWindow
{
    /// <summary>
    /// Each AI job runs on this PC with a downloaded model unless a cloud model is chosen for it. A key is the user's own:
    /// they pay the provider directly, and nothing passes through anything of ours.
    /// </summary>
    private UIElement AiSection()
    {
        var panel = new StackPanel();
        var jobs = new StackPanel();
        void FillJobs()
        {
            jobs.Children.Clear();
            jobs.Children.Add(Text("What does each job", "Petal.Label").WithMargin(0, 16, 0, 0));
            jobs.Children.Add(JobRow("Writing and answers", _s.AiWriting, v => _s.AiWriting = v));
            jobs.Children.Add(JobRow("Translation", _s.AiTranslating, v => _s.AiTranslating = v));
            jobs.Children.Add(JobRow("Describing pictures", _s.AiSeeing, v => _s.AiSeeing = v));
            jobs.Children.Add(Text("This PC needs a model downloaded once, and nothing leaves it. A cloud model needs no download, but what the job works on goes to that company, and the provider bills your key.", "Petal.Caption").WithMargin(0, 8, 0, 0));
        }
        foreach (var provider in Enum.GetValues<CloudProvider>())
            panel.Children.Add(ProviderRow(provider, FillJobs));
        FillJobs();
        panel.Children.Add(jobs);
        return panel;
    }

    private UIElement JobRow(string job, string current, Action<string> chosen)
    {
        var box = new ComboBox { MinWidth = 220 };
        box.Items.Add(new ComboBoxItem { Content = L.T("This PC"), Tag = "" });
        foreach (var provider in Enum.GetValues<CloudProvider>())
            if (CloudKeys.Has(provider))
                box.Items.Add(new ComboBoxItem { Content = CloudAi.Name(provider), Tag = provider.ToString() });
        box.SelectedItem = box.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == current) ?? box.Items[0];
        box.SelectionChanged += (_, _) =>
        {
            if (box.SelectedItem is ComboBoxItem { Tag: string name })
            {
                chosen(name);
                AiSetup.Apply(_s);
                Save();
            }
        };
        return Row(job, box, 170, 6);
    }

    private UIElement ProviderRow(CloudProvider provider, Action changed)
    {
        var name = provider.ToString();
        var row = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        var heading = new DockPanel();
        var get = new Button { Content = L.T("Get a key"), Style = (Style)FindResource("Petal.SubtleButton"), Padding = new Thickness(8, 2, 8, 2) };
        get.Click += (_, _) => Process.Start(new ProcessStartInfo(CloudAi.KeysPage(provider)) { UseShellExecute = true });
        DockPanel.SetDock(get, Dock.Right);
        heading.Children.Add(get);
        heading.Children.Add(Text(CloudAi.Name(provider), "Petal.Label"));
        row.Children.Add(heading);

        var key = new PasswordBox { MinWidth = 260, Margin = new Thickness(0, 6, 8, 0), FlowDirection = FlowDirection.LeftToRight };
        var save = new Button { Content = L.T("Save Key"), Margin = new Thickness(0, 6, 8, 0) };
        var remove = new Button { Content = L.T("Remove"), Margin = new Thickness(0, 6, 0, 0) };
        var status = Text("", "Petal.Caption").WithMargin(0, 4, 0, 0);
        var model = new ComboBox { IsEditable = true, MinWidth = 260, FlowDirection = FlowDirection.LeftToRight };
        model.Text = _s.AiModels.GetValueOrDefault(name) is { Length: > 0 } saved ? saved : CloudAi.DefaultModel(provider);
        var modelRow = Row("Model", model, 80, 6);

        void Show()
        {
            bool has = CloudKeys.Has(provider);
            remove.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
            modelRow.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
            key.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
            save.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
            if (has && status.Text.Length == 0)
                status.Text = L.T("A key is saved, encrypted for this Windows user.");
        }

        async Task CheckAsync()
        {
            status.Text = L.T("Checking the key…");
            try
            {
                var models = await CloudAi.ModelsAsync(provider);
                var chosen = model.Text;
                model.ItemsSource = models;
                model.Text = chosen;
                status.Text = L.F("The key works: {0} models on offer.", models.Count);
            }
            catch (CloudException ex)
            {
                status.Text = L.T(ex.Message);
            }
        }

        save.Click += async (_, _) =>
        {
            if (key.Password.Trim().Length == 0)
                return;
            CloudKeys.Set(provider, key.Password);
            key.Clear();
            status.Text = "";
            Show();
            changed();
            AiSetup.Apply(_s);
            await CheckAsync();
        };
        remove.Click += (_, _) =>
        {
            CloudKeys.Remove(provider);
            foreach (var clear in new Action[] { () => { if (_s.AiWriting == name) _s.AiWriting = ""; }, () => { if (_s.AiTranslating == name) _s.AiTranslating = ""; }, () => { if (_s.AiSeeing == name) _s.AiSeeing = ""; } })
                clear();
            AiSetup.Apply(_s);
            Save();
            status.Text = "";
            Show();
            changed();
        };
        model.LostKeyboardFocus += (_, _) => Remember();
        model.SelectionChanged += (_, _) =>
        {
            if (model.SelectedItem is string picked)
            {
                model.Text = picked;
                Remember();
            }
        };
        void Remember()
        {
            if (model.Text.Trim() is { Length: > 0 } text && _s.AiModels.GetValueOrDefault(name) != text)
            {
                _s.AiModels[name] = text;
                AiSetup.Apply(_s);
                Save();
            }
        }

        row.Children.Add(new WrapPanel { Children = { key, save, remove } });
        row.Children.Add(modelRow);
        row.Children.Add(status);
        Show();
        return row;
    }
}
