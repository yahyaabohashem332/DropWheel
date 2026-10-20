using System.Windows.Input;
using System.Windows.Interop;
using Petal.App.Input;
using static Petal.App.Interop.NativeMethods;

namespace Petal.App.Shell;

/// <summary>
/// Petal's system-wide shortcut (Ctrl + Win + Z by default), registered with Windows on a
/// message-only window so it works whichever app is in front.
/// </summary>
public sealed class GlobalHotkey : IDisposable
{
    private const int Id = 0x5E7A;
    private readonly HwndSource _window;
    private bool _registered;

    public event Action? Pressed;

    public GlobalHotkey()
    {
        // HWND_MESSAGE: a window that only receives messages.
        _window = new HwndSource(new HwndSourceParameters("Petal shortcut") { ParentWindow = new IntPtr(-3), WindowStyle = 0 });
        _window.AddHook(Hook);
    }

    /// <summary>The shortcut now in effect, or null when none is registered.</summary>
    public Shortcut? Current { get; private set; }

    /// <summary>
    /// Registers the shortcut in place of the current one; null turns it off. Returns false when
    /// another program already owns the combination (the old one stays registered then).
    /// </summary>
    public bool Register(Shortcut? shortcut)
    {
        var previous = Current;
        Unregister();
        if (shortcut is null || !shortcut.IsValid)
            return shortcut is null;
        if (TryRegister(shortcut))
            return true;
        if (previous is not null)
            TryRegister(previous);
        return false;
    }

    /// <summary>Releases the shortcut for a moment, e.g. while a new one is being recorded.</summary>
    public void Suspend()
    {
        if (_registered)
            UnregisterHotKey(_window.Handle, Id);
        _registered = false;
    }

    public void Resume()
    {
        if (!_registered && Current is not null)
            _registered = RegisterHotKey(_window.Handle, Id, Flags(Current), (uint)KeyInterop.VirtualKeyFromKey(Current.Key));
    }

    private bool TryRegister(Shortcut shortcut)
    {
        _registered = RegisterHotKey(_window.Handle, Id, Flags(shortcut), (uint)KeyInterop.VirtualKeyFromKey(shortcut.Key));
        Current = _registered ? shortcut : null;
        return _registered;
    }

    private void Unregister()
    {
        Suspend();
        Current = null;
    }

    private static uint Flags(Shortcut s) =>
        MOD_NOREPEAT
        | (s.Modifiers.HasFlag(ModifierKeys.Control) ? MOD_CONTROL : 0)
        | (s.Modifiers.HasFlag(ModifierKeys.Alt) ? MOD_ALT : 0)
        | (s.Modifiers.HasFlag(ModifierKeys.Shift) ? MOD_SHIFT : 0)
        | (s.Modifiers.HasFlag(ModifierKeys.Windows) ? MOD_WIN : 0);

    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == Id)
        {
            handled = true;
            Pressed?.Invoke();
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        Unregister();
        _window.Dispose();
    }
}
