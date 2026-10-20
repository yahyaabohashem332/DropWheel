using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Petal.App.Input;
using Petal.App.Jobs;
using Petal.Core.Jobs;
using Petal.Core.Tools;
using Petal.Engines.Imaging;

namespace Petal.App.Tools.Windows;

/// <summary>
/// Lightroom-style adjustments with a live preview. The preview is rendered off the UI thread from
/// a downscaled copy; saving applies the same maths to the full-resolution photo.
/// </summary>
public sealed class EditPhotoWindow : ToolWindow
{
    private readonly ToolInfo _tool;
    private readonly string _file;
    private readonly JobRunner _jobs;
    private readonly ScreenPoint _at;
    private readonly Image _preview = new() { Stretch = Stretch.Uniform };
    private readonly HistogramView _histogram = new() { Height = 46 };
    private readonly Dictionary<PhotoAdjustmentSlider, (Slider Slider, TextBlock Value)> _rows = [];
    private readonly Button _compare = new() { Content = "Hold to compare", Style = null };
    private PhotoEditSession? _session;
    private WriteableBitmap? _bitmap;
    private BitmapSource? _original;
    private PhotoAdjustments _adjustments = PhotoAdjustments.Neutral;
    private bool _rendering;
    private bool _dirty;
    private bool _exporting;

    public EditPhotoWindow(ToolInfo tool, string file, JobRunner jobs, ScreenPoint at) : base(tool.Name, 500)
    {
        _tool = tool;
        _file = file;
        _jobs = jobs;
        _at = at;

        var stage = new Grid();
        stage.Children.Add(_preview);
        _compare.Style = (Style)FindResource("Petal.SubtleButton");
        _compare.Foreground = Brushes.White;
        _compare.Background = new SolidColorBrush(Color.FromArgb(0x80, 0, 0, 0));
        _compare.HorizontalAlignment = HorizontalAlignment.Right;
        _compare.VerticalAlignment = VerticalAlignment.Top;
        _compare.Margin = new Thickness(0, 8, 8, 0);
        _compare.PreviewMouseLeftButtonDown += (_, _) => { if (_original is not null) _preview.Source = _original; };
        _compare.PreviewMouseLeftButtonUp += (_, _) => _preview.Source = _bitmap;
        _compare.MouseLeave += (_, _) => _preview.Source = _bitmap;
        stage.Children.Add(_compare);
        Body.Children.Add(PreviewPanel(stage, 300));
        _histogram.Margin = new Thickness(0, 8, 0, 4);
        Body.Children.Add(_histogram);

        var list = new StackPanel();
        string? section = null;
        foreach (var spec in PhotoAdjustments.Sliders)
        {
            if (spec.Section != section)
            {
                section = spec.Section;
                var title = SectionTitle(section);
                title.Margin = new Thickness(0, list.Children.Count == 0 ? 2 : 14, 0, 4);
                title.SetResourceReference(TextBlock.ForegroundProperty, "Petal.Accent");
                list.Children.Add(title);
            }
            list.Children.Add(SliderRow(spec));
        }
        Body.Children.Add(new ScrollViewer { Content = list, Height = 280, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(0, 0, 8, 0) });

        ResetButton.Visibility = Visibility.Visible;
        ResetButton.Click += (_, _) => SetAll(PhotoAdjustments.Neutral);
        PrimaryButton.Content = "Save Edited Copy";
        PrimaryButton.IsEnabled = false;
        PrimaryButton.Click += (_, _) => Save();
        FooterNote = Path.GetFileName(OutputNaming.ForTool(file, tool));

        Loaded += async (_, _) => await OpenAsync();
        Closed += (_, _) =>
        {
            if (!_exporting) _session?.Dispose();
        };
        PreviewKeyDown += (_, e) =>
        {
            // Backslash shows the original while held, as in photo editors.
            if (e.Key == Key.OemBackslash || e.Key == Key.Oem5) { if (_original is not null) _preview.Source = _original; e.Handled = true; }
        };
        PreviewKeyUp += (_, e) =>
        {
            if (e.Key == Key.OemBackslash || e.Key == Key.Oem5) _preview.Source = _bitmap;
        };
    }

