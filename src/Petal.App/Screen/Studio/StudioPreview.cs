using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using Petal.Core.Recording;
using SkiaSharp;

namespace Petal.App.Screen.Studio;

/// <summary>
/// The studio's live picture: the recording playing in its frame on the background, seen through the moving
/// view, with the pointer, the click rings, the shortcuts and the camera's bubble — worked out with the same plans
/// the finished video is drawn with, so what plays here is what will be saved (less the motion blur).
/// </summary>
internal sealed class StudioPreview : Grid
{
    private readonly Grid _stage = new() { ClipToBounds = true, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly Border _background = new();
    private readonly Border _shadow = new() { Background = Brushes.Black };
    private readonly Canvas _content = new() { ClipToBounds = true };
    private readonly Canvas _world = new();
    private readonly MatrixTransform _look = new();
    private readonly WorldLayer _layer;
    private readonly StageLayer _keys;
    private readonly Border _bubble = new() { BorderBrush = Brushes.White, Visibility = Visibility.Collapsed };
    private StudioProject? _project;
    private PointerArt? _art;
    private double _scale = 1;
    private RectD _content01;

    public MediaElement Player { get; } = new() { LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Close, ScrubbingEnabled = true, Stretch = Stretch.Fill };

    public MediaElement Face { get; } = new() { LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Close, ScrubbingEnabled = true, Stretch = Stretch.UniformToFill, IsMuted = true };

    public PointerPlan? Pointer { get; private set; }

    public CameraPlan? Camera { get; private set; }

    /// <summary>A point of the recording picked on the picture (for a zoom that looks at one place).</summary>
    public event Action<double, double>? Picked;

    public StudioPreview()
    {
        // The picture is the screen as it was: never mirrored, whatever the language.
        FlowDirection = FlowDirection.LeftToRight;
        _layer = new WorldLayer(this);
        _keys = new StageLayer(this);
        _world.RenderTransform = _look;
        _world.Children.Add(Player);
        _world.Children.Add(_layer);
        _content.Children.Add(_world);
        _stage.Children.Add(_background);
        _stage.Children.Add(_shadow);
        _stage.Children.Add(_content);
        _stage.Children.Add(_bubble);
        _stage.Children.Add(_keys);
        foreach (FrameworkElement e in new FrameworkElement[] { _background, _shadow, _content, _bubble, _keys })
        {
            e.HorizontalAlignment = HorizontalAlignment.Left;
            e.VerticalAlignment = VerticalAlignment.Top;
        }
        _background.HorizontalAlignment = HorizontalAlignment.Stretch;
        _background.VerticalAlignment = VerticalAlignment.Stretch;
        _keys.HorizontalAlignment = HorizontalAlignment.Stretch;
        _keys.VerticalAlignment = VerticalAlignment.Stretch;
        _bubble.Child = Face;
        Children.Add(_stage);
        SizeChanged += (_, _) => Layout();
        _content.MouseLeftButtonDown += (_, e) =>
        {
            if (_project is null || Camera is null)
                return;
            var p = e.GetPosition(_content);
            var view = Camera.View(Time);
            double s = _content01.Width * _scale / view.Width;
            Picked?.Invoke(view.X + p.X / s, view.Y + p.Y / s);
        };
    }

    /// <summary>The recording's moment shown.</summary>
    public double Time { get; private set; }

    public bool Picking
    {
        set => _content.Cursor = value ? Cursors.Cross : null;
    }

    /// <summary>Loads a project's video (once) and works out the plans for its edits.</summary>
    public void Load(StudioProject project, PointerArt art)
    {
        bool fresh = _project != project;
        _project = project;
        _art = art;
        if (fresh)
        {
            _world.Width = project.Width;
            _world.Height = project.Height;
            Player.Width = project.Width;
            Player.Height = project.Height;
            Player.Source = new Uri(project.PathOf(project.Preview ?? project.Video));
            if (project.Camera is { } camera && File.Exists(project.PathOf(camera)) && project.CameraPlaces.Count > 0)
            {
                Face.Source = new Uri(project.PathOf(camera));
                _bubble.Visibility = Visibility.Visible;
            }
        }
        Rebuild();
    }

    /// <summary>After an edit: the frame's shape and look, and the plans, again.</summary>
    public void Rebuild()
    {
        if (_project is null)
            return;
        var look = _project.Edit.Look;
        var (w, h) = StudioLayout.OutputSize(_project.Width, _project.Height, look, _project.Edit.Output);
        Frame = (w, h);
        _content01 = StudioLayout.Content(w, h, _project.Width, _project.Height, look);
        Pointer = new PointerPlan(_project);
        Camera = new CameraPlan(_project, Pointer, _content01.Width / _content01.Height);
        _background.Background = BackgroundFor(look);
        _shadow.Visibility = look.Framed && look.Shadow ? Visibility.Visible : Visibility.Collapsed;
        Layout();
        Show(Time);
    }

    /// <summary>The finished video's size.</summary>
    public (int Width, int Height) Frame { get; private set; } = (1920, 1080);

    private Brush BackgroundFor(StudioLook look)
    {
        if (look.Background == "none")
            return Brushes.Black;
        if (look.Background == "blur")
        {
            var brush = new VisualBrush(Player) { Stretch = Stretch.UniformToFill };
            return brush;
        }
        if (look.Background == "wallpaper" && StudioBackgrounds.WallpaperPath() is { } path)
        {
            try
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.UriSource = new Uri(path);
                image.DecodePixelWidth = 1280;
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.EndInit();
                image.Freeze();
                return new ImageBrush(image) { Stretch = Stretch.UniformToFill };
            }
            catch (Exception ex) when (ex is IOException or NotSupportedException or UriFormatException)
            {
            }
        }
        var g = StudioBackgrounds.Gradient(look.Background) ?? StudioBackgrounds.Gradient("violet")!.Value;
        var gradient = new LinearGradientBrush(Color.FromRgb(g.From.Red, g.From.Green, g.From.Blue), Color.FromRgb(g.To.Red, g.To.Green, g.To.Blue), new Point(0, 0), new Point(1, 1));
        gradient.Freeze();
        return gradient;
    }

