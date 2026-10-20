using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Petal.App.Input;
using Petal.App.Jobs;
using Petal.App.Tools.Parts;
using Petal.Core.Jobs;
using Petal.Core.Tools;
using Petal.Engines.Media;
using Petal.App.Localization;

namespace Petal.App.Tools.Windows;

/// <summary>A video window with a frame preview, a timeline and a transport bar.</summary>
public abstract class VideoToolWindow : MediaToolWindow
{
    protected readonly Grid PreviewHost = new();
    protected readonly FrameView Frame;
    protected readonly TransportBar Transport;
    protected double Position;

    protected VideoToolWindow(ToolInfo tool, IReadOnlyList<string> files, JobRunner jobs, ScreenPoint at, double previewHeight = 230, bool showPlay = true)
        : base(tool, files, jobs, at, 420)
    {
        Frame = new FrameView(files[0]);
        PreviewHost.Children.Add(Frame);
        Body.Children.Add(PreviewPanel(PreviewHost, previewHeight));
        Transport = new TransportBar(showPlay);
        Transport.StepBack += () => Seek(Position - FrameStep);
        Transport.StepForward += () => Seek(Position + FrameStep);
        Transport.PlayPause += TogglePlay;
        if (showPlay)
            AttachPlayer(PreviewHost);
    }

    protected virtual (double From, double To) PlayRange => (Position, Duration);

    protected void TogglePlay()
    {
        if (IsPlaying)
        {
            StopPlayback();
            return;
        }
        var (from, to) = PlayRange;
        if (to - Position < 0.05) Seek(from);
        StartPlayback(Position, to);
    }

    protected override void PlaybackPosition(double seconds)
    {
        Position = seconds;
        Transport.Position = seconds;
        OnPositionChanged(seconds);
    }

    protected override void PlaybackStateChanged(bool playing)
    {
        Transport.Playing = playing;
        if (!playing) Frame.Show(Position);
    }

    protected virtual void OnPositionChanged(double seconds) { }

    protected void Seek(double seconds)
    {
        if (IsPlaying) StopPlayback();
        Position = Math.Clamp(seconds, 0, Duration);
        Transport.Position = Position;
        Frame.Show(Position);
        OnPositionChanged(Position);
    }

    protected override void OnKeys(object sender, KeyEventArgs e)
    {
        if (e.OriginalSource is TextBox) return;
        switch (e.Key)
        {
            case Key.Space: TogglePlay(); e.Handled = true; break;
            case Key.Left: Seek(Position - FrameStep); e.Handled = true; break;
            case Key.Right: Seek(Position + FrameStep); e.Handled = true; break;
        }
    }

    protected override async Task OnMediaReadyAsync(MediaInfo info)
    {
        Transport.Duration = info.DurationSeconds;
        Frame.Show(0);
        await OnVideoReadyAsync(info);
    }

    protected abstract Task OnVideoReadyAsync(MediaInfo info);

    protected async Task<Filmstrip> LoadFilmstripAsync(int count = 10)
    {
        var strip = new Filmstrip();
        try
        {
            var frames = await MediaPreview.ExtractFilmstripAsync(File, count, 160);
            strip.Frames = frames.Select(FrameView.Decode).ToList();
        }
        catch (Exception ex)
        {
            Log.Info($"Filmstrip failed: {ex.Message}");
        }
        return strip;
    }
}

// ---------------------------------------------------------------------------------------------

public sealed class TrimVideoWindow : VideoToolWindow
{
    private readonly ContentControl _timelineHost = new();
    private readonly TextBlock _selected = new();
    private readonly CheckBox _fast = new() { Content = L.T("Fast trim (no re-encoding, cuts at keyframes)"), Margin = new Thickness(0, 10, 0, 0) };
    private TrimTimeline? _timeline;
    private TextBox? _startBox, _endBox;

    public TrimVideoWindow(ToolInfo tool, IReadOnlyList<string> files, JobRunner jobs, ScreenPoint at) : base(tool, files, jobs, at)
    {
        var header = new DockPanel { Margin = new Thickness(0, 12, 0, 6) };
        _selected.Style = (Style)FindResource("Petal.Caption");
        DockPanel.SetDock(_selected, Dock.Right);
        header.Children.Add(_selected);
        header.Children.Add(Text("Drag the ends to trim", "Petal.Caption"));
        Body.Children.Add(header);
        Body.Children.Add(_timelineHost);
        Body.Children.Add(Transport);
        Body.Children.Add(_fast);
        PrimaryButton.Content = L.T("Apply");
        PrimaryButton.Click += (_, _) => Apply();
    }

