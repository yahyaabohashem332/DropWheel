using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using Petal.App.Input;
using Petal.App.Jobs;
using Petal.App.Localization;
using Petal.App.Settings;
using Petal.App.Tools.Parts;
using Petal.Core.Formats;
using Petal.Core.Jobs;
using Petal.Core.Tools;
using Petal.Engines.Documents.Pdf;
using Petal.Engines.Imaging;
using Petal.Engines.Media;
using Petal.Recipes;

namespace Petal.App.Tools.Windows;

// ---------------------------------------------------------------------------------------------
// Add Watermark — text or a logo over photos, every frame of a video, or every PDF page.
// ---------------------------------------------------------------------------------------------

public sealed class WatermarkWindow : ToolWindow
{
    private readonly ToolInfo _tool;
    private readonly IReadOnlyList<string> _files;
    private readonly JobRunner _jobs;
    private readonly ScreenPoint _at;
    private readonly WatermarkSettings _saved = SettingsStore.Current.Watermark;
    private readonly Segmented<bool> _kind;
    private readonly TextBox _text = new();
    private readonly ColorWell _color;
    private readonly Button _choose = new();
    private readonly LabeledSlider _size;
    private readonly LabeledSlider _opacity;
    private readonly PlacePicker _place;
    private readonly CheckBox _tiled = new() { Content = L.T("Repeat across"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0) };
    private readonly Grid _textRow, _colorRow, _pictureRow;
    private readonly Image _background = new() { FlowDirection = FlowDirection.LeftToRight, Stretch = Stretch.Uniform };
    private readonly Image _overlay = new() { FlowDirection = FlowDirection.LeftToRight, Stretch = Stretch.Uniform, IsHitTestVisible = false };
    private readonly DispatcherTimer _redraw;
    private BitmapSource? _picture;
    private string? _picturePath;

    public WatermarkWindow(ToolInfo tool, IReadOnlyList<string> files, JobRunner jobs, ScreenPoint at) : base(tool.Name, 450)
    {
        _tool = tool;
        _files = files;
        _jobs = jobs;
        _at = at;

        var stage = new Grid { Margin = new Thickness(12) };
        stage.Children.Add(_background);
        stage.Children.Add(_overlay);
        Body.Children.Add(PreviewPanel(stage, 230));
        if (files.Count > 1)
            Body.Children.Add(Text(L.P(files.Count, "{0} file", "{0} files") + " · " + L.T("the first one is shown"), "Petal.Caption").WithMarginT(6));

        _kind = Segments([(false, "Text"), (true, "Picture")], _saved.UsePicture);
        _kind.Changed += _ => Sync();
        Body.Children.Add(Row("Watermark", _kind, 100, 14));

        _text.Text = _saved.Text.Length > 0 ? _saved.Text : $"© {DateTime.Now.Year}";
        _text.TextChanged += (_, _) => Sync();
        _textRow = Row("Text", _text, 100, 10);
        Body.Children.Add(_textRow);
        _color = new ColorWell(ColorWell.TryParse(_saved.Color, out var c) ? c : Colors.White);
        _color.Changed += _ => Sync();
        _colorRow = Row("Colour", _color, 100, 8);
        Body.Children.Add(_colorRow);

        _choose.HorizontalAlignment = HorizontalAlignment.Left;
        _choose.Click += (_, _) => ChoosePicture();
        _pictureRow = Row("Picture", _choose, 100, 10);
        Body.Children.Add(_pictureRow);

        _size = Slider(5, 90, Math.Clamp(_saved.Size * 100, 5, 90), v => v.ToString("0", CultureInfo.CurrentCulture) + "%", 1);
        _size.Changed += _ => Sync();
        Body.Children.Add(Row("Size", _size, 100, 12));
        _opacity = Slider(10, 100, Math.Clamp(_saved.Opacity * 100, 10, 100), v => v.ToString("0", CultureInfo.CurrentCulture) + "%", 5);
        _opacity.Changed += _ => Sync();
        Body.Children.Add(Row("Opacity", _opacity, 100, 6));

        _place = new PlacePicker(Enum.TryParse<Place9>(_saved.Place, out var place) ? place : Place9.BottomRight);
        _place.Changed += _ => Sync();
        _tiled.IsChecked = _saved.Tiled;
        _tiled.Checked += (_, _) => Sync();
        _tiled.Unchecked += (_, _) => Sync();
        var placement = new StackPanel { Orientation = Orientation.Horizontal, Children = { _place, _tiled } };
        Body.Children.Add(Row("Position", placement, 100, 12));

        _redraw = new DispatcherTimer(TimeSpan.FromMilliseconds(70), DispatcherPriority.Background, (_, _) => DrawPreview(), Dispatcher);
        PrimaryButton.Content = L.T("Add Watermark");
        PrimaryButton.Click += (_, _) => Apply();
        FooterNote = FileLabel(files.Count == 1 ? [OutputNaming.ForTool(files[0], tool)] : files);

        Loaded += async (_, _) =>
        {
            if (_saved.PicturePath is { } saved && File.Exists(saved))
                await LoadPictureAsync(saved);
            try
            {
                _background.Source = await PreviewSource.LoadAsync(files[0], 900);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Watermark preview");
            }
            Sync();
        };
        Sync();
    }

