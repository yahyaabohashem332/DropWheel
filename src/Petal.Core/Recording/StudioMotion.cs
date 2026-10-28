using Petal.Core.Imaging;

namespace Petal.Core.Recording;

/// <summary>The view at a moment: how far in (1 shows the whole of <see cref="CameraPlan.BaseWidth"/>), and the point it looks at, in the recording's pixels.</summary>
public readonly record struct CameraState(double Zoom, double X, double Y);

/// <summary>The pointer as drawn at a moment: where, how visible, how far pressed in by a click, its shape and how fast it moves (pixels a second).</summary>
public readonly record struct PointerState(double X, double Y, double Opacity, double Press, PointerKind Kind, int Shape, double VelocityX, double VelocityY);

/// <summary>
/// The pointer as the finished video draws it, worked out for the whole recording at once: the hand's jitter
/// smoothed away without lagging behind (the smoothing looks ahead as well as back, and holds to the exact spot
/// of every click), faded out while it rests and back in just as it starts to move, and pressed in at clicks.
/// </summary>
public sealed class PointerPlan
{
    public const double Rate = 120;

    /// <summary>How long the pointer rests before it fades, and how fast it fades out and in.</summary>
    public const double Rest = 1.8, FadeOut = 0.35, FadeIn = 0.15;

    private readonly double[] _x = [], _y = [], _opacity = [];
    private readonly int[] _sample = [];
    private readonly List<PointerSample> _samples;
    private readonly double[] _clicks;

    /// <summary>The recording has a pointer to draw.</summary>
    public bool Any { get; }

    public PointerPlan(StudioProject project)
    {
        _samples = project.Pointer;
        _clicks = project.Clicks.Select(c => c.Time).OrderBy(t => t).ToArray();
        if (!project.HasPointer || _samples.Count == 0)
            return;
        Any = true;
        var look = project.Edit.Look;
        int n = Steps(project.Duration);
        double dt = 1 / Rate;
        var rawX = new double[n];
        var rawY = new double[n];
        var visible = new bool[n];
        _sample = new int[n];
        int j = 0;
        for (int i = 0; i < n; i++)
        {
            double t = i * dt;
            while (j + 1 < _samples.Count && _samples[j + 1].Time <= t)
                j++;
            var a = _samples[j];
            _sample[i] = j;
            visible[i] = a.Visible;
            if (j + 1 < _samples.Count && t > a.Time && _samples[j + 1].Visible && a.Visible)
            {
                var b = _samples[j + 1];
                double f = Math.Clamp((t - a.Time) / Math.Max(1e-6, b.Time - a.Time), 0, 1);
                rawX[i] = a.X + (b.X - a.X) * f;
                rawY[i] = a.Y + (b.Y - a.Y) * f;
            }
            else
            {
                rawX[i] = a.X;
                rawY[i] = a.Y;
            }
        }
        _x = look.SmoothPointer ? Smooth(rawX) : rawX;
        _y = look.SmoothPointer ? Smooth(rawY) : rawY;
        if (look.SmoothPointer)
        {
            // At a click the pointer is exactly where the click was.
            foreach (var click in project.Clicks)
            {
                int c = (int)Math.Round(click.Time * Rate);
                for (int i = Math.Max(0, c - 36); i < Math.Min(n, c + 36); i++)
                {
                    double d = (i - c) * dt / 0.12;
                    double w = Math.Exp(-d * d);
                    _x[i] += (rawX[i] - _x[i]) * w;
                    _y[i] += (rawY[i] - _y[i]) * w;
                }
            }
        }
        _opacity = Opacity(rawX, rawY, visible, look.HideStillPointer, project.Clicks);
    }

    public static int Steps(double duration) => (int)Math.Ceiling(Math.Max(0, duration) * Rate) + 2;

