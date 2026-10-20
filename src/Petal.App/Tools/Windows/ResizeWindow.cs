using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Petal.App.Input;
using Petal.App.Jobs;
using Petal.App.Localization;
using Petal.Core.Jobs;
using Petal.Core.Tools;
using Petal.Engines.Imaging;
// Inside a Window, "ResizeMode" is the window's own property.
using SizeMode = Petal.Engines.Imaging.ResizeMode;

namespace Petal.App.Tools.Windows;

// ---------------------------------------------------------------------------------------------
// Resize & Rotate — one image with a live preview, or a selection with the same settings.
// ---------------------------------------------------------------------------------------------

public sealed class ResizeImageWindow : ToolWindow
{
    private readonly ToolInfo _tool;
    private readonly IReadOnlyList<string> _files;
    private readonly JobRunner _jobs;
    private readonly ScreenPoint _at;
    private readonly Segmented<SizeMode> _mode;
    private readonly LabeledSlider _percent;
    private readonly TextBox _width = new() { Width = 76, FlowDirection = FlowDirection.LeftToRight };
    private readonly TextBox _height = new() { Width = 76, FlowDirection = FlowDirection.LeftToRight };
    private readonly CheckBox _enlarge = new() { Content = L.T("Allow making images larger"), Margin = new Thickness(0, 10, 0, 0) };
    private readonly Grid _percentRow;
    private readonly Grid _sizeRow;
    private readonly TextBlock _sizeLabel;
    private readonly TextBlock _result = new();
    private readonly Image _preview = new() { FlowDirection = FlowDirection.LeftToRight, Stretch = Stretch.Uniform, Margin = new Thickness(12) };
    private readonly TextBlock _turn = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
    private (int Width, int Height)? _source;
    private int _rotation;
    private bool _flipH, _flipV;

    public ResizeImageWindow(ToolInfo tool, IReadOnlyList<string> files, JobRunner jobs, ScreenPoint at) : base(tool.Name, 450)
    {
        _tool = tool;
        _files = files;
        _jobs = jobs;
        _at = at;
        bool batch = files.Count > 1;

        if (batch)
        {
            Body.Children.Add(Text(L.P(files.Count, "{0} image", "{0} images"), "Petal.Section"));
            Body.Children.Add(Text("Every image keeps its proportions. Sizes are in pixels, after rotating.", "Petal.Caption"));
        }
        else
        {
            Body.Children.Add(PreviewPanel(_preview, 220));
            _result.Style = (Style)FindResource("Petal.Caption");
            _result.FlowDirection = FlowDirection.LeftToRight;
            _result.HorizontalAlignment = HorizontalAlignment.Center;
            _result.Margin = new Thickness(0, 6, 0, 0);
            Body.Children.Add(_result);
        }

        _mode = Segments([(SizeMode.None, "Keep"), (SizeMode.Percent, "Percent"), (SizeMode.Width, "Width"), (SizeMode.Height, "Height"), (SizeMode.Fit, "Fit")], SizeMode.Percent);
        _mode.Changed += _ => Sync();
        Body.Children.Add(Row("Resize", _mode, 90, 14));

        _percent = Slider(5, 200, 50, v => v.ToString("0", CultureInfo.CurrentCulture) + "%", 5);
        _percent.Changed += _ => Sync();
        _percentRow = Row("Scale", _percent, 90, 10);
        Body.Children.Add(_percentRow);

        var size = new StackPanel { Orientation = Orientation.Horizontal };
        size.Children.Add(_width);
        size.Children.Add(Text("  ×  ", "Petal.Label"));
        size.Children.Add(_height);
        size.Children.Add(Text("  px", "Petal.Label"));
        _sizeRow = Row("Size", size, 90, 10);
        _sizeLabel = (TextBlock)_sizeRow.Children[0];
        Body.Children.Add(_sizeRow);
        foreach (var box in new[] { _width, _height })
        {
            box.TextChanged += (_, _) => Sync();
            box.KeyDown += (_, e) => { if (e.Key == Key.Enter) PrimaryButton.Focus(); };
        }
        _enlarge.Checked += (_, _) => Sync();
        _enlarge.Unchecked += (_, _) => Sync();
        Body.Children.Add(_enlarge);

        var turns = new StackPanel { Orientation = Orientation.Horizontal };
        var left = IconButton("rotateCcw", "Rotate left", 16);
        left.Click += (_, _) => { _rotation = (_rotation + 270) % 360; Sync(); };
        var right = IconButton("rotateCw", "Rotate right", 16);
        right.Click += (_, _) => { _rotation = (_rotation + 90) % 360; Sync(); };
        var flipH = IconButton("flipH", "Flip horizontally", 16);
        flipH.Click += (_, _) => { _flipH = !_flipH; Sync(); };
        var flipV = IconButton("flipV", "Flip vertically", 16);
        flipV.Click += (_, _) => { _flipV = !_flipV; Sync(); };
        foreach (var b in new[] { left, right, flipH, flipV })
        {
            b.Margin = new Thickness(0, 0, 6, 0);
            turns.Children.Add(b);
        }
        _turn.Style = (Style)FindResource("Petal.Caption");
        turns.Children.Add(_turn);
        Body.Children.Add(Row("Turn", turns, 90, 14));

        ResetButton.Visibility = Visibility.Visible;
        ResetButton.Click += (_, _) =>
        {
            _rotation = 0;
            _flipH = _flipV = false;
            _mode.Value = SizeMode.Percent;
            _percent.Value = 50;
            _width.Text = _height.Text = "";
            _enlarge.IsChecked = false;
            Sync();
        };
        PrimaryButton.Content = L.T("Save");
        PrimaryButton.Click += (_, _) => Apply();
        FooterNote = batch ? L.P(files.Count, "{0} file", "{0} files") : Path.GetFileName(OutputNaming.ForTool(files[0], tool));

        if (!batch)
        {
            Loaded += async (_, _) =>
            {
                try
                {
                    var info = await ImageInspector.ReadAsync(files[0]);
                    _source = (info.DisplayWidth, info.DisplayHeight);
                    _preview.Source = await Parts.ImageLoader.LoadAsync(files[0], 900);
                    Sync();
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Opening image to resize");
                    FooterNote = "Couldn't open this image.";
                }
            };
        }
        Sync();
    }

