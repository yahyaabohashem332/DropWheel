using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Petal.App.Visuals;

/// <summary>Stroke icons drawn from vector data, crisp at any DPI.</summary>
public static partial class Icons
{
    private static readonly ConcurrentDictionary<string, Geometry> Cache = new();

    public static IReadOnlyCollection<string> Keys => Data.Keys;

    public static Geometry Geometry(string key)
    {
        return Cache.GetOrAdd(key, k =>
        {
            var data = Data.TryGetValue(k, out var d) ? d : Data["file"];
            var geometry = System.Windows.Media.Geometry.Parse(data);
            geometry.Freeze();
            return geometry;
        });
    }

    /// <summary>A ready-to-place icon. The 24-unit design box is scaled to <paramref name="size"/>.</summary>
    public static Path Create(string key, double size, Brush stroke, double strokeWidth = 2)
    {
        var scale = size / 24.0;
        return new Path
        {
            Data = Geometry(key),
            Stroke = stroke,
            StrokeThickness = strokeWidth,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            Width = 24,
            Height = 24,
            Stretch = Stretch.None,
            LayoutTransform = new ScaleTransform(scale, scale),
            SnapsToDevicePixels = false,
            IsHitTestVisible = false,
        };
    }
}
