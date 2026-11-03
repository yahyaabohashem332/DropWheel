using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using Petal.App.Localization;
using Petal.App.Screen;
using Petal.App.Settings;
using Petal.App.Visuals;

namespace Petal.App.Tools.Parts;

/// <summary>Colours as hue (0 to 360), saturation and brightness (0 to 1), and as hex text.</summary>
public static class ColourMath
{
    public static (double Hue, double Saturation, double Brightness) ToHsv(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), range = max - min;
        double hue = range == 0 ? 0
            : max == r ? 60 * ((g - b) / range % 6)
            : max == g ? 60 * ((b - r) / range + 2)
            : 60 * ((r - g) / range + 4);
        return (hue < 0 ? hue + 360 : hue, max == 0 ? 0 : range / max, max);
    }

    public static Color FromHsv(double hue, double saturation, double brightness)
    {
        hue = (hue % 360 + 360) % 360;
        double chroma = brightness * saturation;
        double x = chroma * (1 - Math.Abs(hue / 60 % 2 - 1));
        double m = brightness - chroma;
        var (r, g, b) = (int)(hue / 60) switch
        {
            0 => (chroma, x, 0.0),
            1 => (x, chroma, 0.0),
            2 => (0.0, chroma, x),
            3 => (0.0, x, chroma),
            4 => (x, 0.0, chroma),
            _ => (chroma, 0.0, x),
        };
        return Color.FromRgb(Byte(r + m), Byte(g + m), Byte(b + m));
    }

    private static byte Byte(double value) => (byte)Math.Clamp(Math.Round(value * 255), 0, 255);

    public static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    /// <summary>Reads "#RRGGBB" or "#RGB", with or without the #; nothing else counts as a hex colour.</summary>
    public static bool TryParseHex(string? text, out Color color)
    {
        color = default;
        var t = text?.Trim().TrimStart('#') ?? "";
        if (t.Length == 3)
            t = string.Concat(t[0], t[0], t[1], t[1], t[2], t[2]);
        if (t.Length != 6 || !int.TryParse(t, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int value))
            return false;
        color = Color.FromRgb((byte)(value >> 16), (byte)(value >> 8), (byte)value);
        return true;
    }

    /// <summary>White or near-black, whichever reads better on the colour.</summary>
    public static Color Readable(Color on) =>
        (0.2126 * Linear(on.R) + 0.7152 * Linear(on.G) + 0.0722 * Linear(on.B)) > 0.45 ? Color.FromRgb(0x16, 0x16, 0x1A) : Colors.White;

    private static double Linear(byte channel)
    {
        double c = channel / 255.0;
        return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }
}

/// <summary>
/// Any colour, the way design programs offer it: a field of saturation and brightness, a hue strip, the hex code
/// and red, green and blue to type, the colour before beside the new one, and swatches with the colours used lately.
/// </summary>
public sealed class ColourPanel : StackPanel
{
    private const double FieldWidth = 240, FieldHeight = 150, StripHeight = 14, MostRecent = 8;

