using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using NAudio.Wave;
using Petal.App.Localization;
using Petal.App.Visuals;
using Petal.Core.Text;

namespace Petal.App.Screen;

/// <summary>
/// A timer or stopwatch that floats over everything, for a break, a talk or an exam: presets or any
/// time typed ("7", "1:30", "90s"), pause, a minute more, and big enough for a room when wanted. At the
/// end it chimes and says so.
/// </summary>
internal sealed class TimerWindow : Window
{
    private static TimerWindow? _open;
    private readonly Border _panel;
    private readonly ContentControl _view = new();
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly Stopwatch _clock = new();
    private readonly ScaleTransform _size = new(1, 1);
    private TimeSpan _length;
    private TimeSpan _added;
    private bool _stopwatch, _done, _big;
    private TextBlock? _digits;
    private Border? _progress;
    private StripButton? _pause;

    public static bool IsOpen => _open is not null;

    /// <summary>The time left (or gone, for the stopwatch), for the wheel's centre.</summary>
    public static string? Showing => _open is { _clock: { } c } t && (c.IsRunning || c.Elapsed > TimeSpan.Zero) ? Durations.Format(t.Current) : null;

    public static void Toggle()
    {
        if (_open is not null)
        {
            _open.Close();
            return;
        }
        _open = new TimerWindow();
        _open.Closed += (_, _) => _open = null;
        _open.Show();
        _open.Activate();
    }

    internal static TimerWindow ForSnapshot(TimeSpan length)
    {
        var timer = new TimerWindow();
        timer.Start(length);
        return timer;
    }

