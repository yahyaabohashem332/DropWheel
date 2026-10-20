using System.Windows.Input;

namespace Petal.App.Input;

/// <summary>
/// A system-wide keyboard shortcut such as Ctrl + Win + Z, stored in settings as "Ctrl+Win+Z".
/// </summary>
public sealed record Shortcut(ModifierKeys Modifiers, Key Key)
{
    /// <summary>
    /// Ctrl + Win + Z: three neighbouring keys, pressed with one hand. Windows leaves it free, and
    /// apps rarely use Windows-key combinations. The obvious alternatives are taken: Ctrl + Win +
    /// Enter (Narrator), Ctrl + Win + Space (input language), Win + Alt + Space (Command Palette),
    /// Ctrl + Alt + Space (often claimed by launcher apps).
    /// </summary>
    public static Shortcut Default { get; } = new(ModifierKeys.Control | ModifierKeys.Windows, Key.Z);

    private static readonly (ModifierKeys Modifier, string Name)[] Names =
    [
        (ModifierKeys.Control, "Ctrl"),
        (ModifierKeys.Alt, "Alt"),
        (ModifierKeys.Shift, "Shift"),
        (ModifierKeys.Windows, "Win"),
    ];

    /// <summary>
    /// A usable shortcut has Ctrl, Alt or Windows (Shift alone would swallow typed capitals)
    /// and one ordinary key.
    /// </summary>
    public bool IsValid =>
        (Modifiers & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows)) != 0 && !IsModifier(Key) && Key != Key.None;

    public static bool IsModifier(Key key) => key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
        or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.System;

    /// <summary>"Ctrl+Win+Z", the form kept in settings.</summary>
    public override string ToString() =>
        string.Join("+", Names.Where(n => Modifiers.HasFlag(n.Modifier)).Select(n => n.Name).Append(KeyName(Key)));

    /// <summary>"Ctrl + Win + Z", the form shown on the settings button.</summary>
    public string Display => ToString().Replace("+", " + ");

    public static Shortcut? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var modifiers = ModifierKeys.None;
        Key key = Key.None;
        foreach (var part in text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var named = Names.FirstOrDefault(n => n.Name.Equals(part, StringComparison.OrdinalIgnoreCase));
            if (named.Name is not null)
                modifiers |= named.Modifier;
            else if (part.Length == 1 && char.IsDigit(part[0]))
                key = Key.D0 + (part[0] - '0');
            else if (part == "`")
                key = Key.Oem3;
            else if (part.Equals("Backspace", StringComparison.OrdinalIgnoreCase))
                key = Key.Back;
            else if (Enum.TryParse(part, ignoreCase: true, out Key parsed))
                key = parsed;
            else
                return null;
        }
        var shortcut = new Shortcut(modifiers, key);
        return shortcut.IsValid ? shortcut : null;
    }

    private static string KeyName(Key key) => key switch
    {
        >= Key.D0 and <= Key.D9 => ((char)('0' + (key - Key.D0))).ToString(),
        Key.Return => "Enter",
        Key.Back => "Backspace",
        Key.Next => "PageDown",
        Key.Prior => "PageUp",
        Key.Oem3 => "`",
        _ => key.ToString(),
    };
}
