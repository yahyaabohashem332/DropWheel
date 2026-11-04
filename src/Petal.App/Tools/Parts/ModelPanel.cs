using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Petal.App.Localization;
using Petal.App.Settings;
using Petal.App.Util;
using Petal.Core.Models;
using Petal.Recipes.Cloud;

namespace Petal.App.Tools.Parts;

/// <summary>
/// Shown in an AI tool's window while its model isn't on this PC yet: what it is, its size and
/// licence, and a Download button with progress. Hidden once the model is here. A tool that needs
/// several files (a program and a voice, say) gets them all with one download. For a job a cloud model
/// can do, the user's own API key is offered beside the download from the start, as the other way to run it.
/// </summary>
public sealed class ModelPanel : Border
{
    private readonly TextBlock _here = Heading("On this PC");
    private readonly TextBlock _text = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _status = new() { Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap };
    private readonly Button _download = new();
    private readonly Border _track = new() { Height = 4, CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 10, 0, 0), Visibility = Visibility.Collapsed };
    private readonly Border _fill = new() { Height = 4, CornerRadius = new CornerRadius(2), HorizontalAlignment = HorizontalAlignment.Left, Width = 0 };
    private readonly StackPanel _byKey = new();
    private readonly Button _ownKey = new();
    private readonly StackPanel _keyForm = new() { Margin = new Thickness(0, 10, 0, 0), Visibility = Visibility.Collapsed };
    private readonly ComboBox _provider = new() { MinWidth = 150 };
    private readonly PasswordBox _key = new() { FlowDirection = FlowDirection.LeftToRight };
    private readonly TextBlock _keyLabel = new() { Margin = new Thickness(0, 10, 0, 4), FontSize = 12 };
    private readonly Button _useKey = new();
    private readonly TextBlock _keyStatus = new() { Margin = new Thickness(0, 6, 0, 0), FontSize = 12, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
    private CancellationTokenSource? _cts;
    private IReadOnlyList<ModelInfo> _models;

    /// <summary>The job a cloud model can do in place of these models; null when none can.</summary>
    private AiJob? Job => AiSetup.JobOf(_models);

    private bool OnKey => Job is { } job && AiSetup.OnKey(job);

    /// <summary>Raised when the model has arrived (or was already here when switching to it), or a key has taken its place.</summary>
    public event Action? Ready;

    public ModelPanel(ModelInfo model) : this([model])
    {
    }

    public ModelPanel(IReadOnlyList<ModelInfo> models)
    {
        _models = models;
        CornerRadius = new CornerRadius(10);
        Padding = new Thickness(14, 12, 14, 12);
        Margin = new Thickness(0, 12, 0, 0);
        SetResourceReference(BackgroundProperty, "Petal.AccentSoft");
        _text.SetResourceReference(TextBlock.ForegroundProperty, "Petal.Text");
        _status.SetResourceReference(TextBlock.ForegroundProperty, "Petal.TextSecondary");
        _status.FontSize = 12;
        _keyLabel.SetResourceReference(TextBlock.ForegroundProperty, "Petal.TextSecondary");
        _keyStatus.SetResourceReference(TextBlock.ForegroundProperty, "Petal.TextSecondary");
        _track.SetResourceReference(Border.BackgroundProperty, "Petal.Control");
        _fill.SetResourceReference(Border.BackgroundProperty, "Petal.Accent");
        _track.Child = _fill;
        _download.Click += async (_, _) => await DownloadAsync();

        _ownKey.Content = L.T("Use My API Key");
        _ownKey.Click += (_, _) => ChooseKey();
        var line = new Border { Height = 1, Margin = new Thickness(0, 12, 0, 12) };
        line.SetResourceReference(Border.BackgroundProperty, "Petal.ControlBorder");
        var keyWords = new TextBlock { Text = L.T("Gemini, ChatGPT or Claude. Nothing to download. What this job works on goes to that company, and it bills your key."), TextWrapping = TextWrapping.Wrap };
        keyWords.SetResourceReference(TextBlock.ForegroundProperty, "Petal.Text");
        _byKey.Children.Add(line);
        _byKey.Children.Add(Heading("With your own API key"));
        _byKey.Children.Add(Beside(keyWords, _ownKey));
        _byKey.Children.Add(KeyForm());

        Child = new StackPanel { Children = { _here, Beside(_text, _download), _track, _status, _byKey } };

        void Changed()
        {
            if (Dispatcher.CheckAccess())
                KeyChanged();
            else
                Dispatcher.BeginInvoke(KeyChanged);
        }
        Loaded += (_, _) => AiSetup.Changed += Changed;
        Unloaded += (_, _) =>
        {
            AiSetup.Changed -= Changed;
            _cts?.Cancel();
        };
        Show();
    }

    private static TextBlock Heading(string words)
    {
        var heading = new TextBlock { Text = L.T(words), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) };
        heading.SetResourceReference(TextBlock.ForegroundProperty, "Petal.Text");
        return heading;
    }

    /// <summary>Words, with their button at the end of the line.</summary>
    private static Grid Beside(UIElement words, FrameworkElement button)
    {
        var grid = new Grid { ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } } };
        button.VerticalAlignment = VerticalAlignment.Center;
        button.Margin = new Thickness(12, 0, 0, 0);
        Grid.SetColumn(button, 1);
        grid.Children.Add(words);
        grid.Children.Add(button);
        return grid;
    }

    private StackPanel KeyForm()
    {
        foreach (var provider in Enum.GetValues<CloudProvider>())
            _provider.Items.Add(new ComboBoxItem { Content = CloudAi.Name(provider), Tag = provider });
        _provider.SelectionChanged += (_, _) => ShowProvider();
        var getKey = new Button { Content = L.T("Get a key"), Style = (Style)Application.Current.FindResource("Petal.SubtleButton"), Margin = new Thickness(8, 0, 0, 0) };
        getKey.Click += (_, _) => Process.Start(new ProcessStartInfo(CloudAi.KeysPage(Provider)) { UseShellExecute = true });
        _keyForm.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { _provider, getKey } });
        _keyLabel.Text = L.T("Your API key");
        _keyForm.Children.Add(_keyLabel);
        _useKey.Click += async (_, _) => await UseKeyAsync();
        _key.KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                await UseKeyAsync();
            }
        };
        _keyForm.Children.Add(Beside(_key, _useKey));
        _keyForm.Children.Add(_keyStatus);
        return _keyForm;
    }

    private CloudProvider Provider => _provider.SelectedItem is ComboBoxItem { Tag: CloudProvider provider } ? provider : CloudProvider.Gemini;

    /// <summary>Opens the place for a key: at a company whose key is already saved, when there is one.</summary>
    public void ChooseKey()
    {
        _ownKey.Visibility = Visibility.Collapsed;
        _keyForm.Visibility = Visibility.Visible;
        var saved = Enum.GetValues<CloudProvider>().Where(CloudKeys.Has).Cast<CloudProvider?>().FirstOrDefault() ?? CloudProvider.Gemini;
        _provider.SelectedItem = _provider.Items.OfType<ComboBoxItem>().First(i => (CloudProvider)i.Tag == saved);
        ShowProvider();
        if (_key.IsVisible)
            _key.Focus();
    }

    private void ShowProvider()
    {
        bool has = CloudKeys.Has(Provider);
        _key.Visibility = _keyLabel.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
        _useKey.Content = has ? L.F("Use {0}", CloudAi.Name(Provider)) : L.T("Save and Use");
        _useKey.HorizontalAlignment = HorizontalAlignment.Right;
        Say(has ? L.T("A key is saved, encrypted for this Windows user.") : "");
    }

    private void Say(string words)
    {
        _keyStatus.Text = words;
        _keyStatus.Visibility = words.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>The key is tried first: a key that doesn't work is not kept, and nothing goes anywhere on it.</summary>
    private async Task UseKeyAsync()
    {
        if (Job is not { } job || !_useKey.IsEnabled)
            return;
        var provider = Provider;
        bool had = CloudKeys.Has(provider);
        if (!had)
        {
            if (_key.Password.Trim().Length == 0)
            {
                _key.Focus();
                return;
            }
            CloudKeys.Set(provider, _key.Password);
        }
        _useKey.IsEnabled = _provider.IsEnabled = false;
        Say(L.T("Checking the key…"));
        try
        {
            await CloudAi.ModelsAsync(provider);
        }
        catch (CloudException ex)
        {
            if (!had)
                CloudKeys.Remove(provider);
            Say(L.T(ex.Message));
            return;
        }
        finally
        {
            _useKey.IsEnabled = _provider.IsEnabled = true;
        }
        _key.Clear();
        _cts?.Cancel();
        AiSetup.Use(job, provider);
        KeyChanged();
    }

    /// <summary>A key now does this panel's job, from here or from Settings: what was waiting for the model goes on.</summary>
    private void KeyChanged()
    {
        if (Visibility == Visibility.Visible && OnKey)
        {
            Visibility = Visibility.Collapsed;
            Ready?.Invoke();
        }
    }

    public bool IsReady => OnKey || _models.All(ModelStore.IsAvailable);

    /// <summary>Points the panel at another model (e.g. a different accuracy).</summary>
    public ModelInfo Model
    {
        get => _models[0];
        set => Models = [value];
    }

    /// <summary>Points the panel at another set of models (e.g. a different voice).</summary>
    public IReadOnlyList<ModelInfo> Models
    {
        get => _models;
        set
        {
            if (_cts is not null || value.SequenceEqual(_models))
                return;
            _models = value;
            Show();
            if (IsReady)
                Ready?.Invoke();
        }
    }

    private List<ModelInfo> Missing => _models.Where(m => !ModelStore.IsAvailable(m)).ToList();

    private void Show()
    {
        Visibility = IsReady ? Visibility.Collapsed : Visibility.Visible;
        _here.Visibility = _byKey.Visibility = Job is not null ? Visibility.Visible : Visibility.Collapsed;
        // Nothing needed: a key does the job, or the language is already the one wanted.
        if (_models.Count == 0)
            return;
        var missing = Missing;
        // The biggest file names the download; its companions (settings, a runtime) ride along.
        var main = missing.Count > 0 ? missing.MaxBy(m => m.Size)! : _models[0];
        var licences = string.Join(", ", missing.Select(m => m.License).Distinct());
        _text.Text = L.F("This tool uses {0} ({1} licence). It's a one-time {2} download, kept on this PC; your files never leave it.",
            L.T(main.Name), licences.Length > 0 ? licences : main.License, Sizes.Format(missing.Sum(m => m.Size)));
        _download.Content = L.T("Download");
        _download.IsEnabled = true;
        _track.Visibility = Visibility.Collapsed;
        _status.Text = "";
        // An empty line would leave a gap above the button.
        _status.Visibility = Visibility.Collapsed;
    }

    private async Task DownloadAsync()
    {
        if (_cts is not null)
        {
            _cts.Cancel();
            return;
        }
        _cts = new CancellationTokenSource();
        _download.Content = L.T("Cancel");
        _track.Visibility = Visibility.Visible;
        var missing = Missing;
        long total = Math.Max(1, missing.Sum(m => m.Size)), before = 0;
        var current = missing.Count > 0 ? missing[0] : _models[0];
        try
        {
            foreach (var model in missing)
            {
                current = model;
                long start = before;
                var progress = new Progress<double>(f =>
                {
                    double done = start + model.Size * f;
                    _fill.Width = Math.Max(0, _track.ActualWidth * done / total);
                    _status.Visibility = Visibility.Visible;
                    _status.Text = L.F("{0} of {1}", Sizes.Format((long)done), Sizes.Format(total));
                });
                await ModelStore.DownloadAsync(model, progress, _cts.Token);
                before += model.Size;
            }
            Visibility = Visibility.Collapsed;
            Ready?.Invoke();
        }
        catch (OperationCanceledException)
        {
            Show();
        }
        catch (Exception ex)
        {
            Log.Error(ex, $"Downloading {current.Name}");
            Show();
            _status.Visibility = Visibility.Visible;
            _status.Text = ex is System.Net.Http.HttpRequestException
                ? L.T("The download didn't work. Check the internet connection and try again.")
                : L.T(ex.Message);
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
        }
    }
}
