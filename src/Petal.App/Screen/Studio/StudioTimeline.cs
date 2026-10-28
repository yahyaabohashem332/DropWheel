using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Petal.App.Localization;
using Petal.App.Visuals;
using Petal.Core.Recording;

namespace Petal.App.Screen.Studio;

/// <summary>
/// The studio's timeline: a ruler, the sound with a mark at each click and a bar under each run of typing, a lane
/// of zooms and a lane of cuts and speed-ups, the trimmed-off ends dimmed behind their handles, and the playhead.
/// Drag on an empty lane to add a stretch, drag a stretch to move it or its ends to change it; stretches in a lane
/// never overlap. Click the ruler or the sound to move the playhead. The wheel scrolls, Ctrl and the wheel zoom.
/// </summary>
internal sealed class StudioTimeline : FrameworkElement
{
    private const double Pad = 12, RulerHeight = 22, SoundTop = 24, SoundHeight = 34, ZoomTop = 62, LaneHeight = 30, EditTop = 96, OverviewTop = 132;
    private const double MinLength = 0.3;

    private double _duration = 1, _viewStart, _viewEnd = 1, _position;
    private float[] _peaks = [];
    private double[] _clicks = [];
    private List<(double Start, double End)> _typing = [];
    private Mode _mode;
    private int _index = -1;
    private double _grab, _anchor;

    private enum Mode { None, Scrub, TrimStart, TrimEnd, CreateZoom, MoveZoom, ZoomStart, ZoomEnd, CreateEdit, MoveEdit, EditStart, EditEnd, Overview }

    /// <summary>Where the playhead was dragged.</summary>
    public event Action<double>? Scrubbed;

    /// <summary>Something changed while dragging.</summary>
    public event Action? Edited;

    /// <summary>A drag ended with a change (the moment to remember it for undo).</summary>
    public event Action? Committed;

    public event Action? SelectionChanged;

    public StudioTimeline()
    {
        FlowDirection = FlowDirection.LeftToRight;
        Height = 146;
        Focusable = true;
        ClipToBounds = true;
        Cursor = Cursors.Arrow;
    }

    public List<ZoomSegment> Zooms { get; set; } = [];

    public List<EditSegment> Edits { get; set; } = [];

    public double TrimStart { get; set; }

    public double TrimEnd { get; set; }

    /// <summary>The zoom a new stretch gets, and the point it looks at when it doesn't follow the action.</summary>
    public Func<double, double, ZoomSegment>? NewZoom { get; set; }

    /// <summary>The selected stretch: 1 a zoom, 2 a cut or speed-up; and which.</summary>
    public int SelectedLane { get; private set; }

    public int SelectedIndex { get; private set; } = -1;

    public double Duration
    {
        get => _duration;
        set
        {
            _duration = Math.Max(0.1, value);
            _viewStart = 0;
            _viewEnd = _duration;
            InvalidateVisual();
        }
    }

    public double Position
    {
        get => _position;
        set
        {
            _position = Math.Clamp(value, 0, _duration);
            // Playing past the edge of a zoomed-in view: the view moves on.
            if (_mode == Mode.None && (_position < _viewStart || _position > _viewEnd))
            {
                double span = _viewEnd - _viewStart;
                _viewStart = Math.Clamp(_position - span * 0.1, 0, _duration - span);
                _viewEnd = _viewStart + span;
            }
            InvalidateVisual();
        }
    }

    public void SetMarks(IEnumerable<double> clicks, List<(double Start, double End)> typing)
    {
        _clicks = clicks.OrderBy(c => c).ToArray();
        _typing = typing;
        InvalidateVisual();
    }

    public float[] Peaks
    {
        set
        {
            _peaks = value;
            InvalidateVisual();
        }
    }

    public void Select(int lane, int index)
    {
        SelectedLane = index < 0 ? 0 : lane;
        SelectedIndex = index;
        InvalidateVisual();
        SelectionChanged?.Invoke();
    }

    // ---------- geometry ----------

    private double Span => Math.Max(0.05, _viewEnd - _viewStart);

    private double X(double t) => Pad + (t - _viewStart) / Span * Math.Max(1, ActualWidth - 2 * Pad);

