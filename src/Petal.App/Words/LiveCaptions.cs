using System.Threading.Channels;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using NAudio.Wave;
using Petal.App.Localization;
using Petal.App.Screen;
using Petal.App.Settings;
using Petal.App.Tools;
using Petal.App.Tools.Parts;
using Petal.App.Visuals;
using Petal.Core.Audio;
using Petal.Core.Models;
using Petal.Core.Text;
using Petal.Engines.Media;
using Petal.Recipes.Translation;
using static Petal.App.Interop.NativeMethods;

namespace Petal.App.Words;

/// <summary>
/// Live captions for anything: a video, a call, a lesson, from the PC's sound or the microphone. Whisper
/// writes down each piece of speech as it ends, and the translation model puts it into Arabic, all on this
/// PC, without the internet. The captions float at the bottom of the screen; drag them anywhere.
/// </summary>
internal static class LiveCaptions
{
    private static CaptionWindow? _window;
    private static IWaveIn? _input;
    private static LiveTranscriber? _transcriber;
    private static Channel<float[]>? _pieces;
    private static Task? _worker;
    private static SpeechChunker? _chunker;
    private static SpeechResampler? _resampler;
    private static readonly object Gate = new();
    private static DateTime _lastSound;
    private static System.Threading.Timer? _flush;

    public static bool IsOn => _window is not null;

    public static void Toggle()
    {
        if (IsOn)
            Stop();
        else
            Start();
    }

    private static void Start()
    {
        if (Listener.ReadyModel() is not { } model)
        {
            DictationSetupWindow.ShowSingle();
            return;
        }
        var settings = SettingsStore.Current;
        if (NeedsArabic(settings.CaptionsShow) && !Translator.IsReady)
            ModelDownloadWindow.Show("Arabic Captions", "The captions are translated into Arabic by a translation model on this PC. Until it is here, they show what was said.", Translator.Models);
        try
        {
            _transcriber = new LiveTranscriber(ModelStore.Find(model)!, settings.CaptionsLanguage);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Error(ex, "Starting live captions");
            Hud.HudCard.ShowAt(Hud.HudCard.CornerPoint(), "", "").Fail(L.T("Live Captions"), L.T("The speech model couldn't be loaded."));
            return;
        }
        _pieces = Channel.CreateBounded<float[]>(new BoundedChannelOptions(3) { FullMode = BoundedChannelFullMode.DropOldest });
        _chunker = new SpeechChunker();
        _window = new CaptionWindow();
        _window.Show();
        if (!Listen(settings.CaptionsSource == "mic"))
        {
            Stop();
            Hud.HudCard.ShowAt(Hud.HudCard.CornerPoint(), "", "").Fail(L.T("Live Captions"), L.T("The sound couldn't be listened to."));
            return;
        }
        var dispatcher = Application.Current.Dispatcher;
        var pieces = _pieces;
        var transcriber = _transcriber;
        _worker = Task.Run(async () =>
        {
            await foreach (var piece in pieces.Reader.ReadAllAsync())
            {
                try
                {
                    var (original, translated) = await CaptionAsync(transcriber, piece, SettingsStore.Current.CaptionsShow);
                    if (original.Length > 0)
                        _ = dispatcher.BeginInvoke(() => _window?.Show(original, translated));
                }
                catch (Exception ex) when (ex is not OutOfMemoryException and not ObjectDisposedException)
                {
                    Log.Error(ex, "Live captions");
                }
            }
        });
        // The PC's sound sends nothing while it is silent: a piece waiting then is finished by the clock.
        _flush = new System.Threading.Timer(_ =>
        {
            float[]? rest = null;
            lock (Gate)
            {
                if (_chunker is not null && DateTime.UtcNow - _lastSound > TimeSpan.FromMilliseconds(700))
                    rest = _chunker.Flush();
            }
            if (rest is not null)
                _pieces?.Writer.TryWrite(rest);
        }, null, 500, 500);
    }

    private static bool NeedsArabic(string show) => show is "arabic" or "both";

    private static bool Listen(bool microphone)
    {
        try
        {
            IWaveIn input = microphone
                ? new WaveInEvent { WaveFormat = new WaveFormat(16000, 16, 1), BufferMilliseconds = 100 }
                : new WasapiLoopbackCapture();
            var format = input.WaveFormat;
            _resampler = new SpeechResampler(format.SampleRate, format.Channels);
            input.DataAvailable += (_, e) =>
            {
                var floats = ToFloats(e.Buffer, e.BytesRecorded, format);
                IReadOnlyList<float[]> done;
                lock (Gate)
                {
                    if (_chunker is null || _resampler is null)
                        return;
                    _lastSound = DateTime.UtcNow;
                    done = _chunker.Add(_resampler.Convert(floats));
                }
                foreach (var piece in done)
                    _pieces?.Writer.TryWrite(piece);
            };
            input.StartRecording();
            _input = input;
            return true;
        }
        catch (Exception ex) when (ex is NAudio.MmException or System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            Log.Error(ex, "Listening for live captions");
            return false;
        }
    }

