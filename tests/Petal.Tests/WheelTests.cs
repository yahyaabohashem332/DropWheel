using System.Windows;
using Petal.App.Overlay;
using Petal.App.Visuals;

namespace Petal.Tests;

public class WheelGeometryTests
{
    [Theory]
    [InlineData(3)]
    [InlineData(6)]
    [InlineData(8)]
    [InlineData(10)]
    public void The_centre_of_each_petal_hits_that_petal(int count)
    {
        for (int i = 0; i < count; i++)
        {
            var p = WheelGeometry.At(WheelGeometry.LabelRadius, WheelGeometry.Angle(i, count));
            Assert.Equal(i, WheelGeometry.HitTest(new Vector(p.X, p.Y), count));
        }
    }

    [Fact]
    public void Petal_zero_is_at_the_top_and_order_is_clockwise()
    {
        Assert.Equal(0, WheelGeometry.HitTest(new Vector(0, -80), 4));
        Assert.Equal(1, WheelGeometry.HitTest(new Vector(80, 0), 4));   // right
        Assert.Equal(2, WheelGeometry.HitTest(new Vector(0, 80), 4));   // bottom
        Assert.Equal(3, WheelGeometry.HitTest(new Vector(-80, 0), 4));  // left
    }

    [Fact]
    public void Hub_gaps_and_outside_hit_nothing()
    {
        Assert.Null(WheelGeometry.HitTest(new Vector(0, 0), 6));
        Assert.Null(WheelGeometry.HitTest(new Vector(0, -30), 6));
        Assert.Null(WheelGeometry.HitTest(new Vector(0, -200), 6));
        // The boundary between petal 0 and 1 of four petals is at 45°: exactly in the gap.
        var gap = WheelGeometry.At(80, Math.PI / 4);
        Assert.Null(WheelGeometry.HitTest(new Vector(gap.X, gap.Y), 4));
    }

    [Fact]
    public void Petal_shapes_contain_their_label_points()
    {
        for (int n = 2; n <= 10; n++)
            for (int i = 0; i < n; i++)
                Assert.True(WheelGeometry.Petal(i, n).FillContains(WheelGeometry.LabelPosition(i, n)), $"petal {i} of {n}");
    }

    [Fact]
    public void Up_to_twelve_petals_or_any_group_keep_one_ring()
    {
        for (int n = 1; n <= WheelGeometry.OneRingMost; n++)
            Assert.False(WheelGeometry.TwoRings(n, hasGroups: false));
        Assert.True(WheelGeometry.TwoRings(13, hasGroups: false));
        Assert.False(WheelGeometry.TwoRings(30, hasGroups: true));
        // One ring is the wheel as it always was.
        var one = WheelGeometry.Slots(8, false);
        Assert.All(one, s => Assert.Equal((0, WheelGeometry.PetalInner, WheelGeometry.PetalOuter), (s.Ring, s.Inner, s.Outer)));
        Assert.Equal(WheelGeometry.OuterRadius, WheelGeometry.Rim(one));
    }

    [Theory]
    [InlineData(13)]
    [InlineData(15)]
    [InlineData(20)]
    [InlineData(24)]
    [InlineData(31)]
    [InlineData(40)]
    public void A_second_ring_takes_the_rest_evenly_and_every_petal_is_as_wide(int count)
    {
        var slots = WheelGeometry.Slots(count, twoRings: true);
        Assert.Equal(count, slots.Length);
        var inner = slots.Where(s => s.Ring == 0).ToList();
        var outer = slots.Where(s => s.Ring == 1).ToList();
        Assert.Equal(WheelGeometry.InnerCount(count), inner.Count);
        // The wheel's petals first, then the ring's, each ring clockwise from the top and evenly spread.
        Assert.Equal(Enumerable.Range(0, inner.Count), slots.Select((s, i) => (s, i)).Where(p => p.s.Ring == 0).Select(p => p.i));
        foreach (var ring in new[] { inner, outer })
        {
            Assert.Equal(Math.PI / 2, ring[0].Angle, 9);
            for (int i = 1; i < ring.Count; i++)
                Assert.Equal(2 * Math.PI / ring.Count, ring[i - 1].Angle - ring[i].Angle, 9);
        }
        // As wide along the circles the names sit on: within an eighth of each other.
        double innerWidth = 2 * inner[0].Half * inner[0].LabelRadius, outerWidth = 2 * outer[0].Half * outer[0].LabelRadius;
        Assert.InRange(innerWidth / outerWidth, 0.8, 1.25);
        Assert.Equal(WheelGeometry.TwoRingRim, WheelGeometry.Rim(slots));
        Assert.True(WheelGeometry.OuterRingInner > WheelGeometry.PetalOuter + WheelGeometry.Gap, "a groove between the rings");
    }

    [Theory]
    [InlineData(13)]
    [InlineData(15)]
    [InlineData(24)]
    public void Every_petal_of_two_rings_is_hit_where_it_is_and_only_there(int count)
    {
        var slots = WheelGeometry.Slots(count, twoRings: true);
        for (int i = 0; i < count; i++)
        {
            var at = WheelGeometry.LabelPosition(slots[i]);
            Assert.Equal(i, WheelGeometry.HitTest(new Vector(at.X, at.Y), slots));
            Assert.True(WheelGeometry.Petal(slots[i]).FillContains(at), $"petal {i} of {count}");
            // No other petal covers its middle.
            for (int j = 0; j < count; j++)
                if (j != i)
                    Assert.False(WheelGeometry.Petal(slots[j]).FillContains(at), $"petal {j} covers petal {i} of {count}");
        }
        // Between the rings, past the rim and over the hub: nothing.
        Assert.Null(WheelGeometry.HitTest(new Vector(0, -(WheelGeometry.PetalOuter + WheelGeometry.OuterRingInner) / 2 - 1), slots));
        Assert.Null(WheelGeometry.HitTest(new Vector(0, -(WheelGeometry.TwoRingRim + 4)), slots));
        Assert.Null(WheelGeometry.HitTest(new Vector(0, -20), slots));
    }
}

public class IconTests
{
    [Fact]
    public void Every_icon_parses()
    {
        Assert.True(Icons.Keys.Count >= 90);
        foreach (var key in Icons.Keys)
        {
            var g = Icons.Geometry(key);
            Assert.False(g.Bounds.IsEmpty, key);
            Assert.InRange(g.Bounds.Right, 0, 24.5);
            Assert.InRange(g.Bounds.Bottom, 0, 24.5);
        }
    }
}
