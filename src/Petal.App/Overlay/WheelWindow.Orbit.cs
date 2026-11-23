using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Petal.App.Localization;
using Petal.App.Interop;
using Petal.App.Visuals;

namespace Petal.App.Overlay;

/// <summary>
/// A group's petals out on an orbit: pointing at a group (or holding over it while dragging) sends its
/// petals out of its tip as glass bubbles that fan along an arc just outside the ring, each on a spring,
/// the nearest first. The rest of the ring steps back; the bubble under the pointer swells and lights
/// up. Leaving pulls them back in. Near a screen edge the arc turns so every bubble stays on screen.
/// </summary>
public sealed partial class WheelWindow
{
    internal const double BubbleRadius = 23;
    internal const double OrbitRadius = WheelGeometry.OuterRadius + 14 + BubbleRadius;
    // Centre to centre along the orbit: two bubbles and a little air.
    internal static readonly double OrbitStep = 2 * Math.Asin((2 * BubbleRadius + 8) / 2 / OrbitRadius);

    /// <summary>How many petals fit on one orbit; a bigger group opens in place instead.</summary>
    private static readonly int OrbitCapacity = (int)Math.Floor(2 * Math.PI / OrbitStep);

    private readonly Canvas _orbitLayer = new();
    private OrbitState? _orbit;
    // Orbits closing: their bubbles still flying home.
    private readonly List<OrbitState> _leaving = [];
    // The part of the stage that is on screen, around the centre (the wheel may sit against an edge).
    private Rect _visible = new(-Centre, -Centre, Side, Side);
    private bool _orbitClock;
    private TimeSpan _lastFrame;

    // A wheel with a second ring has no groups (WheelGeometry.TwoRings), so an orbit always opens around a single wheel.
    private bool CanOrbit(WheelItem item) =>
        item.IsGroup && item.Children is { Count: > 0 } c && c.Count <= OrbitCapacity && _rimRadius <= WheelGeometry.OuterRadius;

    private bool Animated => _options.Motion != WheelMotion.None && SystemParameters.ClientAreaAnimation;

    /// <summary>Sends the group's petals out on their orbit; with the keyboard, the first one is lit.</summary>
    private void OpenOrbit(int group, bool keyboard, bool animate = true)
    {
        if (_content is null || group >= _content.Items.Count || !CanOrbit(_content.Items[group]))
            return;
        _dwellTimer.Stop();
        if (_orbit is { Group: var open } && open == group)
            return;
        CloseOrbit(animate);
        if (_highlight != group)
            SetHighlight(group, null);

        var item = _content.Items[group];
        double anchor = _slots[group].Angle;
        // Reopened before its bubbles got home: they turn round.
        var o = _leaving.FirstOrDefault(l => l.Group == group && ReferenceEquals(l.Item, item));
        if (o is not null)
            _leaving.Remove(o);
        else
            o = BuildOrbit(group, item, anchor);
        o.Closing = false;
        o.Band.IsHitTestVisible = true;
        _orbit = o;

        int nearest = o.Nearest;
        foreach (var b in o.Bubbles)
        {
            b.Target = 1;
            b.Wait = Math.Abs(b.Index - nearest) * 0.03;
        }
        for (int i = 0; i < _visuals.Count; i++)
            if (i != group)
                _visuals[i].Recede(true, animate && Animated);
        // The strip of wheel names steps aside only when the arc couldn't turn away from it.
        o.HidesHint = o.Bubbles.Any(b =>
        {
            var c = WheelGeometry.At(OrbitRadius, b.Angle);
            return HintArea().IntersectsWith(new Rect(c.X - BubbleRadius, c.Y - BubbleRadius, 2 * BubbleRadius, 2 * BubbleRadius));
        });
        if (o.HidesHint)
            FadeHint(true, animate && Animated);
        Sound.Tick();
        SetHub(L.T(item.Name), L.T("Choose one around it"), animate: IsVisible, about: item);
        if (keyboard)
            SetOrbitHighlight(0, null);
        Run(animate);
    }

