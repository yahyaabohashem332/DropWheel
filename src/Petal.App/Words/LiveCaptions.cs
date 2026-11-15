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
/// The words come as they are said: Whisper writes the sentence being said down again about every half second and
/// what two readings agree on is settled (<see cref="StreamingTranscriber"/>), so nothing waits for a pause; a model
/// that tells speech from other sound keeps music and quiet away from it, and a quiet video is heard like a loud one.
/// Translation runs in a lane of its own (<see cref="LiveInterpreter"/>): the sentence being said word by word on the
/// second line, each finished sentence properly on the first, so a slow translation never holds the words up. When the
/// PC can't keep up, a quicker speech model takes over without a word lost.
/// </summary>
internal static class LiveCaptions
{
    private static CaptionWindow? _window;
    private static IWaveIn? _input;
    private static SpeechResampler? _resampler;
    private static StreamingTranscriber? _engine;
    private static LiveInterpreter? _interpreter;
    private static ModelInfo? _model;
    private static CancellationTokenSource? _stop;
    private static readonly object Gate = new();

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

    /// <summary>
    /// The speech model captions listen with. Where a graphics card runs it, the most accurate one here: measured on
    /// one, it keeps up with the talk and hears Arabic, Turkish, Persian, Urdu, Indonesian and French right where the
    /// smaller models get every third word wrong. On a processor alone, the model chosen for dictation.
    /// </summary>
    internal static ModelInfo? SpeechModel() =>
        GemmaTranslation.HasCard ? Listener.Models.Reverse().FirstOrDefault(ModelStore.IsAvailable) : Listener.ReadyModel();