    /// <summary>A Gaussian of about 60 ms either side: the hand's tremor goes, the path stays.</summary>
    private static double[] Smooth(double[] raw)
    {
        const double sigma = 0.06 * Rate;
        int radius = (int)Math.Ceiling(sigma * 3);
        var weights = new double[radius * 2 + 1];
        for (int k = -radius; k <= radius; k++)
            weights[k + radius] = Math.Exp(-0.5 * (k / sigma) * (k / sigma));
        var result = new double[raw.Length];
        for (int i = 0; i < raw.Length; i++)
        {
            double sum = 0, total = 0;
            for (int k = -radius; k <= radius; k++)
            {
                int at = i + k;
                if (at < 0 || at >= raw.Length)
                    continue;
                sum += raw[at] * weights[k + radius];
                total += weights[k + radius];
            }
            result[i] = sum / total;
        }
        return result;
    }

    private static double[] Opacity(double[] x, double[] y, bool[] visible, bool hideStill, IReadOnlyList<ClickSample> clicks)
    {
        int n = x.Length;
        double dt = 1 / Rate;
        var clickSteps = new HashSet<int>(clicks.Select(c => (int)Math.Round(c.Time * Rate)));
        var result = new double[n];
        double lastMove = double.NegativeInfinity, o = visible.Length > 0 && visible[0] ? 1 : 0;
        for (int i = 0; i < n; i++)
        {
            double t = i * dt;
            if (i == 0 || Math.Abs(x[i] - x[i - 1]) + Math.Abs(y[i] - y[i - 1]) > 0.6 || clickSteps.Contains(i))
                lastMove = t;
            double want = visible[i] && (!hideStill || t - lastMove < Rest || i == 0) ? 1 : 0;
            o += Math.Clamp(want - o, -dt / FadeOut, dt / FadeIn);
            result[i] = o;
        }
        // Coming back, it is there as the move begins rather than a moment after.
        for (int i = n - 2; i >= 0; i--)
            if (visible[i])
                result[i] = Math.Max(result[i], result[i + 1] - dt / FadeIn);
        return result;
    }

    public PointerState At(double t)
    {
        if (!Any)
            return default;
        double at = Math.Max(0, t * Rate);
        int i = Math.Min((int)at, _x.Length - 2);
        double f = Math.Clamp(at - i, 0, 1);
        double x = _x[i] + (_x[i + 1] - _x[i]) * f, y = _y[i] + (_y[i + 1] - _y[i]) * f;
        double opacity = _opacity[i] + (_opacity[i + 1] - _opacity[i]) * f;
        var sample = _samples[_sample[Math.Min(_sample.Length - 1, (int)Math.Round(at))]];
        return new PointerState(x, y, opacity, Press(t), sample.Kind, sample.Shape, (_x[i + 1] - _x[i]) * Rate, (_y[i + 1] - _y[i]) * Rate);
    }

    /// <summary>How far a click presses the pointer in: quickly down, held a moment, back up.</summary>
    public double Press(double t)
    {
        double press = 0;
        int from = Array.BinarySearch(_clicks, t - 0.25);
        if (from < 0)
            from = ~from;
        for (int k = from; k < _clicks.Length && _clicks[k] <= t + 0.05; k++)
        {
            double d = t - _clicks[k];
            double p = d < -0.03 ? 0 : d < 0 ? (d + 0.03) / 0.03 : d < 0.06 ? 1 : d < 0.2 ? 1 - (d - 0.06) / 0.14 : 0;
            press = Math.Max(press, p);
        }
        return press;
    }
}

/// <summary>
/// The view's moves, worked out for the whole recording at once. In a zoomed stretch it goes in softly and
/// looks at what is happening — the pointer, leaning to where a click lands, or the text cursor while typing —
/// but lets the pointer move about the middle half of the view before it follows, so it doesn't sway with every
/// small move; a stretch with a fixed point looks there. Out of the stretches it comes back to the whole
/// recording, or, when the frame's shape cuts the recording, keeps following the action. It never leaves the
/// recording.
/// </summary>
public sealed class CameraPlan
{
    public const double Rate = 120;

    /// <summary>How quickly the view goes in and out, and moves across.</summary>
    public const double ZoomSpeed = 5, MoveSpeed = 3.6;

    /// <summary>The share of the view's half the pointer may roam before the view follows.</summary>
    public const double Slack = 0.5;

