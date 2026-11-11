using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Petal.App.Input;
using Petal.App.Jobs;
using Petal.App.Localization;
using Petal.App.Tools;
using Petal.App.Visuals;
using Petal.Core.Jobs;
using Petal.Core.Recording;
using Petal.Core.Tools;
using Petal.Core.Util;

namespace Petal.App.Screen.Studio;

/// <summary>
/// The cinematic studio: a recording on its background, playing as it will be saved, over a timeline of its zooms,
/// cuts and speed-ups. The view went in by itself where things happened; any zoom can be moved, stretched, made
/// stronger, pointed at one place or taken out, and new ones added; stretches can be cut out or sped up and the
/// ends trimmed. The look — background, margin, corners, shadow, the frame's shape, the pointer, motion blur,
/// shortcuts on screen, the sound — is on the right. Save Video makes the MP4; the edits are kept, so the same
/// recording opens again as it was left.
/// </summary>
internal sealed class StudioWindow : ToolWindow
{
    private const double InspectorWidth = 316;

    /// <summary>The picture's size: as large as 780 × 440, smaller on a screen too small for the window around it.</summary>
    private static readonly double PreviewHeight = Math.Clamp(Math.Min(SystemParameters.WorkArea.Height - 360, (SystemParameters.WorkArea.Width - InspectorWidth - 90) / 1.773), 260, 440);

    private static readonly double PreviewWidth = Math.Round(PreviewHeight * 1.773);