    private UIElement SliderRow(PhotoAdjustmentSlider spec)
    {
        var grid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(112) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(66) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        grid.Children.Add(Text(spec.Label, "Petal.Label"));

        var slider = new Slider
        {
            Minimum = spec.Minimum,
            Maximum = spec.Maximum,
            SmallChange = spec.Step,
            LargeChange = spec.Step * 10,
            Value = spec.Get(_adjustments),
            VerticalAlignment = VerticalAlignment.Center,
        };
        bool bipolar = spec.Minimum < 0;
        slider.Loaded += (_, _) =>
        {
            // Bipolar sliders start in the middle; a fill from the left would mislead.
            if (bipolar && slider.Template.FindName("PART_Track", slider) is Track track && track.DecreaseRepeatButton is { } dec)
                dec.Background = Brushes.Transparent;
        };
        Grid.SetColumn(slider, 1);
        grid.Children.Add(slider);

        var value = Text(Format(spec, spec.Get(_adjustments)), "Petal.Value");
        value.HorizontalAlignment = HorizontalAlignment.Right;
        value.Margin = new Thickness(6, 0, 4, 0);
        Grid.SetColumn(value, 2);
        grid.Children.Add(value);

        var reset = IconButton("rotateCcw", $"Reset {spec.Label}", 13);
        reset.Width = reset.Height = 24;
        reset.Click += (_, _) => slider.Value = 0;
        Grid.SetColumn(reset, 3);
        grid.Children.Add(reset);

        slider.ValueChanged += (_, e) =>
        {
            var v = Math.Round(e.NewValue / spec.Step) * spec.Step;
            value.Text = Format(spec, v);
            _adjustments = spec.Set(_adjustments, v);
            reset.Opacity = Math.Abs(v) < 1e-9 ? 0.35 : 1;
            PrimaryButton.IsEnabled = _session is not null && !_adjustments.IsNeutral;
            RequestRender();
        };
        slider.MouseDoubleClick += (_, _) => slider.Value = 0;
        reset.Opacity = 0.35;
        _rows[spec] = (slider, value);
        return grid;
    }

    private static string Format(PhotoAdjustmentSlider spec, double v) => v.ToString(spec.Format, CultureInfo.CurrentCulture);

    private void SetAll(PhotoAdjustments adjustments)
    {
        foreach (var (spec, (slider, _)) in _rows)
            slider.Value = spec.Get(adjustments);
        _adjustments = adjustments;
        RequestRender();
    }

    private async Task OpenAsync()
    {
        try
        {
            _session = await PhotoEditSession.OpenAsync(_file);
            _bitmap = new WriteableBitmap(_session.PreviewWidth, _session.PreviewHeight, 96, 96, PixelFormats.Bgra32, null);
            var original = _session.OriginalPreview;
            _original = BitmapSource.Create(original.Width, original.Height, 96, 96, PixelFormats.Bgra32, null, original.Pixels, original.Stride);
            _original.Freeze();
            _preview.Source = _bitmap;
            RequestRender();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Opening photo to edit");
            FooterNote = "Couldn't open this photo.";
        }
    }

    /// <summary>Renders on a worker thread; while one render runs, later changes collapse into one more.</summary>
    private void RequestRender()
    {
        if (_session is null || _bitmap is null) return;
        _dirty = true;
        if (_rendering) return;
        _ = RenderLoopAsync();
    }

    private async Task RenderLoopAsync()
    {
        _rendering = true;
        var session = _session!;
        while (_dirty)
        {
            _dirty = false;
            var adjustments = _adjustments;
            var frame = await Task.Run(() => session.RenderPreview(adjustments));
            var histogram = PhotoEditSession.Histogram(frame);
            _bitmap!.WritePixels(new Int32Rect(0, 0, frame.Width, frame.Height), frame.Pixels, frame.Stride, 0);
            _histogram.Data = histogram;
        }
        _rendering = false;
    }

    private void Save()
    {
        if (_session is null) return;
        var session = _session;
        var adjustments = _adjustments;
        var output = OutputNaming.ForTool(_file, _tool);
        _exporting = true;
        Close();
        _ = _jobs.RunAsync("Saving edited photo", "Photo saved", [new WorkItem(_file, Path.GetFileName(_file), async (p, ct) =>
        {
            try
            {
                await session.ExportAsync(output, adjustments, p, ct);
            }
            finally
            {
                session.Dispose();
            }
            return (IReadOnlyList<string>)[output];
        })], _at);
    }
}

/// <summary>RGB + luminance histogram, drawn as soft overlapping areas.</summary>
public sealed class HistogramView : FrameworkElement
{
    private ImageHistogram? _data;

    public ImageHistogram? Data
    {
        get => _data;
        set
        {
            _data = value;
            InvalidateVisual();
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        var bg = (Brush)FindResource("Petal.Control");
        dc.DrawRoundedRectangle(bg, null, new Rect(RenderSize), 8, 8);
        if (_data is null || _data.Max <= 0 || ActualWidth <= 0) return;
        void Area(int[] bins, Color color)
        {
            var geometry = new StreamGeometry();
            using (var g = geometry.Open())
            {
                double w = ActualWidth - 8, h = ActualHeight - 8;
                g.BeginFigure(new Point(4, 4 + h), true, true);
                // Square-root scale keeps small counts visible next to a big peak.
                double max = Math.Sqrt(_data.Max);
                for (int i = 0; i < bins.Length; i++)
                    g.LineTo(new Point(4 + w * i / (bins.Length - 1), 4 + h - h * Math.Sqrt(bins[i]) / max), true, false);
                g.LineTo(new Point(4 + w, 4 + h), true, false);
            }
            geometry.Freeze();
            dc.DrawGeometry(new SolidColorBrush(color), null, geometry);
        }
        Area(_data.Red, Color.FromArgb(0x60, 0xFF, 0x45, 0x45));
        Area(_data.Green, Color.FromArgb(0x60, 0x3D, 0xD6, 0x5A));
        Area(_data.Blue, Color.FromArgb(0x60, 0x45, 0x80, 0xFF));
        Area(_data.Luminance, Color.FromArgb(0x50, 0xE8, 0xE8, 0xE8));
    }
}
