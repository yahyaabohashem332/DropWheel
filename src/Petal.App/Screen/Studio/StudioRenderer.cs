using Petal.Core.Recording;
using SkiaSharp;

namespace Petal.App.Screen.Studio;

/// <summary>
/// Draws the frames of a cinematic recording: the background with the recording's soft shadow (drawn once), the
/// recording in its rounded frame seen through the moving view, blurred along the way when the view moves fast,
/// the click rings and the pointer (blurred too when it is flung), the shortcuts pressed, and the camera's bubble.
/// </summary>
internal sealed class StudioRenderer : IDisposable
{
    private readonly StudioProject _project;
    private readonly StudioLook _look;
    private readonly PointerArt _art;
    private readonly SKImage? _back;
    private readonly SKImage? _wallpaper;
    private readonly float _radius;
    private readonly SKTypeface _keysFace = SKTypeface.FromFamilyName("Segoe UI", SKFontStyleWeight.SemiBold, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright);

    public int Width { get; }

    public int Height { get; }

    public RectD Content { get; }

    public PointerPlan Pointer { get; }

    public CameraPlan Camera { get; }

    public StudioRenderer(StudioProject project, int width, int height, PointerArt art, SKImage? wallpaper = null)
    {
        _project = project;
        _look = project.Edit.Look;
        _art = art;
        _wallpaper = wallpaper;
        Width = width;
        Height = height;
        Content = StudioLayout.Content(width, height, project.Width, project.Height, _look);
        _radius = (float)StudioLayout.Radius(width, height, _look);
        Pointer = new PointerPlan(project);
        Camera = new CameraPlan(project, Pointer, Content.Width / Content.Height);
        if (_look.Background != "blur")
            _back = Background();
    }

    private SKRect ContentRect => SKRect.Create((float)Content.X, (float)Content.Y, (float)Content.Width, (float)Content.Height);

    /// <summary>The background and the recording's shadow, which never change: drawn once.</summary>
    private SKImage Background()
    {
        using var surface = SKSurface.Create(new SKImageInfo(Width, Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Black);
        if (_look.Background == "wallpaper" && _wallpaper is not null)
            DrawCover(canvas, _wallpaper, SKRect.Create(Width, Height), blur: 0);
        else if (StudioBackgrounds.Gradient(_look.Background) is { } g)
        {
            using var shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(Width, Height), [g.From, g.To], SKShaderTileMode.Clamp);
            using var fill = new SKPaint { Shader = shader };
            canvas.DrawRect(0, 0, Width, Height, fill);
        }
        DrawShadow(canvas);
        return surface.Snapshot();
    }

