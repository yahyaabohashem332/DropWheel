using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Petal.App.Input;
using Petal.App.Localization;
using Petal.Core.Imaging;
using static Petal.App.Interop.NativeMethods;

namespace Petal.App.Screen;

/// <summary>
/// A screenshot of a whole page that doesn't fit on the screen: Petal scrolls the area chosen and joins
/// what comes into view into one tall picture, until the page ends (or Esc, or Stop). Sticky headers and
/// footers appear once. The picture goes to Pictures\Screenshots and the conversion wheel opens on it.
/// </summary>
internal static class ScrollCapture
{
    private const int MaxFrames = 80, MaxHeight = 30000;
    private static bool _running;
    private static volatile bool _stop;

    public static bool IsRunning => _running;

    public static void Toggle(Action<string, ScreenPoint>? openFiles)
    {
        if (_running)
        {
            _stop = true;
            return;
        }
        Wheels.Screenshots.ChooseArea("Drag over the part of the window that scrolls · Esc cancels", false, (picture, area) => _ = RunAsync(area, openFiles));
    }

    private static async Task RunAsync(RECT area, Action<string, ScreenPoint>? openFiles)
    {
        _running = true;
        _stop = false;
        var monitor = ScreenNative.Describe(MonitorFromPoint(new POINT { X = area.Left + 1, Y = area.Top + 1 }, 2));
        var frame = new MonitorWindow(monitor.Bounds, clickThrough: true, noActivate: true, hideFromCapture: true) { Title = L.T("Scrolling Screenshot") };
        var canvas = new Canvas();
        frame.Content = canvas;
        var status = new TextBlock { Text = L.T("Scrolling… Esc stops"), Foreground = Brushes.White, FontSize = 13, Margin = new Thickness(14, 8, 14, 9) };
        frame.Loaded += (_, _) =>
        {
            var rect = new Rect(frame.ToLocal(new ScreenPoint(area.Left, area.Top)), frame.ToLocal(new ScreenPoint(area.Right, area.Bottom)));
            var line = new Rectangle { Width = rect.Width + 4, Height = rect.Height + 4, Stroke = new SolidColorBrush(Visuals.Theme.Current.Accent), StrokeThickness = 2, StrokeDashArray = [5, 3] };
            Canvas.SetLeft(line, rect.X - 2);
            Canvas.SetTop(line, rect.Y - 2);
            canvas.Children.Add(line);
            var pill = ScreenUi.Strip(status, 14);
            pill.Padding = new Thickness(0);
            canvas.Children.Add(pill);
            pill.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(pill, rect.X + (rect.Width - pill.DesiredSize.Width) / 2);
            Canvas.SetTop(pill, rect.Bottom + 10 + pill.DesiredSize.Height < frame.ActualHeight ? rect.Bottom + 10 : rect.Y + 10);
        };
        frame.Show();
        using var escape = ScreenHooks.Escape(() => _stop = true);
        GetCursorPos(out var before);
        var middle = new POINT { X = area.Left + area.Width / 2, Y = area.Top + area.Height / 2 };
        var stitcher = new ScrollStitcher(area.Width, area.Height);
        string? problem = null;
        try
        {
            ScreenNative.SetCursorPos(middle.X, middle.Y);
            await Task.Delay(150);
            int still = 0;
            for (int i = 0; i < MaxFrames && !_stop && stitcher.Height < MaxHeight; i++)
            {
                var pixels = await SettledFrameAsync(area);
                if (pixels is null)
                    break;
                int added = stitcher.Add(pixels);
                if (added < 0)
                {
                    problem = L.T("The page changed while scrolling; the part so far is kept.");
                    break;
                }
                // The page didn't move twice in a row: its end.
                still = added == 0 ? still + 1 : 0;
                if (still >= 2)
                    break;
                // Someone moved the mouse away: they want it back.
                GetCursorPos(out var now);
                if (Math.Abs(now.X - middle.X) > 12 || Math.Abs(now.Y - middle.Y) > 12)
                    break;
                status.Text = L.F("Scrolling… {0} px · Esc stops", stitcher.Height);
                Scroll(Math.Clamp(area.Height / 260, 1, 6));
                await Task.Delay(120);
            }
        }
        finally
        {
            frame.Close();
            ScreenNative.SetCursorPos(before.X, before.Y);
            _running = false;
        }
        if (stitcher.Height == 0)
            return;
        var bitmap = BitmapSource.Create(stitcher.Width, stitcher.Height, 96, 96, PixelFormats.Bgra32, null, stitcher.Result(), stitcher.Width * 4);
        bitmap.Freeze();
        // Saved, copied, and a card with where it is (and what stopped the scrolling, if anything did).
        var path = Wheels.Screenshots.Keep(bitmap, "Scrolling screenshot saved and copied", problem);
        var at = new ScreenPoint(middle.X, middle.Y);
        openFiles?.Invoke(path, at);
    }

    /// <summary>The area once it stops moving (smooth scrolling takes a moment to settle).</summary>
    private static async Task<byte[]?> SettledFrameAsync(RECT area)
    {
        byte[]? last = null;
        for (int tries = 0; tries < 8; tries++)
        {
            var pixels = Overlay.GlassCapture.CaptureScreen(area.Left, area.Top, area.Width, area.Height);
            if (pixels is null)
                return last;
            if (last is not null && pixels.AsSpan().SequenceEqual(last))
                return pixels;
            last = pixels;
            await Task.Delay(70);
        }
        return last;
    }

    private static void Scroll(int notches)
    {
        var input = new INPUT
        {
            type = INPUT_MOUSE,
            u = new INPUTUNION { mi = new MOUSEINPUT { mouseData = unchecked((uint)(-120 * notches)), dwFlags = 0x0800 } },
        };
        SendInput(1, [input], Marshal.SizeOf<INPUT>());
    }
}
