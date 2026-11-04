using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Petal.App.Input;
using Petal.App.Jobs;
using Petal.App.Localization;
using Petal.App.Settings;
using Petal.App.Tools.Parts;
using Petal.Core.Jobs;
using Petal.Core.Tools;
using Petal.Engines.Media;

namespace Petal.App.Tools.Windows;

/// <summary>
/// Hides faces and number plates in a video without drawing a single box: the video is looked through, what was found is
/// shown to keep any of it (the person it is about, say), and the rest is covered wherever it goes.
/// </summary>
public sealed class AutoBlurWindow : ToolWindow
{
    private readonly ToolInfo _tool;
    private readonly string _file;
    private readonly JobRunner _jobs;
    private readonly ScreenPoint _at;
    private readonly CheckBox _faces = new() { Content = L.T("Faces"), IsChecked = true };
    private readonly CheckBox _plates = new() { Content = L.T("Number plates"), IsChecked = true, Margin = new Thickness(16, 0, 0, 0) };
    private readonly ModelPanel _model = new(AutoBlur.ModelsFor(new AutoBlurOptions()));
    private readonly TextBlock _status = Text("", "Petal.Caption");
    private readonly WrapPanel _found = new();
    private readonly ScrollViewer _list = new() { MaxHeight = 320, Margin = new Thickness(0, 10, 0, 0), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Visibility = Visibility.Collapsed };
    private readonly CancellationTokenSource _closing = new();
    private IReadOnlyList<BlurTrack>? _tracks;
    private bool _busy;

    public AutoBlurWindow(ToolInfo tool, string file, JobRunner jobs, ScreenPoint at) : base(tool.Name, 520)
    {
        _tool = tool;
        _file = file;
        _jobs = jobs;
        _at = at;
        Body.Children.Add(Text(Path.GetFileName(file), "Petal.Section"));
        Body.Children.Add(Text("It runs on this PC: the file doesn't leave it.", "Petal.Caption").WithMarginT(6));
        Body.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0), Children = { _faces, _plates } });
        Body.Children.Add(_model);
        _status.Margin = new Thickness(0, 10, 0, 0);
        _status.TextWrapping = TextWrapping.Wrap;
        Body.Children.Add(_status);
        _list.Content = _found;
        Body.Children.Add(_list);
        Body.Children.Add(Text("A face turned away, very small or behind something can be missed, and plates from some countries are found less well: watch the result before sharing it.", "Petal.Caption").WithMarginT(10));
        _faces.Click += (_, _) => Choose();
        _plates.Click += (_, _) => Choose();
        _model.Ready += Sync;
        PrimaryButton.Content = L.T("Find");
        PrimaryButton.Click += async (_, _) =>
        {
            if (_tracks is null)
                await FindAsync();
            else
                Save();
        };
        Closed += (_, _) => _closing.Cancel();
        Choose();
    }

    private AutoBlurOptions Options => new(_faces.IsChecked == true, _plates.IsChecked == true);

    private void Choose()
    {
        _tracks = null;
        _found.Children.Clear();
        _list.Visibility = Visibility.Collapsed;
        _status.Text = "";
        PrimaryButton.Content = L.T("Find");
        if (Options is { Faces: true } or { Plates: true })
            _model.Models = AutoBlur.ModelsFor(Options);
        Sync();
    }

    private void Sync()
    {
        PrimaryButton.IsEnabled = !_busy && (Options.Faces || Options.Plates) && (_tracks is { Count: > 0 } || (_tracks is null && _model.IsReady));
        FooterNote = Path.GetFileName(OutputNaming.ForTool(_file, _tool));
    }

    private async Task FindAsync()
    {
        _busy = true;
        Sync();
        _status.Text = L.T("Looking through the video…");
        try
        {
            var progress = new Progress<double>(f => _status.Text = L.T("Looking through the video…") + " " + (int)(Math.Clamp(f, 0, 1) * 100) + "%");
            _tracks = await AutoBlur.FindAsync(_file, Options, progress, _closing.Token);
            Show(_tracks);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Error(ex, "Looking for faces and plates");
            _status.Text = L.T("That didn't work.") + " " + L.T(ex.Message);
        }
        finally
        {
            _busy = false;
            Sync();
        }
    }

    private void Show(IReadOnlyList<BlurTrack> tracks)
    {
        _found.Children.Clear();
        if (tracks.Count == 0)
        {
            _status.Text = L.T("No faces or number plates were found in this video.");
            return;
        }
        int faces = tracks.Count(t => t.Kind == BlurKind.Face), plates = tracks.Count - faces;
        _status.Text = L.F("Found {0} faces and {1} plates, each counted again if it left and came back. Untick any that should stay visible.", faces, plates);
        foreach (var track in tracks.OrderByDescending(t => t.End - t.Start).Take(120))
            _found.Children.Add(Tile(track));
        _list.Visibility = Visibility.Visible;
        PrimaryButton.Content = L.T("Save Blurred Copy");
    }

    /// <summary>What was found, with whether it is covered.</summary>
    private static UIElement Tile(BlurTrack track)
    {
        var panel = new StackPanel { Width = 92, Margin = new Thickness(0, 0, 8, 8) };
        if (track.Picture is { } bgr && track.PictureWidth > 0)
        {
            var picture = BitmapSource.Create(track.PictureWidth, track.PictureHeight, 96, 96, PixelFormats.Bgr24, null, bgr, track.PictureWidth * 3);
            picture.Freeze();
            panel.Children.Add(new Border { Height = 72, CornerRadius = new CornerRadius(8), ClipToBounds = true, Child = new Image { Source = picture, Stretch = Stretch.Uniform } }
                .WithDynamic(Border.BackgroundProperty, "Petal.Preview"));
        }
        var cover = new CheckBox { Content = Clock(track.Start), IsChecked = true, Margin = new Thickness(0, 4, 0, 0) };
        cover.Click += (_, _) => track.Keep = cover.IsChecked != true;
        panel.Children.Add(cover);
        return panel;
    }

    private static string Clock(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{(int)t.TotalMinutes}:{t.Seconds:00}";
    }

    private void Save()
    {
        if (_tracks is not { } tracks)
            return;
        var output = OutputNaming.ForTool(_file, _tool);
        var settings = SettingsStore.Current.Conversion;
        Close();
        _ = _jobs.RunAsync("Blurring faces and plates", "Blurred copy saved", [new WorkItem(_file, Path.GetFileName(_file), async (p, ct) =>
        {
            await AutoBlur.ApplyAsync(_file, output, tracks, settings, p, ct);
            return (IReadOnlyList<string>)[output];
        })], _at);
    }
}
