using System.Windows;
using System.Windows.Controls;
using Petal.App.Localization;
using Petal.App.Util;
using Petal.Core.Models;

namespace Petal.App.Tools.Parts;

/// <summary>
/// Shown in an AI tool's window while its model isn't on this PC yet: what it is, its size and
/// licence, and a Download button with progress. Hidden once the model is here.
/// </summary>
public sealed class ModelPanel : Border
{
    private readonly TextBlock _text = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _status = new() { Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap };
    private readonly Button _download = new() { HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 10, 0, 0) };
    private readonly Border _track = new() { Height = 4, CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 10, 0, 0), Visibility = Visibility.Collapsed };
    private readonly Border _fill = new() { Height = 4, CornerRadius = new CornerRadius(2), HorizontalAlignment = HorizontalAlignment.Left, Width = 0 };
    private CancellationTokenSource? _cts;
    private ModelInfo _model;

    /// <summary>Raised when the model has arrived (or was already here when switching to it).</summary>
    public event Action? Ready;

    public ModelPanel(ModelInfo model)
    {
        _model = model;
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

    public bool IsReady => ModelStore.IsAvailable(_model);

    /// <summary>Points the panel at another model (e.g. a different accuracy).</summary>
    public ModelInfo Model
    {
        get => _model;
        set
        {
            if (_cts is not null || value == _model)
                return;
            _model = value;
            Show();
            if (IsReady)
                Ready?.Invoke();
        }
    }

    private void Show()
    {
        Visibility = IsReady ? Visibility.Collapsed : Visibility.Visible;
        _text.Text = L.F("This tool uses {0} ({1} licence). It's a one-time {2} download, kept on this PC; your files never leave it.",
            _model.Name, _model.License, Sizes.Format(_model.Size));
        _download.Content = L.T("Download");
        _download.IsEnabled = true;
        _track.Visibility = Visibility.Collapsed;
        _status.Text = "";
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
        var progress = new Progress<double>(f =>
        {
            _fill.Width = Math.Max(0, (_track.ActualWidth - 0) * f);
            _status.Text = L.F("{0} of {1}", Sizes.Format((long)(_model.Size * f)), Sizes.Format(_model.Size));
        });
        try
        {
            await ModelStore.DownloadAsync(_model, progress, _cts.Token);
            Visibility = Visibility.Collapsed;
            Ready?.Invoke();
        }
        catch (OperationCanceledException)
        {
            Show();
        }
        catch (Exception ex)
        {
            Log.Error(ex, $"Downloading {_model.Name}");
            Show();
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
