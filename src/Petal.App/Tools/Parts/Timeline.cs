using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Petal.App.Tools.Parts;

public static class TimeText
{
    /// <summary>"1:04.25" style: minutes, seconds and hundredths.</summary>
    public static string Format(double seconds)
    {
        if (seconds < 0) seconds = 0;
        var t = TimeSpan.FromSeconds(seconds);
        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}.{t.Milliseconds / 10:00}"
            : $"{(int)t.TotalMinutes}:{t.Seconds:00}.{t.Milliseconds / 10:00}";
    }

    /// <summary>Accepts "75", "75.5", "1:15.5" or "0:01:15.5".</summary>
    public static bool TryParse(string text, out double seconds)
    {
        seconds = 0;
        var parts = text.Trim().Split(':');
        if (parts.Length is 0 or > 3) return false;
        double total = 0;
        foreach (var part in parts)
        {
            if (!double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || v < 0)
                return false;
            total = total * 60 + v;
        }
        seconds = total;
        return true;
    }
}

/// <summary>Bars drawn from waveform peaks (0–1), centred vertically.</summary>
public sealed class WaveformView : FrameworkElement
{
    public WaveformView() => FlowDirection = FlowDirection.LeftToRight;

    private float[] _peaks = [];

    public float[] Peaks
    {
        get => _peaks;
        set
        {
            _peaks = value;
            InvalidateVisual();
        }
    }

    public Brush? Fill { get; set; }

    public double BarWidth { get; set; } = 2;

    public double Gap { get; set; } = 1.5;

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        if (_peaks.Length == 0 || ActualWidth <= 0)
            return;
        var fill = Fill ?? (Brush)FindResource("Petal.Accent");
        int bars = Math.Max(1, (int)(ActualWidth / (BarWidth + Gap)));
        double mid = ActualHeight / 2;
        for (int i = 0; i < bars; i++)
        {
            // Max over the peaks that fall into this bar.
            int from = i * _peaks.Length / bars, to = Math.Max(from + 1, (i + 1) * _peaks.Length / bars);
            float peak = 0;
            for (int k = from; k < to && k < _peaks.Length; k++)
                peak = Math.Max(peak, _peaks[k]);
            double h = Math.Max(2, peak * (ActualHeight - 4));
            dc.DrawRoundedRectangle(fill, null, new Rect(i * (BarWidth + Gap), mid - h / 2, BarWidth, h), BarWidth / 2, BarWidth / 2);
        }
    }
}

/// <summary>A row of thumbnails filling the width.</summary>
public sealed class Filmstrip : FrameworkElement
{
    public Filmstrip() => FlowDirection = FlowDirection.LeftToRight;

    private IReadOnlyList<ImageSource> _frames = [];

    public IReadOnlyList<ImageSource> Frames
    {
        get => _frames;
        set
        {
            _frames = value;
            InvalidateVisual();
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle((Brush)FindResource("Petal.Preview"), null, new Rect(RenderSize));
        if (_frames.Count == 0 || ActualWidth <= 0)
            return;
        double w = ActualWidth / _frames.Count;
        for (int i = 0; i < _frames.Count; i++)
        {
            var f = _frames[i];
            // Uniform-to-fill each cell.
            double scale = Math.Max(w / f.Width, ActualHeight / f.Height);
            double fw = f.Width * scale, fh = f.Height * scale;
            dc.PushClip(new RectangleGeometry(new Rect(i * w, 0, w + 0.5, ActualHeight)));
            dc.DrawImage(f, new Rect(i * w + (w - fw) / 2, (ActualHeight - fh) / 2, fw, fh));
            dc.Pop();
        }
    }
}

/// <summary>
/// Trim control: any background (filmstrip or waveform) with a framed selection whose thick ends
/// are drag handles, the outside dimmed, and a playhead that can be scrubbed.
/// </summary>
public sealed class TrimTimeline : Grid
{
    private readonly Canvas _overlay = new() { ClipToBounds = true, Background = Brushes.Transparent };
    private readonly Rectangle _dimLeft = new() { IsHitTestVisible = false };
    private readonly Rectangle _dimRight = new() { IsHitTestVisible = false };
    private readonly Border _frame = new() { BorderThickness = new Thickness(0, 2.5, 0, 2.5), IsHitTestVisible = false };
    private readonly Border _left = Handle();
    private readonly Border _right = Handle();
    private readonly Rectangle _playhead = new() { Width = 2, Fill = Brushes.White, IsHitTestVisible = false, RadiusX = 1, RadiusY = 1 };
    private double _duration = 1;
    private double _start, _end = 1, _position;
    private int _drag; // 0 none, 1 start, 2 end, 3 playhead

