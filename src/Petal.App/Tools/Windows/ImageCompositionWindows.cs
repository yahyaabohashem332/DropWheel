using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Petal.App.Input;
using Petal.App.Jobs;
using Petal.App.Tools.Parts;
using Petal.Core.Jobs;
using Petal.Core.Tools;
using Petal.App.Localization;

namespace Petal.App.Tools.Windows;

/// <summary>Shared plumbing for windows that edit one image.</summary>
public abstract class SingleImageWindow : ToolWindow
{
    protected SingleImageWindow(ToolInfo tool, string file, JobRunner jobs, ScreenPoint at, double width = 440) : base(tool.Name, width)
    {
        Tool = tool;
        File = file;
        Jobs = jobs;
        At = at;
        FooterNote = Path.GetFileName(OutputNaming.ForTool(file, tool));
    }

    protected ToolInfo Tool { get; }
    protected string File { get; }
    protected JobRunner Jobs { get; }
    protected ScreenPoint At { get; }

    /// <summary>Closes the window and saves the rendered image beside the original on a HUD card.</summary>
    protected void SaveRendered(Func<BitmapSource> render, string? extension = null)
    {
        BitmapSource image;
        try
        {
            image = render();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, L.T(Tool.Name), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        Close();
        var output = OutputNaming.ForTool(File, Tool);
        if (extension is not null)
            output = Path.ChangeExtension(output, extension);
        var quality = Petal.App.Settings.SettingsStore.Current.Conversion.ImageQuality;
        _ = Jobs.RunAsync(L.F("Saving {0}", L.Code == "en" ? Tool.Name.ToLowerInvariant() : L.T(Tool.Name)), Tool.Name, [new WorkItem(File, Path.GetFileName(File), (progress, ct) =>
        {
            progress.Report(0.3);
            ImageLoader.Save(image, output, quality);
            progress.Report(1);
            return Task.FromResult<IReadOnlyList<string>>([output]);
        })], At);
    }
}

// ---------------------------------------------------------------------------------------------
// Annotate Photo
// ---------------------------------------------------------------------------------------------

public sealed class AnnotateWindow : SingleImageWindow
{
    private readonly Canvas _overlay = new();
    private readonly AnnotateEditor _editor;
    private readonly List<(AnnotationTool Tool, Border Tile)> _tiles = [];
    private readonly Button _undo;
    private readonly Button _delete;

    private static readonly (AnnotationTool Tool, string Icon, string Tip)[] Tools =
    [
        (AnnotationTool.Arrow, "arrowUpRight", "Arrow"),
        (AnnotationTool.Highlighter, "highlighter", "Highlighter"),
        (AnnotationTool.Text, "type", "Text"),
        (AnnotationTool.Rectangle, "square", "Rectangle"),
        (AnnotationTool.Ellipse, "circle", "Ellipse"),
        (AnnotationTool.Pen, "pencil", "Draw"),
        (AnnotationTool.Number, "hash", "Numbered step"),
        (AnnotationTool.Magnifier, "zoomIn", "Magnifier"),
        (AnnotationTool.Image, "imagePlus", "Insert image"),
    ];

