namespace Petal.Core.Imaging;

/// <summary>Where the pointer was in a frame of a recording, in the recording's pixels.</summary>
public readonly record struct CursorSample(double X, double Y, bool Visible);

/// <summary>A click during a recording: when (seconds) and where (the recording's pixels).</summary>
public readonly record struct ClickSample(double Time, double X, double Y, int Button = 1);

/// <summary>The virtual camera for one frame: how far in, where it looks, and where the smoothed pointer is.</summary>
public readonly record struct CameraFrame(double Zoom, double CenterX, double CenterY, double CursorX, double CursorY, bool CursorVisible);

/// <summary>
/// The camera moves of a polished screen recording: it glides in a little before a click, follows the
/// pointer while you work there, and drifts back out when the clicking stops. Moves are springs, so
/// they start and settle softly; the pointer itself is smoothed of the hand's small jitters. The view
/// never leaves the recording.
/// </summary>
public static class CameraPath
{
    /// <summary>How long before a click the camera starts moving in, and how long it stays after.</summary>
    public const double LeadIn = 0.55, Hold = 2.4;

    public static IReadOnlyList<CameraFrame> Build(double fps, double width, double height, IReadOnlyList<CursorSample> cursor,
        IReadOnlyList<ClickSample> clicks, double zoom)
    {
        int frames = cursor.Count;
        var result = new CameraFrame[frames];
        if (frames == 0)
            return result;
        double dt = 1 / fps;
        var segments = Segments(clicks, zoom > 1.01);
        // State of the three springs.
        double z = 1, zv = 0;
        double cx = width / 2, cy = height / 2, cxv = 0, cyv = 0;
        double px = cursor[0].X, py = cursor[0].Y, pxv = 0, pyv = 0;
        for (int i = 0; i < frames; i++)
        {
            double t = i * dt;
            var sample = cursor[i];
            // The pointer: a quick spring, enough to smooth the hand.
            if (sample.Visible)
            {
                Spring(ref px, ref pxv, sample.X, 14, dt);
                Spring(ref py, ref pyv, sample.Y, 14, dt);
            }
            bool active = segments.Any(s => t >= s.Start && t <= s.End);
            double wantZoom = active ? zoom : 1;
            Spring(ref z, ref zv, wantZoom, 5.5, dt);
            // While in, look at the pointer; going out, come back to the middle.
            double fx = active ? Focus(segments, t, px, clicks) : width / 2;
            double fy = active ? FocusY(segments, t, py, clicks) : height / 2;
            Spring(ref cx, ref cxv, fx, 4.2, dt);
            Spring(ref cy, ref cyv, fy, 4.2, dt);
            double zoomNow = Math.Max(1, z);
            // Keep the view inside the recording.
            double halfW = width / 2 / zoomNow, halfH = height / 2 / zoomNow;
            double clampedX = Math.Clamp(cx, halfW, width - halfW), clampedY = Math.Clamp(cy, halfH, height - halfH);
            result[i] = new CameraFrame(zoomNow, clampedX, clampedY, px, py, sample.Visible);
        }
        return result;
    }

    // Moving in follows the pointer, but a click is where the eye goes: lean towards it near the click.
    private static double Focus(List<(double Start, double End)> segments, double t, double pointer, IReadOnlyList<ClickSample> clicks) =>
        Lean(t, pointer, clicks, c => c.X);

    private static double FocusY(List<(double Start, double End)> segments, double t, double pointer, IReadOnlyList<ClickSample> clicks) =>
        Lean(t, pointer, clicks, c => c.Y);

    private static double Lean(double t, double pointer, IReadOnlyList<ClickSample> clicks, Func<ClickSample, double> at)
    {
        ClickSample? near = null;
        double best = double.MaxValue;
        foreach (var c in clicks)
        {
            double d = Math.Abs(c.Time - t);
            if (d < best)
            {
                best = d;
                near = c;
            }
        }
        if (near is not { } click || best > 1.2)
            return pointer;
        double weight = 1 - best / 1.2;
        return pointer + (at(click) - pointer) * weight * 0.6;
    }

    /// <summary>The times the camera is in: around each click, joined when clicks come close together.</summary>
    public static List<(double Start, double End)> Segments(IReadOnlyList<ClickSample> clicks, bool zooming)
    {
        var list = new List<(double Start, double End)>();
        if (!zooming)
            return list;
        foreach (var c in clicks.OrderBy(c => c.Time))
        {
            double start = Math.Max(0, c.Time - LeadIn), end = c.Time + Hold;
            if (list.Count > 0 && start <= list[^1].End)
                list[^1] = (list[^1].Start, Math.Max(list[^1].End, end));
            else
                list.Add((start, end));
        }
        return list;
    }

    /// <summary>One step of a critically damped spring towards a target.</summary>
    private static void Spring(ref double x, ref double v, double target, double omega, double dt)
    {
        double a = omega * omega * (target - x) - 2 * omega * v;
        v += a * dt;
        x += v * dt;
    }
}
