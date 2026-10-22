using System.Text.Json.Serialization;

namespace Petal.App.Wheels;

/// <summary>What a petal of the user's wheels does.</summary>
public enum EntryKind
{
    /// <summary>One of Petal's built-in actions: dark mode, mute, a screenshot, play or pause…</summary>
    Action,
    /// <summary>Opens a program, or brings it to the front when it is already open.</summary>
    App,
    /// <summary>Presses keys in the app that was in front.</summary>
    Keystroke,
    /// <summary>Runs a PowerShell or Command Prompt script.</summary>
    Script,
    /// <summary>Opens a folder.</summary>
    Folder,
    /// <summary>Petals of its own, opened around it.</summary>
    Group,
    /// <summary>The windows that are open, to switch to one.</summary>
    RunningApps,
    /// <summary>Opens a web address or a link such as ms-settings:.</summary>
    Link,
    /// <summary>What was copied lately: each clip a petal, to paste in the app in front.</summary>
    Clips,
    /// <summary>One saved workspace (<see cref="WheelEntry.Path"/> is its id): its apps opened and arranged.</summary>
    Workspace,
    /// <summary>Every saved workspace around it, and saving the windows open now as one.</summary>
    Workspaces,
}

public enum ScriptShell
{
    PowerShell,
    Cmd,
}

/// <summary>What a script is given to work on, as the PETAL_INPUT variable and on its standard input.</summary>
public enum ScriptInput
{
    None,
    Clipboard,
    SelectedText,
    SelectedFiles,
}

/// <summary>One petal of a wheel the user can change.</summary>
public sealed class WheelEntry
{
    public string Id { get; set; } = NewId();
    public EntryKind Kind { get; set; }

    /// <summary>The user's own name for it; null uses the usual name of what it does.</summary>
    public string? Name { get; set; }

    /// <summary>The short text on the petal ("NEW TAB"); null uses the name.</summary>
    public string? Label { get; set; }

    /// <summary>A line icon's key; null uses the usual one.</summary>
    public string? Icon { get; set; }

    /// <summary>"#RRGGBB", or null for the wheel's own colours.</summary>
    public string? Tint { get; set; }

    /// <summary>For <see cref="EntryKind.Action"/>: which built-in action ("system.darkmode").</summary>
    public string? Action { get; set; }

    /// <summary>A program, shortcut, folder or address.</summary>
    public string? Path { get; set; }

    public string? Arguments { get; set; }

    /// <summary>For <see cref="EntryKind.Keystroke"/>: "Ctrl+Shift+T", or several presses separated by spaces ("Ctrl+K Ctrl+S").</summary>
    public string? Keys { get; set; }

    public string? Script { get; set; }
    public ScriptShell Shell { get; set; }
    public ScriptInput Input { get; set; }

    /// <summary>Show what the script printed on a card when it finishes.</summary>
    public bool ShowOutput { get; set; } = true;

    /// <summary>For <see cref="EntryKind.Group"/>: up to eight petals.</summary>
    public List<WheelEntry>? Children { get; set; }

    public static string NewId() => Guid.NewGuid().ToString("N")[..10];

    /// <summary>A copy with new ids throughout, for templates and duplicates.</summary>
    public WheelEntry Clone()
    {
        var copy = (WheelEntry)MemberwiseClone();
        copy.Id = NewId();
        copy.Children = Children?.Select(c => c.Clone()).ToList();
        return copy;
    }
}

public enum WheelKind
{
    /// <summary>Made by the user.</summary>
    Custom,
    /// <summary>Quick actions for Windows.</summary>
    Quick,
    /// <summary>The music or video playing.</summary>
    Media,
    /// <summary>Shown first while one of its apps is in front.</summary>
    App,
}

/// <summary>A wheel that needs no files: opened with the shortcut when nothing is selected, or with its own.</summary>
public sealed class UserWheel
{
    public string Id { get; set; } = WheelEntry.NewId();
    public WheelKind Kind { get; set; }
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;

    /// <summary>For <see cref="WheelKind.App"/>: the programs it belongs to, by file name without ".exe" ("chrome", "msedge").</summary>
    public List<string> Apps { get; set; } = [];

    /// <summary>The ready-made wheel it started from, so it can be put back as it was.</summary>
    public string? Template { get; set; }

    /// <summary>A shortcut of its own ("Ctrl+Alt+M") that opens straight onto it; null for none.</summary>
    public string? Shortcut { get; set; }

    public List<WheelEntry> Items { get; set; } = [];
}

/// <summary>What the quick shortcut does when nothing is selected in File Explorer.</summary>
public enum NothingSelected
{
    /// <summary>The wheels without files: the app in front, quick actions, media, the user's own.</summary>
    Wheels,
    /// <summary>The wheel for what is on the clipboard.</summary>
    Clipboard,
    /// <summary>Petal's settings.</summary>
    Petal,
}

/// <summary>A mouse button that opens the wheel where the pointer is.</summary>
public enum MouseTrigger
{
    None,
    /// <summary>The side button usually meaning Back.</summary>
    Back,
    /// <summary>The side button usually meaning Forward.</summary>
    Forward,
}

/// <summary>A window that is open, for the running-apps petal.</summary>
public sealed record RunningWindow(IntPtr Handle, string Title, string Program, string? ExePath)
{
    [JsonIgnore] public System.Windows.Media.ImageSource? Icon { get; init; }
}

/// <summary>A folder on the actions wheel: files dropped on it are copied or moved there.</summary>
public sealed class FolderDestination
{
    public string Id { get; set; } = WheelEntry.NewId();
    public string Folder { get; set; } = "";
    public string? Name { get; set; }
    public bool Move { get; set; }
    public string? Tint { get; set; }
}
