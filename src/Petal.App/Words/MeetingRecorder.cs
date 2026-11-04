using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using NAudio.Wave;
using Petal.App.Localization;
using Petal.App.Screen;
using Petal.App.Settings;
using Petal.App.Tools;
using Petal.App.Tools.Parts;
using Petal.App.Visuals;
using Petal.Core.Models;
using Petal.Core.Text;
using Petal.Core.Util;
using Petal.Engines.Media;
using Petal.Recipes;
using static Petal.App.Interop.NativeMethods;

namespace Petal.App.Words;

/// <summary>
/// Meeting notes: Petal records the meeting (what the PC plays and your microphone), then writes the
/// transcript with times and, with the AI model, a summary, the decisions and the action items. The notes
/// and the sound are kept in Documents\Petal Meetings. Nothing leaves the PC.
/// </summary>
internal static class MeetingRecorder
{
    private static readonly List<AudioTrack> Tracks = [];
    private static readonly Stopwatch Clock = new();
    private static string? _work;
    private static DateTime _started;
    private static MeetingPill? _pill;
    private static bool _busy;

    public static bool IsRecording => _pill is not null;

    public static string Folder => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "DropWheel Meetings");

    public static void Toggle()
    {
        if (IsRecording)
            _ = StopAsync();
        else
            Start();
    }

    private static void Start()
    {
        if (_busy)
            return;
        if (Listener.ReadyModel() is null)
        {
            DictationSetupWindow.ShowSingle();
            return;
        }
        if (ToolLocator.Ffmpeg is null)
        {
            Hud.HudCard.ShowAt(Hud.HudCard.CornerPoint(), "", "").Fail(L.T("Meeting Notes"), L.T("FFmpeg was not found. Install FFmpeg or place ffmpeg.exe in DropWheel's tools folder."));
            return;
        }
        _work = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "DropWheel meeting " + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_work);
        foreach (var (make, name) in new (Func<IWaveIn>, string)[] { (() => new WasapiLoopbackCapture(), "system.wav"), (() => new NAudio.CoreAudioApi.WasapiCapture(), "mic.wav") })
        {
            try
            {
                Tracks.Add(new AudioTrack(make(), System.IO.Path.Combine(_work, name)));
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException or ArgumentException)
            {
                Log.Error(ex, "Opening a sound for meeting notes");
            }
        }
        if (Tracks.Count == 0)
        {
            Hud.HudCard.ShowAt(Hud.HudCard.CornerPoint(), "", "").Fail(L.T("Meeting Notes"), L.T("The sound couldn't be listened to."));
            return;
        }
        _started = DateTime.Now;
        Clock.Restart();
        foreach (var t in Tracks)
            t.Start();
        _pill = new MeetingPill();
        _pill.Stop += () => _ = StopAsync();
        _pill.Cancel += Cancel;
        _pill.Show();
    }

    private static void Cancel()
    {
        var length = Clock.Elapsed;
        Clock.Stop();
        foreach (var t in Tracks)
        {
            t.Stop(length);
            t.Dispose();
        }
        Tracks.Clear();
        _pill?.Close();
        _pill = null;
        TryDelete(_work);
    }

    private static async Task StopAsync()
    {
        if (_pill is null || _work is null)
            return;
        _busy = true;
        var length = Clock.Elapsed;
        Clock.Stop();
        foreach (var t in Tracks)
            t.Stop(length);
        var sounds = Tracks.Where(t => t.HasSound).Select(t => t.Path).ToList();
        foreach (var t in Tracks)
            t.Dispose();
        Tracks.Clear();
        _pill.Close();
        _pill = null;
        var work = _work;
        var card = Hud.HudCard.ShowAt(Hud.HudCard.CornerPoint(), L.T("Writing the meeting down…"), L.T("This takes a while for a long meeting"));
        card.SetProgress(null);
        try
        {
            if (sounds.Count == 0)
                throw new InvalidOperationException("Nothing was heard.");
            Directory.CreateDirectory(Folder);
            var stamp = _started.ToString("yyyy-MM-dd HH.mm", System.Globalization.CultureInfo.InvariantCulture);
            var audio = Petal.Core.Jobs.OutputNaming.Unique(Folder, $"Meeting {stamp}", "m4a");
            var speech = System.IO.Path.Combine(work, "speech.wav");
            await FfmpegAsync(Mix(sounds, ["-ac", "1", "-ar", "16000", "-y", speech]));
            await FfmpegAsync(Mix(sounds, ["-c:a", "aac", "-b:a", "96k", "-y", audio]));
            var model = Listener.Models.Reverse().First(ModelStore.IsAvailable);
            var progress = new Progress<double>(p => card.SetProgress(p * 0.8));
            var transcript = await Transcriber.TranscribeAsync(speech, new TranscribeOptions(ModelStore.Find(model)!, SettingsStore.Current.DictationLanguage), progress);
            var text = MeetingNotes.TranscriptText(transcript);
            string? summary = null;
            if (AskPetal.ModelReady && text.Length > 0)
            {
                card.SetProgress(0.85);
                try
                {
                    summary = await MeetingNotes.SummarizeAsync(text);
                }
                catch (Exception ex) when (ex is InvalidOperationException or IOException)
                {
                    Log.Error(ex, "Summarising a meeting");
                }
            }
            var notes = System.IO.Path.ChangeExtension(audio, ".md");
            bool arabic = transcript.Language == "ar" || transcript.Language is null or "" && KeyboardLayout.LooksArabicLanguage(text);
            await File.WriteAllTextAsync(notes, MeetingNotes.Markdown(_started, length, summary, text, arabic));
            card.Succeed(L.T("Meeting notes ready"), System.IO.Path.GetFileName(notes), () => MeetingNotesWindow.Open(notes, audio));
            MeetingNotesWindow.Open(notes, audio);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Error(ex, "Meeting notes");
            card.Fail(L.T("Meeting notes not made"), L.T(ex.Message == "Nothing was heard." ? "Nothing was heard." : "Something went wrong. The activity log has the details."));
        }
        finally
        {
            _busy = false;
            TryDelete(work);
        }
    }

    /// <summary>FFmpeg arguments that mix the recorded sounds (PC and microphone) into one.</summary>
    private static List<string> Mix(List<string> sounds, string[] output)
    {
        var args = new List<string> { "-v", "error" };
        foreach (var s in sounds)
            args.AddRange(["-i", s]);
        if (sounds.Count == 2)
            args.AddRange(["-filter_complex", "[0:a][1:a]amix=inputs=2:duration=longest:dropout_transition=0"]);
        args.AddRange(output);
        return args;
    }

    private static async Task FfmpegAsync(List<string> args)
    {
        var start = new ProcessStartInfo(ToolLocator.Ffmpeg!) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (var a in args)
            start.ArgumentList.Add(a);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("FFmpeg didn't start.");
        var errors = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
            throw new InvalidOperationException(errors);
    }

    private static void TryDelete(string? folder)
    {
        if (folder is null)
            return;
        _ = Task.Run(async () =>
        {
            for (int i = 0; i < 10; i++)
            {
                try
                {
                    if (Directory.Exists(folder))
                        Directory.Delete(folder, true);
                    return;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    await Task.Delay(500);
                }
            }
        });
    }

    public static TimeSpan Elapsed => Clock.Elapsed;
}