    private readonly StudioPreview _preview = new() { Width = PreviewWidth, Height = PreviewHeight };
    private readonly StudioTimeline _timeline = new();
    private readonly StackPanel _inspector = new() { Margin = new Thickness(0, 0, 10, 0) };
    private readonly TextBlock _time = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 10, 0), FontFamily = UiFonts.Mono, FontSize = 12.5, FlowDirection = FlowDirection.LeftToRight };
    private readonly Button _play;
    private readonly Button _undoButton, _redoButton;
    private readonly Grid _work = new();
    private readonly StackPanel _loading = new() { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly TextBlock _loadingText = new() { HorizontalAlignment = HorizontalAlignment.Center };
    private readonly Border _exporting = new() { Visibility = Visibility.Collapsed, CornerRadius = new CornerRadius(10), Padding = new Thickness(22, 16, 22, 18), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _exportText = new() { Foreground = Brushes.White, FontSize = 14, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly Border _exportTrack = new() { Width = 300, Height = 6, CornerRadius = new CornerRadius(3), Background = new SolidColorBrush(Color.FromArgb(0x50, 255, 255, 255)), Margin = new Thickness(0, 12, 0, 0) };
    private readonly Border _exportFill = new() { HorizontalAlignment = HorizontalAlignment.Left, Width = 0, CornerRadius = new CornerRadius(3) };
    private readonly List<StudioEdit> _undo = [], _redo = [];
    private readonly DispatcherTimer _save = new() { Interval = TimeSpan.FromSeconds(1.2) };
    private readonly DispatcherTimer _settle = new() { Interval = TimeSpan.FromMilliseconds(650) };
    private readonly Action<string, ScreenPoint>? _openFiles;
    private StudioProject? _project;
    private PointerArt? _art;
    private EditTimeline? _cuts;
    private StudioEdit? _before;
    private CancellationTokenSource? _export;
    private bool _playing, _dirty, _closeAfterSaving, _picking;
    private int _pickZoom = -1;

    /// <summary>Opens a recording just made.</summary>
    public static void Open(StudioProject project, Action<string, ScreenPoint>? openFiles)
    {
        var window = new StudioWindow(openFiles) { WindowStartupLocation = WindowStartupLocation.CenterScreen };
        window.Loaded += (_, _) => window.Ready(project);
        window.Show();
        window.Activate();
    }

    /// <summary>From the wheel: a video's project when it was saved from one, else a new one made from it.</summary>
    public StudioWindow(ToolInfo tool, string file, JobRunner jobs, ScreenPoint at) : this(null)
    {
        Loaded += async (_, _) =>
        {
            try
            {
                Ready(await Task.Run(() => FromFileAsync(file)));
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Opening a video in the studio");
                _loadingText.Text = L.T("This video couldn't be opened.") + "\n" + L.T(ex.Message);
            }
        };
    }

    private StudioWindow(Action<string, ScreenPoint>? openFiles) : base("Recording Studio", PreviewWidth + InspectorWidth + 52)
    {
        _openFiles = openFiles;
        // While the recording loads.
        _loading.Children.Add(new ProgressBar { IsIndeterminate = true, Width = 220, Height = 4 });
        _loadingText.Text = L.T("Getting the recording ready…");
        _loadingText.Style = (Style)FindResource("Petal.Caption");
        _loadingText.Margin = new Thickness(0, 12, 0, 0);
        _loadingText.TextAlignment = TextAlignment.Center;
        _loading.Children.Add(_loadingText);
        var host = new Grid { Height = PreviewHeight + 196 };
        host.Children.Add(_work);
        host.Children.Add(_loading);
        _work.Visibility = Visibility.Hidden;
        Body.Children.Add(host);

        // The picture and the look beside it.
        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(PreviewWidth) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(InspectorWidth) });
        var stage = new Grid { Width = PreviewWidth, Height = PreviewHeight };
        var frame = new Border { CornerRadius = new CornerRadius(10), ClipToBounds = true, Child = _preview };
        frame.SetResourceReference(Border.BackgroundProperty, "Petal.Preview");
        stage.Children.Add(frame);
        _exportFill.SetResourceReference(Border.BackgroundProperty, "Petal.Accent");
        _exportTrack.Child = _exportFill;
        _exporting.Background = new SolidColorBrush(Color.FromArgb(0xD0, 0x12, 0x12, 0x16));
        _exporting.Child = new StackPanel { Children = { _exportText, _exportTrack } };
        stage.Children.Add(_exporting);
        top.Children.Add(stage);
        var scroll = new ScrollViewer { Content = _inspector, Height = PreviewHeight, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetColumn(scroll, 2);
        top.Children.Add(scroll);
        var stack = new StackPanel();
        stack.Children.Add(top);

        // Playing, adding and undoing.
        var transport = new DockPanel { Margin = new Thickness(0, 10, 0, 0), LastChildFill = false };
        _play = IconButton("play", "Play (Space)", 16);
        _play.Click += (_, _) => Toggle();
        transport.Children.Add(_play);
        transport.Children.Add(_time);
        var right = new StackPanel { Orientation = Orientation.Horizontal };
        DockPanel.SetDock(right, Dock.Right);
        right.Children.Add(Action("zoomIn", "Add Zoom", "Zoom in from the playhead (Z)", AddZoom));
        right.Children.Add(Action("scissors", "Cut", "Cut out two seconds from the playhead (C)", () => AddEdit(EditKind.Cut)));
        right.Children.Add(Action("gauge", "Speed Up", "Play two seconds from the playhead faster (S)", () => AddEdit(EditKind.Speed)));
        right.Children.Add(new Border { Width = 1, Height = 22, Margin = new Thickness(8, 0, 8, 0) }.WithDynamic(Border.BackgroundProperty, "Petal.Divider"));
        _undoButton = IconButton("undo", "Undo (Ctrl+Z)", 15);
        _undoButton.Click += (_, _) => Undo();
        _redoButton = IconButton("redo", "Redo (Ctrl+Y)", 15);
        _redoButton.Click += (_, _) => Redo();
        right.Children.Add(_undoButton);
        right.Children.Add(_redoButton);
        transport.Children.Add(right);
        stack.Children.Add(transport);
        _timeline.Margin = new Thickness(-12, 8, -12, 0);
        stack.Children.Add(_timeline);
        _work.Children.Add(stack);

        PrimaryButton.Content = L.T("Save Video");
        PrimaryButton.Click += async (_, _) =>
        {
            if (_export is not null)
                _export.Cancel();
            else
                await ExportAsync();
        };
        PrimaryButton.IsEnabled = false;
        ResetButton.Content = L.T("Start Over");
        ResetButton.ToolTip = L.T("Back to the zooms DropWheel chose, with nothing cut");
        ResetButton.Click += (_, _) => StartOver();

        _timeline.Scrubbed += t =>
        {
            Pause();
            Seek(t);
        };
        _timeline.Edited += () =>
        {
            if (_project is null)
                return;
            _before ??= _project.Edit;
            _project.Edit = FromTimeline();
            _cuts = new EditTimeline(_project.Duration, _project.Edit);
            _preview.Rebuild();
            Note();
        };
        _timeline.Committed += () =>
        {
            if (_project is null)
                return;
            if (_before is not null)
                Remember(_before);
            _before = null;
            _project.Edit = FromTimeline();
            Changed();
        };
        _timeline.SelectionChanged += () =>
        {
            Picking(-1);
            Inspect();
        };
        _timeline.NewZoom = (start, end) => NewZoom(start, end);
        _preview.Picked += (x, y) =>
        {
            if (_project is null || _pickZoom < 0 || _pickZoom >= _project.Edit.Zooms.Count)
                return;
            var zooms = _project.Edit.Zooms.ToList();
            zooms[_pickZoom] = zooms[_pickZoom] with { Follow = false, X = Math.Round(x), Y = Math.Round(y) };
            Apply(_project.Edit with { Zooms = zooms });
        };
        _save.Tick += (_, _) =>
        {
            _save.Stop();
            SaveProject();
        };
        _settle.Tick += (_, _) =>
        {
            _settle.Stop();
            if (_before is not null && _project is not null)
            {
                Remember(_before);
                _before = null;
                Changed();
            }
        };
        PreviewKeyDown += OnKeys;
        Closing += OnClosing;
        Closed += (_, _) =>
        {
            CompositionTarget.Rendering -= OnFrame;
            _export?.Cancel();
            _preview.Player.Close();
            _preview.Face.Close();
            if (_save.IsEnabled)
                SaveProject();
            _art?.Dispose();
        };
    }

    private Button Action(string icon, string text, string tip, Action run)
    {
        var button = new Button
        {
            Margin = new Thickness(4, 0, 0, 0),
            Padding = new Thickness(10, 4, 12, 4),
            ToolTip = L.T(tip),
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children =
                {
                    ThemedIcon(icon, 14).WithMargin(0, 0, 6, 0),
                    new TextBlock { Text = L.T(text), VerticalAlignment = VerticalAlignment.Center },
                },
            },
        };
        button.Click += (_, _) => run();
        return button;
    }

    // ---------- opening ----------

    private static readonly string[] Playable = [".mp4", ".m4v", ".mov", ".wmv", ".avi", ".mpg", ".mpeg"];

    private async Task<StudioProject> FromFileAsync(string file)
    {
        if (StudioLibrary.ForVideo(file) is { } known && File.Exists(known.PathOf(known.Video)))
            return known;
        var info = await Petal.Engines.Media.MediaProbe.ProbeAsync(file);
        if (!info.HasVideo || info.Width <= 0)
            throw new InvalidOperationException("This file has no picture.");
        var folder = StudioLibrary.NewFolder();
        var project = new StudioProject
        {
            Folder = folder,
            Video = Path.GetFullPath(file),
            Width = info.Width,
            Height = info.Height,
            Fps = info.FrameRate > 1 ? Math.Min(info.FrameRate, 60) : 30,
            Duration = info.DurationSeconds,
            HasPointer = false,
        };
        if (!Playable.Contains(Path.GetExtension(file).ToLowerInvariant()))
        {
            // Windows can't play it: a copy it can, only to watch while editing.
            Dispatcher.Invoke(() => _loadingText.Text = L.T("Making a copy Windows can play…"));
            var preview = Path.Combine(folder, "preview.mp4");
            var ffmpeg = ToolLocator.Ffmpeg ?? throw new InvalidOperationException("FFmpeg was not found. Install FFmpeg or place ffmpeg.exe in DropWheel's tools folder.");
            var start = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
            foreach (var a in (string[])["-v", "error", "-i", file, "-vf", "scale=-2:'min(ih,1080)'", .. Petal.Engines.Media.MediaEncoders.H264(23, hardware: false, fast: true),
                         .. Petal.Engines.Media.MediaEncoders.Aac(128), "-y", preview])
                start.ArgumentList.Add(a);
            // What FFmpeg can't decode (H.264, HEVC, AAC) Windows decodes for it.
            await using var run = await Petal.Engines.Media.Foundation.FfmpegRun.StartAsync(start);
            var process = run.Process;
            await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            await run.FinishAsync();
            if (process.ExitCode != 0 || !File.Exists(preview))
                throw new InvalidOperationException("This video couldn't be read.");
            project.Preview = "preview.mp4";
        }
        project.Edit = project.Automatic(StudioSettings.Look, StudioSettings.Output, StudioSettings.Zoom);
        project.Save();
        StudioLibrary.Prune(folder);
        return project;
    }

    private void Ready(StudioProject project)
    {
        _project = project;
        _art = new PointerArt(project);
        _cuts = new EditTimeline(project.Duration, project.Edit);
        _dirty = project.ExportedAt is null;
        _preview.Load(project, _art);
        // Opened: the frame at the playhead (wherever it was put meanwhile) shows.
        _preview.Player.MediaOpened += (_, _) => Seek(_preview.Time);
        _preview.Player.MediaFailed += (_, e) =>
        {
            Log.Info($"The studio can't play {project.Video}: {e.ErrorException?.Message}");
            _loadingText.Text = L.T("Windows can't play this video.");
            _loading.Visibility = Visibility.Visible;
        };
        _preview.Player.Pause();
        _preview.Face.Pause();
        _timeline.Duration = project.Duration;
        _timeline.SetMarks(project.Clicks.Select(c => c.Time), ZoomPlanner.TypingRuns(project.Typing));
        SyncTimeline();
        Show(_cuts.NextKept(project.Edit.TrimStart));
        Inspect();
        Note();
        _loading.Visibility = Visibility.Collapsed;
        _work.Visibility = Visibility.Visible;
        PrimaryButton.IsEnabled = true;
        ResetButton.Visibility = Visibility.Visible;
        Buttons();
        CompositionTarget.Rendering += OnFrame;
        if (!_snapshot)
            _ = PeaksAsync(project);
        _timeline.Focus();
    }

    private async Task PeaksAsync(StudioProject project)
    {
        try
        {
            var data = await Petal.Engines.Media.MediaPreview.GetWaveformAsync(project.PathOf(project.Preview ?? project.Video), 1600);
            float peak = Math.Max(0.01f, data.Peak);
            _timeline.Peaks = data.Max.Select((m, i) => Math.Min(1, Math.Max(m, -data.Min[i]) / peak)).ToArray();
        }
        catch (Exception ex)
        {
            Log.Info($"No waveform for the studio: {ex.Message}");
        }
    }

    // ---------- playing ----------

    private void Toggle()
    {
        if (_playing)
            Pause();
        else
            Play();
    }

    private void Play()
    {
        if (_project is null || _cuts is null || _cuts.Length < 0.05)
            return;
        double t = _preview.Time;
        if (t >= _project.Edit.TrimEnd - 0.05 || t < _project.Edit.TrimStart)
            t = _project.Edit.TrimStart;
        Seek(_cuts.NextKept(t));
        _preview.Player.Play();
        _preview.Face.Play();
        _playing = true;
        _play.Content = ThemedIcon("pause", 16);
    }

    private void Pause()
    {
        if (!_playing)
            return;
        _preview.Player.Pause();
        _preview.Face.Pause();
        _playing = false;
        _play.Content = ThemedIcon("play", 16);
    }

    private void Seek(double t)
    {
        var at = TimeSpan.FromSeconds(Math.Max(0, t));
        _preview.Player.Position = at;
        _preview.Face.Position = at;
        Show(t);
    }

    private void Show(double t)
    {
        _preview.Show(t);
        _timeline.Position = t;
        if (_cuts is not null)
            _time.Text = $"{StudioTimeline.Clock(_cuts.ToOutput(t) ?? _cuts.ToOutput(_cuts.NextKept(t)) ?? 0, true)} / {StudioTimeline.Clock(_cuts.Length, true)}";
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        if (!_playing || _project is null || _cuts is null)
            return;
        double t = _preview.Player.Position.TotalSeconds;
        if (t >= _project.Edit.TrimEnd - 0.01)
        {
            Pause();
            Show(_project.Edit.TrimEnd);
            return;
        }
        if (_cuts.At(t) is { } piece)
        {
            if (Math.Abs(_preview.Player.SpeedRatio - piece.Speed) > 0.01)
                _preview.Player.SpeedRatio = _preview.Face.SpeedRatio = piece.Speed;
        }
        else
        {
            // A stretch cut out: on past it.
            double next = _cuts.NextKept(t);
            if (next >= _project.Edit.TrimEnd - 0.01)
            {
                Pause();
                Show(_project.Edit.TrimEnd);
                return;
            }
            Seek(next);
            return;
        }
        Show(t);
    }

    // ---------- editing ----------

    private StudioEdit FromTimeline() => _project!.Edit with
    {
        TrimStart = _timeline.TrimStart,
        TrimEnd = _timeline.TrimEnd,
        Zooms = _timeline.Zooms.ToList(),
        Edits = _timeline.Edits.ToList(),
    };

    private void SyncTimeline()
    {
        if (_project is null)
            return;
        var edit = _project.Edit;
        _timeline.Zooms = edit.Zooms.ToList();
        _timeline.Edits = edit.Edits.ToList();
        _timeline.TrimStart = edit.TrimStart;
        _timeline.TrimEnd = edit.TrimEnd <= 0 ? _project.Duration : edit.TrimEnd;
        if (_timeline.SelectedLane == 1 && _timeline.SelectedIndex >= _timeline.Zooms.Count || _timeline.SelectedLane == 2 && _timeline.SelectedIndex >= _timeline.Edits.Count)
            _timeline.Select(0, -1);
        _timeline.InvalidateVisual();
    }

    /// <summary>A change to keep: remembered for undo, shown, and saved.</summary>
    private void Apply(StudioEdit next)
    {
        if (_project is null)
            return;
        Remember(_project.Edit);
        _project.Edit = next;
        Changed();
    }

    /// <summary>A change while a slider is moved: shown at once, remembered for undo when the slider rests.</summary>
    private void Live(StudioEdit next)
    {
        if (_project is null)
            return;
        _before ??= _project.Edit;
        _project.Edit = next;
        _cuts = new EditTimeline(_project.Duration, next);
        _preview.Rebuild();
        Note();
        _settle.Stop();
        _settle.Start();
    }

    private void Remember(StudioEdit edit)
    {
        _undo.Add(edit);
        if (_undo.Count > 200)
            _undo.RemoveAt(0);
        _redo.Clear();
    }

    private void Changed(bool inspect = true)
    {
        if (_project is null)
            return;
        _dirty = true;
        _cuts = new EditTimeline(_project.Duration, _project.Edit);
        _preview.Rebuild();
        SyncTimeline();
        if (inspect)
            Inspect();
        Note();
        Buttons();
        Show(_preview.Time);
        _save.Stop();
        _save.Start();
    }

    private void Undo()
    {
        if (_project is null || _undo.Count == 0)
            return;
        _redo.Add(_project.Edit);
        _project.Edit = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        Changed();
    }

    private void Redo()
    {
        if (_project is null || _redo.Count == 0)
            return;
        _undo.Add(_project.Edit);
        _project.Edit = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        Changed();
    }

    private void Buttons()
    {
        _undoButton.IsEnabled = _undo.Count > 0;
        _redoButton.IsEnabled = _redo.Count > 0;
    }

    private void StartOver()
    {
        if (_project is null)
            return;
        Apply(_project.Automatic(_project.Edit.Look, _project.Edit.Output, StudioSettings.Zoom));
    }

    private ZoomSegment NewZoom(double start, double end)
    {
        double level = StudioSettings.Zoom > 1.05 ? StudioSettings.Zoom : 1.8;
        if (_project is { HasPointer: true })
            return new ZoomSegment(start, end, level);
        // Without a recorded pointer the zoom looks at the middle until a place is picked.
        return new ZoomSegment(start, end, level, Follow: false, X: _project?.Width / 2.0 ?? 0, Y: _project?.Height / 2.0 ?? 0);
    }

    private void AddZoom()
    {
        if (_project is null)
            return;
        double t = _preview.Time;
        var zooms = _project.Edit.Zooms.ToList();
        if (zooms.Any(z => t >= z.Start && t < z.End))
            return;
        double end = Math.Min(Math.Min(_project.Duration, t + 3), zooms.Where(z => z.Start > t).Select(z => z.Start).DefaultIfEmpty(_project.Duration).Min());
        if (end - t < 0.3)
            return;
        var made = NewZoom(t, end);
        zooms.Add(made);
        zooms.Sort((a, b) => a.Start.CompareTo(b.Start));
        Apply(_project.Edit with { Zooms = zooms });
        _timeline.Select(1, zooms.IndexOf(made));
    }

    private void AddEdit(EditKind kind)
    {
        if (_project is null)
            return;
        double t = _preview.Time;
        var edits = _project.Edit.Edits.ToList();
        if (edits.Any(e => t >= e.Start && t < e.End))
            return;
        double end = Math.Min(Math.Min(_project.Duration, t + 2), edits.Where(e => e.Start > t).Select(e => e.Start).DefaultIfEmpty(_project.Duration).Min());
        if (end - t < 0.3)
            return;
        var made = new EditSegment(t, end, kind, kind == EditKind.Speed ? 4 : 1);
        edits.Add(made);
        edits.Sort((a, b) => a.Start.CompareTo(b.Start));
        Apply(_project.Edit with { Edits = edits });
        _timeline.Select(2, edits.IndexOf(made));
    }

    private void RemoveSelected()
    {
        if (_project is null)
            return;
        int i = _timeline.SelectedIndex;
        if (_timeline.SelectedLane == 1 && i >= 0 && i < _project.Edit.Zooms.Count)
            Apply(_project.Edit with { Zooms = _project.Edit.Zooms.Where((_, k) => k != i).ToList() });
        else if (_timeline.SelectedLane == 2 && i >= 0 && i < _project.Edit.Edits.Count)
            Apply(_project.Edit with { Edits = _project.Edit.Edits.Where((_, k) => k != i).ToList() });
        else
            return;
        _timeline.Select(0, -1);
    }

    private void Picking(int zoom)
    {
        _pickZoom = zoom;
        _picking = zoom >= 0;
        _preview.Picking = _picking;
    }

    // ---------- the inspector ----------

    private void Inspect()
    {
        _inspector.Children.Clear();
        if (_project is null)
            return;
        int i = _timeline.SelectedIndex;
        if (_timeline.SelectedLane == 1 && i >= 0 && i < _project.Edit.Zooms.Count)
            InspectZoom(i);
        else if (_timeline.SelectedLane == 2 && i >= 0 && i < _project.Edit.Edits.Count)
            InspectEdit(i);
        else
            InspectLook();
    }

    private void Add(UIElement e) => _inspector.Children.Add(e);

    private void Heading(string text, bool first = false) => Add(SectionTitle(text).WithMargin(0, first ? 0 : 16, 0, 6));

    private CheckBox Switch(string text, bool value, Action<bool> changed)
    {
        var toggle = new CheckBox { Style = (Style)FindResource("Petal.Switch"), Content = L.T(text), IsChecked = value, Margin = new Thickness(0, 6, 0, 0) };
        toggle.Checked += (_, _) => changed(true);
        toggle.Unchecked += (_, _) => changed(false);
        return toggle;
    }

    private void InspectLook()
    {
        var p = _project!;
        var look = p.Edit.Look;
        void Set(StudioLook next) => Apply(p.Edit with { Look = next });
        void Slide(StudioLook next) => Live(p.Edit with { Look = next });

        Heading("Background", first: true);
        var swatches = new WrapPanel();
        foreach (var (key, name, from, to) in StudioBackgrounds.Gradients)
        {
            var brush = new LinearGradientBrush(Color.FromRgb(from.Red, from.Green, from.Blue), Color.FromRgb(to.Red, to.Green, to.Blue), new Point(0, 0), new Point(1, 1));
            swatches.Children.Add(Swatch(key, name, brush, look.Background == key, () => Set(look with { Background = key })));
        }
        if (StudioBackgrounds.WallpaperPath() is { } wallpaper && Thumbnail(wallpaper) is { } picture)
            swatches.Children.Add(Swatch("wallpaper", "Your wallpaper", new ImageBrush(picture) { Stretch = Stretch.UniformToFill }, look.Background == "wallpaper", () => Set(look with { Background = "wallpaper" })));
        var blurred = new LinearGradientBrush(Color.FromRgb(0x9C, 0xA3, 0xAF), Color.FromRgb(0x4B, 0x55, 0x63), 45);
        swatches.Children.Add(Swatch("blur", "The recording, blurred", blurred, look.Background == "blur", () => Set(look with { Background = "blur" }), "blend"));
        swatches.Children.Add(Swatch("none", "None: the recording fills the frame", Brushes.Black, look.Background == "none", () => Set(look with { Background = "none" }), "maximize"));
        bool own = Tools.Parts.ColourMath.TryParseHex(look.Background, out var ownColour);
        var start = own ? ownColour : Colors.White;
        swatches.Children.Add(Swatch("custom", "Any colour…", own ? Palette.Brush(ownColour) : Tools.Parts.CustomColourChip.Rainbow(), own, () => Tools.Parts.ColourWindow.Pick(start,
            c => Set(look with { Background = Tools.Parts.ColourMath.Hex(c) }),
            c => Slide(look with { Background = c == start ? look.Background : Tools.Parts.ColourMath.Hex(c) }), owner: this), own ? null : "palette"));
        Add(swatches);

        if (look.Framed)
        {
            Add(Text("Margin", "Petal.Caption").WithMargin(0, 8, 0, 0));
            var margin = Slider(0, 15, look.Padding * 100, v => L.F("{0}%", Math.Round(v)), 1);
            margin.Changed += v => Slide(p.Edit.Look with { Padding = v / 100 });
            Add(margin);
            Add(Text("Corners", "Petal.Caption").WithMargin(0, 6, 0, 0));
            var corners = Slider(0, 6, look.Roundness * 100, v => v < 0.05 ? L.T("Square") : L.F("{0}%", Math.Round(v, 1)), 0.2);
            corners.Changed += v => Slide(p.Edit.Look with { Roundness = v / 100 });
            Add(corners);
            Add(Switch("Shadow", look.Shadow, v => Set(p.Edit.Look with { Shadow = v })));
        }

        Heading("Shape");
        var shape = Segments([("recorded", L.T("Original")), ("16:9", "16:9"), ("9:16", "9:16"), ("1:1", "1:1"), ("4:5", "4:5")], look.Shape);
        shape.Stretch = true;
        shape.Changed += v => Set(p.Edit.Look with { Shape = v });
        Add(shape);
        if (look.Shape != "recorded")
            Add(Text("A shape other than the recording's follows the action, so nothing important is cut off.", "Petal.Caption").WithMargin(0, 6, 0, 0));

        Heading("Pointer");
        if (p.HasPointer)
        {
            Add(Text("Size", "Petal.Caption"));
            var size = Slider(1, 3, look.PointerSize, v => L.F("{0}×", Math.Round(v, 1)), 0.1);
            size.Changed += v => Slide(p.Edit.Look with { PointerSize = v });
            Add(size);
            Add(Switch("Smooth movement", look.SmoothPointer, v => Set(p.Edit.Look with { SmoothPointer = v })));
            Add(Switch("Hide when it rests", look.HideStillPointer, v => Set(p.Edit.Look with { HideStillPointer = v })));
            Add(Switch("Rings at clicks", look.ClickRings, v => Set(p.Edit.Look with { ClickRings = v })));
        }
        else
        {
            Add(Text("This video's pointer is part of its picture; recordings made with DropWheel have one the studio can draw.", "Petal.Caption"));
        }

        Heading("Effects");
        Add(Switch("Motion blur when the view moves", look.MotionBlur, v => Set(p.Edit.Look with { MotionBlur = v })));
        if (p.Keys.Count > 0)
            Add(Switch("Show the shortcuts pressed", look.ShowKeys, v => Set(p.Edit.Look with { ShowKeys = v })));

        Heading("Sound");
        if (p.Microphone is not null)
            Add(Switch("Clean up the microphone", look.CleanVoice, v => Set(p.Edit.Look with { CleanVoice = v })));
        Add(Switch("Even out the volume", look.EvenVolume, v => Set(p.Edit.Look with { EvenVolume = v })));

        Heading("Video");
        var auto = StudioLayout.OutputSize(p.Width, p.Height, look, p.Edit.Output with { Quality = "auto" });
        var quality = Choice([("auto", L.F("Automatic ({0})", Quality(Math.Min(auto.Width, auto.Height)))), ("1080", "1080p"), ("1440", "1440p"), ("2160", "4K")], p.Edit.Output.Quality);
        quality.SelectionChanged += (_, _) => Apply(p.Edit with { Output = p.Edit.Output with { Quality = ValueOf<string>(quality) } });
        Add(Row("Size", quality, 96, 0));
        var smooth = Segments([(60, L.F("{0} fps", 60)), (30, L.F("{0} fps", 30))], p.Edit.Output.Fps >= 50 ? 60 : 30);
        smooth.Changed += v => Apply(p.Edit with { Output = p.Edit.Output with { Fps = v } });
        Add(Row("Smoothness", smooth, 96, 8));
    }

    private static string Quality(int shorter) => shorter >= 2160 ? "4K" : $"{shorter}p";

    private static BitmapImage? Thumbnail(string path)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri(path);
            image.DecodePixelWidth = 96;
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UriFormatException)
        {
            return null;
        }
    }

    private static Border Swatch(string key, string name, Brush brush, bool selected, Action choose, string? icon = null)
    {
        var swatch = new Border
        {
            Width = 40,
            Height = 40,
            CornerRadius = new CornerRadius(9),
            Margin = new Thickness(0, 0, 8, 8),
            Background = brush,
            BorderThickness = new Thickness(selected ? 2.5 : 1),
            Cursor = Cursors.Hand,
            ToolTip = L.T(name),
            Tag = key,
        };
        swatch.SetResourceReference(Border.BorderBrushProperty, selected ? "Petal.Accent" : "Petal.ControlBorder");
        if (icon is not null)
            swatch.Child = new Viewbox { Width = 18, Height = 18, Child = ThemedIcon(icon, 18, "Petal.OnAccent") };
        System.Windows.Automation.AutomationProperties.SetName(swatch, L.T(name));
        swatch.MouseLeftButtonUp += (_, _) => choose();
        return swatch;
    }

    private void InspectZoom(int index)
    {
        var p = _project!;
        var zoom = p.Edit.Zooms[index];
        Heading("Zoom", first: true);
        Add(Text(L.F("{0} to {1}", StudioTimeline.Clock(zoom.Start, true), StudioTimeline.Clock(zoom.End, true)), "Petal.Caption"));
        Add(Text("How far in", "Petal.Caption").WithMargin(0, 10, 0, 0));
        var level = Slider(1.2, 4, zoom.Level, v => L.F("{0}×", Math.Round(v, 1)), 0.1);
        level.Changed += v =>
        {
            var zooms = p.Edit.Zooms.ToList();
            if (index < zooms.Count)
            {
                zooms[index] = zooms[index] with { Level = Math.Round(v, 2) };
                Live(p.Edit with { Zooms = zooms });
            }
        };
        Add(level);
        Add(Text("Looks at", "Petal.Caption").WithMargin(0, 10, 0, 4));
        var focus = Segments([(true, L.T("The action")), (false, L.T("One place"))], zoom.Follow);
        focus.Stretch = true;
        focus.Changed += follow =>
        {
            var zooms = p.Edit.Zooms.ToList();
            if (index >= zooms.Count)
                return;
            if (follow)
            {
                zooms[index] = zooms[index] with { Follow = true };
                Picking(-1);
            }
            else
            {
                // Where the view looked half way through, until a place is picked.
                var view = _preview.Camera?.View((zoom.Start + zoom.End) / 2);
                zooms[index] = zooms[index] with { Follow = false, X = Math.Round(view?.CenterX ?? p.Width / 2.0), Y = Math.Round(view?.CenterY ?? p.Height / 2.0) };
                Picking(index);
            }
            Apply(p.Edit with { Zooms = zooms });
        };
        Add(focus);
        if (!p.HasPointer && zoom.Follow)
            Add(Text("This video has no recorded pointer to follow: pick a place.", "Petal.Caption").WithMargin(0, 6, 0, 0));
        if (!zoom.Follow)
        {
            Picking(index);
            Add(Text("Click the picture where the view should look.", "Petal.Caption").WithMargin(0, 6, 0, 0));
            // Shown from where it looks.
            if (_preview.Time < zoom.Start || _preview.Time >= zoom.End)
                Seek(Math.Min(zoom.End - 0.05, zoom.Start + 1));
        }
        var remove = new Button { Content = L.T("Remove Zoom"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 16, 0, 0), ToolTip = L.T("Delete") };
        remove.Click += (_, _) => RemoveSelected();
        Add(remove);
    }

    private void InspectEdit(int index)
    {
        var p = _project!;
        var edit = p.Edit.Edits[index];
        Heading(edit.Kind == EditKind.Cut ? "Cut out" : "Sped up", first: true);
        Add(Text(L.F("{0} to {1}", StudioTimeline.Clock(edit.Start, true), StudioTimeline.Clock(edit.End, true)), "Petal.Caption"));
        var kind = Segments([(EditKind.Cut, L.T("Cut out")), (EditKind.Speed, L.T("Speed up"))], edit.Kind);
        kind.Stretch = true;
        kind.Margin = new Thickness(0, 10, 0, 0);
        kind.Changed += k =>
        {
            var edits = p.Edit.Edits.ToList();
            if (index < edits.Count)
            {
                edits[index] = edits[index] with { Kind = k, Speed = k == EditKind.Speed ? Math.Max(2, edits[index].Speed) : 1 };
                Apply(p.Edit with { Edits = edits });
            }
        };
        Add(kind);
        if (edit.Kind == EditKind.Speed)
        {
            Add(Text("How fast", "Petal.Caption").WithMargin(0, 10, 0, 4));
            var speed = Segments([(2.0, "2×"), (4.0, "4×"), (8.0, "8×"), (16.0, "16×")], edit.Speed);
            speed.Stretch = true;
            speed.Changed += v =>
            {
                var edits = p.Edit.Edits.ToList();
                if (index < edits.Count)
                {
                    edits[index] = edits[index] with { Speed = v };
                    Apply(p.Edit with { Edits = edits });
                }
            };
            Add(speed);
            Add(Text(L.F("{0} becomes {1}.", StudioTimeline.Clock(edit.Length, true), StudioTimeline.Clock(edit.Length / edit.Speed, true)), "Petal.Caption").WithMargin(0, 6, 0, 0));
        }
        var remove = new Button { Content = L.T("Remove"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 16, 0, 0), ToolTip = L.T("Delete") };
        remove.Click += (_, _) => RemoveSelected();
        Add(remove);
    }

    private void Note()
    {
        if (_project is null || _cuts is null)
            return;
        var (w, h) = StudioLayout.OutputSize(_project.Width, _project.Height, _project.Edit.Look, _project.Edit.Output);
        // The quality ("1080p") rather than "1080 × 1920": a size reads back to front in a right-to-left line, the quality never does.
        var dot = L.IsRightToLeft ? (char)0x200F + " · " + (char)0x200F : " · ";
        FooterNote = string.Join(dot, StudioTimeline.Clock(_cuts.Length), Quality(Math.Min(w, h)), L.F("{0} fps", _project.Edit.Output.Fps));
    }

    // ---------- keys ----------

    private void OnKeys(object sender, KeyEventArgs e)
    {
        if (_project is null || _export is not null || e.OriginalSource is TextBox)
            return;
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control), shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        switch (e.Key)
        {
            case Key.Space:
                Toggle();
                break;
            case Key.Delete or Key.Back:
                RemoveSelected();
                break;
            case Key.Z when ctrl && shift:
            case Key.Y when ctrl:
                Redo();
                break;
            case Key.Z when ctrl:
                Undo();
                break;
            case Key.Z:
                AddZoom();
                break;
            case Key.C when !ctrl:
                AddEdit(EditKind.Cut);
                break;
            case Key.S when !ctrl:
                AddEdit(EditKind.Speed);
                break;
            case Key.Left or Key.Right:
                Pause();
                double step = shift ? 1 : 1 / Math.Max(1, _project.Fps);
                Seek(Math.Clamp(_preview.Time + (e.Key == Key.Left ? -step : step), 0, _project.Duration));
                break;
            case Key.Home:
                Pause();
                Seek(_project.Edit.TrimStart);
                break;
            case Key.End:
                Pause();
                Seek(_project.Edit.TrimEnd);
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    // ---------- saving ----------

    private void SaveProject()
    {
        try
        {
            _project?.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Info($"The studio project couldn't be saved: {ex.Message}");
        }
    }

    /// <summary>Where the video goes: over the one saved from this project before, else beside the recordings (or the video it was made from).</summary>
    private static string OutputFor(StudioProject project)
    {
        if (project.Exported is { } before && File.Exists(before))
            return before;
        if (Path.IsPathRooted(project.Video))
        {
            var folder = Path.GetDirectoryName(project.Video) ?? ScreenRecorder.Folder;
            return OutputNaming.Unique(folder, Path.GetFileNameWithoutExtension(project.Video) + " Cinematic", "mp4");
        }
        Directory.CreateDirectory(ScreenRecorder.Folder);
        var stamp = project.Made.ToString("yyyy-MM-dd HH.mm.ss", System.Globalization.CultureInfo.InvariantCulture);
        return OutputNaming.Unique(ScreenRecorder.Folder, $"Recording {stamp}", "mp4");
    }

    private async Task ExportAsync()
    {
        if (_project is null || _cuts is null || _export is not null)
            return;
        Pause();
        if (_cuts.Length < 0.1)
        {
            MessageBox.Show(this, L.T("Nothing is left of the recording after the cuts."), Title, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var project = _project;
        var output = OutputFor(project);
        var cancel = _export = new CancellationTokenSource();
        _exporting.Visibility = Visibility.Visible;
        _exportText.Text = L.F("Making the video… {0}%", 0);
        _exportFill.Width = 0;
        _work.IsEnabled = false;
        ResetButton.IsEnabled = false;
        PrimaryButton.Content = L.T("Cancel");
        SaveProject();
        StudioSettings.Remember(project.Edit.Look, project.Edit.Output);
        var progress = new Progress<double>(p =>
        {
            _exportText.Text = L.F("Making the video… {0}%", (int)Math.Round(p * 100));
            _exportFill.Width = _exportTrack.Width * Math.Clamp(p, 0, 1);
        });
        var clock = Stopwatch.StartNew();
        try
        {
            // A copy of the project, so edits can't change it while the video is made.
            var frozen = StudioProject.Load(project.Folder) ?? project;
            await Task.Run(() => StudioExport.RenderAsync(frozen, output, progress, cancel.Token), cancel.Token);
            project.Exported = output;
            project.ExportedAt = DateTime.Now;
            SaveProject();
            _dirty = false;
            Log.Info($"Studio video saved in {clock.Elapsed.TotalSeconds:0} s: {output}");
            Hud.HudCard.ShowSaved("Video saved", output);
            if (_closeAfterSaving)
                Close();
        }
        catch (OperationCanceledException)
        {
            _closeAfterSaving = false;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Saving the studio's video");
            _closeAfterSaving = false;
            Hud.HudCard.ShowAt(Hud.HudCard.CornerPoint(), "", "").Fail(L.T("The video couldn't be made"), L.T(ex.Message));
        }
        finally
        {
            _export = null;
            cancel.Dispose();
            if (IsLoaded)
            {
                _exporting.Visibility = Visibility.Collapsed;
                _work.IsEnabled = true;
                ResetButton.IsEnabled = true;
                PrimaryButton.Content = L.T("Save Video");
            }
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_project is null || _snapshot)
            return;
        if (_export is not null)
        {
            if (MessageBox.Show(this, L.T("Stop making the video and close?"), Title, MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
                e.Cancel = true;
            return;
        }
        if (!_dirty)
            return;
        var answer = MessageBox.Show(this, L.T("Save the video before closing? If you don't, this recording isn't saved."), Title, MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (answer == MessageBoxResult.Cancel)
            e.Cancel = true;
        else if (answer == MessageBoxResult.Yes)
        {
            e.Cancel = true;
            _closeAfterSaving = true;
            _ = ExportAsync();
        }
    }

    // ---------- for the design snapshots ----------

    /// <summary>A studio built around a project without playing it, for the design snapshots.</summary>
    internal static StudioWindow ForSnapshot(StudioProject project, ImageSource still, float[] peaks, Action<StudioWindow>? then = null)
    {
        var window = new StudioWindow(null) { _snapshot = true };
        window.Loaded += (_, _) =>
        {
            window.Ready(project);
            window._preview.StillForSnapshot(still);
            window._timeline.Peaks = peaks;
            window.Seek(project.Edit.TrimStart);
            then?.Invoke(window);
        };
        return window;
    }

    private bool _snapshot;

    internal void SelectForSnapshot(int lane, int index) => _timeline.Select(lane, index);

    internal void SeekForSnapshot(double t) => Seek(t);

    internal void ExportingForSnapshot(double share)
    {
        _exporting.Visibility = Visibility.Visible;
        _exportText.Text = L.F("Making the video… {0}%", (int)Math.Round(share * 100));
        _exportFill.Width = _exportTrack.Width * share;
        PrimaryButton.Content = L.T("Cancel");
    }
}

internal static class StudioUi
{
    public static T WithMargin<T>(this T element, double left, double top, double right, double bottom) where T : FrameworkElement
    {
        element.Margin = new Thickness(left, top, right, bottom);
        return element;
    }
}