    protected override (double From, double To) PlayRange => (_timeline?.Start ?? 0, _timeline?.End ?? Duration);

    protected override async Task OnVideoReadyAsync(MediaInfo info)
    {
        var strip = await LoadFilmstripAsync();
        _timeline = new TrimTimeline(strip, 56) { Duration = info.DurationSeconds };
        _timeline.RangeChanged += (s, e) => Sync();
        _timeline.Scrubbed += t => Seek(t);
        _timelineHost.Content = _timeline;

        var times = new Grid { Margin = new Thickness(0, 8, 0, 0), FlowDirection = FlowDirection.LeftToRight };
        _startBox = TimeBox(0, t => _timeline.SetRange(t, _timeline.End));
        _endBox = TimeBox(info.DurationSeconds, t => _timeline.SetRange(_timeline.Start, t));
        var start = new StackPanel { Orientation = Orientation.Horizontal };
        start.Children.Add(Text("Start ", "Petal.Label"));
        start.Children.Add(_startBox);
        var end = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        end.Children.Add(Text("End ", "Petal.Label"));
        end.Children.Add(_endBox);
        times.Children.Add(start);
        times.Children.Add(end);
        Body.Children.Insert(Body.Children.IndexOf(Transport), times);
        Sync();
    }

    protected override void OnPositionChanged(double seconds)
    {
        if (_timeline is not null) _timeline.Position = seconds;
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
        var options = new TrimVideoOptions(_timeline.Start, _timeline.End, _fast.IsChecked == true) { Settings = Quality };
        var output = OutputNaming.ForTool(File, Tool);
        Run("Trimming video", "Video trimmed", async (p, ct) => [await VideoTools.TrimAsync(File, output, options, p, ct)]);
    }
}

// ---------------------------------------------------------------------------------------------

public sealed class CropVideoWindow : VideoToolWindow
{
    private readonly CropOverlay _crop = new();
    private readonly TextBox _w = new() { Width = 72 };
    private readonly TextBox _h = new() { Width = 72 };
    private readonly Slider _scrub = new() { Minimum = 0, Maximum = 1, Margin = new Thickness(0, 10, 0, 0) };
    private bool _syncing;

    public CropVideoWindow(ToolInfo tool, IReadOnlyList<string> files, JobRunner jobs, ScreenPoint at) : base(tool, files, jobs, at, 260, showPlay: false)
    {
        // The crop overlay replaces the plain frame view.
        PreviewHost.Children.Clear();
        PreviewHost.Children.Add(_crop);
        Frame.FrameShown += bitmap => _crop.Source = bitmap;

        var ratio = Segments([(0.0, "Free"), (1.0, "1:1"), (16.0 / 9, "16:9"), (9.0 / 16, "9:16"), (4.0 / 3, "4:3"), (3.0 / 4, "3:4")], 0.0);
        ratio.Changed += r => _crop.AspectRatio = r > 0 ? r : null;
        Body.Children.Add(Row("Aspect ratio", ratio, 100, 14));

        var size = new StackPanel { Orientation = Orientation.Horizontal };
        size.Children.Add(Text("W ", "Petal.Label"));
        size.Children.Add(_w);
        size.Children.Add(Text("   H ", "Petal.Label"));
        size.Children.Add(_h);
        size.Children.Add(Text("  px", "Petal.Label"));
        Body.Children.Add(Row("Size", size, 100, 8));
        foreach (var box in new[] { _w, _h })
        {
            box.KeyDown += (_, e) => { if (e.Key == Key.Enter) ApplySize(); };
            box.LostKeyboardFocus += (_, _) => ApplySize();
        }
        _crop.Changed += r =>
        {
            _syncing = true;
            _w.Text = r.Width.ToString(CultureInfo.CurrentCulture);
            _h.Text = r.Height.ToString(CultureInfo.CurrentCulture);
            _syncing = false;
        };

        Body.Children.Add(_scrub);
        _scrub.ValueChanged += (_, e) => Seek(e.NewValue * Duration);
        Body.Children.Add(Transport);

        ResetButton.Visibility = Visibility.Visible;
        ResetButton.Click += (_, _) =>
        {
            ratio.Value = 0;
            _crop.Reset();
        };
        PrimaryButton.Content = L.T("Apply");
        PrimaryButton.Click += (_, _) => Apply();
    }

