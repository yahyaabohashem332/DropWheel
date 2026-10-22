using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Petal.App.Settings;
using Petal.Core.Imaging;
using Petal.Core.Util;
using SkiaSharp;

namespace Petal.App.Screen;

/// <summary>How a recording is polished: what sits behind it, how far the camera goes in, the pointer's size, click rings.</summary>
internal sealed record PolishOptions(string Background, double Zoom, double CursorScale, bool ClickRings)
{
    public static PolishOptions FromSettings()
    {
        var s = SettingsStore.Current;
        return new PolishOptions(s.PolishBackground, s.PolishZoom, s.PolishLargeCursor ? 1.6 : 1.15, s.PolishClickRings);
    }

    /// <summary>The backgrounds offered, by key: two colours each, corner to corner; "none" fills the frame.</summary>
    public static readonly (string Key, string Name, SKColor From, SKColor To)[] Backgrounds =
    [
        ("violet", "Violet", new SKColor(0x7B, 0x5C, 0xFF), new SKColor(0xD9, 0x6C, 0xF4)),
        ("ocean", "Ocean", new SKColor(0x06, 0xB6, 0xD4), new SKColor(0x25, 0x63, 0xEB)),
        ("sunset", "Sunset", new SKColor(0xF9, 0x73, 0x16), new SKColor(0xDB, 0x27, 0x77)),
        ("mint", "Mint", new SKColor(0x10, 0xB9, 0x81), new SKColor(0x0E, 0xA5, 0xE9)),
        ("graphite", "Graphite", new SKColor(0x4B, 0x55, 0x63), new SKColor(0x11, 0x18, 0x27)),
        ("none", "None", SKColors.Black, SKColors.Black),
    ];
}

