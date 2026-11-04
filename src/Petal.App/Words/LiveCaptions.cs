using System.Threading.Channels;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
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
/// Live captions for anything: a video, a call, a lesson, from the PC's sound or the microphone, all on this PC.
/// The words come as they are said: Whisper writes the talk down again about every half second and the words
/// two passes agree on are final (<see cref="StreamingTranscriber"/>), so nothing waits for a pause. Translation
/// runs in a lane of its own: each finished sentence properly, the sentence being said quickly in between, so
/// a slow translation never holds the words up. When the PC can't keep up, a quicker speech model takes over.
/// </summary>
internal static class LiveCaptions
{
    private static CaptionWindow? _window;
    private static IWaveIn? _input;
    private static SpeechResampler? _resampler;
    private static StreamingTranscriber? _engine;
    private static CancellationTokenSource? _engineStop;
    private static ModelInfo? _model;
    private static CancellationTokenSource? _stop;
    // Finished sentences waiting for translation, with their number (the first is 1).
    private static Channel<(int Number, StreamSentence Sentence)>? _sentences;
    private static readonly object Gate = new();

    // The translation lane's view of the talk: the sentence being said (written by the engine, read by the lane).
    private static volatile StreamState? _live;
    private static int _finished;
    private static int _partialWanted;

    public static bool IsOn => _window is not null;

    /// <summary>For tests: a sound file played into the captions in real time instead of the PC's sound.</summary>
    internal static string? SoundFile { get; set; }

    internal static CaptionWindow? Window => _window;

