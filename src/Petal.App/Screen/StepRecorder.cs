using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Petal.App.Input;
using Petal.App.Localization;
using Petal.App.Visuals;
using static Petal.App.Interop.NativeMethods;

namespace Petal.App.Screen;

/// <summary>What a step of a guide is.</summary>
public enum StepKind
{
    Click,
    RightClick,
    DoubleClick,
    Type,
    Secret,
    Keys,
}

/// <summary>One step of a guide: what to do, in words that can be changed, and its picture.</summary>
public sealed class GuideStep
{
    public StepKind Kind { get; set; }
    public string Text { get; set; } = "";
    public string Picture { get; set; } = "";
}

/// <summary>
/// The sentence for a step, from what is under the pointer: "Click «Save» in Notepad", "Choose «Export»",
/// "Open the «Layout» tab", "Type «hello»", "Press Ctrl+S". In the language Petal speaks.
/// </summary>
public static class StepWords
{
    public static string For(StepKind kind, string? name, string? control, string? window, string? typed = null)
    {
        name = Clean(name);
        window = Clean(window);
        switch (kind)
        {
            case StepKind.Type:
                return L.F("Type «{0}»", typed ?? "");
            case StepKind.Secret:
                return L.T("Type the password");
            case StepKind.Keys:
                return L.F("Press {0}", typed ?? "");
        }
        if (name is null)
        {
            return (kind, window) switch
            {
                (StepKind.RightClick, { } w) => L.F("Right-click here in {0}", w),
                (StepKind.RightClick, null) => L.T("Right-click here"),
                (StepKind.DoubleClick, { } w) => L.F("Double-click here in {0}", w),
                (StepKind.DoubleClick, null) => L.T("Double-click here"),
                (_, { } w) => L.F("Click here in {0}", w),
                _ => L.T("Click here"),
            };
        }
        if (kind == StepKind.RightClick)
            return L.F("Right-click «{0}»", name);
        if (kind == StepKind.DoubleClick)
            return L.F("Double-click «{0}»", name);
        return control switch
        {
            "menu item" => L.F("Choose «{0}»", name),
            "tab item" => L.F("Open the «{0}» tab", name),
            "check box" => L.F("Tick «{0}»", name),
            "radio button" => L.F("Choose «{0}»", name),
            "hyperlink" => L.F("Click the «{0}» link", name),
            "edit" or "document" or "combo box" => L.F("Click in «{0}»", name),
            "list item" or "tree item" or "data item" => L.F("Select «{0}»", name),
            _ when window is not null && !string.Equals(window, name, StringComparison.CurrentCultureIgnoreCase) => L.F("Click «{0}» in {1}", name, window),
            _ => L.F("Click «{0}»", name),
        };
    }

