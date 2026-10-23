using System.Runtime.InteropServices;
using Petal.App.Interop;
using static Petal.App.Interop.NativeMethods;

namespace Petal.App.Input;

public enum ModifierKey
{
    None,
    Shift,
    Control,
    Alt,
    Windows,
}

public readonly record struct ScreenPoint(int X, int Y);

/// <summary>
/// Low-level mouse and keyboard hooks on their own thread, so a busy UI thread can never
/// make Windows drop them. Events are raised on the hook thread and must return quickly.
/// The hooks only observe, with two exceptions the user turns on: the mouse side button chosen to open the
/// wheel, and the mouse wheel while a dragged file's wheel is open (it switches wheels instead of scrolling).
/// </summary>
public sealed class GlobalInputHook : IDisposable
{
    private Thread? _thread;
    private uint _threadId;
    private IntPtr _mouseHook, _keyboardHook;

    /// <summary>Both hooks are in place: the wheel can see drags and key taps.</summary>
    public bool IsRunning => _mouseHook != IntPtr.Zero && _keyboardHook != IntPtr.Zero;
    private LowLevelProc? _mouseProc, _keyboardProc;
    private readonly ManualResetEventSlim _started = new();

    public event Action<ScreenPoint>? LeftButtonDown;
    public event Action<ScreenPoint>? LeftButtonUp;
    public event Action<ScreenPoint>? MouseMoved;
    public event Action<ModifierKey>? ModifierDown;
    public event Action<ModifierKey>? ModifierUp;
    public event Action? EscapePressed;
    public event Action? OtherKeyPressed;

    /// <summary>The hooks Petal is running, for the screen tools that watch clicks and keys.</summary>
    public static GlobalInputHook? Current { get; private set; }

    /// <summary>Any key went down or up (virtual-key code), modifiers included: for showing keys on screen.</summary>
    public event Action<int, bool>? KeyChanged;

    /// <summary>A key went down: its virtual-key code, its scan code, and whether a program (not a hand) pressed it.</summary>
    public event Action<int, int, bool>? KeyPressed;

    /// <summary>A mouse button went down: 1 left, 2 right, 3 middle.</summary>
    public event Action<int, ScreenPoint>? ButtonPressed;

    /// <summary>Asked first when the mouse wheel turns (by a screen tool such as the spotlight); true keeps the turn.</summary>
    public Func<int, bool>? WheelFirst { get; set; }

    /// <summary>A side button went down (1 = Back, 2 = Forward); returns true to keep it from the app under the pointer.</summary>
    public Func<int, ScreenPoint, bool>? SideButtonDown { get; set; }

    /// <summary>Whether the side button's release is kept back too (it is when its press was).</summary>
    public Func<int, bool>? SideButtonUp { get; set; }

    /// <summary>The mouse wheel turned (up is positive); returns true to keep the turn from the app under the pointer.</summary>
    public Func<int, bool>? WheelTurned { get; set; }

    private volatile Func<int, bool, bool>[] _keyFilters = [];

    /// <summary>
    /// Adds a filter that sees each key (its code, and whether it went down) before anything else and
    /// keeps it from the app in front by returning true. Filters run on the hook's thread and must be quick.
    /// </summary>
    public void AddKeyFilter(Func<int, bool, bool> filter)
    {
        lock (this)
            _keyFilters = [.. _keyFilters, filter];
    }

    public void Start()
    {
        if (_thread is not null)
            return;
        Current = this;
        _thread = new Thread(Run) { IsBackground = true, Name = "Petal input hooks", Priority = ThreadPriority.Highest };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _started.Wait(TimeSpan.FromSeconds(5));
    }

    private void Run()
    {
        _threadId = GetCurrentThreadId();
        _mouseProc = MouseProc;
        _keyboardProc = KeyboardProc;
        var module = GetModuleHandleW(null);
        _mouseHook = SetWindowsHookExW(WH_MOUSE_LL, _mouseProc, module, 0);
        _keyboardHook = SetWindowsHookExW(WH_KEYBOARD_LL, _keyboardProc, module, 0);
        _started.Set();

        while (GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            // Low-level hooks are called from inside GetMessage; nothing else to dispatch.
        }

        UnhookWindowsHookEx(_mouseHook);
        UnhookWindowsHookEx(_keyboardHook);
    }

