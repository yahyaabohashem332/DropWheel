using System.Windows;

namespace Petal.App.Actions.Float;

/// <summary>What a floating window shows, as far as its size goes: each kind keeps the size it was last given.</summary>
public enum FloatKind
{
    Picture,
    Pages,
    Sheet,
    Video,
    Sound,
    Text,
    Other,
}

/// <summary>The sums of a floating window: its first size, its zoom about the pointer, its ready sizes, its edges.</summary>
public static class FloatMath
{
    /// <summary>The least and most a picture or a page is magnified: 5 % to 800 %.</summary>
    public const double LeastZoom = 0.05, MostZoom = 8;

    /// <summary>The part of the screen a picture fills at most when it first floats at its own size.</summary>
    public const double PictureShare = 0.6;

    /// <summary>The window's header and the margin its shadow takes, in device-independent pixels.</summary>
    public const double Header = 38, Shadow = 10;

    /// <summary>
    /// A zoom about a point: the point of the content under <paramref name="at"/> stays under it. Gives the new zoom and
    /// the content's new offset (where its top left corner is in the view).
    /// </summary>
    public static (double Zoom, Point Offset) ZoomAbout(double zoom, Point offset, Point at, double factor)
    {
        double next = Math.Clamp(zoom * factor, LeastZoom, MostZoom);
        // The content point under the pointer, in the content's own pixels.
        double cx = (at.X - offset.X) / zoom, cy = (at.Y - offset.Y) / zoom;
        return (next, new Point(at.X - cx * next, at.Y - cy * next));
    }

    /// <summary>The zoom that shows all of a <paramref name="content"/> in a <paramref name="view"/>, never above its own size.</summary>
    public static double Fit(Size content, Size view)
    {
        if (content.Width <= 0 || content.Height <= 0 || view.Width <= 0 || view.Height <= 0)
            return 1;
        return Math.Clamp(Math.Min(1, Math.Min(view.Width / content.Width, view.Height / content.Height)), LeastZoom, MostZoom);
    }

    /// <summary>Where content of a size, at a zoom, sits centred in a view.</summary>
    public static Point Centred(Size content, double zoom, Size view) =>
        new((view.Width - content.Width * zoom) / 2, (view.Height - content.Height * zoom) / 2);

    /// <summary>
    /// The window's first size: a picture at its own size (no more than <see cref="PictureShare"/> of the screen,
    /// smaller kept in proportion), anything else at the size its kind was last given, or a size that suits it.
    /// Sizes are the window's, header and shadow included, never larger than the screen's work area.
    /// </summary>
    public static Size FirstSize(FloatKind kind, Size natural, Size work, Size? remembered)
    {
        Size size;
        if (kind == FloatKind.Picture && natural.Width > 0 && natural.Height > 0)
        {
            double scale = Math.Min(1, Math.Min(work.Width * PictureShare / natural.Width, work.Height * PictureShare / natural.Height));
            size = new Size(Math.Max(280, natural.Width * scale) + 2 * Shadow, Math.Max(200, natural.Height * scale) + Header + 2 * Shadow);
        }
        else if (remembered is { Width: > 0, Height: > 0 } kept)
            size = kept;
        else
            size = kind switch
            {
                FloatKind.Pages => new Size(Math.Min(720, work.Width * 0.45), work.Height * 0.85),
                FloatKind.Sheet => new Size(work.Width * 0.55, work.Height * 0.6),
                FloatKind.Video => VideoSize(natural, work),
                FloatKind.Sound => new Size(460, 250),
                FloatKind.Text => new Size(Math.Min(760, work.Width * 0.45), work.Height * 0.7),
                _ => new Size(560, 460),
            };
        return new Size(Math.Min(size.Width, work.Width), Math.Min(size.Height, work.Height));
    }

    /// <summary>A video a third of the screen wide, in its own proportion, with room for its controls.</summary>
    private static Size VideoSize(Size natural, Size work)
    {
        double ratio = natural.Width > 0 && natural.Height > 0 ? natural.Height / natural.Width : 9.0 / 16;
        double width = Math.Max(420, work.Width / 3);
        return new Size(width + 2 * Shadow, width * ratio + Header + 52 + 2 * Shadow);
    }

    /// <summary>A ready size: a share of the screen's work area, in the content's proportion for a picture.</summary>
    public static Size ShareOfScreen(double share, Size work, Size? picture)
    {
        if (picture is { Width: > 0, Height: > 0 } p)
        {
            double scale = Math.Min(work.Width * share / p.Width, (work.Height * share - Header) / p.Height);
            return new Size(p.Width * scale + 2 * Shadow, p.Height * scale + Header + 2 * Shadow);
        }
        return new Size(work.Width * share, work.Height * share);
    }

    /// <summary>A rectangle kept inside the work area: moved in first, and made smaller only when it can't fit.</summary>
    public static Rect Inside(Rect window, Rect work)
    {
        double width = Math.Min(window.Width, work.Width), height = Math.Min(window.Height, work.Height);
        double left = Math.Clamp(window.Left, work.Left, work.Right - width), top = Math.Clamp(window.Top, work.Top, work.Bottom - height);
        return new Rect(left, top, width, height);
    }

    /// <summary>Which edge or corner of the window a point is on, for resizing from any of them; None inside it.</summary>
    public static Edge EdgeAt(Point at, Size window, double inset, double grip)
    {
        bool left = at.X >= inset - grip && at.X < inset + grip;
        bool right = at.X <= window.Width - inset + grip && at.X > window.Width - inset - grip;
        bool top = at.Y >= inset - grip && at.Y < inset + grip;
        bool bottom = at.Y <= window.Height - inset + grip && at.Y > window.Height - inset - grip;
        return (top, bottom, left, right) switch
        {
            (true, _, true, _) => Edge.TopLeft,
            (true, _, _, true) => Edge.TopRight,
            (_, true, true, _) => Edge.BottomLeft,
            (_, true, _, true) => Edge.BottomRight,
            (true, _, _, _) => Edge.Top,
            (_, true, _, _) => Edge.Bottom,
            (_, _, true, _) => Edge.Left,
            (_, _, _, true) => Edge.Right,
            _ => Edge.None,
        };
    }

    /// <summary>A zoom as people read it: "125%".</summary>
    public static string Percent(double zoom) => Math.Round(zoom * 100).ToString(System.Globalization.CultureInfo.CurrentCulture) + "%";
}

/// <summary>An edge or a corner of a window.</summary>
public enum Edge
{
    None,
    Left,
    Right,
    Top,
    Bottom,
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight,
}