    /// <summary>A name worth saying: one line, not too long; null when there is none.</summary>
    internal static string? Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var line = string.Join(' ', text.Split((char[])['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        if (line.Length == 0)
            return null;
        return line.Length > 60 ? line[..60].TrimEnd() + "…" : line;
    }
}

/// <summary>
/// Records a step-by-step guide while you work: each click with a picture of the window and a ring where
/// it was, typing (never what goes into a password box) and shortcuts. Stop opens the guide to edit and
/// save as PDF, Word, a web page or Markdown. The pictures stay on this PC.
/// </summary>
internal static class StepRecorder
{
    /// <summary>A guide longer than this is stopped: it is more than one task.</summary>
    public const int MostSteps = 200;

    private static StepsPill? _pill;
    private static BlockingCollection<Action>? _work;
    private static Thread? _worker;
    private static readonly List<GuideStep> Steps = [];
    private static readonly StringBuilder Typed = new();
    private static bool _typingSecret, _typingChecked;
    private static string _folder = "";
    private static (int X, int Y, long At)? _lastClick;
    // The keyboard is in a password box: kept up to date as the focus moves, read when a key is pressed.
    private static volatile bool _focusIsPassword;
    private static AutomationFocusChangedEventHandler? _focusWatch;
    private static CancellationTokenSource? _session;

    public static bool IsRecording => _pill is not null;

    public static int Count
    {
        get
        {
            lock (Steps)
                return Steps.Count;
        }
    }

    public static void Toggle()
    {
        if (IsRecording)
            Finish();
        else
            Start();
    }

    public static void Start()
    {
        // The last recording's pictures are still being finished: one at a time.
        if (IsRecording || _worker is { IsAlive: true } || GlobalInputHook.Current is not { } hook)
            return;
        lock (Steps)
            Steps.Clear();
        Typed.Clear();
        _typingChecked = _typingSecret = false;
        _typingPicture = null;
        _lastClick = null;
        TidyOld();
        _folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Petal Steps",
            DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(_folder);
        _session = new CancellationTokenSource();
        _work = new BlockingCollection<Action>();
        _worker = new Thread(Work) { IsBackground = true, Name = "Petal steps" };
        _worker.SetApartmentState(ApartmentState.STA);
        _worker.Start((_work, _session.Token));
        WatchFocus(true);
        hook.ButtonPressed += OnButton;
        hook.KeyPressed += OnKey;
        _pill = new StepsPill();
        _pill.Finish += Finish;
        _pill.Cancel += Cancel;
        _pill.Show();
    }

    private static void Stop()
    {
        if (GlobalInputHook.Current is { } hook)
        {
            hook.ButtonPressed -= OnButton;
            hook.KeyPressed -= OnKey;
        }
        WatchFocus(false);
        _pill?.Close();
        _pill = null;
    }

    /// <summary>Follows the keyboard focus, so a key pressed in a password box is known as one at once.</summary>
    private static void WatchFocus(bool on)
    {
        try
        {
            if (on)
            {
                _focusIsPassword = IsSecret();
                _focusWatch = (sender, _) =>
                {
                    try
                    {
                        _focusIsPassword = sender is AutomationElement element && element.Current.IsPassword;
                    }
                    catch (Exception)
                    {
                        // Unknown: kept back, to be safe.
                        _focusIsPassword = true;
                    }
                };
                Automation.AddAutomationFocusChangedEventHandler(_focusWatch);
            }
            else if (_focusWatch is { } watch)
            {
                _focusWatch = null;
                // Removing waits for UI Automation's own thread: not on the interface thread.
                _ = Task.Run(() =>
                {
                    try { Automation.RemoveAutomationFocusChangedEventHandler(watch); }
                    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or COMException) { }
                });
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or COMException)
        {
            Log.Info($"Step recording can't follow the focus: {ex.Message}");
        }
    }

    /// <summary>Pictures left by a recording Petal didn't get to finish (it was closed, or the PC turned off).</summary>
    private static void TidyOld()
    {
        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Petal Steps");
        _ = Task.Run(() =>
        {
            try
            {
                foreach (var dir in Directory.EnumerateDirectories(root))
                    if (Directory.GetLastWriteTimeUtc(dir) < DateTime.UtcNow.AddDays(-2))
                        TryDelete(dir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        });
    }

    /// <summary>Stops, waits for the last pictures, and opens the guide.</summary>
    public static async void Finish()
    {
        if (!IsRecording)
            return;
        Stop();
        var work = _work;
        _work = null;
        if (work is not null)
        {
            work.Add(FlushTyping);
            work.CompleteAdding();
            await Task.Run(() => _worker?.Join(TimeSpan.FromSeconds(20)));
        }
        List<GuideStep> steps;
        lock (Steps)
            steps = [.. Steps];
        if (steps.Count == 0)
        {
            Hud.HudCard.ShowAt(Hud.HudCard.CornerPoint(), "", "").Fail(L.T("No steps were recorded"), L.T("Click in an app while the steps are being recorded."));
            TryDelete(_folder);
            return;
        }
        StepGuideWindow.Open(steps, _folder);
    }

    public static void Cancel()
    {
        Stop();
        // What is still queued is thrown away, not added to anything.
        _session?.Cancel();
        _work?.CompleteAdding();
        _work = null;
        var folder = _folder;
        _ = Task.Run(() =>
        {
            _worker?.Join(TimeSpan.FromSeconds(20));
            TryDelete(folder);
        });
    }

    private static void TryDelete(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void Work(object? state)
    {
        var (queue, cancelled) = ((BlockingCollection<Action>, CancellationToken))state!;
        foreach (var job in queue.GetConsumingEnumerable())
        {
            if (cancelled.IsCancellationRequested)
                continue;
            try
            {
                job();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Recording a step");
            }
        }
    }

    /// <summary>On the hook's thread: a click outside Petal becomes a step (two quick ones in one place, a double-click).</summary>
    private static void OnButton(int button, ScreenPoint at)
    {
        if (button is not (1 or 2) || _work is not { IsAddingCompleted: false } work)
            return;
        var under = WindowFromPoint(new POINT(at.X, at.Y));
        GetWindowThreadProcessId(under, out var process);
        if (process == Environment.ProcessId)
            return;
        long now = Environment.TickCount64;
        bool twice = button == 1 && _lastClick is { } last && now - last.At <= GetDoubleClickTime()
            && Math.Abs(last.X - at.X) <= 6 && Math.Abs(last.Y - at.Y) <= 6;
        _lastClick = button == 1 && !twice ? (at.X, at.Y, now) : null;
        var root = GetAncestor(under, GA_ROOT);
        if (twice)
        {
            work.Add(MakeLastDouble);
            return;
        }
        // The picture now, before the click changes anything; the words can follow.
        var picture = Picture(root, at);
        var kind = button == 2 ? StepKind.RightClick : StepKind.Click;
        work.Add(() =>
        {
            FlushTyping();
            Describe(at, out var name, out var control);
            Add(kind, StepWords.For(kind, name, control, TitleOf(root)), picture);
        });
    }

    /// <summary>On the hook's thread: typing and shortcuts.</summary>
    private static void OnKey(int vk, int scan, bool injected)
    {
        if (injected || _work is not { IsAddingCompleted: false } work)
            return;
        if (vk is 0x10 or 0x11 or 0x12 or 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5 or 0x5B or 0x5C or 0x14)
            return;
        var front = GetForegroundWindow();
        GetWindowThreadProcessId(front, out var process);
        if (process == Environment.ProcessId)
            return;
        bool ctrl = KeyText.Down(0x11), alt = KeyText.Down(0x12), win = KeyText.Down(0x5B) || KeyText.Down(0x5C);
        bool altGr = ctrl && alt;
        if ((ctrl || alt || win) && !altGr)
        {
            var keys = string.Join("+", new[] { ctrl ? "Ctrl" : null, alt ? "Alt" : null, win ? "Win" : null, KeyText.Down(0x10) ? "Shift" : null }.OfType<string>().Append(KeyText.Name(vk)));
            var picture = Picture(front, null);
            work.Add(() =>
            {
                FlushTyping();
                Add(StepKind.Keys, StepWords.For(StepKind.Keys, null, null, null, keys), picture);
            });
            return;
        }
        if (vk is 0x0D or 0x09)
        {
            var picture = Picture(front, null);
            work.Add(() =>
            {
                FlushTyping(picture);
                if (vk == 0x0D)
                    Add(StepKind.Keys, StepWords.For(StepKind.Keys, null, null, null, KeyText.Name(vk)), null);
            });
            return;
        }
        if (vk == 0x08)
        {
            work.Add(() =>
            {
                if (Typed.Length > 0)
                    Typed.Length--;
            });
            return;
        }
        var text = KeyText.Of(vk, scan, front, altGr);
        if (text is null)
            return;
        // Decided now, as the key goes down: by the time the worker gets to it the focus may have moved on.
        bool secret = _focusIsPassword;
        var shot = Typed.Length == 0 ? Picture(front, null) : null;
        work.Add(() =>
        {
            if (!_typingChecked)
            {
                _typingChecked = true;
                _typingPicture = shot;
            }
            // Whatever goes into a password box is never kept.
            if (secret)
                _typingSecret = true;
            Typed.Append(_typingSecret ? "•" : text);
        });
    }

    private static Func<string?>? _typingPicture;

    /// <summary>On the worker: the typing so far becomes a step.</summary>
    private static void FlushTyping() => FlushTyping(null);

    private static void FlushTyping(Func<string?>? picture)
    {
        if (Typed.Length == 0)
        {
            _typingChecked = false;
            return;
        }
        var kind = _typingSecret ? StepKind.Secret : StepKind.Type;
        // A run that went into a password box at any point keeps none of its letters.
        Add(kind, StepWords.For(kind, null, null, null, _typingSecret ? "" : Typed.ToString()), _typingPicture ?? picture);
        Typed.Clear();
        _typingChecked = false;
        _typingSecret = false;
        _typingPicture = null;
    }

    private static void MakeLastDouble()
    {
        lock (Steps)
        {
            if (Steps.Count == 0 || Steps[^1].Kind != StepKind.Click)
                return;
            var last = Steps[^1];
            last.Kind = StepKind.DoubleClick;
            var name = last.Text;
            int open = name.IndexOf('«'), close = name.IndexOf('»');
            last.Text = open >= 0 && close > open
                ? L.F("Double-click «{0}»", name[(open + 1)..close])
                : L.T("Double-click here");
        }
    }

    private static void Add(StepKind kind, string text, Func<string?>? picture)
    {
        int count;
        lock (Steps)
        {
            if (Steps.Count >= MostSteps)
                return;
            Steps.Add(new GuideStep { Kind = kind, Text = text, Picture = picture?.Invoke() ?? "" });
            count = Steps.Count;
        }
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            _pill?.SetCount(count);
            if (count >= MostSteps)
                Finish();
        });
    }

    /// <summary>What Windows says is under the point: its name and kind ("button", "menu item").</summary>
    private static void Describe(ScreenPoint at, out string? name, out string? control)
    {
        name = null;
        control = null;
        try
        {
            var element = AutomationElement.FromPoint(new System.Windows.Point(at.X, at.Y));
            if (element is null)
                return;
            name = element.Current.Name;
            control = element.Current.ControlType?.LocalizedControlType is { } localized && element.Current.ControlType.ProgrammaticName is { } programmatic
                ? programmatic.Replace("ControlType.", "") switch
                {
                    "MenuItem" => "menu item",
                    "TabItem" => "tab item",
                    "CheckBox" => "check box",
                    "RadioButton" => "radio button",
                    "Hyperlink" => "hyperlink",
                    "Edit" => "edit",
                    "Document" => "document",
                    "ComboBox" => "combo box",
                    "ListItem" => "list item",
                    "TreeItem" => "tree item",
                    "DataItem" => "data item",
                    _ => localized,
                }
                : null;
            // A nameless part (an icon inside a button): its parent's name says it better.
            if (string.IsNullOrWhiteSpace(name) && TreeWalker.ControlViewWalker.GetParent(element) is { } parent)
                name = parent.Current.Name;
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or COMException or InvalidOperationException or ArgumentException)
        {
        }
    }

    private static bool IsSecret()
    {
        try
        {
            return AutomationElement.FocusedElement?.Current.IsPassword == true;
        }
        catch (Exception)
        {
            // Unknown: kept back, to be safe.
            return true;
        }
    }

    private static string? TitleOf(IntPtr window)
    {
        var text = new StringBuilder(256);
        GetWindowText(window, text, text.Capacity);
        var title = text.ToString();
        // "Document - App" reads better as "App".
        int dash = title.LastIndexOf(" - ", StringComparison.Ordinal);
        return StepWords.Clean(dash > 0 ? title[(dash + 3)..] : title);
    }

    /// <summary>
    /// The window's picture now (with a ring at the click, when there is one), saved later on the worker.
    /// A small window (a menu, a popup) brings some of what is around it.
    /// </summary>
    private static Func<string?>? Picture(IntPtr window, ScreenPoint? click)
    {
        var monitor = ScreenNative.OfWindow(window);
        RECT area;
        if (window == IntPtr.Zero || ScreenNative.DwmGetWindowAttribute(window, ScreenNative.DWMWA_EXTENDED_FRAME_BOUNDS, out area, Marshal.SizeOf<RECT>()) != 0)
            GetWindowRect(window, out area);
        area = Clip(area, monitor.Bounds);
        if (area.Width < 320 || area.Height < 200)
        {
            var c = click ?? new ScreenPoint(area.Left + area.Width / 2, area.Top + area.Height / 2);
            area = Clip(new RECT { Left = c.X - 480, Top = c.Y - 320, Right = c.X + 480, Bottom = c.Y + 320 }, monitor.Bounds);
        }
        if (area.Width <= 0 || area.Height <= 0)
            return null;
        // Taken at once but off the hook's thread: the hook must answer Windows quickly, or Windows drops it.
        var shot = Task.Run(() => Wheels.Screenshots.Capture(area.Left, area.Top, area.Width, area.Height));
        var folder = _folder;
        var ring = click is { } at ? new System.Windows.Point(at.X - area.Left, at.Y - area.Top) : (System.Windows.Point?)null;
        return () => shot.GetAwaiter().GetResult() is { } picture ? Save(picture, ring, folder) : null;
    }

    private static RECT Clip(RECT r, RECT bounds) => new()
    {
        Left = Math.Max(r.Left, bounds.Left),
        Top = Math.Max(r.Top, bounds.Top),
        Right = Math.Min(r.Right, bounds.Right),
        Bottom = Math.Min(r.Bottom, bounds.Bottom),
    };

    private static int _saved;

    /// <summary>The picture with a ring at the click, at most 1600 pixels wide, as a PNG in the guide's folder.</summary>
    internal static string Save(BitmapSource shot, System.Windows.Point? ring, string folder)
    {
        double scale = Math.Min(1, 1600.0 / shot.PixelWidth);
        int w = (int)Math.Round(shot.PixelWidth * scale), h = (int)Math.Round(shot.PixelHeight * scale);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawImage(shot, new Rect(0, 0, w, h));
            if (ring is { } p)
            {
                var accent = Theme.Current.Accent;
                var centre = new System.Windows.Point(p.X * scale, p.Y * scale);
                dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(0x38, accent.R, accent.G, accent.B)), new Pen(new SolidColorBrush(accent), 3.5), centre, 22, 22);
                dc.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromArgb(0xD0, 0xFF, 0xFF, 0xFF)), 1.5), centre, 25.5, 25.5);
            }
        }
        var bitmap = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var path = System.IO.Path.Combine(folder, $"step-{Interlocked.Increment(ref _saved):D4}.png");
        using (var file = File.Create(path))
            encoder.Save(file);
        return path;
    }

    private const uint GA_ROOT = 2;

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out int process);

    [DllImport("user32.dll")]
    private static extern uint GetDoubleClickTime();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int count);
}

