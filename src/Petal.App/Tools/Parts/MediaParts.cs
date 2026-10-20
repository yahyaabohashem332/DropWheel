using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NAudio.Wave;
using Petal.Engines.Media;

namespace Petal.App.Tools.Parts;

/// <summary>Shows the video frame at a time. Scrubbing quickly only ever shows the latest request.</summary>
public sealed class FrameView : Grid
{
    private readonly Image _image = new() { Stretch = Stretch.Uniform };
    private readonly string _path;
    private readonly int _maxWidth;
    private int _generation;
    private bool _busy;
    private double? _pending;

    public FrameView(string path, int maxWidth = 960)
    {
        _path = path;
        _maxWidth = maxWidth;
        Children.Add(_image);
    }

    public Image Image => _image;

    public event Action<BitmapSource>? FrameShown;

    public void Show(double seconds)
    {
        _pending = seconds;
        if (!_busy)
            _ = PumpAsync();
    }

    private async Task PumpAsync()
    {
        _busy = true;
        while (_pending is { } t)
        {
            _pending = null;
            int generation = ++_generation;
            try
            {
                var bytes = await MediaPreview.ExtractFrameAsync(_path, TimeSpan.FromSeconds(t), _maxWidth);
                if (generation != _generation || bytes.Length == 0) continue;
                var bitmap = Decode(bytes);
                _image.Source = bitmap;
                FrameShown?.Invoke(bitmap);
            }
            catch (Exception ex)
            {
                Log.Info($"Frame at {t:0.00}s failed: {ex.Message}");
            }
        }
        _busy = false;
    }

    public static BitmapSource Decode(byte[] png)
    {
        using var stream = new MemoryStream(png);
        var frame = BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        frame.Freeze();
        return frame;
    }
}

/// <summary>
/// Plays decoded PCM through the default output. Supports playing a range, reporting position,
/// and an optional live transform (used to preview bleeps and channel mixes before saving).
/// </summary>
public sealed class PcmPlayer : IDisposable
{
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private PcmAudio? _audio;
    private WaveOutEvent? _output;
    private RangeProvider? _provider;

    public event Action<double>? PositionChanged;
    public event Action? Stopped;

    public PcmPlayer()
    {
        _tick.Tick += (_, _) =>
        {
            if (_provider is not null)
                PositionChanged?.Invoke(_provider.Seconds);
        };
    }

    public bool IsLoaded => _audio is not null;
    public bool IsPlaying => _output?.PlaybackState == PlaybackState.Playing;
    public double Duration => _audio is null ? 0 : _audio.Samples.Length / (double)_audio.Channels / _audio.SampleRate;
    public int Channels => _audio?.Channels ?? 0;
    public PcmAudio? Audio => _audio;

    /// <summary>Called for each block of interleaved samples with the block's start time in seconds.</summary>
    public Action<float[], int, int, double>? Transform { get; set; }

    /// <summary>Plays already-decoded samples, e.g. a processed preview.</summary>
    public void Use(PcmAudio audio) => _audio = audio;

    public async Task LoadAsync(string path, int channels = 2)
    {
        _audio = await MediaPreview.DecodePcmAsync(path, new PcmDecodeOptions(SampleRate: 44100, Channels: channels));
    }

    public void Play(double from, double to)
    {
        if (_audio is null) return;
        Stop();
        _provider = new RangeProvider(_audio, from, to, this);
        _output = new WaveOutEvent { BufferMilliseconds = 60, NumberOfBuffers = 2 };
        _output.Init(_provider);
        _output.PlaybackStopped += (_, _) => Dispatcher.CurrentDispatcher.BeginInvoke(() => { });
        _output.Play();
        _tick.Start();
    }

    public void Stop()
    {
        _tick.Stop();
        if (_output is not null)
        {
            _output.Stop();
            _output.Dispose();
            _output = null;
            Stopped?.Invoke();
        }
    }

    public void Dispose() => Stop();