    public AnnotateWindow(ToolInfo tool, string file, JobRunner jobs, ScreenPoint at) : base(tool, file, jobs, at, 520)
    {
        _editor = new AnnotateEditor(_overlay);
        var stage = new Grid();
        stage.Children.Add(_editor);
        stage.Children.Add(_overlay);
        Body.Children.Add(PreviewPanel(stage, 360));

        var strip = new UniformGrid9();
        foreach (var (t, icon, tip) in Tools)
        {
            var tile = new Border
            {
                Height = 38,
                CornerRadius = new CornerRadius(9),
                Margin = new Thickness(2),
                Cursor = Cursors.Hand,
                ToolTip = L.T(tip),
                Child = ThemedIcon(icon, 18),
            };
            ((FrameworkElement)tile.Child).HorizontalAlignment = HorizontalAlignment.Center;
            ((FrameworkElement)tile.Child).VerticalAlignment = VerticalAlignment.Center;
            var selectedTool = t;
            tile.MouseLeftButtonUp += (_, _) => SelectTool(selectedTool);
            _tiles.Add((t, tile));
            strip.Add(tile);
        }
        strip.Margin = new Thickness(-2, 12, -2, 0);
        Body.Children.Add(strip);

        var row = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        var color = new ColorWell(_editor.Color);
        color.Changed += c => _editor.ApplyColor(c);
        DockPanel.SetDock(color, Dock.Left);
        row.Children.Add(color);
        var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        _undo = IconButton("undo", "Undo (Ctrl+Z)");
        _undo.Click += (_, _) => _editor.Undo();
        _delete = IconButton("trash", "Delete selected (Del)");
        _delete.Click += (_, _) => _editor.DeleteSelected();
        right.Children.Add(_undo);
        right.Children.Add(_delete);
        row.Children.Add(right);
        Body.Children.Add(row);

        Body.Children.Add(Text("Drag from the tail toward the arrowhead. Drag an annotation to move it; double-click text to edit.", "Petal.Caption").WithMarginT(10));

        ResetButton.Visibility = Visibility.Visible;
        ResetButton.Click += (_, _) => _editor.Clear();
        PrimaryButton.Content = L.T("Save Annotated Copy");
        PrimaryButton.Click += (_, _) => SaveRendered(_editor.RenderFull, "png");
        _editor.Changed += Sync;
        SelectTool(AnnotationTool.Arrow);
        Sync();
        Loaded += async (_, _) =>
        {
            try
            {
                _editor.Photo = await ImageLoader.LoadAsync(file);
                Sync();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Loading photo to annotate");
                FooterNote = "Couldn't open this image.";
            }
        };
    }

    private void SelectTool(AnnotationTool tool)
    {
        if (tool == AnnotationTool.Image)
        {
            var dialog = new OpenFileDialog { Filter = "Images|*.png;*.jpg;*.jpeg;*.webp;*.heic;*.bmp;*.gif;*.tif;*.tiff;*.avif;*.svg" };
            if (dialog.ShowDialog(this) != true)
                return;
            try
            {
                _editor.PendingPicture = ImageLoader.Load(dialog.FileName, 2400);
            }
            catch
            {
                return;
            }
        }
        _editor.Tool = tool;
        foreach (var (t, tile) in _tiles)
        {
            bool on = t == tool;
            if (on) tile.SetResourceReference(Border.BackgroundProperty, "Petal.Accent");
            else tile.SetResourceReference(Border.BackgroundProperty, "Petal.Control");
            if (tile.Child is System.Windows.Shapes.Path icon)
                icon.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, on ? "Petal.OnAccent" : "Petal.Text");
        }
        _editor.Focus();
    }

    private void Sync()
    {
        _undo.IsEnabled = _editor.CanUndo;
        _delete.IsEnabled = _editor.HasSelection;
        PrimaryButton.IsEnabled = _editor.Photo is not null && !_editor.IsEmpty;
    }

    private sealed class UniformGrid9 : Grid
    {
        public UniformGrid9()
        {
            for (int i = 0; i < 9; i++) ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        public void Add(UIElement e)
        {
            SetColumn(e, Children.Count);
            Children.Add(e);
        }
    }
}

// ---------------------------------------------------------------------------------------------
// Add Background
// ---------------------------------------------------------------------------------------------

public sealed class AddBackgroundWindow : SingleImageWindow
{
    private readonly Image _preview = new() { FlowDirection = FlowDirection.LeftToRight, Stretch = Stretch.Uniform };
    private readonly LabeledSlider _padding;
    private readonly LabeledSlider _corners;
    private readonly LabeledSlider _shadow;
    private readonly LabeledSlider _blur;
    private readonly Segmented<BackgroundRatio> _ratio;
    private readonly List<(BackgroundFill Fill, Border Swatch)> _swatches = [];
    private readonly Grid _blurRow;
    private BitmapSource? _photo;
    private BitmapSource? _small;
    private BackgroundFill _fill = BackgroundFill.Gradients[0];

