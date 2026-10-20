using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Petal.App.Tools.Parts;

/// <summary>
/// Shows an image (or video frame) and an adjustable crop rectangle over it: eight handles,
/// drag inside to move, outside dimmed, thirds grid while dragging, optional fixed aspect ratio.
/// The rectangle is kept in source pixels.
/// </summary>
public sealed class CropOverlay : Grid
{
    private const double HandleSize = 12;
    private readonly Image _image = new() { Stretch = Stretch.Uniform };
    private readonly Canvas _canvas = new() { ClipToBounds = false };
    private readonly Path _shade = new() { IsHitTestVisible = false };
    private readonly Rectangle _frame = new() { StrokeThickness = 2, Fill = Brushes.Transparent, Cursor = Cursors.SizeAll };
    private readonly Path _thirds = new() { StrokeThickness = 1, IsHitTestVisible = false, Opacity = 0 };
    private readonly Rectangle[] _handles = new Rectangle[8];
    private Size _source;
    private Rect _crop;
    private double? _ratio;
    private int _dragHandle = -2; // -2 none, -1 move, 0..7 handles
    private Point _dragStart;
    private Rect _dragStartCrop;

    private static readonly Cursor[] HandleCursors =
    [
        Cursors.SizeNWSE, Cursors.SizeNS, Cursors.SizeNESW, Cursors.SizeWE,
        Cursors.SizeNWSE, Cursors.SizeNS, Cursors.SizeNESW, Cursors.SizeWE,
    ];

    public event Action<Rect>? Changed;

    public CropOverlay()
    {
        FlowDirection = FlowDirection.LeftToRight;
        Children.Add(_image);
        Children.Add(_canvas);
        _canvas.Children.Add(_shade);
        _canvas.Children.Add(_thirds);
        _canvas.Children.Add(_frame);
        _shade.Fill = new SolidColorBrush(Color.FromArgb(0x96, 0, 0, 0));
        _thirds.Stroke = new SolidColorBrush(Color.FromArgb(0x90, 255, 255, 255));
        _frame.SetResourceReference(Shape.StrokeProperty, "Petal.Accent");
        for (int i = 0; i < 8; i++)
        {
            var handle = new Rectangle
            {
                Width = HandleSize,
                Height = HandleSize,
                RadiusX = 3,
                RadiusY = 3,
                Fill = Brushes.White,
                StrokeThickness = 2,
                Cursor = HandleCursors[i],
                Tag = i,
            };
            handle.SetResourceReference(Shape.StrokeProperty, "Petal.Accent");
            handle.MouseLeftButtonDown += OnHandleDown;
            _handles[i] = handle;
            _canvas.Children.Add(handle);
        }
        _frame.MouseLeftButtonDown += (_, e) => BeginDrag(-1, e);
        MouseMove += OnMove;
        MouseLeftButtonUp += (_, _) => EndDrag();
        LostMouseCapture += (_, _) => EndDrag();
        SizeChanged += (_, _) => Layout();
        Focusable = true;
        KeyDown += OnKey;
    }

    public ImageSource? Source
    {
        get => _image.Source;
        set
        {
            _image.Source = value;
            Layout();
        }
    }

    /// <summary>Size of the original media in pixels (may differ from the preview bitmap).</summary>
    public Size SourceSize
    {
        get => _source;
        set
        {
            _source = value;
            _crop = new Rect(0, 0, value.Width, value.Height);
            Layout();
        }
    }

    /// <summary>The crop rectangle in source pixels, rounded to whole pixels.</summary>
    public Rect Crop
    {
        get => new(Math.Round(_crop.X), Math.Round(_crop.Y), Math.Round(_crop.Width), Math.Round(_crop.Height));
        set
        {
            _crop = Clamp(value);
            Layout();
            Changed?.Invoke(Crop);
        }
    }

    /// <summary>Width ÷ height to keep, or null for free-form.</summary>
    public double? AspectRatio
    {
        get => _ratio;
        set
        {
            _ratio = value;
            if (value is { } r && _source.Width > 0)
            {
                // Largest rectangle of that ratio, centred on the current crop.
                double w = _crop.Width, h = w / r;
                if (h > _crop.Height) { h = _crop.Height; w = h * r; }
                if (w < 16) { w = Math.Min(_source.Width, _source.Height * r); h = w / r; }
                var c = new Point(_crop.X + _crop.Width / 2, _crop.Y + _crop.Height / 2);
                Crop = new Rect(c.X - w / 2, c.Y - h / 2, w, h);
            }
        }
    }

    public void Reset()
    {
        _ratio = null;
        Crop = new Rect(0, 0, _source.Width, _source.Height);
    }

    /// <summary>Sets an exact output size, centred on the current crop, honouring the ratio.</summary>
    public void SetSize(double width, double height)
    {
        width = Math.Clamp(width, 8, _source.Width);
        height = Math.Clamp(height, 8, _source.Height);
        var c = new Point(_crop.X + _crop.Width / 2, _crop.Y + _crop.Height / 2);
        Crop = new Rect(c.X - width / 2, c.Y - height / 2, width, height);
    }

    // ---------- layout ----------

    private Rect ImageRect()
    {
        if (_source.Width <= 0 || ActualWidth <= 0 || ActualHeight <= 0)
            return Rect.Empty;
        double scale = Math.Min(ActualWidth / _source.Width, ActualHeight / _source.Height);
        double w = _source.Width * scale, h = _source.Height * scale;
        return new Rect((ActualWidth - w) / 2, (ActualHeight - h) / 2, w, h);
    }

    private double Scale => _source.Width > 0 ? ImageRect().Width / _source.Width : 1;

