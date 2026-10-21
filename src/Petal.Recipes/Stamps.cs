using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Petal.Recipes;

/// <summary>Nine places on a picture or page.</summary>
public enum Place9
{
    TopLeft,
    Top,
    TopRight,
    Left,
    Center,
    Right,
    BottomLeft,
    Bottom,
    BottomRight,
}

/// <summary>A watermark or signature: text or a picture, placed once or repeated across.</summary>
public sealed record StampSpec
{
    public string Text { get; init; } = "";

    /// <summary>A frozen picture (a logo or a signature); used instead of the text when set.</summary>
    public BitmapSource? Picture { get; init; }

    public Place9 Place { get; init; } = Place9.BottomRight;

    /// <summary>Width of the mark as a fraction of the width it is drawn on.</summary>
    public double Size { get; init; } = 0.25;

    public double Opacity { get; init; } = 0.6;
    public Color Color { get; init; } = Colors.White;

    /// <summary>Repeat the mark diagonally over the whole area.</summary>
    public bool Tiled { get; init; }

    public bool IsEmpty => Picture is null && string.IsNullOrWhiteSpace(Text);
}

/// <summary>
/// Draws a <see cref="StampSpec"/> onto a transparent canvas of any size. WPF lays the text out,
/// so Arabic is shaped and ordered properly, then the engines composite the PNG.
/// </summary>
public static class StampRenderer
{
    private static readonly FontFamily DisplayFont = new("Segoe UI Variable Display, Segoe UI");

    /// <summary>Renders on an STA thread (WPF's requirement), whichever thread asks.</summary>
    /// <param name="maxSide">Draw smaller than asked when larger than this (the caller scales up); 0 for exact size.</param>
    public static byte[] RenderPng(int width, int height, StampSpec spec, int maxSide = 0)
    {
        double scale = maxSide > 0 && Math.Max(width, height) > maxSide ? (double)maxSide / Math.Max(width, height) : 1;
        int w = Math.Max(1, (int)Math.Round(width * scale)), h = Math.Max(1, (int)Math.Round(height * scale));
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
            return Encode(Render(w, h, spec));
        byte[]? result = null;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { result = Encode(Render(w, h, spec)); }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        thread.Join();
        if (error is not null)
            throw new InvalidOperationException("The watermark could not be drawn.", error);
        return result!;
    }

    public static BitmapSource Render(int width, int height, StampSpec spec)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            if (!spec.IsEmpty)
            {
                var mark = Mark(spec, width, height);
                var size = mark.Bounds.Size;
                dc.PushOpacity(Math.Clamp(spec.Opacity, 0, 1));
                if (spec.Tiled)
                {
                    // Rows of marks turned 30°, covering the corners too.
                    dc.PushTransform(new RotateTransform(-30, width / 2.0, height / 2.0));
                    double reach = Math.Sqrt(width * (double)width + height * (double)height);
                    double stepX = size.Width * 1.7, stepY = Math.Max(size.Height * 3.2, reach / 9);
                    int row = 0;
                    for (double y = height / 2.0 - reach; y < height / 2.0 + reach; y += stepY, row++)
                    {
                        double shift = row % 2 == 0 ? 0 : stepX / 2;
                        for (double x = width / 2.0 - reach + shift; x < width / 2.0 + reach; x += stepX)
                            Draw(dc, mark, x, y);
                    }
                    dc.Pop();
                }
                else
                {
                    double margin = Math.Min(width, height) * 0.035;
                    var (x, y) = Position(spec.Place, width, height, size, margin);
                    Draw(dc, mark, x, y);
                }
                dc.Pop();
            }
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>Top-left corner of a mark of <paramref name="size"/> at a place.</summary>
    public static (double X, double Y) Position(Place9 place, double width, double height, Size size, double margin)
    {
        int col = (int)place % 3, row = (int)place / 3;
        double x = col switch { 0 => margin, 1 => (width - size.Width) / 2, _ => width - size.Width - margin };
        double y = row switch { 0 => margin, 1 => (height - size.Height) / 2, _ => height - size.Height - margin };
        return (x, y);
    }

    private static void Draw(DrawingContext dc, Drawing mark, double x, double y)
    {
        dc.PushTransform(new TranslateTransform(x - mark.Bounds.X, y - mark.Bounds.Y));
        dc.DrawDrawing(mark);
        dc.Pop();
    }

    /// <summary>The mark at its final size, with its own origin.</summary>
    private static Drawing Mark(StampSpec spec, int width, int height)
    {
        double targetWidth = Math.Max(4, width * Math.Clamp(spec.Size, 0.01, 1));
        double maxHeight = height * (spec.Tiled ? 0.3 : 0.5);
        if (spec.Picture is { } picture)
        {
            double aspect = picture.PixelHeight / (double)Math.Max(1, picture.PixelWidth);
            double w = targetWidth, h = w * aspect;
            if (h > maxHeight)
                (w, h) = (maxHeight / aspect, maxHeight);
            var image = new ImageDrawing(picture, new Rect(0, 0, w, h));
            image.Freeze();
            return image;
        }

        bool rtl = spec.Text.Any(c => c is >= '\u0590' and <= '\u08FF' or >= '\uFB1D' and <= '\uFEFC');
        var text = new FormattedText(spec.Text.Trim(), CultureInfo.CurrentUICulture, rtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight,
            new Typeface(DisplayFont, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal), 100, Brushes.Black, 1.0);
        var geometry = text.BuildGeometry(new Point(0, 0));
        var bounds = geometry.Bounds;
        if (bounds.IsEmpty || bounds.Width <= 0)
            return new DrawingGroup();
        double scale = Math.Min(targetWidth / bounds.Width, maxHeight / Math.Max(1, bounds.Height));
        var place = new TransformGroup();
        place.Children.Add(new TranslateTransform(-bounds.X, -bounds.Y));
        place.Children.Add(new ScaleTransform(scale, scale));
        geometry.Transform = place;
        geometry.Freeze();

        // A soft shadow keeps light text readable on light pictures and vice versa.
        bool light = spec.Color.R * 0.299 + spec.Color.G * 0.587 + spec.Color.B * 0.114 > 140;
        var shadowGeometry = geometry.Clone();
        double offset = Math.Max(1, bounds.Height * scale * 0.04);
        shadowGeometry.Transform = new TransformGroup { Children = { place, new TranslateTransform(offset, offset) } };
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(new SolidColorBrush(light ? Color.FromArgb(90, 0, 0, 0) : Color.FromArgb(90, 255, 255, 255)), null, shadowGeometry));
        group.Children.Add(new GeometryDrawing(new SolidColorBrush(spec.Color), null, geometry));
        group.Freeze();
        return group;
    }

    private static byte[] Encode(BitmapSource bitmap)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }
}
