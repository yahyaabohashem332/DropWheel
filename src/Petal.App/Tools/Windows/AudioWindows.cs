using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using Petal.App.Input;
using Petal.App.Jobs;
using Petal.App.Tools.Parts;
using Petal.Core.Jobs;
using Petal.Core.Tools;
using Petal.Engines.Media;
using Petal.App.Localization;

namespace Petal.App.Tools.Windows;

/// <summary>An audio window with a decoded preview it can play.</summary>
public abstract class AudioToolWindow : MediaToolWindow
{
    protected readonly PcmPlayer Player = new();

    protected AudioToolWindow(ToolInfo tool, IReadOnlyList<string> files, JobRunner jobs, ScreenPoint at, double width = 400)
        : base(tool, files, jobs, at, width)
    {
        Closed += (_, _) => Player.Dispose();
    }

    protected Border Lane(UIElement content, double height = 60)
    {
        var border = new Border { CornerRadius = new CornerRadius(10), Height = height, Padding = new Thickness(8, 6, 8, 6), Child = content, ClipToBounds = true };
        border.SetResourceReference(Border.BackgroundProperty, "Petal.Control");
        return border;
    }

    protected Button PlayButton(string label, Action toggle)
    {
        var button = new Button { HorizontalAlignment = HorizontalAlignment.Left };
        void Paint(bool playing) => button.Content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children = { ThemedIcon(playing ? "pause" : "play", 13), new TextBlock { Text = "  " + L.T(playing ? "Stop" : label), VerticalAlignment = VerticalAlignment.Center } },
        };
        Paint(false);
        button.Click += (_, _) => toggle();
        Player.Stopped += () => Paint(false);
        button.Tag = (Action<bool>)Paint;
        return button;
    }

    protected static void MarkPlaying(Button button) => (button.Tag as Action<bool>)?.Invoke(true);
}

// ---------------------------------------------------------------------------------------------

public sealed class NormalizeWindow : AudioToolWindow
{
    private readonly WaveformView _wave = new();
    private readonly Border _playhead = new() { Width = 2, Background = Brushes.White, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly LabeledSlider _target = Slider(-24, -5, -16, v => $"{v:0} LUFS", 1, ticks: true);
    private readonly LabeledSlider _range = Slider(1, 20, 11, v => $"{v:0} LU", 1, ticks: true);
    private readonly LabeledSlider _peak = Slider(-9, 0, -1.5, v => $"{v:0.0} dBTP", 0.5, ticks: true);
    private readonly TextBlock _measured = new();
    private readonly Button _play;

    public NormalizeWindow(ToolInfo tool, IReadOnlyList<string> files, JobRunner jobs, ScreenPoint at) : base(tool, files, jobs, at)
    {
        if (files.Count == 1)
        {
            var grid = new Grid();
            grid.Children.Add(_wave);
            grid.Children.Add(_playhead);
            Body.Children.Add(Lane(grid, 70));
            _play = PlayButton("Play", TogglePlay);
            _play.Margin = new Thickness(0, 8, 0, 0);
            Body.Children.Add(_play);
            _measured.Style = (Style)FindResource("Petal.Caption");
            _measured.Margin = new Thickness(0, 8, 0, 0);
            Body.Children.Add(_measured);
        }
        else
        {
            _play = new Button();
            Body.Children.Add(Text(L.P(files.Count, "{0} recording", "{0} recordings"), "Petal.Section"));
        }
        Body.Children.Add(Row("Target loudness", _target, 120, 14));
        Body.Children.Add(Row("Loudness range", _range, 120, 10));
        Body.Children.Add(Row("True peak limit", _peak, 120, 10));
        Body.Children.Add(Text("−16 LUFS is a balanced default for spoken audio and music. The true peak limit helps prevent clipping after encoding.", "Petal.Caption").WithMarginT(12));
        PrimaryButton.Content = L.T("Normalize Audio");
        PrimaryButton.Click += (_, _) => Apply();
        ResetButton.Visibility = Visibility.Visible;
        ResetButton.Click += (_, _) =>
        {
            _target.Value = -16;
            _range.Value = 11;
            _peak.Value = -1.5;
        };
        Player.PositionChanged += t => _playhead.Margin = new Thickness(Math.Max(0, _wave.ActualWidth * t / Math.Max(0.01, Player.Duration)), 0, 0, 0);
    }

    private void TogglePlay()
    {
        if (Player.IsPlaying) { Player.Stop(); return; }
        Player.Play(0, Player.Duration);
        MarkPlaying(_play);
    }

    protected override async Task OnMediaReadyAsync(MediaInfo info)
    {
        if (Files.Count > 1) return;
        _wave.Peaks = await Peaks.LoadAsync(File, 300);
        _ = Player.LoadAsync(File);
        _measured.Text = L.T("Measuring loudness…");
        try
        {
            var stats = await AudioTools.MeasureLoudnessAsync(File);
            _measured.Text = L.F("Now: {0} LUFS · peak {1} dBTP · range {2} LU", stats.IntegratedLufs.ToString("0.0", CultureInfo.CurrentCulture), stats.TruePeakDbtp.ToString("0.0", CultureInfo.CurrentCulture), stats.LoudnessRangeLu.ToString("0.0", CultureInfo.CurrentCulture));
        }
        catch (Exception ex)
        {
            _measured.Text = "";
            Log.Info($"Loudness measurement failed: {ex.Message}");
        }
    }

    private void Apply()
    {
        var options = new NormalizeOptions(_target.Value, _range.Value, _peak.Value) { Settings = Quality };
        RunEach("Normalizing", "Volume normalized", async (file, p, ct) =>
        {
            var output = OutputNaming.ForTool(file, Tool);
            var result = await AudioTools.NormalizeAsync(file, output, options, p, ct);
            return [result.OutputPath];
        });
    }
}

// ---------------------------------------------------------------------------------------------

public sealed class TrimAudioWindow : AudioToolWindow
{
    private readonly WaveformView _wave = new();
    private readonly ContentControl _host = new();
    private readonly TextBlock _selected = new();
    private readonly CheckBox _silence = new() { Content = L.T("Trim silent ends automatically"), Margin = new Thickness(0, 12, 0, 0) };
    private TrimTimeline? _timeline;
    private TextBox? _startBox, _endBox;
    private Button? _play;