    protected override Task OnVideoReadyAsync(MediaInfo info)
    {
        _crop.SourceSize = new Size(info.Width, info.Height);
        _crop.Reset();
        return Task.CompletedTask;
    }

    protected override void OnPositionChanged(double seconds)
    {
        if (Math.Abs(_scrub.Value * Duration - seconds) > 0.01)
            _scrub.Value = seconds / Duration;
    }

    private void ApplySize()
    {
        if (_syncing) return;
        if (int.TryParse(_w.Text, out var w) && int.TryParse(_h.Text, out var h))
            _crop.SetSize(w, h);
    }

    private void Apply()
    {
        var r = _crop.Crop;
        // H.264 needs even dimensions.
        int w = (int)r.Width / 2 * 2, h = (int)r.Height / 2 * 2;
        var options = new CropVideoOptions((int)r.X, (int)r.Y, w, h) { Settings = Quality };
        var output = OutputNaming.ForTool(File, Tool);
        Run("Cropping video", "Video cropped", async (p, ct) => [await VideoTools.CropAsync(File, output, options, p, ct)]);
    }
}

// ---------------------------------------------------------------------------------------------

public sealed class ChangeSpeedWindow : VideoToolWindow
{
    private static readonly double[] Stops = [0.25, 0.5, 1, 1.5, 2, 4];
    private readonly Slider _speed = new() { Minimum = 0, Maximum = Stops.Length - 1, Value = 2, IsSnapToTickEnabled = true, TickFrequency = 1, TickPlacement = System.Windows.Controls.Primitives.TickPlacement.BottomRight };
    private readonly TextBlock _value = new();
    private readonly CheckBox _pitch = new() { Content = L.T("Keep the voice's pitch"), IsChecked = true, Margin = new Thickness(0, 12, 0, 0) };

    public ChangeSpeedWindow(ToolInfo tool, IReadOnlyList<string> files, JobRunner jobs, ScreenPoint at) : base(tool, files, jobs, at)
    {
        var header = new DockPanel { Margin = new Thickness(0, 14, 0, 4) };
        _value.Style = (Style)FindResource("Petal.Value");
        _value.FlowDirection = FlowDirection.LeftToRight;
        _value.FontWeight = FontWeights.SemiBold;
        DockPanel.SetDock(_value, Dock.Right);
        header.Children.Add(_value);
        header.Children.Add(Text("Playback speed", "Petal.Label"));
        Body.Children.Add(header);
        Body.Children.Add(_speed);
        var labels = new Grid { Margin = new Thickness(0, 2, 0, 0) };
        for (int i = 0; i < Stops.Length; i++)
        {
            labels.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var t = Text($"{Stops[i].ToString(CultureInfo.CurrentCulture)}×", "Petal.Caption");
            t.HorizontalAlignment = i == 0 ? HorizontalAlignment.Left : i == Stops.Length - 1 ? HorizontalAlignment.Right : HorizontalAlignment.Center;
            Grid.SetColumn(t, i);
            labels.Children.Add(t);
        }
        Body.Children.Add(labels);
        Body.Children.Add(_pitch);
        Body.Children.Add(Transport);
        _speed.ValueChanged += (_, _) => Sync();
        PrimaryButton.Content = L.T("Apply");
        PrimaryButton.Click += (_, _) => Apply();
        Sync();
    }

    private double Factor => Stops[(int)Math.Round(_speed.Value)];

    private void Sync()
    {
        _value.Text = $"{Factor.ToString(CultureInfo.CurrentCulture)}×";
        PlaybackSpeed = Factor;
        PrimaryButton.IsEnabled = Math.Abs(Factor - 1) > 0.001;
        FooterNote = Path.GetFileName(OutputNaming.ForTool(File, Tool, VideoTools.SpeedSuffix(Factor)));
    }

