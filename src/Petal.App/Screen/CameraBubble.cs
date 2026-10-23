using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Petal.App.Localization;
using Petal.App.Settings;
using Petal.App.Visuals;
using Petal.Core.Util;
using SkiaSharp;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.MediaProperties;
using static Petal.App.Interop.NativeMethods;

namespace Petal.App.Screen;

/// <summary>Where the camera's bubble was from a moment of a recording on: its middle and width, in the recording's pixels.</summary>
internal readonly record struct BubbleSample(double Time, double X, double Y, double Diameter);

/// <summary>The camera's own video (square, <paramref name="Side"/> pixels, one frame for each frame of the screen) and where its bubble sat.</summary>
internal sealed record CameraOverlay(string Video, int Side, IReadOnlyList<BubbleSample> Places)
{
    /// <summary>Where the bubble was at a time (seconds): the last place it was moved to before then.</summary>
    public BubbleSample At(double time)
    {
        var place = Places[0];
        foreach (var p in Places)
        {
            if (p.Time > time)
                break;
            place = p;
        }
        return place;
    }
}

/// <summary>
/// The webcam as a square, mirrored picture (the way people see themselves), kept fresh as it comes. Nothing
/// is kept or sent anywhere: the bubble shows it, and a recording copies it into its own video.
/// </summary>
internal sealed class CameraFeed : IDisposable
{
    public const int Side = 480;

    private readonly MediaCapture _capture;
    private readonly MediaFrameReader _reader;
    private readonly object _lock = new();
    private readonly byte[] _latest = new byte[Side * Side * 4];
    private readonly SKBitmap _square = new(new SKImageInfo(Side, Side, SKColorType.Bgra8888, SKAlphaType.Premul));
    private byte[] _raw = [];
    private long _frames;
    private volatile bool _disposed;
    private bool _gone;

    /// <summary>A new picture arrived (on a worker thread).</summary>
    public event Action? FrameReady;

    /// <summary>How many pictures came so far.</summary>
    public long Frames => Interlocked.Read(ref _frames);

    private CameraFeed(MediaCapture capture, MediaFrameReader reader)
    {
        _capture = capture;
        _reader = reader;
        _reader.FrameArrived += OnFrame;
    }

    /// <summary>Starts the camera: the feed, or why it couldn't start (in words for the person).</summary>
    public static async Task<(CameraFeed? Feed, string? Problem)> StartAsync()
    {
        MediaFrameSourceGroup? group = null;
        MediaFrameSourceInfo? info = null;
        try
        {
            foreach (var g in await MediaFrameSourceGroup.FindAllAsync())
            {
                var colour = g.SourceInfos.FirstOrDefault(i => i.SourceKind == MediaFrameSourceKind.Color && i.MediaStreamType is MediaStreamType.VideoPreview or MediaStreamType.VideoRecord);
                if (colour is null)
                    continue;
                // The camera on the front of the screen, when one says where it is.
                bool front = colour.DeviceInformation?.EnclosureLocation?.Panel == Windows.Devices.Enumeration.Panel.Front;
                if (group is null || front)
                {
                    group = g;
                    info = colour;
                }
                if (front)
                    break;
            }
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or InvalidOperationException)
        {
            Log.Info($"Cameras couldn't be listed: {ex.Message}");
        }
        if (group is null || info is null)
            return (null, L.T("No camera was found on this PC."));
        foreach (var sharing in new[] { MediaCaptureSharingMode.ExclusiveControl, MediaCaptureSharingMode.SharedReadOnly })
        {
            var capture = new MediaCapture();
            CameraFeed? started = null;
            try
            {
                await capture.InitializeAsync(new MediaCaptureInitializationSettings
                {
                    SourceGroup = group,
                    SharingMode = sharing,
                    MemoryPreference = MediaCaptureMemoryPreference.Cpu,
                    StreamingCaptureMode = StreamingCaptureMode.Video,
                });
                var source = capture.FrameSources[info.Id];
                if (sharing == MediaCaptureSharingMode.ExclusiveControl && Choose(source.SupportedFormats) is { } format)
                    await source.SetFormatAsync(format);
                var reader = await capture.CreateFrameReaderAsync(source, MediaEncodingSubtypes.Bgra8);
                reader.AcquisitionMode = MediaFrameReaderAcquisitionMode.Realtime;
                var feed = started = new CameraFeed(capture, reader);
                if (await reader.StartAsync() == MediaFrameReaderStartStatus.Success)
                    return (feed, null);
                feed.Dispose();
            }
            catch (UnauthorizedAccessException)
            {
                capture.Dispose();
                return (null, L.T("Windows doesn't let apps use the camera. Turn on camera access for desktop apps in Settings › Privacy › Camera."));
            }
            catch (Exception ex)
            {
                // Camera drivers throw all kinds of errors: each is a camera that didn't start.
                Log.Info($"The camera didn't start ({sharing}): {ex.Message}");
                if (started is not null)
                    started.Dispose();
                else
                    capture.Dispose();
            }
        }
        return (null, L.T("The camera may be in use by another app."));
    }

