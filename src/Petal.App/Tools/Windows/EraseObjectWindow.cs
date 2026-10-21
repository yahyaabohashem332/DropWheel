using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using ImageMagick;
using Petal.App.Input;
using Petal.App.Jobs;
using Petal.App.Localization;
using Petal.App.Tools.Parts;
using Petal.Core.Jobs;
using Petal.Core.Tools;
using Petal.Engines.Imaging;

namespace Petal.App.Tools.Windows;

/// <summary>
/// Erase Object: brush over what shouldn't be in the photo (a passer-by, a bin, a cable) and LaMa
/// fills it in on this PC. Erase as often as needed, step back with Undo, then save a copy.
/// </summary>
public sealed class EraseObjectWindow : ToolWindow
{
    private const int MaxSteps = 6;
    private readonly ToolInfo _tool;
    private readonly string _file;
    private readonly JobRunner _jobs;
    private readonly ScreenPoint _at;
    private readonly ModelPanel _model = new(ObjectEraser.Models);
    private readonly Image _picture = new() { Stretch = Stretch.Uniform, FlowDirection = FlowDirection.LeftToRight };
    private readonly Canvas _ink = new() { Background = Brushes.Transparent, Cursor = Cursors.Cross, FlowDirection = FlowDirection.LeftToRight };
    private readonly Grid _stage = new();
    private readonly LabeledSlider _size;
    private readonly Button _erase = new() { Content = L.T("Erase") };
    private readonly Button _undo = new() { Content = L.T("Undo"), Margin = new Thickness(8, 0, 0, 0) };
    private readonly TextBlock _status;
    private readonly List<BrushStroke> _strokes = [];
    private readonly Stack<MagickImage> _history = new();
    private List<(double X, double Y)>? _current;
    private Polyline? _line;
    private MagickImage? _image;
    private bool _busy;

    public EraseObjectWindow(ToolInfo tool, string file, JobRunner jobs, ScreenPoint at) : base(tool.Name, 560)
    {
        _tool = tool;
        _file = file;
        _jobs = jobs;
        _at = at;
        _stage.Children.Add(_picture);
        _stage.Children.Add(_ink);
        Body.Children.Add(PreviewPanel(_stage, 360));
        Body.Children.Add(_model);

        _size = Slider(6, 80, 22, v => L.F("{0} px", (int)v));
        Body.Children.Add(Row("Brush", _size, 80, 12));
        var buttons = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Margin = new Thickness(80, 10, 0, 0), Children = { _erase, _undo } };
        Body.Children.Add(buttons);
        _status = Text("Brush over what should go, then Erase. Brush a little beyond its edges.", "Petal.Caption");
        _status.Margin = new Thickness(0, 10, 0, 0);
        _status.TextWrapping = TextWrapping.Wrap;
        Body.Children.Add(_status);

        _ink.MouseLeftButtonDown += (_, e) => Begin(e.GetPosition(_ink));
        _ink.MouseMove += (_, e) => { if (_current is not null && e.LeftButton == MouseButtonState.Pressed) Extend(e.GetPosition(_ink)); };
        _ink.MouseLeftButtonUp += (_, _) => End();
        _ink.LostMouseCapture += (_, _) => End();
        _erase.Click += async (_, _) => await EraseAsync();
        _undo.Click += (_, _) => Undo();
        _model.Ready += Sync;

