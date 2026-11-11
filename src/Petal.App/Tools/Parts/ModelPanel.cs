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
/// In an AI tool's window: what its model is, its size and licence, and a Download button with progress. A tool that
/// needs several files (a program and a voice, say) gets them all with one download. For a job a cloud model can do,
/// both ways stay here whichever is in use, the model on this PC and the user's own API key, to move between them at
/// any time. For any other job the panel is only there until the model has arrived.
/// </summary>
public sealed class ModelPanel : Border
{
    private readonly StackPanel _hereHeading = new() { Orientation = Orientation.Horizontal };
    private readonly TextBlock _hereMark = InUse();
    private readonly TextBlock _text = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _status = new() { Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap };
    private readonly Button _download = new();
    private readonly Border _track = new() { Height = 4, CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 10, 0, 0), Visibility = Visibility.Collapsed };
    private readonly Border _fill = new() { Height = 4, CornerRadius = new CornerRadius(2), HorizontalAlignment = HorizontalAlignment.Left, Width = 0 };
    private readonly StackPanel _byKey = new();
    private readonly TextBlock _keyMark = InUse();
    private readonly TextBlock _keyWords = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _ownKey = new();
    private readonly StackPanel _keyForm = new() { Margin = new Thickness(0, 10, 0, 0), Visibility = Visibility.Collapsed };
    private readonly ComboBox _provider = new() { MinWidth = 150 };
    private readonly PasswordBox _key = new() { FlowDirection = FlowDirection.LeftToRight };
    private readonly TextBlock _keyLabel = new() { Margin = new Thickness(0, 10, 0, 4), FontSize = 12 };
    private readonly Button _useKey = new();
    private readonly TextBlock _keyStatus = new() { Margin = new Thickness(0, 6, 0, 0), FontSize = 12, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
    private readonly Grid _folderRow = new() { Margin = new Thickness(0, 8, 0, 0) };
    private readonly TextBlock _folder = new() { FontSize = 12, FlowDirection = FlowDirection.LeftToRight, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _changeFolder = new();
    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _moving;
    private IReadOnlyList<ModelInfo> _models;
    private bool _wanted = true;
    private bool _ready;
    private CloudChoice? _runner;
    private bool _choosing;

    /// <summary>The job a cloud model can do in place of these models; null when none can.</summary>
    private AiJob? Job => AiSetup.JobOf(_models);

    private CloudChoice? Runner => Job is { } job ? AiSetup.Runner(job) : null;

    /// <summary>
    /// Raised when the job can run and wasn't able to (the model has arrived, a key has taken its place), or when the
    /// user has moved it to the other way from this panel.
    /// </summary>
    public event Action? Ready;

    /// <summary>Raised when what does the job has changed, from here or from Settings, whichever way it went.</summary>
    public event Action? Switched;

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
        _keyWords.SetResourceReference(TextBlock.ForegroundProperty, "Petal.Text");
        _status.SetResourceReference(TextBlock.ForegroundProperty, "Petal.TextSecondary");
        _status.FontSize = 12;
        _keyLabel.SetResourceReference(TextBlock.ForegroundProperty, "Petal.TextSecondary");
        _keyStatus.SetResourceReference(TextBlock.ForegroundProperty, "Petal.TextSecondary");
        _track.SetResourceReference(Border.BackgroundProperty, "Petal.Control");
        _fill.SetResourceReference(Border.BackgroundProperty, "Petal.Accent");
        _track.Child = _fill;
        _download.Click += async (_, _) => await HereAsync();

        _ownKey.Click += (_, _) => ChooseKey();
        var line = new Border { Height = 1, Margin = new Thickness(0, 12, 0, 12) };
        line.SetResourceReference(Border.BackgroundProperty, "Petal.ControlBorder");
        _hereHeading.Children.Add(Heading("On this PC"));
        _hereHeading.Children.Add(_hereMark);
        _byKey.Children.Add(line);
        var keyHeading = new StackPanel { Orientation = Orientation.Horizontal, Children = { Heading("With your own API key"), _keyMark } };
        _byKey.Children.Add(Beside(new StackPanel { Children = { keyHeading, _keyWords } }, _ownKey));
        _byKey.Children.Add(KeyForm());

        Child = new StackPanel { Children = { Beside(new StackPanel { Children = { _hereHeading, _text } }, _download), FolderRow(), _track, _status, _byKey } };

        void Changed()
        {
            if (Dispatcher.CheckAccess())
                RunnerChanged();
            else
                Dispatcher.BeginInvoke(RunnerChanged);
        }
        // The models moved, from this panel or another, or from Settings.
        void Moved() => Dispatcher.BeginInvoke(Show);
        Loaded += (_, _) =>
        {
            AiSetup.Changed += Changed;
            ModelStore.FolderChanged += Moved;
            RunnerChanged();
        };
        Unloaded += (_, _) =>
        {
            AiSetup.Changed -= Changed;
            ModelStore.FolderChanged -= Moved;
            _cts?.Cancel();
            _moving?.Cancel();
        };
        Show();
    }

    /// <summary>Where the download goes, with a button to choose another folder.</summary>
    private Grid FolderRow()
    {
        var label = new TextBlock { Text = L.T("Saved in"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
        label.SetResourceReference(TextBlock.ForegroundProperty, "Petal.TextSecondary");
        _folder.SetResourceReference(TextBlock.ForegroundProperty, "Petal.Text");
        _changeFolder.Content = L.T("Change Folder…");
        _changeFolder.Style = (Style)Application.Current.FindResource("Petal.SubtleButton");
        _changeFolder.Margin = new Thickness(8, 0, 0, 0);
        _changeFolder.VerticalAlignment = VerticalAlignment.Center;
        _changeFolder.Click += async (_, _) => await ChangeFolderAsync();
        _folderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _folderRow.ColumnDefinitions.Add(new ColumnDefinition());
        _folderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_folder, 1);
        Grid.SetColumn(_changeFolder, 2);
        _folderRow.Children.Add(label);
        _folderRow.Children.Add(_folder);
        _folderRow.Children.Add(_changeFolder);
        return _folderRow;
    }

    /// <summary>Another folder for the models: those already downloaded move there, with their progress shown here.</summary>
    private async Task ChangeFolderAsync()
    {
        if (_moving is not null)
        {
            _moving.Cancel();
            return;
        }
        if (_cts is not null || ModelFolderChooser.Ask(Window.GetWindow(this)) is not { } folder)
            return;
        if (ModelFolderChooser.Problem(folder) is { } problem)
        {
            Tell(problem);
            return;
        }
        _moving = new CancellationTokenSource();
        _changeFolder.Content = L.T("Cancel");
        _download.IsEnabled = false;
        var total = ModelStore.SizeOf(ModelStore.Folder);
        var progress = new Progress<double>(f =>
        {
            if (total <= 0)
                return;
            _track.Visibility = Visibility.Visible;
            _fill.Width = Math.Max(0, _track.ActualWidth * f);
            Tell(L.F("Moving the models: {0} of {1}", Sizes.Format((long)(total * f)), Sizes.Format(total)));
        });
        string? failure = null;
        try
        {
            await ModelFolderChooser.MoveAsync(folder, progress, _moving.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Error(ex, $"Moving the models to {folder}");
            failure = ModelFolderChooser.Failure(ex);
        }
        finally
        {
            _moving.Dispose();
            _moving = null;
        }
        Show();
        if (failure is not null)
            Tell(failure);
    }

    private void Tell(string words)
    {
        _status.Visibility = Visibility.Visible;
        _status.Text = words;
    }

    private static TextBlock Heading(string words)
    {
        var heading = new TextBlock { Text = L.T(words), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) };
        heading.SetResourceReference(TextBlock.ForegroundProperty, "Petal.Text");
        return heading;
    }

    private static TextBlock InUse()
    {
        var mark = new TextBlock { Text = "✓ " + L.T("In use"), FontWeight = FontWeights.SemiBold, Margin = new Thickness(8, 0, 0, 4), Visibility = Visibility.Collapsed };
        mark.SetResourceReference(TextBlock.ForegroundProperty, "Petal.Accent");
        return mark;
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

    /// <summary>Opens the place for a key: at the company in use, or else one whose key is already saved.</summary>
    public void ChooseKey()
    {
        _keyForm.Visibility = Visibility.Visible;
        var first = Runner?.Provider ?? Enum.GetValues<CloudProvider>().Where(CloudKeys.Has).Cast<CloudProvider?>().FirstOrDefault() ?? CloudProvider.Gemini;
        _provider.SelectedItem = _provider.Items.OfType<ComboBoxItem>().First(i => (CloudProvider)i.Tag == first);
        ShowProvider();
        Show();
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
        Say("");
        _keyForm.Visibility = Visibility.Collapsed;
        Choose(job, provider);
    }

    /// <summary>The user moves the job, from this panel.</summary>
    private void Choose(AiJob job, CloudProvider? provider)
    {
        _choosing = true;
        AiSetup.Use(job, provider);
        RunnerChanged();
        _choosing = false;
    }

    /// <summary>The model on this PC: downloaded when it isn't here, and in use from then on.</summary>
    private async Task HereAsync()
    {
        if (_cts is null && Missing.Count == 0)
        {
            if (Job is { } job)
                Choose(job, null);
            return;
        }
        await DownloadAsync();
    }

    private void RunnerChanged()
    {
        var runner = Runner;
        if (runner == _runner)
            return;
        bool was = _ready;
        Show();
        Switched?.Invoke();
        if (_wanted && _ready && (!was || _choosing))
            Ready?.Invoke();
    }

    public bool IsReady => Runner is not null || _models.All(ModelStore.IsAvailable);

    /// <summary>Whether this window needs the panel now. Off, it stays out of sight whatever is here.</summary>
    public bool Wanted
    {
        get => _wanted;
        set
        {
            _wanted = value;
            Show();
        }
    }

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
            if (PointAt(value) && IsReady)
                Ready?.Invoke();
        }
    }

    /// <summary>Points the panel at another set of models, for a window that goes on by itself: Ready isn't raised.</summary>
    public bool PointAt(IReadOnlyList<ModelInfo> models)
    {
        if (_cts is not null || models.SequenceEqual(_models))
            return false;
        _models = models;
        Show();
        return true;
    }

    private List<ModelInfo> Missing => _models.Where(m => !ModelStore.IsAvailable(m)).ToList();

    private void Show()
    {
        var job = Job;
        _runner = Runner;
        _ready = IsReady;
        // A job with two ways keeps showing both; any other has nothing to say once its model is here.
        Visibility = _wanted && _models.Count > 0 && (job is not null || !_ready) ? Visibility.Visible : Visibility.Collapsed;
        _hereHeading.Visibility = _byKey.Visibility = job is not null ? Visibility.Visible : Visibility.Collapsed;
        if (_models.Count == 0)
            return;
        var missing = Missing;
        bool here = missing.Count == 0;
        // The biggest file names the download; its companions (settings, a runtime) ride along.
        var main = !here ? missing.MaxBy(m => m.Size)! : _models.MaxBy(m => m.Size)!;
        var licences = string.Join(", ", missing.Select(m => m.License).Distinct());
        _text.Text = here
            ? L.F("{0} is on this PC; your files never leave it.", L.T(main.Name))
            : L.F("This tool uses {0} ({1} licence). It's a one-time {2} download, kept on this PC; your files never leave it.",
                L.T(main.Name), licences.Length > 0 ? licences : main.License, Sizes.Format(missing.Sum(m => m.Size)));
        _hereMark.Visibility = here && _runner is null ? Visibility.Visible : Visibility.Collapsed;
        _keyMark.Visibility = _runner is not null ? Visibility.Visible : Visibility.Collapsed;
        _keyWords.Text = _runner is { } runner
            ? L.F("{0} does this job. What it works on goes to that company, and it bills your key.", CloudAi.Name(runner.Provider))
            : L.T("Gemini, ChatGPT or Claude. Nothing to download. What this job works on goes to that company, and it bills your key.");
        _keyWords.Visibility = _runner is null && here && _keyForm.Visibility != Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        _ownKey.Content = L.T(_runner is not null ? "Change" : "Use My API Key");
        _ownKey.Visibility = _keyForm.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        // Where a download goes, until it has arrived: there it can be changed.
        _folderRow.Visibility = here ? Visibility.Collapsed : Visibility.Visible;
        _folder.Text = ModelFolderChooser.Short(ModelStore.Folder);
        _folder.ToolTip = ModelStore.Folder;
        _changeFolder.IsEnabled = _cts is null;
        if (_cts is not null || _moving is not null)
            return;
        _changeFolder.Content = L.T("Change Folder…");
        _download.Content = L.T(here ? "Use This PC" : "Download");
        _download.IsEnabled = true;
        _download.Visibility = here && _runner is null ? Visibility.Collapsed : Visibility.Visible;
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
        if (_moving is not null)
            return;
        _cts = new CancellationTokenSource();
        _changeFolder.IsEnabled = false;
        _download.Content = L.T("Cancel");
        _track.Visibility = Visibility.Visible;
        var missing = Missing;
        long total = Math.Max(1, missing.Sum(m => m.Size)), before = 0;
        var current = missing.Count > 0 ? missing[0] : _models[0];
        bool arrived = false;
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
            arrived = true;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Error(ex, $"Downloading {current.Name}");
            _cts.Dispose();
            _cts = null;
            Show();
            _status.Visibility = Visibility.Visible;
            _status.Text = ex is System.Net.Http.HttpRequestException
                ? L.T("The download didn't work. Check the internet connection and try again.")
                : L.T(ex.Message);
            return;
        }
        _cts.Dispose();
        _cts = null;
        // Downloaded because it is wanted: from here on the job runs on it, a key or no key.
        if (arrived && Runner is not null && Job is { } job)
            Choose(job, null);
        else
        {
            Show();
            if (arrived)
                Ready?.Invoke();
        }
    }
}
