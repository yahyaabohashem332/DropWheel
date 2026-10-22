using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Petal.App.Input;
using Petal.App.Localization;
using Petal.App.Visuals;
using Petal.Core.Util;
using static Petal.App.Interop.NativeMethods;

namespace Petal.App.Screen;

/// <summary>
/// Records an area of the screen (or a whole monitor) to an MP4 with the PC's sound and the microphone
/// if wanted, then opens the conversion wheel on it (to a GIF, smaller, trimmed...). The frame around the
/// area and the bar under it stay out of the video.
/// </summary>
internal static class ScreenRecorder
{
    private static RecordingSession? _session;

    public static bool IsBusy => _session is not null;

    public static bool IsRecording => _session?.Recording == true;

    public static string Folder => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Screen Recordings");

    public static void Toggle(Action<string, ScreenPoint>? openFiles)
    {
        if (_session is not null)
        {
            // While it saves, a second press must not throw the recording away.
            if (_session.Recording)
                _session.Stop();
            else if (!_session.Saving)
                _session.Cancel();
            return;
        }
        if (ToolLocator.Ffmpeg is null)
        {
            Hud.HudCard.ShowAt(Hud.HudCard.CornerPoint(), "", "").Fail(L.T("Can't record"), L.T("FFmpeg was not found. Install FFmpeg or place ffmpeg.exe in Petal's tools folder."));
            return;
        }
        Wheels.Screenshots.ChooseArea("Drag over the area to record · Enter records the whole screen · Esc cancels", true, (_, area) =>
        {
            // Video wants even sides.
            area.Right = area.Left + Math.Max(32, area.Width & ~1);
            area.Bottom = area.Top + Math.Max(32, area.Height & ~1);
            _session = new RecordingSession(area, openFiles);
            _session.Ended += () => _session = null;
        });
    }
}

/// <summary>One recording: the frame, the bar, the frames sent to FFmpeg and the sound written beside them.</summary>
internal sealed class RecordingSession
{
    private readonly RECT _area;
    private readonly Action<string, ScreenPoint>? _openFiles;
    private readonly MonitorWindow _frame;
    private readonly RecordBar _bar;
    private readonly string _work = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Petal recording " + Guid.NewGuid().ToString("N")[..8]);
    private Process? _ffmpeg;
    private Thread? _grabber;
    private volatile bool _stopping;
    private readonly List<AudioTrack> _tracks = [];
    private readonly Stopwatch _clock = new();
    private bool _showedClicks;
    private int _fps = 30;
    private string? _warning;

    public event Action? Ended;

    public bool Recording { get; private set; }

    public bool Saving => _stopping && !Recording;

    public TimeSpan Elapsed => _clock.Elapsed;