    public AddBackgroundWindow(ToolInfo tool, string file, JobRunner jobs, ScreenPoint at) : base(tool, file, jobs, at, 460)
    {
        var previewHost = new Border { CornerRadius = new CornerRadius(12), Height = 280, Padding = new Thickness(10), Child = _preview };
        previewHost.SetResourceReference(Border.BackgroundProperty, "Petal.Control");
        Body.Children.Add(previewHost);

        Body.Children.Add(SectionTitle("Background").WithMarginT(14));
        var swatches = new StackPanel();
        swatches.Children.Add(SwatchRow(BackgroundFill.Gradients, addImage: true));
        swatches.Children.Add(SwatchRow(BackgroundFill.Solids.Select(BackgroundFill.Solid).ToList(), addColor: true));
        Body.Children.Add(new ScrollViewer { Content = swatches, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(0, 0, 0, 6) });

        _padding = Slider(0, 600, 120, v => $"{v:0} px", 2);
        _corners = Slider(0, 200, 24, v => $"{v:0} px", 1);
        _shadow = Slider(0, 120, 30, v => $"{v:0} px", 1);
        _blur = Slider(0, 80, 24, v => $"{v:0} px", 1);
        Body.Children.Add(Row("Padding", _padding, 90, 10));
        Body.Children.Add(Row("Corners", _corners, 90));
        Body.Children.Add(Row("Shadow", _shadow, 90));
        _blurRow = Row("Blur", _blur, 90);
        _blurRow.Visibility = Visibility.Collapsed;
        Body.Children.Add(_blurRow);
        _ratio = Segments([(BackgroundRatio.Auto, "Auto"), (BackgroundRatio.Square, "1:1"), (BackgroundRatio.Widescreen, "16:9"), (BackgroundRatio.Portrait, "4:5"), (BackgroundRatio.Story, "9:16")], BackgroundRatio.Auto);
        Body.Children.Add(Row("Ratio", _ratio, 90, 10));

        foreach (var s in new[] { _padding, _corners, _shadow, _blur }) s.Changed += _ => Refresh();
        _ratio.Changed += _ => Refresh();

        ResetButton.Visibility = Visibility.Visible;
        ResetButton.Click += (_, _) =>
        {
            _padding.Value = 120;
            _corners.Value = 24;
            _shadow.Value = 30;
            _ratio.Value = BackgroundRatio.Auto;
            Select(BackgroundFill.Gradients[0]);
        };
        PrimaryButton.Content = L.T("Save with Background");
        PrimaryButton.Click += (_, _) =>
        {
            if (_photo is null) return;
            var options = Options(1);
            SaveRendered(() => BackgroundComposer.Render(_photo, options));
        };

        Loaded += async (_, _) =>
        {
            try
            {
                _photo = await ImageLoader.LoadAsync(file);
                // A smaller copy keeps the live preview fast; settings scale with it.
                _small = await ImageLoader.LoadAsync(file, 720);
                double longest = Math.Max(_photo.PixelWidth, _photo.PixelHeight);
                _padding.Inner.Maximum = Math.Round(longest * 0.5);
                _padding.Value = Math.Round(longest * 0.08);
                _corners.Inner.Maximum = Math.Round(longest * 0.15);
                _corners.Value = Math.Round(longest * 0.02);
                _shadow.Inner.Maximum = Math.Round(longest * 0.1);
                _shadow.Value = Math.Round(longest * 0.025);
                Refresh();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Loading photo for background");
                FooterNote = "Couldn't open this image.";
            }
        };
        Select(_fill);
    }

    private BackgroundOptions Options(double scale) =>
        new(_fill, _padding.Value * scale, _corners.Value * scale, _shadow.Value * scale, _ratio.Value, _blur.Value * scale);

    private void Refresh()
    {
        if (_photo is null || _small is null) return;
        double scale = (double)_small.PixelWidth / _photo.PixelWidth;
        _preview.Source = BackgroundComposer.Render(_small, Options(scale));
        _blurRow.Visibility = _fill.IsImage ? Visibility.Visible : Visibility.Collapsed;
    }