    protected override Task OnVideoReadyAsync(MediaInfo info)
    {
        _pitch.IsEnabled = info.HasAudio;
        return Task.CompletedTask;
    }

    private void Apply()
    {
        var options = new ChangeSpeedOptions(Factor, _pitch.IsChecked == true) { Settings = Quality };
        var output = OutputNaming.ForTool(File, Tool, VideoTools.SpeedSuffix(Factor));
        Run("Changing speed", "Speed changed", async (p, ct) => [await VideoTools.ChangeSpeedAsync(File, output, options, p, ct)]);
    }
}

// ---------------------------------------------------------------------------------------------

public sealed class VideoSnapshotsWindow : VideoToolWindow
{
    private readonly Slider _scrub = new() { Minimum = 0, Maximum = 1, Margin = new Thickness(0, 10, 0, 0) };
    private readonly WrapPanel _tiles = new();
    private readonly TextBlock _count = new();
    private readonly List<double> _times = [];

    public VideoSnapshotsWindow(ToolInfo tool, IReadOnlyList<string> files, JobRunner jobs, ScreenPoint at) : base(tool, files, jobs, at, 230, showPlay: false)
    {
        Body.Children.Add(_scrub);
        _scrub.ValueChanged += (_, e) => Seek(e.NewValue * Duration);
        Body.Children.Add(Transport);
        Body.Children.Add(Divider().WithMarginT(10));
        var row = new DockPanel { Margin = new Thickness(0, 10, 0, 6) };
        var add = new Button { Content = L.T("Add Frame") };
        add.Click += (_, _) => AddFrame();
        DockPanel.SetDock(add, Dock.Right);
        row.Children.Add(add);
        _count.Style = (Style)FindResource("Petal.Label");
        row.Children.Add(_count);
        Body.Children.Add(row);
        Body.Children.Add(new ScrollViewer { Content = _tiles, MaxHeight = 170, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        PrimaryButton.Click += (_, _) => Apply();
        Sync();
    }

    protected override Task OnVideoReadyAsync(MediaInfo info) => Task.CompletedTask;

    protected override void OnPositionChanged(double seconds)
    {
        if (Math.Abs(_scrub.Value * Duration - seconds) > 0.01) _scrub.Value = seconds / Duration;
    }

    protected override void OnKeys(object sender, KeyEventArgs e)
    {
        base.OnKeys(sender, e);
        if (e.Key == Key.Enter && e.OriginalSource is not Button) { AddFrame(); e.Handled = true; }
    }

    private void AddFrame()
    {
        var t = Math.Round(Position, 3);
        if (_times.Any(x => Math.Abs(x - t) < 0.001)) return;
        _times.Add(t);
        var image = new Image { FlowDirection = FlowDirection.LeftToRight, Width = 112, Height = 64, Stretch = Stretch.UniformToFill, Source = Frame.Image.Source };
        image.Clip = new RectangleGeometry(new Rect(0, 0, 112, 64), 8, 8);
        var chip = new Border { CornerRadius = new CornerRadius(5), Padding = new Thickness(5, 1, 5, 1), Margin = new Thickness(4), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom, Background = new SolidColorBrush(Color.FromArgb(0xB0, 0, 0, 0)), Child = new TextBlock { Text = TimeText.Format(t), Foreground = Brushes.White, FontSize = 10.5, FontFamily = Petal.App.Visuals.UiFonts.Mono } };
        var remove = IconButton("x", "Remove", 11, filled: true);
        remove.Width = remove.Height = 20;
        remove.HorizontalAlignment = HorizontalAlignment.Right;
        remove.VerticalAlignment = VerticalAlignment.Top;
        remove.Margin = new Thickness(0, 3, 3, 0);
        var tile = new Grid { Margin = new Thickness(0, 0, 8, 8), Cursor = Cursors.Hand };
        tile.Children.Add(image);
        tile.Children.Add(chip);
        tile.Children.Add(remove);
        remove.Click += (_, _) =>
        {
            _times.Remove(t);
            _tiles.Children.Remove(tile);
            Sync();
        };
        tile.MouseLeftButtonUp += (_, _) => Seek(t);
        _tiles.Children.Add(tile);
        Sync();
    }

    private void Sync()
    {
        _count.Text = _times.Count == 0 ? L.T("No frames yet") : L.F("{0} selected", _times.Count);
        PrimaryButton.Content = L.P(_times.Count, "Save {0} Frame", "Save {0} Frames");
        PrimaryButton.IsEnabled = _times.Count > 0;
    }

    private void Apply()
    {
        var snapshots = new List<VideoSnapshot>();
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in _times.Order())
        {
            // "weekend Snapshot.png", "weekend Snapshot 2.png"… without clashing with each other.
            var dir = Path.GetDirectoryName(Path.GetFullPath(File))!;
            var path = OutputNaming.Unique(dir, $"{Petal.Core.Formats.Formats.BaseName(File)} Snapshot", "png", p => taken.Contains(p) || System.IO.File.Exists(p));
            taken.Add(path);
            snapshots.Add(new VideoSnapshot(t, path));
        }
        var options = new VideoSnapshotsOptions(snapshots);
        Run("Saving frames", snapshots.Count == 1 ? "Frame saved" : "Frames saved", (p, ct) => VideoTools.SaveSnapshotsAsync(File, options, p, ct));
    }
}

// ---------------------------------------------------------------------------------------------

public sealed class SplitVideoWindow : VideoToolWindow
{
    private readonly List<double> _points = [];
    private readonly TextBlock _count = new();
    private readonly Canvas _markers = new() { IsHitTestVisible = false };
    private readonly ContentControl _timelineHost = new();
    private readonly TextBox _parts = new() { Text = "2", Width = 48, HorizontalContentAlignment = HorizontalAlignment.Center };
    private TrimTimeline? _timeline;