    public TrimAudioWindow(ToolInfo tool, IReadOnlyList<string> files, JobRunner jobs, ScreenPoint at) : base(tool, files, jobs, at)
    {
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        _selected.Style = (Style)FindResource("Petal.Caption");
        DockPanel.SetDock(_selected, Dock.Right);
        header.Children.Add(_selected);
        header.Children.Add(Text("Drag the ends to trim", "Petal.Caption"));
        Body.Children.Add(header);
        Body.Children.Add(_host);
        Body.Children.Add(_silence);
        _silence.Checked += async (_, _) => await DetectSilenceAsync();
        PrimaryButton.Content = L.T("Apply");
        PrimaryButton.Click += (_, _) => Apply();
    }

    protected override async Task OnMediaReadyAsync(MediaInfo info)
    {
        _wave.Peaks = await Peaks.LoadAsync(File, 400);
        var lane = Lane(_wave, 64);
        lane.Padding = new Thickness(16, 6, 16, 6);
        _timeline = new TrimTimeline(lane, 64) { Duration = info.DurationSeconds };
        _timeline.RangeChanged += (_, _) => Sync();
        _host.Content = _timeline;

        var times = new Grid { Margin = new Thickness(0, 8, 0, 0), FlowDirection = FlowDirection.LeftToRight };
        _startBox = TimeBox(0, t => _timeline.SetRange(t, _timeline.End));
        _endBox = TimeBox(info.DurationSeconds, t => _timeline.SetRange(_timeline.Start, t));
        var start = new StackPanel { Orientation = Orientation.Horizontal, Children = { Text("Start ", "Petal.Label"), _startBox } };
        var end = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { Text("End ", "Petal.Label"), _endBox } };
        times.Children.Add(start);
        times.Children.Add(end);
        Body.Children.Insert(Body.Children.IndexOf(_silence), times);