    /// <summary>For this long after a stretch begins the view aims straight at what is happening.</summary>
    public const double Aim = 0.8;

    private readonly CameraState[] _states;

    /// <summary>The part of the recording shown when it isn't zoomed in, in its pixels.</summary>
    public double BaseWidth { get; }

    public double BaseHeight { get; }

    public int Width { get; }

    public int Height { get; }

    /// <param name="aspect">The proportions of the recording's place in the frame.</param>
    public CameraPlan(StudioProject project, PointerPlan pointer, double aspect)
    {
        Width = project.Width;
        Height = project.Height;
        (BaseWidth, BaseHeight) = StudioLayout.BaseView(project.Width, project.Height, aspect);
        bool cropped = BaseWidth < project.Width - 1 || BaseHeight < project.Height - 1;
        var zooms = project.Edit.Zooms.OrderBy(z => z.Start).ToList();
        var runs = ZoomPlanner.TypingRuns(project.Typing);
        var placed = project.Typing.Where(k => k.Placed).OrderBy(k => k.Time).ToList();
        var clicks = project.Clicks.OrderBy(c => c.Time).ToList();
        var placedTimes = placed.Select(k => k.Time).ToArray();
        double pointerSize = 32 * Math.Max(0.5, project.PixelScale) * Math.Max(0.5, project.Edit.Look.PointerSize);
        var clickTimes = clicks.Select(c => c.Time).ToArray();
        int n = PointerPlan.Steps(project.Duration);
        double dt = 1 / Rate;
        _states = new CameraState[n];
        double lnz = 0, lnzv = 0;
        var start = pointer.Any && cropped ? Focus(0) : (project.Width / 2.0, project.Height / 2.0);
        double cx = start.Item1, cy = start.Item2, cxv = 0, cyv = 0;
        double aimX = cx, aimY = cy;
        int z = 0;
        for (int i = 0; i < n; i++)
        {
            double t = i * dt;
            while (z < zooms.Count && zooms[z].End <= t)
                z++;
            var active = z < zooms.Count && zooms[z].Start <= t ? zooms[z] : null;
            Spring(ref lnz, ref lnzv, active is null ? 0 : Math.Log(Math.Max(1, active.Level)), ZoomSpeed, dt);
            double zoom = Math.Exp(Math.Max(0, lnz));
            double halfW = BaseWidth / zoom / 2, halfH = BaseHeight / zoom / 2;
            bool following = false;
            double fx = 0, fy = 0;
            if (active is { Follow: false })
            {
                aimX = active.X;
                aimY = active.Y;
            }
            else if ((active is not null || cropped) && pointer.Any)
            {
                following = true;
                (fx, fy) = Focus(t);
                if (active is not null && t - active.Start < Aim)
                {
                    aimX = fx;
                    aimY = fy;
                }
                else
                {
                    aimX = Follow(aimX, fx, halfW * Slack);
                    aimY = Follow(aimY, fy, halfH * Slack);
                }
            }
            else
            {
                aimX = project.Width / 2.0;
                aimY = project.Height / 2.0;
            }
            // The further out the action is, the quicker the view goes after it.
            Spring(ref cx, ref cxv, aimX, following ? Quicker(fx - cx, halfW) : MoveSpeed, dt);
            Spring(ref cy, ref cyv, aimY, following ? Quicker(fy - cy, halfH) : MoveSpeed, dt);
            if (following)
            {
                // And it never leaves the view, however fast it goes.
                // The pointer hangs right of and below its point: room for all of it on those sides.
                Within(ref cx, ref cxv, fx, halfW * Edge, Math.Max(halfW * 0.3, halfW * Edge - pointerSize * 0.5));
                Within(ref cy, ref cyv, fy, halfH * Edge, Math.Max(halfH * 0.3, halfH * Edge - pointerSize * 0.7));
            }
            // The view stays on the recording.
            Keep(ref cx, ref cxv, halfW, project.Width);
            Keep(ref cy, ref cyv, halfH, project.Height);
            _states[i] = new CameraState(zoom, cx, cy);
        }

        (double, double) Focus(double t)
        {
            // Typing: where the text cursor is (or the click that put it there).
            foreach (var (from, to) in runs)
            {
                if (t < from - 0.6 || t > to + 1.0)
                    continue;
                int k = Last(placedTimes, Math.Max(t, from));
                if (k >= 0 && placed[k].Time >= from - 0.01)
                    return (placed[k].X, placed[k].Y);
                int c = Last(clickTimes, from);
                if (c >= 0)
                    return (clicks[c].X, clicks[c].Y);
                break;
            }
            var p = pointer.At(t);
            // Near a click, lean towards where it lands.
            int before = Last(clickTimes, t);
            ClickSample? near = null;
            double best = 1.2;
            foreach (int c in new[] { before, before + 1 })
            {
                if (c < 0 || c >= clicks.Count)
                    continue;
                double d = Math.Abs(clicks[c].Time - t);
                if (d < best)
                {
                    best = d;
                    near = clicks[c];
                }
            }
            if (near is not { } click)
                return (p.X, p.Y);
            double w = (1 - best / 1.2) * 0.6;
            return (p.X + (click.X - p.X) * w, p.Y + (click.Y - p.Y) * w);
        }
    }