    private TimerWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        Title = L.T("Timer");
        L.Apply(this);
        _panel = ScreenUi.Strip(_view, 20);
        _panel.Padding = new Thickness(14, 12, 14, 12);
        _panel.Margin = new Thickness(16);
        _panel.LayoutTransform = _size;
        Content = _panel;
        _panel.MouseLeftButtonDown += (_, e) =>
        {
            if (e.OriginalSource is not TextBox && e.ButtonState == MouseButtonState.Pressed)
                DragMove();
        };
        _tick.Tick += (_, _) => Update();
        Closed += (_, _) => _tick.Stop();
        Setup();
        Loaded += (_, _) =>
        {
            var monitor = ScreenNative.UnderPointer();
            var s = monitor.Scale;
            Left = monitor.Work.Right / s - ActualWidth - 8;
            Top = monitor.Work.Bottom / s - ActualHeight - 8;
        };
    }

    private TimeSpan Current => _stopwatch ? _clock.Elapsed : _length + _added - _clock.Elapsed;

    // ---------- choosing ----------

    private void Setup()
    {
        _tick.Stop();
        _clock.Reset();
        _added = TimeSpan.Zero;
        _done = false;
        var stack = new StackPanel { Width = 250 };
        var head = new DockPanel { Margin = new Thickness(2, 0, 0, 10) };
        var close = new StripButton("x", "Close", Close, null, 26);
        DockPanel.SetDock(close, Dock.Right);
        head.Children.Add(close);
        head.Children.Add(new TextBlock { Text = L.T("Timer"), FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center });
        stack.Children.Add(head);
        var presets = new UniformGrid { Columns = 3 };
        foreach (var minutes in new[] { 1, 3, 5, 10, 15, 25 })
        {
            var button = Chip(L.F("{0} min", minutes));
            button.MouseLeftButtonUp += (_, e) => { e.Handled = true; Start(TimeSpan.FromMinutes(minutes)); };
            presets.Children.Add(button);
        }
        stack.Children.Add(presets);
        var custom = new TextBox
        {
            Margin = new Thickness(3, 8, 3, 0),
            Padding = new Thickness(8, 5, 8, 5),
            FontSize = 14,
            Background = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)),
            Foreground = Brushes.White,
            CaretBrush = Brushes.White,
            BorderThickness = new Thickness(0),
            ToolTip = L.T("Minutes, or 1:30, 90s, 1h 15m"),
        };
        var placeholder = new TextBlock { Text = L.T("Any time: 7, 1:30, 90s…"), Foreground = new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF)), IsHitTestVisible = false, Margin = new Thickness(14, 13, 0, 0), FontSize = 13 };
        custom.TextChanged += (_, _) => placeholder.Visibility = custom.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        custom.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Return && Durations.Parse(custom.Text) is { } span)
                Start(span);
            else if (e.Key == Key.Return)
                custom.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x8A, 0x80));
        };
        custom.PreviewTextInput += (_, _) => custom.Foreground = Brushes.White;
        stack.Children.Add(new Grid { Children = { custom, placeholder } });
        var watch = Chip(L.T("Stopwatch"));
        watch.Margin = new Thickness(3, 8, 3, 0);
        watch.MouseLeftButtonUp += (_, e) => { e.Handled = true; StartStopwatch(); };
        stack.Children.Add(watch);
        _view.Content = stack;
        Dispatcher.BeginInvoke(() => custom.Focus(), DispatcherPriority.Input);
    }

    private static Border Chip(string text)
    {
        var chip = new Border
        {
            CornerRadius = new CornerRadius(10),
            Margin = new Thickness(3),
            Padding = new Thickness(6, 7, 6, 8),
            Background = new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)),
            Cursor = Cursors.Hand,
            Child = new TextBlock { Text = text, Foreground = Brushes.White, FontSize = 13, HorizontalAlignment = HorizontalAlignment.Center },
        };
        chip.MouseEnter += (_, _) => chip.Background = new SolidColorBrush(Color.FromArgb(0x48, 0xFF, 0xFF, 0xFF));
        chip.MouseLeave += (_, _) => chip.Background = new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF));
        return chip;
    }

    // ---------- running ----------

    private void Start(TimeSpan length)
    {
        _stopwatch = false;
        _length = length;
        Running();
    }

    private void StartStopwatch()
    {
        _stopwatch = true;
        Running();
    }

    private void Running()
    {
        _done = false;
        _added = TimeSpan.Zero;
        _clock.Restart();
        var stack = new StackPanel { MinWidth = 250 };
        _digits = new TextBlock
        {
            FontFamily = UiFonts.Display,
            FontSize = 48,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
            FlowDirection = FlowDirection.LeftToRight,
        };
        stack.Children.Add(_digits);
        _progress = new Border { Height = 4, CornerRadius = new CornerRadius(2), Background = new SolidColorBrush(Theme.Current.Accent), HorizontalAlignment = HorizontalAlignment.Left, Width = 0 };
        var track = new Border { Height = 4, CornerRadius = new CornerRadius(2), Background = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)), Margin = new Thickness(0, 4, 0, 10), Child = _progress, Visibility = _stopwatch ? Visibility.Collapsed : Visibility.Visible };
        stack.Children.Add(track);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        _pause = new StripButton("pause", "Pause", TogglePause);
        buttons.Children.Add(_pause);
        if (!_stopwatch)
            buttons.Children.Add(new StripButton("plus", "A minute more", () => { _added += TimeSpan.FromMinutes(1); _done = false; if (!_clock.IsRunning) TogglePause(); }));
        buttons.Children.Add(new StripButton("rotateCcw", "Start again", () => { if (_stopwatch) StartStopwatch(); else Start(_length); }));
        buttons.Children.Add(new StripButton("maximize", "Bigger or smaller", ToggleBig));
        buttons.Children.Add(new StripButton("timer", "Another time", Setup));
        buttons.Children.Add(new StripButton("x", "Close", Close));
        stack.Children.Add(buttons);
        _view.Content = stack;
        _tick.Start();
        Update();
    }

    private void TogglePause()
    {
        if (_clock.IsRunning)
            _clock.Stop();
        else
            _clock.Start();
        if (_pause is not null)
            _pause.Child = Icons.Create(_clock.IsRunning ? "pause" : "play", 18, Brushes.White, 1.9);
    }

    private void ToggleBig()
    {
        _big = !_big;
        double to = _big ? 2.6 : 1;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        _size.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
        _size.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
    }

    private void Update()
    {
        if (_digits is null)
            return;
        var now = Current;
        // Round up, so a five-minute timer shows 05:00 and reaches 00:00 at the end.
        var shown = _stopwatch ? now : TimeSpan.FromSeconds(Math.Ceiling(Math.Max(0, now.TotalSeconds)));
        _digits.Text = Durations.Format(shown);
        if (_stopwatch || _progress is null)
            return;
        double total = (_length + _added).TotalSeconds;
        _progress.Width = Math.Clamp(1 - now.TotalSeconds / Math.Max(1, total), 0, 1) * ((FrameworkElement)_progress.Parent).ActualWidth;
        if (now <= TimeSpan.Zero && !_done)
            Finish();
    }

    private void Finish()
    {
        _done = true;
        _clock.Stop();
        _digits!.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x6B));
        _digits.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.3, TimeSpan.FromMilliseconds(450)) { AutoReverse = true, RepeatBehavior = new RepeatBehavior(6) });
        Chime.Play();
        Hud.HudCard.ShowAt(Hud.HudCard.CornerPoint(), "", "").Succeed(L.T("Time's up"), L.F("{0} timer", Durations.Format(_length + _added)), () => Activate());
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape)
            Close();
        else if (e.Key == Key.Space && _digits is not null && e.OriginalSource is not TextBox)
            TogglePause();
    }
}

/// <summary>Three soft bell notes, made on the spot.</summary>
internal static class Chime
{
    public static void Play()
    {
        const int rate = 44100;
        var notes = new[] { 880.0, 1108.73, 1318.51 };
        var samples = new float[(int)(rate * 1.6)];
        for (int n = 0; n < notes.Length; n++)
        {
            int start = (int)(n * 0.22 * rate);
            for (int i = 0; start + i < samples.Length; i++)
            {
                double t = (double)i / rate;
                double envelope = Math.Exp(-t * 3.2) * Math.Min(1, t * 200);
                samples[start + i] += (float)(0.22 * envelope * (Math.Sin(2 * Math.PI * notes[n] * t) + 0.3 * Math.Sin(4 * Math.PI * notes[n] * t)));
            }
        }
        try
        {
            var output = new WaveOutEvent();
            var provider = new RawSourceWaveStream(new System.IO.MemoryStream(Floats(samples)), WaveFormat.CreateIeeeFloatWaveFormat(rate, 1));
            output.Init(provider);
            output.PlaybackStopped += (_, _) => { output.Dispose(); provider.Dispose(); };
            output.Play();
        }
        catch (Exception ex) when (ex is NAudio.MmException or InvalidOperationException)
        {
            System.Media.SystemSounds.Exclamation.Play();
        }
    }

    private static byte[] Floats(float[] samples)
    {
        var bytes = new byte[samples.Length * 4];
        Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
        return bytes;
    }
}