        PrimaryButton.Content = L.T("Save Copy");
        PrimaryButton.Click += (_, _) => Save();
        FooterNote = System.IO.Path.GetFileName(OutputNaming.ForTool(file, tool));
        Closed += (_, _) =>
        {
            _image?.Dispose();
            foreach (var old in _history)
                old.Dispose();
        };
        Loaded += async (_, _) =>
        {
            try
            {
                _image = await Task.Run(() => ObjectEraser.Load(file));
                Show(_image);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Log.Error(ex, "Opening a photo to erase from");
                _status.Text = L.T("Couldn't open this image.");
            }
            Sync();
        };
        Sync();
    }

    /// <summary>The image shown, at up to 1400 pixels.</summary>
    private void Show(MagickImage image)
    {
        using var preview = (MagickImage)image.Clone();
        preview.Resize(new MagickGeometry(1400, 1400) { Greater = true });
        preview.Format = MagickFormat.Bmp;
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = new MemoryStream(preview.ToByteArray());
        bitmap.EndInit();
        bitmap.Freeze();
        _picture.Source = bitmap;
    }

    /// <summary>Where the picture is drawn inside the stage, and its scale from image pixels.</summary>
    private (double Left, double Top, double Scale) Placement()
    {
        if (_image is null || _stage.ActualWidth <= 0)
            return (0, 0, 1);
        double scale = Math.Min(_stage.ActualWidth / _image.Width, _stage.ActualHeight / _image.Height);
        return ((_stage.ActualWidth - _image.Width * scale) / 2, (_stage.ActualHeight - _image.Height * scale) / 2, scale);
    }

    private (double X, double Y) ToImage(Point p)
    {
        var (left, top, scale) = Placement();
        return ((p.X - left) / scale, (p.Y - top) / scale);
    }

    private void Begin(Point p)
    {
        if (_image is null || _busy)
            return;
        _ink.CaptureMouse();
        _current = [ToImage(p)];
        _line = new Polyline
        {
            StrokeThickness = _size.Value * 2,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            Opacity = 0.55,
            IsHitTestVisible = false,
        };
        _line.SetResourceReference(Shape.StrokeProperty, "Petal.Accent");
        _line.Points.Add(p);
        _line.Points.Add(p);
        _ink.Children.Add(_line);
    }

    private void Extend(Point p)
    {
        _current!.Add(ToImage(p));
        _line!.Points.Add(p);
    }

    private void End()
    {
        if (_current is null)
            return;
        var (_, _, scale) = Placement();
        _strokes.Add(new BrushStroke(_current, _size.Value / scale));
        _current = null;
        _line = null;
        if (_ink.IsMouseCaptured)
            _ink.ReleaseMouseCapture();
        Sync();
    }

    private async Task EraseAsync()
    {
        if (_image is null || _strokes.Count == 0 || !_model.IsReady)
            return;
        _busy = true;
        Sync();
        _status.Text = L.T("Erasing…");
        var image = _image;
        var strokes = _strokes.ToList();
        try
        {
            var erased = await Task.Run(() => ObjectEraser.Erase(image, ObjectEraser.Mask((int)image.Width, (int)image.Height, strokes)));
            _history.Push(image);
            // Only a few steps back are kept: each is a whole photo in memory.
            if (_history.Count > MaxSteps)
            {
                var kept = _history.Take(MaxSteps).ToList();
                foreach (var old in _history.Skip(MaxSteps))
                    old.Dispose();
                _history.Clear();
                foreach (var step in Enumerable.Reverse(kept))
                    _history.Push(step);
            }
            _image = erased;
            _strokes.Clear();
            _ink.Children.Clear();
            Show(erased);
            _status.Text = L.T("Erased. Brush more and erase again, or save a copy.");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Error(ex, "Erasing an object");
            _status.Text = L.T("That didn't work.") + " " + L.T(ex.Message);
        }
        finally
        {
            _busy = false;
            Sync();
        }
    }

    /// <summary>Takes back the brush strokes not erased yet, else the last erase.</summary>
    private void Undo()
    {
        if (_strokes.Count > 0)
        {
            _strokes.RemoveAt(_strokes.Count - 1);
            _ink.Children.RemoveAt(_ink.Children.Count - 1);
        }
        else if (_history.Count > 0)
        {
            _image?.Dispose();
            _image = _history.Pop();
            Show(_image);
        }
        Sync();
    }

    private void Sync()
    {
        _erase.IsEnabled = !_busy && _image is not null && _strokes.Count > 0 && _model.IsReady;
        _undo.IsEnabled = !_busy && (_strokes.Count > 0 || _history.Count > 0);
        PrimaryButton.IsEnabled = !_busy && _history.Count > 0;
        _ink.IsEnabled = !_busy;
    }

    private void Save()
    {
        if (_image is null)
            return;
        var image = (MagickImage)_image.Clone();
        var output = OutputNaming.ForTool(_file, _tool);
        Close();
        _ = _jobs.RunAsync("Saving", "Object erased", [new WorkItem(_file, System.IO.Path.GetFileName(_file), async (_, ct) =>
        {
            using (image)
                await ObjectEraser.SaveAsync(image, output, ct);
            return (IReadOnlyList<string>)[output];
        })], _at);
    }
}
