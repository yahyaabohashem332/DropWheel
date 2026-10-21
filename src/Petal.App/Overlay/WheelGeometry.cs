using System.Windows;
using System.Windows.Media;

namespace Petal.App.Overlay;

/// <summary>How much of the middle of the wheel cancels when the drop or click lands there.</summary>
public enum CancelZone
{
    Small,
    Medium,
    Large,
}

/// <summary>
/// Shape of the wheel, in device-independent pixels around a centre at (0, 0).
/// Petals are annular sectors separated by gaps of constant width, with rounded corners.
/// Petal 0 sits at the top and the rest follow clockwise.
/// </summary>
public static class WheelGeometry
{
    public const double OuterRadius = 124;       // glass rim edge
    public const double PetalOuter = 116;        // petals end just inside the rim
    public const double PetalInner = 48;         // petals start outside the hub groove
    public const double HubRadius = 41;
    public const double Gap = 4;
    public const double OuterCorner = 16;
    public const double InnerCorner = 6;
    public const double LabelRadius = 83;

    /// <summary>Visual angle (radians, maths convention: 0 = right, counter-clockwise positive) of petal i.</summary>
    public static double Angle(int index, int count) => Math.PI / 2 - 2 * Math.PI * index / count;

    /// <summary>Screen-space point (y down) at a radius and maths angle.</summary>
    public static Point At(double radius, double angle) => new(Math.Cos(angle) * radius, -Math.Sin(angle) * radius);

    /// <summary>Half the angle the gap removes at a radius, so the gap has the same width everywhere.</summary>
    private static double GapAngle(double radius) => Math.Asin(Math.Min(0.95, Gap / 2 / Math.Max(radius, Gap)));

    public static Geometry Petal(int index, int count)
    {
        if (count == 1)
            return Ring(PetalInner, PetalOuter);

        double centre = Angle(index, count);
        double half = Math.PI / count;
        double a = centre - half; // clockwise edge
        double b = centre + half; // counter-clockwise edge
        double rO = PetalOuter, rI = PetalInner;
        double cO = Math.Min(OuterCorner, (rO - rI) / 3), cI = Math.Min(InnerCorner, (rO - rI) / 4);

        // Keep corners from swallowing narrow petals.
        double outerSpan = (b - a - 2 * GapAngle(rO)) * rO;
        cO = Math.Min(cO, outerSpan * 0.32);
        double innerSpan = (b - a - 2 * GapAngle(rI)) * rI;
        cI = Math.Min(cI, innerSpan * 0.3);

        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            var p1 = At(rI + cI, a + GapAngle(rI + cI));
            ctx.BeginFigure(p1, isFilled: true, isClosed: true);
            ctx.LineTo(At(rO - cO, a + GapAngle(rO - cO)), true, true);
            ctx.QuadraticBezierTo(At(rO, a + GapAngle(rO)), At(rO, a + GapAngle(rO) + cO / rO), true, true);
            var outerEnd = At(rO, b - GapAngle(rO) - cO / rO);
            ctx.ArcTo(outerEnd, new Size(rO, rO), 0, isLargeArc: (b - a) > Math.PI + 0.01, SweepDirection.Counterclockwise, true, true);
            ctx.QuadraticBezierTo(At(rO, b - GapAngle(rO)), At(rO - cO, b - GapAngle(rO - cO)), true, true);
            ctx.LineTo(At(rI + cI, b - GapAngle(rI + cI)), true, true);
            ctx.QuadraticBezierTo(At(rI, b - GapAngle(rI)), At(rI, b - GapAngle(rI) - cI / rI), true, true);
            ctx.ArcTo(At(rI, a + GapAngle(rI) + cI / rI), new Size(rI, rI), 0, isLargeArc: (b - a) > Math.PI + 0.01, SweepDirection.Clockwise, true, true);
            ctx.QuadraticBezierTo(At(rI, a + GapAngle(rI)), p1, true, true);
        }
        g.Freeze();
        return g;
    }

    public static Geometry Ring(double inner, double outer)
    {
        var ring = new CombinedGeometry(GeometryCombineMode.Exclude,
            new EllipseGeometry(new Point(0, 0), outer, outer),
            new EllipseGeometry(new Point(0, 0), inner, inner));
        ring.Freeze();
        return ring;
    }

    public static Point LabelPosition(int index, int count) => At(LabelRadius, Angle(index, count));

    /// <summary>The cancelling zone in the middle: the hub alone, or a little or a lot of the petals' inner ends too.</summary>
    public static double CancelRadius(CancelZone zone) => zone switch
    {
        CancelZone.Medium => 60,
        CancelZone.Large => 72,
        _ => PetalInner,
    };

    /// <summary>
    /// Index of the petal under a point relative to the centre, or null over the hub, a gap or outside.
    /// Nearer the centre than <paramref name="inner"/> counts as the hub: letting go there cancels.
    /// </summary>
    public static int? HitTest(Vector offset, int count, double inner = PetalInner)
    {
        if (count == 0)
            return null;
        double r = offset.Length;
        if (r < Math.Max(PetalInner, inner) || r > PetalOuter + 2)
            return null;
        if (count == 1)
            return 0;

        double phi = Math.Atan2(-offset.Y, offset.X);
        double step = 2 * Math.PI / count;
        double fromTop = Math.PI / 2 - phi;
        int index = (int)Math.Round(Mod(fromTop, 2 * Math.PI) / step) % count;

        double delta = Math.Abs(Math.Atan2(Math.Sin(phi - Angle(index, count)), Math.Cos(phi - Angle(index, count))));
        return delta > step / 2 - GapAngle(r) ? null : index;
    }

    public static bool IsOverHub(Vector offset) => offset.Length < HubRadius + 2;

    private static double Mod(double x, double m) => ((x % m) + m) % m;
}
