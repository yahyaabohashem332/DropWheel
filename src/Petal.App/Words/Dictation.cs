using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Petal.App.Input;
using Petal.App.Localization;
using Petal.App.Screen;
using Petal.App.Settings;
using Petal.App.Tools;
using Petal.App.Tools.Parts;
using Petal.App.Tools.Windows;
using Petal.App.Visuals;
using Petal.Core.Models;
using static Petal.App.Interop.NativeMethods;

namespace Petal.App.Words;

/// <summary>
/// Dictation in any app: the shortcut (Ctrl+Win+J) starts listening, the shortcut again (or a pause of
/// the microphone at two minutes) stops, and what was said is typed where the cursor is, in Arabic,
/// English or any language Whisper knows. Esc cancels. All on this PC.
/// </summary>
internal static class Dictation
{
    private static readonly TimeSpan Longest = TimeSpan.FromMinutes(2);
    private static Listener? _listener;
    private static DictationPill? _pill;
    private static IntPtr _target;
    private static IDisposable? _escape;
    private static bool _writing;
    private static Shell.GlobalHotkey? _hotkey;

    public static bool IsListening => _listener is not null;

    /// <summary>Whether the shortcut is Petal's now (false when another program has it, or there is none).</summary>
    public static bool ShortcutHeld => _hotkey?.Current is not null;

    /// <summary>Registers the dictation shortcut from the settings; false when another app owns it.</summary>
    public static bool RegisterShortcut()
    {
        _hotkey ??= new Shell.GlobalHotkey();
        _hotkey.Pressed -= OnShortcut;
        _hotkey.Pressed += OnShortcut;
        var text = SettingsStore.Current.DictationShortcut;
        return _hotkey.Register(string.IsNullOrWhiteSpace(text) ? null : Shortcut.Parse(text));
    }

    public static void Dispose() => _hotkey?.Dispose();

    private static void OnShortcut() => Toggle(GetForegroundWindow());

    /// <summary>Starts listening for <paramref name="target"/>, or stops and types what was said.</summary>
    public static void Toggle(IntPtr target)
    {
        if (_writing)
            return;
        if (_listener is not null)
        {
            _ = FinishAsync();
            return;
        }
        // One microphone, one listener: a voice command is being heard.
        if (VoiceCommand.IsListening)
            return;
        if (Listener.ReadyModel() is null)
        {
            DictationSetupWindow.ShowSingle();
            return;
        }
        if (!Listener.HasMicrophone)
        {
            Hud.HudCard.ShowAt(Hud.HudCard.CornerPoint(), "", "").Fail(L.T("Can't dictate"), L.T("No microphone found"));
            return;
        }
        _target = target;
        _listener = new Listener();
        try
        {
            _listener.Start();
        }
        catch (Exception ex) when (ex is NAudio.MmException or InvalidOperationException)
        {
            _listener.Dispose();
            _listener = null;
            Hud.HudCard.ShowAt(Hud.HudCard.CornerPoint(), "", "").Fail(L.T("Can't dictate"), L.T("The microphone couldn't be opened."));
            return;
        }
        _pill = new DictationPill(target);
        _pill.Show();
        _listener.Level += level => _pill?.Dispatcher.BeginInvoke(() => _pill?.ShowLevel(level));
        _pill.Stop += () => _ = FinishAsync();
        _pill.Cancel += Cancel;
        _escape = ScreenHooks.Escape(Cancel);
        _ = WatchLengthAsync(_listener);
    }

    private static async Task WatchLengthAsync(Listener listener)
    {
        while (_listener == listener)
        {
            await Task.Delay(500);
            if (_listener == listener && listener.Length >= Longest)
                await FinishAsync();
        }
    }

    private static void Cancel()
    {
        _escape?.Dispose();
        _listener?.Dispose();
        _listener = null;
        _pill?.Close();
        _pill = null;
    }

    private static async Task FinishAsync()
    {
        if (_listener is not { } listener)
            return;
        _writing = true;
        _escape?.Dispose();
        _listener = null;
        _pill?.Writing();
        string text;
        try
        {
            text = await listener.FinishAsync();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Error(ex, "Dictation");
            text = "";
            Hud.HudCard.ShowAt(Hud.HudCard.CornerPoint(), "", "").Fail(L.T("Dictation didn't work"), L.T("Something went wrong. The activity log has the details."));
        }
        finally
        {
            listener.Dispose();
            _pill?.Close();
            _pill = null;
            _writing = false;
        }
        if (text.Length == 0)
            return;
        // A space before it when it follows other words, as typing would.
        if (!await Wheels.KeySender.TypeTextAsync(_target, text))
        {
            SelectedText.TrySet(text);
            Hud.HudCard.ShowAt(Hud.HudCard.CornerPoint(), "", "").Succeed(L.T("Copied instead"), L.T("The app in front changed, so the words are on the clipboard."), null);
        }
    }
}

/// <summary>The small "listening" pill at the bottom of the screen: a level meter, the time, Stop and Cancel.</summary>
internal sealed class DictationPill : Window
{
    private readonly Border _meter;
    private readonly TextBlock _status;
    private readonly System.Windows.Threading.DispatcherTimer _clock = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DateTime _started = DateTime.Now;
    private readonly StackPanel _buttons = new() { Orientation = Orientation.Horizontal };

    public event Action? Stop;
    public event Action? Cancel;