    private static CancellationTokenSource? _fileFeed;

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
        Settle(settings);
        _asked.Clear();
        _window = new CaptionWindow();
        _window.Show();
        _stop = new CancellationTokenSource();
        _sentences = Channel.CreateUnbounded<(int, StreamSentence)>(new UnboundedChannelOptions { SingleReader = true });
        _finished = 0;
        _live = null;
        var stop = _stop.Token;
        var dispatcher = Application.Current.Dispatcher;
        _ = Task.Run(async () =>
        {
            // Loading a model takes a moment (longer for the bigger ones): the window is up meanwhile.
            if (!await BeginAsync(model, settings.CaptionsLanguage, stop))
            {
                _ = dispatcher.BeginInvoke(() =>
                {
                    Stop();
                    Hud.HudCard.ShowAt(Hud.HudCard.CornerPoint(), "", "").Fail(L.T("Live Captions"), L.T("The speech model couldn't be loaded."));
                });
                return;
            }
            bool listening = await dispatcher.InvokeAsync(() => IsOn && Listen(SettingsStore.Current.CaptionsSource == "mic"));
            if (!listening)
            {
                _ = dispatcher.BeginInvoke(() =>
                {
                    if (!IsOn)
                        return;
                    Stop();
                    Hud.HudCard.ShowAt(Hud.HudCard.CornerPoint(), "", "").Fail(L.T("Live Captions"), L.T("The sound couldn't be listened to."));
                });
                return;
            }
            _ = Task.Run(() => TranslateAsync(stop), stop);
            await KeepUpAsync(stop);
        }, stop);
    }

    /// <summary>Starts the speech engine with a model, warmed up first; false when it couldn't be loaded.</summary>
    private static async Task<bool> BeginAsync(ModelInfo model, string language, CancellationToken stop)
    {
        StreamingTranscriber engine;
        try
        {
            engine = new StreamingTranscriber(ModelStore.Find(model)!, language, translatingModelPath: SpeechLanguages.TranslatingModel());
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Error(ex, "Starting live captions");
            return false;
        }
        try
        {
            await engine.WarmUpAsync(stop);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            Log.Error(ex, "Warming up live captions");
        }
        catch (OperationCanceledException)
        {
        }
        // Turned off while the model was loading.
        if (stop.IsCancellationRequested)
        {
            engine.Dispose();
            return true;
        }
        var dispatcher = Application.Current.Dispatcher;
        // Only the engine in use speaks (one being replaced may still finish a pass).
        engine.Changed += state =>
        {
            if (_engine != engine)
                return;
            _live = state;
            if (Interlocked.Exchange(ref _partialWanted, 1) == 0)
                _wake.Release();
            _ = dispatcher.BeginInvoke(() => _window?.ShowTalk(state));
        };
        engine.Sentence += sentence =>
        {
            if (_engine != engine)
                return;
            _sentences?.Writer.TryWrite((Interlocked.Increment(ref _finished), sentence));
            _wake.Release();
        };
        engine.Failed += ex => Log.Error(ex, "Live captions");
        var own = CancellationTokenSource.CreateLinkedTokenSource(stop);
        CancellationTokenSource? oldStop;
        lock (Gate)
        {
            oldStop = _engineStop;
            _engine = engine;
            _engineStop = own;
            _model = model;
        }
        oldStop?.Cancel();
        _ = Task.Run(async () =>
        {
            try
            {
                await engine.RunAsync(own.Token);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Error(ex, "Live captions");
            }
            finally
            {
                // Its last pass is over: the model can go (Dispose waits for a translation under way).
                engine.Dispose();
                own.Dispose();
            }
        }, CancellationToken.None);
        return true;
    }

    /// <summary>
    /// Watches how quickly the passes go. When the speech model is too heavy for this PC (each pass takes most of
    /// the time it covers, so the words fall behind), a smaller model that is here takes over.
    /// </summary>
    private static async Task KeepUpAsync(CancellationToken stop)
    {
        double average = 0;
        int slow = 0;
        while (!stop.IsCancellationRequested)
        {
            try { await Task.Delay(1000, stop); } catch (OperationCanceledException) { return; }
            StreamingTranscriber? engine;
            ModelInfo? model;
            lock (Gate)
            {
                engine = _engine;
                model = _model;
            }
            if (engine is null || model is null || engine.LastPass <= 0)
                continue;
            // Passes run one after another, so a pass's length is how far the words are behind the talk.
            average = average == 0 ? engine.LastPass : average * 0.7 + engine.LastPass * 0.3;
            slow = average > 1.6 ? slow + 1 : 0;
            if (slow < 6)
                continue;
            int at = Array.IndexOf(Listener.Models, model);
            var quicker = Listener.Models.Take(Math.Max(0, at)).LastOrDefault(ModelStore.IsAvailable);
            if (quicker is null)
                continue;
            Log.Info($"Live captions: {model.Id} is too slow here ({average:0.00}), switching to {quicker.Id}");
            if (await BeginAsync(quicker, SettingsStore.Current.CaptionsLanguage, stop))
                _ = Application.Current.Dispatcher.BeginInvoke(() => _window?.Note(L.T("A quicker speech model took over, so the captions keep up.")));
            average = 0;
            slow = 0;
        }
    }

    private static readonly SemaphoreSlim _wake = new(0);

    /// <summary>
    /// The translation lane: finished sentences first, in order, each translated properly; when none waits, the
    /// sentence being said, quickly, so its translation grows along with it.
    /// </summary>
    private static async Task TranslateAsync(CancellationToken stop)
    {
        var dispatcher = Application.Current.Dispatcher;
        var sentences = _sentences!;
        string lastPartial = "";
        while (!stop.IsCancellationRequested)
        {
            try { await _wake.WaitAsync(stop); } catch (OperationCanceledException) { return; }
            try
            {
                StreamingTranscriber? engine;
                lock (Gate)
                    engine = _engine;
                if (engine is null)
                    continue;
                string? target = Target(SettingsStore.Current.CaptionsShow);
                while (sentences.Reader.TryRead(out var item))
                {
                    var (finished, sentence) = item;
                    var translated = target is null ? null : await TranslateAsync(engine, target, sentence.Text, sentence.Language, sentence, stop);
                    _ = dispatcher.BeginInvoke(() => _window?.ShowTranslation(translated, final: true, finished));
                    lastPartial = "";
                }
                if (Interlocked.Exchange(ref _partialWanted, 0) == 0 || target is null || _live is not { } live)
                    continue;
                var talk = (live.Current + " " + live.Draft).Trim();
                // A few words are enough to start; a translation of the same words isn't made twice.
                if (talk.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length < 3 || talk == lastPartial)
                    continue;
                // The sentence being said is the one after those finished so far.
                int number = Volatile.Read(ref _finished) + 1;
                var partial = await TranslateAsync(engine, target, talk, live.Language, null, stop);
                lastPartial = talk;
                if (partial is not null)
                    _ = dispatcher.BeginInvoke(() => _window?.ShowTranslation(partial, final: false, number));
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
            {
                Log.Error(ex, "Translating live captions");
            }
        }
    }

    // The translations asked for already this time, so a missing model is offered once.
    private static readonly HashSet<(string, string)> _asked = [];

    /// <summary>
    /// Words in the language wanted, or null when they are in it already or nothing can translate them yet. The chosen
    /// cloud model takes finished sentences only: the sentence being said changes twice a second, and each pass would be
    /// billed. On this PC the sentence being said is translated too, following only the likeliest word. A language no model reads is put into English by Whisper from the sentence's own
    /// sound, and from there into the language wanted.
    /// </summary>
    private static async Task<string?> TranslateAsync(StreamingTranscriber engine, string target, string text, string language, StreamSentence? sentence, CancellationToken stop)
    {
        string from = language is "" or "auto" ? Petal.Core.Text.LanguageGuess.Of(text) ?? "en" : language;
        if (from == target)
            return null;
        bool known = Translator.Languages.Any(l => l.Code == from);
        if (Petal.Recipes.Cloud.CloudAi.CanTranslate && sentence is not null)
            return (await Translator.TranslateAsync([text], from, target, null, stop))[0];
        if (known)
        {
            var models = Translator.ModelsFor(from, target);
            if (models.All(ModelStore.IsAvailable))
                return await Task.Run(() => Translator.Line(text, from, target, quick: sentence is null), stop);
            if (sentence is not null && _asked.Add((from, target)))
                _ = Application.Current.Dispatcher.BeginInvoke(() => ModelDownloadWindow.Show("Translation Model", "The captions are translated by a model on this PC, downloaded once. Until it is here, they show what was said.", models));
        }
        if (sentence is null)
            return null;
        string? english = from == "en" ? text : await engine.ToEnglishAsync(sentence, stop);
        if (string.IsNullOrWhiteSpace(english))
            return null;
        if (target == "en")
            return english;
        var through = Translator.ModelsFor("en", target);
        if (!through.All(ModelStore.IsAvailable))
            return null;
        return await Task.Run(() => Translator.Line(english, "en", target), stop);
    }

    /// <summary>Settings from before there was a choice of language: "In Arabic" and "In English" become the language chosen.</summary>
    internal static void Settle(AppSettings settings)
    {
        if (settings.CaptionsShow is "arabic" or "english")
        {
            settings.CaptionsInto = settings.CaptionsShow == "arabic" ? "ar" : "en";
            settings.CaptionsShow = "translated";
        }
    }

    /// <summary>The language the captions are translated into, or null for what was said only.</summary>
    internal static string? Target(string show) => show switch
    {
        "original" => null,
        "english" => "en",
        "arabic" => "ar",
        _ => SettingsStore.Current.CaptionsInto is { Length: > 0 } chosen ? chosen : LanguageChoice.Interface,
    };

    private static bool Listen(bool microphone)
    {
        if (SoundFile is { } file)
            return ListenToFile(file);
        try
        {
            IWaveIn input = microphone
                ? new WaveInEvent { WaveFormat = new WaveFormat(16000, 16, 1), BufferMilliseconds = 100 }
                : new WasapiLoopbackCapture();
            var format = input.WaveFormat;
            var resampler = new SpeechResampler(format.SampleRate, format.Channels);
            lock (Gate)
                _resampler = resampler;
            input.DataAvailable += (_, e) =>
            {
                var floats = ToFloats(e.Buffer, e.BytesRecorded, format);
                lock (Gate)
                {
                    if (_resampler != resampler || _engine is null)
                        return;
                    _engine.Add(resampler.Convert(floats));
                }
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

    /// <summary>A sound file fed in as fast as it would play, a tenth of a second at a time.</summary>
    private static bool ListenToFile(string file)
    {
        var feed = new CancellationTokenSource();
        _fileFeed = feed;
        _ = Task.Run(async () =>
        {
            using var reader = new WaveFileReader(file);
            var format = reader.WaveFormat;
            var resampler = new SpeechResampler(format.SampleRate, format.Channels);
            var buffer = new byte[format.AverageBytesPerSecond / 10 / format.BlockAlign * format.BlockAlign];
            var clock = System.Diagnostics.Stopwatch.StartNew();
            double played = 0;
            int read;
            while (!feed.IsCancellationRequested && (read = reader.Read(buffer, 0, buffer.Length)) > 0)
            {
                var samples = resampler.Convert(ToFloats(buffer, read, format));
                lock (Gate)
                    _engine?.Add(samples);
                played += (double)read / format.AverageBytesPerSecond;
                double wait = played - clock.Elapsed.TotalSeconds;
                if (wait > 0)
                    try { await Task.Delay(TimeSpan.FromSeconds(wait), feed.Token); } catch (OperationCanceledException) { }
            }
        });
        return true;
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

    public static void Stop()
    {
        _stop?.Cancel();
        _stop = null;
        StopListening();
        _sentences?.Writer.TryComplete();
        _sentences = null;
        // Cancelling the engine's run lets its model go once the pass under way is over.
        lock (Gate)
        {
            _engine = null;
            _engineStop = null;
            _model = null;
        }
        _window?.Close();
        _window = null;
    }

    private static void StopListening()
    {
        _fileFeed?.Cancel();
        _fileFeed = null;
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
            _resampler = null;
    }

    /// <summary>Switches between the PC's sound and the microphone while the captions run.</summary>
    public static void SwitchSource()
    {
        var settings = SettingsStore.Current;
        settings.CaptionsSource = settings.CaptionsSource == "mic" ? "system" : "mic";
        SettingsStore.Save();
        if (!IsOn)
            return;
        StopListening();
        Listen(settings.CaptionsSource == "mic");
        _window?.Refresh();
    }

    /// <summary>The translation, what was said, or both: the next one.</summary>
    public static void NextShow()
    {
        var settings = SettingsStore.Current;
        Settle(settings);
        string[] order = ["both", "translated", "original"];
        settings.CaptionsShow = order[(Array.IndexOf(order, settings.CaptionsShow) + 1) % order.Length];
        SettingsStore.Save();
        _window?.ClearTranslation();
        _window?.Refresh();
        if (Interlocked.Exchange(ref _partialWanted, 1) == 0)
            _wake.Release();
    }
}

/// <summary>
/// The caption bar: what was said rolls as the talk goes on, the final words bright and the newest (which may
/// still change) dimmer. A translation has two lines of its own, because many languages can't be put right until
/// their sentence ends: the first holds the last finished sentence, translated properly; the second, under it, the
/// sentence being said, translated as it comes. When that sentence ends, it moves up to the first line.
/// What was said is small above them.
/// </summary>
internal sealed class CaptionWindow : Window
{
    private readonly TextBlock _main, _live, _small, _status;
    private readonly StackPanel _buttons, _text;
    private readonly StripButton _source, _show;
    private StreamState? _talk;
    // The translation: the last finished sentence's and the sentence being said.
    private string _sentence = "";
    private string _partial = "";
    // The number of the sentence the partial translation is of, and of the latest finished one translated.
    private int _partialFor, _shownFinal;

    private static readonly Brush Bright = Brushes.White;
    private static readonly Brush Dim = Frozen(Color.FromArgb(0x9A, 0xFF, 0xFF, 0xFF));
    private static readonly Brush SmallBright = Frozen(Color.FromArgb(0xC8, 0xFF, 0xFF, 0xFF));
    private static readonly Brush SmallDim = Frozen(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF));

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

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
        _small = new TextBlock { FontSize = 16, Foreground = SmallBright, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 0, 4), TextAlignment = TextAlignment.Center };
        _main = new TextBlock { FontSize = 27, FontWeight = FontWeights.SemiBold, Foreground = Bright, TextWrapping = TextWrapping.Wrap, FontFamily = UiFonts.Display, TextAlignment = TextAlignment.Center };
        _live = new TextBlock { FontSize = 23, Foreground = Dim, TextWrapping = TextWrapping.Wrap, FontFamily = UiFonts.Display, TextAlignment = TextAlignment.Center, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 4, 0, 0) };
        _status = new TextBlock { FontSize = 14, Foreground = new SolidColorBrush(Color.FromArgb(0xA0, 0xFF, 0xFF, 0xFF)), TextWrapping = TextWrapping.Wrap };
        _source = new StripButton("volume", "The PC's sound or the microphone", LiveCaptions.SwitchSource, null, 28);
        _show = new StripButton("languages", "The translation, the original, or both", LiveCaptions.NextShow, null, 28);
        _buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Opacity = 0, Margin = new Thickness(0, -6, -10, 0) };
        _buttons.Children.Add(_source);
        _buttons.Children.Add(_show);
        _buttons.Children.Add(new StripButton("x", "Turn off captions", LiveCaptions.Stop, null, 28));
        _text = new StackPanel { Children = { _status, _small, _main, _live }, Margin = new Thickness(0, 0, 90, 0) };
        var grid = new Grid { Children = { _text, _buttons } };
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
        ScreenUi.DragFromBackground(this, card);
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
            Render();
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
        if (_talk is null)
            _status.Text = L.T(settings.CaptionsSource == "mic" ? "Listening to the microphone…" : "Listening to the PC's sound…");
        Render();
    }

    /// <summary>A short line above the captions for a few seconds.</summary>
    public void Note(string text)
    {
        _status.Text = text;
        _status.Visibility = Visibility.Visible;
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (_status.Text == text && _talk is not null)
                _status.Visibility = Visibility.Collapsed;
        };
        timer.Start();
    }

    /// <summary>The talk as heard now.</summary>
    public void ShowTalk(StreamState talk)
    {
        if (_talk is null)
            _status.Visibility = Visibility.Collapsed;
        _talk = talk;
        Render();
    }

    /// <summary>
    /// A translation of sentence number <paramref name="number"/>: finished (final), or still being said. The
    /// translation of a sentence still being said that comes after that sentence was finished is let go.
    /// </summary>
    public void ShowTranslation(string? text, bool final, int number)
    {
        if (final)
        {
            _shownFinal = Math.Max(_shownFinal, number);
            if (!string.IsNullOrWhiteSpace(text))
                _sentence = text.Trim();
            if (_partialFor <= number)
                _partial = "";
        }
        else if (number > _shownFinal)
        {
            _partial = text?.Trim() ?? "";
            _partialFor = number;
        }
        Render();
    }

    public void ClearTranslation()
    {
        _sentence = "";
        _partial = "";
    }

    /// <summary>Fills the lines from what was heard and its translation, as the settings want them shown.</summary>
    private void Render()
    {
        if (_talk is not { } talk)
            return;
        var said = (Join(talk.Previous, talk.Current), talk.Draft);
        bool translating = LiveCaptions.Target(SettingsStore.Current.CaptionsShow) is not null;
        bool hasTranslation = translating && (_sentence.Length > 0 || _partial.Length > 0);
        bool both = SettingsStore.Current.CaptionsShow == "both";
        if (hasTranslation)
        {
            // Both lines keep their room even while one is empty, so the bar doesn't jump at each sentence.
            Fill(_main, (_sentence, ""), Bright, Dim, 3);
            Fill(_live, (_partial, ""), Dim, Dim, 2);
            _main.MinHeight = _main.FontSize * _main.FontFamily.LineSpacing;
            _live.MinHeight = _live.FontSize * _live.FontFamily.LineSpacing;
            _live.Visibility = Visibility.Visible;
            _small.Visibility = both ? Visibility.Visible : Visibility.Collapsed;
            if (both)
                Fill(_small, said, SmallBright, SmallDim, 2);
        }
        else
        {
            _small.Visibility = Visibility.Collapsed;
            _live.Visibility = Visibility.Collapsed;
            _main.MinHeight = 0;
            Fill(_main, said, Bright, Dim, 2);
        }
    }

    /// <summary>What the lines show now (for tests): what was said, the first line and the second.</summary>
    internal (string Small, string Main, string Live) Shown =>
        (_small.Visibility == Visibility.Visible ? new TextRange(_small.ContentStart, _small.ContentEnd).Text : "",
            new TextRange(_main.ContentStart, _main.ContentEnd).Text,
            _live.Visibility == Visibility.Visible ? new TextRange(_live.ContentStart, _live.ContentEnd).Text : "");

    private static string Join(string a, string b) => a.Length == 0 ? b : b.Length == 0 ? a : a + " " + b;

    public void Show(string original, string? translated, string? saying = null)
    {
        // For pictures of the window: a finished sentence and its translation, and the sentence being said.
        _talk = new StreamState(original, "", "", "", -1);
        _status.Visibility = Visibility.Collapsed;
        if (translated is not null)
            ShowTranslation(translated, final: true, 1);
        else
            Render();
        if (saying is not null)
            ShowTranslation(saying, final: false, 2);
    }

    /// <summary>
    /// The newest words that fit in a few lines: the final ones, then the draft dimmer. Older words go from the
    /// start, so the lines roll as the talk goes on and the newest are never cut off.
    /// </summary>
    private void Fill(TextBlock block, (string Final, string Draft) text, Brush bright, Brush dim, int lines)
    {
        var final = text.Final.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        var draft = text.Draft.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        bool arabic = KeyboardLayout.LooksArabic(text.Final + " " + text.Draft);
        block.FlowDirection = arabic ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        double width = _text.ActualWidth > 0 ? _text.ActualWidth : double.IsNaN(Width) ? 760 : Math.Max(300, Width - 134);
        double lineHeight = block.FontSize * block.FontFamily.LineSpacing;
        bool cut = false;
        while (true)
        {
            block.Inlines.Clear();
            if (cut)
                block.Inlines.Add(new Run("… ") { Foreground = dim });
            if (final.Count > 0)
                block.Inlines.Add(new Run(string.Join(' ', final)) { Foreground = bright });
            if (draft.Count > 0)
                block.Inlines.Add(new Run((final.Count > 0 ? " " : "") + string.Join(' ', draft)) { Foreground = dim });
            block.Measure(new Size(width, double.PositiveInfinity));
            if (block.DesiredSize.Height <= lineHeight * lines + 2 || final.Count + draft.Count <= 1)
                break;
            if (final.Count > 0)
                final.RemoveAt(0);
            else
                draft.RemoveAt(0);
            cut = true;
        }
    }
}