    private void Layout()
    {
        if (_project is null || ActualWidth <= 0 || ActualHeight <= 0)
            return;
        var (w, h) = Frame;
        _scale = Math.Min(ActualWidth / w, ActualHeight / h);
        _stage.Width = w * _scale;
        _stage.Height = h * _scale;
        var look = _project.Edit.Look;
        var c = _content01;
        double radius = StudioLayout.Radius(w, h, look) * _scale;
        foreach (var e in new FrameworkElement[] { _content, _shadow })
        {
            e.Margin = new Thickness(c.X * _scale, c.Y * _scale, 0, 0);
            e.Width = c.Width * _scale;
            e.Height = c.Height * _scale;
        }
        _content.Clip = new RectangleGeometry(new Rect(0, 0, c.Width * _scale, c.Height * _scale), radius, radius);
        _shadow.CornerRadius = new CornerRadius(radius);
        _shadow.Effect = new DropShadowEffect { BlurRadius = Math.Min(w, h) * 0.04 * _scale, ShadowDepth = Math.Min(w, h) * 0.008 * _scale, Direction = 270, Opacity = 0.5 };
        _background.Effect = look.Background == "blur" ? new BlurEffect { Radius = Math.Min(w, h) * 0.03 * _scale } : null;
        _background.Opacity = look.Background == "blur" ? 0.75 : 1;
        Show(Time);
    }

    /// <summary>The picture at a moment of the recording.</summary>
    public void Show(double time)
    {
        Time = time;
        if (_project is null || Camera is null)
            return;
        var state = Camera.At(time);
        var view = Camera.View(state);
        double s = _content01.Width * _scale / view.Width;
        _look.Matrix = new Matrix(s, 0, 0, s, -view.X * s, -view.Y * s);
        _layer.InvalidateVisual();
        _keys.InvalidateVisual();
        if (_bubble.Visibility == Visibility.Visible && _project.CameraPlaces.Count > 0)
        {
            var place = _project.CameraPlaces.LastOrDefault(p => p.Time <= time);
            if (place.Diameter <= 0)
                place = _project.CameraPlaces[0];
            var c = _content01;
            double k = Math.Min(c.Width, c.Height) / Math.Min(_project.Width, _project.Height);
            double d = Math.Min(place.Diameter * k, Math.Min(c.Width, c.Height) * 0.6);
            double cx = Math.Clamp(c.X + place.X / _project.Width * c.Width, c.X + d / 2, c.Right - d / 2);
            double cy = Math.Clamp(c.Y + place.Y / _project.Height * c.Height, c.Y + d / 2, c.Bottom - d / 2);
            _bubble.Width = _bubble.Height = d * _scale;
            _bubble.CornerRadius = new CornerRadius(d * _scale / 2);
            _bubble.BorderThickness = new Thickness(Math.Max(1.5, d * 0.018 * _scale));
            _bubble.Margin = new Thickness((cx - d / 2) * _scale, (cy - d / 2) * _scale, 0, 0);
            _bubble.Clip = new EllipseGeometry(new Rect(0, 0, d * _scale, d * _scale));
        }
    }

    /// <summary>A still of the recording over the player, for the design snapshots (a player draws nothing off screen).</summary>
    internal void StillForSnapshot(ImageSource still)
    {
        if (_project is null)
            return;
        _world.Children.Insert(1, new Image { Source = still, Width = _project.Width, Height = _project.Height, Stretch = Stretch.Fill });
    }