    private double T(double x) => Math.Clamp(_viewStart + (x - Pad) / Math.Max(1, ActualWidth - 2 * Pad) * Span, 0, _duration);

    private bool Zoomed => Span < _duration - 1e-6;

    // ---------- drawing ----------

    private static Brush Res(string key) => (Brush)Application.Current.FindResource(key);

    private static readonly Brush ZoomFill = Frozen(Color.FromRgb(0x7B, 0x5C, 0xFF));
    private static readonly Brush CutFill = Frozen(Color.FromRgb(0xE5, 0x48, 0x4D));
    private static readonly Brush SpeedFill = Frozen(Color.FromRgb(0xE8, 0x9B, 0x14));
    private static readonly Brush ClickMark = Frozen(Color.FromRgb(0xF5, 0x9E, 0x0B));
    private static readonly Brush TypingMark = Frozen(Color.FromRgb(0x22, 0xC5, 0x5E));
    private static readonly Brush Dim = Frozen(Color.FromArgb(0x8C, 0, 0, 0));
    private static readonly Brush Playhead = Frozen(Color.FromRgb(0xFF, 0x4D, 0x4D));

    private static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private FormattedText Label(string text, double size, Brush brush, bool bold = false) =>
        new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface(UiFonts.Text, FontStyles.Normal, bold ? FontWeights.SemiBold : FontWeights.Normal, FontStretches.Normal),
            size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth;
        if (w <= 0)
            return;
        var lane = Res("Petal.Control");
        var text = Res("Petal.Text");
        var caption = Res("Petal.TextSecondary");
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, ActualHeight));
        DrawRuler(dc, caption);
        // The sound, the clicks and the typing.
        var soundRect = new Rect(Pad, SoundTop, w - 2 * Pad, SoundHeight);
        dc.DrawRoundedRectangle(lane, null, soundRect, 6, 6);
        dc.PushClip(new RectangleGeometry(soundRect, 6, 6));
        if (_peaks.Length > 0)
        {
            var wave = Res("Petal.Accent").Clone();
            wave.Opacity = 0.45;
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                double mid = SoundTop + SoundHeight / 2;
                for (double x = Pad; x < w - Pad; x += 2)
                {
                    double t = T(x);
                    int i = Math.Clamp((int)(t / _duration * _peaks.Length), 0, _peaks.Length - 1);
                    double h = Math.Max(0.6, _peaks[i] * (SoundHeight / 2 - 3));
                    ctx.BeginFigure(new Point(x, mid - h), false, false);
                    ctx.LineTo(new Point(x, mid + h), true, false);
                }
            }
            g.Freeze();
            dc.DrawGeometry(null, new Pen(wave, 1.4), g);
        }
        foreach (var (start, end) in _typing)
            dc.DrawRoundedRectangle(TypingMark, null, new Rect(X(start), SoundTop + SoundHeight - 5, Math.Max(3, X(end) - X(start)), 3), 1.5, 1.5);
        foreach (var c in _clicks)
            dc.DrawEllipse(ClickMark, null, new Point(X(c), SoundTop + 6), 3, 3);
        dc.Pop();
        // The zooms.
        DrawLane(dc, lane, ZoomTop, Zooms.Count == 0 ? L.T("Drag here to zoom in") : null, caption);
        for (int i = 0; i < Zooms.Count; i++)
        {
            var z = Zooms[i];
            string tag = z.Follow ? $"{z.Level:0.0}×" : $"{z.Level:0.0}× ·";
            DrawStretch(dc, ZoomTop, z.Start, z.End, ZoomFill, tag, SelectedLane == 1 && SelectedIndex == i);
        }
        // The cuts and speed-ups.
        DrawLane(dc, lane, EditTop, Edits.Count == 0 ? L.T("Drag here to cut out or speed up") : null, caption);
        for (int i = 0; i < Edits.Count; i++)
        {
            var e = Edits[i];
            bool cut = e.Kind == EditKind.Cut;
            DrawStretch(dc, EditTop, e.Start, e.End, cut ? CutFill : SpeedFill, cut ? L.T("Cut") : $"{e.Speed:0.#}×", SelectedLane == 2 && SelectedIndex == i, hatched: cut);
        }
        // The trimmed ends.
        double top = SoundTop, bottom = EditTop + LaneHeight;
        double ts = X(TrimStart), te = X(TrimEnd);
        if (ts > Pad)
            dc.DrawRectangle(Dim, null, new Rect(Pad, top, ts - Pad, bottom - top));
        if (te < w - Pad)
            dc.DrawRectangle(Dim, null, new Rect(te, top, w - Pad - te, bottom - top));
        DrawHandle(dc, ts, top, bottom, true);
        DrawHandle(dc, te, top, bottom, false);
        // The playhead.
        double px = X(_position);
        if (px >= Pad - 1 && px <= w - Pad + 1)
        {
            dc.DrawRectangle(Playhead, null, new Rect(px - 1, 2, 2, bottom - 2));
            var knob = new StreamGeometry();
            using (var ctx = knob.Open())
            {
                ctx.BeginFigure(new Point(px - 6, 2), true, true);
                ctx.LineTo(new Point(px + 6, 2), true, false);
                ctx.LineTo(new Point(px, 10), true, false);
            }
            knob.Freeze();
            dc.DrawGeometry(Playhead, null, knob);
        }
        // Zoomed in: where the view is in the whole.
        if (Zoomed)
        {
            dc.DrawRoundedRectangle(lane, null, new Rect(Pad, OverviewTop, w - 2 * Pad, 6), 3, 3);
            double a = Pad + _viewStart / _duration * (w - 2 * Pad), b = Pad + _viewEnd / _duration * (w - 2 * Pad);
            dc.DrawRoundedRectangle(caption, null, new Rect(a, OverviewTop, Math.Max(8, b - a), 6), 3, 3);
        }
        _ = text;
    }

    private void DrawRuler(DrawingContext dc, Brush caption)
    {
        double w = ActualWidth - 2 * Pad;
        // A tick every so often, so labels sit at least 70 pixels apart.
        double[] steps = [0.1, 0.25, 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600];
        double step = steps.FirstOrDefault(s => s / Span * w >= 70, 600);
        var pen = new Pen(caption, 1);
        for (double t = Math.Ceiling(_viewStart / step) * step; t <= _viewEnd + 1e-6; t += step)
        {
            double x = X(t);
            dc.DrawLine(pen, new Point(x, RulerHeight - 6), new Point(x, RulerHeight - 1));
            var label = Label(Clock(t, step < 1), 10.5, caption);
            dc.DrawText(label, new Point(Math.Min(x + 3, ActualWidth - label.Width - 2), 3));
        }
    }

    public static string Clock(double t, bool tenths = false)
    {
        var span = TimeSpan.FromSeconds(Math.Max(0, t));
        string whole = span.TotalHours >= 1 ? $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}" : $"{(int)span.TotalMinutes}:{span.Seconds:00}";
        return tenths ? whole + "." + (span.Milliseconds / 100).ToString(CultureInfo.InvariantCulture) : whole;
    }

    private void DrawLane(DrawingContext dc, Brush lane, double top, string? empty, Brush caption)
    {
        var rect = new Rect(Pad, top, ActualWidth - 2 * Pad, LaneHeight);
        dc.DrawRoundedRectangle(lane, null, rect, 6, 6);
        if (empty is not null)
        {
            var label = Label(empty, 11.5, caption);
            dc.DrawText(label, new Point(rect.X + 10, top + (LaneHeight - label.Height) / 2));
        }
    }

    private void DrawStretch(DrawingContext dc, double top, double start, double end, Brush fill, string tag, bool selected, bool hatched = false)
    {
        double a = X(start), b = X(end);
        if (b < 0 || a > ActualWidth)
            return;
        var rect = new Rect(a, top + 2, Math.Max(6, b - a), LaneHeight - 4);
        dc.DrawRoundedRectangle(fill, selected ? new Pen(Brushes.White, 2) : null, rect, 5, 5);
        if (hatched)
        {
            dc.PushClip(new RectangleGeometry(rect, 5, 5));
            var pen = new Pen(new SolidColorBrush(Color.FromArgb(0x50, 0, 0, 0)), 3);
            for (double x = rect.X - rect.Height; x < rect.Right; x += 9)
                dc.DrawLine(pen, new Point(x, rect.Bottom), new Point(x + rect.Height, rect.Top));
            dc.Pop();
        }
        // The grips at its ends.
        var grip = new SolidColorBrush(Color.FromArgb(0xC8, 255, 255, 255));
        if (rect.Width > 18)
        {
            dc.DrawRoundedRectangle(grip, null, new Rect(rect.X + 3, rect.Y + 7, 2.5, rect.Height - 14), 1.2, 1.2);
            dc.DrawRoundedRectangle(grip, null, new Rect(rect.Right - 5.5, rect.Y + 7, 2.5, rect.Height - 14), 1.2, 1.2);
        }
        var label = Label(tag, 11.5, Brushes.White, bold: true);
        if (label.Width + 16 < rect.Width)
            dc.DrawText(label, new Point(rect.X + 10, rect.Y + (rect.Height - label.Height) / 2));
    }

    private void DrawHandle(DrawingContext dc, double x, double top, double bottom, bool start)
    {
        if (x < Pad - 8 || x > ActualWidth - Pad + 8)
            return;
        var accent = Res("Petal.Accent");
        var rect = start ? new Rect(x - 7, top, 7, bottom - top) : new Rect(x, top, 7, bottom - top);
        dc.DrawRoundedRectangle(accent, null, rect, 3, 3);
        dc.DrawRoundedRectangle(Brushes.White, null, new Rect(rect.X + 2.5, (top + bottom) / 2 - 8, 2, 16), 1, 1);
    }

    // ---------- mouse ----------

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Focus();
        var p = e.GetPosition(this);
        double t = T(p.X);
        _mode = Mode.None;
        if (Zoomed && p.Y >= OverviewTop - 2)
        {
            _mode = Mode.Overview;
            _grab = t;
        }
        else if (p.Y >= SoundTop && Math.Abs(p.X - X(TrimStart)) <= 7 && p.X <= X(TrimStart) + 2)
            _mode = Mode.TrimStart;
        else if (p.Y >= SoundTop && Math.Abs(p.X - X(TrimEnd)) <= 7 && p.X >= X(TrimEnd) - 2)
            _mode = Mode.TrimEnd;
        else if (p.Y >= ZoomTop && p.Y < ZoomTop + LaneHeight)
            Press(p.X, t, Zooms.Select(z => (z.Start, z.End)).ToList(), 1);
        else if (p.Y >= EditTop && p.Y < EditTop + LaneHeight)
            Press(p.X, t, Edits.Select(z => (z.Start, z.End)).ToList(), 2);
        else
        {
            _mode = Mode.Scrub;
            Scrub(t);
        }
        if (_mode != Mode.None)
        {
            CaptureMouse();
            e.Handled = true;
        }
    }

    /// <summary>A press on a lane: an end of a stretch, a stretch, or empty space to make one.</summary>
    private void Press(double x, double t, List<(double Start, double End)> stretches, int lane)
    {
        for (int i = stretches.Count - 1; i >= 0; i--)
        {
            var (start, end) = stretches[i];
            double a = X(start), b = X(end);
            if (x < a - 5 || x > b + 5)
                continue;
            Select(lane, i);
            _index = i;
            if (Math.Abs(x - a) <= 6 && b - a > 14)
                _mode = lane == 1 ? Mode.ZoomStart : Mode.EditStart;
            else if (Math.Abs(x - b) <= 6 && b - a > 14)
                _mode = lane == 1 ? Mode.ZoomEnd : Mode.EditEnd;
            else
            {
                _mode = lane == 1 ? Mode.MoveZoom : Mode.MoveEdit;
                _grab = t - start;
            }
            return;
        }
        // Empty: a new stretch from here, as long as the drag goes (or a few seconds for a click).
        _anchor = t;
        _mode = lane == 1 ? Mode.CreateZoom : Mode.CreateEdit;
        _index = -1;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var p = e.GetPosition(this);
        double t = T(p.X);
        if (_mode == Mode.None || e.LeftButton != MouseButtonState.Pressed)
        {
            Cursor = Hover(p);
            return;
        }
        switch (_mode)
        {
            case Mode.Scrub:
                Scrub(t);
                return;
            case Mode.Overview:
                double span = Span;
                double centre = Math.Clamp((p.X - Pad) / Math.Max(1, ActualWidth - 2 * Pad) * _duration, span / 2, _duration - span / 2);
                _viewStart = centre - span / 2;
                _viewEnd = centre + span / 2;
                break;
            case Mode.TrimStart:
                TrimStart = Math.Clamp(t, 0, TrimEnd - MinLength);
                Scrub(TrimStart, edited: true);
                break;
            case Mode.TrimEnd:
                TrimEnd = Math.Clamp(t, TrimStart + MinLength, _duration);
                Scrub(TrimEnd, edited: true);
                break;
            case Mode.CreateZoom or Mode.CreateEdit:
                Create(t);
                break;
            default:
                Drag(t);
                break;
        }
        InvalidateVisual();
        Edited?.Invoke();
    }

    private Cursor Hover(Point p)
    {
        if (p.Y >= SoundTop && (Math.Abs(p.X - X(TrimStart)) <= 7 || Math.Abs(p.X - X(TrimEnd)) <= 7))
            return Cursors.SizeWE;
        IEnumerable<(double Start, double End)>? lane = p.Y >= ZoomTop && p.Y < ZoomTop + LaneHeight ? Zooms.Select(z => (z.Start, z.End))
            : p.Y >= EditTop && p.Y < EditTop + LaneHeight ? Edits.Select(z => (z.Start, z.End)) : null;
        if (lane is null)
            return p.Y < ZoomTop ? Cursors.IBeam : Cursors.Arrow;
        foreach (var (start, end) in lane)
        {
            double a = X(start), b = X(end);
            if (p.X < a - 5 || p.X > b + 5)
                continue;
            return Math.Abs(p.X - a) <= 6 || Math.Abs(p.X - b) <= 6 ? Cursors.SizeWE : Cursors.SizeAll;
        }
        return Cursors.Cross;
    }

    private void Scrub(double t, bool edited = false)
    {
        _position = t;
        InvalidateVisual();
        Scrubbed?.Invoke(t);
        if (!edited)
            return;
    }

    /// <summary>The room around a stretch: from the end of the one before to the start of the one after.</summary>
    private static (double Low, double High) Room<TS>(List<TS> list, int index, Func<TS, double> start, Func<TS, double> end, double duration)
    {
        double low = index > 0 ? end(list[index - 1]) : 0;
        double high = index + 1 < list.Count ? start(list[index + 1]) : duration;
        return (low, high);
    }

    private void Create(double t)
    {
        bool zoom = _mode == Mode.CreateZoom;
        double a = Math.Min(_anchor, t), b = Math.Max(_anchor, t);
        if (b - a < 0.05 && _index < 0)
            return;
        if (_index < 0)
        {
            // Placed among the others, in the free space around the press.
            if (zoom)
            {
                var made = NewZoom?.Invoke(a, b) ?? new ZoomSegment(a, b, 1.8);
                Zooms.Add(made);
                Zooms.Sort((x, y) => x.Start.CompareTo(y.Start));
                _index = Zooms.IndexOf(made);
            }
            else
            {
                var made = new EditSegment(a, b, EditKind.Cut);
                Edits.Add(made);
                Edits.Sort((x, y) => x.Start.CompareTo(y.Start));
                _index = Edits.IndexOf(made);
            }
            Select(zoom ? 1 : 2, _index);
        }
        if (zoom)
        {
            var (low, high) = Room(Zooms, _index, z => z.Start, z => z.End, _duration);
            Zooms[_index] = Zooms[_index] with { Start = Math.Max(low, a), End = Math.Min(high, Math.Max(b, Math.Max(low, a) + 0.05)) };
        }
        else
        {
            var (low, high) = Room(Edits, _index, z => z.Start, z => z.End, _duration);
            Edits[_index] = Edits[_index] with { Start = Math.Max(low, a), End = Math.Min(high, Math.Max(b, Math.Max(low, a) + 0.05)) };
        }
    }

    private void Drag(double t)
    {
        if (_index < 0)
            return;
        if (_mode is Mode.MoveZoom or Mode.ZoomStart or Mode.ZoomEnd)
        {
            var z = Zooms[_index];
            var (low, high) = Room(Zooms, _index, s => s.Start, s => s.End, _duration);
            Zooms[_index] = _mode switch
            {
                Mode.MoveZoom => z with { Start = Math.Clamp(t - _grab, low, high - z.Length), End = Math.Clamp(t - _grab, low, high - z.Length) + z.Length },
                Mode.ZoomStart => z with { Start = Math.Clamp(t, low, z.End - MinLength) },
                _ => z with { End = Math.Clamp(t, z.Start + MinLength, high) },
            };
        }
        else
        {
            var z = Edits[_index];
            var (low, high) = Room(Edits, _index, s => s.Start, s => s.End, _duration);
            Edits[_index] = _mode switch
            {
                Mode.MoveEdit => z with { Start = Math.Clamp(t - _grab, low, high - z.Length), End = Math.Clamp(t - _grab, low, high - z.Length) + z.Length },
                Mode.EditStart => z with { Start = Math.Clamp(t, low, z.End - MinLength) },
                _ => z with { End = Math.Clamp(t, z.Start + MinLength, high) },
            };
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        var mode = _mode;
        if (mode is Mode.CreateZoom or Mode.CreateEdit && _index < 0)
        {
            // A click on an empty lane: a stretch of a few seconds from there, if it fits.
            double length = mode == Mode.CreateZoom ? 3 : 2;
            _mode = mode;
            Create(Math.Min(_duration, _anchor + 0.06));
            if (_index >= 0)
            {
                if (mode == Mode.CreateZoom)
                {
                    var (_, high) = Room(Zooms, _index, z => z.Start, z => z.End, _duration);
                    Zooms[_index] = Zooms[_index] with { End = Math.Min(high, _anchor + length) };
                    if (Zooms[_index].Length < MinLength)
                        Zooms.RemoveAt(_index);
                }
                else
                {
                    var (_, high) = Room(Edits, _index, z => z.Start, z => z.End, _duration);
                    Edits[_index] = Edits[_index] with { End = Math.Min(high, _anchor + length) };
                    if (Edits[_index].Length < MinLength)
                        Edits.RemoveAt(_index);
                }
            }
        }
        else if (mode is Mode.CreateZoom && _index >= 0 && Zooms[_index].Length < MinLength)
            Zooms[_index] = Zooms[_index] with { End = Math.Min(_duration, Zooms[_index].Start + MinLength) };
        else if (mode is Mode.CreateEdit && _index >= 0 && Edits[_index].Length < MinLength)
            Edits[_index] = Edits[_index] with { End = Math.Min(_duration, Edits[_index].Start + MinLength) };
        _mode = Mode.None;
        if (IsMouseCaptured)
            ReleaseMouseCapture();
        if (SelectedLane == 1 && SelectedIndex >= Zooms.Count || SelectedLane == 2 && SelectedIndex >= Edits.Count)
            Select(0, -1);
        InvalidateVisual();
        if (mode is not (Mode.None or Mode.Scrub or Mode.Overview))
            Committed?.Invoke();
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        if (_mode != Mode.None)
        {
            var mode = _mode;
            _mode = Mode.None;
            if (mode is not (Mode.Scrub or Mode.Overview))
                Committed?.Invoke();
        }
        base.OnLostMouseCapture(e);
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        double span = Span;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            // Zoom about the pointer.
            double at = T(e.GetPosition(this).X);
            double next = Math.Clamp(span * (e.Delta > 0 ? 0.8 : 1.25), Math.Min(_duration, 2), _duration);
            double f = (at - _viewStart) / span;
            _viewStart = Math.Clamp(at - f * next, 0, _duration - next);
            _viewEnd = _viewStart + next;
        }
        else if (Zoomed)
        {
            double shift = span * 0.15 * (e.Delta > 0 ? -1 : 1);
            _viewStart = Math.Clamp(_viewStart + shift, 0, _duration - span);
            _viewEnd = _viewStart + span;
        }
        else
        {
            return;
        }
        e.Handled = true;
        InvalidateVisual();
    }
}