    public event Action<double, double>? RangeChanged;
    public event Action<double>? Scrubbed;

    public TrimTimeline(UIElement background, double height = 56)
    {
        FlowDirection = FlowDirection.LeftToRight;
        Height = height;
        var clip = new Border { CornerRadius = new CornerRadius(9), ClipToBounds = true, Child = background };
        Children.Add(clip);
        Children.Add(_overlay);
        _dimLeft.Fill = _dimRight.Fill = new SolidColorBrush(Color.FromArgb(0x9A, 0, 0, 0));
        _frame.SetResourceReference(Border.BorderBrushProperty, "Petal.Accent");
        _overlay.Children.Add(_dimLeft);
        _overlay.Children.Add(_dimRight);
        _overlay.Children.Add(_frame);
        _overlay.Children.Add(_playhead);
        _overlay.Children.Add(_left);
        _overlay.Children.Add(_right);
        _left.MouseLeftButtonDown += (_, e) => Begin(1, e);
        _right.MouseLeftButtonDown += (_, e) => Begin(2, e);
        _overlay.MouseLeftButtonDown += (_, e) => Begin(3, e);
        MouseMove += OnMove;
        MouseLeftButtonUp += (_, _) => StopDrag();
        LostMouseCapture += (_, _) => _drag = 0;
        SizeChanged += (_, _) => Layout();
    }