    private static readonly Guid FloatFormat = new("00000003-0000-0010-8000-00aa00389b71");

    /// <summary>Sound card bytes as samples from -1 to 1, whether 16-bit, 24-bit, 32-bit or floating point.</summary>
    internal static float[] ToFloats(byte[] buffer, int count, WaveFormat format)
    {
        if (format.Encoding == WaveFormatEncoding.IeeeFloat || format is WaveFormatExtensible { SubFormat: var sub } && sub == FloatFormat)
        {
            var floats = new float[count / 4];
            Buffer.BlockCopy(buffer, 0, floats, 0, floats.Length * 4);
            return floats;
        }
        switch (format.BitsPerSample)
        {
            case 16:
            {
                var result = new float[count / 2];
                for (int i = 0; i < result.Length; i++)
                    result[i] = BitConverter.ToInt16(buffer, i * 2) / 32768f;
                return result;
            }
            case 24:
            {
                var result = new float[count / 3];
                for (int i = 0; i < result.Length; i++)
                    result[i] = ((buffer[i * 3] << 8 | buffer[i * 3 + 1] << 16 | buffer[i * 3 + 2] << 24) >> 8) / 8388608f;
                return result;
            }
            default:
            {
                var result = new float[count / 4];
                for (int i = 0; i < result.Length; i++)
                    result[i] = BitConverter.ToInt32(buffer, i * 4) / 2147483648f;
                return result;
            }
        }
    }

    /// <summary>A piece of speech as shown: what was said, and its translation when one is wanted.</summary>
    private static async Task<(string Original, string? Translated)> CaptionAsync(LiveTranscriber transcriber, float[] piece, string show)
    {
        var heard = await transcriber.TranscribeAsync(piece);
        if (heard.Text.Length == 0)
            return ("", null);
        switch (show)
        {
            case "original":
                return (heard.Text, null);
            case "english":
                return heard.Language == "en" ? (heard.Text, null) : (heard.Text, (await transcriber.TranscribeAsync(piece, intoEnglish: true)).Text);
            default:
            {
                string? arabic = null;
                if (heard.Language == "ar" || KeyboardLayout.LooksArabic(heard.Text))
                    arabic = null;
                else if (Translator.IsReady)
                {
                    // English goes straight into Arabic; other languages through Whisper's English first.
                    var english = heard.Language == "en" ? heard.Text : (await transcriber.TranscribeAsync(piece, intoEnglish: true)).Text;
                    arabic = english.Length == 0 ? null : string.Join(" ", Translator.ToArabic([english]));
                }
                return show == "arabic" && arabic is not null ? (arabic, null) : (heard.Text, arabic);
            }
        }
    }

    public static void Stop()
    {
        _flush?.Dispose();
        _flush = null;
        try
        {
            _input?.StopRecording();
            _input?.Dispose();
        }
        catch (Exception ex) when (ex is NAudio.MmException or System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
        }
        _input = null;
        lock (Gate)
        {
            _chunker = null;
            _resampler = null;
        }
        _pieces?.Writer.TryComplete();
        var worker = _worker;
        var transcriber = _transcriber;
        _ = Task.Run(async () =>
        {
            if (worker is not null)
                await Task.WhenAny(worker, Task.Delay(15000));
            transcriber?.Dispose();
        });
        _worker = null;
        _transcriber = null;
        _pieces = null;
        _window?.Close();
        _window = null;
    }

    /// <summary>Switches between the PC's sound and the microphone while the captions run.</summary>
    public static void SwitchSource()
    {
        var settings = SettingsStore.Current;
        settings.CaptionsSource = settings.CaptionsSource == "mic" ? "system" : "mic";
        SettingsStore.Save();
        if (!IsOn)
            return;
        try
        {
            _input?.StopRecording();
            _input?.Dispose();
        }
        catch (Exception ex) when (ex is NAudio.MmException or System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
        }
        lock (Gate)
            _chunker = new SpeechChunker();
        Listen(settings.CaptionsSource == "mic");
        _window?.Refresh();
    }

    /// <summary>Original, Arabic, English, or both: the next one.</summary>
    public static void NextShow()
    {
        var settings = SettingsStore.Current;
        string[] order = ["both", "arabic", "original", "english"];
        settings.CaptionsShow = order[(Array.IndexOf(order, settings.CaptionsShow) + 1) % order.Length];
        SettingsStore.Save();
        if (NeedsArabic(settings.CaptionsShow) && !Translator.IsReady)
            ModelDownloadWindow.Show("Arabic Captions", "The captions are translated into Arabic by a translation model on this PC. Until it is here, they show what was said.", Translator.Models);
        _window?.Refresh();
    }
}

/// <summary>The caption bar: the latest words large, the original small above them when both are shown.</summary>
internal sealed class CaptionWindow : Window
{
    private readonly TextBlock _main, _small, _status;
    private readonly StackPanel _buttons;
    private readonly StripButton _source, _show;