/// <summary>A small window that downloads a model a feature needs, with what it is for.</summary>
internal sealed class ModelDownloadWindow : ToolWindow
{
    private static ModelDownloadWindow? _open;

    /// <param name="over">A window that stays on top of everything (Circle Anything's still screen): this one goes over it.</param>
    /// <param name="ready">Called once the models are all here, or a key does their job.</param>
    /// <param name="ownKey">Opened at the place for a key, for someone who asked for that.</param>
    public static void Show(string title, string why, IReadOnlyList<ModelInfo> models, Window? over = null, Action? ready = null, bool ownKey = false)
    {
        if (_open is not null)
        {
            _open.Activate();
            return;
        }
        _open = new ModelDownloadWindow(title, why, models, ready, ownKey);
        _open.Closed += (_, _) => _open = null;
        _open.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        if (over is not null)
        {
            _open.Owner = over;
            _open.Topmost = true;
            _open.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        _open.Show();
    }

    /// <summary>The window with the place for a key open or closed, for checking by eye.</summary>
    internal static Window ForSnapshot(bool ownKey) => new ModelDownloadWindow("Circle Anything", "Answering on this PC needs the AI model, downloaded once.",
        [.. Petal.Recipes.AskPetal.Models.Select(m => m with { Size = m.Size + 1 })], null, ownKey);

    private ModelDownloadWindow(string title, string why, IReadOnlyList<ModelInfo> models, Action? ready, bool ownKey = false) : base(title, 420)
    {
        Body.Children.Add(Text(why, "Petal.Body"));
        var panel = new ModelPanel(models);
        Body.Children.Add(panel);
        if (ownKey)
            panel.ChooseKey();
        panel.Ready += () =>
        {
            FooterNote = "Ready";
            ready?.Invoke();
        };
        PrimaryButton.Content = L.T("Done");
        PrimaryButton.Click += (_, _) => Close();
    }
}
