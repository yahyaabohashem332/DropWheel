using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Petal.App.Input;
using Petal.App.Jobs;
using Petal.App.Localization;
using Petal.App.Tools.Parts;
using Petal.Core.Jobs;
using Petal.Core.Models;
using Petal.Core.Tools;
using Petal.Engines.Imaging;
using Petal.Engines.Media;

namespace Petal.App.Tools.Windows;

/// <summary>
/// Vertical Video: a wide video made tall for Reels, Shorts and TikTok: following the subject, from the
/// middle, or the whole picture over a blurred copy. A frame shows the tall window.
/// </summary>
public sealed class ReframeWindow : MediaToolWindow
{
    private readonly Segmented<ReframeMode> _mode;
    private readonly Segmented<int> _size;
    private readonly ModelPanel _model = new(ModelCatalog.Background);
    private readonly TextBlock _how = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
    private readonly Grid _preview = new() { Height = 190, Margin = new Thickness(0, 0, 0, 12) };
    private readonly Image _frame = new() { Stretch = Stretch.Uniform };
    private readonly Canvas _overlay = new() { IsHitTestVisible = false };

    public ReframeWindow(ToolInfo tool, IReadOnlyList<string> files, JobRunner jobs, ScreenPoint at) : base(tool, files, jobs, at, 460)
    {
        _preview.Children.Add(new Border { Background = Brushes.Black, CornerRadius = new CornerRadius(8) });
        _preview.Children.Add(_frame);
        _preview.Children.Add(_overlay);
        Body.Children.Add(_preview);
        _mode = Segments([(ReframeMode.Follow, "Follow"), (ReframeMode.Centre, "Middle"), (ReframeMode.Blurred, "Whole picture")], ReframeMode.Follow);
        _size = Segments([(1080, "1080 × 1920"), (720, "720 × 1280")], 1080);
        Body.Children.Add(Row("Tall window", _mode, 110, 0));
        Body.Children.Add(Row("Size", _size, 110, 10));
        _how.Style = (Style)FindResource("Petal.Caption");
        Body.Children.Add(_how);
        Body.Children.Add(_model);
        _mode.Changed += _ => Sync();
        _model.Ready += Sync;
        SizeChanged += (_, _) => DrawWindow();
        _frame.SizeChanged += (_, _) => DrawWindow();
        PrimaryButton.Content = L.T("Make Vertical");
        PrimaryButton.Click += (_, _) => Apply();
        Sync();
    }

    protected override async Task OnMediaReadyAsync(MediaInfo info)
    {
        try
        {
            var png = await MediaPreview.ExtractFrameAsync(File, info.Duration / 2, 640);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = new MemoryStream(png);
            image.EndInit();
            image.Freeze();
            _frame.Source = image;
            if (info.Height >= info.Width)
                FooterNote = "This video is already tall";
        }
        catch (Exception ex)
        {
            Log.Info($"No frame for Vertical Video: {ex.Message}");
        }
    }

    private void Sync()
    {
        _how.Text = _mode.Value switch
        {
            ReframeMode.Follow when BackgroundRemover.IsReady => L.T("The tall window follows the person, found by the background model on this PC, and glides rather than jumps."),
            ReframeMode.Follow => L.T("The tall window follows what moves. With the background model it follows people more surely."),
            ReframeMode.Centre => L.T("The middle of the picture, from top to bottom."),
            _ => L.T("All of the picture, over a blurred, enlarged copy of itself."),
        };
        _model.Visibility = _mode.Value == ReframeMode.Follow && !BackgroundRemover.IsReady ? Visibility.Visible : Visibility.Collapsed;
        DrawWindow();
    }

    /// <summary>The tall window over the frame (in the middle; when following, it moves while the video plays).</summary>
    private void DrawWindow()
    {
        _overlay.Children.Clear();
        if (_frame.Source is not BitmapSource source || _frame.ActualWidth <= 0 || _mode.Value == ReframeMode.Blurred)
            return;
        double w = _frame.ActualWidth, h = _frame.ActualHeight;
        double left = (_preview.ActualWidth - w) / 2, top = (_preview.ActualHeight - h) / 2;
        double tall = Math.Min(w, h * 9 / 16);
        double x = left + (w - tall) / 2;
        var shade = new SolidColorBrush(Color.FromArgb(0x90, 0, 0, 0));
        _overlay.Children.Add(Box(left, top, x - left, h, shade));
        _overlay.Children.Add(Box(x + tall, top, left + w - x - tall, h, shade));
        var frame = new Rectangle { Width = tall, Height = h, Stroke = Brushes.White, StrokeThickness = 2, RadiusX = 3, RadiusY = 3 };
        Canvas.SetLeft(frame, x);
        Canvas.SetTop(frame, top);
        _overlay.Children.Add(frame);
    }

    private static Rectangle Box(double x, double y, double w, double h, Brush fill)
    {
        var r = new Rectangle { Width = Math.Max(0, w), Height = Math.Max(0, h), Fill = fill };
        Canvas.SetLeft(r, x);
        Canvas.SetTop(r, y);
        return r;
    }

    private void Apply()
    {
        int width = _size.Value;
        var options = new ReframeOptions(_mode.Value, width, width * 16 / 9) { Settings = Quality };
        Reframer.SubjectFinder? finder = options.Mode == ReframeMode.Follow && BackgroundRemover.IsReady ? BackgroundRemover.SubjectCentreAsync : null;
        RunEach("Making it vertical", "Made vertical", async (file, p, ct) =>
        {
            var output = OutputNaming.ForTool(file, Tool);
            await Reframer.ReframeAsync(file, output, options, finder, p, ct);
            return [output];
        });
    }
}