        _play = PlayButton("Play Selected Range", () =>
        {
            if (Player.IsPlaying) { Player.Stop(); return; }
            Player.Play(_timeline.Start, _timeline.End);
            MarkPlaying(_play!);
        });
        _play.Margin = new Thickness(0, 12, 0, 0);
        _play.HorizontalAlignment = HorizontalAlignment.Right;
        Body.Children.Add(_play);
        Player.PositionChanged += t => _timeline.Position = t;
        _ = Player.LoadAsync(File);
        Sync();
    }

    private async Task DetectSilenceAsync()
    {
        if (_timeline is null) return;
        try
        {
            var range = await AudioTools.DetectSilentEndsAsync(File);
            _timeline.SetRange(range.Start, range.End);
        }
        catch (Exception ex)
        {
            Log.Info($"Silence detection failed: {ex.Message}");
        }
    }

    private void Sync()
    {
        if (_timeline is null) return;
        _selected.Text = L.F("{0} selected", TimeText.Format(_timeline.End - _timeline.Start));
        if (_startBox is not null && !_startBox.IsKeyboardFocused) _startBox.Text = TimeText.Format(_timeline.Start);
        if (_endBox is not null && !_endBox.IsKeyboardFocused) _endBox.Text = TimeText.Format(_timeline.End);
    }

    private void Apply()
    {
        if (_timeline is null) return;
        var options = new TrimAudioOptions(_timeline.Start, _timeline.End, _silence.IsChecked == true) { Settings = Quality };
        var output = OutputNaming.ForTool(File, Tool);
        Run("Trimming audio", "Audio trimmed", async (p, ct) => [await AudioTools.TrimAsync(File, output, options, p, ct)]);
    }
}

// ---------------------------------------------------------------------------------------------

public sealed class AudioChannelsWindow : AudioToolWindow
{
    private readonly WaveformView _left = new();
    private readonly WaveformView _right = new();
    private readonly Segmented<AudioChannelMode> _mode = Segments([(AudioChannelMode.Mono, L.T("Mono", "audio")), (AudioChannelMode.Stereo, "Stereo")], AudioChannelMode.Stereo);
    private readonly LabeledSlider _leftVolume = Slider(0, 200, 100, v => $"{v:0}%", 1);
    private readonly LabeledSlider _rightVolume = Slider(0, 200, 100, v => $"{v:0}%", 1);
    private readonly PcmPlayer _output = new();
    private readonly Button _playOutput;

    public AudioChannelsWindow(ToolInfo tool, IReadOnlyList<string> files, JobRunner jobs, ScreenPoint at) : base(tool, files, jobs, at)
    {
        Body.Children.Add(SectionTitle("Output channel preview"));
        Body.Children.Add(ChannelRow("L", _left, WaveformChannel.Left));
        Body.Children.Add(ChannelRow("R", _right, WaveformChannel.Right));
        Body.Children.Add(Divider().WithMarginT(10));
        Body.Children.Add(Row("Output", _mode, 110, 12));
        Body.Children.Add(Row("Left volume", _leftVolume, 110, 10));
        Body.Children.Add(Row("Right volume", _rightVolume, 110, 8));
        _playOutput = new Button { HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(110, 12, 0, 0), Content = new StackPanel { Orientation = Orientation.Horizontal, Children = { ThemedIcon("play", 13), new TextBlock { Text = L.T("  Play Output") } } } };
        _playOutput.Click += async (_, _) => await PlayOutputAsync();
        Body.Children.Add(_playOutput);
        PrimaryButton.Content = L.T("Apply");
        PrimaryButton.Click += (_, _) => Apply();
        _mode.Changed += _ => Sync();
        _leftVolume.Changed += _ => Sync();
        _rightVolume.Changed += _ => Sync();
        Closed += (_, _) => _output.Dispose();
    }