/// <summary>The "recording the meeting" pill at the top of the screen: the time, Stop and Cancel.</summary>
internal sealed class MeetingPill : Window
{
    private readonly TextBlock _time = new() { Foreground = Brushes.White, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0), MinWidth = 150 };
    private readonly System.Windows.Threading.DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };

    public event Action? Stop;
    public event Action? Cancel;

    public MeetingPill()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        Title = L.T("Meeting Notes");
        L.Apply(this);
        var dot = new Ellipse { Width = 10, Height = 10, Fill = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44)), Margin = new Thickness(10, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        dot.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.25, TimeSpan.FromMilliseconds(800)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
        var row = new StackPanel { Orientation = Orientation.Horizontal, Children = { dot, _time } };
        row.Children.Add(new StripButton("check", "Stop and write the notes", () => Stop?.Invoke(), null, 30) { Accent = Theme.Current.Accent });
        row.Children.Add(new StripButton("x", "Cancel", () => Cancel?.Invoke(), null, 30));
        var strip = ScreenUi.Strip(row, 20);
        strip.Margin = new Thickness(10);
        ScreenUi.DragFromBackground(this, strip);
        Content = strip;
        _tick.Tick += (_, _) => UpdateTime();
        _tick.Start();
        UpdateTime();
        Closed += (_, _) => _tick.Stop();
        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            long ex = (long)GetWindowLongPtr(handle, GWL_EXSTYLE) | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
            SetWindowLongPtr(handle, GWL_EXSTYLE, new IntPtr(ex));
            // Out of the meeting's own screen shares and recordings.
            ScreenNative.SetWindowDisplayAffinity(handle, ScreenNative.WDA_EXCLUDEFROMCAPTURE);
        };
        Loaded += (_, _) =>
        {
            var monitor = ScreenNative.UnderPointer();
            double s = monitor.Scale;
            Left = (monitor.Work.Left + monitor.Work.Width / 2.0) / s - ActualWidth / 2;
            Top = monitor.Work.Top / s + 8;
        };
    }

    private void UpdateTime() => _time.Text = L.F("Recording the meeting… {0}", MeetingNotes.Stamp(MeetingRecorder.Elapsed));
}

