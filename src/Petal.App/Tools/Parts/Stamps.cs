using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Petal.App.Localization;
using Petal.App.Visuals;

namespace Petal.App.Tools.Parts;

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

        bool rtl = spec.Text.Any(c => c is >= '֐' and <= 'ࣿ' or >= 'יִ' and <= 'ﻼ');
        var text = new FormattedText(spec.Text.Trim(), CultureInfo.CurrentUICulture, rtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight,
            new Typeface(UiFonts.Display, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal), 100, Brushes.Black, 1.0);
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

/// <summary>A 3 × 3 grid of squares for choosing where a mark goes. Laid out as the picture is, in any language.</summary>
public sealed class PlacePicker : UniformGrid
{
    private readonly List<ToggleButton> _cells = [];
    private Place9 _value;

    public event Action<Place9>? Changed;

    public PlacePicker(Place9 value)
    {
        Rows = 3;
        Columns = 3;
        Width = 66;
        Height = 50;
        FlowDirection = FlowDirection.LeftToRight;
        HorizontalAlignment = HorizontalAlignment.Left;
        for (int i = 0; i < 9; i++)
        {
            var place = (Place9)i;
            var cell = new ToggleButton
            {
                Margin = new Thickness(1.5),
                Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = L.T(Name(place)),
                Template = CellTemplate(),
            };
            cell.Click += (_, _) => Value = place;
            _cells.Add(cell);
            Children.Add(cell);
        }
        Value = value;
    }

    public Place9 Value
    {
        get => _value;
        set
        {
            _value = value;
            for (int i = 0; i < 9; i++)
                _cells[i].IsChecked = i == (int)value;
            Changed?.Invoke(value);
        }
    }

    public static string Name(Place9 place) => place switch
    {
        Place9.TopLeft => "Top left",
        Place9.Top => "Top",
        Place9.TopRight => "Top right",
        Place9.Left => "Left",
        Place9.Center => "Centre",
        Place9.Right => "Right",
        Place9.BottomLeft => "Bottom left",
        Place9.Bottom => "Bottom",
        _ => "Bottom right",
    };

    private static ControlTemplate CellTemplate()
    {
        var border = new FrameworkElementFactory(typeof(Border)) { Name = "Cell" };
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(3));
        border.SetResourceReference(Border.BackgroundProperty, "Petal.Control");
        var template = new ControlTemplate(typeof(ToggleButton)) { VisualTree = border };
        var on = new Trigger { Property = ToggleButton.IsCheckedProperty, Value = true };
        on.Setters.Add(new Setter { TargetName = "Cell", Property = Border.BackgroundProperty, Value = new DynamicResourceExtension("Petal.Accent") });
        template.Triggers.Add(on);
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter { TargetName = "Cell", Property = UIElement.OpacityProperty, Value = 0.8 });
        template.Triggers.Add(hover);
        return template;
    }
}

/// <summary>A password box with a button that shows what was typed.</summary>
public sealed class PasswordField : Grid
{
    private readonly PasswordBox _hidden = new() { FlowDirection = FlowDirection.LeftToRight };
    private readonly TextBox _shown = new() { FlowDirection = FlowDirection.LeftToRight, Visibility = Visibility.Collapsed };
    private bool _syncing;

    public event Action? Changed;

    public PasswordField()
    {
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Children.Add(_hidden);
        Children.Add(_shown);
        var eye = new Button
        {
            Style = (Style)Application.Current.FindResource("Petal.IconButton"),
            Margin = new Thickness(4, 0, 0, 0),
            ToolTip = L.T("Show password"),
            Content = ToolWindow.ThemedIcon("eyeOff", 15),
        };
        SetColumn(eye, 1);
        Children.Add(eye);
        eye.Click += (_, _) => Reveal(_shown.Visibility != Visibility.Visible);
        _hidden.PasswordChanged += (_, _) =>
        {
            if (_syncing) return;
            _syncing = true;
            _shown.Text = _hidden.Password;
            _syncing = false;
            Changed?.Invoke();
        };
        _shown.TextChanged += (_, _) =>
        {
            if (_syncing) return;
            _syncing = true;
            _hidden.Password = _shown.Text;
            _syncing = false;
            Changed?.Invoke();
        };
    }

    public string Password => _hidden.Password;

    public void FocusInput()
    {
        if (_shown.IsVisible) _shown.Focus();
        else _hidden.Focus();
    }

    private void Reveal(bool show)
    {
        _shown.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        _hidden.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        FocusInput();
    }
}

/// <summary>A still of any file Petal can preview: the picture, a video frame or a PDF's first page.</summary>
public static class PreviewSource
{
    public static async Task<BitmapSource?> LoadAsync(string path, int maxWidth = 900)
    {
        var format = Petal.Core.Formats.Formats.FromPath(path);
        switch (format?.Family)
        {
            case Petal.Core.Formats.FormatFamily.Video:
                var frame = await Petal.Engines.Media.MediaPreview.ExtractFrameAsync(path, TimeSpan.FromSeconds(1), maxWidth);
                return Decode(frame);
            case Petal.Core.Formats.FormatFamily.Pdf:
                var page = await Task.Run(() => Petal.Engines.Documents.Pdf.PdfInfo.RenderPagePng(path, 0, maxWidth));
                return Decode(page);
            default:
                return await ImageLoader.LoadAsync(path, maxWidth);
        }
    }

    private static BitmapSource Decode(byte[] bytes)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = new MemoryStream(bytes);
        image.EndInit();
        image.Freeze();
        return image;
    }
}
