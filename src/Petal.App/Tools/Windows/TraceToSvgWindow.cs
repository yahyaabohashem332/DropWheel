using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ImageMagick;
using Petal.App.Input;
using Petal.App.Jobs;
using Petal.App.Localization;
using Petal.Core.Jobs;
using Petal.Core.Tools;
using Petal.Engines.Imaging;

namespace Petal.App.Tools.Windows;

/// <summary>A picture as an SVG: for logos, icons, signatures and flat drawings, which then stay sharp at any size.</summary>
public sealed class TraceToSvgWindow : ToolWindow
{
    private readonly ToolInfo _tool;
    private readonly IReadOnlyList<string> _files;
    private readonly JobRunner _jobs;
    private readonly ScreenPoint _at;
    private readonly Image _picture = new() { Stretch = Stretch.Uniform, FlowDirection = FlowDirection.LeftToRight, Margin = new Thickness(8) };
    private readonly Segmented<int> _colours;
    private readonly LabeledSlider _count;
    private readonly LabeledSlider _smooth;
    private readonly CheckBox _drop = new() { Content = L.T("Leave out the background"), Margin = new Thickness(0, 10, 0, 0) };
    private readonly TextBlock _status = Text("", "Petal.Caption");
    private readonly CancellationTokenSource _closing = new();
    private int _draw;

    public TraceToSvgWindow(ToolInfo tool, IReadOnlyList<string> files, JobRunner jobs, ScreenPoint at) : base(tool.Name, 500)
    {
        _tool = tool;
        _files = files;
        _jobs = jobs;
        _at = at;
        _colours = Segments([(0, "Find them"), (1, "Choose")], 0);
        _count = Slider(2, 16, 4, v => ((int)v).ToString());
        _smooth = Slider(0.2, 2, 0.6, v => L.F("{0} px", v.ToString("0.0")), 0.1);
        if (files.Count == 1)
            Body.Children.Add(PreviewPanel(_picture, 300));
        else
            Body.Children.Add(Text(FileLabel(files), "Petal.Section"));
        Body.Children.Add(Row("Colours", _colours, 110, 12));
        Body.Children.Add(Row("How many", _count, 110, 8));
        Body.Children.Add(Row("Smoothness", _smooth, 110, 8));
        Body.Children.Add(_drop);
        _status.Margin = new Thickness(0, 10, 0, 0);
        _status.TextWrapping = TextWrapping.Wrap;
        Body.Children.Add(_status);
        Body.Children.Add(Text("Best for logos, icons and flat drawings. A photograph comes out as patches of colour.", "Petal.Caption").WithMarginT(6));
        _colours.Changed += _ => Draw();
        _count.Changed += _ => Draw();
        _smooth.Changed += _ => Draw();
        _drop.Click += (_, _) => Draw();
        PrimaryButton.Content = L.T("Save SVG");
        PrimaryButton.Click += (_, _) => Save();
        FooterNote = files.Count == 1 ? Path.GetFileName(OutputNaming.ForTool(files[0], tool)) : L.P(files.Count, "{0} file", "{0} files");
        Closed += (_, _) => _closing.Cancel();
        Loaded += (_, _) => Draw();
        _count.Visibility = Visibility.Collapsed;
    }

    private TraceOptions Options => new(_colours.Value == 0 ? 0 : (int)_count.Value, _smooth.Value, _drop.IsChecked == true);

    /// <summary>The result is shown as it will be, drawn from the SVG itself.</summary>
    private async void Draw()
    {
        ((FrameworkElement)_count.Parent).Visibility = _colours.Value == 0 ? Visibility.Collapsed : Visibility.Visible;
        _count.Visibility = Visibility.Visible;
        if (_files.Count != 1)
            return;
        int mine = ++_draw;
        _status.Text = L.T("Tracing…");
        var options = Options;
        try
        {
            await Task.Delay(120, _closing.Token);
            if (mine != _draw)
                return;
            var result = await ImageTracer.TraceFileAsync(_files[0], options, _closing.Token);
            var picture = await Task.Run(() => Show(result.Svg), _closing.Token);
            if (mine != _draw)
                return;
            _picture.Source = picture;
            _status.Text = L.F("{0} colours, {1} shapes, {2} KB.", result.Colours, result.Shapes, Math.Max(1, Encoding.UTF8.GetByteCount(result.Svg) / 1024));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Error(ex, "Tracing a picture");
            _status.Text = L.T("Couldn't open this image.");
        }
    }

    private static BitmapSource Show(string svg)
    {
        using var image = new MagickImage(Encoding.UTF8.GetBytes(svg), new MagickReadSettings { Format = MagickFormat.Svg, BackgroundColor = MagickColors.Transparent });
        image.Format = MagickFormat.Png32;
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = new MemoryStream(image.ToByteArray());
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private void Save()
    {
        var options = Options;
        Close();
        var work = _files.Select(file => new WorkItem(file, Path.GetFileName(file), async (p, ct) =>
        {
            var result = await ImageTracer.TraceFileAsync(file, options, ct);
            var output = OutputNaming.ForTool(file, _tool);
            await File.WriteAllTextAsync(output, result.Svg, new UTF8Encoding(false), ct);
            return (IReadOnlyList<string>)[output];
        })).ToList();
        _ = _jobs.RunAsync("Tracing", "SVG saved", work, _at);
    }
}
