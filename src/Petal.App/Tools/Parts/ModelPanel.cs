using System.Windows;
using System.Windows.Controls;
using Petal.App.Localization;
using Petal.App.Util;
using Petal.Core.Models;

namespace Petal.App.Tools.Parts;

/// <summary>
/// Shown in an AI tool's window while its model isn't on this PC yet: what it is, its size and
/// licence, and a Download button with progress. Hidden once the model is here. A tool that needs
/// several files (a program and a voice, say) gets them all with one download.
/// </summary>
public sealed class ModelPanel : Border
{
    private readonly TextBlock _text = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _status = new() { Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap };
    private readonly Button _download = new() { HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 10, 0, 0) };
    private readonly Border _track = new() { Height = 4, CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 10, 0, 0), Visibility = Visibility.Collapsed };
    private readonly Border _fill = new() { Height = 4, CornerRadius = new CornerRadius(2), HorizontalAlignment = HorizontalAlignment.Left, Width = 0 };
    private CancellationTokenSource? _cts;
    private IReadOnlyList<ModelInfo> _models;

    /// <summary>Raised when the model has arrived (or was already here when switching to it).</summary>
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
        _track.SetResourceReference(Border.BackgroundProperty, "Petal.Control");
        _fill.SetResourceReference(Border.BackgroundProperty, "Petal.Accent");
        _track.Child = _fill;
        _download.Click += async (_, _) => await DownloadAsync();
        Child = new StackPanel { Children = { _text, _track, _status, _download } };
        Unloaded += (_, _) => _cts?.Cancel();
        Show();
    }

    public bool IsReady => _models.All(ModelStore.IsAvailable);

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
