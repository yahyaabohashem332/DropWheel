using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Petal.App.Localization;
using Petal.App.Settings;
using Petal.App.Tools;
using Petal.App.Tools.Windows;
using Petal.App.Visuals;
using Petal.App.Wheels;
using Petal.Core.Text;
using static Petal.App.Interop.NativeMethods;

namespace Petal.App.Screen;

/// <summary>
/// A focus session: a set time in which the apps you find distracting are sent down to the taskbar
/// whenever they come to the front, the sound can be muted, and a small timer counts down at the top of
/// the screen. Minutes of focus are counted per day on this PC.
/// </summary>
internal static class FocusMode
{
    private static FocusPill? _pill;
    private static System.Windows.Threading.DispatcherTimer? _watch;
    private static DateTime _ends, _started;
    private static HashSet<string> _hidden = [];
    private static bool? _wasMuted;
    private static DateTime _lastCard;

    public static bool IsOn => _pill is not null;

    public static TimeSpan Left => IsOn ? _ends - DateTime.Now : TimeSpan.Zero;

    /// <summary>Opens the window to start a session, or ends the one running.</summary>
    public static void Toggle()
    {
        if (IsOn)
            End(early: true);
        else
            FocusWindow.ShowSingle();
    }

    public static void Start(TimeSpan length, IReadOnlyCollection<string> apps, bool mute)
    {
        _started = DateTime.Now;
        _ends = _started + length;
        _hidden = new HashSet<string>(apps, StringComparer.OrdinalIgnoreCase);
        if (mute)
        {
            _wasMuted = SystemToggles.IsMuted;
            SystemToggles.SetMuted(true);
        }
        foreach (var window in RunningApps.List().Where(w => _hidden.Contains(w.Program)))
            ScreenNative.ShowWindow(window.Handle, ScreenNative.SW_MINIMIZE);
        _pill = new FocusPill();
        _pill.Show();
        _watch = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _watch.Tick += (_, _) => Watch();
        _watch.Start();
    }

    /// <summary>Every second: the time, and any distracting app that came to the front goes back down.</summary>
    private static void Watch()
    {
        if (DateTime.Now >= _ends)
        {
            End(early: false);
            return;
        }
        _pill?.Tick(_ends - DateTime.Now);
        var front = GetForegroundWindow();
        if (front == IntPtr.Zero || RunningApps.ProgramOf(front) is not { } program || !_hidden.Contains(program))
            return;
        ScreenNative.ShowWindow(front, ScreenNative.SW_MINIMIZE);
        if (DateTime.Now - _lastCard > TimeSpan.FromSeconds(20))
        {
            _lastCard = DateTime.Now;
            Hud.HudCard.ShowAt(Hud.HudCard.CornerPoint(), "", "").Succeed(L.F("{0} waits until {1}", program, _ends.ToString("t", System.Globalization.CultureInfo.CurrentCulture)), L.T("You're in a focus session"), null);
        }
    }

    public static void AddFive()
    {
        if (IsOn)
            _ends = _ends.AddMinutes(5);
    }

    public static void End(bool early)
    {
        if (!IsOn)
            return;
        _watch?.Stop();
        _watch = null;
        _pill?.Close();
        _pill = null;
        if (_wasMuted is { } muted)
            SystemToggles.SetMuted(muted);
        _wasMuted = null;
        int minutes = (int)Math.Round((DateTime.Now - _started).TotalMinutes);
        FocusLog.Add(minutes);
        if (!early)
            Chime.Play();
        Hud.HudCard.ShowAt(Hud.HudCard.CornerPoint(), "", "").Succeed(L.T(early ? "Focus session ended" : "Focus session done"),
            L.F("{0} today · {1}", L.P(FocusLog.Today, "{0} minute", "{0} minutes"), L.P(FocusLog.Streak, "{0} day in a row", "{0} days in a row")), null);
    }
}

/// <summary>Minutes of focus per day, kept beside the settings.</summary>
internal static class FocusLog
{
    private static string FilePath => Path.Combine(SettingsStore.Directory, "focus.json");