    public SplitVideoWindow(ToolInfo tool, IReadOnlyList<string> files, JobRunner jobs, ScreenPoint at) : base(tool, files, jobs, at)
    {
        var row = new DockPanel { Margin = new Thickness(0, 12, 0, 8) };
        var add = new Button { Content = new StackPanel { Orientation = Orientation.Horizontal, Children = { ThemedIcon("scissors", 14), new TextBlock { Text = "  " + L.T("Add Split") } } } };
        add.Click += (_, _) => AddPoint(Position);
        DockPanel.SetDock(add, Dock.Right);
        row.Children.Add(add);
        _count.Style = (Style)FindResource("Petal.Label");
        row.Children.Add(_count);
        Body.Children.Add(row);
        var stack = new Grid();
        stack.Children.Add(_timelineHost);
        stack.Children.Add(_markers);
        Body.Children.Add(stack);
        Body.Children.Add(Transport);
        Body.Children.Add(Divider().WithMarginT(10));

        var equal = new StackPanel { Orientation = Orientation.Horizontal };
        var minus = IconButton("minus", "Fewer", 13, filled: true);
        var plus = IconButton("plus", "More", 13, filled: true);
        minus.Click += (_, _) => Step(-1);
        plus.Click += (_, _) => Step(1);
        var set = new Button { Content = L.T("Set"), Margin = new Thickness(10, 0, 0, 0) };
        set.Click += (_, _) => SetEqual();
        equal.Children.Add(minus);
        equal.Children.Add(_parts);
        equal.Children.Add(plus);
        equal.Children.Add(set);
        Body.Children.Add(Row("Equal clips", equal, 100, 10));

        PrimaryButton.Content = L.T("Split Video");
        PrimaryButton.Click += (_, _) => Apply();
        ResetButton.Visibility = Visibility.Visible;
        ResetButton.Click += (_, _) =>
        {
            _points.Clear();
            Sync();
        };
        Sync();
    }

    private void Step(int delta)
    {
        int n = int.TryParse(_parts.Text, out var v) ? v : 2;
        _parts.Text = Math.Clamp(n + delta, 2, 50).ToString(CultureInfo.CurrentCulture);
    }

    protected override async Task OnVideoReadyAsync(MediaInfo info)
    {
        var strip = await LoadFilmstripAsync();
        _timeline = new TrimTimeline(strip, 52) { Duration = info.DurationSeconds, ShowHandles = false };
        _timeline.Scrubbed += t => Seek(t);
        _timelineHost.Content = _timeline;
        _timeline.SizeChanged += (_, _) => Sync();
    }