    public DictationPill(IntPtr target, string stopTip = "Stop and type it")
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        Title = L.T("Dictation");
        L.Apply(this);
        var mic = new Grid { Width = 30, Height = 30, Margin = new Thickness(4, 0, 8, 0) };
        var halo = new Ellipse { Fill = new SolidColorBrush(Color.FromArgb(0x60, 0xEF, 0x44, 0x44)) };
        halo.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.3, TimeSpan.FromMilliseconds(800)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
        mic.Children.Add(halo);
        mic.Children.Add(new Ellipse { Width = 22, Height = 22, Fill = new SolidColorBrush(Color.FromRgb(0xE5, 0x39, 0x35)) });
        mic.Children.Add(new ContentControl { Content = Icons.Create("mic", 13, Brushes.White, 2), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
        var track = new Border { Width = 70, Height = 5, CornerRadius = new CornerRadius(2.5), Background = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        _meter = new Border { Width = 0, Height = 5, CornerRadius = new CornerRadius(2.5), Background = Brushes.White, HorizontalAlignment = HorizontalAlignment.Left };
        track.Child = _meter;
        _status = new TextBlock { Foreground = Brushes.White, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0), MinWidth = 140 };
        _buttons.Children.Add(new StripButton("check", stopTip, () => Stop?.Invoke(), null, 30) { Accent = Theme.Current.Accent });
        _buttons.Children.Add(new StripButton("x", "Cancel (Esc)", () => Cancel?.Invoke(), null, 30));
        var row = new StackPanel { Orientation = Orientation.Horizontal, Children = { mic, track, _status, _buttons } };
        var strip = ScreenUi.Strip(row, 22);
        strip.Margin = new Thickness(12);
        Content = strip;
        _clock.Tick += (_, _) => _status.Text = L.F("Listening… {0}", (DateTime.Now - _started).ToString(@"m\:ss"));
        _status.Text = L.F("Listening… {0}", "0:00");
        _clock.Start();
        Closed += (_, _) => _clock.Stop();
        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            long ex = (long)GetWindowLongPtr(handle, GWL_EXSTYLE) | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
            SetWindowLongPtr(handle, GWL_EXSTYLE, new IntPtr(ex));
        };
        Loaded += (_, _) =>
        {
            var monitor = target != IntPtr.Zero ? ScreenNative.OfWindow(target) : ScreenNative.UnderPointer();
            double s = monitor.Scale;
            Left = (monitor.Work.Left + monitor.Work.Width / 2.0) / s - ActualWidth / 2;
            Top = monitor.Work.Bottom / s - ActualHeight - 24;
        };
    }

    public void ShowLevel(double level) => _meter.Width = 70 * Math.Clamp(level, 0, 1);

    public void Writing()
    {
        _clock.Stop();
        _status.Text = L.T("Writing it down…");
        _meter.Width = 0;
        _buttons.Visibility = Visibility.Collapsed;
    }
}

/// <summary>Dictation's first run: the speech model to download, the language and how accurate.</summary>
internal sealed class DictationSetupWindow : ToolWindow
{
    private static DictationSetupWindow? _open;
    private readonly ModelPanel _model;

    public static void ShowSingle()
    {
        if (_open is not null)
        {
            _open.Activate();
            return;
        }
        _open = new DictationSetupWindow();
        _open.Closed += (_, _) => _open = null;
        _open.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        _open.Show();
        _open.Activate();
    }

    private DictationSetupWindow() : base("Dictation", 440)
    {
        var settings = SettingsStore.Current;
        Body.Children.Add(Text("Speak, and DropWheel types what you say in any app: in Arabic, English and many more languages. It runs on this PC; nothing is sent anywhere.", "Petal.Body"));
        var accuracy = Segments([("Fast", "Fast"), ("Balanced", "Balanced"), ("Accurate", "Accurate")], settings.DictationAccuracy);
        Body.Children.Add(Row("Accuracy", accuracy, 110, 14));
        Body.Children.Add(Text("Accurate is best for Arabic and accents; Fast suits clear English.", "Petal.Caption").WithMargin(110, 4, 0, 0));
        var languages = new List<(string, string)> { ("auto", "Detect automatically"), ("ar", "العربية"), ("en", "English") };
        languages.AddRange(Petal.Engines.Media.Transcriber.Languages.Where(c => c is not "ar" and not "en").Select(c => (c, System.Globalization.CultureInfo.GetCultureInfo(c).NativeName)));
        var language = Choice(languages, settings.DictationLanguage);
        Body.Children.Add(Row("Language", language, 110, 12));
        _model = new ModelPanel(Listener.ChosenModel);
        Body.Children.Add(_model);
        accuracy.Changed += value =>
        {
            settings.DictationAccuracy = value;
            SettingsStore.Save();
            _model.Model = Listener.ChosenModel;
            Sync();
        };
        language.SelectionChanged += (_, _) =>
        {
            settings.DictationLanguage = ValueOf<string>(language);
            SettingsStore.Save();
        };
        _model.Ready += Sync;
        Body.Children.Add(Text(L.F("Then press {0} in any app to start, and again to stop.", settings.DictationShortcut), "Petal.Caption").WithMarginT(12));
        PrimaryButton.Content = L.T("Done");
        PrimaryButton.Click += (_, _) => Close();
        Sync();
    }

    private void Sync() => PrimaryButton.IsEnabled = Listener.ReadyModel() is not null;
}