    private readonly Grid _field = new() { Width = FieldWidth, Height = FieldHeight, Cursor = Cursors.Cross, ClipToBounds = true };
    private readonly Rectangle _hueFill = new();
    private readonly Canvas _fieldMarks = new() { IsHitTestVisible = false };
    private readonly Ellipse _fieldThumb = new() { Width = 14, Height = 14, Stroke = Brushes.White, StrokeThickness = 2, Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 3, ShadowDepth = 0, Opacity = 0.8 } };
    private readonly Grid _strip = new() { Width = FieldWidth, Height = StripHeight, Cursor = Cursors.SizeWE, Margin = new Thickness(0, 10, 0, 0) };
    private readonly Canvas _stripMarks = new() { IsHitTestVisible = false };
    private readonly Border _stripThumb = new() { Width = 8, Height = StripHeight + 4, CornerRadius = new CornerRadius(3), BorderBrush = Brushes.White, BorderThickness = new Thickness(2), Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 3, ShadowDepth = 0, Opacity = 0.8 } };
    private readonly Border _before = new() { Width = 26, Height = 30, CornerRadius = new CornerRadius(6, 0, 0, 6) };
    private readonly Border _after = new() { Width = 26, Height = 30, CornerRadius = new CornerRadius(0, 6, 6, 0) };
    private readonly TextBox _hex = new() { Width = FieldWidth - 60, VerticalContentAlignment = VerticalAlignment.Center, FontFamily = new FontFamily("Cascadia Mono, Consolas"), MaxLength = 7 };
    private readonly TextBox _red = Channel(), _green = Channel(), _blue = Channel();
    private readonly WrapPanel _recent = new();

    private double _hue, _saturation, _brightness;
    private Color _colour;
    private bool _writing;

    /// <summary>While the colour is being chosen: on every move of the pointer and every valid value typed.</summary>
    public event Action<Color>? Changing;

    /// <summary>The colour has settled: the pointer was let go, Enter was pressed or a swatch was chosen.</summary>
    public event Action<Color>? Settled;

    public ColourPanel(Color initial, IReadOnlyList<Color>? swatches = null)
    {
        FlowDirection = FlowDirection.LeftToRight;
        BuildField();
        BuildStrip();
        Children.Add(_field);
        Children.Add(_strip);
        Children.Add(BuildValues());
        if (swatches is { Count: > 0 })
            Children.Add(SwatchRow(swatches, top: 10));
        _recent.Margin = new Thickness(0, 8, 0, 0);
        Children.Add(_recent);
        ShowRecent();
        _before.Background = Palette.Brush(initial);
        _before.MouseLeftButtonUp += (_, _) => { SetColour(initial); Settle(); };
        _before.Cursor = Cursors.Hand;
        _before.ToolTip = L.T("The colour before");
        Colour = initial;
    }

    public Color Colour
    {
        get => _colour;
        set => SetColour(value);
    }

    private static TextBox Channel() => new() { Width = (FieldWidth - 12) / 3, TextAlignment = TextAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center, MaxLength = 3 };

    private void BuildField()
    {
        var towardsWhite = new Rectangle
        {
            Fill = new LinearGradientBrush(Colors.White, Color.FromArgb(0, 255, 255, 255), new Point(0, 0), new Point(1, 0)),
        };
        var towardsBlack = new Rectangle
        {
            Fill = new LinearGradientBrush(Color.FromArgb(0, 0, 0, 0), Colors.Black, new Point(0, 0), new Point(0, 1)),
        };
        _field.Children.Add(_hueFill);
        _field.Children.Add(towardsWhite);
        _field.Children.Add(towardsBlack);
        _fieldMarks.Children.Add(_fieldThumb);
        _field.Children.Add(_fieldMarks);
        Draggable(_field, at =>
        {
            _saturation = Math.Clamp(at.X / FieldWidth, 0, 1);
            _brightness = 1 - Math.Clamp(at.Y / FieldHeight, 0, 1);
            FromPicker();
        });
    }

    private void BuildStrip()
    {
        var stops = new GradientStopCollection();
        for (int i = 0; i <= 6; i++)
            stops.Add(new GradientStop(ColourMath.FromHsv(i * 60 % 360, 1, 1), i / 6.0));
        _strip.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(4),
            Background = new LinearGradientBrush(stops, new Point(0, 0), new Point(1, 0)),
        });
        _stripMarks.Children.Add(_stripThumb);
        _strip.Children.Add(_stripMarks);
        Draggable(_strip, at =>
        {
            _hue = Math.Clamp(at.X / FieldWidth, 0, 1) * 359.99;
            FromPicker();
        });
    }

    private UIElement BuildValues()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        row.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { _before, _after }, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Bottom });
        row.Children.Add(Labelled("HEX", _hex, 0));
        var channels = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        channels.Children.Add(Labelled("R", _red, 6));
        channels.Children.Add(Labelled("G", _green, 6));
        channels.Children.Add(Labelled("B", _blue, 0));
        foreach (var box in new[] { _hex, _red, _green, _blue })
        {
            box.GotKeyboardFocus += (_, _) => box.SelectAll();
            box.LostKeyboardFocus += (_, _) => WriteValues(force: true);
            box.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter)
                {
                    e.Handled = true;
                    Settle();
                }
            };
        }
        _hex.TextChanged += (_, _) =>
        {
            if (!_writing && ColourMath.TryParseHex(_hex.Text, out var typed))
                FromTyping(typed, _hex);
        };
        foreach (var box in new[] { _red, _green, _blue })
        {
            box.TextChanged += (_, _) =>
            {
                if (!_writing && Channels() is { } typed)
                    FromTyping(typed, box);
            };
            box.PreviewKeyDown += (_, e) =>
            {
                if (e.Key is not (Key.Up or Key.Down) || !byte.TryParse(box.Text, out var value))
                    return;
                int step = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? 10 : 1;
                box.Text = Math.Clamp(value + (e.Key == Key.Up ? step : -step), 0, 255).ToString(CultureInfo.InvariantCulture);
                box.SelectAll();
                e.Handled = true;
            };
        }
        return new StackPanel { Children = { row, channels } };
    }

    private static StackPanel Labelled(string label, TextBox box, double gap)
    {
        var caption = new TextBlock { Text = label, FontSize = 10, Margin = new Thickness(2, 0, 0, 2) };
        caption.SetResourceReference(TextBlock.ForegroundProperty, "Petal.TextSecondary");
        return new StackPanel { Margin = new Thickness(0, 0, gap, 0), Children = { caption, box } };
    }

    private Color? Channels() =>
        byte.TryParse(_red.Text, out var r) && byte.TryParse(_green.Text, out var g) && byte.TryParse(_blue.Text, out var b) ? Color.FromRgb(r, g, b) : null;

    private WrapPanel SwatchRow(IEnumerable<Color> colours, double top)
    {
        var row = new WrapPanel { Margin = new Thickness(0, top, 0, 0), MaxWidth = FieldWidth };
        foreach (var colour in colours)
        {
            var swatch = new Border
            {
                Width = 20,
                Height = 20,
                Margin = new Thickness(0, 0, 4, 4),
                CornerRadius = new CornerRadius(5),
                Background = Palette.Brush(colour),
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
                ToolTip = ColourMath.Hex(colour),
            };
            swatch.SetResourceReference(Border.BorderBrushProperty, "Petal.ControlBorder");
            swatch.MouseLeftButtonUp += (_, _) =>
            {
                SetColour(colour);
                Changing?.Invoke(colour);
                Settle();
            };
            row.Children.Add(swatch);
        }
        return row;
    }

    private void ShowRecent()
    {
        _recent.Children.Clear();
        var colours = SettingsStore.Current.RecentColours.Select(h => ColourMath.TryParseHex(h, out var c) ? c : (Color?)null).OfType<Color>().ToList();
        _recent.Visibility = colours.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (colours.Count == 0)
            return;
        var caption = new TextBlock { Text = L.T("Used lately"), FontSize = 10, Width = FieldWidth, FlowDirection = L.IsRightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight, Margin = new Thickness(0, 0, 0, 3) };
        caption.SetResourceReference(TextBlock.ForegroundProperty, "Petal.TextSecondary");
        _recent.Children.Add(caption);
        _recent.Children.Add(SwatchRow(colours, 0));
    }

    /// <summary>The field and the strip follow the pointer while it is held down on them.</summary>
    private void Draggable(FrameworkElement target, Action<Point> moved)
    {
        target.MouseLeftButtonDown += (_, e) =>
        {
            target.CaptureMouse();
            moved(e.GetPosition(target));
            e.Handled = true;
        };
        target.MouseMove += (_, e) =>
        {
            if (target.IsMouseCaptured)
                moved(e.GetPosition(target));
        };
        target.MouseLeftButtonUp += (_, e) =>
        {
            if (!target.IsMouseCaptured)
                return;
            target.ReleaseMouseCapture();
            Settle();
            e.Handled = true;
        };
    }

    private void FromPicker()
    {
        _colour = ColourMath.FromHsv(_hue, _saturation, _brightness);
        WriteValues(force: true);
        Place();
        Changing?.Invoke(_colour);
    }

    private void FromTyping(Color typed, TextBox from)
    {
        Take(typed);
        WriteValues(force: false, except: from);
        Place();
        Changing?.Invoke(_colour);
    }

    private void SetColour(Color colour)
    {
        Take(colour);
        WriteValues(force: true);
        Place();
    }

    private void Take(Color colour)
    {
        _colour = colour;
        var (hue, saturation, brightness) = ColourMath.ToHsv(colour);
        // Black and greys have no hue of their own: the one chosen stays.
        if (saturation > 0 && brightness > 0)
            _hue = hue;
        if (brightness > 0)
            _saturation = saturation;
        _brightness = brightness;
    }

    private void WriteValues(bool force, TextBox? except = null)
    {
        _writing = true;
        void Write(TextBox box, string text)
        {
            if (box != except && (force || !box.IsKeyboardFocused) && box.Text != text)
                box.Text = text;
        }
        Write(_hex, ColourMath.Hex(_colour));
        Write(_red, _colour.R.ToString(CultureInfo.InvariantCulture));
        Write(_green, _colour.G.ToString(CultureInfo.InvariantCulture));
        Write(_blue, _colour.B.ToString(CultureInfo.InvariantCulture));
        _writing = false;
    }

    private void Place()
    {
        _hueFill.Fill = Palette.Brush(ColourMath.FromHsv(_hue, 1, 1));
        _after.Background = Palette.Brush(_colour);
        Canvas.SetLeft(_fieldThumb, _saturation * FieldWidth - 7);
        Canvas.SetTop(_fieldThumb, (1 - _brightness) * FieldHeight - 7);
        _fieldThumb.Stroke = _brightness > 0.6 && _saturation < 0.4 ? Brushes.Black : Brushes.White;
        Canvas.SetLeft(_stripThumb, _hue / 360 * FieldWidth - 4);
        Canvas.SetTop(_stripThumb, -2);
    }

    private void Settle() => Settled?.Invoke(_colour);

    /// <summary>Puts a colour first among those used lately.</summary>
    public static void Remember(Color colour)
    {
        var list = SettingsStore.Current.RecentColours;
        var hex = ColourMath.Hex(colour);
        list.RemoveAll(h => string.Equals(h, hex, StringComparison.OrdinalIgnoreCase));
        list.Insert(0, hex);
        if (list.Count > MostRecent)
            list.RemoveRange((int)MostRecent, list.Count - (int)MostRecent);
        SettingsStore.Save();
    }
}

