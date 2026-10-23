using System.Runtime.InteropServices;
using Petal.App.Screen;

namespace Petal.App.Input;

/// <summary>What a key types in the window in front, in that window's keyboard language, without disturbing a dead key it is in the middle of.</summary>
internal static partial class KeyText
{
    public static bool Down(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    /// <summary>The character(s) the key makes now, or null for keys that make none (or control characters).</summary>
    public static string? Of(int vk, int scan, IntPtr front, bool altGr)
    {
        var state = new byte[256];
        if (Down(0x10))
            state[0x10] = 0x80;
        if ((GetKeyState(0x14) & 1) != 0)
            state[0x14] = 0x01;
        if (altGr)
        {
            state[0x11] = 0x80;
            state[0x12] = 0x80;
        }
        var layout = ScreenNative.GetKeyboardLayout(GetWindowThreadProcessId(front, out _));
        var buffer = new char[8];
        // 4: don't disturb a dead key the app is in the middle of.
        int n = ToUnicodeEx((uint)vk, (uint)scan, state, buffer, buffer.Length, 4, layout);
        if (n <= 0)
            return null;
        var text = new string(buffer, 0, n);
        return text.Any(char.IsControl) ? null : text;
    }

    /// <summary>A key's name for "Press Ctrl+S": letters and digits as they are, the others by name.</summary>
    public static string Name(int vk) => vk switch
    {
        >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A => ((char)vk).ToString(),
        >= 0x70 and <= 0x87 => "F" + (vk - 0x6F),
        0x0D => "Enter",
        0x09 => "Tab",
        0x1B => "Esc",
        0x20 => "Space",
        0x08 => "Backspace",
        0x2E => "Delete",
        0x2D => "Insert",
        0x24 => "Home",
        0x23 => "End",
        0x21 => "Page Up",
        0x22 => "Page Down",
        0x25 => "←",
        0x26 => "↑",
        0x27 => "→",
        0x28 => "↓",
        0xBB => "+",
        0xBD => "-",
        0xBE => ".",
        0xBC => ",",
        _ => $"0x{vk:X2}",
    };

    [LibraryImport("user32.dll")]
    private static partial short GetKeyState(int key);

    [LibraryImport("user32.dll")]
    private static partial short GetAsyncKeyState(int key);

    [LibraryImport("user32.dll", EntryPoint = "ToUnicodeEx", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int ToUnicodeEx(uint vk, uint scan, [In] byte[] state, [Out] char[] buffer, int size, uint flags, IntPtr layout);

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(IntPtr hWnd, out int processId);
}
