using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace Petal.App.Tools.Parts;

/// <summary>A background fill: a preset gradient, a solid colour, or another image (optionally blurred).</summary>
public sealed record BackgroundFill(string Name, Func<Rect, Brush> Create, bool IsImage = false)
{
    public static BackgroundFill Solid(Color c) => new(ColorWell.Hex(c), _ => new SolidColorBrush(c));

    public static BackgroundFill FromImage(BitmapSource image) => new("Image", _ => new ImageBrush(image) { Stretch = Stretch.UniformToFill }, IsImage: true);

    /// <summary>Soft multi-point "mesh" gradients, built from layered radial gradients.</summary>
    public static readonly IReadOnlyList<BackgroundFill> Gradients =
    [
        Mesh("Dawn", "#FF9A8B", "#FF6A88", "#FFD3A5", "#FFC2E2"),
        Mesh("Lagoon", "#5EE7DF", "#B490CA", "#A1C4FD", "#C2E9FB"),
        Mesh("Aurora", "#7B5CFF", "#00D4FF", "#6EE7B7", "#312E81"),
        Mesh("Peach", "#FFECD2", "#FCB69F", "#FFD1BA", "#FF9F80"),
        Mesh("Ultraviolet", "#5B21B6", "#C026D3", "#1E1B4B", "#A78BFA"),
        Mesh("Mint", "#D4FC79", "#96E6A1", "#E0FFE8", "#5EEAD4"),
        Mesh("Sky", "#E0EAFC", "#CFDEF3", "#A1C4FD", "#FFFFFF"),
        Mesh("Ember", "#F83600", "#F9D423", "#FE8C00", "#B91C1C"),
        Mesh("Night", "#0F2027", "#203A43", "#2C5364", "#4B6CB7"),
        Mesh("Blush", "#FBC2EB", "#A6C1EE", "#FDE2F3", "#E9D5FF"),
        Mesh("Citrus", "#F6D365", "#FDA085", "#FFF1A8", "#FF7E5F"),
        Mesh("Graphite", "#434343", "#000000", "#2B2B2B", "#5A5A5A"),
    ];

    public static readonly IReadOnlyList<Color> Solids =
    [
        Color.FromRgb(0xF7, 0xF5, 0xF2), Color.FromRgb(0xFF, 0xE8, 0x7A), Color.FromRgb(0xC8, 0xF0, 0x6A), Color.FromRgb(0x4C, 0xC9, 0x7C),
        Color.FromRgb(0x2E, 0xB8, 0xA8), Color.FromRgb(0x9B, 0xE3, 0xF5), Color.FromRgb(0x3B, 0x82, 0xF6), Color.FromRgb(0x1E, 0x2A, 0x5A),
        Color.FromRgb(0x7B, 0x5C, 0xFF), Color.FromRgb(0xD8, 0xC8, 0xFF), Color.FromRgb(0xFF, 0xB5, 0xD6), Color.FromRgb(0xEF, 0x44, 0x44),
        Color.FromRgb(0xFF, 0x7A, 0x59), Color.FromRgb(0xF9, 0x97, 0x2B), Color.FromRgb(0x8B, 0x5E, 0x3C), Color.FromRgb(0x8E, 0x8E, 0x96),
        Colors.White, Colors.Black,
    ];

    private static BackgroundFill Mesh(string name, string a, string b, string c, string d) => new(name, area =>
    {
        Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);
        var visual = new DrawingVisual();
        var size = new Rect(0, 0, 400, 400);
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new LinearGradientBrush(C(a), C(b), 45), null, size);
            void Blob(string hex, double x, double y, double r)
            {
                var col = C(hex);
                dc.DrawEllipse(new RadialGradientBrush(col, Color.FromArgb(0, col.R, col.G, col.B)), null, new Point(x, y), r, r);
            }
            Blob(c, 90, 80, 260);
            Blob(d, 330, 320, 280);
            Blob(b, 340, 60, 200);
        }
        var brush = new VisualBrush(visual) { Stretch = Stretch.Fill };
        return brush;
    });
}

public enum BackgroundRatio
{
    Auto,
    Square,
    Widescreen,
    Portrait,
    Story,
}

public sealed record BackgroundOptions(BackgroundFill Fill, double Padding, double Corners, double Shadow, BackgroundRatio Ratio, double BackgroundBlur = 0);

/// <summary>Places a photo on a new canvas. The same drawing is used for the preview and the saved file.</summary>
public static class BackgroundComposer
{
    public static Size CanvasSize(Size photo, BackgroundOptions o)
    {
        double w = photo.Width + o.Padding * 2, h = photo.Height + o.Padding * 2;
        double? ratio = o.Ratio switch
        {
            BackgroundRatio.Square => 1,
            BackgroundRatio.Widescreen => 16.0 / 9,
            BackgroundRatio.Portrait => 4.0 / 5,
            BackgroundRatio.Story => 9.0 / 16,
            _ => null,
        };
        if (ratio is { } r)
        {
            if (w / h > r) h = w / r;
            else w = h * r;
        }
        return new Size(Math.Round(w), Math.Round(h));
    }

