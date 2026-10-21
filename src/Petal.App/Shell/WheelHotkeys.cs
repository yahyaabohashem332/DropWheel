using System.Windows.Input;
using System.Windows.Interop;
using Petal.App.Input;
using static Petal.App.Interop.NativeMethods;

namespace Petal.App.Shell;

/// <summary>
/// Shortcuts of the wheels that have their own ("Ctrl+Alt+M" for media): each registered with Windows, so
/// it works whichever app is in front. One another program already owns is skipped and reported.
/// </summary>
public sealed class WheelHotkeys : IDisposable
{
    private const int FirstId = 0x6100;
    private readonly HwndSource _window;
    private readonly Dictionary<int, string> _wheels = [];

    /// <summary>A wheel's shortcut was pressed: its id.</summary>
    public event Action<string>? Pressed;

    /// <summary>Wheels whose shortcut another program owns, by id.</summary>
    public IReadOnlyCollection<string> Refused => _refused;

    private readonly HashSet<string> _refused = [];
    private string _applied = "";

    public WheelHotkeys()
    {
        _window = new HwndSource(new HwndSourceParameters("Petal wheel shortcuts") { ParentWindow = new IntPtr(-3), WindowStyle = 0 });
        _window.AddHook(Hook);
    }

    /// <summary>Registers the shortcuts of the wheels that are on, in place of those before.</summary>
    public void Apply(IReadOnlyList<Wheels.UserWheel> wheels)
    {
        var wanted = wheels.Where(w => w.Enabled && !string.IsNullOrWhiteSpace(w.Shortcut)).Select(w => (w.Id, w.Shortcut!)).ToList();
        var key = string.Join("|", wanted.Select(w => w.Id + "=" + w.Item2));
        if (key == _applied)
            return;
        _applied = key;
        Clear();
        int id = FirstId;
        foreach (var (wheel, text) in wanted)
        {
            if (Shortcut.Parse(text) is not { } shortcut)
                continue;
            if (RegisterHotKey(_window.Handle, id, Flags(shortcut), (uint)KeyInterop.VirtualKeyFromKey(shortcut.Key)))
                _wheels[id++] = wheel;
            else
                _refused.Add(wheel);
        }
    }

    private void Clear()
    {
        foreach (var id in _wheels.Keys)
            UnregisterHotKey(_window.Handle, id);
        _wheels.Clear();
        _refused.Clear();
    }

    private static uint Flags(Shortcut s) =>
        MOD_NOREPEAT
        | (s.Modifiers.HasFlag(ModifierKeys.Control) ? MOD_CONTROL : 0)
        | (s.Modifiers.HasFlag(ModifierKeys.Alt) ? MOD_ALT : 0)
        | (s.Modifiers.HasFlag(ModifierKeys.Shift) ? MOD_SHIFT : 0)
        | (s.Modifiers.HasFlag(ModifierKeys.Windows) ? MOD_WIN : 0);

    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && _wheels.TryGetValue(wParam.ToInt32(), out var wheel))
        {
            handled = true;
            Pressed?.Invoke(wheel);
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        Clear();
        _window.Dispose();
    }
}
