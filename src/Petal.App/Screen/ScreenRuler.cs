using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Petal.App.Input;
using Petal.App.Localization;
using Petal.App.Visuals;
using static Petal.App.Interop.NativeMethods;

namespace Petal.App.Screen;

/// <summary>
/// Measures on the screen, in pixels, over a still of it. "Spacing" follows the pointer and finds the
/// edges around it (to the next change of colour) up, down, left and right; "Box" measures what is
/// dragged. Tab switches, a click copies the size, Esc closes.
/// </summary>
internal sealed class ScreenRuler : MonitorWindow
{
    private const int Tolerance = 18;
    private readonly BitmapSource _still;
    private readonly byte[] _pixels;
    private readonly int _w, _h;
    private readonly Canvas _canvas = new() { IsHitTestVisible = false };
    private readonly System.Windows.Shapes.Path _lines = new() { Stroke = new SolidColorBrush(Color.FromRgb(0xFF, 0x2D, 0x78)), StrokeThickness = 1.5, SnapsToDevicePixels = true };
    private readonly Rectangle _box = new() { Stroke = new SolidColorBrush(Color.FromRgb(0xFF, 0x2D, 0x78)), StrokeThickness = 1.5, Fill = new SolidColorBrush(Color.FromArgb(0x28, 0xFF, 0x2D, 0x78)), Visibility = Visibility.Collapsed };
    private readonly Border _label;
    private readonly TextBlock _labelText = new() { Foreground = Brushes.White, FontSize = 13, FontWeight = FontWeights.SemiBold, FontFamily = UiFonts.Display };
    private readonly TextBlock _mode = new() { Foreground = Brushes.White, FontSize = 13, FlowDirection = L.Direction };
    private bool _boxMode;
    private Point? _start;
    private string _measure = "";

    public static void Start()
    {
        var monitor = ScreenNative.UnderPointer();
        if (ScreenUi.Still(monitor.Bounds) is not { } still)
            return;
        new ScreenRuler(monitor.Bounds, still).Show();
    }

    private ScreenRuler(RECT bounds, BitmapSource still) : base(bounds)
    {
        _still = still;
        _w = still.PixelWidth;
        _h = still.PixelHeight;
        _pixels = new byte[_w * _h * 4];
        still.CopyPixels(_pixels, _w * 4, 0);
        Title = L.T("Screen Ruler");
        Cursor = Cursors.Cross;
        Background = new ImageBrush(still) { Stretch = Stretch.Fill };
        _label = ScreenUi.Strip(_labelText, 8);
        _label.Padding = new Thickness(8, 3, 8, 4);
        _canvas.Children.Add(_lines);
        _canvas.Children.Add(_box);
        _canvas.Children.Add(_label);
        var hint = ScreenUi.Strip(_mode, 10);
        hint.Padding = new Thickness(14, 7, 14, 8);
        hint.HorizontalAlignment = HorizontalAlignment.Center;
        hint.VerticalAlignment = VerticalAlignment.Top;
        hint.Margin = new Thickness(0, 24, 0, 0);
        hint.IsHitTestVisible = false;
        Content = new Grid { Background = Brushes.Transparent, Children = { _canvas, hint } };
        ShowMode();
        Loaded += (_, _) =>
        {
            Activate();
            Focus();
            GetCursorPos(out var p);
            Spacing(new ScreenPoint(p.X, p.Y));
        };
    }

    private void ShowMode() => _mode.Text = _boxMode
        ? L.T("Box: drag to measure · Tab for spacing · Click copies · Esc closes")
        : L.T("Spacing: point between edges · Tab for a box · Click copies · Esc closes");

    private bool Same(int x1, int y1, int x2, int y2)
    {
        int a = (y1 * _w + x1) * 4, b = (y2 * _w + x2) * 4;
        return Math.Abs(_pixels[a] - _pixels[b]) <= Tolerance && Math.Abs(_pixels[a + 1] - _pixels[b + 1]) <= Tolerance && Math.Abs(_pixels[a + 2] - _pixels[b + 2]) <= Tolerance;
    }