    /// <summary>Pulls the bubbles back into their petal and brings the ring forward again.</summary>
    private void CloseOrbit(bool animate = true)
    {
        StopOrbitLeave();
        if (_orbit is not { } o)
            return;
        _orbit = null;
        o.Closing = true;
        o.Band.IsHitTestVisible = false;
        int far = o.Bubbles.Max(b => Math.Abs(b.Index - o.Nearest));
        foreach (var b in o.Bubbles)
        {
            b.Target = 0;
            b.LiftTarget = 0;
            b.Wait = (far - Math.Abs(b.Index - o.Nearest)) * 0.018;
        }
        for (int i = 0; i < _visuals.Count; i++)
            _visuals[i].Recede(false, animate && Animated);
        if (o.HidesHint)
            FadeHint(false, animate && Animated);
        _leaving.Add(o);
        // The centre goes back to the group petal, which stays lit.
        if (_highlight is { } h && _content is not null && h < _content.Items.Count)
            SetHub(HubTitle(_content.Items[h]), Subtitle(_content.Items[h]), animate: IsVisible, about: _content.Items[h]);
        Run(animate);
    }

    /// <summary>Takes every orbit away at once (the wheel is redrawn or closed).</summary>
    private void ResetOrbit()
    {
        _orbit = null;
        _leaving.Clear();
        _orbitLayer.Children.Clear();
        StopClock();
    }

    private OrbitState BuildOrbit(int group, WheelItem item, double anchor)
    {
        var children = item.Children!;
        int n = children.Count;
        double centre = ArcCentre(anchor, n);
        var o = new OrbitState(group, item, anchor, centre, OrbitStep);
        var p = _palette;

        double half = (n - 1) / 2.0 * OrbitStep;
        double lo = centre - half, hi = centre + half;
        // Catches the pointer (and near-miss drops) between the ring and the bubbles.
        o.Band = new Path
        {
            Data = Sector(WheelGeometry.OuterRadius - 6, OrbitRadius + BubbleRadius + 12, lo - OrbitStep * 0.6, hi + OrbitStep * 0.6),
            Fill = HitBrush,
        };
        _orbitLayer.Children.Add(o.Band);
        o.Track = new Path
        {
            Data = Arc(OrbitRadius, lo - OrbitStep * 0.55, hi + OrbitStep * 0.55),
            Stroke = Palette.Brush(Color.FromArgb(p.IsDark ? (byte)0x30 : (byte)0x40, p.Text.R, p.Text.G, p.Text.B)),
            StrokeThickness = 1.2,
            StrokeDashArray = new DoubleCollection { 0.1, 5 },
            StrokeDashCap = PenLineCap.Round,
            IsHitTestVisible = false,
            Opacity = 0,
        };
        _orbitLayer.Children.Add(o.Track);

        for (int k = 0; k < n; k++)
        {
            var b = new Bubble(children[k], k, centre + ((n - 1) / 2.0 - k) * OrbitStep, anchor);
            b.Glass.Fill = _glass ?? (Brush)Palette.Brush(p.WindowBackground);
            // Over frosted glass, the ring's own tint, so a bubble reads as the ring does whatever is behind it
            // (without it a dark ring's light icons vanished into bubbles over a white page).
            b.Tint.Fill = _glass is null ? null : Palette.Brush(p.GlassTint);
            AddBubbleFace(b, p);
            PaintBubble(b, p, false);
            _orbitLayer.Children.Add(b.Root);
            o.Bubbles.Add(b);
            b.Update(OrbitRadius);
        }
        return o;
    }

    /// <summary>
    /// The angle the arc is centred on: the group petal's own, or as little turned as it takes to keep
    /// every bubble (and its number) on screen.
    /// </summary>
    private double ArcCentre(double anchor, int n)
    {
        var safe = _visible;
        safe.Inflate(-(BubbleRadius + 12), -(BubbleRadius + 12));
        return ArcCentreFor(anchor, n, safe, HintArea());
    }

