using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Petal.App.Tools.Parts;

public enum AnnotationTool
{
    Arrow,
    Highlighter,
    Text,
    Rectangle,
    Ellipse,
    Pen,
    Number,
    Magnifier,
    Image,
}

/// <summary>An annotation in image pixel coordinates. Drawing at scale 1 gives the exported result.</summary>
public abstract class Annotation
{
    public Color Color { get; set; }

    /// <summary>Stroke width in image pixels.</summary>
    public double Weight { get; set; }

    public abstract Rect Bounds { get; }

    public abstract void Draw(DrawingContext dc, BitmapSource photo);

    public abstract void Move(Vector delta);

    public virtual bool Hit(Point p) => Inflate(Bounds, Weight * 2 + 6).Contains(p);

    public abstract Annotation Clone();

    protected Pen Stroke(double alpha = 1) =>
        new(new SolidColorBrush(Color.FromArgb((byte)(255 * alpha), Color.R, Color.G, Color.B)), Weight)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        };

    protected static Rect Inflate(Rect r, double by)
    {
        r.Inflate(by, by);
        return r;
    }

    protected static double Distance(Point p, Point a, Point b)
    {
        var ab = b - a;
        double t = ab.LengthSquared < 1e-6 ? 0 : Math.Clamp(Vector.Multiply(p - a, ab) / ab.LengthSquared, 0, 1);
        return (p - (a + ab * t)).Length;
    }
}

public sealed class ArrowAnnotation : Annotation
{
    public Point Start { get; set; }
    public Point End { get; set; }

    public override Rect Bounds => new(Start, End);

    public override void Draw(DrawingContext dc, BitmapSource photo)
    {
        var dir = End - Start;
        if (dir.Length < 1) return;
        dir.Normalize();
        double head = Weight * 4.2;
        var normal = new Vector(-dir.Y, dir.X);
        var baseCentre = End - dir * head;
        // Shaft stops inside the head so the tip stays sharp.
        dc.DrawLine(Stroke(), Start, End - dir * head * 0.6);
        var tip = new StreamGeometry();
        using (var g = tip.Open())
        {
            g.BeginFigure(End, true, true);
            g.LineTo(baseCentre + normal * head * 0.62, true, true);
            g.LineTo(baseCentre - normal * head * 0.62, true, true);
        }
        dc.DrawGeometry(new SolidColorBrush(Color), new Pen(new SolidColorBrush(Color), Weight * 0.4) { LineJoin = PenLineJoin.Round }, tip);
    }

    public override bool Hit(Point p) => Distance(p, Start, End) < Weight * 2 + 6;

    public override void Move(Vector delta)
    {
        Start += delta;
        End += delta;
    }

    public override Annotation Clone() => (ArrowAnnotation)MemberwiseClone();
}

public sealed class StrokeAnnotation : Annotation
{
    public List<Point> Points { get; set; } = [];

    /// <summary>A highlighter: wide, translucent, flat ends.</summary>
    public bool Highlighter { get; init; }

    public override Rect Bounds
    {
        get
        {
            if (Points.Count == 0) return Rect.Empty;
            var r = new Rect(Points[0], Points[0]);
            foreach (var p in Points) r.Union(p);
            return r;
        }
    }

    public override void Draw(DrawingContext dc, BitmapSource photo)
    {
        if (Points.Count < 2) return;
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(Points[0], false, false);
            // Smooth with quadratic curves through midpoints.
            for (int i = 1; i < Points.Count - 1; i++)
            {
                var mid = new Point((Points[i].X + Points[i + 1].X) / 2, (Points[i].Y + Points[i + 1].Y) / 2);
                g.QuadraticBezierTo(Points[i], mid, true, true);
            }
            g.LineTo(Points[^1], true, true);
        }
        var pen = Highlighter
            ? new Pen(new SolidColorBrush(Color.FromArgb(0x70, Color.R, Color.G, Color.B)), Weight * 3.2) { StartLineCap = PenLineCap.Flat, EndLineCap = PenLineCap.Flat, LineJoin = PenLineJoin.Round }
            : Stroke();
        dc.DrawGeometry(null, pen, geometry);
    }

    public override bool Hit(Point p)
    {
        for (int i = 1; i < Points.Count; i++)
            if (Distance(p, Points[i - 1], Points[i]) < Weight * (Highlighter ? 2 : 1) + 6)
                return true;
        return false;
    }

    public override void Move(Vector delta)
    {
        for (int i = 0; i < Points.Count; i++) Points[i] += delta;
    }

    public override Annotation Clone()
    {
        var c = (StrokeAnnotation)MemberwiseClone();
        c.Points = [.. Points];
        return c;
    }
}