    private static Border Handle()
    {
        var grip = new Border { Width = 2.5, Height = 16, CornerRadius = new CornerRadius(1.25), Background = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var b = new Border { Width = 14, Cursor = Cursors.SizeWE, Child = grip };
        b.SetResourceReference(Border.BackgroundProperty, "Petal.Accent");
        return b;
    }

    public double Duration
    {
        get => _duration;
        set
        {
            _duration = Math.Max(0.01, value);
            _start = 0;
            _end = _duration;
            Layout();
        }
    }

    public double Start => _start;
    public double End => _end;

    public double Position
    {
        get => _position;
        set
        {
            _position = Math.Clamp(value, 0, _duration);
            Layout();
        }
    }

    /// <summary>Hide the handles to use the control as a plain scrubber.</summary>
    public bool ShowHandles
    {
        set
        {
            var v = value ? Visibility.Visible : Visibility.Collapsed;
            _left.Visibility = _right.Visibility = _frame.Visibility = _dimLeft.Visibility = _dimRight.Visibility = v;
        }
    }

    public void SetRange(double start, double end)
    {
        _start = Math.Clamp(start, 0, _duration);
        _end = Math.Clamp(end, _start + 0.05, _duration);
        Layout();
        RangeChanged?.Invoke(_start, _end);
    }

    private double X(double t) => HandleWidth + (ActualWidth - HandleWidth * 2) * t / _duration;
    private double T(double x) => Math.Clamp((x - HandleWidth) / Math.Max(1, ActualWidth - HandleWidth * 2) * _duration, 0, _duration);
    private const double HandleWidth = 14;

    private void Layout()
    {
        if (ActualWidth <= 0) return;
        double h = ActualHeight;
        double xs = X(_start), xe = X(_end);
        _dimLeft.Width = Math.Max(0, xs);
        _dimLeft.Height = h;
        Canvas.SetLeft(_dimRight, xe);
        _dimRight.Width = Math.Max(0, ActualWidth - xe);
        _dimRight.Height = h;
        Canvas.SetLeft(_frame, xs);
        _frame.Width = Math.Max(0, xe - xs);
        _frame.Height = h;
        Canvas.SetLeft(_left, xs - HandleWidth);
        _left.Height = h;
        _left.CornerRadius = new CornerRadius(8, 0, 0, 8);
        Canvas.SetLeft(_right, xe);
        _right.Height = h;
        _right.CornerRadius = new CornerRadius(0, 8, 8, 0);
        Canvas.SetLeft(_playhead, X(_position) - 1);
        _playhead.Height = h;
    }

    private void Begin(int what, MouseButtonEventArgs e)
    {
        e.Handled = true;
        _drag = what;
        CaptureMouse();
        OnMove(this, e);
    }

    private void StopDrag()
    {
        _drag = 0;
        if (IsMouseCaptured) ReleaseMouseCapture();
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        if (_drag == 0 || e.LeftButton != MouseButtonState.Pressed) return;
        double t = T(e.GetPosition(this).X);
        const double minimum = 0.1;
        switch (_drag)
        {
            case 1:
                _start = Math.Min(t, _end - minimum);
                _position = _start;
                RangeChanged?.Invoke(_start, _end);
                Scrubbed?.Invoke(_position);
                break;
            case 2:
                _end = Math.Max(t, _start + minimum);
                _position = _end;
                RangeChanged?.Invoke(_start, _end);
                Scrubbed?.Invoke(_position);
                break;
            case 3:
                _position = t;
                Scrubbed?.Invoke(t);
                break;
        }
        Layout();
    }
}

/// <summary>A time range on a lane (a bleep, or when a redaction box is visible).</summary>
public sealed class TimeRange(double start, double end)
{
    public double Start { get; set; } = start;
    public double End { get; set; } = end;
    public object? Tag { get; set; }
}

/// <summary>
/// One horizontal lane of draggable clips: drag on empty space to add a range, drag a clip to
/// move it, drag either edge to adjust it, Delete removes the selected clip.
/// </summary>
public sealed class RangeLane : Canvas
{
    private readonly List<(TimeRange Range, Border Clip)> _clips = [];
    private double _duration = 1;
    private int _mode; // 0 none, 1 create, 2 move, 3 left edge, 4 right edge
    private TimeRange? _active;
    private double _anchor, _grabOffset;
    private TimeRange? _selected;

    public event Action? Changed;

    public RangeLane(string label, double height = 30)
    {
        FlowDirection = FlowDirection.LeftToRight;
        Label = label;
        Height = height;
        ClipToBounds = true;
        Focusable = true;
        SetResourceReference(BackgroundProperty, "Petal.Control");
        MouseLeftButtonDown += OnDown;
        MouseMove += OnMove;
        MouseLeftButtonUp += (_, _) => Finish();
        KeyDown += (_, e) =>
        {
            if (e.Key is Key.Delete or Key.Back && _selected is not null)
            {
                Remove(_selected);
                e.Handled = true;
            }
        };
        SizeChanged += (_, _) => Layout();
    }

    public string Label { get; }

    /// <summary>Text drawn on each clip, e.g. "Bleep" or "Box 2".</summary>
    public string ClipText { get; set; } = "";

    public double Duration
    {
        get => _duration;
        set
        {
            _duration = Math.Max(0.01, value);
            Layout();
        }
    }

    /// <summary>Optional single-range mode (a redaction box lives during one span).</summary>
    public bool Single { get; set; }

    public IReadOnlyList<TimeRange> Ranges => _clips.Select(c => c.Range).OrderBy(r => r.Start).ToList();