    /// <summary>A size big enough for a sharp bubble without wasting work: the smallest at least 640 wide, near 30 frames a second.</summary>
    private static MediaFrameFormat? Choose(IReadOnlyList<MediaFrameFormat> formats)
    {
        static double Rate(MediaFrameFormat f) => f.FrameRate.Denominator == 0 ? 0 : f.FrameRate.Numerator / (double)f.FrameRate.Denominator;
        return formats
            .Where(f => f.VideoFormat.Width >= 640 && f.VideoFormat.Width <= 1920 && Rate(f) >= 14)
            .OrderBy(f => Math.Abs(Rate(f) - 30) < 3 ? 0 : 1)
            .ThenBy(f => f.VideoFormat.Width)
            .FirstOrDefault();
    }

    private void OnFrame(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
    {
        if (_disposed)
            return;
        try
        {
            using var frame = sender.TryAcquireLatestFrame();
            var bitmap = frame?.VideoMediaFrame?.SoftwareBitmap;
            if (bitmap is null)
                return;
            using var converted = bitmap.BitmapPixelFormat == BitmapPixelFormat.Bgra8 ? null : SoftwareBitmap.Convert(bitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
            var source = converted ?? bitmap;
            int w = source.PixelWidth, h = source.PixelHeight;
            if (_raw.Length != w * h * 4)
                _raw = new byte[w * h * 4];
            source.CopyToBuffer(_raw.AsBuffer());
            Square(w, h);
            Interlocked.Increment(ref _frames);
            FrameReady?.Invoke();
        }
        catch (Exception ex) when (ex is COMException or ObjectDisposedException or InvalidOperationException or ArgumentException)
        {
            // A frame that can't be read is skipped; the next one comes in a moment.
        }
    }

    /// <summary>The middle square of the picture, mirrored and scaled to <see cref="Side"/>, into the latest picture.</summary>
    private void Square(int w, int h)
    {
        var handle = GCHandle.Alloc(_raw, GCHandleType.Pinned);
        try
        {
            // Cameras send no transparency.
            using var pixmap = new SKPixmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Opaque), handle.AddrOfPinnedObject(), w * 4);
            using var image = SKImage.FromPixels(pixmap);
            int s = Math.Min(w, h);
            lock (_lock)
            {
                if (_gone)
                    return;
                using (var canvas = new SKCanvas(_square))
                {
                    canvas.Scale(-1, 1, Side / 2f, 0);
                    canvas.DrawImage(image, SKRect.Create((w - s) / 2, (h - s) / 2, s, s), SKRect.Create(0, 0, Side, Side), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
                }
                _square.GetPixelSpan().CopyTo(_latest);
            }
        }
        finally
        {
            handle.Free();
        }
    }

    /// <summary>Copies the latest picture (BGRA, <see cref="Side"/> square); false before the first.</summary>
    public bool CopyLatest(byte[] into)
    {
        if (Frames == 0)
            return false;
        lock (_lock)
            Buffer.BlockCopy(_latest, 0, into, 0, _latest.Length);
        return true;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _reader.FrameArrived -= OnFrame;
        var reader = _reader;
        var capture = _capture;
        // Stopping waits for the camera; it mustn't hold up the screen.
        _ = Task.Run(async () =>
        {
            try { await reader.StopAsync(); } catch (Exception ex) when (ex is COMException or ObjectDisposedException or InvalidOperationException) { }
            reader.Dispose();
            capture.Dispose();
            lock (_lock)
            {
                _gone = true;
                _square.Dispose();
            }
        });
    }
}

/// <summary>
/// The camera's round bubble over the recorded area: dragged anywhere, sized with the mouse wheel. It stays
/// out of the screen's frames; the recording draws the camera itself where the bubble is, sharper.
/// </summary>
internal sealed class CameraBubble : Window
{
    private readonly CameraFeed _feed;
    private readonly RECT _area;
    private readonly WriteableBitmap _picture = new(CameraFeed.Side, CameraFeed.Side, 96, 96, PixelFormats.Bgra32, null);
    private readonly byte[] _pixels = new byte[CameraFeed.Side * CameraFeed.Side * 4];
    private readonly Image _image = new() { Stretch = Stretch.UniformToFill };
    private readonly Ellipse _ring = new() { Stroke = Brushes.White, StrokeThickness = 3 };
    private readonly FrameworkElement _waiting = Icons.Create("video", 30, new SolidColorBrush(Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF)), 1.8);
    private IntPtr _handle;
    private int _pending;
    private int _diameter;

