using Petal.App.Input;
using Petal.App.Localization;
using Petal.App.Screen;
using Petal.App.Settings;
using static Petal.App.Interop.NativeMethods;

namespace Petal.App.Words;

/// <summary>
/// Voice commands: press the shortcut (or the microphone in the command palette) and say what you want,
/// "record the screen", "خمس وعشرون دقيقة تركيز", "compress these". The words are heard on this PC by the
/// same speech model as dictation and open the command palette already searched, the best match ready for
/// Enter, so nothing runs that you didn't see first.
/// </summary>
internal static class VoiceCommand
{
    /// <summary>A command is short: listening stops by itself after this long.</summary>
    private static readonly TimeSpan Longest = TimeSpan.FromSeconds(10);

    /// <summary>The quiet after speech that ends a command.</summary>
    private static readonly TimeSpan Pause = TimeSpan.FromSeconds(1.3);

    private const double Speaking = 0.08;

    private static Listener? _listener;
    private static DictationPill? _pill;
    private static IntPtr _target;
    private static IDisposable? _escape;
    private static bool _hearing;
    private static Shell.GlobalHotkey? _hotkey;
    private static Action<string>? _into;

    public static bool IsListening => _listener is not null;

    /// <summary>Whether the shortcut is Petal's now (false when another program has it, or there is none).</summary>
    public static bool ShortcutHeld => _hotkey?.Current is not null;

    public static bool RegisterShortcut()
    {
        _hotkey ??= new Shell.GlobalHotkey();
        _hotkey.Pressed -= OnShortcut;
        _hotkey.Pressed += OnShortcut;
        var text = SettingsStore.Current.VoiceShortcut;
        return _hotkey.Register(string.IsNullOrWhiteSpace(text) ? null : Shortcut.Parse(text));
    }

    public static void Dispose() => _hotkey?.Dispose();

    private static void OnShortcut() => Toggle(GetForegroundWindow());

    /// <summary>
    /// Listens for a command for the window <paramref name="target"/>, or, when listening, stops. With
    /// <paramref name="into"/> the words go there (the open palette's search) instead of opening the palette.
    /// </summary>
    public static void Toggle(IntPtr target, Action<string>? into = null)
    {
        if (_hearing)
            return;
        if (_listener is not null)
        {
            _ = FinishAsync();
            return;
        }
        if (Dictation.IsListening)
            return;
        if (Listener.ReadyModel() is null)
        {
            DictationSetupWindow.ShowSingle();
            return;
        }
        if (!Listener.HasMicrophone)
        {
            Hud.HudCard.ShowAt(Hud.HudCard.CornerPoint(), "", "").Fail(L.T("Can't listen"), L.T("No microphone found"));
            return;
        }
        _target = target;
        _into = into;
        var listener = _listener = new Listener();
        try
        {
            listener.Start();
        }
        catch (Exception ex) when (ex is NAudio.MmException or InvalidOperationException)
        {
            listener.Dispose();
            _listener = null;
            Hud.HudCard.ShowAt(Hud.HudCard.CornerPoint(), "", "").Fail(L.T("Can't listen"), L.T("The microphone couldn't be opened."));
            return;
        }
        _pill = new DictationPill(target, "Done");
        _pill.Title = L.T("Voice Command");
        _pill.Show();
        DateTime? spoke = null, quietSince = null;
        listener.Level += level =>
        {
            // Speech, then a pause: the command is over.
            var now = DateTime.UtcNow;
            if (level >= Speaking)
            {
                spoke = now;
                quietSince = null;
            }
            else if (spoke is not null)
            {
                quietSince ??= now;
                if (now - quietSince.Value >= Pause)
                    System.Windows.Application.Current.Dispatcher.BeginInvoke(() => { if (_listener == listener) _ = FinishAsync(); });
            }
            _pill?.Dispatcher.BeginInvoke(() => _pill?.ShowLevel(level));
        };
        _pill.Stop += () => _ = FinishAsync();
        _pill.Cancel += Cancel;
        _escape = ScreenHooks.Escape(Cancel);
        _ = WatchLengthAsync(listener);
    }

    private static async Task WatchLengthAsync(Listener listener)
    {
        while (_listener == listener)
        {
            await Task.Delay(300);
            if (_listener == listener && listener.Length >= Longest)
                await FinishAsync();
        }
    }

    private static void Cancel()
    {
        _escape?.Dispose();
        _listener?.Dispose();
        _listener = null;
        _pill?.Close();
        _pill = null;
    }

    private static async Task FinishAsync()
    {
        if (_listener is not { } listener)
            return;
        _hearing = true;
        _escape?.Dispose();
        _listener = null;
        _pill?.Writing();
        string text = "";
        try
        {
            text = Words(await listener.FinishAsync());
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Error(ex, "Voice command");
            Hud.HudCard.ShowAt(Hud.HudCard.CornerPoint(), "", "").Fail(L.T("The command wasn't heard"), L.T("Something went wrong. The activity log has the details."));
        }
        finally
        {
            listener.Dispose();
            _pill?.Close();
            _pill = null;
            _hearing = false;
        }
        if (text.Length == 0)
            return;
        if (_into is { } into)
            into(text);
        else if (!Commands.CommandPalette.SearchFor(text))
            await Commands.CommandPalette.ToggleAsync(_target, text);
    }

    /// <summary>What was said as search words: no full stop or question mark at the end, no "please".</summary>
    internal static string Words(string heard)
    {
        var text = heard.Trim().TrimEnd('.', '!', '?', '؟', '،', ',', '。').Trim();
        foreach (var polite in new[] { "please ", "petal ", "من فضلك ", "لو سمحت " })
        {
            if (text.StartsWith(polite, StringComparison.OrdinalIgnoreCase))
                text = text[polite.Length..].TrimStart();
        }
        foreach (var polite in new[] { " please", " من فضلك", " لو سمحت" })
        {
            if (text.EndsWith(polite, StringComparison.OrdinalIgnoreCase))
                text = text[..^polite.Length].TrimEnd();
        }
        return text;
    }
}
