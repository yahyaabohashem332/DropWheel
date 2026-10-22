using System.Windows;
using System.Windows.Input;
using Petal.App.Screen;
using Petal.App.Wheels;

namespace Petal.App.Words;

/// <summary>
/// The text selected in another app, and putting new text in its place. Asks the app through UI
/// Automation first; otherwise copies the selection with Ctrl+C. Whatever was on the clipboard is put
/// back afterwards, and Petal's own trips through the clipboard stay out of the clipboard history.
/// </summary>
internal static class SelectedText
{
    private static readonly KeyChord Copy = new(ModifierKeys.Control, Key.C);
    private static readonly KeyChord Paste = new(ModifierKeys.Control, Key.V);

    /// <summary>The selection in <paramref name="target"/>, or "" when nothing is selected or the app is gone.</summary>
    public static async Task<string> GetAsync(IntPtr target)
    {
        if (!await KeySender.BringToFrontAsync(target))
            return "";
        await Task.Delay(80);
        var viaAutomation = await WithTimeout(Task.Run(Wheels.ScriptRunner.SelectedText), 700);
        if (!string.IsNullOrEmpty(viaAutomation))
            return viaAutomation;
        // The app doesn't say: copy the selection, read it, and put the clipboard back.
        var saved = ClipboardSnapshot.Take();
        try
        {
            Clips.ClipboardHistory.Pause();
            uint before = ScreenNative.GetClipboardSequenceNumber();
            if (!await KeySender.SendAsync(target, [Copy]))
                return "";
            for (int i = 0; i < 20 && ScreenNative.GetClipboardSequenceNumber() == before; i++)
                await Task.Delay(30);
            if (ScreenNative.GetClipboardSequenceNumber() == before)
                return "";
            await Task.Delay(30);
            return TryRead();
        }
        finally
        {
            saved.Restore();
            Clips.ClipboardHistory.Resume();
        }
    }

    /// <summary>Types <paramref name="text"/> over the selection in <paramref name="target"/>, by pasting it.</summary>
    public static async Task<bool> ReplaceAsync(IntPtr target, string text)
    {
        var saved = ClipboardSnapshot.Take();
        try
        {
            Clips.ClipboardHistory.Pause();
            if (!TrySet(text))
                return false;
            bool sent = await KeySender.SendAsync(target, [Paste]);
            // The app reads the clipboard after the keys arrive; give it time before putting the old one back.
            await Task.Delay(350);
            return sent;
        }
        finally
        {
            saved.Restore();
            Clips.ClipboardHistory.Resume();
        }
    }

    private static async Task<string> WithTimeout(Task<string> task, int milliseconds) =>
        await Task.WhenAny(task, Task.Delay(milliseconds)) == task ? task.Result : "";

    private static string TryRead()
    {
        for (int i = 0; i < 5; i++)
        {
            try
            {
                return Clipboard.ContainsText() ? Clipboard.GetText() : "";
            }
            catch (System.Runtime.InteropServices.ExternalException)
            {
                Thread.Sleep(40);
            }
        }
        return "";
    }

    internal static bool TrySet(string text)
    {
        for (int i = 0; i < 5; i++)
        {
            try
            {
                Clipboard.SetDataObject(new DataObject(DataFormats.UnicodeText, text), true);
                return true;
            }
            catch (System.Runtime.InteropServices.ExternalException)
            {
                Thread.Sleep(40);
            }
        }
        return false;
    }
}

/// <summary>What the clipboard held, copied out so it can be put back after Petal borrows it.</summary>
internal sealed class ClipboardSnapshot
{
    private readonly DataObject? _data;

    private ClipboardSnapshot(DataObject? data) => _data = data;

    public static ClipboardSnapshot Take()
    {
        try
        {
            var current = Clipboard.GetDataObject();
            if (current is null)
                return new ClipboardSnapshot(null);
            var copy = new DataObject();
            bool any = false;
            foreach (var format in current.GetFormats(false))
            {
                try
                {
                    if (current.GetData(format, false) is { } value)
                    {
                        copy.SetData(format, value, false);
                        any = true;
                    }
                }
                catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException or System.Runtime.InteropServices.COMException or OutOfMemoryException or InvalidOperationException)
                {
                    // Some formats can't be read back (delayed rendering by an app that closed); the rest are kept.
                }
            }
            return new ClipboardSnapshot(any ? copy : null);
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            return new ClipboardSnapshot(null);
        }
    }

    public void Restore()
    {
        for (int i = 0; i < 5; i++)
        {
            try
            {
                if (_data is null)
                    Clipboard.Clear();
                else
                    Clipboard.SetDataObject(_data, true);
                return;
            }
            catch (System.Runtime.InteropServices.ExternalException)
            {
                Thread.Sleep(40);
            }
        }
    }
}
