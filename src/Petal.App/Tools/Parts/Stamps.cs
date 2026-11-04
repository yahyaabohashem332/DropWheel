using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Petal.App.Localization;
using Petal.App.Visuals;
using Petal.Recipes;

namespace Petal.App.Tools.Parts;

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

/// <summary>A signature as a tightly cropped transparent picture, from strokes on a pad or a typed name.</summary>
public static class Signatures
{
    public static readonly Color Ink = Color.FromRgb(0x1D, 0x2B, 0x6B);

    public static BitmapSource Render(System.Windows.Ink.StrokeCollection strokes)
    {
        var bounds = strokes.GetBounds();
        bounds.Inflate(4, 4);
        const double scale = 3;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(scale, scale));
            dc.PushTransform(new TranslateTransform(-bounds.X, -bounds.Y));
            strokes.Draw(dc);
        }
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(bounds.Width * scale), (int)Math.Ceiling(bounds.Height * scale), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    public static BitmapSource Render(string name)
    {
        // Handwriting-style fonts that come with Windows, in order of preference.
        var family = new[] { "Segoe Script", "Ink Free", "Segoe Print" }.FirstOrDefault(f => Fonts.SystemFontFamilies.Any(s => s.Source == f)) ?? "Segoe UI";
        bool rtl = name.Any(c => c is >= '\u0590' and <= '\u08FF');
        var text = new FormattedText(name, CultureInfo.CurrentUICulture, rtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight,
            new Typeface(new FontFamily(rtl ? "Segoe UI" : family), FontStyles.Italic, FontWeights.Normal, FontStretches.Normal), 120, new SolidColorBrush(Ink), 1.0);
        var geometry = text.BuildGeometry(new Point(0, 0));
        var b = geometry.Bounds;
        b.Inflate(6, 6);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new TranslateTransform(-b.X, -b.Y));
            dc.DrawGeometry(new SolidColorBrush(Ink), null, geometry);
        }
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(b.Width), (int)Math.Ceiling(b.Height), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
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