    private readonly Dictionary<(PointerKind, int), (BitmapSource Image, PointerPicture Picture)> _pointers = [];

    private (BitmapSource Image, PointerPicture Picture) PointerImage(PointerKind kind, int shape)
    {
        if (_pointers.TryGetValue((kind, shape), out var made))
            return made;
        var picture = _art!.For(kind, shape);
        using var bitmap = SKBitmap.FromImage(picture.Image);
        var source = BitmapSource.Create(bitmap.Width, bitmap.Height, 96, 96, PixelFormats.Pbgra32, null, bitmap.GetPixels(), bitmap.ByteCount, bitmap.RowBytes);
        source.Freeze();
        return _pointers[(kind, shape)] = (source, picture);
    }

    /// <summary>What sits on the recording and moves with it: the click rings and the pointer.</summary>
    private sealed class WorldLayer(StudioPreview owner) : FrameworkElement
    {
        protected override void OnRender(DrawingContext dc)
        {
            var project = owner._project;
            if (project is null || owner.Pointer is not { } pointer)
                return;
            var look = project.Edit.Look;
            double t = owner.Time;
            double unit = 32 * Math.Max(0.5, project.PixelScale) * Math.Max(0.5, look.PointerSize) / 32;
            if (look.ClickRings)
            {
                foreach (var click in project.Clicks)
                {
                    double age = t - click.Time;
                    if (age < 0 || age > 0.55)
                        continue;
                    double p = age / 0.55, eased = 1 - Math.Pow(1 - p, 3);
                    var colour = click.Button switch { 2 => Color.FromRgb(0x3B, 0x82, 0xF6), 3 => Color.FromRgb(0x22, 0xC5, 0x5E), _ => Color.FromRgb(0xF5, 0x9E, 0x0B) };
                    double r = unit * (6 + 22 * eased);
                    var fill = new SolidColorBrush(Color.FromArgb((byte)(80 * (1 - p)), colour.R, colour.G, colour.B));
                    var pen = new Pen(new SolidColorBrush(Color.FromArgb((byte)(235 * (1 - p)), colour.R, colour.G, colour.B)), unit * 2.4);
                    dc.DrawEllipse(fill, pen, new Point(click.X, click.Y), r, r);
                }
            }
            var state = pointer.At(t);
            if (!pointer.Any || state.Opacity < 0.01)
                return;
            var (image, picture) = owner.PointerImage(state.Kind, state.Shape);
            double size = 32 * Math.Max(0.5, project.PixelScale) * Math.Max(0.5, look.PointerSize) * (1 - 0.14 * state.Press);
            double scale = size / 32 / picture.PixelsPerPoint;
            dc.PushOpacity(state.Opacity);
            dc.DrawImage(image, new Rect(state.X - picture.HotX * scale, state.Y - picture.HotY * scale, image.PixelWidth * scale, image.PixelHeight * scale));
            dc.Pop();
        }
    }

    /// <summary>What sits on the frame: the shortcuts pressed.</summary>
    private sealed class StageLayer(StudioPreview owner) : FrameworkElement
    {
        private static readonly Typeface Face = new(Visuals.UiFonts.Text, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

        protected override void OnRender(DrawingContext dc)
        {
            var project = owner._project;
            if (project is null || !project.Edit.Look.ShowKeys)
                return;
            double t = owner.Time;
            var shown = project.Keys.Where(k => t >= k.Time && t - k.Time < 1.5).TakeLast(3).ToList();
            if (shown.Count == 0)
                return;
            var (w, h) = owner.Frame;
            double unit = Math.Min(w, h) / 1080.0 * owner._scale;
            double bottom = Math.Min(h * owner._scale - 24 * unit, owner._content01.Bottom * owner._scale - 28 * unit);
            for (int i = shown.Count - 1; i >= 0; i--)
            {
                double age = t - shown[i].Time;
                double alpha = Math.Min(1, Math.Min(age / 0.1, (1.5 - age) / 0.2));
                var text = new FormattedText(shown[i].Keys, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, Math.Max(6, 30 * unit),
                    new SolidColorBrush(Color.FromArgb((byte)(255 * alpha), 255, 255, 255)), VisualTreeHelper.GetDpi(this).PixelsPerDip);
                double pw = text.Width + 44 * unit, ph = 58 * unit;
                var rect = new Rect(w * owner._scale / 2 - pw / 2, bottom - ph, pw, ph);
                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb((byte)(0xD8 * alpha), 0x10, 0x10, 0x14)), null, rect, ph / 2, ph / 2);
                dc.DrawText(text, new Point(rect.X + (pw - text.Width) / 2, rect.Y + (ph - text.Height) / 2));
                bottom -= ph + 10 * unit;
            }
        }
    }
}
