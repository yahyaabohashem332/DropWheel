using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Petal.App.Hud;
using Petal.App.Input;
using Petal.App.Localization;
using Petal.App.Tools;
using Petal.App.Tools.Windows;
using Petal.App.Visuals;
using static Petal.App.Interop.NativeMethods;

namespace Petal.App.Screen;

/// <summary>
/// Picks a colour from anywhere on the screen: a loupe shows the pixels around the pointer, the arrow
/// keys move one pixel, and a click copies the colour as HEX and opens its card (RGB, HSL, and how well it
/// reads against the colour picked before). Esc cancels.
/// </summary>
internal sealed class ColourPicker : MonitorWindow
{
    private const int Cells = 15;
    private const double Loupe = 150;
    private static Color? _previous;
    private readonly BitmapSource _still;
    private readonly byte[] _pixels;
    private readonly ImageBrush _zoom;
    private readonly Canvas _canvas = new();
    private readonly Grid _loupe;
    private readonly Border _swatch;
    private readonly TextBlock _hex, _rgb;

    public static void Start()
    {
        var monitor = ScreenNative.UnderPointer();
        if (ScreenUi.Still(monitor.Bounds) is not { } still)
            return;
        new ColourPicker(monitor.Bounds, still).Show();
    }

    private ColourPicker(RECT bounds, BitmapSource still) : base(bounds)
    {
        _still = still;
        _pixels = new byte[still.PixelWidth * still.PixelHeight * 4];
        still.CopyPixels(_pixels, still.PixelWidth * 4, 0);
        Title = L.T("Pick a Colour");
        Cursor = Cursors.None;
        Background = new ImageBrush(still) { Stretch = Stretch.Fill };
        _zoom = new ImageBrush(still) { ViewboxUnits = BrushMappingMode.Absolute, Stretch = Stretch.Fill };
        RenderOptions.SetBitmapScalingMode(_zoom, BitmapScalingMode.NearestNeighbor);
        var lens = new Rectangle { Width = Loupe, Height = Loupe, Fill = _zoom };
        RenderOptions.SetBitmapScalingMode(lens, BitmapScalingMode.NearestNeighbor);
        double cell = Loupe / Cells;
        var grid = new StreamGeometry();
        using (var g = grid.Open())
            for (int i = 1; i < Cells; i++)
            {
                g.BeginFigure(new Point(i * cell, 0), false, false);
                g.LineTo(new Point(i * cell, Loupe), true, false);
                g.BeginFigure(new Point(0, i * cell), false, false);
                g.LineTo(new Point(Loupe, i * cell), true, false);
            }
        var lines = new System.Windows.Shapes.Path { Data = grid, Stroke = new SolidColorBrush(Color.FromArgb(0x30, 0x80, 0x80, 0x80)), StrokeThickness = 0.6 };
        var centre = new Rectangle { Width = cell + 2, Height = cell + 2, Stroke = Brushes.White, StrokeThickness = 1.5, Margin = new Thickness(Cells / 2 * cell - 1, Cells / 2 * cell - 1, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        var round = new Grid { Width = Loupe, Height = Loupe, Clip = new EllipseGeometry(new Point(Loupe / 2, Loupe / 2), Loupe / 2, Loupe / 2), Children = { lens, lines, centre } };
        var ring = new Ellipse { Width = Loupe + 4, Height = Loupe + 4, Stroke = Brushes.White, StrokeThickness = 2, Margin = new Thickness(-2) };
        _swatch = new Border { Width = 14, Height = 14, CornerRadius = new CornerRadius(4), BorderBrush = new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 6, 0) };
        _hex = new TextBlock { Foreground = Brushes.White, FontWeight = FontWeights.SemiBold, FontSize = 13, FontFamily = UiFonts.Display };
        _rgb = new TextBlock { Foreground = new SolidColorBrush(Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF)), FontSize = 11, Margin = new Thickness(0, 1, 0, 0), FontFamily = UiFonts.Text };
        var label = ScreenUi.Strip(new StackPanel
        {
            Children =
            {
                new StackPanel { Orientation = Orientation.Horizontal, Children = { _swatch, _hex } },
                _rgb,
            },
        }, 10);
        label.Padding = new Thickness(10, 6, 10, 7);
        label.HorizontalAlignment = HorizontalAlignment.Center;
        label.Margin = new Thickness(0, Loupe + 10, 0, 0);
        _loupe = new Grid { Width = Loupe + 80, IsHitTestVisible = false, Children = { new Grid { Width = Loupe, Height = Loupe, VerticalAlignment = VerticalAlignment.Top, Children = { round, ring } }, label } };
        _canvas.Children.Add(_loupe);
        var hint = ScreenUi.Strip(new TextBlock { Text = L.T("Click to copy the colour · Arrow keys move a pixel · Esc cancels"), Foreground = Brushes.White, FontSize = 13, FlowDirection = L.Direction }, 10);
        hint.Padding = new Thickness(14, 7, 14, 8);
        hint.HorizontalAlignment = HorizontalAlignment.Center;
        hint.VerticalAlignment = VerticalAlignment.Top;
        hint.Margin = new Thickness(0, 24, 0, 0);
        hint.IsHitTestVisible = false;
        Content = new Grid { Children = { hint, _canvas } };
        Loaded += (_, _) =>
        {
            Activate();
            Focus();
            GetCursorPos(out var p);
            Follow(new ScreenPoint(p.X, p.Y));
        };
    }

