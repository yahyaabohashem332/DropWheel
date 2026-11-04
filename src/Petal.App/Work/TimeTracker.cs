using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;
using Petal.App.Settings;
using Petal.Core.Time;

namespace Petal.App.Work;

/// <summary>
/// Notices which program is in front every few seconds, while it is on and the user is at the PC, and counts its time in a
/// small file on this PC. It starts only when asked, and goes on after a restart until it is turned off. Window titles are
/// kept only if asked for; what is typed, seen or opened never is.
/// </summary>
public static class TimeTracker
{
    private static readonly TimeSpan Every = TimeSpan.FromSeconds(5);

    /// <summary>With no key or mouse for this long the user is taken to be away, and the time isn't counted.</summary>
    private static readonly TimeSpan Away = TimeSpan.FromMinutes(3);

    private const int KeepDays = 365;

    private static readonly string Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Petal", "time");
    private static readonly Dictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase);
    private static DispatcherTimer? _timer;
    private static DateTime _saved = DateTime.MinValue;

    public static TimeLog Log { get; } = new(Folder);

    public static bool IsOn => _timer is not null;

    public static event Action? Changed;

    /// <summary>Goes on after a restart if it was on.</summary>
    public static void Resume()
    {
        if (SettingsStore.Current.TimeTrackerOn)
            Start();
    }

    public static void Toggle()
    {
        if (IsOn)
            Stop();
        else
            Start();
        SettingsStore.Current.TimeTrackerOn = IsOn;
        SettingsStore.Save();
        Changed?.Invoke();
    }

    private static void Start()
    {
        if (_timer is not null)
            return;
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = Every };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        Log.Trim(KeepDays, DateOnly.FromDateTime(DateTime.Now));
    }

    private static void Stop()
    {
        _timer?.Stop();
        _timer = null;
        Flush();
    }

    /// <summary>Whatever is counted so far goes to the file; on leaving, it stops without changing the setting.</summary>
    public static void Flush()
    {
        try
        {
            Log.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Petal.App.Log.Error(ex, "Saving the time log");
        }
    }

    private static void Tick()
    {
        try
        {
            if (TimeSinceInput() < Away && Foreground() is { } front && !IsExcluded(front.App))
                Log.Add(DateOnly.FromDateTime(DateTime.Now), front.App, (long)Every.TotalSeconds, SettingsStore.Current.TimeTrackerTitles ? front.Title : null);
            if (DateTime.UtcNow - _saved > TimeSpan.FromMinutes(1))
            {
                _saved = DateTime.UtcNow;
                Flush();
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Petal.App.Log.Error(ex, "Counting time");
        }
    }

    private static bool IsExcluded(string app) =>
        SettingsStore.Current.TimeTrackerLeftOut.Contains(app, StringComparer.OrdinalIgnoreCase);

    private static TimeSpan TimeSinceInput()
    {
        var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
        return GetLastInputInfo(ref info) ? TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount - info.Time)) : TimeSpan.Zero;
    }

    /// <summary>The program in front and its window's title; null at the lock screen or with nothing in front.</summary>
    private static (string App, string Title)? Foreground()
    {
        var window = GetForegroundWindow();
        if (window == IntPtr.Zero)
            return null;
        GetWindowThreadProcessId(window, out uint id);
        if (id == 0)
            return null;
        var process = ProcessName(id);
        // Store apps sit inside a frame: the program is the child that isn't the frame.
        if (string.Equals(process, "ApplicationFrameHost", StringComparison.OrdinalIgnoreCase))
        {
            uint inner = 0;
            EnumChildWindows(window, (child, _) =>
            {
                GetWindowThreadProcessId(child, out uint childId);
                if (childId != id && childId != 0)
                {
                    inner = childId;
                    return false;
                }
                return true;
            }, IntPtr.Zero);
            if (inner != 0)
            {
                id = inner;
                process = ProcessName(id);
            }
        }
        if (process.Length == 0 || process is "LockApp" or "LogonUI" or "ShellExperienceHost" or "SearchHost" or "StartMenuExperienceHost")
            return null;
        var title = new StringBuilder(256);
        GetWindowText(window, title, title.Capacity);
        return (FriendlyName(id, process), title.ToString());
    }

    private static string ProcessName(uint id)
    {
        try
        {
            using var process = Process.GetProcessById((int)id);
            return process.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return "";
        }
    }

    /// <summary>What a program calls itself ("Google Chrome") rather than its file's name ("chrome").</summary>
    private static string FriendlyName(uint id, string process)
    {
        if (Names.TryGetValue(process, out var known))
            return known;
        var name = process;
        try
        {
            using var running = Process.GetProcessById((int)id);
            if (running.MainModule?.FileVersionInfo.FileDescription is { Length: > 0 } description)
                name = description.Trim();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            // A program run by another user or the system won't say; its file's name will do.
        }
        Names[process] = name;
        return name;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint Size;
        public uint Time;
    }

    private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LastInputInfo info);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr window, StringBuilder text, int most);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr window, EnumWindowsProc callback, IntPtr parameter);
}