    private static Dictionary<string, int> Read()
    {
        try
        {
            return File.Exists(FilePath) ? JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(FilePath)) ?? [] : [];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static string Key(DateTime day) => day.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    public static void Add(int minutes)
    {
        if (minutes <= 0)
            return;
        var days = Read();
        days[Key(DateTime.Today)] = days.GetValueOrDefault(Key(DateTime.Today)) + minutes;
        try
        {
            Directory.CreateDirectory(SettingsStore.Directory);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(days));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    public static int Today => Read().GetValueOrDefault(Key(DateTime.Today));

    /// <summary>Days in a row with some focus, today included (or up to yesterday, when today has none yet).</summary>
    public static int Streak
    {
        get
        {
            var days = Read();
            var day = DateTime.Today;
            if (!days.ContainsKey(Key(day)))
                day = day.AddDays(-1);
            int streak = 0;
            while (days.GetValueOrDefault(Key(day)) > 0)
            {
                streak++;
                day = day.AddDays(-1);
            }
            return streak;
        }
    }
}

/// <summary>The window that starts a session: how long, which apps wait, and the sound.</summary>
internal sealed class FocusWindow : ToolWindow
{
    private static FocusWindow? _open;
    private readonly Segmented<int> _length;
    private readonly TextBox _custom = new() { Width = 90, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly StackPanel _apps = new();
    private readonly CheckBox _mute = new() { Content = L.T("Mute the sound"), Margin = new Thickness(0, 12, 0, 0) };

    public static void ShowSingle()
    {
        if (_open is not null)
        {
            _open.Activate();
            return;
        }
        _open = new FocusWindow(RunningApps.List().Select(w => w.Program));
        _open.Closed += (_, _) => _open = null;
        _open.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        _open.Show();
        _open.Activate();
    }

    /// <summary>With made-up running apps, so a snapshot never shows what the user has open.</summary>
    internal static FocusWindow ForSnapshot(IEnumerable<string> running) => new(running);

    private FocusWindow(IEnumerable<string> running) : base("Focus", 440)
    {
        var settings = SettingsStore.Current;
        Body.Children.Add(Text(L.F("{0} today · {1}", L.P(FocusLog.Today, "{0} minute", "{0} minutes"), L.P(FocusLog.Streak, "{0} day in a row", "{0} days in a row")), "Petal.Caption"));
        _length = Segments([(25, "25 min"), (50, "50 min"), (90, "90 min"), (0, "Other")], settings.FocusMinutes is 25 or 50 or 90 ? settings.FocusMinutes : 0);
        _custom.Text = settings.FocusMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Body.Children.Add(Row("Length", _length, 110, 12));
        // The minutes box shows only for Other, and takes no room otherwise.
        var custom = Row("", _custom, 110, 6);
        custom.Visibility = _length.Value == 0 ? Visibility.Visible : Visibility.Collapsed;
        _length.Changed += v => custom.Visibility = v == 0 ? Visibility.Visible : Visibility.Collapsed;
        Body.Children.Add(custom);
        Body.Children.Add(Text("These apps go down to the taskbar whenever they come to the front, until the session ends:", "Petal.Body").WithMarginT(14));
        foreach (var program in settings.FocusApps.Concat(running.Where(p => p.Length > 0)).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
            _apps.Children.Add(new CheckBox { Content = program, Tag = program, IsChecked = settings.FocusApps.Contains(program, StringComparer.OrdinalIgnoreCase), Margin = new Thickness(0, 6, 0, 0) });
        Body.Children.Add(new ScrollViewer { Content = _apps, MaxHeight = 200, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 4, 0, 0) });
        _mute.IsChecked = settings.FocusMute;
        Body.Children.Add(_mute);
        Body.Children.Add(Text("Windows' own Do not disturb can also keep notifications quiet.", "Petal.Caption").WithMarginT(10));
        ResetButton.Content = L.T("Do Not Disturb…");
        ResetButton.Visibility = Visibility.Visible;
        ResetButton.Click += (_, _) => SystemToggles.Open("ms-settings:notifications");
        PrimaryButton.Content = L.T("Start the Session");
        PrimaryButton.Click += (_, _) => Begin();
    }

    private void Begin()
    {
        var minutes = _length.Value != 0 ? _length.Value : Durations.Parse(_custom.Text) is { } span ? (int)Math.Round(span.TotalMinutes) : 0;
        if (minutes <= 0)
        {
            FooterNote = "Type how many minutes";
            return;
        }
        var settings = SettingsStore.Current;
        settings.FocusMinutes = minutes;
        settings.FocusApps = _apps.Children.OfType<CheckBox>().Where(c => c.IsChecked == true).Select(c => (string)c.Tag).ToList();
        settings.FocusMute = _mute.IsChecked == true;
        SettingsStore.Save();
        Close();
        FocusMode.Start(TimeSpan.FromMinutes(minutes), settings.FocusApps, settings.FocusMute);
    }
}

/// <summary>The focus timer at the top of the screen, with five more minutes and stop.</summary>
internal sealed class FocusPill : Window
{
    private readonly TextBlock _time = new() { Foreground = Brushes.White, FontSize = 14, FontWeight = FontWeights.SemiBold, FontFamily = UiFonts.Display, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0), FlowDirection = FlowDirection.LeftToRight };

    public FocusPill()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        Title = L.T("Focus");
        L.Apply(this);
        var icon = Icons.Create("target", 15, Brushes.White, 2);
        icon.Margin = new Thickness(10, 0, 8, 0);
        icon.VerticalAlignment = VerticalAlignment.Center;
        var label = new TextBlock { Text = L.T("Focus"), Foreground = new SolidColorBrush(Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF)), FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Children = { icon, label, _time } };
        row.Children.Add(new StripButton("plus", "Five more minutes", FocusMode.AddFive, null, 28));
        row.Children.Add(new StripButton("x", "End the session", () => FocusMode.End(early: true), null, 28));
        var strip = ScreenUi.Strip(row, 18);
        strip.Margin = new Thickness(10);
        ScreenUi.DragFromBackground(this, strip);
        Content = strip;
        Tick(FocusMode.Left);
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
            Left = monitor.Work.Right / s - ActualWidth - 12;
            Top = monitor.Work.Top / s + 8;
        };
    }

    public void Tick(TimeSpan left) => _time.Text = Durations.Format(TimeSpan.FromSeconds(Math.Ceiling(Math.Max(0, left.TotalSeconds))));
}