    private (int X, int Y) PixelAt(ScreenPoint p) =>
        (Math.Clamp(p.X - Bounds.Left, 0, _still.PixelWidth - 1), Math.Clamp(p.Y - Bounds.Top, 0, _still.PixelHeight - 1));

    private Color ColourAt(int x, int y)
    {
        int i = (y * _still.PixelWidth + x) * 4;
        return Color.FromRgb(_pixels[i + 2], _pixels[i + 1], _pixels[i]);
    }

    private void Follow(ScreenPoint at)
    {
        var (x, y) = PixelAt(at);
        _zoom.Viewbox = new Rect(x - Cells / 2, y - Cells / 2, Cells, Cells);
        var colour = ColourAt(x, y);
        _swatch.Background = new SolidColorBrush(colour);
        _hex.Text = ColourCard.Hex(colour);
        _rgb.Text = $"{colour.R}, {colour.G}, {colour.B}";
        var local = ToLocal(at);
        // Beside the pointer, on whichever side has room.
        double left = local.X + 24, top = local.Y + 24;
        if (left + _loupe.Width > ActualWidth) left = local.X - 24 - _loupe.Width;
        if (top + Loupe + 60 > ActualHeight) top = local.Y - 24 - Loupe - 60;
        Canvas.SetLeft(_loupe, left);
        Canvas.SetTop(_loupe, top);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        GetCursorPos(out var p);
        Follow(new ScreenPoint(p.X, p.Y));
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        GetCursorPos(out var p);
        Pick(new ScreenPoint(p.X, p.Y));
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        Close();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        GetCursorPos(out var p);
        int step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 10 : 1;
        switch (e.Key)
        {
            case Key.Escape: Close(); return;
            case Key.Return or Key.Space: Pick(new ScreenPoint(p.X, p.Y)); return;
            case Key.Left: p.X -= step; break;
            case Key.Right: p.X += step; break;
            case Key.Up: p.Y -= step; break;
            case Key.Down: p.Y += step; break;
            default: return;
        }
        e.Handled = true;
        ScreenNative.SetCursorPos(p.X, p.Y);
        Follow(new ScreenPoint(p.X, p.Y));
    }