    public CaptionWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        SizeToContent = SizeToContent.Height;
        Title = L.T("Live Captions");
        L.Apply(this);
        _small = new TextBlock { FontSize = 16, Foreground = new SolidColorBrush(Color.FromArgb(0xB8, 0xFF, 0xFF, 0xFF)), TextWrapping = TextWrapping.Wrap, MaxHeight = 46, TextTrimming = TextTrimming.CharacterEllipsis, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 0, 4) };
        _main = new TextBlock { FontSize = 27, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap, MaxHeight = 80, TextTrimming = TextTrimming.CharacterEllipsis, FontFamily = UiFonts.Display };
        _status = new TextBlock { FontSize = 14, Foreground = new SolidColorBrush(Color.FromArgb(0xA0, 0xFF, 0xFF, 0xFF)) };
        _source = new StripButton("volume", "The PC's sound or the microphone", LiveCaptions.SwitchSource, null, 28);
        _show = new StripButton("languages", "Arabic, the original, English, or both", LiveCaptions.NextShow, null, 28);
        _buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Opacity = 0, Margin = new Thickness(0, -6, -10, 0) };
        _buttons.Children.Add(_source);
        _buttons.Children.Add(_show);
        _buttons.Children.Add(new StripButton("x", "Turn off captions", LiveCaptions.Stop, null, 28));
        var text = new StackPanel { Children = { _status, _small, _main }, Margin = new Thickness(0, 0, 90, 0) };
        var grid = new Grid { Children = { text, _buttons } };
        var card = new Border
        {
            CornerRadius = new CornerRadius(16),
            Background = new SolidColorBrush(Color.FromArgb(0xD8, 0x12, 0x12, 0x16)),
            Padding = new Thickness(22, 12, 22, 14),
            Margin = new Thickness(10),
            Child = grid,
            Cursor = Cursors.SizeAll,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, ShadowDepth = 3, Opacity = 0.4 },
        };
        card.MouseLeftButtonDown += (_, e) =>
        {
            if (e.OriginalSource is not StripButton)
                DragMove();
        };
        MouseEnter += (_, _) => _buttons.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(120)));
        MouseLeave += (_, _) => _buttons.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(250)));
        Content = card;
        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            long ex = (long)GetWindowLongPtr(handle, GWL_EXSTYLE) | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
            SetWindowLongPtr(handle, GWL_EXSTYLE, new IntPtr(ex));
        };
        Loaded += (_, _) =>
        {
            var monitor = ScreenNative.UnderPointer();
            double s = monitor.Scale;
            Width = Math.Min(1000, monitor.Work.Width / s * 0.7);
            UpdateLayout();
            Left = (monitor.Work.Left + monitor.Work.Width / 2.0) / s - Width / 2;
            Top = monitor.Work.Bottom / s - ActualHeight - 40;
        };
        SizeChanged += (_, e) =>
        {
            // Grows upwards from where it sits, so new lines don't push it off the bottom.
            if (e.HeightChanged && e.PreviousSize.Height > 0)
                Top -= e.NewSize.Height - e.PreviousSize.Height;
        };
        Refresh();
    }

    public void Refresh()
    {
        var settings = SettingsStore.Current;
        _source.Child = Icons.Create(settings.CaptionsSource == "mic" ? "mic" : "volume", 15, Brushes.White, 1.9);
        if (_main.Text.Length == 0)
            _status.Text = L.T(settings.CaptionsSource == "mic" ? "Listening to the microphone…" : "Listening to the PC's sound…");
    }

    public void Show(string original, string? translated)
    {
        _status.Visibility = Visibility.Collapsed;
        bool both = translated is not null;
        _small.Visibility = both ? Visibility.Visible : Visibility.Collapsed;
        _small.Text = both ? original : "";
        _main.Text = translated ?? original;
        _small.FlowDirection = KeyboardLayout.LooksArabic(_small.Text) ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        _main.FlowDirection = KeyboardLayout.LooksArabic(_main.Text) ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        _main.TextAlignment = _small.TextAlignment = TextAlignment.Center;
        _main.BeginAnimation(OpacityProperty, new DoubleAnimation(0.25, 1, TimeSpan.FromMilliseconds(180)));
    }
}

/// <summary>A small window that downloads a model a feature needs, with what it is for.</summary>
internal sealed class ModelDownloadWindow : ToolWindow
{
    private static ModelDownloadWindow? _open;

    public static void Show(string title, string why, IReadOnlyList<ModelInfo> models)
    {
        if (_open is not null)
        {
            _open.Activate();
            return;
        }
        _open = new ModelDownloadWindow(title, why, models);
        _open.Closed += (_, _) => _open = null;
        _open.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        _open.Show();
    }

    private ModelDownloadWindow(string title, string why, IReadOnlyList<ModelInfo> models) : base(title, 420)
    {
        Body.Children.Add(Text(why, "Petal.Body"));
        var panel = new ModelPanel(models);
        Body.Children.Add(panel);
        panel.Ready += () => FooterNote = "Ready";
        PrimaryButton.Content = L.T("Done");
        PrimaryButton.Click += (_, _) => Close();
    }
}
