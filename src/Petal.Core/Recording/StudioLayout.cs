namespace Petal.Core.Recording;

/// <summary>A rectangle in pixels.</summary>
public readonly record struct RectD(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;

    public double Bottom => Y + Height;

    public double CenterX => X + Width / 2;

    public double CenterY => Y + Height / 2;
}

/// <summary>
/// Where things go in the finished frame: its size from the shape and quality chosen, the recording's place in
/// it inside the background's margin, and how much of the recording the view shows when it isn't zoomed in.
/// </summary>
public static class StudioLayout
{
    public static readonly string[] Shapes = ["recorded", "16:9", "9:16", "1:1", "4:5"];

    /// <summary>A shape's width over its height.</summary>
    public static double Aspect(string shape, int width, int height) => shape switch
    {
        "16:9" => 16 / 9.0,
        "9:16" => 9 / 16.0,
        "1:1" => 1,
        "4:5" => 0.8,
        _ => width / (double)Math.Max(1, height),
    };

    /// <summary>The finished video's size: the quality is its shorter side, rounded to even numbers as video needs.</summary>
    public static (int Width, int Height) OutputSize(int width, int height, StudioLook look, StudioOutput output)
    {
        double aspect = Aspect(look.Shape, width, height);
        int shorter = output.Quality switch
        {
            "1080" => 1080,
            "1440" => 1440,
            "2160" => 2160,
            _ => Auto(width, height, look),
        };
        double w = aspect >= 1 ? shorter * aspect : shorter;
        double h = aspect >= 1 ? shorter : shorter / aspect;
        return (Even(w), Even(h));
    }

    /// <summary>The standard size that keeps the recording's detail: 1080, or more for a larger recording.</summary>
    private static int Auto(int width, int height, StudioLook look)
    {
        // The recording's shorter side as it will be drawn: a tall frame shows a landscape recording by its height.
        double aspect = Aspect(look.Shape, width, height);
        double needed = aspect >= 1 ? Math.Min(height, width / aspect) : Math.Min(width, height * aspect);
        if (look.Framed)
            needed /= 1 - 2 * Math.Clamp(look.Padding, 0, 0.2);
        // A little smaller than the recording is still sharp; much smaller loses its small text.
        return needed > 1700 ? 2160 : needed > 1300 ? 1440 : 1080;
    }

    private static int Even(double v) => Math.Max(2, (int)Math.Round(v / 2) * 2);

    /// <summary>The background's margin in pixels (none without a background).</summary>
    public static double Margin(int width, int height, StudioLook look) =>
        look.Framed ? Math.Round(Math.Min(width, height) * Math.Clamp(look.Padding, 0, 0.2)) : 0;

    /// <summary>The corners' radius in pixels.</summary>
    public static double Radius(int width, int height, StudioLook look) =>
        look.Framed ? Math.Min(width, height) * Math.Clamp(look.Roundness, 0, 0.08) : 0;

    /// <summary>
    /// Where the recording is drawn in the frame. With the shape it was recorded in, the recording keeps its own
    /// proportions inside the margin; any other shape fills the frame inside the margin and the view is cut to it.
    /// </summary>
    public static RectD Content(int frameWidth, int frameHeight, int recordingWidth, int recordingHeight, StudioLook look)
    {
        double margin = Margin(frameWidth, frameHeight, look);
        double w = Math.Max(2, frameWidth - 2 * margin), h = Math.Max(2, frameHeight - 2 * margin);
        if (look.Shape == "recorded")
        {
            double aspect = recordingWidth / (double)Math.Max(1, recordingHeight);
            if (w / h > aspect)
                w = h * aspect;
            else
                h = w / aspect;
        }
        return new RectD((frameWidth - w) / 2, (frameHeight - h) / 2, w, h);
    }

    /// <summary>The part of the recording the view shows when it isn't zoomed in: the largest of the content's proportions.</summary>
    public static (double Width, double Height) BaseView(int recordingWidth, int recordingHeight, double aspect)
    {
        double w = recordingWidth, h = recordingHeight;
        if (w / h > aspect)
            w = h * aspect;
        else
            h = w / aspect;
        return (w, h);
    }
}