    private void Pick(ScreenPoint at)
    {
        var (x, y) = PixelAt(at);
        var colour = ColourAt(x, y);
        Close();
        try { Clipboard.SetText(ColourCard.Hex(colour)); } catch (System.Runtime.InteropServices.ExternalException) { }
        var card = new ColourCard(colour, _previous);
        _previous = colour;
        card.ShowNear(at);
        card.Activate();
    }
}

/// <summary>A picked colour in every form, with copy buttons and its contrast against the colour picked before.</summary>
internal sealed class ColourCard : ToolWindow
{
    public static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    public ColourCard(Color colour, Color? previous) : base("Colour", 360)
    {
        var swatch = new Border { Height = 64, CornerRadius = new CornerRadius(10), Background = new SolidColorBrush(colour) };
        swatch.SetResourceReference(Border.BorderBrushProperty, "Petal.Border");
        swatch.BorderThickness = new Thickness(1);
        Body.Children.Add(swatch);
        var (h, s, l) = Hsl(colour);
        Line("HEX", Hex(colour));
        Line("RGB", $"rgb({colour.R}, {colour.G}, {colour.B})");
        Line("HSL", string.Create(CultureInfo.InvariantCulture, $"hsl({h:0}, {s:0}%, {l:0}%)"));
        Line("WPF / C#", $"Color.FromRgb({colour.R}, {colour.G}, {colour.B})");
        if (previous is { } before && before != colour)
        {
            double ratio = Contrast(colour, before);
            var pair = new Border { Height = 46, CornerRadius = new CornerRadius(10), Background = new SolidColorBrush(before), Margin = new Thickness(0, 16, 0, 0) };
            pair.Child = new TextBlock
            {
                Text = L.T("Sample text Aa نص"),
                Foreground = new SolidColorBrush(colour),
                FontSize = 18,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Body.Children.Add(pair);
            string verdict = ratio >= 7 ? "Very easy to read (AAA)" : ratio >= 4.5 ? "Easy to read (AA)" : ratio >= 3 ? "Only for large text (AA large)" : "Hard to read";
            Body.Children.Add(Text(L.F("Against {0}: {1}:1 · {2}", Hex(before), ratio.ToString("0.0#", CultureInfo.InvariantCulture), L.T(verdict)), "Petal.Caption").WithMarginT(6));
        }
        PrimaryButton.Content = L.T("Pick Another");
        PrimaryButton.Click += async (_, _) =>
        {
            Close();
            await Task.Delay(150);
            ColourPicker.Start();
        };
        FooterNote = "HEX copied";
    }

    private void Line(string name, string value)
    {
        var copy = IconButton("copyPlain", "Copy", 14);
        copy.Click += (_, _) =>
        {
            try { Clipboard.SetText(value); } catch (System.Runtime.InteropServices.ExternalException) { }
            FooterNote = L.F("{0} copied", name);
        };
        var row = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        DockPanel.SetDock(copy, Dock.Right);
        row.Children.Add(copy);
        var label = Text(name, "Petal.Caption");
        label.Width = 70;
        label.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(label, Dock.Left);
        row.Children.Add(label);
        var text = new TextBox { Text = value, IsReadOnly = true, FlowDirection = FlowDirection.LeftToRight, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(text);
        Body.Children.Add(row);
    }

    internal static (double H, double S, double L) Hsl(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        double l = (max + min) / 2, h = 0, s = 0;
        if (max != min)
        {
            double d = max - min;
            s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
            h = max == r ? (g - b) / d + (g < b ? 6 : 0) : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
            h *= 60;
        }
        return (h, s * 100, l * 100);
    }

    /// <summary>WCAG contrast ratio, 1 to 21.</summary>
    internal static double Contrast(Color a, Color b)
    {
        static double Lum(Color c)
        {
            static double Channel(byte v)
            {
                double x = v / 255.0;
                return x <= 0.03928 ? x / 12.92 : Math.Pow((x + 0.055) / 1.055, 2.4);
            }
            return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
        }
        double la = Lum(a), lb = Lum(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }
}