/// <summary>
/// The colour panel in a small window of its own, for choices that aren't a <see cref="ColorWell"/>. Clicking away
/// or Enter keeps the colour; Esc puts back the one before.
/// </summary>
internal sealed class ColourWindow : Window
{
    private static ColourWindow? _open;

    private readonly ColourPanel _panel;
    private readonly Color _before;
    private readonly Action<Color> _chosen;
    private readonly Action<Color>? _changing;
    private bool _finished;

    /// <param name="chosen">Gets the colour kept.</param>
    /// <param name="changing">Gets the colour as it changes, for a live preview (and the one before, when cancelled).</param>
    /// <param name="swatches">Ready-made colours offered under the panel.</param>
    /// <param name="owner">The window it opens from; stays on top of it.</param>
    public static void Pick(Color initial, Action<Color> chosen, Action<Color>? changing = null, IReadOnlyList<Color>? swatches = null, Window? owner = null)
    {
        _open?.Close();
        _open = new ColourWindow(initial, chosen, changing, swatches);
        if (owner is { IsLoaded: true })
        {
            _open.Owner = owner;
            _open.Topmost = owner.Topmost;
        }
        _open.Show();
    }

    private ColourWindow(Color initial, Action<Color> chosen, Action<Color>? changing, IReadOnlyList<Color>? swatches)
    {
        _before = initial;
        _chosen = chosen;
        _changing = changing;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        Title = L.T("Choose a Colour");
        _panel = new ColourPanel(initial, swatches);
        _panel.Changing += c => _changing?.Invoke(c);
        _panel.Settled += c => _changing?.Invoke(c);
        var card = new Border
        {
            Padding = new Thickness(12),
            Margin = new Thickness(10, 8, 10, 14),
            CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(1),
            Child = _panel,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, ShadowDepth = 4, Direction = 270, Opacity = 0.3 },
        };
        card.SetResourceReference(Border.BackgroundProperty, "Petal.Popup");
        card.SetResourceReference(Border.BorderBrushProperty, "Petal.ControlBorder");
        Content = card;
        Closed += (_, _) =>
        {
            if (_open == this)
                _open = null;
        };
        Deactivated += (_, _) => Finish(keep: true);
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                Finish(keep: false);
            }
            else if (e.Key == Key.Enter)
            {
                e.Handled = true;
                Finish(keep: true);
            }
        };
        Loaded += (_, _) => PlaceNearPointer();
    }

    private void PlaceNearPointer()
    {
        Interop.NativeMethods.GetCursorPos(out var p);
        var monitor = ScreenNative.UnderPointer();
        double s = monitor.Scale;
        double left = p.X / s - ActualWidth / 2, top = p.Y / s + 8;
        Left = Math.Clamp(left, monitor.Work.Left / s, Math.Max(monitor.Work.Left / s, monitor.Work.Right / s - ActualWidth));
        Top = top + ActualHeight > monitor.Work.Bottom / s ? Math.Max(monitor.Work.Top / s, p.Y / s - ActualHeight - 8) : top;
        Activate();
    }

    private void Finish(bool keep)
    {
        if (_finished)
            return;
        _finished = true;
        if (keep)
        {
            if (_panel.Colour != _before)
                ColourPanel.Remember(_panel.Colour);
            _chosen(_panel.Colour);
        }
        else
        {
            _changing?.Invoke(_before);
        }
        Close();
    }

    /// <summary>For pictures of the panel.</summary>
    internal static ColourWindow ForSnapshot(Color initial, IReadOnlyList<Color>? swatches) => new(initial, _ => { }, null, swatches);
}