public sealed class ShapeAnnotation : Annotation
{
    public Rect Rect { get; set; }
    public bool Round { get; init; }

    public override Rect Bounds => Rect;

    public override void Draw(DrawingContext dc, BitmapSource photo)
    {
        if (Round)
            dc.DrawEllipse(null, Stroke(), new Point(Rect.X + Rect.Width / 2, Rect.Y + Rect.Height / 2), Rect.Width / 2, Rect.Height / 2);
        else
            dc.DrawRoundedRectangle(null, Stroke(), Rect, Weight, Weight);
    }

    public override bool Hit(Point p)
    {
        // Only the outline counts, so clicks inside reach whatever is underneath.
        var outer = Inflate(Rect, Weight + 6);
        var inner = Inflate(Rect, -(Weight + 6));
        return outer.Contains(p) && (inner.IsEmpty || !inner.Contains(p));
    }

    public override void Move(Vector delta) => Rect = Rect.Offset(Rect, delta);

    public override Annotation Clone() => (ShapeAnnotation)MemberwiseClone();
}

public sealed class TextAnnotation : Annotation
{
    public Point Position { get; set; }
    public string Text { get; set; } = "";

    private FormattedText Format() => new(
        Text.Length == 0 ? " " : Text,
        CultureInfo.CurrentUICulture,
        FlowDirection.LeftToRight,
        new Typeface(new FontFamily("Segoe UI Variable Display, Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
        Weight * 7,
        new SolidColorBrush(Color),
        1.0);

    public override Rect Bounds
    {
        get
        {
            var f = Format();
            return new Rect(Position, new Size(f.Width, f.Height));
        }
    }

    public override void Draw(DrawingContext dc, BitmapSource photo)
    {
        var f = Format();
        // A soft outline in the opposite tone keeps text readable on any photo.
        var outline = f.BuildGeometry(Position);
        bool darkText = Color.R * 0.3 + Color.G * 0.59 + Color.B * 0.11 < 128;
        var halo = new Pen(new SolidColorBrush(darkText ? Color.FromArgb(0xC0, 255, 255, 255) : Color.FromArgb(0xA0, 0, 0, 0)), Weight * 1.2) { LineJoin = PenLineJoin.Round };
        dc.DrawGeometry(null, halo, outline);
        dc.DrawText(f, Position);
    }

    public override void Move(Vector delta) => Position += delta;

    public override Annotation Clone() => (TextAnnotation)MemberwiseClone();
}

public sealed class NumberAnnotation : Annotation
{
    public Point Centre { get; set; }
    public int Number { get; set; }

    private double Radius => Weight * 3.4;

    public override Rect Bounds => new(Centre.X - Radius, Centre.Y - Radius, Radius * 2, Radius * 2);

    public override void Draw(DrawingContext dc, BitmapSource photo)
    {
        dc.DrawEllipse(new SolidColorBrush(Color), new Pen(Brushes.White, Weight * 0.5), Centre, Radius, Radius);
        var text = new FormattedText(Number.ToString(CultureInfo.CurrentCulture), CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI Variable Display, Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
            Radius * 1.15, Brushes.White, 1.0);
        dc.DrawText(text, new Point(Centre.X - text.Width / 2, Centre.Y - text.Height / 2));
    }

    public override void Move(Vector delta) => Centre += delta;

    public override Annotation Clone() => (NumberAnnotation)MemberwiseClone();
}

public sealed class MagnifierAnnotation : Annotation
{
    public Point Centre { get; set; }
    public double Radius { get; set; }
    public double Zoom { get; set; } = 2;

    public override Rect Bounds => new(Centre.X - Radius, Centre.Y - Radius, Radius * 2, Radius * 2);

    public override void Draw(DrawingContext dc, BitmapSource photo)
    {
        var circle = new EllipseGeometry(Centre, Radius, Radius);
        dc.PushClip(circle);
        double w = photo.PixelWidth * Zoom, h = photo.PixelHeight * Zoom;
        // Keep the magnified point under the lens centre.
        dc.DrawImage(photo, new Rect(Centre.X - Centre.X * Zoom, Centre.Y - Centre.Y * Zoom, w, h));
        dc.Pop();
        dc.DrawEllipse(null, new Pen(Brushes.White, Weight * 1.4), Centre, Radius, Radius);
        dc.DrawEllipse(null, new Pen(new SolidColorBrush(Color), Weight * 0.7), Centre, Radius, Radius);
    }

    public override void Move(Vector delta) => Centre += delta;

    public override Annotation Clone() => (MagnifierAnnotation)MemberwiseClone();
}

public sealed class ImageAnnotation : Annotation
{
    public Rect Rect { get; set; }
    public required BitmapSource Picture { get; init; }

    public override Rect Bounds => Rect;

    public override void Draw(DrawingContext dc, BitmapSource photo)
    {
        dc.PushClip(new RectangleGeometry(Rect, Weight * 2, Weight * 2));
        dc.DrawImage(Picture, Rect);
        dc.Pop();
        dc.DrawRoundedRectangle(null, new Pen(Brushes.White, Weight * 0.8), Rect, Weight * 2, Weight * 2);
    }

    public override void Move(Vector delta) => Rect = Rect.Offset(Rect, delta);

    public override Annotation Clone() => (ImageAnnotation)MemberwiseClone();
}

/// <summary>
/// The Annotate Photo canvas: shows the photo, lets the user add and move annotations with the
/// current tool, and renders the final full-resolution image.
/// </summary>
public sealed class AnnotateEditor : FrameworkElement
{
    private readonly List<Annotation> _items = [];
    private readonly Stack<List<Annotation>> _undo = new();
    private BitmapSource? _photo;
    private Annotation? _active;
    private Annotation? _selected;
    private Point _start;
    private bool _moving;
    private int _nextNumber = 1;
    private TextBox? _editor;
    private readonly Canvas _host;

    public AnnotateEditor(Canvas host)
    {
        _host = host;
        Focusable = true;
        Cursor = Cursors.Cross;
        ClipToBounds = true;
    }

    public event Action? Changed;

    public AnnotationTool Tool { get; set; } = AnnotationTool.Arrow;

    public Color Color { get; set; } = Color.FromRgb(0xEB, 0x4D, 0x47);

    public BitmapSource? Photo
    {
        get => _photo;
        set
        {
            _photo = value;
            InvalidateVisual();
        }
    }

    /// <summary>Picture used by the next Image annotation.</summary>
    public BitmapSource? PendingPicture { get; set; }

    public bool CanUndo => _undo.Count > 0;
    public bool HasSelection => _selected is not null;
    public bool IsEmpty => _items.Count == 0;

    private double DefaultWeight => _photo is null ? 4 : Math.Max(3, Math.Max(_photo.PixelWidth, _photo.PixelHeight) / 220.0);

    private Rect ImageRect()
    {
        if (_photo is null || ActualWidth <= 0) return Rect.Empty;
        double s = Math.Min(ActualWidth / _photo.PixelWidth, ActualHeight / _photo.PixelHeight);
        return new Rect((ActualWidth - _photo.PixelWidth * s) / 2, (ActualHeight - _photo.PixelHeight * s) / 2, _photo.PixelWidth * s, _photo.PixelHeight * s);
    }

    private double Scale => _photo is null ? 1 : ImageRect().Width / _photo.PixelWidth;

    private Point ToImage(Point p)
    {
        var r = ImageRect();
        return new Point((p.X - r.X) / Scale, (p.Y - r.Y) / Scale);
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        if (_photo is null) return;
        var r = ImageRect();
        dc.DrawImage(_photo, r);
        dc.PushClip(new RectangleGeometry(r));
        dc.PushTransform(new TranslateTransform(r.X, r.Y));
        dc.PushTransform(new ScaleTransform(Scale, Scale));
        foreach (var a in _items) a.Draw(dc, _photo);
        if (_selected is not null)
        {
            var b = _selected.Bounds;
            b.Inflate(_selected.Weight + 4, _selected.Weight + 4);
            var pen = new Pen(new SolidColorBrush(Color.FromArgb(0xC0, 255, 255, 255)), 1.5 / Scale) { DashStyle = new DashStyle([4, 3], 0) };
            dc.DrawRectangle(null, pen, b);
        }
        dc.Pop();
        dc.Pop();
        dc.Pop();
    }

    private void Snapshot() => _undo.Push(_items.Select(a => a.Clone()).ToList());

    public void Undo()
    {
        if (_undo.Count == 0) return;
        _items.Clear();
        _items.AddRange(_undo.Pop());
        _selected = null;
        _nextNumber = _items.OfType<NumberAnnotation>().Select(n => n.Number).DefaultIfEmpty(0).Max() + 1;
        InvalidateVisual();
        Changed?.Invoke();
    }

    public void DeleteSelected()
    {
        if (_selected is null) return;
        Snapshot();
        _items.Remove(_selected);
        _selected = null;
        InvalidateVisual();
        Changed?.Invoke();
    }

    public void Clear()
    {
        if (_items.Count == 0) return;
        Snapshot();
        _items.Clear();
        _selected = null;
        _nextNumber = 1;
        InvalidateVisual();
        Changed?.Invoke();
    }

    public void ApplyColor(Color color)
    {
        Color = color;
        if (_selected is not null)
        {
            Snapshot();
            _selected.Color = color;
            InvalidateVisual();
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (_photo is null) return;
        CommitText();
        Focus();
        var p = ToImage(e.GetPosition(this));
        _start = p;

        // Clicking an annotation moves it, whatever the tool.
        var hit = _items.LastOrDefault(a => a.Hit(p));
        if (hit is not null)
        {
            Snapshot();
            _selected = hit;
            _moving = true;
            CaptureMouse();
            if (e.ClickCount == 2 && hit is TextAnnotation text)
                BeginTextEdit(text);
            InvalidateVisual();
            Changed?.Invoke();
            return;
        }

        Snapshot();
        double w = DefaultWeight;
        _active = Tool switch
        {
            AnnotationTool.Arrow => new ArrowAnnotation { Start = p, End = p, Color = Color, Weight = w },
            AnnotationTool.Highlighter => new StrokeAnnotation { Points = [p], Color = Color, Weight = w, Highlighter = true },
            AnnotationTool.Pen => new StrokeAnnotation { Points = [p], Color = Color, Weight = w },
            AnnotationTool.Rectangle => new ShapeAnnotation { Rect = new Rect(p, p), Color = Color, Weight = w },
            AnnotationTool.Ellipse => new ShapeAnnotation { Rect = new Rect(p, p), Color = Color, Weight = w, Round = true },
            AnnotationTool.Number => new NumberAnnotation { Centre = p, Number = _nextNumber++, Color = Color, Weight = w },
            AnnotationTool.Magnifier => new MagnifierAnnotation { Centre = p, Radius = w * 18, Color = Color, Weight = w },
            AnnotationTool.Text => new TextAnnotation { Position = p, Color = Color, Weight = w },
            AnnotationTool.Image when PendingPicture is not null => new ImageAnnotation
            {
                Picture = PendingPicture,
                Rect = new Rect(p, new Size(_photo.PixelWidth * 0.3, _photo.PixelWidth * 0.3 * PendingPicture.PixelHeight / PendingPicture.PixelWidth)),
                Weight = w,
                Color = Colors.White,
            },
            _ => null,
        };
        if (_active is null)
        {
            _undo.Pop();
            return;
        }
        _items.Add(_active);
        _selected = _active;
        if (_active is TextAnnotation t)
        {
            _active = null;
            BeginTextEdit(t);
        }
        else
        {
            CaptureMouse();
        }
        InvalidateVisual();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (e.LeftButton != MouseButtonState.Pressed) return;
        var p = ToImage(e.GetPosition(this));
        if (_moving && _selected is not null)
        {
            _selected.Move(p - _start);
            _start = p;
            InvalidateVisual();
            return;
        }
        switch (_active)
        {
            case ArrowAnnotation arrow: arrow.End = p; break;
            case StrokeAnnotation stroke when (p - stroke.Points[^1]).Length > 1.5 / Scale: stroke.Points.Add(p); break;
            case ShapeAnnotation shape: shape.Rect = new Rect(_start, p); break;
            case MagnifierAnnotation lens: lens.Radius = Math.Max(DefaultWeight * 8, (p - lens.Centre).Length); break;
            case ImageAnnotation picture:
                double w = Math.Max(20, p.X - picture.Rect.X);
                picture.Rect = new Rect(picture.Rect.X, picture.Rect.Y, w, w * picture.Picture.PixelHeight / picture.Picture.PixelWidth);
                break;
        }
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (IsMouseCaptured) ReleaseMouseCapture();
        // Tiny accidental shapes are dropped.
        if (_active is ArrowAnnotation a && (a.End - a.Start).Length < 4 / Scale ||
            _active is ShapeAnnotation s && (s.Rect.Width < 4 || s.Rect.Height < 4))
        {
            _items.Remove(_active);
            _selected = null;
            _undo.Pop();
        }
        _active = null;
        _moving = false;
        InvalidateVisual();
        Changed?.Invoke();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_editor is not null) return;
        if (e.Key is Key.Delete or Key.Back) { DeleteSelected(); e.Handled = true; }
        else if (e.Key == Key.Z && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { Undo(); e.Handled = true; }
    }

    private void BeginTextEdit(TextAnnotation text)
    {
        var r = ImageRect();
        _editor = new TextBox
        {
            Text = text.Text,
            MinWidth = 120,
            FontSize = Math.Max(13, text.Weight * 7 * Scale),
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(text.Color),
            Background = new SolidColorBrush(Color.FromArgb(0xE6, 255, 255, 255)),
            Tag = text,
        };
        Canvas.SetLeft(_editor, r.X + text.Position.X * Scale);
        Canvas.SetTop(_editor, r.Y + text.Position.Y * Scale);
        _host.Children.Add(_editor);
        _editor.Focus();
        _editor.KeyDown += (_, e) =>
        {
            if (e.Key is Key.Enter or Key.Escape)
            {
                e.Handled = true;
                CommitText();
                Focus();
            }
        };
        _editor.LostKeyboardFocus += (_, _) => CommitText();
    }

    private void CommitText()
    {
        if (_editor?.Tag is not TextAnnotation text) return;
        text.Text = _editor.Text.Trim();
        _host.Children.Remove(_editor);
        _editor = null;
        if (text.Text.Length == 0)
        {
            _items.Remove(text);
            _selected = null;
        }
        InvalidateVisual();
        Changed?.Invoke();
    }

    /// <summary>The photo with all annotations at full resolution.</summary>
    public BitmapSource RenderFull()
    {
        CommitText();
        var photo = _photo ?? throw new InvalidOperationException("No photo");
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawImage(photo, new Rect(0, 0, photo.PixelWidth, photo.PixelHeight));
            foreach (var a in _items) a.Draw(dc, photo);
        }
        return ImageLoader.Render(visual, photo.PixelWidth, photo.PixelHeight);
    }
}