    /// <summary>The last of sorted times at or before a moment (-1 when none is).</summary>
    private static int Last(double[] times, double t)
    {
        int i = Array.BinarySearch(times, t);
        if (i >= 0)
        {
            while (i + 1 < times.Length && times[i + 1] <= t)
                i++;
            return i;
        }
        return ~i - 1;
    }

    /// <summary>The share of the view's half the action may come to before the view is pushed along with it.</summary>
    public const double Edge = 0.8;

    /// <summary>The view's speed: as set while the action is near its middle, up to twice as quick near its edge.</summary>
    private static double Quicker(double offset, double half)
    {
        double k = Math.Clamp((Math.Abs(offset) / Math.Max(1, half) - Slack) / (Edge - Slack), 0, 1);
        return MoveSpeed * (1 + k);
    }

    /// <summary>Keeps a point inside the view, moving the view with it when it gets to the edge.</summary>
    private static void Within(ref double c, ref double v, double target, double before, double after)
    {
        if (target - c > after)
        {
            c = target - after;
            v = Math.Max(v, 0);
        }
        else if (c - target > before)
        {
            c = target + before;
            v = Math.Min(v, 0);
        }
    }

    /// <summary>Moves an aim only as far as needed to keep the target within the slack.</summary>
    private static double Follow(double aim, double target, double slack)
    {
        double d = target - aim;
        return Math.Abs(d) <= slack ? aim : target - Math.Sign(d) * slack;
    }

    private static void Keep(ref double c, ref double v, double half, double size)
    {
        double low = Math.Min(half, size / 2), high = Math.Max(size - half, size / 2);
        if (c < low)
        {
            c = low;
            v = Math.Max(0, v);
        }
        else if (c > high)
        {
            c = high;
            v = Math.Min(0, v);
        }
    }

    /// <summary>One step of a critically damped spring towards a target.</summary>
    private static void Spring(ref double x, ref double v, double target, double omega, double dt)
    {
        double a = omega * omega * (target - x) - 2 * omega * v;
        v += a * dt;
        x += v * dt;
    }

    public CameraState At(double t)
    {
        double at = Math.Max(0, t * Rate);
        int i = Math.Min((int)at, _states.Length - 2);
        if (i < 0)
            return new CameraState(1, Width / 2.0, Height / 2.0);
        double f = Math.Clamp(at - i, 0, 1);
        var a = _states[i];
        var b = _states[i + 1];
        return new CameraState(a.Zoom + (b.Zoom - a.Zoom) * f, a.X + (b.X - a.X) * f, a.Y + (b.Y - a.Y) * f);
    }

    /// <summary>The part of the recording in view at a moment.</summary>
    public RectD View(double t) => View(At(t));

    public RectD View(CameraState s)
    {
        double w = BaseWidth / s.Zoom, h = BaseHeight / s.Zoom;
        return new RectD(s.X - w / 2, s.Y - h / 2, w, h);
    }
}