    private Rect ToScreen(Rect r)
    {
        var img = ImageRect();
        double s = Scale;
        return new Rect(img.X + r.X * s, img.Y + r.Y * s, r.Width * s, r.Height * s);
    }

    private void Layout()
    {
        var img = ImageRect();
        if (img.IsEmpty)
            return;
        var screen = ToScreen(_crop);
        _shade.Data = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(img), new RectangleGeometry(screen));
        Canvas.SetLeft(_frame, screen.X);
        Canvas.SetTop(_frame, screen.Y);
        _frame.Width = Math.Max(1, screen.Width);
        _frame.Height = Math.Max(1, screen.Height);

        var thirds = new GeometryGroup();
        for (int i = 1; i <= 2; i++)
        {
            double x = screen.X + screen.Width * i / 3, y = screen.Y + screen.Height * i / 3;
            thirds.Children.Add(new LineGeometry(new Point(x, screen.Y), new Point(x, screen.Bottom)));
            thirds.Children.Add(new LineGeometry(new Point(screen.X, y), new Point(screen.Right, y)));
        }
        _thirds.Data = thirds;

        Point[] points =
        [
            screen.TopLeft, new(screen.X + screen.Width / 2, screen.Y), screen.TopRight, new(screen.Right, screen.Y + screen.Height / 2),
            screen.BottomRight, new(screen.X + screen.Width / 2, screen.Bottom), screen.BottomLeft, new(screen.X, screen.Y + screen.Height / 2),
        ];
        for (int i = 0; i < 8; i++)
        {
            bool edge = i % 2 == 1;
            // Edge handles become bars along their edge.
            _handles[i].Width = edge && (i == 1 || i == 5) ? 22 : edge ? 5 : HandleSize;
            _handles[i].Height = edge && (i == 3 || i == 7) ? 22 : edge ? 5 : HandleSize;
            Canvas.SetLeft(_handles[i], points[i].X - _handles[i].Width / 2);
            Canvas.SetTop(_handles[i], points[i].Y - _handles[i].Height / 2);
        }
    }

    // ---------- dragging ----------

    private void OnHandleDown(object sender, MouseButtonEventArgs e)
    {
        BeginDrag((int)((FrameworkElement)sender).Tag, e);
    }

    private void BeginDrag(int handle, MouseButtonEventArgs e)
    {
        e.Handled = true;
        Focus();
        _dragHandle = handle;
        _dragStart = e.GetPosition(this);
        _dragStartCrop = _crop;
        CaptureMouse();
        _thirds.Opacity = 1;
    }

    private void EndDrag()
    {
        if (_dragHandle == -2)
            return;
        _dragHandle = -2;
        _thirds.Opacity = 0;
        if (IsMouseCaptured)
            ReleaseMouseCapture();
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        if (_dragHandle == -2 || e.LeftButton != MouseButtonState.Pressed)
            return;
        var delta = (e.GetPosition(this) - _dragStart) / Scale;
        var r = _dragStartCrop;

        if (_dragHandle == -1)
        {
            var moved = new Rect(r.X + delta.X, r.Y + delta.Y, r.Width, r.Height);
            moved.X = Math.Clamp(moved.X, 0, _source.Width - moved.Width);
            moved.Y = Math.Clamp(moved.Y, 0, _source.Height - moved.Height);
            _crop = moved;
        }
        else
        {
            double left = r.Left, top = r.Top, right = r.Right, bottom = r.Bottom;
            int h = _dragHandle;
            if (h is 0 or 6 or 7) left += delta.X;
            if (h is 2 or 3 or 4) right += delta.X;
            if (h is 0 or 1 or 2) top += delta.Y;
            if (h is 4 or 5 or 6) bottom += delta.Y;
            const double min = 16;
            left = Math.Clamp(left, 0, right - min);
            right = Math.Clamp(right, left + min, _source.Width);
            top = Math.Clamp(top, 0, bottom - min);
            bottom = Math.Clamp(bottom, top + min, _source.Height);
            var next = new Rect(left, top, right - left, bottom - top);
            if (_ratio is { } ratio)
                next = ApplyRatio(next, h, ratio);
            _crop = Clamp(next);
        }
        Layout();
        Changed?.Invoke(Crop);
    }

    private Rect ApplyRatio(Rect r, int handle, double ratio)
    {
        bool horizontalEdge = handle is 1 or 5;
        double w = r.Width, h = r.Height;
        if (horizontalEdge) w = h * ratio;
        else h = w / ratio;
        if (w > _source.Width) { w = _source.Width; h = w / ratio; }
        if (h > _source.Height) { h = _source.Height; w = h * ratio; }
        // Anchor the corner opposite the handle.
        double x = handle is 0 or 6 or 7 ? r.Right - w : handle is 1 or 5 ? r.X + (r.Width - w) / 2 : r.X;
        double y = handle is 0 or 1 or 2 ? r.Bottom - h : handle is 3 or 7 ? r.Y + (r.Height - h) / 2 : r.Y;
        return new Rect(x, y, w, h);
    }

    private Rect Clamp(Rect r)
    {
        if (_source.Width <= 0)
            return r;
        double w = Math.Clamp(r.Width, 8, _source.Width), h = Math.Clamp(r.Height, 8, _source.Height);
        double x = Math.Clamp(r.X, 0, _source.Width - w), y = Math.Clamp(r.Y, 0, _source.Height - h);
        return new Rect(x, y, w, h);
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        double step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 10 : 1;
        var r = _crop;
        switch (e.Key)
        {
            case Key.Left: r.X -= step; break;
            case Key.Right: r.X += step; break;
            case Key.Up: r.Y -= step; break;
            case Key.Down: r.Y += step; break;
            default: return;
        }
        e.Handled = true;
        Crop = r;
    }
}
