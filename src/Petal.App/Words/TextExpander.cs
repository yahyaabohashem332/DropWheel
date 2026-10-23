using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using Petal.App.Input;
using Petal.App.Screen;
using Petal.App.Settings;
using Petal.Core.Text;
using static Petal.App.Interop.NativeMethods;

namespace Petal.App.Words;

/// <summary>
/// Snippets in any app: type an abbreviation such as ";addr" and it becomes the text you saved for it,
/// with today's date, the Hijri date or the clipboard filled in. It works whichever keyboard language is
/// on. Only the last few characters typed are held, in memory, to spot an abbreviation; nothing is kept
/// or sent, and typing that a program makes (Petal's own included) is ignored.
/// </summary>
internal static partial class TextExpander
{
    private const int Keep = 48;
    private static readonly StringBuilder Typed = new();
    private static readonly object Gate = new();
    private static IntPtr _window;
    private static bool _started;

    public static void Start()
    {
        if (_started || GlobalInputHook.Current is not { } hook)
            return;
        _started = true;
        hook.KeyPressed += OnKey;
        hook.ButtonPressed += (_, _) => Forget();
    }

    private static void Forget()
    {
        lock (Gate)
            Typed.Clear();
    }

    /// <summary>On the hook's thread: keep the character, and see whether an abbreviation was just finished.</summary>
    private static void OnKey(int vk, int scan, bool injected)
    {
        if (injected)
            return;
        var settings = SettingsStore.Current;
        if (!settings.SnippetsOn || settings.Snippets.Count == 0)
            return;
        var front = GetForegroundWindow();
        GetWindowThreadProcessId(front, out var pid);
        if (pid == Environment.ProcessId)
            return;
        lock (Gate)
        {
            if (front != _window)
            {
                Typed.Clear();
                _window = front;
            }
            bool ctrl = Down(0x11), alt = Down(0x12), win = Down(0x5B) || Down(0x5C);
            bool altGr = ctrl && alt;
            if ((ctrl || alt || win) && !altGr)
            {
                Typed.Clear();
                return;
            }
            switch (vk)
            {
                case 0x08:
                    if (Typed.Length > 0)
                        Typed.Length--;
                    return;
                case 0x0D or 0x09 or 0x1B or >= 0x21 and <= 0x28 or 0x2E:
                    Typed.Clear();
                    return;
                case 0x10 or 0x11 or 0x12 or 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5 or 0x14:
                    return;
            }
            var ch = CharOf(vk, scan, front, altGr);
            if (ch is null)
                return;
            Typed.Append(ch);
            if (Typed.Length > Keep)
                Typed.Remove(0, Typed.Length - Keep);
            if (Snippets.Match(Typed.ToString(), settings.Snippets) is not { } match)
                return;
            Typed.Clear();
            Application.Current.Dispatcher.BeginInvoke(async () =>
            {
                // Let the last key reach the app before taking the abbreviation back.
                await Task.Delay(40);
                await ExpandAsync(match, front);
            });
        }
    }

    private static bool Down(int vk) => KeyText.Down(vk);

    private static string? CharOf(int vk, int scan, IntPtr front, bool altGr) => KeyText.Of(vk, scan, front, altGr);

    private static async Task ExpandAsync(SnippetMatch match, IntPtr front)
    {
        if (GetForegroundWindow() != front)
            return;
        var text = Snippets.Fill(match.Snippet.Text, DateTime.Now, () =>
        {
            try { return Clipboard.ContainsText() ? Clipboard.GetText() : ""; }
            catch (ExternalException) { return ""; }
        });
        await Wheels.KeySender.ReplaceTypedAsync(front, match.Typed, text.Text, text.CursorBack);
    }

    /// <summary>Types a snippet into the window in front, from the command palette.</summary>
    public static async Task TypeAsync(Snippet snippet, IntPtr target)
    {
        var text = Snippets.Fill(snippet.Text, DateTime.Now, () =>
        {
            try { return Clipboard.ContainsText() ? Clipboard.GetText() : ""; }
            catch (ExternalException) { return ""; }
        });
        if (!await Wheels.KeySender.BringToFrontAsync(target))
            return;
        await Wheels.KeySender.ReplaceTypedAsync(target, 0, text.Text, text.CursorBack);
    }

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(IntPtr hWnd, out int processId);
}
