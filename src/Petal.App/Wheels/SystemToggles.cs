using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using NAudio.CoreAudioApi;

namespace Petal.App.Wheels;

/// <summary>
/// Windows' own switches and commands for the quick-actions wheel. Each one does what the matching
/// control in Windows does, and a switch reads its state from where Windows keeps it, so the wheel shows
/// the truth even when the change was made elsewhere.
/// </summary>
public static partial class SystemToggles
{
    private const string Personalize = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string Advanced = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";

    // ---------- dark mode ----------

    public static bool IsDarkMode => RegistryRead(Personalize, "AppsUseLightTheme") == 0;

    /// <summary>Apps and Windows itself together, as Settings › Personalisation › Colours does.</summary>
    public static void SetDarkMode(bool dark)
    {
        using var key = Registry.CurrentUser.CreateSubKey(Personalize);
        key.SetValue("AppsUseLightTheme", dark ? 0 : 1, RegistryValueKind.DWord);
        key.SetValue("SystemUsesLightTheme", dark ? 0 : 1, RegistryValueKind.DWord);
        Broadcast("ImmersiveColorSet");
    }

    // ---------- sound ----------

    private static MMDevice? Device(DataFlow flow)
    {
        try
        {
            using var devices = new MMDeviceEnumerator();
            return devices.HasDefaultAudioEndpoint(flow, Role.Multimedia) ? devices.GetDefaultAudioEndpoint(flow, Role.Multimedia) : null;
        }
        catch (COMException)
        {
            return null;
        }
    }

    public static bool HasSpeakers => Device(DataFlow.Render) is { } d && Dispose(d);
    public static bool HasMicrophone => Device(DataFlow.Capture) is { } d && Dispose(d);

    private static bool Dispose(MMDevice device)
    {
        device.Dispose();
        return true;
    }

    public static bool? IsMuted => Mute(DataFlow.Render, null);
    public static bool? IsMicMuted => Mute(DataFlow.Capture, null);

    public static void SetMuted(bool muted) => Mute(DataFlow.Render, muted);
    public static void SetMicMuted(bool muted) => Mute(DataFlow.Capture, muted);

    /// <summary>Reads the default device's mute, or sets it when <paramref name="set"/> has a value.</summary>
    private static bool? Mute(DataFlow flow, bool? set)
    {
        using var device = Device(flow);
        if (device is null)
            return null;
        try
        {
            if (set is { } value)
                device.AudioEndpointVolume.Mute = value;
            return device.AudioEndpointVolume.Mute;
        }
        catch (COMException)
        {
            return null;
        }
    }

    /// <summary>One step of the volume slider up (1) or down (-1); returns the volume now, 0 to 100.</summary>
    public static int? StepVolume(int direction)
    {
        using var device = Device(DataFlow.Render);
        if (device is null)
            return null;
        try
        {
            var volume = device.AudioEndpointVolume;
            if (direction > 0)
            {
                if (volume.Mute) volume.Mute = false;
                volume.VolumeStepUp();
            }
            else
            {
                volume.VolumeStepDown();
            }
            return (int)Math.Round(volume.MasterVolumeLevelScalar * 100);
        }
        catch (COMException)
        {
            return null;
        }
    }

    // ---------- keep awake ----------

    private static Thread? _awake;
    private static ManualResetEventSlim? _wake;

    public static bool IsKeepingAwake => _awake is not null;

    public static event Action? AwakeChanged;

