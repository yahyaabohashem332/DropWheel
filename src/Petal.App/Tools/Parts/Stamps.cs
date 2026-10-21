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