    /// <summary>The bubble was moved or resized.</summary>
    public event Action? Moved;

    public CameraBubble(CameraFeed feed, RECT area)
    {
        _feed = feed;
        _area = area;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        // Placed and sized in pixels as soon as it has a handle; this only keeps the first frame small.
        Width = Height = 120;
        Left = Top = -10000;
        Cursor = Cursors.SizeAll;
        Title = L.T("Camera");
        ToolTip = L.T("Drag to move · scroll to resize");
        _image.Source = _picture;
        _waiting.HorizontalAlignment = HorizontalAlignment.Center;
        _waiting.VerticalAlignment = VerticalAlignment.Center;
        var face = new Grid { Children = { new Ellipse { Fill = new SolidColorBrush(Color.FromRgb(0x24, 0x24, 0x2C)) }, _waiting, _image, _ring } };
        Content = face;
        SizeChanged += (_, _) =>
        {
            _image.Clip = new EllipseGeometry(new Rect(0, 0, ActualWidth, ActualHeight));
            _ring.StrokeThickness = Math.Max(2, ActualWidth * 0.018);
        };
        MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState != MouseButtonState.Pressed)
                return;
            try { DragMove(); } catch (InvalidOperationException) { }
            Remember();
        };
        MouseWheel += (_, e) => Resize(e.Delta > 0 ? 1.1 : 1 / 1.1);
        LocationChanged += (_, _) => Moved?.Invoke();
        SourceInitialized += (_, _) =>
        {
            _handle = new WindowInteropHelper(this).Handle;
            ScreenNative.SetWindowDisplayAffinity(_handle, ScreenNative.WDA_EXCLUDEFROMCAPTURE);
            var ex = (long)GetWindowLongPtr(_handle, GWL_EXSTYLE) | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
            SetWindowLongPtr(_handle, GWL_EXSTYLE, new IntPtr(ex));
            var s = SettingsStore.Current;
            _diameter = Fit(s.RecordCameraSize * Math.Min(area.Width, area.Height));
            Place(area.Left + s.RecordCameraX * area.Width, area.Top + s.RecordCameraY * area.Height);
        };
        _feed.FrameReady += OnFrame;
        Closed += (_, _) => _feed.FrameReady -= OnFrame;
    }

    /// <summary>Where the bubble is now, in the recording's pixels.</summary>
    public BubbleSample Sample(double time)
    {
        GetWindowRect(_handle, out var r);
        return new BubbleSample(time, (r.Left + r.Right) / 2.0 - _area.Left, (r.Top + r.Bottom) / 2.0 - _area.Top, r.Right - r.Left);
    }

    /// <summary>A size that reads as a face and doesn't cover the screen.</summary>
    private int Fit(double diameter) => (int)Math.Clamp(diameter, 96, Math.Max(96, Math.Min(_area.Width, _area.Height) * 0.6));

    /// <summary>Centres the bubble on a point, kept inside the recorded area.</summary>
    private void Place(double cx, double cy)
    {
        double r = _diameter / 2.0;
        cx = Math.Clamp(cx, _area.Left + r, Math.Max(_area.Left + r, _area.Right - r));
        cy = Math.Clamp(cy, _area.Top + r, Math.Max(_area.Top + r, _area.Bottom - r));
        SetWindowPos(_handle, HWND_TOPMOST, (int)Math.Round(cx - r), (int)Math.Round(cy - r), _diameter, _diameter, SWP_NOACTIVATE);
        Moved?.Invoke();
    }

    private void Resize(double factor)
    {
        GetWindowRect(_handle, out var r);
        _diameter = Fit(_diameter * factor);
        Place((r.Left + r.Right) / 2.0, (r.Top + r.Bottom) / 2.0);
        Remember();
    }

    /// <summary>The next recording puts the bubble where this one is, at this size.</summary>
    private void Remember()
    {
        GetWindowRect(_handle, out var r);
        var s = SettingsStore.Current;
        s.RecordCameraX = Math.Clamp(((r.Left + r.Right) / 2.0 - _area.Left) / Math.Max(1, _area.Width), 0, 1);
        s.RecordCameraY = Math.Clamp(((r.Top + r.Bottom) / 2.0 - _area.Top) / Math.Max(1, _area.Height), 0, 1);
        s.RecordCameraSize = Math.Clamp(_diameter / (double)Math.Max(1, Math.Min(_area.Width, _area.Height)), 0.05, 0.6);
        SettingsStore.Save();
    }

    /// <summary>A new picture: shown on the UI thread, skipping pictures while one is still being drawn.</summary>
    private void OnFrame()
    {
        if (Interlocked.Exchange(ref _pending, 1) == 1)
            return;
        Dispatcher.BeginInvoke(() =>
        {
            if (_feed.CopyLatest(_pixels))
            {
                _picture.WritePixels(new Int32Rect(0, 0, CameraFeed.Side, CameraFeed.Side), _pixels, CameraFeed.Side * 4, 0);
                _waiting.Visibility = Visibility.Collapsed;
            }
            Interlocked.Exchange(ref _pending, 0);
        });
    }
}