    /// <summary>
    /// Where an arc of <paramref name="n"/> bubbles goes: all of them within <paramref name="safe"/> (on screen), and
    /// above all against the group petal they come from, so the pointer goes straight from it onto them. In order: turned
    /// only as far as keeps the petal within the arc and off the strip of wheel names; the same over the strip (it
    /// fades while the bubbles are out: it once turned the arc a quarter away from More); and only when the screen's
    /// edge leaves no such place, further (up to a quarter turn), off the strip if it can be.
    /// </summary>
    internal static double ArcCentreFor(double anchor, int n, Rect safe, Rect hint)
    {
        double half = (n - 1) / 2.0 * OrbitStep;
        double touch = half + OrbitStep * 0.5 + 1e-6;
        foreach (var (touching, offHint) in new[] { (true, true), (true, false), (false, true), (false, false) })
        {
            for (int i = 0; i <= 14; i++)
            {
                double turn = (i + 1) / 2 * (OrbitStep / 2) * (i % 2 == 1 ? 1 : -1);
                if (Math.Abs(turn) > Math.PI / 2)
                    break;
                if (touching && Math.Abs(turn) > touch)
                    continue;
                bool fits = true;
                for (int k = 0; k < n && fits; k++)
                {
                    var c = WheelGeometry.At(OrbitRadius, anchor + turn + half - k * OrbitStep);
                    fits = safe.Contains(c) && (!offHint || !hint.IntersectsWith(new Rect(c.X - BubbleRadius - 3, c.Y - BubbleRadius - 3, 2 * BubbleRadius + 6, 2 * BubbleRadius + 6)));
                }
                if (fits)
                    return anchor + turn;
            }
        }
        return anchor;
    }

    /// <summary>Where the strip of wheel names sits, around the centre; empty when it isn't shown.</summary>
    private Rect HintArea()
    {
        if (!ShowHints || _hint.Visibility != Visibility.Visible)
            return Rect.Empty;
        _hint.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double left = Canvas.GetLeft(_hint), top = Canvas.GetTop(_hint);
        if (double.IsNaN(left) || double.IsNaN(top))
            return Rect.Empty;
        return new Rect(left - Centre, top - Centre, _hint.DesiredSize.Width, _hint.DesiredSize.Height);
    }

