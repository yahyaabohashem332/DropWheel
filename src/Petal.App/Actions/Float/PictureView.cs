using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Petal.App.Localization;
using Petal.App.Shell;

namespace Petal.App.Actions.Float;

/// <summary>
/// A picture to look into: the wheel zooms about the pointer (to 800 %, its pixels sharp past 200 %), dragging moves
/// it, a double-click goes between all of it and its own size. It turns and flips, gives a colour's code under the
/// pointer, and copies.
/// </summary>
internal sealed class PictureView : FloatView
{
    /// <summary>The longest side read: enough to look into, without holding a giant picture whole.</summary>
    private const int Largest = 8192;

    private readonly Canvas _canvas = new() { ClipToBounds = true };
    private readonly Image _image = new() { FlowDirection = FlowDirection.LeftToRight };
    private BitmapSource _source;
    private byte[]? _pixels;
    private double _zoom = 1;
    private Point _offset;
    private bool _fitted = true;
    private Point? _grab;
    private Point _grabOffset;
    private bool _picking;
    private readonly string _size;

    private PictureView(string path, BitmapSource source, string size) : base(path)
    {
        _source = source;
        _size = size;
        Background = QuickLookWindow.Checkers();
        _canvas.Children.Add(_image);
        Children.Add(_canvas);
        _image.Source = source;
        Focusable = true;
        SizeChanged += (_, _) =>
        {
            if (_fitted)
                FitAll();
            else
                Place();
        };
        MouseWheel += (_, e) =>
        {
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
                return;
            e.Handled = true;
            ZoomAt(e.GetPosition(_canvas), Math.Pow(1.15, e.Delta / 120.0));
        };
        MouseLeftButtonDown += (_, e) =>
        {
            if (_picking)
            {
                PickAt(e.GetPosition(_canvas), keep: true);
                e.Handled = true;
                return;
            }
            if (e.ClickCount == 2)
            {
                e.Handled = true;
                if (_fitted)
                {
                    var at = e.GetPosition(_canvas);
                    ZoomAt(at, OwnZoom / _zoom);
                }
                else
                    FitAll();
                return;
            }
            _grab = e.GetPosition(_canvas);
            _grabOffset = _offset;
            CaptureMouse();
            Cursor = Cursors.SizeAll;
        };
        MouseMove += (_, e) =>
        {
            if (_picking)
            {
                PickAt(e.GetPosition(_canvas), keep: false);
                return;
            }
            if (_grab is not { } from || e.LeftButton != MouseButtonState.Pressed)
                return;
            var now = e.GetPosition(_canvas);
            _offset = new Point(_grabOffset.X + now.X - from.X, _grabOffset.Y + now.Y - from.Y);
            _fitted = false;
            Place();
        };
        MouseLeftButtonUp += (_, _) =>
        {
            _grab = null;
            ReleaseMouseCapture();
            Cursor = _picking ? Cursors.Cross : null;
        };
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && _picking)
            {
                e.Handled = true;
                StopPicking();
            }
        };
        Tell(Said());
    }

    public static async Task<PictureView> LoadAsync(string path, CancellationToken cancellationToken)
    {
        var picture = await Tools.Parts.ImageLoader.LoadAsync(path, Largest);
        cancellationToken.ThrowIfCancellationRequested();
        string size;
        try
        {
            var info = await Task.Run(() => new ImageMagick.MagickImageInfo(path), cancellationToken);
            size = $"{info.Width} × {info.Height}";
        }
        catch (Exception ex) when (ex is ImageMagick.MagickException or IOException)
        {
            size = $"{picture.PixelWidth} × {picture.PixelHeight}";
        }
        return new PictureView(path, picture, size);
    }

    public override FloatKind Kind => FloatKind.Picture;

    /// <summary>Its pixels as device-independent pixels on this screen.</summary>
    public override Size Natural => new(_source.PixelWidth / Dpi, _source.PixelHeight / Dpi);

    public override string? ZoomText => FloatMath.Percent(_zoom * Dpi);

    private double Dpi => VisualTreeHelper.GetDpi(this).DpiScaleX is > 0 and var d ? d : 1;

    /// <summary>The zoom at which one of its pixels is one of the screen's.</summary>
    private double OwnZoom => 1 / Dpi;

    private Size Content => new(_source.PixelWidth, _source.PixelHeight);

    private Size View => new(_canvas.ActualWidth, _canvas.ActualHeight);

    private void ZoomAt(Point at, double factor)
    {
        (_zoom, _offset) = FloatMath.ZoomAbout(_zoom, _offset, at, factor);
        _fitted = false;
        Place();
    }

    public override void ZoomBy(double factor) => ZoomAt(new Point(View.Width / 2, View.Height / 2), factor);

    public override void FitAll()
    {
        _zoom = FloatMath.Fit(Content, View);
        _offset = FloatMath.Centred(Content, _zoom, View);
        _fitted = true;
        Place();
    }

    public override void OwnSize()
    {
        _zoom = OwnZoom;
        _offset = FloatMath.Centred(Content, _zoom, View);
        _fitted = false;
        Place();
    }

    private void Place()
    {
        _image.Width = _source.PixelWidth;
        _image.Height = _source.PixelHeight;
        _image.RenderTransform = new MatrixTransform(_zoom, 0, 0, _zoom, _offset.X, _offset.Y);
        // Past twice its size a picture shows its pixels as squares, not smudged.
        RenderOptions.SetBitmapScalingMode(_image, _zoom * Dpi >= 2 ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality);
        Tell();
    }

    private string Said() => QuickLookWindow.Facts(Path, _size);

    /// <summary>Turned or flipped: a new picture of the same pixels, all of it in view.</summary>
    private void Transform(Transform transform)
    {
        _source = new TransformedBitmap(_source, transform);
        _source.Freeze();
        _image.Source = _source;
        _pixels = null;
        FitAll();
    }

    public override IEnumerable<Control> MenuItems() =>
    [
        Item("Turn Right", () => Transform(new RotateTransform(90))),
        Item("Turn Left", () => Transform(new RotateTransform(-90))),
        Item("Flip Across", () => Transform(new ScaleTransform(-1, 1))),
        Item("Flip Upside Down", () => Transform(new ScaleTransform(1, -1))),
        new Separator(),
        Item("Pick a Colour", StartPicking),
        Item("Copy the Picture", () => Copy()),
    ];

    public override bool Copy()
    {
        Clipboard.SetImage(_source);
        return true;
    }

    // ───────────── a colour's code under the pointer ─────────────

    private void StartPicking()
    {
        _picking = true;
        Cursor = Cursors.Cross;
        Focus();
        Tell(L.T("Point at a colour and click to copy its code · Esc stops"));
    }

    private void StopPicking()
    {
        _picking = false;
        Cursor = null;
        Tell(Said());
    }

    /// <summary>The colour under the pointer said in the header; a click copies its code and stops.</summary>
    private void PickAt(Point at, bool keep)
    {
        int x = (int)Math.Floor((at.X - _offset.X) / _zoom), y = (int)Math.Floor((at.Y - _offset.Y) / _zoom);
        if (x < 0 || y < 0 || x >= _source.PixelWidth || y >= _source.PixelHeight)
            return;
        var code = ColourAt(x, y);
        if (!keep)
        {
            Tell(code + " · " + L.T("Click to copy"));
            return;
        }
        Clipboard.SetText(code);
        _picking = false;
        Cursor = null;
        Tell(L.F("{0} copied", code));
    }

    private string ColourAt(int x, int y)
    {
        if (_pixels is null)
        {
            var bgra = new FormatConvertedBitmap(_source, PixelFormats.Bgra32, null, 0);
            _pixels = new byte[_source.PixelWidth * _source.PixelHeight * 4];
            bgra.CopyPixels(_pixels, _source.PixelWidth * 4, 0);
        }
        int i = (y * _source.PixelWidth + x) * 4;
        return $"#{_pixels[i + 2]:X2}{_pixels[i + 1]:X2}{_pixels[i]:X2}";
    }

    public override void Dispose() => _pixels = null;
}