    private ImageTransform Transform()
    {
        int.TryParse(_width.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out var w);
        int.TryParse(_height.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out var h);
        return new ImageTransform
        {
            Mode = _mode.Value,
            Percent = _percent.Value,
            Width = Math.Max(0, w),
            Height = Math.Max(0, h),
            Enlarge = _enlarge.IsChecked == true || (_mode.Value == SizeMode.Percent && _percent.Value > 100),
            Rotation = _rotation,
            FlipHorizontal = _flipH,
            FlipVertical = _flipV,
        };
    }

    private void Sync()
    {
        var mode = _mode.Value;
        _percentRow.Visibility = mode == SizeMode.Percent ? Visibility.Visible : Visibility.Collapsed;
        _sizeRow.Visibility = mode is SizeMode.Width or SizeMode.Height or SizeMode.Fit ? Visibility.Visible : Visibility.Collapsed;
        _width.Visibility = mode is SizeMode.Width or SizeMode.Fit ? Visibility.Visible : Visibility.Collapsed;
        _height.Visibility = mode is SizeMode.Height or SizeMode.Fit ? Visibility.Visible : Visibility.Collapsed;
        ((UIElement)((StackPanel)_width.Parent).Children[1]).Visibility = mode == SizeMode.Fit ? Visibility.Visible : Visibility.Collapsed;
        _sizeLabel.Text = L.T(mode switch { SizeMode.Width => "Width", SizeMode.Height => "Height", _ => "At most" });
        _enlarge.Visibility = mode is SizeMode.Width or SizeMode.Height or SizeMode.Fit ? Visibility.Visible : Visibility.Collapsed;

        _turn.Text = (_rotation == 0 && !_flipH && !_flipV) ? "" : string.Join(" · ", new[]
        {
            _rotation != 0 ? _rotation.ToString(CultureInfo.CurrentCulture) + "°" : null,
            _flipH ? L.T("flipped") : null,
            _flipV ? L.T("upside down") : null,
        }.Where(s => s is not null));

        // The preview turns and mirrors like the result will.
        var transform = new TransformGroup();
        transform.Children.Add(new ScaleTransform(_flipH ? -1 : 1, _flipV ? -1 : 1));
        transform.Children.Add(new RotateTransform(_rotation));
        _preview.LayoutTransform = transform;

        var t = Transform();
        bool ready = mode switch
        {
            SizeMode.Width => t.Width > 0,
            SizeMode.Height => t.Height > 0,
            SizeMode.Fit => t.Width > 0 && t.Height > 0,
            SizeMode.None => _rotation != 0 || _flipH || _flipV,
            _ => true,
        };
        PrimaryButton.IsEnabled = ready;
        if (_source is { } s)
        {
            var (tw, th) = ImageTransformer.TargetSize(s.Width, s.Height, t);
            _result.Text = $"{s.Width} × {s.Height}  →  {tw} × {th} px";
        }
    }

    private void Apply()
    {
        var transform = Transform();
        Close();
        var work = _files.Select(file => new WorkItem(file, Path.GetFileName(file), async (p, ct) =>
        {
            var output = OutputNaming.ForTool(file, _tool);
            await ImageTransformer.TransformAsync(file, output, transform, p, ct);
            return (IReadOnlyList<string>)[output];
        })).ToList();
        _ = _jobs.RunAsync("Resizing", "Resized", work, _at);
    }
}