    private UIElement ChannelRow(string label, WaveformView view, WaveformChannel channel)
    {
        var dock = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var badge = new Border { Width = 26, Height = 26, CornerRadius = new CornerRadius(13), Margin = new Thickness(0, 0, 10, 0), Child = new TextBlock { Text = label, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
        badge.SetResourceReference(Border.BackgroundProperty, "Petal.AccentSoft");
        ((TextBlock)badge.Child).SetResourceReference(TextBlock.ForegroundProperty, "Petal.Accent");
        DockPanel.SetDock(badge, Dock.Left);
        dock.Children.Add(badge);
        var play = IconButton("play", label == "L" ? "Play the left channel" : "Play the right channel", 14);
        DockPanel.SetDock(play, Dock.Right);
        play.Click += (_, _) =>
        {
            if (Player.IsPlaying) { Player.Stop(); return; }
            // Silence the other side for a solo preview.
            Player.Transform = (buffer, offset, count, _) =>
            {
                for (int i = offset + (label == "L" ? 1 : 0); i < offset + count; i += 2) buffer[i] = 0;
            };
            Player.Play(0, Player.Duration);
        };
        dock.Children.Add(play);
        view.Tag = channel;
        dock.Children.Add(Lane(view, 44));
        return dock;
    }

    protected override async Task OnMediaReadyAsync(MediaInfo info)
    {
        _left.Peaks = await Peaks.LoadAsync(File, 260, WaveformChannel.Left);
        _right.Peaks = await Peaks.LoadAsync(File, 260, WaveformChannel.Right);
        _ = Player.LoadAsync(File);
        if (info.AudioChannels == 1) _mode.Value = AudioChannelMode.Stereo;
        Sync();
    }

    private AudioChannelsOptions Options => new(_mode.Value, _leftVolume.Value / 100, _rightVolume.Value / 100) { Settings = Quality };

    private void Sync()
    {
        bool stereo = _mode.Value == AudioChannelMode.Stereo;
        _right.Opacity = stereo ? 1 : 0.35;
        FooterNote = Path.GetFileName(OutputNaming.ForTool(File, Tool, stereo ? "Stereo" : "Mono"));
    }

    private async Task PlayOutputAsync()
    {
        if (_output.IsPlaying) { _output.Stop(); return; }
        try
        {
            var filter = AudioTools.ChannelsPreviewFilter(Options, Info?.AudioChannels ?? 2);
            var pcm = await MediaPreview.DecodePcmAsync(File, new PcmDecodeOptions(AudioFilter: filter));
            _output.Use(pcm);
            _output.Play(0, _output.Duration);
        }
        catch (Exception ex)
        {
            Log.Info($"Channel preview failed: {ex.Message}");
        }
    }

    private void Apply()
    {
        var options = Options;
        var output = OutputNaming.ForTool(File, Tool, options.Output == AudioChannelMode.Stereo ? "Stereo" : "Mono");
        Run("Converting channels", "Channels converted", async (p, ct) => [await AudioTools.ConvertChannelsAsync(File, output, options, p, ct)]);
    }
}

// ---------------------------------------------------------------------------------------------

public sealed class VisualizerWindow : AudioToolWindow
{
    private readonly Segmented<VisualizerStyle> _style = Segments([(VisualizerStyle.Waveform, "Waveform"), (VisualizerStyle.StillImage, "Still image")], VisualizerStyle.Waveform);
    private readonly Segmented<VisualizerShape> _shape = Segments([(VisualizerShape.Landscape, "16:9"), (VisualizerShape.Portrait, "9:16"), (VisualizerShape.Square, "1:1")], VisualizerShape.Landscape);
    private readonly ColorWell _accent = new(Petal.App.Visuals.Theme.BrandAccent);
    private readonly Border _frame = new() { Background = Brushes.Black, CornerRadius = new CornerRadius(10), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, ClipToBounds = true };
    private readonly WaveformView _wave = new() { BarWidth = 4, Gap = 3, Margin = new Thickness(18, 30, 18, 30) };
    private readonly Image _still = new() { FlowDirection = FlowDirection.LeftToRight, Stretch = Stretch.UniformToFill };
    private readonly Button _choose = new() { Content = L.T("Choose image…"), HorizontalAlignment = HorizontalAlignment.Left };
    private readonly Grid _imageRow;
    private string? _imagePath;

    public VisualizerWindow(ToolInfo tool, IReadOnlyList<string> files, JobRunner jobs, ScreenPoint at) : base(tool, files, jobs, at)
    {
        var stage = new Grid { Height = 220 };
        var content = new Grid();
        content.Children.Add(_still);
        content.Children.Add(_wave);
        _frame.Child = content;
        stage.Children.Add(_frame);
        Body.Children.Add(stage);
        Body.Children.Add(Row("Style", _style, 90, 14));
        Body.Children.Add(Row("Shape", _shape, 90, 10));
        Body.Children.Add(Row("Colour", _accent, 90, 10));
        _imageRow = Row("Image", _choose, 90, 10);
        Body.Children.Add(_imageRow);
        _choose.Click += (_, _) =>
        {
            var dialog = new OpenFileDialog { Filter = L.T("Images") + "|*.png;*.jpg;*.jpeg;*.webp;*.bmp" };
            if (dialog.ShowDialog(this) == true)
            {
                _imagePath = dialog.FileName;
                _still.Source = ImageLoader.Load(dialog.FileName, 1200);
                Sync();
            }
        };
        _style.Changed += _ => Sync();
        _shape.Changed += _ => Sync();
        _accent.Changed += _ => Sync();
        PrimaryButton.Content = L.T("Create Visualization");
        PrimaryButton.Click += (_, _) => Apply();
        Sync();
    }

    protected override async Task OnMediaReadyAsync(MediaInfo info) => _wave.Peaks = await Peaks.LoadAsync(File, 120);

    private void Sync()
    {
        var (w, h) = AudioTools.VisualizerSize(_shape.Value);
        double scale = Math.Min(360.0 / w, 210.0 / h);
        _frame.Width = w * scale;
        _frame.Height = h * scale;
        bool still = _style.Value == VisualizerStyle.StillImage;
        _wave.Visibility = still ? Visibility.Collapsed : Visibility.Visible;
        _still.Visibility = still ? Visibility.Visible : Visibility.Collapsed;
        _wave.Fill = Petal.App.Visuals.Palette.Brush(_accent.Color);
        _wave.InvalidateVisual();
        _imageRow.Visibility = still ? Visibility.Visible : Visibility.Collapsed;
        _accent.IsEnabled = !still;
        PrimaryButton.IsEnabled = !still || _imagePath is not null;
    }

    private void Apply()
    {
        var options = new AudioVisualizerOptions(_shape.Value, _style.Value, ColorWell.Hex(_accent.Color), _imagePath) { Settings = Quality };
        var output = OutputNaming.ForTool(File, Tool);
        Run("Creating visualization", "Visualization created", async (p, ct) => [await AudioTools.CreateVisualizerAsync(File, output, options, p, ct)]);
    }
}

// ---------------------------------------------------------------------------------------------

public sealed class BleepWindow : AudioToolWindow
{
    private readonly RangeLane _bleeps = new("Bleep", 34) { ClipText = L.T("Bleep") };
    private readonly WaveformView _wave = new();
    private readonly TextBlock _count = new();
    private readonly TextBlock _start = new();
    private readonly TextBlock _end = new();
    private readonly Border _playhead = new() { Width = 2, HorizontalAlignment = HorizontalAlignment.Left, IsHitTestVisible = false };

    public BleepWindow(ToolInfo tool, IReadOnlyList<string> files, JobRunner jobs, ScreenPoint at) : base(tool, files, jobs, at)
    {
        _count.Style = (Style)FindResource("Petal.Section");
        Body.Children.Add(_count);
        var lanes = new Grid();
        lanes.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        lanes.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        lanes.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(54) });
        lanes.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        lanes.Children.Add(Text("Bleep", "Petal.Label"));
        var bleepHost = new Border { CornerRadius = new CornerRadius(8), ClipToBounds = true, Child = _bleeps };
        Grid.SetColumn(bleepHost, 1);
        lanes.Children.Add(bleepHost);
        var audioLabel = Text("Audio", "Petal.Label");
        Grid.SetRow(audioLabel, 1);
        lanes.Children.Add(audioLabel);
        var waveGrid = new Grid();
        waveGrid.Children.Add(_wave);
        _playhead.SetResourceReference(Border.BackgroundProperty, "Petal.Text");
        waveGrid.Children.Add(_playhead);
        var audioLane = Lane(waveGrid, 54);
        audioLane.Margin = new Thickness(0, 6, 0, 0);
        audioLane.Padding = new Thickness(0, 6, 0, 6);
        Grid.SetRow(audioLane, 1);
        Grid.SetColumn(audioLane, 1);
        lanes.Children.Add(audioLane);
        Body.Children.Add(lanes);

        // Under the lanes, which sit beside their labels (on the right in Arabic).
        var times = new Grid { Margin = L.IsRightToLeft ? new Thickness(0, 4, 54, 0) : new Thickness(54, 4, 0, 0), FlowDirection = FlowDirection.LeftToRight };
        _start.Style = _end.Style = (Style)FindResource("Petal.Mono");
        _end.HorizontalAlignment = HorizontalAlignment.Right;
        times.Children.Add(_start);
        times.Children.Add(_end);
        Body.Children.Add(times);
        Body.Children.Add(Text("Drag across the Bleep layer to add a section, then drag a clip or either edge to adjust it. Delete removes the selected one.", "Petal.Caption").WithMarginT(10));
        Body.Children.Add(Divider().WithMarginT(12));

        var preview = new StackPanel { Orientation = Orientation.Horizontal };
        var original = PlayButton("Original", () => Toggle(withBleeps: false));
        var withBleeps = PlayButton("With Bleeps", () => Toggle(withBleeps: true));
        withBleeps.Margin = new Thickness(8, 0, 0, 0);
        preview.Children.Add(original);
        preview.Children.Add(withBleeps);
        Body.Children.Add(Row("Preview", preview, 70, 12));

        PrimaryButton.Content = L.T("Save Bleeped Copy");
        PrimaryButton.Click += (_, _) => Apply();
        _bleeps.Changed += Sync;
        Player.PositionChanged += t => _playhead.Margin = new Thickness(_wave.ActualWidth * t / Math.Max(0.01, Player.Duration), 0, 0, 0);
        Sync();
    }