    private const int WM_MOUSEWHEEL = 0x020A, WM_XBUTTONDOWN = 0x020B, WM_XBUTTONUP = 0x020C, WM_RBUTTONDOWN_HOOK = 0x0204, WM_MBUTTONDOWN_HOOK = 0x0207;

    private IntPtr MouseProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            var point = new ScreenPoint(data.pt.X, data.pt.Y);
            try
            {
                switch ((int)wParam)
                {
                    case WM_XBUTTONDOWN when SideButtonDown?.Invoke((int)(data.mouseData >> 16), point) == true:
                        return new IntPtr(1);
                    case WM_XBUTTONUP when SideButtonUp?.Invoke((int)(data.mouseData >> 16)) == true:
                        return new IntPtr(1);
                    case WM_MOUSEWHEEL when WheelFirst?.Invoke((short)(data.mouseData >> 16)) == true:
                        return new IntPtr(1);
                    case WM_MOUSEWHEEL when WheelTurned?.Invoke((short)(data.mouseData >> 16)) == true:
                        return new IntPtr(1);
                    case WM_RBUTTONDOWN_HOOK:
                        ButtonPressed?.Invoke(2, point);
                        break;
                    case WM_MBUTTONDOWN_HOOK:
                        ButtonPressed?.Invoke(3, point);
                        break;
                    case WM_MOUSEMOVE:
                        MouseMoved?.Invoke(point);
                        break;
                    case WM_LBUTTONDOWN:
                        LeftButtonDown?.Invoke(point);
                        ButtonPressed?.Invoke(1, point);
                        break;
                    case WM_LBUTTONUP:
                        LeftButtonUp?.Invoke(point);
                        break;
                }
            }
            catch
            {
                // Never let an exception escape into the hook chain.
            }
        }
        return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    private IntPtr KeyboardProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            var message = (int)wParam;
            var down = message is WM_KEYDOWN or WM_SYSKEYDOWN;
            var up = message is WM_KEYUP or WM_SYSKEYUP;
            try
            {
                if ((down || up) && (data.flags & 0x10) == 0)
                {
                    foreach (var filter in _keyFilters)
                    {
                        if (filter((int)data.vkCode, down))
                            return new IntPtr(1);
                    }
                }
                if (down || up)
                    KeyChanged?.Invoke((int)data.vkCode, down);
                if (down)
                    KeyPressed?.Invoke((int)data.vkCode, (int)data.scanCode, (data.flags & 0x10) != 0);
                var modifier = ToModifier((int)data.vkCode);
                if (modifier != ModifierKey.None)
                {
                    if (down) ModifierDown?.Invoke(modifier);
                    else if (up) ModifierUp?.Invoke(modifier);
                }
                else if (down && data.vkCode == VK_ESCAPE)
                {
                    EscapePressed?.Invoke();
                }
                else if (down)
                {
                    OtherKeyPressed?.Invoke();
                }
            }
            catch
            {
                // Never let an exception escape into the hook chain.
            }
        }
        return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
    }

    private static ModifierKey ToModifier(int vk) => vk switch
    {
        VK_SHIFT or VK_LSHIFT or VK_RSHIFT => ModifierKey.Shift,
        VK_CONTROL or VK_LCONTROL or VK_RCONTROL => ModifierKey.Control,
        VK_MENU or VK_LMENU or VK_RMENU => ModifierKey.Alt,
        VK_LWIN or VK_RWIN => ModifierKey.Windows,
        _ => ModifierKey.None,
    };

    public void Dispose()
    {
        if (_thread is null)
            return;
        PostThreadMessageW(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _thread.Join(TimeSpan.FromSeconds(2));
        _thread = null;
    }
}