/// <summary>The camera written to its own video while the screen records, frame for frame in time with it.</summary>
internal sealed class CameraTrack : IDisposable
{
    private readonly CameraFeed _feed;
    private readonly Stopwatch _clock;
    private readonly int _fps;
    private Process? _ffmpeg;
    private Thread? _writer;
    private volatile bool _stopping;

    public CameraTrack(CameraFeed feed, Stopwatch clock, int fps, string path)
    {
        _feed = feed;
        _clock = clock;
        _fps = fps;
        Path = path;
    }

    public string Path { get; }

    /// <summary>At least one picture of the camera went into the video.</summary>
    public bool HasPictures { get; private set; }

    public bool Start()
    {
        var start = new ProcessStartInfo(ToolLocator.Ffmpeg!) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardError = true };
        foreach (var arg in new[]
        {
            "-hide_banner", "-loglevel", "error", "-f", "rawvideo", "-pix_fmt", "bgra", "-s", $"{CameraFeed.Side}x{CameraFeed.Side}", "-framerate", _fps.ToString(),
            "-i", "-", "-c:v", "libx264", "-preset", "ultrafast", "-crf", "18", "-pix_fmt", "yuv420p", "-y", Path,
        })
            start.ArgumentList.Add(arg);
        try
        {
            _ffmpeg = Process.Start(start);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
        if (_ffmpeg is null)
            return false;
        _ffmpeg.ErrorDataReceived += (_, _) => { };
        _ffmpeg.BeginErrorReadLine();
        _writer = new Thread(Write) { IsBackground = true, Name = "Petal camera recording" };
        _writer.Start();
        return true;
    }

    /// <summary>
    /// The latest picture at each frame's time, so frame n of the camera is frame n of the screen. Until the
    /// camera's first picture nothing is written; then that picture fills the time that went by.
    /// </summary>
    private void Write()
    {
        var frame = new byte[CameraFeed.Side * CameraFeed.Side * 4];
        var input = _ffmpeg!.StandardInput.BaseStream;
        double frameMs = 1000.0 / _fps;
        long sent = 0;
        try
        {
            while (!_stopping)
            {
                double now = _clock.Elapsed.TotalMilliseconds;
                if (now < sent * frameMs || !_feed.CopyLatest(frame))
                {
                    Thread.Sleep(Math.Max(1, (int)(sent * frameMs - now)));
                    continue;
                }
                do
                {
                    input.Write(frame, 0, frame.Length);
                    sent++;
                }
                while (!_stopping && sent * frameMs < _clock.Elapsed.TotalMilliseconds - frameMs);
                HasPictures = true;
            }
        }
        catch (IOException)
        {
            // FFmpeg went away; the recording goes on without the camera.
        }
    }

    public async Task StopAsync()
    {
        _stopping = true;
        await Task.Run(() => _writer?.Join(3000));
        try { _ffmpeg?.StandardInput.Close(); } catch (IOException) { }
        if (_ffmpeg is not null)
        {
            using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            try { await _ffmpeg.WaitForExitAsync(wait.Token); }
            catch (OperationCanceledException) { Kill(); }
        }
    }

    public void Kill()
    {
        _stopping = true;
        _writer?.Join(2000);
        try
        {
            _ffmpeg?.StandardInput.Close();
            if (_ffmpeg is { HasExited: false })
                _ffmpeg.Kill();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
    }

    public void Dispose() => _ffmpeg?.Dispose();
}