    protected override void OnPositionChanged(double seconds)
    {
        if (_timeline is not null) _timeline.Position = seconds;
    }

    private void AddPoint(double t)
    {
        if (t < 0.2 || t > Duration - 0.2 || _points.Any(p => Math.Abs(p - t) < 0.2)) return;
        _points.Add(t);
        Sync();
    }

    private void SetEqual()
    {
        int n = int.TryParse(_parts.Text, out var v) ? Math.Clamp(v, 2, 50) : 2;
        _points.Clear();
        for (int i = 1; i < n; i++) _points.Add(Duration * i / n);
        Sync();
    }

    private void Sync()
    {
        _count.Text = L.P(_points.Count, "{0} split point", "{0} split points");
        PrimaryButton.IsEnabled = _points.Count > 0;
        FooterNote = _points.Count > 0 ? L.F("{0} clips in “{1}”", _points.Count + 1, Path.GetFileName(OutputNaming.ForTool(File, Tool))) : Path.GetFileName(OutputNaming.ForTool(File, Tool));
        _markers.Children.Clear();
        if (_timeline is null || _timeline.ActualWidth <= 0) return;
        foreach (var p in _points)
        {
            double x = 14 + (_timeline.ActualWidth - 28) * p / Duration;
            var marker = new Border { Width = 3, Height = 60, CornerRadius = new CornerRadius(1.5) };
            marker.SetResourceReference(Border.BackgroundProperty, "Petal.Accent");
            Canvas.SetLeft(marker, x - 1.5);
            Canvas.SetTop(marker, -4);
            _markers.Children.Add(marker);
        }
    }

    private void Apply()
    {
        var folder = OutputNaming.ForTool(File, Tool);
        var options = new SplitVideoOptions(_points.Order().ToList()) { Settings = Quality };
        Run("Splitting video", "Video split", async (p, ct) =>
        {
            Directory.CreateDirectory(folder);
            await VideoTools.SplitAsync(File, folder, options, p, ct);
            return [folder];
        });
    }
}

// ---------------------------------------------------------------------------------------------

public sealed class RedactVideoWindow : VideoToolWindow
{
    private readonly RedactionCanvas _canvas = new();
    private readonly StackPanel _lanes = new();
    private readonly Dictionary<Parts.RedactionBox, RangeLane> _laneFor = [];
    private readonly Segmented<RedactStyle> _style;
    private readonly ColorWell _color = new(Colors.Black);
    private readonly Slider _scrub = new() { Minimum = 0, Maximum = 1, Margin = new Thickness(0, 8, 0, 0) };

    public RedactVideoWindow(ToolInfo tool, IReadOnlyList<string> files, JobRunner jobs, ScreenPoint at) : base(tool, files, jobs, at, 240)
    {
        PreviewHost.Children.Add(_canvas);
        Frame.FrameShown += bitmap => _canvas.Source = bitmap;
        _canvas.Changed += SyncLanes;

        _style = Segments([(RedactStyle.Solid, "Solid"), (RedactStyle.Blur, "Blur")], RedactStyle.Solid);
        _style.Changed += _ => SyncFill();
        _color.Changed += _ => SyncFill();
        var styleRow = new StackPanel { Orientation = Orientation.Horizontal };
        styleRow.Children.Add(_style);
        _color.Margin = new Thickness(12, 0, 0, 0);
        _color.VerticalAlignment = VerticalAlignment.Center;
        styleRow.Children.Add(_color);
        Body.Children.Add(Row("Style", styleRow, 70, 12));
        Body.Children.Add(Divider().WithMarginT(10));
        Body.Children.Add(SectionTitle("Redactions").WithMarginT(8));
        Body.Children.Add(Text("Draw a box on the picture. Each box gets a lane: drag its ends to choose when it's visible.", "Petal.Caption"));
        Body.Children.Add(new ScrollViewer { Content = _lanes, MaxHeight = 150, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 6, 0, 0) });
        Body.Children.Add(_scrub);
        _scrub.ValueChanged += (_, e) => Seek(e.NewValue * Duration);
        Body.Children.Add(Transport);
        PrimaryButton.Content = L.T("Save Redacted Copy");
        PrimaryButton.Click += (_, _) => Apply();
        ResetButton.Visibility = Visibility.Visible;
        ResetButton.Click += (_, _) => _canvas.Clear();
        SyncFill();
        SyncLanes();
    }