    public static DrawingVisual Compose(BitmapSource photo, BackgroundOptions o)
    {
        var size = new Size(photo.PixelWidth, photo.PixelHeight);
        var canvas = CanvasSize(size, o);
        var rect = new Rect((canvas.Width - size.Width) / 2, (canvas.Height - size.Height) / 2, size.Width, size.Height);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var area = new Rect(canvas);
            dc.DrawRectangle(o.Fill.Create(area), null, area);
            if (o.Shadow > 0)
            {
                // Soft shadow: stacked translucent rounded rectangles approximate a blur cheaply.
                int layers = 14;
                for (int i = layers; i >= 1; i--)
                {
                    double spread = o.Shadow * i / layers;
                    var s = new Rect(rect.X - spread * 0.5, rect.Y - spread * 0.5 + o.Shadow * 0.35, rect.Width + spread, rect.Height + spread);
                    dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb((byte)(70.0 / layers), 0, 0, 0)), null, s, o.Corners + spread / 2, o.Corners + spread / 2);
                }
            }
            dc.PushClip(new RectangleGeometry(rect, o.Corners, o.Corners));
            dc.DrawImage(photo, rect);
            dc.Pop();
        }
        if (o.Fill.IsImage && o.BackgroundBlur > 0)
            visual.Effect = null; // Blur is applied to the background layer in Render.
        return visual;
    }

    public static BitmapSource Render(BitmapSource photo, BackgroundOptions o)
    {
        var canvas = CanvasSize(new Size(photo.PixelWidth, photo.PixelHeight), o);
        var root = new DrawingVisual();
        using (var dc = root.RenderOpen())
        {
            if (o.Fill.IsImage && o.BackgroundBlur > 0)
            {
                // Render the blurred background separately so the photo stays sharp.
                var bg = new DrawingVisual();
                using (var bdc = bg.RenderOpen())
                    bdc.DrawRectangle(o.Fill.Create(new Rect(canvas)), null, new Rect(canvas));
                bg.Effect = new BlurEffect { Radius = o.BackgroundBlur, KernelType = KernelType.Gaussian, RenderingBias = RenderingBias.Quality };
                var blurred = ImageLoader.Render(bg, (int)canvas.Width, (int)canvas.Height);
                dc.DrawImage(blurred, new Rect(canvas));
                var noFill = o with { Fill = new BackgroundFill("none", _ => Brushes.Transparent) };
                dc.DrawDrawing(Compose(photo, noFill).Drawing);
            }
            else
            {
                dc.DrawDrawing(Compose(photo, o).Drawing);
            }
        }
        return ImageLoader.Render(root, (int)canvas.Width, (int)canvas.Height);
    }
}

public enum CollageLayout
{
    Grid,
    Row,
    Column,
    Featured,
}

public sealed record CollageOptions(
    CollageLayout Layout,
    int Columns,
    double AspectRatio,
    int Width,
    bool FillCells,
    double Spacing,
    double Corners,
    Color Background);

/// <summary>Arranges several images into one picture.</summary>
public static class CollageComposer
{
    public static IReadOnlyList<Rect> Cells(int count, CollageOptions o, Size canvas)
    {
        var cells = new List<Rect>();
        double s = o.Spacing;
        switch (o.Layout)
        {
            case CollageLayout.Row:
            {
                double w = (canvas.Width - s * (count + 1)) / count;
                for (int i = 0; i < count; i++) cells.Add(new Rect(s + i * (w + s), s, w, canvas.Height - 2 * s));
                break;
            }
            case CollageLayout.Column:
            {
                double h = (canvas.Height - s * (count + 1)) / count;
                for (int i = 0; i < count; i++) cells.Add(new Rect(s, s + i * (h + s), canvas.Width - 2 * s, h));
                break;
            }
            case CollageLayout.Featured when count >= 2:
            {
                // One large image on the left, the rest stacked on the right.
                double bigW = (canvas.Width - 3 * s) * 0.62;
                cells.Add(new Rect(s, s, bigW, canvas.Height - 2 * s));
                int rest = count - 1;
                double h = (canvas.Height - s * (rest + 1)) / rest;
                for (int i = 0; i < rest; i++) cells.Add(new Rect(2 * s + bigW, s + i * (h + s), canvas.Width - 3 * s - bigW, h));
                break;
            }
            default:
            {
                int cols = Math.Clamp(o.Columns, 1, count);
                int rows = (int)Math.Ceiling(count / (double)cols);
                double w = (canvas.Width - s * (cols + 1)) / cols, h = (canvas.Height - s * (rows + 1)) / rows;
                for (int i = 0; i < count; i++)
                    cells.Add(new Rect(s + i % cols * (w + s), s + i / cols * (h + s), w, h));
                break;
            }
        }
        return cells;
    }

    public static Size CanvasSize(CollageOptions o) => new(o.Width, Math.Round(o.Width / o.AspectRatio));

    public static DrawingVisual Compose(IReadOnlyList<BitmapSource> images, CollageOptions o, double scale = 1)
    {
        var canvas = CanvasSize(o);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(scale, scale));
            dc.DrawRectangle(new SolidColorBrush(o.Background), null, new Rect(canvas));
            var cells = Cells(images.Count, o, canvas);
            for (int i = 0; i < images.Count; i++)
            {
                var cell = cells[i];
                var img = images[i];
                double sx = cell.Width / img.PixelWidth, sy = cell.Height / img.PixelHeight;
                double k = o.FillCells ? Math.Max(sx, sy) : Math.Min(sx, sy);
                double w = img.PixelWidth * k, h = img.PixelHeight * k;
                var target = new Rect(cell.X + (cell.Width - w) / 2, cell.Y + (cell.Height - h) / 2, w, h);
                var clip = o.FillCells ? cell : target;
                dc.PushClip(new RectangleGeometry(clip, o.Corners, o.Corners));
                dc.DrawImage(img, target);
                dc.Pop();
            }
            dc.Pop();
        }
        return visual;
    }

    public static BitmapSource Render(IReadOnlyList<BitmapSource> images, CollageOptions o)
    {
        var size = CanvasSize(o);
        return ImageLoader.Render(Compose(images, o), (int)size.Width, (int)size.Height);
    }
}