    private UIElement SwatchRow(IReadOnlyList<BackgroundFill> fills, bool addImage = false, bool addColor = false)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        foreach (var fill in fills)
            row.Children.Add(Swatch(fill));
        if (addImage)
        {
            var add = IconButton("imagePlus", "Use an image as the background", 16, filled: true);
            add.Width = add.Height = 34;
            add.Click += (_, _) =>
            {
                var dialog = new OpenFileDialog { Filter = "Images|*.png;*.jpg;*.jpeg;*.webp;*.heic;*.bmp;*.tif;*.tiff;*.avif" };
                if (dialog.ShowDialog(this) == true)
                {
                    try
                    {
                        var fill = BackgroundFill.FromImage(ImageLoader.Load(dialog.FileName, 2400));
                        row.Children.Insert(row.Children.Count - 1, Swatch(fill));
                        Select(fill);
                    }
                    catch
                    {
                        // Unreadable image: ignore.
                    }
                }
            };
            row.Children.Add(add);
        }
        if (addColor)
        {
            var well = new ColorWell(Color.FromRgb(0x41, 0x6A, 0xC0)) { Margin = new Thickness(4, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            well.Changed += c => Select(BackgroundFill.Solid(c));
            row.Children.Add(well);
        }
        return row;
    }

    private Border Swatch(BackgroundFill fill)
    {
        var swatch = new Border
        {
            Width = 34,
            Height = 34,
            CornerRadius = new CornerRadius(9),
            Margin = new Thickness(0, 0, 6, 0),
            BorderThickness = new Thickness(2),
            Background = fill.Create(new Rect(0, 0, 34, 34)),
            Cursor = Cursors.Hand,
            ToolTip = L.T(fill.Name),
        };
        swatch.MouseLeftButtonUp += (_, _) => Select(fill);
        _swatches.Add((fill, swatch));
        return swatch;
    }

    private void Select(BackgroundFill fill)
    {
        _fill = fill;
        foreach (var (f, s) in _swatches)
        {
            if (f == fill) s.SetResourceReference(Border.BorderBrushProperty, "Petal.Accent");
            else s.SetResourceReference(Border.BorderBrushProperty, "Petal.ControlBorder");
        }
        Refresh();
    }
}

// ---------------------------------------------------------------------------------------------
// Create Collage
// ---------------------------------------------------------------------------------------------

public sealed class CollageWindow : ToolWindow
{
    private readonly ToolInfo _tool;
    private readonly JobRunner _jobs;
    private readonly ScreenPoint _at;
    private readonly Image _preview = new() { FlowDirection = FlowDirection.LeftToRight, Stretch = Stretch.Uniform };
    private readonly Segmented<CollageLayout> _layout;
    private readonly ComboBox _canvas;
    private readonly ComboBox _width;
    private readonly ComboBox _fit;
    private readonly LabeledSlider _columns;
    private readonly LabeledSlider _spacing;
    private readonly LabeledSlider _corners;
    private readonly ColorWell _background;
    private readonly OrderList _order;
    private readonly Dictionary<string, BitmapSource> _thumbs = [];
    private readonly Grid _columnsRow;