    protected override Task OnVideoReadyAsync(MediaInfo info)
    {
        _canvas.SourceSize = new Size(info.Width, info.Height);
        return Task.CompletedTask;
    }

    protected override void OnPositionChanged(double seconds)
    {
        if (Math.Abs(_scrub.Value * Duration - seconds) > 0.01) _scrub.Value = seconds / Duration;
    }

    private void SyncFill()
    {
        _canvas.BoxFill = _style.Value == RedactStyle.Blur
            ? new SolidColorBrush(Color.FromArgb(0xC8, 0x90, 0x90, 0x98))
            : new SolidColorBrush(_color.Color);
        _color.Visibility = _style.Value == RedactStyle.Solid ? Visibility.Visible : Visibility.Collapsed;
        _canvas.Refresh();
    }

    private void SyncLanes()
    {
        var boxes = _canvas.Boxes;
        foreach (var gone in _laneFor.Keys.Except(boxes).ToList())
            _laneFor.Remove(gone);
        foreach (var box in boxes.Where(b => !_laneFor.ContainsKey(b)))
        {
            var lane = new RangeLane(L.F("Box {0}", box.Number), 28) { Duration = Duration, Single = true, ClipText = L.F("Box {0}", box.Number) };
            lane.Add(0, Duration);
            _laneFor[box] = lane;
        }
        _lanes.Children.Clear();
        foreach (var box in boxes)
        {
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
            var label = Text(box.Number.ToString(CultureInfo.CurrentCulture), "Petal.Label");
            label.Width = 18;
            DockPanel.SetDock(label, Dock.Left);
            row.Children.Add(label);
            var border = new Border { CornerRadius = new CornerRadius(7), ClipToBounds = true, Child = _laneFor[box] };
            if (_laneFor[box].Parent is Border old) old.Child = null;
            border.Child = _laneFor[box];
            row.Children.Add(border);
            _lanes.Children.Add(row);
        }
        if (boxes.Count == 0)
            _lanes.Children.Add(Text("No boxes yet.", "Petal.Caption"));
        PrimaryButton.IsEnabled = boxes.Count > 0;
    }

    private void Apply()
    {
        var boxes = _canvas.Boxes.Select(b =>
        {
            var range = _laneFor.TryGetValue(b, out var lane) ? lane.Ranges.FirstOrDefault() : null;
            return new Petal.Engines.Media.RedactionBox((int)b.Rect.X, (int)b.Rect.Y, (int)Math.Max(2, b.Rect.Width), (int)Math.Max(2, b.Rect.Height), range?.Start ?? 0, range?.End ?? 0);
        }).ToList();
        var options = new RedactVideoOptions(boxes, _style.Value, ColorWell.Hex(_color.Color)) { Settings = Quality };
        var output = OutputNaming.ForTool(File, Tool);
        Run("Redacting video", "Video redacted", async (p, ct) => [await VideoTools.RedactAsync(File, output, options, p, ct)]);
    }
}

// ---------------------------------------------------------------------------------------------

public sealed class OrderedFilesWindow : ToolWindow
{
    public OrderedFilesWindow(ToolInfo tool, IReadOnlyList<string> files, JobRunner jobs, ScreenPoint at, string heading, string action,
        string running, string done, Func<IReadOnlyList<string>, string, IProgress<double>, CancellationToken, Task<string>> run)
        : base(tool.Name, 360)
    {
        var order = new OrderList(files);
        Body.Children.Add(SectionTitle(heading));
        Body.Children.Add(new ScrollViewer { Content = order, MaxHeight = 320, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        PrimaryButton.Content = action;
        FooterNote = Path.GetFileName(OutputNaming.ForTool(files[0], tool));
        PrimaryButton.Click += (_, _) =>
        {
            var items = order.Items.ToList();
            var output = OutputNaming.ForTool(items[0], tool);
            Close();
            _ = jobs.RunAsync(running, done, [new WorkItem(items[0], L.P(items.Count, "{0} file", "{0} files"), async (p, ct) => [await run(items, output, p, ct)])], at);
        };
    }
}