    public RecordingSession(RECT area, Action<string, ScreenPoint>? openFiles)
    {
        _area = area;
        _openFiles = openFiles;
        var monitor = ScreenNative.Describe(MonitorFromPoint(new POINT { X = area.Left + area.Width / 2, Y = area.Top + area.Height / 2 }, 2));
        _frame = new MonitorWindow(monitor.Bounds, clickThrough: true, noActivate: true, hideFromCapture: true) { Title = L.T("Screen Recording") };
        var canvas = new Canvas();
        _frame.Content = canvas;
        _frame.Loaded += (_, _) =>
        {
            var topLeft = _frame.ToLocal(new ScreenPoint(area.Left, area.Top));
            var bottomRight = _frame.ToLocal(new ScreenPoint(area.Right, area.Bottom));
            var rect = new Rect(topLeft, bottomRight);
            // Dim around the area, and a line just outside it.
            canvas.Children.Add(new System.Windows.Shapes.Path
            {
                Data = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(new Rect(0, 0, _frame.ActualWidth, _frame.ActualHeight)), new RectangleGeometry(rect)),
                Fill = new SolidColorBrush(Color.FromArgb(0x50, 0, 0, 0)),
            });
            var line = new Rectangle { Width = rect.Width + 4, Height = rect.Height + 4, Stroke = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44)), StrokeThickness = 2, StrokeDashArray = [5, 3] };
            Canvas.SetLeft(line, rect.X - 2);
            Canvas.SetTop(line, rect.Y - 2);
            canvas.Children.Add(line);
        };
        _frame.Show();
        _bar = new RecordBar(this);
        _bar.Show();
        _bar.PlaceNear(area, monitor);
    }

    // ---------- the steps ----------

    /// <summary>3, 2, 1 in the middle of the area, then recording.</summary>
    public async void Begin()
    {
        _bar.Counting();
        var canvas = (Canvas)_frame.Content;
        var middle = _frame.ToLocal(new ScreenPoint(_area.Left + _area.Width / 2, _area.Top + _area.Height / 2));
        foreach (var n in new[] { "3", "2", "1" })
        {
            var number = new TextBlock { Text = n, FontFamily = UiFonts.Display, FontWeight = FontWeights.Bold, FontSize = 96, Foreground = Brushes.White, Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 20, ShadowDepth = 0, Opacity = 0.8 } };
            number.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(number, middle.X - number.DesiredSize.Width / 2);
            Canvas.SetTop(number, middle.Y - number.DesiredSize.Height / 2);
            canvas.Children.Add(number);
            number.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(800)));
            await Task.Delay(850);
            canvas.Children.Remove(number);
            if (_stopping)
                return;
        }
        Start();
    }

    private void Start()
    {
        Directory.CreateDirectory(_work);
        var settings = Settings.SettingsStore.Current;
        _fps = (long)_area.Width * _area.Height > 1920L * 1080 ? 24 : 30;
        var start = new ProcessStartInfo(ToolLocator.Ffmpeg!)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in new[]
        {
            "-hide_banner", "-loglevel", "error", "-f", "rawvideo", "-pix_fmt", "bgra", "-s", $"{_area.Width}x{_area.Height}", "-framerate", _fps.ToString(),
            "-i", "-", "-c:v", "libx264", "-preset", "ultrafast", "-crf", "20", "-pix_fmt", "yuv420p", "-y", System.IO.Path.Combine(_work, "video.mkv"),
        })
            start.ArgumentList.Add(arg);
        try
        {
            _ffmpeg = Process.Start(start);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Fail(ex.Message);
            return;
        }
        if (_ffmpeg is null)
        {
            Fail(L.T("FFmpeg didn't start."));
            return;
        }
        _ffmpeg.ErrorDataReceived += (_, _) => { };
        _ffmpeg.BeginErrorReadLine();
        if (settings.RecordSystemSound)
            AddTrack(() => new WasapiLoopbackCapture(), "system.wav", L.T("The PC's sound couldn't be recorded."));
        if (settings.RecordMicrophone)
            AddTrack(() => new WasapiCapture(), "mic.wav", L.T("The microphone couldn't be recorded."));
        if (settings.RecordShowsClicks && !InputVisualizer.IsOn)
        {
            InputVisualizer.Toggle();
            _showedClicks = true;
        }
        Recording = true;
        _clock.Start();
        foreach (var track in _tracks)
            track.Start();
        _grabber = new Thread(Grab) { IsBackground = true, Name = "Petal screen recording", Priority = ThreadPriority.AboveNormal };
        _grabber.Start();
        _bar.Recording();
    }

    private void AddTrack(Func<IWaveIn> make, string name, string problem)
    {
        try
        {
            _tracks.Add(new AudioTrack(make(), System.IO.Path.Combine(_work, name)));
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or ArgumentException)
        {
            _warning = problem;
        }
    }

    /// <summary>Frames at a steady rate; when the PC falls behind, the last frame is sent again so the video keeps time with the sound.</summary>
    private void Grab()
    {
        using var grabber = new FrameGrabber(_area);
        var input = _ffmpeg!.StandardInput.BaseStream;
        double frameMs = 1000.0 / _fps;
        long sent = 0;
        try
        {
            while (!_stopping)
            {
                double due = sent * frameMs;
                double now = _clock.Elapsed.TotalMilliseconds;
                if (now < due)
                {
                    Thread.Sleep(Math.Max(1, (int)(due - now)));
                    continue;
                }
                var frame = grabber.Grab();
                input.Write(frame, 0, frame.Length);
                sent++;
                // Behind: repeat this frame for the time that went by.
                while (!_stopping && sent * frameMs < _clock.Elapsed.TotalMilliseconds - frameMs)
                {
                    input.Write(frame, 0, frame.Length);
                    sent++;
                }
            }
        }
        catch (IOException)
        {
            // FFmpeg went away; Stop reports it.
        }
    }

    public async void Stop()
    {
        if (!Recording || _stopping)
            return;
        _stopping = true;
        Recording = false;
        _bar.Saving();
        var elapsed = _clock.Elapsed;
        _clock.Stop();
        foreach (var track in _tracks)
            track.Stop(elapsed);
        await Task.Run(() => _grabber?.Join(3000));
        try { _ffmpeg?.StandardInput.Close(); } catch (IOException) { }
        if (_ffmpeg is not null)
            await _ffmpeg.WaitForExitAsync();
        if (_showedClicks && InputVisualizer.IsOn)
            InputVisualizer.Toggle();
        var video = System.IO.Path.Combine(_work, "video.mkv");
        if (!File.Exists(video) || new FileInfo(video).Length == 0)
        {
            Fail(L.T("The video couldn't be made."));
            return;
        }
        Directory.CreateDirectory(ScreenRecorder.Folder);
        var stamp = DateTime.Now.ToString("yyyy-MM-dd HH.mm.ss", System.Globalization.CultureInfo.InvariantCulture);
        var output = Petal.Core.Jobs.OutputNaming.Unique(ScreenRecorder.Folder, $"Recording {stamp}", "mp4");
        var sounds = _tracks.Where(t => t.HasSound).Select(t => t.Path).ToList();
        bool ok = await Mux(video, sounds, output);
        if (!ok && sounds.Count > 0)
            ok = await Mux(video, [], output);
        Close();
        Cleanup();
        if (!ok)
        {
            Hud.HudCard.ShowAt(Hud.HudCard.CornerPoint(), "", "").Fail(L.T("Recording not saved"), L.T("The video couldn't be made."));
            return;
        }
        var middle = new ScreenPoint(_area.Left + _area.Width / 2, _area.Top + _area.Height / 2);
        var card = Hud.HudCard.ShowAt(Hud.HudCard.CornerPoint(), L.T("Recording saved"), "");
        card.Succeed(L.T("Recording saved"), _warning ?? System.IO.Path.GetFileName(output), () => _openFiles?.Invoke(output, middle));
        _openFiles?.Invoke(output, middle);
    }

    /// <summary>The video with its sound (both mixed when there are two) into an MP4 that plays everywhere.</summary>
    private static async Task<bool> Mux(string video, List<string> sounds, string output)
    {
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-i", video };
        foreach (var s in sounds)
            args.AddRange(["-i", s]);
        if (sounds.Count == 2)
            args.AddRange(["-filter_complex", "[1:a][2:a]amix=inputs=2:duration=longest:dropout_transition=0[a]", "-map", "0:v", "-map", "[a]"]);
        else if (sounds.Count == 1)
            args.AddRange(["-map", "0:v", "-map", "1:a"]);
        args.AddRange(["-c:v", "copy"]);
        if (sounds.Count > 0)
            args.AddRange(["-c:a", "aac", "-b:a", "160k", "-shortest"]);
        args.AddRange(["-movflags", "+faststart", "-y", output]);
        var start = new ProcessStartInfo(ToolLocator.Ffmpeg!) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (var a in args)
            start.ArgumentList.Add(a);
        try
        {
            using var process = Process.Start(start);
            if (process is null)
                return false;
            var errors = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            await errors;
            if (process.ExitCode == 0 && File.Exists(output) && new FileInfo(output).Length > 0)
                return true;
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
        try { File.Delete(output); } catch (IOException) { }
        return false;
    }

    public void Cancel()
    {
        _stopping = true;
        Recording = false;
        foreach (var track in _tracks)
            track.Stop(_clock.Elapsed);
        _grabber?.Join(2000);
        try
        {
            _ffmpeg?.StandardInput.Close();
            if (_ffmpeg is { HasExited: false })
                _ffmpeg.Kill();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
        if (_showedClicks && InputVisualizer.IsOn)
            InputVisualizer.Toggle();
        Close();
        Cleanup();
    }

    private void Fail(string why)
    {
        Cancel();
        Hud.HudCard.ShowAt(Hud.HudCard.CornerPoint(), "", "").Fail(L.T("Can't record"), why);
    }

    private void Close()
    {
        _frame.Close();
        _bar.Close();
        Ended?.Invoke();
        Ended = null;
    }

    private void Cleanup()
    {
        foreach (var track in _tracks)
            track.Dispose();
        _ffmpeg?.Dispose();
        // FFmpeg may still hold the files for a moment.
        _ = Task.Run(async () =>
        {
            for (int i = 0; i < 10; i++)
            {
                try
                {
                    if (Directory.Exists(_work))
                        Directory.Delete(_work, true);
                    return;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    await Task.Delay(500);
                }
            }
        });
    }
}

/// <summary>Copies an area of the screen with the pointer drawn on it, reusing its buffers frame after frame.</summary>
internal sealed class FrameGrabber : IDisposable
{
    private readonly RECT _area;
    private readonly IntPtr _screen, _memory, _dib, _old, _bits;
    private readonly byte[] _buffer;

    public FrameGrabber(RECT area)
    {
        _area = area;
        _screen = GetDC(IntPtr.Zero);
        _memory = CreateCompatibleDC(_screen);
        var header = new BITMAPINFOHEADER
        {
            biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = area.Width,
            biHeight = -area.Height,
            biPlanes = 1,
            biBitCount = 32,
        };
        _dib = CreateDIBSection(_screen, ref header, 0, out _bits, IntPtr.Zero, 0);
        _old = SelectObject(_memory, _dib);
        _buffer = new byte[area.Width * area.Height * 4];
    }

    public byte[] Grab()
    {
        BitBlt(_memory, 0, 0, _area.Width, _area.Height, _screen, _area.Left, _area.Top, SRCCOPY | CAPTUREBLT);
        var info = new ScreenNative.CURSORINFO { cbSize = Marshal.SizeOf<ScreenNative.CURSORINFO>() };
        if (ScreenNative.GetCursorInfo(ref info) && (info.flags & ScreenNative.CURSOR_SHOWING) != 0 && ScreenNative.GetIconInfo(info.hCursor, out var icon))
        {
            ScreenNative.DrawIconEx(_memory, info.ptScreenPos.X - _area.Left - icon.xHotspot, info.ptScreenPos.Y - _area.Top - icon.yHotspot, info.hCursor, 0, 0, 0, IntPtr.Zero, ScreenNative.DI_NORMAL);
            if (icon.hbmMask != IntPtr.Zero) DeleteObject(icon.hbmMask);
            if (icon.hbmColor != IntPtr.Zero) DeleteObject(icon.hbmColor);
        }
        Marshal.Copy(_bits, _buffer, 0, _buffer.Length);
        return _buffer;
    }

    public void Dispose()
    {
        SelectObject(_memory, _old);
        if (_dib != IntPtr.Zero)
            DeleteObject(_dib);
        DeleteDC(_memory);
        ReleaseDC(IntPtr.Zero, _screen);
    }
}

/// <summary>
/// Sound written to a WAV as it comes. Windows sends nothing while the PC is silent, so the gaps are filled
/// with silence to keep the sound in time with the video.
/// </summary>
internal sealed class AudioTrack : IDisposable
{
    private readonly IWaveIn _input;
    private readonly object _lock = new();
    private readonly Stopwatch _clock = new();
    private WaveFileWriter? _writer;
    private long _bytes;
    private bool _heard;

    public AudioTrack(IWaveIn input, string path)
    {
        _input = input;
        Path = path;
        _writer = new WaveFileWriter(path, input.WaveFormat);
        input.DataAvailable += OnData;
    }

    public string Path { get; }

    /// <summary>Something was written (an empty file would break the mix).</summary>
    public bool HasSound => _heard && File.Exists(Path);

    public void Start()
    {
        _clock.Start();
        try
        {
            _input.StartRecording();
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            lock (_lock)
            {
                _writer?.Dispose();
                _writer = null;
            }
        }
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        lock (_lock)
        {
            if (_writer is null)
                return;
            var format = _writer.WaveFormat;
            PadTo(_clock.Elapsed - TimeSpan.FromSeconds((double)e.BytesRecorded / format.AverageBytesPerSecond));
            _writer.Write(e.Buffer, 0, e.BytesRecorded);
            _bytes += e.BytesRecorded;
            _heard = true;
        }
    }

    /// <summary>Silence up to a moment, when the sound so far falls short of it by more than a little.</summary>
    private void PadTo(TimeSpan moment)
    {
        var format = _writer!.WaveFormat;
        long want = (long)(moment.TotalSeconds * format.AverageBytesPerSecond);
        want -= want % format.BlockAlign;
        long missing = want - _bytes;
        if (missing < format.AverageBytesPerSecond / 20)
            return;
        var zeros = new byte[Math.Min(missing, format.AverageBytesPerSecond)];
        while (missing > 0)
        {
            int n = (int)Math.Min(missing, zeros.Length);
            _writer.Write(zeros, 0, n);
            _bytes += n;
            missing -= n;
        }
        _heard = true;
    }

    /// <summary>Stops listening and makes the sound as long as the video.</summary>
    public void Stop(TimeSpan length)
    {
        try { _input.StopRecording(); } catch (Exception ex) when (ex is COMException or InvalidOperationException) { }
        lock (_lock)
        {
            if (_writer is null)
                return;
            PadTo(length);
            _writer.Dispose();
            _writer = null;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _writer?.Dispose();
            _writer = null;
        }
        _input.Dispose();
    }
}

/// <summary>The bar under the area: before, the sound switches and Record; while recording, the time, Stop and Cancel.</summary>
internal sealed class RecordBar : Window
{
    private readonly RecordingSession _session;
    private readonly StackPanel _row = new() { Orientation = Orientation.Horizontal };
    private readonly System.Windows.Threading.DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private TextBlock? _time;

    public RecordBar(RecordingSession session)
    {
        _session = session;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        Title = L.T("Screen Recording");
        L.Apply(this, mirror: false);
        Content = ScreenUi.Strip(_row, 22);
        _row.MouseLeftButtonDown += (_, e) =>
        {
            if (e.OriginalSource is not StripButton)
                DragMove();
        };
        SourceInitialized += (_, _) => ScreenNative.SetWindowDisplayAffinity(new WindowInteropHelper(this).Handle, ScreenNative.WDA_EXCLUDEFROMCAPTURE);
        _tick.Tick += (_, _) =>
        {
            if (_time is not null)
                _time.Text = _session.Elapsed.ToString(_session.Elapsed.TotalHours >= 1 ? @"h\:mm\:ss" : @"mm\:ss");
        };
        Closed += (_, _) => _tick.Stop();
        Ready();
    }

    /// <summary>Under the area, or above it when there is no room, or inside it at the bottom.</summary>
    public void PlaceNear(RECT area, ScreenNative.Monitor monitor)
    {
        UpdateLayout();
        double s = monitor.Scale;
        double w = ActualWidth * s, h = ActualHeight * s;
        double x = Math.Clamp(area.Left + (area.Width - w) / 2, monitor.Work.Left + 8, monitor.Work.Right - w - 8);
        double y = area.Bottom + 12 + h < monitor.Work.Bottom ? area.Bottom + 12
            : area.Top - 12 - h > monitor.Work.Top ? area.Top - 12 - h
            : area.Bottom - h - 16;
        var handle = new WindowInteropHelper(this).Handle;
        SetWindowPos(handle, HWND_TOPMOST, (int)x, (int)y, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE);
    }

    private void Ready()
    {
        var settings = Settings.SettingsStore.Current;
        _row.Children.Clear();
        var record = new Border
        {
            CornerRadius = new CornerRadius(17),
            Height = 34,
            Padding = new Thickness(14, 0, 16, 0),
            Margin = new Thickness(1, 1, 6, 1),
            Background = new SolidColorBrush(Color.FromRgb(0xE5, 0x39, 0x35)),
            Cursor = Cursors.Hand,
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children =
                {
                    new Ellipse { Width = 11, Height = 11, Fill = Brushes.White, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center },
                    new TextBlock { Text = L.T("Record"), Foreground = Brushes.White, FontWeight = FontWeights.SemiBold, FontSize = 13, VerticalAlignment = VerticalAlignment.Center },
                },
            },
        };
        record.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            _session.Begin();
        };
        _row.Children.Add(record);
        StripButton? system = null, mic = null, clicks = null;
        system = new StripButton("volume", "Record the PC's sound", () =>
        {
            settings.RecordSystemSound = !settings.RecordSystemSound;
            system!.IsOn = settings.RecordSystemSound;
            Settings.SettingsStore.Save();
        }) { IsOn = settings.RecordSystemSound };
        mic = new StripButton("mic", "Record the microphone", () =>
        {
            settings.RecordMicrophone = !settings.RecordMicrophone;
            mic!.IsOn = settings.RecordMicrophone;
            Settings.SettingsStore.Save();
        }) { IsOn = settings.RecordMicrophone };
        clicks = new StripButton("clickShow", "Show clicks and shortcuts in the video", () =>
        {
            settings.RecordShowsClicks = !settings.RecordShowsClicks;
            clicks!.IsOn = settings.RecordShowsClicks;
            Settings.SettingsStore.Save();
        }) { IsOn = settings.RecordShowsClicks };
        _row.Children.Add(system);
        _row.Children.Add(mic);
        _row.Children.Add(clicks);
        _row.Children.Add(ScreenUi.Divider());
        _row.Children.Add(new StripButton("x", "Cancel", _session.Cancel));
    }

    public void Counting()
    {
        _row.Children.Clear();
        _row.Children.Add(new TextBlock { Text = L.T("Starting…"), Foreground = Brushes.White, FontSize = 13, Margin = new Thickness(12, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center });
        _row.Children.Add(new StripButton("x", "Cancel", _session.Cancel));
    }

    public void Recording()
    {
        _row.Children.Clear();
        var dot = new Ellipse { Width = 10, Height = 10, Fill = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44)), Margin = new Thickness(12, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        dot.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.25, TimeSpan.FromMilliseconds(700)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
        _time = new TextBlock { Text = "00:00", Foreground = Brushes.White, FontSize = 14, FontWeight = FontWeights.SemiBold, FontFamily = UiFonts.Display, MinWidth = 46, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        _row.Children.Add(dot);
        _row.Children.Add(_time);
        var stop = new StripButton("square", "Stop and save", _session.Stop) { Accent = Color.FromRgb(0xE5, 0x39, 0x35) };
        _row.Children.Add(stop);
        _row.Children.Add(new StripButton("x", "Cancel", _session.Cancel));
        _tick.Start();
    }

    public void Saving()
    {
        _tick.Stop();
        _row.Children.Clear();
        _row.Children.Add(new TextBlock { Text = L.T("Saving the recording…"), Foreground = Brushes.White, FontSize = 13, Margin = new Thickness(14, 8, 14, 8), VerticalAlignment = VerticalAlignment.Center });
    }
}
