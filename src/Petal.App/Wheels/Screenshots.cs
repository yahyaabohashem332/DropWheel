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
    public static string Folder => Settings.SavedPlaces.Folder(Settings.SavedKind.Screenshots);

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

    /// <summary>Where a picture waits until it has been covered (a private screenshot): Petal's temporary folder.</summary>
    private static string TemporaryFolder => System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Petal", "Private");

    /// <summary>Saves a picture with a screenshot's name, but in Petal's temporary folder, and returns its path.</summary>
    public static string SaveTemporary(BitmapSource picture)
    {
        Directory.CreateDirectory(TemporaryFolder);
        var stamp = DateTime.Now.ToString("yyyy-MM-dd HH.mm.ss", System.Globalization.CultureInfo.InvariantCulture);
        var path = Petal.Core.Jobs.OutputNaming.Unique(TemporaryFolder, $"Screenshot {stamp}", "png");
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(picture));
        using (var stream = File.Create(path))
            encoder.Save(stream);
        return path;
    }

    /// <summary>Deletes a picture saved by <see cref="SaveTemporary"/> (nothing else).</summary>
    public static void ForgetTemporary(string path)
    {
        if (!string.Equals(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path)), System.IO.Path.GetFullPath(TemporaryFolder), StringComparison.OrdinalIgnoreCase))
            return;
        try { File.Delete(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>A saved picture put on the clipboard, with the card that says where it is.</summary>
    public static void KeepFile(string path, string title)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.UriSource = new Uri(path);
        image.EndInit();
        image.Freeze();
        bool copied = Copy(image);
        Hud.HudCard.ShowSaved(copied ? title : "Screenshot saved", path, copied ? null : L.T("The clipboard is busy, so it wasn't copied."));
    }

    /// <summary>
    /// A screenshot taken to keep: saved, put on the Windows clipboard at once (to paste anywhere, as
    /// Win+Shift+S does), and a card says where it is with buttons to show it or open it. Returns its path.
    /// </summary>
    public static string Keep(BitmapSource picture, string title = "Screenshot saved and copied", string? note = null)
    {
        var path = Save(picture);
        bool copied = Copy(picture);
        Hud.HudCard.ShowSaved(copied ? title : "Screenshot saved", path, copied ? note : note ?? L.T("The clipboard is busy, so it wasn't copied."));
        return path;
    }

    /// <summary>
    /// The picture on the clipboard as a bitmap and as PNG (which browsers and Office prefer); false when
    /// another program holds the clipboard all through a few tries.
    /// </summary>
    public static bool Copy(BitmapSource picture)
    {
        var data = ClipboardData(picture);
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(data, copy: true);
                return true;
            }
            catch (System.Runtime.InteropServices.ExternalException)
            {
                Thread.Sleep(60);
            }
        }
        return false;
    }

    internal static DataObject ClipboardData(BitmapSource picture)
    {
        var data = new DataObject();
        data.SetImage(picture);
        var png = new MemoryStream();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(picture));
        encoder.Save(png);
        png.Position = 0;
        data.SetData("PNG", png);
        return data;
    }

    /// <summary>
    /// Shows a still of the screen under the pointer to drag an area on; Esc or a right-click cancels.
    /// <paramref name="done"/> gets the area's picture and its middle on the screen.
    /// </summary>
    public static void ChooseArea(Action<BitmapSource, ScreenPoint> done) =>
        ChooseArea("Drag over the area to capture · Esc cancels", false, (picture, area) =>
            done(picture, new ScreenPoint(area.Left + area.Width / 2, area.Top + area.Height / 2)));

    /// <summary>
    /// The same, for the other screen tools: <paramref name="done"/> gets the area's picture and where it is on
    /// the screen, in pixels. With <paramref name="wholeScreen"/>, Enter takes the whole monitor.
    /// </summary>
    public static void ChooseArea(string hint, bool wholeScreen, Action<BitmapSource, RECT> done)
    {
        if (CaptureScreenUnderPointer() is not { } shot)
            return;
        new AreaWindow(shot.Picture, shot.Bounds, L.T(hint), wholeScreen, done).Show();
    }

    /// <summary>A still of one monitor, covering it, to drag out an area.</summary>
    private sealed class AreaWindow : Window
    {
        private readonly BitmapSource _picture;
        private readonly RECT _bounds;
        private readonly Action<BitmapSource, RECT> _done;
        private readonly bool _wholeScreen;
        private readonly Canvas _canvas = new();
        private readonly System.Windows.Shapes.Path _shade = new() { Fill = new SolidColorBrush(Color.FromArgb(0x78, 0, 0, 0)), IsHitTestVisible = false };
        private readonly Rectangle _frame = new() { Stroke = Brushes.White, StrokeThickness = 1, StrokeDashArray = [4, 3], IsHitTestVisible = false, Visibility = Visibility.Collapsed };
        private readonly Border _size = new() { CornerRadius = new CornerRadius(5), Padding = new Thickness(6, 2, 6, 3), Background = new SolidColorBrush(Color.FromArgb(0xD0, 0x20, 0x20, 0x24)), IsHitTestVisible = false, Visibility = Visibility.Collapsed };
        private readonly TextBlock _sizeText = new() { Foreground = Brushes.White, FontSize = 12, FontFamily = UiFonts.Text };
        private Point? _start;

        public AreaWindow(BitmapSource picture, RECT bounds, string hintText, bool wholeScreen, Action<BitmapSource, RECT> done)
        {
            _picture = picture;
            _bounds = bounds;
            _done = done;
            _wholeScreen = wholeScreen;
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
                Child = new TextBlock { Text = hintText, Foreground = Brushes.White, FontSize = 13, FontFamily = UiFonts.Text, FlowDirection = L.Direction },
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
            else if (e.Key == Key.Return && _wholeScreen)
                Finish(new Int32Rect(0, 0, _picture.PixelWidth, _picture.PixelHeight));
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
            Finish(px);
        }

        private void Finish(Int32Rect px)
        {
            var crop = new CroppedBitmap(_picture, px);
            crop.Freeze();
            var area = new RECT { Left = _bounds.Left + px.X, Top = _bounds.Top + px.Y, Right = _bounds.Left + px.X + px.Width, Bottom = _bounds.Top + px.Y + px.Height };
            Close();
            _done(crop, area);
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
