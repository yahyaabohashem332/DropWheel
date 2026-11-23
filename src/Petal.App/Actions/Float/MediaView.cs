using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Petal.App.Localization;
using Petal.App.Settings;
using Petal.App.Shell;
using Petal.App.Visuals;
using Petal.Core.Formats;
using Petal.Core.Jobs;
using Petal.Engines.Files.Subtitles;

namespace Petal.App.Actions.Float;

/// <summary>
/// A video or a song playing in its own window above the others: play and pause, a bar to move through it, its time,
/// a speed from half to twice, playing round again, its volume. Subtitles beside it with the same name are shown. What
/// Windows can't play is made playable once (kept in the cache) through DropWheel's own converters.
/// </summary>
internal sealed class MediaView : FloatView
{
    private static readonly double[] Speeds = [1, 1.25, 1.5, 2, 0.5, 0.75];

    private readonly bool _video;
    private readonly MediaElement _media = new() { LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Close, Stretch = Stretch.Uniform, Volume = 0.8 };
    private readonly Grid _stage = new();
    private readonly Slider _bar = new() { Minimum = 0, Maximum = 1, VerticalAlignment = VerticalAlignment.Center, IsMoveToPointEnabled = true };
    private readonly Slider _volume = new() { Minimum = 0, Maximum = 1, Value = 0.8, Width = 80, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _time = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 8, 0), FontSize = 12, FlowDirection = FlowDirection.LeftToRight };
    private readonly Button _play;
    private readonly Button _speed = new() { MinWidth = 44, Padding = new Thickness(6, 2, 6, 3), FontSize = 12 };
    private readonly Button _loop = new() { Padding = new Thickness(6), Margin = new Thickness(0, 0, 4, 0), ToolTip = L.T("Play Round Again"), Style = (Style)Application.Current.FindResource("Petal.SubtleButton") };
    private bool _looping;
    private readonly TextBlock _line = new() { TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Foreground = Brushes.White, FontSize = 18, Padding = new Thickness(10, 4, 10, 5) };
    private readonly Border _caption;
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private readonly CancellationTokenSource _closing = new();
    private List<SubtitleCue> _cues = [];
    private TimeSpan _length;
    private bool _playing;
    private bool _moving;
    private bool _converted;
    private int _speedAt;

    public MediaView(string path, bool video) : base(path)
    {
        _video = video;
        RowDefinitions.Add(new RowDefinition());
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _stage.Background = video ? Brushes.Black : Brushes.Transparent;
        _stage.Children.Add(_media);
        _caption = new Border { Background = new SolidColorBrush(Color.FromArgb(0xB0, 0, 0, 0)), CornerRadius = new CornerRadius(6), Child = _line, VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(16, 0, 16, 14), Visibility = Visibility.Collapsed, IsHitTestVisible = false };
        _stage.Children.Add(_caption);
        _stage.MouseLeftButtonUp += (_, _) => Toggle();
        Children.Add(_stage);

        _play = Round("pause", "Pause (Space)");
        _play.Click += (_, _) => Toggle();
        _speed.Content = "1×";
        _speed.ToolTip = L.T("Speed");
        _speed.Click += (_, _) =>
        {
            _speedAt = (_speedAt + 1) % Speeds.Length;
            _media.SpeedRatio = Speeds[_speedAt];
            _speed.Content = Speeds[_speedAt].ToString(System.Globalization.CultureInfo.CurrentCulture) + "×";
        };
        Loop(!video);
        _loop.Click += (_, _) => Loop(!_looping);
        var mute = Round("volume", "Sound on or off");
        mute.Click += (_, _) => _media.IsMuted = !_media.IsMuted;
        _volume.ValueChanged += (_, e) => _media.Volume = e.NewValue;
        _bar.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler((_, _) => _moving = true));
        _bar.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, _) =>
        {
            _moving = false;
            _media.Position = TimeSpan.FromSeconds(_bar.Value * _length.TotalSeconds);
        }));
        _bar.PreviewMouseLeftButtonUp += (_, _) =>
        {
            if (!_moving)
                _media.Position = TimeSpan.FromSeconds(_bar.Value * _length.TotalSeconds);
        };
        var controls = new DockPanel { Margin = new Thickness(8, 6, 8, 6), LastChildFill = true };
        foreach (var (element, dock) in new (UIElement, Dock)[] { (_play, Dock.Left), (_volume, Dock.Right), (mute, Dock.Right), (_loop, Dock.Right), (_speed, Dock.Right), (_time, Dock.Right) })
        {
            DockPanel.SetDock(element, dock);
            controls.Children.Add(element);
        }
        controls.Children.Add(_bar);
        controls.FlowDirection = FlowDirection.LeftToRight;
        var bar = new Border { Child = controls };
        bar.SetResourceReference(Border.BackgroundProperty, "Petal.Control");
        Grid.SetRow(bar, 1);
        Children.Add(bar);

        _media.MediaOpened += (_, _) =>
        {
            _length = _media.NaturalDuration.HasTimeSpan ? _media.NaturalDuration.TimeSpan : _length;
            Tick();
        };
        _media.MediaEnded += (_, _) =>
        {
            _media.Position = TimeSpan.Zero;
            if (_looping)
                _media.Play();
            else
                Pause();
        };
        _media.MediaFailed += async (_, _) => await PlayableAsync();
        _clock.Tick += (_, _) => Tick();
        Focusable = true;
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Space)
            {
                e.Handled = true;
                Toggle();
            }
            else if (e.Key is Key.Left or Key.Right)
            {
                e.Handled = true;
                _media.Position += TimeSpan.FromSeconds(e.Key == Key.Right ? 5 : -5);
            }
        };
        _ = StartAsync();
    }

    public override FloatKind Kind => _video ? FloatKind.Video : FloatKind.Sound;

    private static Button Round(string icon, string tip) => new() { Content = ThemedIcon(icon, 14), Padding = new Thickness(6), Margin = new Thickness(0, 0, 4, 0), ToolTip = L.T(tip), Style = (Style)Application.Current.FindResource("Petal.SubtleButton") };

    private static FrameworkElement ThemedIcon(string name, double size)
    {
        var icon = Icons.Create(name, size, Brushes.Gray, 2);
        icon.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "Petal.Text");
        return icon;
    }

    private async Task StartAsync()
    {
        try
        {
            var info = await Petal.Engines.Media.MediaProbe.ProbeAsync(Path, _closing.Token);
            _length = info.Duration;
            var length = Petal.Core.Text.Durations.Format(info.Duration);
            Tell(QuickLookWindow.Facts(Path, _video && info.Width > 0 ? $"{info.Width} × {info.Height} · {length}" : length));
            if (!_video)
            {
                var peaks = await Tools.Parts.Peaks.LoadAsync(Path);
                _stage.Children.Insert(0, QuickLookWindow.WaveformPicture(peaks));
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or OperationCanceledException)
        {
            Tell(QuickLookWindow.Facts(Path, null));
        }
        _cues = Subtitles(Path);
        _media.Source = new Uri(Path);
        Play();
    }

    /// <summary>Subtitles beside a video with its name (.srt, .vtt, .ass, .ssa, .ttml, .smi), or none.</summary>
    internal static List<SubtitleCue> Subtitles(string video)
    {
        foreach (var format in new[] { Petal.Core.Formats.Formats.Srt, Petal.Core.Formats.Formats.Vtt, Petal.Core.Formats.Formats.Ass,
                     Petal.Core.Formats.Formats.Ssa, Petal.Core.Formats.Formats.Ttml, Petal.Core.Formats.Formats.Sami })
        {
            var beside = System.IO.Path.ChangeExtension(video, "." + format.WriteExtension);
            try
            {
                if (File.Exists(beside) && new FileInfo(beside).Length < 4 * 1024 * 1024)
                    return SubtitleFile.Read(SubtitleFile.Decode(File.ReadAllBytes(beside)), format);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or InvalidDataException)
            {
            }
        }
        return [];
    }

    /// <summary>The subtitle being said at a moment; null between them.</summary>
    internal static string? CueAt(IReadOnlyList<SubtitleCue> cues, TimeSpan at) => cues.FirstOrDefault(c => at >= c.Start && at < c.End)?.Text;

    /// <summary>
    /// Windows couldn't play it: a copy it can play (MP4 for a video, M4A for sound), made once with DropWheel's own
    /// converters and kept in the cache by the file's place, size and time.
    /// </summary>
    private async Task PlayableAsync()
    {
        if (_converted || _closing.IsCancellationRequested)
        {
            Children.Clear();
            Children.Add(QuickLookWindow.Fallback(Path));
            return;
        }
        _converted = true;
        var to = _video ? Formats.Mp4 : Formats.M4a;
        var copy = System.IO.Path.ChangeExtension(LayoutView.CachePath(Path), to.Extensions[0]);
        try
        {
            if (!File.Exists(copy))
            {
                if (Formats.FromPath(Path) is not { } from || App.Current.Controller.Jobs.Converters.Find(from, to) is not { } converter)
                    throw new NotSupportedException();
                _line.Text = L.T("Making it playable…");
                _caption.Visibility = Visibility.Visible;
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(copy)!);
                var temporary = copy + ".tmp" + System.IO.Path.GetExtension(copy);
                await converter.ConvertAsync(new ConversionJob(Path, from, to, temporary, SettingsStore.Current.Conversion), null, _closing.Token);
                File.Move(temporary, copy, true);
                _caption.Visibility = Visibility.Collapsed;
            }
            _media.Source = new Uri(copy);
            Play();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or NotSupportedException or UnauthorizedAccessException)
        {
            Log.Info($"float: {System.IO.Path.GetFileName(Path)} can't be played ({ex.Message})");
            Children.Clear();
            Children.Add(QuickLookWindow.Fallback(Path));
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Playing round again or not: the button lit in the accent while it is on.</summary>
    private void Loop(bool on)
    {
        _looping = on;
        var icon = Icons.Create("repeat", 14, Brushes.Gray, 2);
        icon.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, on ? "Petal.Accent" : "Petal.TextSecondary");
        _loop.Content = icon;
    }

    private void Toggle()
    {
        if (_playing)
            Pause();
        else
            Play();
    }

    private void Play()
    {
        _media.Play();
        _playing = true;
        _play.Content = ThemedIcon("pause", 14);
        _play.ToolTip = L.T("Pause (Space)");
        _clock.Start();
    }

    private void Pause()
    {
        _media.Pause();
        _playing = false;
        _play.Content = ThemedIcon("play", 14);
        _play.ToolTip = L.T("Play (Space)");
    }

    private void Tick()
    {
        var at = _media.Position;
        if (!_moving && _length > TimeSpan.Zero)
            _bar.Value = Math.Clamp(at.TotalSeconds / _length.TotalSeconds, 0, 1);
        _time.Text = Petal.Core.Text.Durations.Format(at) + " / " + Petal.Core.Text.Durations.Format(_length);
        var said = CueAt(_cues, at);
        if (!_converted || _caption.Visibility == Visibility.Collapsed || said is not null)
        {
            _line.Text = said ?? "";
            _caption.Visibility = said is null ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    public override IEnumerable<Control> MenuItems() =>
    [
        Item(_playing ? "Pause" : "Play", Toggle),
        Item("Play Round Again", () => Loop(!_looping), _looping),
    ];

    public override void Dispose()
    {
        _closing.Cancel();
        _clock.Stop();
        _media.Stop();
        _media.Close();
    }
}
