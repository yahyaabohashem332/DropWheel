using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Petal.App.Input;
using Petal.App.Jobs;
using Petal.App.Settings;
using Petal.App.Tools.Parts;
using Petal.App.Visuals;
using Petal.Core.Jobs;
using Petal.Core.Tools;
using Petal.Engines.Media;
using Petal.App.Localization;

namespace Petal.App.Tools.Windows;

/// <summary>Shared plumbing for audio and video tool windows: probing, playback, running the job.</summary>
public abstract class MediaToolWindow : ToolWindow
{
    private readonly DispatcherTimer _playTimer = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private MediaElement? _player;
    private double _playEnd;

    protected MediaToolWindow(ToolInfo tool, IReadOnlyList<string> files, JobRunner jobs, ScreenPoint at, double width = 400)
        : base(tool.Name, width)
    {
        Tool = tool;
        Files = files;
        Jobs = jobs;
        At = at;
        FooterNote = files.Count == 1 ? Path.GetFileName(OutputNaming.ForTool(files[0], tool)) : L.P(files.Count, "{0} file", "{0} files");
        Closed += (_, _) => StopPlayback();
        PreviewKeyDown += OnKeys;
        Loaded += async (_, _) =>
        {
            try
            {
                Info = await MediaProbe.ProbeAsync(files[0]);
                await OnMediaReadyAsync(Info);
            }
            catch (Exception ex)
            {
                Log.Error(ex, $"Opening {files[0]} in {tool.Name}");
                FooterNote = "Couldn't read this file.";
                PrimaryButton.IsEnabled = false;
            }
        };
    }

    protected ToolInfo Tool { get; }
    protected IReadOnlyList<string> Files { get; }
    protected JobRunner Jobs { get; }
    protected ScreenPoint At { get; }
    protected MediaInfo? Info { get; private set; }
    protected string File => Files[0];

    protected double Duration => Math.Max(0.01, Info?.DurationSeconds ?? 0.01);

    /// <summary>One frame's length, for frame stepping.</summary>
    protected double FrameStep => Info is { FrameRate: > 1 } i ? 1.0 / i.FrameRate : 1.0 / 30;

    protected static ConversionSettings Quality => SettingsStore.Current.Conversion;

    protected abstract Task OnMediaReadyAsync(MediaInfo info);

    /// <summary>Space plays, arrow keys step frames — handled by windows that have a transport.</summary>
    protected virtual void OnKeys(object sender, KeyEventArgs e) { }

    /// <summary>Closes the window and runs the tool on a progress card that springs from the drop point.</summary>
    protected void Run(string running, string done, Func<IProgress<double>, CancellationToken, Task<IReadOnlyList<string>>> work)
    {
        Close();
        _ = Jobs.RunAsync(running, done, [new WorkItem(File, Path.GetFileName(File), work)], At);
    }

    protected void RunEach(string running, string done, Func<string, IProgress<double>, CancellationToken, Task<IReadOnlyList<string>>> work)
    {
        Close();
        _ = Jobs.RunAsync(running, done, Files.Select(f => new WorkItem(f, Path.GetFileName(f), (p, ct) => work(f, p, ct))).ToList(), At);
    }

    // ---------- video playback via MediaElement (when Windows can decode the file) ----------

    /// <summary>Puts a hidden player over the preview; it's shown only while playing.</summary>
    protected void AttachPlayer(Grid host)
    {
        _player = new MediaElement
        {
            LoadedBehavior = MediaState.Manual,
            UnloadedBehavior = MediaState.Close,
            Stretch = Stretch.Uniform,
            Visibility = Visibility.Hidden,
            IsMuted = false,
            ScrubbingEnabled = true,
        };
        _player.MediaFailed += (_, e) =>
        {
            Log.Info($"Preview playback isn't available for {File}: {e.ErrorException?.Message}");
            StopPlayback();
            PlaybackUnavailable = true;
        };
        _player.Source = new Uri(File);
        host.Children.Add(_player);
        _playTimer.Tick += (_, _) =>
        {
            if (_player is null) return;
            var t = _player.Position.TotalSeconds;
            PlaybackPosition(t);
            if (t >= _playEnd - 0.02)
                StopPlayback();
        };
    }

    protected bool PlaybackUnavailable { get; private set; }
    protected bool IsPlaying { get; private set; }

    protected double PlaybackSpeed
    {
        set
        {
            if (_player is not null) _player.SpeedRatio = value;
        }
    }

    protected void StartPlayback(double from, double to)
    {
        if (_player is null || PlaybackUnavailable) return;
        _playEnd = to;
        _player.Visibility = Visibility.Visible;
        _player.Position = TimeSpan.FromSeconds(from);
        _player.Play();
        IsPlaying = true;
        _playTimer.Start();
        PlaybackStateChanged(true);
    }

    protected void StopPlayback()
    {
        _playTimer.Stop();
        if (_player is not null)
        {
            _player.Pause();
            _player.Visibility = Visibility.Hidden;
        }
        if (IsPlaying)
        {
            IsPlaying = false;
            PlaybackStateChanged(false);
        }
    }

    protected virtual void PlaybackPosition(double seconds) { }

    protected virtual void PlaybackStateChanged(bool playing) { }

    // ---------- small builders ----------

    protected static TextBox TimeBox(double seconds, Action<double> changed)
    {
        var box = new TextBox { FlowDirection = FlowDirection.LeftToRight, Text = TimeText.Format(seconds), Width = 92, FontFamily = UiFonts.Mono, HorizontalContentAlignment = HorizontalAlignment.Center };
        box.LostKeyboardFocus += (_, _) => Commit();
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) Commit(); };
        void Commit()
        {
            if (TimeText.TryParse(box.Text, out var t)) changed(t);
        }
        return box;
    }

    protected static string Size(long bytes) => Util.Sizes.Format(bytes);
}
