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
/// The hooks only observe: nothing is ever swallowed.
/// </summary>
public sealed class GlobalInputHook : IDisposable
{
    private Thread? _thread;
    private uint _threadId;
    private IntPtr _mouseHook, _keyboardHook;
    private LowLevelProc? _mouseProc, _keyboardProc;
    private readonly ManualResetEventSlim _started = new();

    public event Action<ScreenPoint>? LeftButtonDown;
    public event Action<ScreenPoint>? LeftButtonUp;
    public event Action<ScreenPoint>? MouseMoved;
    public event Action<ModifierKey>? ModifierDown;
    public event Action<ModifierKey>? ModifierUp;
    public event Action? EscapePressed;
    public event Action? OtherKeyPressed;

    public void Start()
    {
        if (_thread is not null)
            return;
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
                    case WM_MOUSEMOVE:
                        MouseMoved?.Invoke(point);
                        break;
                    case WM_LBUTTONDOWN:
                        LeftButtonDown?.Invoke(point);
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