    protected override async Task OnMediaReadyAsync(MediaInfo info)
    {
        _bleeps.Duration = info.DurationSeconds;
        _end.Text = TimeText.Format(info.DurationSeconds);
        _start.Text = TimeText.Format(0);
        _wave.Peaks = await Peaks.LoadAsync(File, 360);
        _ = Player.LoadAsync(File);
    }

    private void Toggle(bool withBleeps)
    {
        if (Player.IsPlaying) { Player.Stop(); return; }
        var ranges = _bleeps.Ranges.Select(r => (r.Start, r.End)).ToList();
        int rate = Player.Audio?.SampleRate ?? 44100, channels = Math.Max(1, Player.Channels);
        // As loud as the saved file's tone.
        double volume = new Petal.Engines.Media.BleepOptions([]).ToneVolume;
        Player.Transform = withBleeps ? (buffer, offset, count, startSeconds) =>
        {
            // Replace marked ranges with a 1 kHz tone, as the saved file will have.
            for (int i = 0; i < count; i += channels)
            {
                double t = startSeconds + (double)i / channels / rate;
                if (!ranges.Any(r => t >= r.Start && t <= r.End)) continue;
                float tone = (float)(Math.Sin(2 * Math.PI * 1000 * t) * volume);
                for (int c = 0; c < channels && offset + i + c < buffer.Length; c++)
                    buffer[offset + i + c] = tone;
            }
        } : null;
        Player.Play(0, Player.Duration);
    }

    private void Sync()
    {
        int n = _bleeps.Ranges.Count;
        _count.Text = L.F("Bleeps  {0}", n);
        PrimaryButton.IsEnabled = n > 0;
        FooterNote = n == 0 ? L.T("Add at least one bleep range.") : Path.GetFileName(OutputNaming.ForTool(File, Tool));
    }

    private void Apply()
    {
        var options = new BleepOptions(_bleeps.Ranges.Select(r => new MediaTimeRange(r.Start, r.End)).ToList()) { Settings = Quality };
        var output = OutputNaming.ForTool(File, Tool);
        Run("Adding bleeps", "Bleeped", async (p, ct) => [await AudioTools.BleepAsync(File, output, options, p, ct)]);
    }
}
