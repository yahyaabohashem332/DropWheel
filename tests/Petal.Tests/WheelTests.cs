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