    public CollageWindow(ToolInfo tool, IReadOnlyList<string> files, JobRunner jobs, ScreenPoint at) : base(tool.Name, 440)
    {
        _tool = tool;
        _jobs = jobs;
        _at = at;
        var previewHost = new Border { CornerRadius = new CornerRadius(12), Height = 260, Padding = new Thickness(12), Child = _preview };
        previewHost.SetResourceReference(Border.BackgroundProperty, "Petal.Control");
        Body.Children.Add(previewHost);

        _layout = Segments([(CollageLayout.Grid, "Grid"), (CollageLayout.Row, "Row"), (CollageLayout.Column, "Column"), (CollageLayout.Featured, "Featured")], CollageLayout.Grid);
        Body.Children.Add(Row("Style", _layout, 100, 14));
        _columns = Slider(1, Math.Max(2, Math.Min(6, files.Count)), Math.Min(2, files.Count), v => $"{v:0}", 1);
        _columnsRow = Row("Columns", _columns, 100, 8);
        Body.Children.Add(_columnsRow);
        _canvas = Choice([(1.0, "Square · 1:1"), (4.0 / 5, "Portrait · 4:5"), (9.0 / 16, "Story · 9:16"), (16.0 / 9, "Landscape · 16:9"), (3.0 / 2, "Photo · 3:2")], 1.0);
        Body.Children.Add(Row("Canvas", _canvas, 100, 8));
        _width = Choice([(1080, "1,080 px"), (2048, "2,048 px"), (3000, "3,000 px"), (4096, "4,096 px")], 2048);
        Body.Children.Add(Row("Width", _width, 100, 8));
        _fit = Choice([(true, "Fill cells (crop edges)"), (false, "Fit whole images")], true);
        Body.Children.Add(Row("Image fit", _fit, 100, 8));

        var more = new Expander { Header = L.T("Spacing & background"), Margin = new Thickness(0, 10, 0, 0) };
        var morePanel = new StackPanel();
        _spacing = Slider(0, 120, 24, v => $"{v:0} px", 1);
        _corners = Slider(0, 120, 16, v => $"{v:0} px", 1);
        _background = new ColorWell(Colors.White);
        morePanel.Children.Add(Row("Spacing", _spacing, 100, 2));
        morePanel.Children.Add(Row("Corners", _corners, 100));
        morePanel.Children.Add(Row("Background", _background, 100, 8));
        more.Content = morePanel;
        Body.Children.Add(more);

        var orderExpander = new Expander { Header = L.T("Image order") };
        _order = new OrderList(files);
        orderExpander.Content = new ScrollViewer { Content = _order, MaxHeight = 220, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Body.Children.Add(orderExpander);

        foreach (var s in new[] { _columns, _spacing, _corners }) s.Changed += _ => Refresh();
        _layout.Changed += _ => Refresh();
        foreach (var c in new[] { _canvas, _width, _fit }) c.SelectionChanged += (_, _) => Refresh();
        _background.Changed += _ => Refresh();
        _order.Changed += Refresh;

        FooterNote = L.P(files.Count, "{0} image", "{0} images");
        PrimaryButton.Content = L.T("Create Collage");
        PrimaryButton.Click += (_, _) => Save();
        Loaded += async (_, _) =>
        {
            foreach (var f in files)
            {
                try { _thumbs[f] = await ImageLoader.LoadAsync(f, 700); }
                catch (Exception ex) { Log.Error(ex, $"Loading {f} for collage"); }
            }
            Refresh();
        };
    }

    private CollageOptions Options(double scale = 1) => new(
        _layout.Value,
        (int)_columns.Value,
        ValueOf<double>(_canvas),
        (int)(ValueOf<int>(_width) * scale),
        ValueOf<bool>(_fit),
        _spacing.Value * scale,
        _corners.Value * scale,
        _background.Color);

    private void Refresh()
    {
        _columnsRow.Visibility = _layout.Value == CollageLayout.Grid ? Visibility.Visible : Visibility.Collapsed;
        var images = _order.Items.Where(_thumbs.ContainsKey).Select(f => _thumbs[f]).ToList();
        if (images.Count == 0) return;
        double scale = 640.0 / ValueOf<int>(_width);
        _preview.Source = CollageComposer.Render(images, Options(scale));
    }

    private void Save()
    {
        var order = _order.Items.ToList();
        var options = Options();
        Close();
        var output = OutputNaming.ForTool(order[0], _tool);
        _ = _jobs.RunAsync("Creating collage", "Collage created", [new WorkItem(order[0], L.P(order.Count, "{0} image", "{0} images"), async (progress, ct) =>
        {
            var full = new List<BitmapSource>();
            for (int i = 0; i < order.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                // Load each image at the size its cell needs, not larger.
                full.Add(await ImageLoader.LoadAsync(order[i], options.Width));
                progress.Report(0.8 * (i + 1) / order.Count);
            }
            var result = await Application.Current.Dispatcher.InvokeAsync(() => CollageComposer.Render(full, options));
            ImageLoader.Save(result, output);
            progress.Report(1);
            return (IReadOnlyList<string>)[output];
        })], _at);
    }
}

internal static class LayoutExtensions
{
    public static T WithMarginT<T>(this T element, double top) where T : FrameworkElement
    {
        element.Margin = new Thickness(element.Margin.Left, top, element.Margin.Right, element.Margin.Bottom);
        return element;
    }
}