/// <summary>
/// The chip at the end of a row of ready-made colours: a rainbow until a colour of your own is chosen, then that
/// colour. A click opens the colour panel.
/// </summary>
public sealed class CustomColourChip : Button
{
    private readonly Ellipse _centre = new();
    private readonly Ellipse _ring = new() { StrokeThickness = 2, Visibility = Visibility.Hidden };
    private readonly Func<Color> _start;
    private readonly IReadOnlyList<Color>? _swatches;

    public event Action<Color>? Picked;

    /// <summary>Goes with each change, for a live preview.</summary>
    public event Action<Color>? Changing;

    /// <param name="start">The colour the panel opens on.</param>
    public CustomColourChip(double size, Func<Color> start, IReadOnlyList<Color>? swatches = null)
    {
        _start = start;
        _swatches = swatches;
        Style = (Style)Application.Current.FindResource("Petal.IconButton");
        Padding = new Thickness(0);
        ToolTip = L.T("Any colour…");
        System.Windows.Automation.AutomationProperties.SetName(this, L.T("Any colour…"));
        var face = new Grid { Width = size, Height = size };
        _ring.SetResourceReference(Shape.StrokeProperty, "Petal.Text");
        face.Children.Add(_ring);
        face.Children.Add(new Ellipse { Margin = new Thickness(4), Fill = Rainbow() });
        _centre.Margin = new Thickness(8);
        _centre.Visibility = Visibility.Collapsed;
        face.Children.Add(_centre);
        Content = face;
        Click += (_, _) => ColourWindow.Pick(_start(), c => Picked?.Invoke(c), c => Changing?.Invoke(c), _swatches, Window.GetWindow(this));
    }

    /// <summary>The colour of your own this chip stands for, or null while it is a plain rainbow.</summary>
    public Color? Colour
    {
        set
        {
            _centre.Visibility = value is null ? Visibility.Collapsed : Visibility.Visible;
            if (value is { } c)
                _centre.Fill = Palette.Brush(c);
        }
    }

    /// <summary>A ring around the chip: its colour is the one in use.</summary>
    public bool Chosen
    {
        set => _ring.Visibility = value ? Visibility.Visible : Visibility.Hidden;
    }

    /// <summary>All the hues around a circle, in a brush small marks can use.</summary>
    internal static Brush Rainbow()
    {
        var stops = new GradientStopCollection();
        for (int i = 0; i <= 6; i++)
            stops.Add(new GradientStop(ColourMath.FromHsv(i * 60 % 360, 0.85, 1), i / 6.0));
        var brush = new LinearGradientBrush(stops, new Point(0, 0), new Point(1, 1));
        brush.Freeze();
        return brush;
    }
}