/// <summary>
/// Turns a raw screen recording into a finished one, the way dedicated screen-video apps do: the
/// recording on a coloured background with rounded corners and a soft shadow, the camera gliding in where
/// you click and following the pointer, a larger, smoothed pointer drawn on top, and a ring at each click.
/// Every frame is drawn on this PC; the sound is kept as it was.
/// </summary>
internal static class RecordingPolish
{
    /// <summary>Draws one polished frame. <paramref name="time"/> is in seconds, for the click rings.</summary>
    public static void Compose(SKCanvas canvas, SKImage frame, int width, int height, CameraFrame camera, double time,
        IReadOnlyList<ClickSample> clicks, PolishOptions options)
    {
        var background = PolishOptions.Backgrounds.FirstOrDefault(b => b.Key == options.Background);
        bool framed = background.Key is not null and not "none";
        canvas.Clear(SKColors.Black);
        float pad = framed ? MathF.Round(Math.Min(width, height) * 0.06f) : 0;
        float scale = framed ? Math.Min((width - 2 * pad) / width, (height - 2 * pad) / height) : 1;
        var content = SKRect.Create((width - width * scale) / 2, (height - height * scale) / 2, width * scale, height * scale);
        float radius = framed ? Math.Min(width, height) * 0.022f : 0;
        if (framed)
        {
            using var shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(width, height), [background.From, background.To], SKShaderTileMode.Clamp);
            using var fill = new SKPaint { Shader = shader };
            canvas.DrawRect(0, 0, width, height, fill);
            using var shadow = new SKPaint
            {
                Color = new SKColor(0, 0, 0, 110),
                MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, Math.Max(4, pad * 0.35f)),
                IsAntialias = true,
            };
            var below = content;
            below.Offset(0, pad * 0.18f);
            canvas.DrawRoundRect(below, radius, radius, shadow);
        }
        canvas.Save();
        if (framed)
            canvas.ClipRoundRect(new SKRoundRect(content, radius, radius), SKClipOperation.Intersect, antialias: true);
        // Recording pixels to the screen: around the camera's centre, zoomed, then into the frame.
        canvas.Translate(content.Left, content.Top);
        canvas.Scale(scale);
        canvas.Translate(width / 2f, height / 2f);
        canvas.Scale((float)camera.Zoom);
        canvas.Translate((float)-camera.CenterX, (float)-camera.CenterY);
        canvas.DrawImage(frame, 0, 0, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
        if (options.ClickRings)
            DrawRings(canvas, time, clicks, scale * (float)camera.Zoom);
        if (camera.CursorVisible)
            DrawPointer(canvas, (float)camera.CursorX, (float)camera.CursorY, (float)options.CursorScale * Math.Max(1, height / 900f));
        canvas.Restore();
    }

    private static void DrawRings(SKCanvas canvas, double time, IReadOnlyList<ClickSample> clicks, float worldScale)
    {
        const double life = 0.5;
        foreach (var click in clicks)
        {
            double age = time - click.Time;
            if (age < 0 || age > life)
                continue;
            float p = (float)(age / life);
            float eased = 1 - (1 - p) * (1 - p);
            var colour = click.Button switch { 2 => new SKColor(0x3B, 0x82, 0xF6), 3 => new SKColor(0x22, 0xC5, 0x5E), _ => new SKColor(0xF5, 0x9E, 0x0B) };
            using var ring = new SKPaint
            {
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 3.5f / Math.Max(0.5f, worldScale) * 1.4f,
                Color = colour.WithAlpha((byte)(230 * (1 - p))),
            };
            using var fill = new SKPaint { IsAntialias = true, Color = colour.WithAlpha((byte)(70 * (1 - p))) };
            float r = (8 + 26 * eased) / Math.Max(0.5f, worldScale) * 1.4f;
            canvas.DrawCircle((float)click.X, (float)click.Y, r, fill);
            canvas.DrawCircle((float)click.X, (float)click.Y, r, ring);
        }
    }

    private static readonly SKPath Arrow = MakeArrow();

    // The classic arrow, its tip at 0,0.
    private static SKPath MakeArrow() => SKPath.ParseSvgPathData("M0 0 L0 17.5 L4.3 13.4 L7.1 19.8 L9.9 18.6 L7.1 12.3 L12.9 12.3 Z");

    /// <summary>A crisp pointer at the smoothed place, white with a dark edge and a soft shadow.</summary>
    private static void DrawPointer(SKCanvas canvas, float x, float y, float size)
    {
        canvas.Save();
        canvas.Translate(x, y);
        canvas.Scale(size);
        using var shadow = new SKPaint { IsAntialias = true, Color = new SKColor(0, 0, 0, 90), MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 1.6f) };
        canvas.Save();
        canvas.Translate(0.8f, 1.6f);
        canvas.DrawPath(Arrow, shadow);
        canvas.Restore();
        using var fill = new SKPaint { IsAntialias = true, Color = SKColors.White };
        using var edge = new SKPaint { IsAntialias = true, Color = new SKColor(0x16, 0x16, 0x1A), Style = SKPaintStyle.Stroke, StrokeWidth = 1.3f, StrokeJoin = SKStrokeJoin.Round };
        canvas.DrawPath(Arrow, fill);
        canvas.DrawPath(Arrow, edge);
        canvas.Restore();
    }

    /// <summary>
    /// Reads every frame of <paramref name="input"/>, draws it polished, and writes <paramref name="output"/>
    /// with the input's sound. False when FFmpeg fails (the caller keeps the raw recording then).
    /// </summary>
    public static async Task<bool> RenderAsync(string input, string output, int width, int height, double fps,
        IReadOnlyList<CursorSample> cursor, IReadOnlyList<ClickSample> clicks, PolishOptions options, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        if (ToolLocator.Ffmpeg is not { } ffmpeg || cursor.Count == 0)
            return false;
        var camera = CameraPath.Build(fps, width, height, cursor, clicks, options.Zoom);
        var decode = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { "-v", "error", "-i", input, "-f", "rawvideo", "-pix_fmt", "bgra", "-" })
            decode.ArgumentList.Add(a);
        var encode = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardError = true };
        foreach (var a in new[]
        {
            "-v", "error", "-f", "rawvideo", "-pix_fmt", "bgra", "-s", $"{width}x{height}", "-framerate", fps.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-i", "-", "-i", input, "-map", "0:v", "-map", "1:a?", "-c:v", "libx264", "-preset", "veryfast", "-crf", "19", "-pix_fmt", "yuv420p",
            "-c:a", "copy", "-shortest", "-movflags", "+faststart", "-y", output,
        })
            encode.ArgumentList.Add(a);
        using var reader = Process.Start(decode);
        using var writer = Process.Start(encode);
        if (reader is null || writer is null)
            return false;
        _ = reader.StandardError.ReadToEndAsync(cancellationToken);
        _ = writer.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await Task.Run(() =>
            {
                var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
                int size = width * height * 4;
                var buffer = new byte[size];
                var source = reader.StandardOutput.BaseStream;
                var sink = writer.StandardInput.BaseStream;
                using var target = new SKBitmap(info);
                using var canvas = new SKCanvas(target);
                var gc = GCHandle.Alloc(buffer, GCHandleType.Pinned);
                try
                {
                    using var pixmap = new SKPixmap(info, gc.AddrOfPinnedObject(), width * 4);
                    int frame = 0;
                    while (ReadFull(source, buffer))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        // The screen has no transparency.
                        for (int i = 3; i < size; i += 4)
                            buffer[i] = 255;
                        using (var image = SKImage.FromPixels(pixmap))
                        {
                            var cam = camera[Math.Min(frame, camera.Count - 1)];
                            Compose(canvas, image, width, height, cam, frame / fps, clicks, options);
                        }
                        canvas.Flush();
                        var bytes = target.GetPixelSpan();
                        sink.Write(bytes);
                        frame++;
                        if (frame % 15 == 0)
                            progress?.Report(Math.Min(1, (double)frame / Math.Max(1, camera.Count)));
                    }
                }
                finally
                {
                    gc.Free();
                    sink.Close();
                }
            }, cancellationToken);
            await writer.WaitForExitAsync(cancellationToken);
            await reader.WaitForExitAsync(cancellationToken);
            return writer.ExitCode == 0 && File.Exists(output) && new FileInfo(output).Length > 0;
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or InvalidOperationException)
        {
            foreach (var p in new[] { reader, writer })
                try { if (!p.HasExited) p.Kill(); } catch (InvalidOperationException) { }
            return false;
        }
    }

    private static bool ReadFull(Stream stream, byte[] buffer)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int n = stream.Read(buffer, read, buffer.Length - read);
            if (n == 0)
                return false;
            read += n;
        }
        return true;
    }
}