/// <summary>The notes of a meeting: the summary and the transcript, to read, copy, or open.</summary>
internal sealed class MeetingNotesWindow : ToolWindow
{
    private readonly string _notes, _audio;
    private readonly TextBox _text = new() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, Height = 380, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, AcceptsReturn = true };
    private readonly Segmented<bool> _view;
    private readonly ModelPanel _model = new(AskPetal.Models) { Wanted = false };
    private readonly Button _summarize = new() { Content = L.T("Write a Summary"), Visibility = Visibility.Collapsed, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 10, 0, 0) };
    private string _summary = "", _transcript = "";

    public static void Open(string notes, string audio)
    {
        var window = new MeetingNotesWindow(notes, audio);
        window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        window.Show();
        window.Activate();
    }

    internal static MeetingNotesWindow ForSnapshot(string notes) => new(notes, notes);

    private MeetingNotesWindow(string notes, string audio) : base("Meeting Notes", 560)
    {
        _notes = notes;
        _audio = audio;
        _view = Segments([(true, "Summary"), (false, "Transcript")], true);
        _view.Changed += _ => Display();
        Body.Children.Add(_view);
        _text.Margin = new Thickness(0, 12, 0, 0);
        _text.VerticalContentAlignment = VerticalAlignment.Top;
        _text.Padding = new Thickness(10, 8, 10, 8);
        Body.Children.Add(_text);
        Body.Children.Add(_model);
        Body.Children.Add(_summarize);
        _summarize.Click += async (_, _) => await SummarizeAsync();
        _model.Ready += Display;
        _model.Switched += Display;
        ResetButton.Content = L.T("Open Folder");
        ResetButton.Visibility = Visibility.Visible;
        ResetButton.Click += (_, _) => Process.Start("explorer.exe", "/select,\"" + _notes + "\"");
        PrimaryButton.Content = L.T("Copy");
        PrimaryButton.Click += (_, _) =>
        {
            if (SelectedText.TrySet(_text.Text))
                FooterNote = "Copied";
        };
        FooterNote = System.IO.Path.GetFileName(notes);
        Read();
    }

    private void Read()
    {
        var all = File.Exists(_notes) ? File.ReadAllText(_notes) : "";
        var lines = all.Replace("\r\n", "\n").Split('\n');
        int transcriptAt = Array.FindIndex(lines, l => l.StartsWith("## ") && (l.Contains("Transcript") || l.Contains("النص الكامل")));
        _summary = transcriptAt <= 0 ? "" : string.Join(Environment.NewLine, lines.Skip(3).Take(transcriptAt - 3)).Trim();
        _transcript = transcriptAt < 0 ? all : string.Join(Environment.NewLine, lines.Skip(transcriptAt + 2)).Trim();
        Display();
    }

    private void Display()
    {
        bool summary = _view.Value;
        _text.Text = summary ? (_summary.Length > 0 ? Readable(_summary) : L.T("No summary yet. With the AI model on this PC, DropWheel writes the summary, the decisions and the action items.")) : _transcript;
        _text.FlowDirection = KeyboardLayout.LooksArabic(_text.Text) ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        bool missing = summary && _summary.Length == 0;
        _model.Wanted = summary;
        _summarize.Visibility = missing && AskPetal.ModelReady ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>The summary without its Markdown marks: headings as plain lines, bullets as dots.</summary>
    internal static string Readable(string markdown) => string.Join(Environment.NewLine, markdown.Replace("\r\n", "\n").Split('\n').Select(line =>
        line.StartsWith("## ", StringComparison.Ordinal) ? line[3..].Trim()
        : line.StartsWith("- ", StringComparison.Ordinal) ? "•  " + line[2..].Trim()
        : line));

    private async Task SummarizeAsync()
    {
        _summarize.IsEnabled = false;
        _text.Text = Petal.App.Settings.AiSetup.Writing("Writing the summary on this PC…");
        try
        {
            _summary = await MeetingNotes.SummarizeAsync(_transcript);
            var all = File.ReadAllText(_notes);
            var marker = all.IndexOf("## ", StringComparison.Ordinal);
            if (marker > 0)
                File.WriteAllText(_notes, all[..marker] + _summary.Trim() + Environment.NewLine + Environment.NewLine + all[marker..]);
            Read();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            Log.Error(ex, "Summarising a meeting");
            _text.Text = L.T("It didn't work. The activity log has the details.");
        }
        finally
        {
            _summarize.IsEnabled = true;
        }
    }
}
