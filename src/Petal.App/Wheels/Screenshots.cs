using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Petal.App.Input;
using Petal.App.Localization;
using Petal.App.Visuals;
using static Petal.App.Interop.NativeMethods;

namespace Petal.App.Wheels;

/// <summary>
/// Screenshots that go straight onto the conversion wheel: the whole screen under the pointer, or an
/// area dragged out over a still of it. Saved as PNG in Pictures\Screenshots, where Windows keeps its own.
/// </summary>
internal static class Screenshots
{
    public static string Folder => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshots");

    /// <summary>The monitor under the pointer, in physical pixels.</summary>
    public static (BitmapSource Picture, RECT Bounds)? CaptureScreenUnderPointer()
    {
        GetCursorPos(out var cursor);
        var (monitor, _, _) = MonitorAt(cursor);
        var picture = Capture(monitor.Left, monitor.Top, monitor.Width, monitor.Height);
        return picture is null ? null : (picture, monitor);
    }

    public static BitmapSource? Capture(int left, int top, int width, int height)
    {
        var pixels = Overlay.GlassCapture.CaptureScreen(left, top, width, height);
        if (pixels is null)
            return null;
        // The screen has no transparency; make sure nothing reads as see-through.
        for (int i = 3; i < pixels.Length; i += 4)
            pixels[i] = 255;
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>Saves a picture as "Screenshot 2026-10-21 16.40.05.png" and returns its path.</summary>
    public static string Save(BitmapSource picture)
    {
        Directory.CreateDirectory(Folder);
        var stamp = DateTime.Now.ToString("yyyy-MM-dd HH.mm.ss", System.Globalization.CultureInfo.InvariantCulture);
        var path = Petal.Core.Jobs.OutputNaming.Unique(Folder, $"Screenshot {stamp}", "png");
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(picture));
        using (var stream = File.Create(path))
            encoder.Save(stream);
        return path;
    }

    /// <summary>
    /// Shows a still of the screen under the pointer to drag an area on; Esc or a right-click cancels.
    /// <paramref name="done"/> gets the area's picture and its middle on the screen.
    /// </summary>
    public static void ChooseArea(Action<BitmapSource, ScreenPoint> done)
    {
        if (CaptureScreenUnderPointer() is not { } shot)
            return;
        new AreaWindow(shot.Picture, shot.Bounds, done).Show();
    }

    /// <summary>A still of one monitor, covering it, to drag out an area.</summary>
    private sealed class AreaWindow : Window
    {
        private readonly BitmapSource _picture;
        private readonly RECT _bounds;
        private readonly Action<BitmapSource, ScreenPoint> _done;
        private readonly Canvas _canvas = new();
        private readonly System.Windows.Shapes.Path _shade = new() { Fill = new SolidColorBrush(Color.FromArgb(0x78, 0, 0, 0)), IsHitTestVisible = false };
        private readonly Rectangle _frame = new() { Stroke = Brushes.White, StrokeThickness = 1, StrokeDashArray = [4, 3], IsHitTestVisible = false, Visibility = Visibility.Collapsed };
        private readonly Border _size = new() { CornerRadius = new CornerRadius(5), Padding = new Thickness(6, 2, 6, 3), Background = new SolidColorBrush(Color.FromArgb(0xD0, 0x20, 0x20, 0x24)), IsHitTestVisible = false, Visibility = Visibility.Collapsed };
        private readonly TextBlock _sizeText = new() { Foreground = Brushes.White, FontSize = 12, FontFamily = UiFonts.Text };
        private Point? _start;

        public AreaWindow(BitmapSource picture, RECT bounds, Action<BitmapSource, ScreenPoint> done)
        {
            _picture = picture;
            _bounds = bounds;
            _done = done;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = true;
            Cursor = Cursors.Cross;
            Title = L.T("Screenshot");
            L.Apply(this, mirror: false);
            Background = new ImageBrush(picture) { Stretch = Stretch.Fill };
            _size.Child = _sizeText;
            _canvas.Background = Brushes.Transparent;
            _canvas.Children.Add(_shade);
            _canvas.Children.Add(_frame);
            _canvas.Children.Add(_size);
            var hint = new Border
            {
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12, 6, 12, 7),
                Background = new SolidColorBrush(Color.FromArgb(0xD8, 0x20, 0x20, 0x24)),
                IsHitTestVisible = false,
                Child = new TextBlock { Text = L.T("Drag over the area to capture · Esc cancels"), Foreground = Brushes.White, FontSize = 13, FontFamily = UiFonts.Text, FlowDirection = L.Direction },
            };
            Canvas.SetTop(hint, 24);
            _canvas.Children.Add(hint);
            Content = _canvas;
            Loaded += (_, _) =>
            {
                Canvas.SetLeft(hint, (ActualWidth - hint.ActualWidth) / 2);
                _shade.Data = new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight));
                Activate();
                Focus();
            };
            SourceInitialized += (_, _) =>
                SetWindowPos(new WindowInteropHelper(this).Handle, new IntPtr(-1), _bounds.Left, _bounds.Top, _bounds.Width, _bounds.Height, SWP_SHOWWINDOW);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.Key == Key.Escape)
                Close();
        }

        protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
        {
            base.OnMouseRightButtonUp(e);
            Close();
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);
            _start = e.GetPosition(_canvas);
            CaptureMouse();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_start is not { } start) return;
            var area = new Rect(start, e.GetPosition(_canvas));
            _shade.Data = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight)), new RectangleGeometry(area));
            _frame.Visibility = Visibility.Visible;
            _frame.Width = area.Width;
            _frame.Height = area.Height;
            Canvas.SetLeft(_frame, area.X);
            Canvas.SetTop(_frame, area.Y);
            var px = Pixels(area);
            _sizeText.Text = $"{px.Width} × {px.Height}";
            _size.Visibility = Visibility.Visible;
            Canvas.SetLeft(_size, area.X);
            Canvas.SetTop(_size, Math.Max(0, area.Y - 26));
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonUp(e);
            if (_start is not { } start) return;
            ReleaseMouseCapture();
            var px = Pixels(new Rect(start, e.GetPosition(_canvas)));
            _start = null;
            if (px.Width < 4 || px.Height < 4)
                return;
            var crop = new CroppedBitmap(_picture, px);
            crop.Freeze();
            var middle = new ScreenPoint(_bounds.Left + px.X + px.Width / 2, _bounds.Top + px.Y + px.Height / 2);
            Close();
            _done(crop, middle);
        }

        /// <summary>An area in this window's units as pixels of the still.</summary>
        private Int32Rect Pixels(Rect area)
        {
            double sx = _picture.PixelWidth / Math.Max(1, ActualWidth), sy = _picture.PixelHeight / Math.Max(1, ActualHeight);
            int x = Math.Clamp((int)Math.Round(area.X * sx), 0, _picture.PixelWidth - 1);
            int y = Math.Clamp((int)Math.Round(area.Y * sy), 0, _picture.PixelHeight - 1);
            int w = Math.Clamp((int)Math.Round(area.Width * sx), 0, _picture.PixelWidth - x);
            int h = Math.Clamp((int)Math.Round(area.Height * sy), 0, _picture.PixelHeight - y);
            return new Int32Rect(x, y, w, h);
        }
    }
}