    private void DrawShadow(SKCanvas canvas)
    {
        if (!_look.Framed || !_look.Shadow)
            return;
        float size = (float)Math.Min(Width, Height);
        using var shadow = new SKPaint
        {
            Color = new SKColor(0, 0, 0, 120),
            MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, size * 0.018f),
            IsAntialias = true,
        };
        var below = ContentRect;
        below.Offset(0, size * 0.008f);
        canvas.DrawRoundRect(below, _radius, _radius, shadow);
    }

    private static void DrawCover(SKCanvas canvas, SKImage image, SKRect into, float blur)
    {
        float scale = Math.Max(into.Width / image.Width, into.Height / image.Height);
        float w = image.Width * scale, h = image.Height * scale;
        using var paint = new SKPaint { IsAntialias = true };
        if (blur > 0)
            paint.ImageFilter = SKImageFilter.CreateBlur(blur, blur);
        canvas.DrawImage(image, SKRect.Create(into.MidX - w / 2, into.MidY - h / 2, w, h), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None), paint);
    }

    /// <summary>
    /// One finished frame. <paramref name="time"/> is the recording's moment; <paramref name="shutter"/> how much of
    /// the recording's time the frame stands for (the blur spreads over half of it).
    /// </summary>
    public void Draw(SKCanvas canvas, SKImage screen, double time, double shutter, SKImage? face = null)
    {
        if (_back is not null)
            canvas.DrawImage(_back, 0, 0, new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None), null);
        else
        {
            canvas.Clear(SKColors.Black);
            // The recording itself, blurred and dimmed, behind it.
            using (var dim = new SKPaint { Color = new SKColor(0, 0, 0, 70) })
            {
                DrawCover(canvas, screen, SKRect.Create(Width, Height), Math.Min(Width, Height) * 0.03f);
                canvas.DrawRect(0, 0, Width, Height, dim);
            }
            DrawShadow(canvas);
        }
        canvas.Save();
        if (_look.Framed)
            canvas.ClipRoundRect(new SKRoundRect(ContentRect, _radius, _radius), SKClipOperation.Intersect, antialias: true);
        else
            canvas.ClipRect(ContentRect);
        var states = Moments(time, shutter);
        var sampling = new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None);
        using (var paint = new SKPaint())
        {
            for (int i = 0; i < states.Count; i++)
            {
                // Each moment averaged in: the last of n drawn at 1/n.
                paint.Color = SKColors.White.WithAlpha((byte)Math.Round(255.0 / (i + 1)));
                canvas.Save();
                Look(canvas, states[i]);
                canvas.DrawImage(screen, 0, 0, sampling, paint);
                canvas.Restore();
            }
        }
        var now = Camera.At(time);
        canvas.Save();
        Look(canvas, now);
        float worldScale = (float)(Content.Width / Camera.View(now).Width);
        if (_look.ClickRings)
            DrawRings(canvas, time, worldScale);
        DrawPointer(canvas, time, shutter, worldScale);
        canvas.Restore();
        canvas.Restore();
        if (face is not null && _project.CameraPlaces.Count > 0)
            DrawBubble(canvas, face, Place(time));
        if (_look.ShowKeys)
            DrawKeys(canvas, time);
    }

    /// <summary>The view at a few moments across the frame when it moves enough to blur, else just the one.</summary>
    private List<CameraState> Moments(double time, double shutter)
    {
        var now = Camera.At(time);
        if (!_look.MotionBlur || shutter <= 0)
            return [now];
        double half = shutter / 4;
        var a = Camera.View(time - half);
        var b = Camera.View(time + half);
        // How far the picture slides across the frame, in its pixels.
        double scale = Content.Width / Camera.View(now).Width;
        double travel = Math.Max(Math.Abs(a.CenterX - b.CenterX) * scale, Math.Abs(a.CenterY - b.CenterY) * scale);
        travel = Math.Max(travel, Math.Abs(a.Width - b.Width) * scale);
        int n = Math.Clamp((int)Math.Ceiling(travel / 3), 1, 7);
        if (n == 1)
            return [now];
        var list = new List<CameraState>(n);
        for (int i = 0; i < n; i++)
            list.Add(Camera.At(time - half + 2 * half * i / (n - 1)));
        return list;
    }

    /// <summary>Recording pixels to the frame for a view.</summary>
    private void Look(SKCanvas canvas, CameraState state)
    {
        var view = Camera.View(state);
        float scale = (float)(Content.Width / view.Width);
        canvas.Translate((float)Content.X, (float)Content.Y);
        canvas.Scale(scale);
        canvas.Translate((float)-view.X, (float)-view.Y);
    }

    /// <summary>The pointer's height in the recording's pixels.</summary>
    private double PointerSize => 32 * Math.Max(0.5, _project.PixelScale) * Math.Max(0.5, _look.PointerSize);

    private void DrawPointer(SKCanvas canvas, double time, double shutter, float worldScale)
    {
        var p = Pointer.At(time);
        if (!Pointer.Any || p.Opacity < 0.01)
            return;
        var picture = _art.For(p.Kind, p.Shape);
        double size = PointerSize * (1 - 0.14 * p.Press);
        float scale = (float)(size / 32 / picture.PixelsPerPoint);
        var sampling = new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear);
        // Flung across the frame: a few fading copies behind it.
        double speed = Math.Sqrt(p.VelocityX * p.VelocityX + p.VelocityY * p.VelocityY) * worldScale;
        int ghosts = _look.MotionBlur && shutter > 0 ? Math.Clamp((int)(speed * shutter / 2 / 6), 0, 5) : 0;
        using var paint = new SKPaint();
        for (int g = ghosts; g >= 0; g--)
        {
            double back = shutter / 2 * g / Math.Max(1, ghosts);
            float x = (float)(p.X - p.VelocityX * back), y = (float)(p.Y - p.VelocityY * back);
            double fade = g == 0 ? 1 : 0.35 * (1 - g / (double)(ghosts + 1));
            paint.Color = SKColors.White.WithAlpha((byte)Math.Round(255 * p.Opacity * fade));
            canvas.Save();
            canvas.Translate(x, y);
            canvas.Scale(scale);
            canvas.Translate((float)-picture.HotX, (float)-picture.HotY);
            canvas.DrawImage(picture.Image, 0, 0, sampling, paint);
            canvas.Restore();
        }
    }

    private void DrawRings(SKCanvas canvas, double time, float worldScale)
    {
        const double life = 0.55;
        float unit = (float)(PointerSize / 32);
        foreach (var click in _project.Clicks)
        {
            double age = time - click.Time;
            if (age < 0 || age > life)
                continue;
            float p = (float)(age / life);
            float eased = 1 - (1 - p) * (1 - p) * (1 - p);
            var colour = click.Button switch { 2 => new SKColor(0x3B, 0x82, 0xF6), 3 => new SKColor(0x22, 0xC5, 0x5E), _ => new SKColor(0xF5, 0x9E, 0x0B) };
            float r = unit * (6 + 22 * eased);
            using var fill = new SKPaint { IsAntialias = true, Color = colour.WithAlpha((byte)(80 * (1 - p))) };
            using var ring = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = unit * 2.4f, Color = colour.WithAlpha((byte)(235 * (1 - p))) };
            canvas.DrawCircle((float)click.X, (float)click.Y, r, fill);
            canvas.DrawCircle((float)click.X, (float)click.Y, r, ring);
        }
    }

    /// <summary>The shortcuts pressed in the last moment, in dark pills at the bottom of the frame.</summary>
    private void DrawKeys(SKCanvas canvas, double time)
    {
        const double life = 1.5, fade = 0.2;
        var shown = _project.Keys.Where(k => time >= k.Time && time - k.Time < life).TakeLast(3).ToList();
        if (shown.Count == 0)
            return;
        float unit = Math.Min(Width, Height) / 1080f;
        using var font = new SKFont(_keysFace, 30 * unit);
        using var text = new SKPaint { IsAntialias = true, Color = SKColors.White };
        using var pill = new SKPaint { IsAntialias = true, Color = new SKColor(0x10, 0x10, 0x14, 0xD8) };
        float bottom = (float)Math.Min(Height - 24 * unit, Content.Bottom - 28 * unit);
        for (int i = shown.Count - 1; i >= 0; i--)
        {
            var key = shown[i];
            double age = time - key.Time;
            double alpha = Math.Min(1, Math.Min(age / fade * 2, (life - age) / fade));
            float w = font.MeasureText(key.Keys) + 44 * unit, h = 58 * unit;
            var rect = SKRect.Create(Width / 2f - w / 2, bottom - h, w, h);
            pill.Color = pill.Color.WithAlpha((byte)(0xD8 * alpha));
            text.Color = SKColors.White.WithAlpha((byte)(255 * alpha));
            canvas.DrawRoundRect(rect, h / 2, h / 2, pill);
            canvas.DrawText(key.Keys, rect.MidX, rect.MidY + font.Size * 0.35f, SKTextAlign.Center, font, text);
            bottom -= h + 10 * unit;
        }
    }

    private CameraPlace Place(double time)
    {
        var places = _project.CameraPlaces;
        var at = places[0];
        foreach (var p in places)
        {
            if (p.Time > time)
                break;
            at = p;
        }
        return at;
    }

    /// <summary>The camera in its round bubble, where it was over the recording; it doesn't zoom with the screen.</summary>
    private void DrawBubble(SKCanvas canvas, SKImage face, CameraPlace place)
    {
        double scale = Math.Min(Content.Width, Content.Height) / Math.Min(_project.Width, _project.Height);
        float d = (float)Math.Min(place.Diameter * scale, Math.Min(Content.Width, Content.Height) * 0.6);
        float r = d / 2;
        float cx = (float)Math.Clamp(Content.X + place.X / _project.Width * Content.Width, Content.X + r, Content.Right - r);
        float cy = (float)Math.Clamp(Content.Y + place.Y / _project.Height * Content.Height, Content.Y + r, Content.Bottom - r);
        using (var shadow = new SKPaint { IsAntialias = true, Color = new SKColor(0, 0, 0, 100), MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, Math.Max(2, d * 0.04f)) })
            canvas.DrawCircle(cx, cy + d * 0.02f, r, shadow);
        canvas.Save();
        canvas.ClipRoundRect(new SKRoundRect(SKRect.Create(cx - r, cy - r, d, d), r, r), SKClipOperation.Intersect, antialias: true);
        canvas.DrawImage(face, SKRect.Create(cx - r, cy - r, d, d), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
        canvas.Restore();
        using var ring = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, Color = SKColors.White.WithAlpha(235), StrokeWidth = Math.Max(2, d * 0.018f) };
        canvas.DrawCircle(cx, cy, r - ring.StrokeWidth / 2, ring);
    }

    public void Dispose()
    {
        _back?.Dispose();
        _keysFace.Dispose();
    }
}

