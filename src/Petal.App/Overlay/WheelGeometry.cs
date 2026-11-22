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
/// Where one petal sits: its ring (0 the wheel, 1 the ring around it), its inner and outer radius, the angle of its
/// middle (maths convention: 0 = right, counter-clockwise positive), half the angle it spans, and the radius its
/// label is centred on.
/// </summary>
public readonly record struct PetalSlot(int Ring, double Inner, double Outer, double Angle, double Half, double LabelRadius)
{
    /// <summary>The only petal of its ring: a whole ring, not a wedge.</summary>
    public bool Whole => Half >= Math.PI - 1e-9;

    /// <summary>The middle of the petal, half way out.</summary>
    public Point Middle => WheelGeometry.At((Inner + Outer) / 2, Angle);
}

/// <summary>
/// Shape of the wheel, in device-independent pixels around a centre at (0, 0).
/// Petals are annular sectors separated by gaps of constant width, with rounded corners.
/// Petal 0 sits at the top and the rest follow clockwise. A wheel with more petals than one ring holds well
/// (<see cref="OneRingMost"/>) and no groups gets a second ring around the first: the first petals stay on the
/// wheel, the rest go round the ring, each ring's petals spread evenly and every petal as wide as the others.
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

    /// <summary>The most petals one ring holds well; past this a wheel without groups gets a second ring.</summary>
    public const int OneRingMost = 12;
    /// <summary>The ring around the wheel starts where the single wheel's rim is, as wide as the wheel's petals.</summary>
    public const double OuterRingInner = OuterRadius + 2;
    public const double OuterRingOuter = OuterRingInner + (PetalOuter - PetalInner);
    /// <summary>The glass rim of a wheel with two rings.</summary>
    public const double TwoRingRim = OuterRingOuter + (OuterRadius - PetalOuter);
    public const double OuterLabelRadius = (OuterRingInner + OuterRingOuter) / 2;

    /// <summary>Whether a wheel of this many petals gets a second ring (a wheel with groups never does: they open on an orbit).</summary>
    public static bool TwoRings(int count, bool hasGroups) => count > OneRingMost && !hasGroups;

    /// <summary>
    /// How many of the petals stay on the wheel when there are two rings: as many as makes the wheel's petals as wide as
    /// the ring's along the circles their names sit on, so the two rings read as one even set.
    /// </summary>
    public static int InnerCount(int count)
    {
        double share = LabelRadius / (LabelRadius + OuterLabelRadius);
        return Math.Clamp((int)Math.Round(count * share, MidpointRounding.AwayFromZero), 4, count - 4);
    }

    /// <summary>Every petal's place, in order: the wheel's clockwise from the top, then the ring's.</summary>
    public static PetalSlot[] Slots(int count, bool twoRings)
    {
        if (count <= 0)
            return [];
        if (!twoRings)
            return [.. Enumerable.Range(0, count).Select(i => new PetalSlot(0, PetalInner, PetalOuter, Angle(i, count), Math.PI / count, LabelRadius))];
        int inner = InnerCount(count), outer = count - inner;
        return
        [
            .. Enumerable.Range(0, inner).Select(i => new PetalSlot(0, PetalInner, PetalOuter, Angle(i, inner), Math.PI / inner, LabelRadius)),
            .. Enumerable.Range(0, outer).Select(i => new PetalSlot(1, OuterRingInner, OuterRingOuter, Angle(i, outer), Math.PI / outer, OuterLabelRadius)),
        ];
    }

    /// <summary>The single wheel's slot for petal <paramref name="index"/> of <paramref name="count"/>.</summary>
    public static PetalSlot Slot(int index, int count) =>
        new(0, PetalInner, PetalOuter, Angle(index, count), Math.PI / Math.Max(1, count), LabelRadius);

    /// <summary>The glass rim around these petals.</summary>
    public static double Rim(IReadOnlyList<PetalSlot> slots) => slots.Any(s => s.Ring > 0) ? TwoRingRim : OuterRadius;

    /// <summary>Visual angle (radians, maths convention: 0 = right, counter-clockwise positive) of petal i.</summary>
    public static double Angle(int index, int count) => Math.PI / 2 - 2 * Math.PI * index / count;

    /// <summary>Screen-space point (y down) at a radius and maths angle.</summary>
    public static Point At(double radius, double angle) => new(Math.Cos(angle) * radius, -Math.Sin(angle) * radius);

    /// <summary>Half the angle the gap removes at a radius, so the gap has the same width everywhere.</summary>
    private static double GapAngle(double radius) => Math.Asin(Math.Min(0.95, Gap / 2 / Math.Max(radius, Gap)));

    public static Geometry Petal(int index, int count) => Petal(Slot(index, count));

    public static Geometry Petal(PetalSlot slot)
    {
        if (slot.Whole)
            return Ring(slot.Inner, slot.Outer);

        double centre = slot.Angle;
        double half = slot.Half;
        double a = centre - half; // clockwise edge
        double b = centre + half; // counter-clockwise edge
        double rO = slot.Outer, rI = slot.Inner;
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

    public static Point LabelPosition(PetalSlot slot) => At(slot.LabelRadius, slot.Angle);

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
    public static int? HitTest(Vector offset, int count, double inner = PetalInner) =>
        HitTest(offset, Slots(count, false), inner);

    /// <summary>
    /// The petal under a point among <paramref name="slots"/>, or null over the hub, a gap (between petals or between the
    /// rings) or outside. The cancelling zone, <paramref name="inner"/>, is in the middle of the wheel.
    /// </summary>
    public static int? HitTest(Vector offset, IReadOnlyList<PetalSlot> slots, double inner = PetalInner)
    {
        if (slots.Count == 0)
            return null;
        double r = offset.Length;
        if (r < Math.Max(PetalInner, inner))
            return null;
        double phi = Math.Atan2(-offset.Y, offset.X);
        for (int i = 0; i < slots.Count; i++)
        {
            var s = slots[i];
            if (r < s.Inner || r > s.Outer + 2)
                continue;
            if (s.Whole)
                return i;
            double delta = Math.Abs(Math.Atan2(Math.Sin(phi - s.Angle), Math.Cos(phi - s.Angle)));
            if (delta <= s.Half - GapAngle(r))
                return i;
        }
        return null;
    }

    public static bool IsOverHub(Vector offset) => offset.Length < HubRadius + 2;
}