    /// <summary>
    /// Keeps the PC and the screen from sleeping until turned off (or Petal quits). The request belongs
    /// to a thread of its own that holds it, as Windows asks.
    /// </summary>
    public static void SetKeepAwake(bool on)
    {
        if (on == IsKeepingAwake)
            return;
        if (on)
        {
            var wake = new ManualResetEventSlim();
            var thread = new Thread(() =>
            {
                SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED);
                wake.Wait();
                SetThreadExecutionState(ES_CONTINUOUS);
            })
            { IsBackground = true, Name = "Petal keep awake" };
            _wake = wake;
            _awake = thread;
            thread.Start();
        }
        else
        {
            _wake?.Set();
            _awake = null;
            _wake = null;
        }
        AwakeChanged?.Invoke();
    }

    private const uint ES_CONTINUOUS = 0x80000000, ES_SYSTEM_REQUIRED = 0x1, ES_DISPLAY_REQUIRED = 0x2;

    [LibraryImport("kernel32.dll")]
    private static partial uint SetThreadExecutionState(uint flags);

    // ---------- power ----------

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool LockWorkStation();

    [LibraryImport("powrprof.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetSuspendState([MarshalAs(UnmanagedType.Bool)] bool hibernate, [MarshalAs(UnmanagedType.Bool)] bool force, [MarshalAs(UnmanagedType.Bool)] bool disableWakeEvent);

    public static void Lock() => LockWorkStation();

    public static void Sleep() => SetSuspendState(false, false, false);

    // ---------- File Explorer ----------

    public static bool ShowsHiddenFiles => RegistryRead(Advanced, "Hidden") == 1;

    public static void SetShowHiddenFiles(bool show)
    {
        RegistryWrite(Advanced, "Hidden", show ? 1 : 2);
        RefreshExplorer();
    }

    public static bool ShowsExtensions => RegistryRead(Advanced, "HideFileExt") == 0;

    public static void SetShowExtensions(bool show)
    {
        RegistryWrite(Advanced, "HideFileExt", show ? 0 : 1);
        RefreshExplorer();
    }

    /// <summary>Whether the desktop shows its icons, read from the desktop itself.</summary>
    public static bool ShowsDesktopIcons
    {
        get
        {
            var list = DesktopListView();
            return list == IntPtr.Zero ? RegistryRead(Advanced, "HideIcons") != 1 : IsWindowVisible(list);
        }
    }

    /// <summary>Shows or hides the desktop's icons, as View › Show desktop icons in its own menu does.</summary>
    public static void SetShowDesktopIcons(bool show)
    {
        if (show == ShowsDesktopIcons)
            return;
        var view = DesktopView();
        if (view != IntPtr.Zero)
            SendMessage(view, WM_COMMAND, new IntPtr(ToggleDesktopIcons), IntPtr.Zero);
    }

    private const int ToggleDesktopIcons = 0x7402;
    private const int WM_COMMAND = 0x0111;

    /// <summary>The desktop's folder view: under Progman, or under a WorkerW once a wallpaper slideshow has run.</summary>
    private static IntPtr DesktopView()
    {
        var progman = Interop.NativeMethods.FindWindowEx(IntPtr.Zero, IntPtr.Zero, "Progman", null);
        var view = Interop.NativeMethods.FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
        var worker = IntPtr.Zero;
        while (view == IntPtr.Zero && (worker = Interop.NativeMethods.FindWindowEx(IntPtr.Zero, worker, "WorkerW", null)) != IntPtr.Zero)
            view = Interop.NativeMethods.FindWindowEx(worker, IntPtr.Zero, "SHELLDLL_DefView", null);
        return view;
    }

    private static IntPtr DesktopListView()
    {
        var view = DesktopView();
        return view == IntPtr.Zero ? IntPtr.Zero : Interop.NativeMethods.FindWindowEx(view, IntPtr.Zero, "SysListView32", null);
    }

    /// <summary>File Explorer windows and the desktop pick up a changed view setting.</summary>
    private static void RefreshExplorer()
    {
        SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);
        Broadcast("ShellState");
        try
        {
            var type = Type.GetTypeFromProgID("Shell.Application");
            if (type is null) return;
            var shell = Activator.CreateInstance(type)!;
            var windows = type.InvokeMember("Windows", System.Reflection.BindingFlags.InvokeMethod, null, shell, null)!;
            int count = (int)windows.GetType().InvokeMember("Count", System.Reflection.BindingFlags.GetProperty, null, windows, null)!;
            for (int i = 0; i < count; i++)
            {
                var window = windows.GetType().InvokeMember("Item", System.Reflection.BindingFlags.InvokeMethod, null, windows, [i]);
                window?.GetType().InvokeMember("Refresh", System.Reflection.BindingFlags.InvokeMethod, null, window, null);
            }
        }
        catch (Exception ex) when (ex is COMException or System.Reflection.TargetInvocationException or MissingMethodException)
        {
            Log.Error(ex, "Refreshing File Explorer");
        }
    }

    /// <summary>
    /// Empties the Recycle Bin on every drive. Windows asks first, as it does from the desktop: this is the
    /// one quick action that can't be undone.
    /// </summary>
    public static void EmptyRecycleBin(IntPtr owner) => SHEmptyRecycleBin(owner, null, 0);

    /// <summary>
    /// Safely removes every USB drive, memory card and disc: each is locked (refused while a file on it is
    /// open), taken offline and ejected. Returns the drives ejected and those still in use.
    /// </summary>
    public static (List<string> Ejected, List<string> Busy) EjectRemovableDrives()
    {
        var ejected = new List<string>();
        var busy = new List<string>();
        foreach (var drive in RemovableDrives())
        {
            if (Eject(drive))
                ejected.Add(drive);
            else
                busy.Add(drive);
        }
        return (ejected, busy);
    }

    /// <summary>"E:" for each removable drive or disc drive that holds something.</summary>
    public static IEnumerable<string> RemovableDrives() => DriveInfo.GetDrives()
        .Where(d => d.DriveType is DriveType.Removable || d.DriveType == DriveType.CDRom && d.IsReady)
        .Select(d => d.Name.TrimEnd('\\'));

    private static bool Eject(string drive)
    {
        using var handle = CreateFile(@"\\.\" + drive, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (handle.IsInvalid)
            return false;
        bool locked = false;
        for (int attempt = 0; attempt < 10 && !locked; attempt++)
        {
            locked = DeviceIoControl(handle, FSCTL_LOCK_VOLUME, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);
            if (!locked) Thread.Sleep(100);
        }
        if (!locked)
            return false;
        DeviceIoControl(handle, FSCTL_DISMOUNT_VOLUME, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);
        var allow = Marshal.AllocHGlobal(1);
        try
        {
            Marshal.WriteByte(allow, 0);
            DeviceIoControl(handle, IOCTL_STORAGE_MEDIA_REMOVAL, allow, 1, IntPtr.Zero, 0, out _, IntPtr.Zero);
        }
        finally
        {
            Marshal.FreeHGlobal(allow);
        }
        return DeviceIoControl(handle, IOCTL_STORAGE_EJECT_MEDIA, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);
    }

    // ---------- shell pages ----------

    /// <summary>Opens an address Windows knows: "ms-settings:", "ms-actioncenter:", a folder.</summary>
    public static void Open(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Error(ex, $"Opening {target}");
        }
    }

    /// <summary>Windows' Task View: every window, and the virtual desktops.</summary>
    public static void TaskView() => Open("shell:::{3080F90E-D7AD-11D9-BD98-0000947B0257}");

    /// <summary>Hides every window to show the desktop, or brings them back.</summary>
    public static void ToggleDesktop()
    {
        try
        {
            var type = Type.GetTypeFromProgID("Shell.Application");
            if (type is null) return;
            var shell = Activator.CreateInstance(type)!;
            type.InvokeMember("ToggleDesktop", System.Reflection.BindingFlags.InvokeMethod, null, shell, null);
        }
        catch (Exception ex) when (ex is COMException or System.Reflection.TargetInvocationException)
        {
            Log.Error(ex, "Showing the desktop");
        }
    }

    // ---------- Bluetooth ----------

    /// <summary>The PC's Bluetooth radio, or null when it has none (or Windows won't say).</summary>
    public static async Task<Windows.Devices.Radios.Radio?> BluetoothAsync()
    {
        try
        {
            var radios = await Windows.Devices.Radios.Radio.GetRadiosAsync();
            return radios.FirstOrDefault(r => r.Kind == Windows.Devices.Radios.RadioKind.Bluetooth);
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static async Task<bool> SetBluetoothAsync(bool on)
    {
        var radio = await BluetoothAsync();
        if (radio is null)
            return false;
        try
        {
            if (await Windows.Devices.Radios.Radio.RequestAccessAsync() != Windows.Devices.Radios.RadioAccessStatus.Allowed)
                return false;
            return await radio.SetStateAsync(on ? Windows.Devices.Radios.RadioState.On : Windows.Devices.Radios.RadioState.Off) == Windows.Devices.Radios.RadioAccessStatus.Allowed;
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException)
        {
            Log.Error(ex, "Switching Bluetooth");
            return false;
        }
    }

    // ---------- helpers ----------

    private static int? RegistryRead(string path, string name)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(path);
            return key?.GetValue(name) is int v ? v : null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void RegistryWrite(string path, string name, int value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(path);
        key.SetValue(name, value, RegistryValueKind.DWord);
    }

    /// <summary>Tells every window a setting changed, the way Settings does, without waiting on any that hang.</summary>
    private static void Broadcast(string area)
    {
        var text = Marshal.StringToHGlobalUni(area);
        try
        {
            SendMessageTimeout(new IntPtr(0xFFFF), WM_SETTINGCHANGE, IntPtr.Zero, text, SMTO_ABORTIFHUNG, 200, out _);
        }
        finally
        {
            Marshal.FreeHGlobal(text);
        }
    }

    private const int WM_SETTINGCHANGE = 0x001A;
    private const uint SMTO_ABORTIFHUNG = 0x0002;
    private const int SHCNE_ASSOCCHANGED = 0x08000000;
    private const uint SHCNF_IDLIST = 0;

    private const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3;
    private const uint FSCTL_LOCK_VOLUME = 0x00090018, FSCTL_DISMOUNT_VOLUME = 0x00090020;
    private const uint IOCTL_STORAGE_MEDIA_REMOVAL = 0x002D4804, IOCTL_STORAGE_EJECT_MEDIA = 0x002D4808;

    [LibraryImport("user32.dll", EntryPoint = "SendMessageTimeoutW")]
    private static partial IntPtr SendMessageTimeout(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);

    [LibraryImport("user32.dll", EntryPoint = "SendMessageW")]
    private static partial IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindowVisible(IntPtr hWnd);

    [LibraryImport("shell32.dll")]
    private static partial void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);

    [LibraryImport("shell32.dll", EntryPoint = "SHEmptyRecycleBinW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SHEmptyRecycleBin(IntPtr hwnd, string? rootPath, uint flags);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial Microsoft.Win32.SafeHandles.SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeviceIoControl(Microsoft.Win32.SafeHandles.SafeFileHandle device, uint code, IntPtr inBuffer, int inSize, IntPtr outBuffer, int outSize, out int returned, IntPtr overlapped);
}