/// <summary>The backgrounds a cinematic recording can sit on.</summary>
internal static class StudioBackgrounds
{
    /// <summary>The gradients, by key: two colours each, corner to corner.</summary>
    public static readonly (string Key, string Name, SKColor From, SKColor To)[] Gradients =
    [
        ("violet", "Violet", new SKColor(0x7B, 0x5C, 0xFF), new SKColor(0xD9, 0x6C, 0xF4)),
        ("ocean", "Ocean", new SKColor(0x06, 0xB6, 0xD4), new SKColor(0x25, 0x63, 0xEB)),
        ("sunset", "Sunset", new SKColor(0xF9, 0x73, 0x16), new SKColor(0xDB, 0x27, 0x77)),
        ("mint", "Mint", new SKColor(0x10, 0xB9, 0x81), new SKColor(0x0E, 0xA5, 0xE9)),
        ("peach", "Peach", new SKColor(0xFD, 0xBA, 0x74), new SKColor(0xF4, 0x72, 0xB6)),
        ("night", "Night", new SKColor(0x1E, 0x1B, 0x4B), new SKColor(0x5B, 0x21, 0xB6)),
        ("graphite", "Graphite", new SKColor(0x4B, 0x55, 0x63), new SKColor(0x11, 0x18, 0x27)),
        ("snow", "Snow", new SKColor(0xF1, 0xF5, 0xF9), new SKColor(0xCB, 0xD5, 0xE1)),
    ];

    /// <summary>A background's two colours; a colour of your own (#RRGGBB) is both.</summary>
    public static (SKColor From, SKColor To)? Gradient(string key)
    {
        if (key.StartsWith('#') && SKColor.TryParse(key, out var own))
            return (own, own);
        foreach (var g in Gradients)
            if (g.Key == key)
                return (g.From, g.To);
        return null;
    }

    /// <summary>The desktop's wallpaper picture, when Windows has one set.</summary>
    public static string? WallpaperPath()
    {
        var buffer = new char[520];
        if (!SystemParametersInfo(0x0073, buffer.Length, buffer, 0))
            return null;
        var path = new string(buffer).TrimEnd('\0');
        return path.Length > 0 && System.IO.File.Exists(path) ? path : null;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern bool SystemParametersInfo(int action, int size, char[] value, int flags);
}