    private sealed class RangeProvider : ISampleProvider
    {
        private readonly PcmAudio _audio;
        private readonly int _end;
        private readonly PcmPlayer _owner;
        private int _position;
        private bool _finished;

        public RangeProvider(PcmAudio audio, double from, double to, PcmPlayer owner)
        {
            _audio = audio;
            _owner = owner;
            int frame = audio.Channels;
            _position = Math.Clamp((int)(from * audio.SampleRate) * frame, 0, audio.Samples.Length);
            _end = Math.Clamp((int)(to * audio.SampleRate) * frame, _position, audio.Samples.Length);
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(audio.SampleRate, audio.Channels);
        }

        public WaveFormat WaveFormat { get; }

        public double Seconds => _position / (double)_audio.Channels / _audio.SampleRate;

        public int Read(Span<float> buffer)
        {
            int count = Math.Min(buffer.Length, _end - _position);
            if (count <= 0)
            {
                if (!_finished)
                {
                    _finished = true;
                    Application.Current?.Dispatcher.BeginInvoke(_owner.Stop);
                }
                return 0;
            }
            var block = new float[count];
            Array.Copy(_audio.Samples, _position, block, 0, count);
            _owner.Transform?.Invoke(block, 0, count, Seconds);
            block.CopyTo(buffer);
            _position += count;
            return count;
        }
    }
}

/// <summary>Step back · play · step forward, and "0:01.60 / 0:04.00" on the right.</summary>
public sealed class TransportBar : DockPanel
{
    private readonly TextBlock _time = new();
    private readonly Button _play;
    private double _duration;

    public event Action? StepBack;
    public event Action? StepForward;
    public event Action? PlayPause;

    public TransportBar(bool showPlay = true, bool showSteps = true)
    {
        Margin = new Thickness(0, 8, 0, 0);
        _time.Style = (Style)Application.Current.FindResource("Petal.Mono");
        _time.SetResourceReference(TextBlock.ForegroundProperty, "Petal.TextSecondary");
        _time.VerticalAlignment = VerticalAlignment.Center;
        SetDock(_time, Dock.Right);
        Children.Add(_time);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        _play = Button("play", "Play (Space)", () => PlayPause?.Invoke());
        if (showSteps) buttons.Children.Add(Button("stepBack", "Previous frame (←)", () => StepBack?.Invoke()));
        if (showPlay) buttons.Children.Add(_play);
        if (showSteps) buttons.Children.Add(Button("stepForward", "Next frame (→)", () => StepForward?.Invoke()));
        Children.Add(buttons);
    }

    private static Button Button(string icon, string tip, Action click)
    {
        var b = new Button
        {
            Style = (Style)Application.Current.FindResource("Petal.IconButton"),
            ToolTip = tip,
            Content = ToolWindow.ThemedIcon(icon, 16),
        };
        b.Click += (_, _) => click();
        return b;
    }

    public double Duration
    {
        set
        {
            _duration = value;
            Position = 0;
        }
    }

    public double Position
    {
        set => _time.Text = $"{TimeText.Format(value)} / {TimeText.Format(_duration)}";
    }

    public bool Playing
    {
        set => _play.Content = ToolWindow.ThemedIcon(value ? "pause" : "play", 16);
    }
}

/// <summary>Waveform peaks scaled 0–1 for drawing, from the media engine's waveform data.</summary>
public static class Peaks
{
    public static async Task<float[]> LoadAsync(string path, int buckets = 400, WaveformChannel channel = WaveformChannel.Mix)
    {
        var data = await MediaPreview.GetWaveformAsync(path, buckets, new WaveformOptions(Channel: channel));
        var peaks = new float[data.Max.Length];
        float max = 0.0001f;
        for (int i = 0; i < peaks.Length; i++)
        {
            peaks[i] = Math.Max(Math.Abs(data.Max[i]), Math.Abs(data.Min[i]));
            max = Math.Max(max, peaks[i]);
        }
        // Normalise so quiet recordings still draw a readable shape.
        for (int i = 0; i < peaks.Length; i++) peaks[i] /= max;
        return peaks;
    }
}