    private StampSpec Spec() => new()
    {
        Text = _kind.Value ? "" : _text.Text,
        Picture = _kind.Value ? _picture : null,
        Place = _place.Value,
        Size = _size.Value / 100,
        Opacity = _opacity.Value / 100,
        Color = _color.Color,
        Tiled = _tiled.IsChecked == true,
    };

    private void Sync()
    {
        bool picture = _kind.Value;
        _textRow.Visibility = _colorRow.Visibility = picture ? Visibility.Collapsed : Visibility.Visible;
        _pictureRow.Visibility = picture ? Visibility.Visible : Visibility.Collapsed;
        _choose.Content = _picturePath is null ? L.T("Choose picture…") : Path.GetFileName(_picturePath);
        _place.IsEnabled = _tiled.IsChecked != true;
        _place.Opacity = _place.IsEnabled ? 1 : 0.4;
        PrimaryButton.IsEnabled = !Spec().IsEmpty;
        _redraw.Stop();
        _redraw.Start();
    }

    private void DrawPreview()
    {
        _redraw.Stop();
        if (_background.Source is not BitmapSource bg)
            return;
        _overlay.Source = Spec().IsEmpty ? null : StampRenderer.Render(bg.PixelWidth, bg.PixelHeight, Spec());
    }

    private void ChoosePicture()
    {
        var dialog = new OpenFileDialog { Filter = L.T("Images") + "|*.png;*.jpg;*.jpeg;*.webp;*.bmp;*.gif;*.tif;*.tiff;*.heic" };
        if (dialog.ShowDialog(this) == true)
            _ = LoadPictureAsync(dialog.FileName);
    }

    private async Task LoadPictureAsync(string path)
    {
        try
        {
            _picture = await Parts.ImageLoader.LoadAsync(path, 2048);
            _picturePath = path;
            _kind.Value = true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Loading watermark picture");
            FooterNote = "Couldn't open this image.";
        }
        Sync();
    }

    private void Remember()
    {
        _saved.UsePicture = _kind.Value;
        _saved.Text = _text.Text;
        _saved.PicturePath = _picturePath;
        _saved.Color = ColorWell.Hex(_color.Color);
        _saved.Size = _size.Value / 100;
        _saved.Opacity = _opacity.Value / 100;
        _saved.Place = _place.Value.ToString();
        _saved.Tiled = _tiled.IsChecked == true;
        SettingsStore.Save();
    }

    private void Apply()
    {
        var spec = Spec();
        Remember();
        Close();
        var settings = SettingsStore.Current.Conversion;
        var work = _files.Select(file => new WorkItem(file, Path.GetFileName(file), async (p, ct) =>
        {
            var output = OutputNaming.ForTool(file, _tool);
            switch (Formats.FromPath(file)?.Family)
            {
                case FormatFamily.Video:
                    await VideoTools.WatermarkAsync(file, output, new WatermarkVideoOptions((w, h) => StampRenderer.RenderPng(w, h, spec)) { Settings = settings }, p, ct);
                    break;
                case FormatFamily.Pdf:
                    // Twice the page size in points: sharp on screen and in print.
                    await PdfStamper.StampAsync(file, output, f => StampRenderer.RenderPng((int)(f.Width * 2), (int)(f.Height * 2), spec), null, p, ct);
                    break;
                default:
                    await ImageOverlay.CompositeAsync(file, output, (w, h) => StampRenderer.RenderPng(w, h, spec, maxSide: 4096), p, ct);
                    break;
            }
            return (IReadOnlyList<string>)[output];
        })).ToList();
        _ = _jobs.RunAsync("Adding watermark", "Watermark added", work, _at);
    }
}