    /// <summary>Fades the strip of wheel names when bubbles have to cross it.</summary>
    private void FadeHint(bool away, bool animate)
    {
        double to = away ? 0 : 1;
        if (!ShowHints)
            return;
        if (animate)
        {
            double from = _hint.Opacity;
            _hint.Opacity = to;
            _hint.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(from, to, TimeSpan.FromMilliseconds(away ? 140 : 200)) { FillBehavior = System.Windows.Media.Animation.FillBehavior.Stop });
        }
        else
        {
            _hint.BeginAnimation(OpacityProperty, null);
            _hint.Opacity = to;
        }
    }

    /// <summary>The icon (or short name), the switch's dot and the key number of a bubble.</summary>
    private void AddBubbleFace(Bubble b, Palette p)
    {
        var item = b.Item;
        var brush = Palette.Brush(p.Text);
        bool dot = item.State is not null;
        if (item.Picture is { } picture)
        {
            var image = new Image { Source = picture, Width = 22, Height = 22, IsHitTestVisible = false, Clip = new EllipseGeometry(new Point(11, 11), 11, 11) };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            Canvas.SetLeft(image, -11);
            Canvas.SetTop(image, dot ? -13 : -11);
            b.Content = image;
        }
        else if (item.Icon is { } key)
        {
            var icon = Icons.Create(key, 19, brush, 2);
            Canvas.SetLeft(icon, -9.5);
            Canvas.SetTop(icon, dot ? -12 : -9.5);
            b.Content = icon;
        }
        else
        {
            CreateFonts();
            var text = L.T(item.Label);
            double size = 10;
            var line = _labelFont!.Create(text, size, brush);
            while (line.Ink.Width > 2 * BubbleRadius - 10 && size > 7)
                line = _labelFont.Create(text, size -= 0.5, brush);
            line.Place(0, dot ? 1 : 3.5);
            b.Label = line;
            b.Content = line;
        }
        b.Face.Children.Add(b.Content);
        if (dot)
        {
            b.Dot = new Ellipse { Width = 4, Height = 4, IsHitTestVisible = false };
            Canvas.SetLeft(b.Dot, -2);
            Canvas.SetTop(b.Dot, 10);
            b.Face.Children.Add(b.Dot);
        }
        if (_interactive && _options.ShowNumbers && b.Index < 9)
        {
            CreateFonts();
            b.Number = _labelFont!.Create(((char)('1' + b.Index)).ToString(), 8.5, Palette.Brush(p.TextSecondary));
            b.Root.Children.Add(b.Number);
        }
    }

    private static void PaintBubble(Bubble b, Palette p, bool on)
    {
        var item = b.Item;
        var accent = item.Tint ?? p.Accent;
        bool dim = item.Unavailable is not null;
        b.Fill.Fill = on ? BubbleAccent(accent) : item.Tint is { } tint ? BubbleTint(p, tint) : BubbleFill(p);
        b.Fill.Stroke = Palette.Brush(on ? Color.FromArgb(0x70, accent.R, accent.G, accent.B) : p.GlassEdge);
        b.Fill.StrokeDashArray = dim && !on ? new DoubleCollection { 3, 2.5 } : null;
        b.Halo.Stroke = Palette.Brush(Color.FromArgb(0x66, accent.R, accent.G, accent.B));
        var fg = Palette.Brush(on ? Readable(accent, p) : item.Tint is { } t ? Legible(t, p) : p.Text);
        if (b.Content is Path icon)
            icon.Stroke = fg;
        b.Label?.Foreground = fg;
        if (b.Dot is not null)
            b.Dot.Fill = item.State == true ? Palette.Brush(on ? Readable(accent, p) : accent) : Palette.Brush(Color.FromArgb(0x55, p.Text.R, p.Text.G, p.Text.B));
        b.Number?.Foreground = Palette.Brush(on ? p.Text : p.TextSecondary);
        b.Face.Opacity = dim && !on ? 0.45 : 1;
    }

    /// <summary>Lights the bubble under the pointer (or none): it swells, and the centre names it.</summary>
    private void SetOrbitHighlight(int? index, IDataObject? data)
    {
        if (_orbit is not { } o || index == o.Highlight)
            return;
        o.Highlight = index;
        var p = _palette;
        foreach (var b in o.Bubbles)
        {
            b.LiftTarget = b.Index == index ? 1 : 0;
            PaintBubble(b, p, b.Index == index);
        }
        if (index is { } k && k < o.Bubbles.Count)
        {
            var item = o.Bubbles[k].Item;
            SetHub(HubTitle(item), Subtitle(item), animate: IsVisible, about: item);
            Sound.Tick();
            if (data is not null)
                DragDropHelper.SetDescription(data, item.IsGroup || item.Unavailable is not null && item.Blocked ? null : Description(item), null);
        }
        else
        {
            SetHub(L.T(o.Item.Name), L.T("Choose one around it"), animate: IsVisible, about: o.Item);
            if (data is not null)
                DragDropHelper.SetDescription(data, null);
        }
        Run(animate: true);
    }

    /// <summary>The bubble at a point around the centre, if it is out far enough to be chosen.</summary>
    private int? OrbitBubbleAt(Vector v)
    {
        if (_orbit is not { } o)
            return null;
        int? best = null;
        double bestDistance = BubbleRadius + 6;
        foreach (var b in o.Bubbles)
        {
            if (b.X < 0.55)
                continue;
            var rest = WheelGeometry.At(OrbitRadius, b.Angle);
            double d = (new Vector(rest.X, rest.Y) - v).Length;
            if (d <= bestDistance)
                (best, bestDistance) = (b.Index, d);
        }
        return best;
    }

    /// <summary>
    /// Between the ring and the bubbles, among them, or anywhere on the way from the group petal to them (the
    /// arc's span widened to take in the petal's own angle, down into the ring's rim): the orbit stays open.
    /// </summary>
    internal static bool OnTheWay(double anchor, double centre, int count, Vector v)
    {
        double r = v.Length;
        if (r < WheelGeometry.OuterRadius - 34 || r > OrbitRadius + BubbleRadius + 14)
            return false;
        double half = (count - 1) / 2.0 * OrbitStep + OrbitStep * 0.6;
        double toPetal = Turn(anchor - centre);
        double lo = Math.Min(-half, toPetal - OrbitStep * 0.6), hi = Math.Max(half, toPetal + OrbitStep * 0.6);
        double off = Turn(Math.Atan2(-v.Y, v.X) - centre);
        return off >= lo && off <= hi;
    }

    private static double Turn(double angle) => Math.Atan2(Math.Sin(angle), Math.Cos(angle));

    /// <summary>Beyond the bubbles by this much, the pointer has plainly left them.</summary>
    internal const double FarMargin = 60;

    /// <summary>How long the pointer rests on another petal before the bubbles give way to it.</summary>
    internal static readonly TimeSpan RestToLeave = TimeSpan.FromMilliseconds(300);

    internal enum OrbitFate { Stay, Rest, Close }

    /// <summary>
    /// What a pointer that isn't on a bubble means for the open orbit. It stays on its petal, on the way to the
    /// bubbles, and in the empty space around them; a pointer on another petal of the ring has to rest there a moment
    /// (passing over one on the way to the bubbles doesn't close them); it closes in the middle and well beyond them.
    /// </summary>
    internal static OrbitFate Fate(double anchor, double centre, int count, Vector v, bool onGroup, bool onOther, double cancelRadius)
    {
        if (onGroup || OnTheWay(anchor, centre, count, v))
            return OrbitFate.Stay;
        if (v.Length < cancelRadius)
            return OrbitFate.Close;
        if (onOther)
            return OrbitFate.Rest;
        return v.Length > OrbitRadius + BubbleRadius + FarMargin ? OrbitFate.Close : OrbitFate.Stay;
    }

    private readonly System.Windows.Threading.DispatcherTimer _orbitLeave = new() { Interval = RestToLeave };
    private int? _leavingFor;
    private Point _lastPointer;
    private IDataObject? _lastData;

    /// <summary>
    /// The pointer (or a drag) moved while bubbles are out: lights the bubble under it, keeps them out on the way,
    /// waits on another petal, or takes them in. True when the orbit dealt with it.
    /// </summary>
    private bool OrbitPointer(Point p, IDataObject? data, DragEventArgs? drag)
    {
        _lastPointer = p;
        _lastData = data;
        if (_orbit is not { } o)
            return false;
        var v = new Vector(p.X - Centre, p.Y - Centre);
        if (OrbitBubbleAt(v) is { } k)
        {
            StopOrbitLeave();
            if (drag is not null)
            {
                var item = o.Bubbles[k].Item;
                drag.Effects = item.IsGroup || item.Unavailable is not null && item.Blocked ? DragDropEffects.None : DragDropEffects.Copy;
            }
            SetOrbitHighlight(k, data);
            return true;
        }
        var over = HitTestAt(p);
        switch (Fate(o.Anchor, o.Centre, o.Bubbles.Count, v, over == o.Group, over is not null && over != o.Group, CancelRadius))
        {
            case OrbitFate.Stay:
                StopOrbitLeave();
                break;
            case OrbitFate.Rest:
                if (_leavingFor != over)
                {
                    _leavingFor = over;
                    _orbitLeave.Stop();
                    _orbitLeave.Start();
                }
                break;
            default:
                StopOrbitLeave();
                CloseOrbit();
                return false;
        }
        drag?.Effects = DragDropEffects.None;
        SetOrbitHighlight(null, data);
        return true;
    }

    private void StopOrbitLeave()
    {
        _orbitLeave.Stop();
        _leavingFor = null;
    }

    /// <summary>The pointer rested on another petal: the bubbles go home and that petal is lit (a group opens in turn).</summary>
    private void OnOrbitLeave(object? sender, EventArgs e)
    {
        var target = _leavingFor;
        StopOrbitLeave();
        if (_orbit is null || target is not { } i || HitTestAt(_lastPointer) != i || _content is null || i >= _content.Items.Count)
            return;
        CloseOrbit();
        SetHighlight(i, _lastData);
        if (_content.Items[i].IsGroup)
        {
            _dwellTimer.Interval = TimeSpan.FromMilliseconds(240);
            _dwellTimer.Start();
        }
    }

    /// <summary>The keys while bubbles are out: arrows move round them, Enter or a number chooses, Esc goes back.</summary>
    private bool OrbitKey(Key key, bool repeat)
    {
        if (_orbit is not { } o)
            return false;
        int n = o.Bubbles.Count;
        int digit = key is >= Key.D1 and <= Key.D9 ? key - Key.D1 : key is >= Key.NumPad1 and <= Key.NumPad9 ? key - Key.NumPad1 : -1;
        if (digit >= 0 && _options.ShowNumbers && Keyboard.Modifiers == ModifierKeys.None)
        {
            if (digit < n && !repeat)
                ChooseItem(o.Bubbles[digit].Item);
            return true;
        }
        switch (key)
        {
            case Key.Right or Key.Down:
                SetOrbitHighlight(((o.Highlight ?? -1) + 1) % n, null);
                return true;
            case Key.Left or Key.Up:
                SetOrbitHighlight(((o.Highlight ?? 0) - 1 + n) % n, null);
                return true;
            case Key.Home:
                SetOrbitHighlight(0, null);
                return true;
            case Key.End:
                SetOrbitHighlight(n - 1, null);
                return true;
            case Key.Enter or Key.Space when !repeat:
                if (o.Highlight is { } k)
                    ChooseItem(o.Bubbles[k].Item);
                else
                    SetOrbitHighlight(0, null);
                return true;
            case Key.Escape or Key.Back:
                CloseOrbit();
                return true;
            default:
                return false;
        }
    }

    /// <summary>For design snapshots: every bubble at <paramref name="progress"/> of its way out.</summary>
    private void SettleOrbit(double progress)
    {
        StopClock();
        foreach (var o in _leaving)
            foreach (var b in o.Bubbles)
                _orbitLayer.Children.Remove(b.Root);
        _leaving.Clear();
        if (_orbit is not { } open)
            return;
        foreach (var b in open.Bubbles)
        {
            b.X = progress;
            b.Lift = b.LiftTarget;
            b.V = b.LiftV = 0;
            b.Update(OrbitRadius);
        }
        open.Track.Opacity = Math.Clamp(progress, 0, 1);
    }

    // ---------- the springs ----------

    /// <summary>Moves every bubble towards where it is going, frame by frame, or puts it there at once.</summary>
    private void Run(bool animate)
    {
        if (animate && Animated && IsVisible)
        {
            if (!_orbitClock)
            {
                _orbitClock = true;
                _lastFrame = TimeSpan.Zero;
                CompositionTarget.Rendering += OnOrbitFrame;
            }
            return;
        }
        Advance(1.0, instant: true);
    }

    private void StopClock()
    {
        if (!_orbitClock)
            return;
        _orbitClock = false;
        CompositionTarget.Rendering -= OnOrbitFrame;
    }

    private void OnOrbitFrame(object? sender, EventArgs e)
    {
        var now = e is RenderingEventArgs r ? r.RenderingTime : TimeSpan.Zero;
        if (now == _lastFrame)
            return; // Rendering can come twice for one frame.
        double dt = _lastFrame == TimeSpan.Zero ? 1 / 60.0 : Math.Clamp((now - _lastFrame).TotalSeconds, 0.001, 1 / 30.0);
        _lastFrame = now;
        if (!Advance(dt, instant: false))
            StopClock();
    }

    /// <summary>One step for every orbit; false once everything has come to rest.</summary>
    private bool Advance(double dt, bool instant)
    {
        bool busy = false;
        foreach (var o in _leaving.ToList().Append(_orbit))
        {
            if (o is null)
                continue;
            double sum = 0;
            foreach (var b in o.Bubbles)
            {
                if (instant)
                {
                    (b.X, b.V, b.Lift, b.LiftV, b.Wait) = (b.Target, 0, b.LiftTarget, 0, 0);
                }
                else
                {
                    if (b.Wait > 0)
                    {
                        b.Wait -= dt;
                        busy = true;
                    }
                    else
                    {
                        // Out on a lively spring that overshoots a little; home on a firm one that doesn't.
                        bool outward = b.Target > 0;
                        busy |= !Spring(ref b.X, ref b.V, b.Target, outward ? 190 : 330, outward ? 0.6 : 0.95, dt);
                    }
                    busy |= !Spring(ref b.Lift, ref b.LiftV, b.LiftTarget, 420, 0.62, dt);
                }
                b.Update(OrbitRadius);
                sum += Math.Clamp(b.X, 0, 1);
            }
            o.Track.Opacity = o.Bubbles.Count == 0 ? 0 : sum / o.Bubbles.Count;
            if (o.Closing && o.Bubbles.All(b => b.X <= 0.001 && b.Target == 0))
            {
                _leaving.Remove(o);
                _orbitLayer.Children.Remove(o.Band);
                _orbitLayer.Children.Remove(o.Track);
                foreach (var b in o.Bubbles)
                    _orbitLayer.Children.Remove(b.Root);
            }
        }
        return busy;
    }

    /// <summary>A damped spring towards <paramref name="target"/>; true once it has settled there.</summary>
    private static bool Spring(ref double x, ref double v, double target, double stiffness, double damping, double dt)
    {
        double c = 2 * damping * Math.Sqrt(stiffness);
        const int steps = 4;
        double h = dt / steps;
        for (int i = 0; i < steps; i++)
        {
            v += (stiffness * (target - x) - c * v) * h;
            x += v * h;
        }
        if (Math.Abs(target - x) < 0.0015 && Math.Abs(v) < 0.02)
        {
            (x, v) = (target, 0);
            return true;
        }
        return false;
    }

    // ---------- shapes ----------

    private static Geometry Sector(double inner, double outer, double from, double to)
    {
        bool large = to - from > Math.PI;
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(WheelGeometry.At(inner, from), true, true);
            ctx.LineTo(WheelGeometry.At(outer, from), false, false);
            ctx.ArcTo(WheelGeometry.At(outer, to), new Size(outer, outer), 0, large, SweepDirection.Counterclockwise, false, false);
            ctx.LineTo(WheelGeometry.At(inner, to), false, false);
            ctx.ArcTo(WheelGeometry.At(inner, from), new Size(inner, inner), 0, large, SweepDirection.Clockwise, false, false);
        }
        g.Freeze();
        return g;
    }

    private static Geometry Arc(double radius, double from, double to)
    {
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(WheelGeometry.At(radius, from), false, false);
            ctx.ArcTo(WheelGeometry.At(radius, to), new Size(radius, radius), 0, to - from > Math.PI, SweepDirection.Counterclockwise, true, false);
        }
        g.Freeze();
        return g;
    }

    private static Brush BubbleFill(Palette p) => BubbleBrush(p.PetalTop, p.PetalBottom);

    private static Brush BubbleTint(Palette p, Color tint) => BubbleBrush(
        Color.FromArgb((byte)Math.Max(p.PetalTop.A, (byte)0x60), tint.R, tint.G, tint.B),
        Color.FromArgb((byte)Math.Max(p.PetalBottom.A, (byte)0x30), tint.R, tint.G, tint.B));

    private static Brush BubbleAccent(Color accent) => BubbleBrush(Mix(accent, Colors.White, 0.18), Mix(accent, Colors.Black, 0.12));

    /// <summary>Lit from above, like the petals: the light's colour high up, the deeper one below.</summary>
    private static Brush BubbleBrush(Color top, Color bottom)
    {
        var b = new RadialGradientBrush
        {
            GradientOrigin = new Point(0.5, 0.15),
            Center = new Point(0.5, 0.35),
            RadiusX = 0.75,
            RadiusY = 0.75,
            GradientStops = [new GradientStop(top, 0.2), new GradientStop(bottom, 1)],
        };
        b.Freeze();
        return b;
    }

    private sealed class OrbitState(int group, WheelItem item, double anchor, double centre, double step)
    {
        public int Group { get; } = group;
        public WheelItem Item { get; } = item;
        public double Anchor { get; } = anchor;
        public double Centre { get; } = centre;
        public List<Bubble> Bubbles { get; } = [];
        public Path Band { get; set; } = null!;
        public Path Track { get; set; } = null!;
        public int? Highlight { get; set; }
        public bool Closing { get; set; }
        public bool HidesHint { get; set; }

        /// <summary>The bubble nearest the group petal: out first, home last.</summary>
        public int Nearest => Math.Clamp((int)Math.Round((Bubbles.Count - 1) / 2.0 + (Centre - Anchor) / step), 0, Math.Max(0, Bubbles.Count - 1));
    }

    /// <summary>
    /// One bubble. Its glass is drawn in the ring's own coordinates so the frosted capture behind the window
    /// lines up while it flies; its face (icon, dot) rides a transform.
    /// </summary>
    private sealed class Bubble
    {
        private readonly EllipseGeometry _shape = new();
        private readonly EllipseGeometry _halo = new();
        private readonly ScaleTransform _faceScale = new();
        private readonly TranslateTransform _faceShift = new();

        public Bubble(WheelItem item, int index, double angle, double from)
        {
            Item = item;
            Index = index;
            Angle = angle;
            From = from;
            Glass = new Path { Data = _shape, IsHitTestVisible = false };
            Tint = new Path { Data = _shape, IsHitTestVisible = false };
            Fill = new Path { Data = _shape, StrokeThickness = 0.9, IsHitTestVisible = false };
            Halo = new Path { Data = _halo, StrokeThickness = 2.5, Fill = null, IsHitTestVisible = false, Opacity = 0 };
            Shadow = new Path
            {
                Data = _shadow,
                IsHitTestVisible = false,
                Fill = new RadialGradientBrush([new GradientStop(Color.FromArgb(0x38, 0, 0, 0), 0.55), new GradientStop(Color.FromArgb(0, 0, 0, 0), 1)]),
            };
            Face.IsHitTestVisible = false;
            Face.RenderTransform = new TransformGroup { Children = { _faceScale, _faceShift } };
            Root.IsHitTestVisible = false;
            Root.Children.Add(Shadow);
            Root.Children.Add(Glass);
            Root.Children.Add(Tint);
            Root.Children.Add(Fill);
            Root.Children.Add(Halo);
            Root.Children.Add(Face);
        }

        private readonly EllipseGeometry _shadow = new();

        public WheelItem Item { get; }
        public int Index { get; }
        /// <summary>Where it rests on the orbit (maths angle).</summary>
        public double Angle { get; }
        /// <summary>The group petal's angle: where it flies out from.</summary>
        public double From { get; }
        public Canvas Root { get; } = new();
        public Canvas Face { get; } = new();
        public Path Glass { get; }
        public Path Tint { get; }
        public Path Fill { get; }
        public Path Halo { get; }
        public Path Shadow { get; }
        public UIElement? Content { get; set; }
        public InkText? Label { get; set; }
        public Ellipse? Dot { get; set; }
        public InkText? Number { get; set; }

        // How far out (0 home, 1 on the orbit; a spring may overshoot) and how lit, with their speeds.
        public double X, V, Target;
        public double Lift, LiftV, LiftTarget;
        public double Wait;

        /// <summary>Puts the bubble where its progress says: along the arc from the petal's tip, swelling as it goes.</summary>
        public void Update(double orbit)
        {
            double x = X;
            // It turns towards its place quickly and then rides the spring outwards.
            double turn = Math.Clamp(x * 1.3, 0, 1);
            turn = turn * turn * (3 - 2 * turn);
            double delta = Math.Atan2(Math.Sin(Angle - From), Math.Cos(Angle - From));
            double angle = From + delta * turn;
            double home = WheelGeometry.PetalOuter - 12;
            double lift = Math.Max(0, Lift);
            double r = home + (orbit - home) * x + 5 * lift;
            double s = Math.Max(0.05, 0.22 + 0.78 * x) * (1 + 0.15 * lift);
            double radius = BubbleRadius * s;
            var c = WheelGeometry.At(r, angle);

            _shape.Center = c;
            _shape.RadiusX = _shape.RadiusY = radius;
            _halo.Center = c;
            _halo.RadiusX = _halo.RadiusY = radius + 4.5;
            _shadow.Center = new Point(c.X, c.Y + 3.5);
            _shadow.RadiusX = _shadow.RadiusY = radius + 7;
            Halo.Opacity = Math.Clamp(lift, 0, 1);
            Root.Opacity = Math.Clamp(x * 2.2, 0, 1);
            double faceScale = Math.Max(0.05, s);
            _faceScale.ScaleX = _faceScale.ScaleY = faceScale;
            _faceShift.X = c.X;
            _faceShift.Y = c.Y;
            if (Number is not null)
            {
                var at = WheelGeometry.At(r + radius + 9, angle);
                Number.Place(at.X, at.Y + 3);
                Number.Opacity = Math.Clamp((x - 0.5) * 2, 0, 1);
            }
        }
    }
}
