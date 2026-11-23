using System.Windows;
using Petal.App.Overlay;

namespace Petal.Tests;

/// <summary>
/// A group's bubbles (More on the actions wheel, the tools that don't fit) can be reached: the arc stays against its
/// petal when it can, the way from the petal to the bubbles keeps them out, and they go home only when the pointer
/// plainly leaves them.
/// </summary>
public class WheelOrbitTests
{
    private static readonly double Step = WheelWindow.OrbitStep;

    private static Vector At(double radius, double angle)
    {
        var p = WheelGeometry.At(radius, angle);
        return new Vector(p.X, p.Y);
    }

    private static double Off(double a, double b) => Math.Abs(Math.Atan2(Math.Sin(a - b), Math.Cos(a - b)));

    [Fact]
    public void An_arc_turned_from_a_screen_edge_still_reaches_its_petal()
    {
        // More at the top left of a wheel near the top of the screen: its arc can't stay centred on it.
        var anchor = 3 * Math.PI / 4;
        var safe = new Rect(-400, -150, 800, 600);
        for (var n = 2; n <= 8; n++)
        {
            var centre = WheelWindow.ArcCentreFor(anchor, n, safe, Rect.Empty);
            var half = (n - 1) / 2.0 * Step;
            Assert.True(Off(centre, anchor) <= half + Step * 0.5 + 1e-9, $"{n} bubbles: turned {Off(centre, anchor)} rad, the arc reaches {half}");
            // And every bubble is on screen.
            for (var k = 0; k < n; k++)
                Assert.True(safe.Contains(WheelGeometry.At(WheelWindow.OrbitRadius, centre + half - k * Step)));
        }
        // With room, it stays centred on its petal.
        Assert.Equal(anchor, WheelWindow.ArcCentreFor(anchor, 6, new Rect(-400, -400, 800, 800), Rect.Empty));
    }

    [Fact]
    public void The_strip_of_wheel_names_never_turns_the_arc_away_from_its_petal()
    {
        // The actions wheel's More (top left) with its six actions, the strip of Shift, Ctrl and Alt just above the
        // ring, as the wheel lays them out: the arc once turned 68° to keep off the strip, leaving More behind.
        var anchor = 117.7 * Math.PI / 180;
        var safe = new Rect(-225, -225, 450, 450);
        var strip = new Rect(-94.5, -166, 189, 29.1);
        var centre = WheelWindow.ArcCentreFor(anchor, 6, safe, strip);
        Assert.True(Off(centre, anchor) <= 2.5 * Step + Step * 0.5 + 1e-6, $"turned {Off(centre, anchor) * 180 / Math.PI:F1}°");
    }

    [Fact]
    public void The_way_from_the_petal_to_a_turned_arc_keeps_it_open()
    {
        var anchor = 3 * Math.PI / 4;
        var centre = anchor + 0.7;
        const int count = 6;
        // On the rim of the petal itself, along the rim towards the arc, and among the bubbles.
        Assert.True(WheelWindow.OnTheWay(anchor, centre, count, At(WheelGeometry.OuterRadius - 12, anchor)));
        Assert.True(WheelWindow.OnTheWay(anchor, centre, count, At(WheelGeometry.OuterRadius + 10, anchor + 0.35)));
        Assert.True(WheelWindow.OnTheWay(anchor, centre, count, At(WheelWindow.OrbitRadius, centre)));
        // Not the far side of the wheel, nor deep in the ring.
        Assert.False(WheelWindow.OnTheWay(anchor, centre, count, At(WheelGeometry.OuterRadius, anchor + Math.PI)));
        Assert.False(WheelWindow.OnTheWay(anchor, centre, count, At(40, anchor)));
    }

    [Fact]
    public void The_bubbles_go_home_only_when_the_pointer_plainly_leaves_them()
    {
        var anchor = Math.PI / 2;
        var centre = anchor;
        const int count = 5;
        const double cancel = 46;
        WheelWindow.OrbitFate Fate(Vector v, bool onGroup = false, bool onOther = false) =>
            WheelWindow.Fate(anchor, centre, count, v, onGroup, onOther, cancel);

        Assert.Equal(WheelWindow.OrbitFate.Stay, Fate(At(100, anchor), onGroup: true));
        // Passing over another petal on the way waits: resting there is what takes the bubbles home.
        Assert.Equal(WheelWindow.OrbitFate.Rest, Fate(At(90, anchor + Math.PI), onOther: true));
        // Empty space beside the bubbles keeps them; well beyond them, or the middle, takes them home.
        Assert.Equal(WheelWindow.OrbitFate.Stay, Fate(At(WheelWindow.OrbitRadius + WheelWindow.BubbleRadius + 30, anchor + Math.PI)));
        Assert.Equal(WheelWindow.OrbitFate.Close, Fate(At(WheelWindow.OrbitRadius + WheelWindow.BubbleRadius + WheelWindow.FarMargin + 10, anchor + Math.PI)));
        Assert.Equal(WheelWindow.OrbitFate.Close, Fate(At(20, anchor + Math.PI)));
        Assert.True(WheelWindow.RestToLeave >= TimeSpan.FromMilliseconds(250));
    }
}