    public TimeRange Add(double start, double end)
    {
        if (Single)
            foreach (var r in _clips.Select(c => c.Range).ToList())
                Remove(r, notify: false);
        var range = new TimeRange(start, end);
        var clip = new Border
        {
            CornerRadius = new CornerRadius(6),
            Height = Height - 6,
            Cursor = Cursors.SizeAll,
            Child = new TextBlock
            {
                Text = ClipText,
                Foreground = Brushes.White,
                FontWeight = FontWeights.SemiBold,
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(9, 0, 9, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
            },
            BorderThickness = new Thickness(3, 0, 3, 0),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0xD0, 255, 255, 255)),
        };
        clip.Background = new SolidColorBrush(Color.FromArgb(0xE0, 0xF0, 0x6E, 0x5A));
        SetTop(clip, 3);
        Children.Add(clip);
        _clips.Add((range, clip));
        Layout();
        Changed?.Invoke();
        return range;
    }

    public void Remove(TimeRange range, bool notify = true)
    {
        var index = _clips.FindIndex(c => c.Range == range);
        if (index < 0) return;
        Children.Remove(_clips[index].Clip);
        _clips.RemoveAt(index);
        if (_selected == range) _selected = null;
        if (notify) Changed?.Invoke();
    }

    private double X(double t) => ActualWidth * t / _duration;
    private double T(double x) => Math.Clamp(x / Math.Max(1, ActualWidth) * _duration, 0, _duration);

    private void Layout()
    {
        foreach (var (range, clip) in _clips)
        {
            SetLeft(clip, X(range.Start));
            clip.Width = Math.Max(10, X(range.End) - X(range.Start));
            clip.Opacity = range == _selected ? 1 : 0.86;
        }
    }

    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        Focus();
        double x = e.GetPosition(this).X;
        double t = T(x);
        var hit = _clips.LastOrDefault(c => x >= X(c.Range.Start) - 4 && x <= X(c.Range.End) + 4);
        if (hit.Range is not null)
        {
            _active = hit.Range;
            _selected = hit.Range;
            double left = X(hit.Range.Start), right = X(hit.Range.End);
            _mode = Math.Abs(x - left) < 8 ? 3 : Math.Abs(x - right) < 8 ? 4 : 2;
            _grabOffset = t - hit.Range.Start;
        }
        else
        {
            _anchor = t;
            _active = Add(t, Math.Min(_duration, t + 0.01));
            _selected = _active;
            _mode = 1;
        }
        CaptureMouse();
        Layout();
        e.Handled = true;
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        double x = e.GetPosition(this).X;
        if (_mode == 0 || _active is null || e.LeftButton != MouseButtonState.Pressed)
        {
            // Hover cursor hints at edges.
            var near = _clips.Any(c => Math.Abs(x - X(c.Range.Start)) < 8 || Math.Abs(x - X(c.Range.End)) < 8);
            Cursor = near ? Cursors.SizeWE : Cursors.Cross;
            return;
        }
        double t = T(x);
        const double min = 0.1;
        switch (_mode)
        {
            case 1:
                _active.Start = Math.Min(_anchor, t);
                _active.End = Math.Max(_anchor, t);
                break;
            case 2:
                double length = _active.End - _active.Start;
                _active.Start = Math.Clamp(t - _grabOffset, 0, _duration - length);
                _active.End = _active.Start + length;
                break;
            case 3:
                _active.Start = Math.Min(t, _active.End - min);
                break;
            case 4:
                _active.End = Math.Max(t, _active.Start + min);
                break;
        }
        Layout();
        Changed?.Invoke();
    }

    private void Finish()
    {
        if (_mode == 1 && _active is not null && _active.End - _active.Start < 0.08)
        {
            // A click rather than a drag makes a half-second range.
            _active.End = Math.Min(_duration, _active.Start + 0.5);
        }
        _mode = 0;
        _active = null;
        if (IsMouseCaptured) ReleaseMouseCapture();
        Layout();
        Changed?.Invoke();
    }
}