    /// <summary>From the pixel under the pointer out to where the colour changes, in each direction.</summary>
    private void Spacing(ScreenPoint at)
    {
        int x = Math.Clamp(at.X - Bounds.Left, 0, _w - 1), y = Math.Clamp(at.Y - Bounds.Top, 0, _h - 1);
        int left = x, right = x, up = y, down = y;
        while (left > 0 && Same(left - 1, y, x, y)) left--;
        while (right < _w - 1 && Same(right + 1, y, x, y)) right++;
        while (up > 0 && Same(x, up - 1, x, y)) up--;
        while (down < _h - 1 && Same(x, down + 1, x, y)) down++;
        double s = Scale;
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            void Line(double x1, double y1, double x2, double y2)
            {
                c.BeginFigure(new Point(x1 / s, y1 / s), false, false);
                c.LineTo(new Point(x2 / s, y2 / s), true, false);
            }
            Line(left, y + 0.5, right + 1, y + 0.5);
            Line(x + 0.5, up, x + 0.5, down + 1);
            // Little end marks.
            Line(left, y - 5, left, y + 6);
            Line(right + 1, y - 5, right + 1, y + 6);
            Line(x - 5, up, x + 6, up);
            Line(x - 5, down + 1, x + 6, down + 1);
        }
        _lines.Data = g;
        _lines.Visibility = Visibility.Visible;
        _box.Visibility = Visibility.Collapsed;
        int width = right - left + 1, height = down - up + 1;
        _measure = $"{width} × {height}";
        Label(_measure + " px", new Point((x + 14) / s, (y + 14) / s));
    }

    private void Box(Point a, Point b)
    {
        var rect = new Rect(a, b);
        _box.Width = rect.Width;
        _box.Height = rect.Height;
        Canvas.SetLeft(_box, rect.X);
        Canvas.SetTop(_box, rect.Y);
        _box.Visibility = Visibility.Visible;
        _lines.Visibility = Visibility.Collapsed;
        int w = (int)Math.Round(rect.Width * Scale), h = (int)Math.Round(rect.Height * Scale);
        double diagonal = Math.Sqrt((double)w * w + (double)h * h);
        _measure = $"{w} × {h}";
        Label($"{w} × {h} px · ⟋ {diagonal:0}", new Point(rect.Right + 10, rect.Bottom + 10));
    }

    private void Label(string text, Point at)
    {
        _labelText.Text = text;
        _label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double x = at.X, y = at.Y;
        if (x + _label.DesiredSize.Width > ActualWidth) x = at.X - _label.DesiredSize.Width - 28;
        if (y + _label.DesiredSize.Height > ActualHeight) y = at.Y - _label.DesiredSize.Height - 28;
        Canvas.SetLeft(_label, x);
        Canvas.SetTop(_label, y);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_boxMode)
        {
            if (_start is { } start)
                Box(start, e.GetPosition(this));
            return;
        }
        GetCursorPos(out var p);
        Spacing(new ScreenPoint(p.X, p.Y));
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (!_boxMode)
            return;
        _start = e.GetPosition(this);
        CaptureMouse();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_boxMode && _start is not null)
        {
            ReleaseMouseCapture();
            _start = null;
            // A drag measures; a plain click (no box yet) is not a copy.
            if (_box.Visibility != Visibility.Visible || _box.Width < 2)
                return;
        }
        Copy();
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        Close();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        switch (e.Key)
        {
            case Key.Escape:
                Close();
                break;
            case Key.Tab:
                _boxMode = !_boxMode;
                _start = null;
                ShowMode();
                if (!_boxMode)
                {
                    GetCursorPos(out var p);
                    Spacing(new ScreenPoint(p.X, p.Y));
                }
                else
                {
                    _lines.Visibility = Visibility.Collapsed;
                    _labelText.Text = "";
                }
                e.Handled = true;
                break;
            case Key.C when Keyboard.Modifiers.HasFlag(ModifierKeys.Control):
                Copy();
                break;
        }
    }

    private void Copy()
    {
        if (_measure.Length == 0)
            return;
        try { Clipboard.SetText(_measure); } catch (System.Runtime.InteropServices.ExternalException) { }
        _labelText.Text = L.F("{0} copied", _measure);
    }
}