/// <summary>The bar at the top of the screen while steps are recorded: how many, Finish and Cancel. Kept out of the pictures.</summary>
internal sealed class StepsPill : Window
{
    private readonly TextBlock _count = new() { Foreground = Brushes.White, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0), MinWidth = 140 };

    public event Action? Finish;
    public event Action? Cancel;

    public StepsPill()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        Title = L.T("Record Steps");
        L.Apply(this);
        var dot = new Ellipse { Width = 10, Height = 10, Fill = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44)), Margin = new Thickness(10, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        dot.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.25, TimeSpan.FromMilliseconds(800)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
        var row = new StackPanel { Orientation = Orientation.Horizontal, Children = { dot, _count } };
        row.Children.Add(new StripButton("check", "Finish the guide", () => Finish?.Invoke(), null, 30) { Accent = Theme.Current.Accent });
        row.Children.Add(new StripButton("x", "Cancel", () => Cancel?.Invoke(), null, 30));
        var strip = ScreenUi.Strip(row, 20);
        strip.Margin = new Thickness(10);
        ScreenUi.DragFromBackground(this, strip);
        Content = strip;
        SetCount(0);
        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            long ex = (long)GetWindowLongPtr(handle, GWL_EXSTYLE) | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
            SetWindowLongPtr(handle, GWL_EXSTYLE, new IntPtr(ex));
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

    public void SetCount(int count) => _count.Text = count == 0 ? L.T("Recording steps… click in any app") : L.F("Recording steps · {0}", L.P(count, "{0} step", "{0} steps"));
}