    private static void Start()
    {
        if (SpeechModel() is not { } model)
        {
            DictationSetupWindow.ShowSingle();
            return;
        }
        var settings = SettingsStore.Current;
        Settle(settings);
        _window = new CaptionWindow();
        _window.Show();
        _stop = new CancellationTokenSource();
        var stop = _stop.Token;
        var dispatcher = Application.Current.Dispatcher;
        var translator = new LiveTranslator();
        translator.Missing += (from, to) => dispatcher.BeginInvoke(() => OfferTranslation(from, to));
        var interpreter = new LiveInterpreter(translator, () => Target(SettingsStore.Current.CaptionsShow));
        interpreter.Line += line => dispatcher.BeginInvoke(() => _window?.ShowLine(line));
        interpreter.Failed += ex => Log.Error(ex, "Translating live captions");
        _interpreter = interpreter;
        string spoken = settings.CaptionsLanguage;
        string? wanted = Target(settings.CaptionsShow);
        bool translating = wanted is not null && wanted != spoken;
        // Without a graphics card the small models made for the two languages translate, and share the processor
        // with the speech model: a few threads each get more done than all of them fighting over every core.
        bool small = translating && !GemmaTranslation.HasCard;
        if (small)
            SmallTranslation.Threads = Math.Clamp(Environment.ProcessorCount / 4, 2, 4);
        Offer(translating);
        // Their models are asked for now when the language spoken is known, and not at its first sentence.
        if (small && LiveTranslator.SmallWanted(spoken, wanted!, card: false) && !SmallTranslation.ReadyFor(spoken, wanted!) && !Petal.Recipes.Cloud.CloudAi.CanTranslate)
            OfferTranslation(spoken, wanted!);
        _ = Task.Run(async () =>
        {
            // The translation model loads beside the speech model, so the first sentence is translated in its own time
            // and not the model's loading too (measured on an 8 GB card: both fit with room to spare). The window is
            // up meanwhile, saying so.
            if (translating)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        // Without a card the large model stays out of the way: it is only what translates until a
                        // pair's small models are here, and loads when that is asked of it.
                        if (small)
                            SmallTranslation.Prepare(spoken, wanted!);
                        else
                            await GemmaTranslation.PrepareAsync(stop);
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        if (ex is not OperationCanceledException)
                            Log.Error(ex, "Preparing the captions' translation");
                    }
                }, stop);
            }
            StreamingTranscriber engine;
            try
            {
                // On a graphics card a pass is quick whatever it reads, and word times are aligned to the sound; on a
                // processor alone Whisper reads only as much as there is to hear, which is several times quicker there.
                engine = new StreamingTranscriber(ModelStore.Find(model)!, spoken, alignWords: GemmaTranslation.HasCard, ModelStore.Find(ModelCatalog.VoiceFinder), Transcriber.Languages);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Log.Error(ex, "Starting live captions");
                _ = dispatcher.BeginInvoke(() =>
                {
                    if (!stop.IsCancellationRequested)
                        Stop();
                    Hud.HudCard.ShowAt(Hud.HudCard.CornerPoint(), "", "").Fail(L.T("Live Captions"), L.T("The speech model couldn't be loaded."));
                });
                return;
            }
            try
            {
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
                // Turned off while the models were loading.
                if (stop.IsCancellationRequested)
                    return;
                engine.Changed += state =>
                {
                    interpreter.Heard(state);
                    _ = dispatcher.BeginInvoke(() => _window?.ShowTalk(state));
                };
                engine.Sentence += interpreter.Finished;
                engine.Failed += ex => Log.Error(ex, "Live captions");
                lock (Gate)
                {
                    _engine = engine;
                    _model = model;
                }
                bool listening = await dispatcher.InvokeAsync(() =>
                {
                    if (!IsOn || !Listen(SettingsStore.Current.CaptionsSource == "mic"))
                        return false;
                    _window?.Listening();
                    return true;
                });
                if (!listening)
                {
                    _ = dispatcher.BeginInvoke(() =>
                    {
                        if (stop.IsCancellationRequested)
                            return;
                        Stop();
                        Hud.HudCard.ShowAt(Hud.HudCard.CornerPoint(), "", "").Fail(L.T("Live Captions"), L.T("The sound couldn't be listened to."));
                    });
                    return;
                }
                _ = Task.Run(() => interpreter.RunAsync(stop), stop);
                _ = Task.Run(() => KeepUpAsync(engine, stop), stop);
                await engine.RunAsync(stop);
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
            {
                Log.Error(ex, "Live captions");
            }
            finally
            {
                lock (Gate)
                {
                    if (_engine == engine)
                        _engine = null;
                }
                // Its last pass is over: the model can go.
                engine.Dispose();
            }
        }, stop);
    }

    // What was offered already since DropWheel started, so nothing is offered twice.
    private static bool _offeredQuick, _offeredAccurate;

    /// <summary>
    /// Offers, once, what would make the captions better on this PC and isn't here: the quick translation model where
    /// only the large one is, or else the most accurate speech model where a graphics card can run it. (When no
    /// translation model is here at all, the first sentence asks for one.)
    /// </summary>
    private static void Offer(bool translating)
    {
        if (translating && GemmaTranslation.HasCard && !Petal.Recipes.Cloud.CloudAi.CanTranslate && GemmaTranslation.Ready && !ModelStore.IsAvailable(ModelCatalog.GemmaQuick))
        {
            if (!_offeredQuick)
                ModelDownloadWindow.Show("Translation Model", "A quicker model keeps the captions up with the speaker. Until it is here, the larger one translates them, a little behind.", GemmaTranslation.LiveModels);
            _offeredQuick = true;
            return;
        }
        if (translating && !Petal.Recipes.Cloud.CloudAi.CanTranslate && !GemmaTranslation.Ready)
            return;
        if (GemmaTranslation.HasCard && !ModelStore.IsAvailable(ModelCatalog.WhisperAccurate) && !_offeredAccurate)
        {
            _offeredAccurate = true;
            ModelDownloadWindow.Show("Speech Model", "This PC's graphics card runs the most accurate speech model without falling behind, and the captions hear every language far better with it. It is downloaded once.",
                [ModelCatalog.WhisperAccurate], ready: UseAccurate);
        }
    }

    // The pairs of languages whose small models were offered already since DropWheel started.
    private static readonly HashSet<string> OfferedPairs = [];

    /// <summary>
    /// Offers, once for each pair of languages, what translates them best on this PC and isn't here. Without a
    /// graphics card that is the small models made for the pair; with one, the model for every pair.
    /// </summary>
    private static void OfferTranslation(string from, string to)
    {
        // One offer at a time: this one waits for the next sentence that needs it.
        if (ModelDownloadWindow.IsOpen || !IsOn)
            return;
        if (!LiveTranslator.SmallWanted(from, to, GemmaTranslation.HasCard))
        {
            ModelDownloadWindow.Show("Translation Model", "The captions are translated by a model on this PC, downloaded once. Until it is here, they show what was said.", GemmaTranslation.LiveModels);
            return;
        }
        if (!OfferedPairs.Add(from + ">" + to))
            return;
        ModelDownloadWindow.Show("Translation Model", WhySmall(from, to, GemmaTranslation.Ready),
            SmallTranslation.ModelsFor(from, to), ready: () => Task.Run(() => SmallTranslation.Prepare(from, to)));
    }

    /// <summary>What the offer of a pair's small models says: why, and what translates until they are here.</summary>
    internal static string WhySmall(string from, string to, bool largeHere)
    {
        var (spoken, wanted) = (LanguageChoice.NameOf(from), LanguageChoice.NameOf(to));
        return largeHere
            ? L.F("Without a graphics card, a small model made for the two languages ({0} → {1}) keeps the captions up with the speaker. It is downloaded once. Until it is here, the larger model translates them, far behind.", spoken, wanted)
            : L.F("Without a graphics card, a small model made for the two languages ({0} → {1}) keeps the captions up with the speaker. It is downloaded once. Until it is here, the captions show what was said.", spoken, wanted);
    }

    /// <summary>The most accurate speech model has arrived: the captions go over to it as they run.</summary>
    private static void UseAccurate()
    {
        StreamingTranscriber? engine;
        var stop = _stop?.Token ?? CancellationToken.None;
        lock (Gate)
            engine = _engine;
        if (engine is null || ModelStore.Find(ModelCatalog.WhisperAccurate) is not { } path)
            return;
        _ = Task.Run(async () =>
        {
            try
            {
                await engine.UseModelAsync(path, stop);
                lock (Gate)
                    _model = ModelCatalog.WhisperAccurate;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                if (ex is not OperationCanceledException)
                    Log.Error(ex, "Changing the captions' speech model");
            }
        }, CancellationToken.None);
    }

    /// <summary>
    /// Watches how quickly the passes go. When the speech model is too heavy for this PC (each pass takes long, so
    /// the words fall behind), a smaller model that is here takes over: the sound held and the words so far stay.
    /// </summary>
    private static async Task KeepUpAsync(StreamingTranscriber engine, CancellationToken stop)
    {
        double average = 0;
        int slow = 0, quick = 0;
        // The model the captions started with, and how long the PC must keep up easily before it is tried again.
        ModelInfo? wanted = null;
        int patience = 30;
        while (!stop.IsCancellationRequested)
        {
            try { await Task.Delay(1000, stop); } catch (OperationCanceledException) { return; }
            ModelInfo? model;
            lock (Gate)
                model = _model;
            if (model is null || engine.LastPass <= 0)
                continue;
            wanted ??= model;
            // Passes run one after another, so a pass's length is how far the words are behind the talk.
            average = average == 0 ? engine.LastPass : average * 0.7 + engine.LastPass * 0.3;
            // What slowed the PC down may have been passing (a game loading, another program on the graphics card):
            // once the quicker model has had it easy for a while the better one is tried again, and each time that
            // doesn't hold, the wait doubles.
            quick = model != wanted && average < 0.6 ? quick + 1 : 0;
            if (quick >= patience)
            {
                int now = Array.IndexOf(Listener.Models, model), top = Array.IndexOf(Listener.Models, wanted);
                var better = Listener.Models.Skip(now + 1).Take(Math.Max(0, top - now)).FirstOrDefault(ModelStore.IsAvailable);
                quick = 0;
                patience *= 2;
                if (better is not null)
                {
                    try
                    {
                        await engine.UseModelAsync(ModelStore.Find(better)!, stop);
                        lock (Gate)
                            _model = better;
                        Log.Info($"Live captions: back to {better.Id}");
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
                    {
                        Log.Error(ex, "Changing the captions' speech model");
                    }
                    average = 0;
                    slow = 0;
                    continue;
                }
            }
            // Up to about two seconds behind still reads as live, and a more accurate model is worth that; beyond it, not.
            slow = average > 2.4 ? slow + 1 : 0;
            if (slow < 6)
                continue;
            int at = Array.IndexOf(Listener.Models, model);
            var quicker = Listener.Models.Take(Math.Max(0, at)).LastOrDefault(ModelStore.IsAvailable);
            if (quicker is null)
                continue;
            Log.Info($"Live captions: {model.Id} is too slow here ({average:0.00}), switching to {quicker.Id}");
            try
            {
                await engine.UseModelAsync(ModelStore.Find(quicker)!, stop);
                lock (Gate)
                    _model = quicker;
                _ = Application.Current.Dispatcher.BeginInvoke(() => _window?.Note(L.T("A quicker speech model took over, so the captions keep up.")));
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
            {
                Log.Error(ex, "Changing the captions' speech model");
            }
            average = 0;
            slow = 0;
        }
    }

    /// <summary>
    /// The model captions are best with. Where a graphics card runs it: one for every pair of languages, and the
    /// quickest of those. Without a card: the small models made for the two languages, once the language spoken is
    /// known (left to be told, it isn't until it is heard, and nothing can be offered before).
    /// </summary>
    /// <param name="card">Whether this PC has a graphics card for the large model; as found when not given.</param>
    internal static IReadOnlyList<ModelInfo> ModelsToOffer(string spoken, string into, bool? card = null)
    {
        if (spoken == into)
            return [];
        bool has = card ?? GemmaTranslation.HasCard;
        if (has)
            return GemmaTranslation.LiveModels;
        return LiveTranslator.SmallWanted(spoken, into, card: false) ? SmallTranslation.ModelsFor(spoken, into)
            : spoken is "" or "auto" ? [] : GemmaTranslation.LiveModels;
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
        // Cancelling ends the engine's run, and its model goes once the pass under way is over.
        _stop?.Cancel();
        _stop = null;
        StopListening();
        _interpreter = null;
        lock (Gate)
        {
            _engine = null;
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
        _interpreter?.Afresh();
        _window?.Refresh();
    }
}

/// <summary>
/// The caption bar. A translation has two lines, because many languages can't be put right until their sentence
/// ends: the first holds the last finished sentence, translated properly; the second, under it, the sentence being
/// said, translated word by word as it comes, its settled words brighter than its newest. The moment that sentence
/// ends it moves up to the first line, and its proper translation takes its place there. What was said is small above
/// them. Without a translation, what was said rolls as the talk goes on, the settled words bright and the newest dimmer.
/// </summary>
internal sealed class CaptionWindow : Window
{
    private readonly TextBlock _main, _live, _small, _status;
    private readonly StackPanel _buttons, _text;
    private readonly StripButton _source, _show;
    private StreamState? _talk;
    // The first line: the last finished sentence's translation, the number of that sentence, when it went up, and
    // the one before it while that hasn't been up long enough to read.
    private string _first = "", _earlier = "";
    private int _firstOf;
    private bool _firstProper;
    private TimeSpan _firstAt;
    // The second line: the translation of the sentence being said, how much of it no longer changes, and its number.
    private string _second = "";
    private int _steady, _secondOf;

    /// <summary>The time, for tests to set: how long a sentence has been up decides whether it stays beside the next.</summary>
    internal Func<TimeSpan> Clock { get; set; } = () => TimeSpan.FromMilliseconds(Environment.TickCount64);

    private static readonly Brush Bright = Brushes.White;
    private static readonly Brush Dim = Frozen(Color.FromArgb(0x9A, 0xFF, 0xFF, 0xFF));
    private static readonly Brush Steady = Frozen(Color.FromArgb(0xDC, 0xFF, 0xFF, 0xFF));
    private static readonly Brush Newest = Frozen(Color.FromArgb(0x86, 0xFF, 0xFF, 0xFF));
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
            _status.Text = L.T(!_listening ? "Preparing…" : settings.CaptionsSource == "mic" ? "Listening to the microphone…" : "Listening to the PC's sound…");
        Render();
    }

    // Whether the models are loaded and the sound is being listened to: until then the bar says it is getting ready.
    private bool _listening;

    /// <summary>The models are loaded and listening has begun.</summary>
    public void Listening()
    {
        _listening = true;
        Refresh();
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
        // The talk has gone on to the next sentence: what the second line holds is of a sentence that is over, and
        // moves up at once, whether or not the translator has got to it yet (it may still be busy with the one before).
        if (_second.Length > 0 && talk.Number > _secondOf && _firstOf < _secondOf)
            ShowLine(new LiveLine(_secondOf, true, _second, 0, false));
        else
            Render();
    }

    /// <summary>
    /// A line of translation: a finished sentence's goes to the first line (and clears the second, which held it while
    /// it was being said); that of the sentence being said goes to the second. A line that comes late, of a sentence
    /// already finished and shown, is let go.
    /// </summary>
    public void ShowLine(LiveLine line)
    {
        var text = line.Text.Trim();
        if (line.Final)
        {
            if (line.Number < _firstOf || text.Length == 0)
                return;
            if (line.Number > _firstOf)
            {
                // A sentence that has only just gone up stays in front of the next one while there is room: a short one
                // after another ("Yes. I agree.") would otherwise be gone before it was read.
                var now = Clock();
                _earlier = _first.Length > 0 && now - _firstAt < Reading(_first) ? _first : "";
                _firstAt = now;
                _firstOf = line.Number;
            }
            _first = text;
            _firstProper = line.Proper;
            if (_secondOf <= line.Number)
            {
                _second = "";
                _steady = 0;
            }
        }
        else if (line.Number > _firstOf)
        {
            _second = text;
            _steady = Math.Clamp(line.Steady, 0, text.Length);
            _secondOf = line.Number;
        }
        Render();
    }

    /// <summary>How long a sentence takes to read: a twentieth of a second a letter, between one second and three and a half.</summary>
    private static TimeSpan Reading(string text) => TimeSpan.FromSeconds(Math.Clamp(text.Length * 0.05, 1, 3.5));

    public void ClearTranslation()
    {
        _first = "";
        _earlier = "";
        _second = "";
        _steady = 0;
    }

    /// <summary>Fills the lines from what was heard and its translation, as the settings want them shown.</summary>
    private void Render()
    {
        if (_talk is not { } talk)
            return;
        var said = (Join(talk.Previous, talk.Current), talk.Draft);
        bool translating = LiveCaptions.Target(SettingsStore.Current.CaptionsShow) is not null;
        bool hasTranslation = translating && (_first.Length > 0 || _second.Length > 0);
        bool both = SettingsStore.Current.CaptionsShow == "both";
        if (hasTranslation)
        {
            // Both lines keep their room even while one is empty, so the bar doesn't jump at each sentence.
            // Just moved up, as the second line had it: a shade under full brightness until its proper translation is there.
            if (_firstProper)
                Fill(_main, (Join(_earlier, _first), ""), Bright, Dim, 3);
            else
                Fill(_main, (_earlier, _first), Bright, Steady, 3);
            int steady = Math.Clamp(_steady, 0, _second.Length);
            Fill(_live, (_second[..steady], _second[steady..]), Steady, Newest, 2);
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

    /// <summary>The settled start of the second line (for tests).</summary>
    internal string SteadyShown => _live.Visibility == Visibility.Visible && _live.Inlines.FirstInline is Run { Foreground: var brush } run && brush == Steady ? run.Text : "";

    private static string Join(string a, string b) => a.Length == 0 ? b : b.Length == 0 ? a : a + " " + b;

    public void Show(string original, string? translated, string? saying = null)
    {
        // For pictures of the window: a finished sentence and its translation, and the sentence being said.
        _talk = new StreamState(original, "", "", "", -1);
        _status.Visibility = Visibility.Collapsed;
        if (translated is not null)
            ShowLine(new LiveLine(1, true, translated, 0, true));
        else
            Render();
        if (saying is not null)
        {
            // Its last word is the newest: dimmer than the settled ones before it.
            int settled = Math.Max(0, saying.LastIndexOf(' '));
            ShowLine(new LiveLine(2, false, saying, settled, false));
        }
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

    /// <summary>Whether a download is on offer now.</summary>
    public static bool IsOpen => _open is not null;

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

    /// <summary>The offer of a pair's small models on a PC without a graphics card, for checking by eye.</summary>
    internal static Window ForCaptionsSnapshot() => new ModelDownloadWindow("Translation Model", LiveCaptions.WhySmall("tr", "ar", largeHere: false),
        [.. SmallTranslation.ModelsFor("tr", "ar").Select(m => m with { Size = m.Size + 1 })], null);

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
