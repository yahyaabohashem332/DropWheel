using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Input;
using static Petal.App.Interop.NativeMethods;

namespace Petal.App.Wheels;

/// <summary>One press of a key with its modifiers: Ctrl + Shift + T.</summary>
public sealed record KeyChord(ModifierKeys Modifiers, Key Key)
{
    public string Display => string.Join(" + ",
        new[] { (ModifierKeys.Control, "Ctrl"), (ModifierKeys.Alt, "Alt"), (ModifierKeys.Shift, "Shift"), (ModifierKeys.Windows, "Win") }
            .Where(m => Modifiers.HasFlag(m.Item1)).Select(m => m.Item2).Append(KeySender.NameOf(Key)));
}

/// <summary>
/// Presses keys in another app for a keystroke petal: "Ctrl+Shift+T", or a sequence ("Ctrl+K Ctrl+S").
/// The keys go only to the window that was in front when the wheel opened, and only once Windows has put
/// it back in front; never to Petal itself.
/// </summary>
public static partial class KeySender
{
    private static readonly Dictionary<string, Key> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Esc"] = Key.Escape, ["Escape"] = Key.Escape, ["Enter"] = Key.Return, ["Return"] = Key.Return,
        ["Del"] = Key.Delete, ["Delete"] = Key.Delete, ["Ins"] = Key.Insert, ["Insert"] = Key.Insert,
        ["Backspace"] = Key.Back, ["Back"] = Key.Back, ["Space"] = Key.Space, ["Tab"] = Key.Tab,
        ["PageUp"] = Key.PageUp, ["PgUp"] = Key.PageUp, ["PageDown"] = Key.PageDown, ["PgDn"] = Key.PageDown,
        ["Up"] = Key.Up, ["Down"] = Key.Down, ["Left"] = Key.Left, ["Right"] = Key.Right,
        ["Plus"] = Key.OemPlus, ["="] = Key.OemPlus, ["Minus"] = Key.OemMinus, ["-"] = Key.OemMinus,
        ["Comma"] = Key.OemComma, [","] = Key.OemComma, ["Period"] = Key.OemPeriod, ["."] = Key.OemPeriod,
        ["Slash"] = Key.Oem2, ["/"] = Key.Oem2, ["Backslash"] = Key.Oem5, ["\\"] = Key.Oem5,
        ["Semicolon"] = Key.Oem1, [";"] = Key.Oem1, ["Quote"] = Key.OemQuotes, ["'"] = Key.OemQuotes,
        ["["] = Key.OemOpenBrackets, ["]"] = Key.Oem6, ["`"] = Key.Oem3, ["Backtick"] = Key.Oem3,
        ["PrintScreen"] = Key.PrintScreen, ["PrtSc"] = Key.PrintScreen, ["Menu"] = Key.Apps,
    };

    /// <summary>The presses in a keystroke text, or null when a part isn't a key.</summary>
    public static IReadOnlyList<KeyChord>? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var chords = new List<KeyChord>();
        foreach (var part in text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var modifiers = ModifierKeys.None;
            Key key = Key.None;
            // "Ctrl++" means Ctrl and the plus key.
            var pieces = part.EndsWith("++", StringComparison.Ordinal) ? [.. part[..^2].Split('+', StringSplitOptions.RemoveEmptyEntries), "Plus"] : part.Split('+', StringSplitOptions.RemoveEmptyEntries);
            foreach (var piece in pieces)
            {
                switch (piece.ToLowerInvariant())
                {
                    case "ctrl" or "control": modifiers |= ModifierKeys.Control; continue;
                    case "alt": modifiers |= ModifierKeys.Alt; continue;
                    case "shift": modifiers |= ModifierKeys.Shift; continue;
                    case "win" or "windows": modifiers |= ModifierKeys.Windows; continue;
                }
                if (key != Key.None)
                    return null;
                if (Aliases.TryGetValue(piece, out var alias))
                    key = alias;
                else if (piece.Length == 1 && char.IsDigit(piece[0]))
                    key = Key.D0 + (piece[0] - '0');
                else if (piece.Length == 1 && char.IsAsciiLetter(piece[0]))
                    key = Key.A + (char.ToUpperInvariant(piece[0]) - 'A');
                else if (Enum.TryParse(piece, ignoreCase: true, out Key parsed) && parsed != Key.None)
                    key = parsed;
                else
                    return null;
            }
            if (key == Key.None)
                return null;
            chords.Add(new KeyChord(modifiers, key));
        }
        return chords.Count == 0 ? null : chords;
    }

    public static string NameOf(Key key) => key switch
    {
        >= Key.D0 and <= Key.D9 => ((char)('0' + (key - Key.D0))).ToString(),
        Key.Return => "Enter",
        Key.Back => "Backspace",
        Key.Escape => "Esc",
        Key.OemPlus => "+",
        Key.OemMinus => "-",
        Key.OemComma => ",",
        Key.OemPeriod => ".",
        Key.Oem2 => "/",
        Key.Oem5 => "\\",
        Key.Oem1 => ";",
        Key.OemQuotes => "'",
        Key.OemOpenBrackets => "[",
        Key.Oem6 => "]",
        Key.Oem3 => "`",
        Key.Next => "PageDown",
        Key.Prior => "PageUp",
        _ => key.ToString(),
    };

    /// <summary>The same text in the form Petal saves, from what someone typed.</summary>
    public static string Normalise(IReadOnlyList<KeyChord> chords) => string.Join(" ", chords.Select(c => c.Display.Replace(" + ", "+")));

    /// <summary>
    /// Brings <paramref name="target"/> back to the front and presses the keys in it. False when the window
    /// is gone, belongs to Petal, or didn't come to the front.
    /// </summary>
    public static async Task<bool> SendAsync(IntPtr target, IReadOnlyList<KeyChord> chords)
    {
        if (target == IntPtr.Zero || !IsWindow(target))
            return false;
        GetWindowThreadProcessId(target, out var pid);
        if (pid == Environment.ProcessId)
            return false;
        if (!await BringToFrontAsync(target))
            return false;
        // Keys the user still holds would mix with ours.
        for (int i = 0; i < 20 && AnyModifierHeld(); i++)
            await Task.Delay(25);
        foreach (var chord in chords)
        {
            // Checked before every press: if something else came to the front, stop.
            if (!SameWindow(GetForegroundWindow(), target))
                return false;
            var inputs = Inputs(chord);
            if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>()) != inputs.Length)
                return false;
            await Task.Delay(45);
        }
        return true;
    }

    /// <summary>Puts a window in front (restoring it when minimised) and waits until Windows agrees.</summary>
    public static async Task<bool> BringToFrontAsync(IntPtr window)
    {
        if (IsIconic(window))
            ShowWindow(window, SW_RESTORE);
        var clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < 700)
        {
            if (SameWindow(GetForegroundWindow(), window))
                return true;
            SetForegroundWindow(window);
            await Task.Delay(30);
        }
        return SameWindow(GetForegroundWindow(), window);
    }

    /// <summary>The same top-level window, or one of its own pop-ups (a dialog it opened).</summary>
    private static bool SameWindow(IntPtr front, IntPtr target) =>
        front == target || front != IntPtr.Zero && GetAncestor(front, GA_ROOTOWNER) == GetAncestor(target, GA_ROOTOWNER);

    private static bool AnyModifierHeld() =>
        new[] { VK_SHIFT, VK_CONTROL, VK_MENU, VK_LWIN, VK_RWIN }.Any(vk => (GetAsyncKeyState(vk) & 0x8000) != 0);

    /// <summary>Modifiers down, the key down and up, modifiers up in reverse.</summary>
    internal static INPUT[] Inputs(KeyChord chord)
    {
        var modifiers = new List<ushort>();
        if (chord.Modifiers.HasFlag(ModifierKeys.Control)) modifiers.Add(VK_CONTROL);
        if (chord.Modifiers.HasFlag(ModifierKeys.Alt)) modifiers.Add(VK_MENU);
        if (chord.Modifiers.HasFlag(ModifierKeys.Shift)) modifiers.Add(VK_SHIFT);
        if (chord.Modifiers.HasFlag(ModifierKeys.Windows)) modifiers.Add(VK_LWIN);
        var key = (ushort)KeyInterop.VirtualKeyFromKey(chord.Key);
        var list = new List<INPUT>();
        foreach (var m in modifiers)
            list.Add(Press(m, up: false));
        list.Add(Press(key, up: false));
        list.Add(Press(key, up: true));
        for (int i = modifiers.Count - 1; i >= 0; i--)
            list.Add(Press(modifiers[i], up: true));
        return [.. list];
    }

    private static INPUT Press(ushort vk, bool up) => new()
    {
        type = INPUT_KEYBOARD,
        u = new INPUTUNION
        {
            ki = new KEYBDINPUTPAD
            {
                wVk = vk,
                wScan = (ushort)MapVirtualKey(vk, 0),
                dwFlags = (up ? KEYEVENTF_KEYUP : 0) | (IsExtended(vk) ? KEYEVENTF_EXTENDEDKEY : 0),
            },
        },
    };

    /// <summary>Keys on the extended part of the keyboard, which Windows tells apart from the number pad's.</summary>
    private static bool IsExtended(ushort vk) => vk is 0x21 or 0x22 or 0x23 or 0x24 or 0x25 or 0x26 or 0x27 or 0x28 or 0x2D or 0x2E or 0x6F or 0x90 or 0x5B or 0x5C or 0x5D or 0x2C;

    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_EXTENDEDKEY = 0x1, KEYEVENTF_KEYUP = 0x2;
    private const ushort VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_LWIN = 0x5B, VK_RWIN = 0x5C;
    private const int SW_RESTORE = 9;
    private const uint GA_ROOTOWNER = 3;

    [LibraryImport("user32.dll")]
    private static partial uint MapVirtualKey(uint code, uint mapType);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsWindow(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsIconic(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ShowWindow(IntPtr hWnd, int command);

    [LibraryImport("user32.dll")]
    internal static partial uint GetWindowThreadProcessId(IntPtr hWnd, out int processId);

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetAncestor(IntPtr hWnd, uint flags);
}
