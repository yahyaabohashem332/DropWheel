using System.IO;
using System.Text.Json;

namespace Petal.App.Overlay;

/// <summary>
/// What the wheel remembers between uses: the petal chosen last on each page ("files:Conversions",
/// "ring:quick") and the wheel without files used last. Kept in its own small file beside the settings,
/// written a moment after a choice so a quick run of choices writes once.
/// </summary>
public static class WheelMemory
{
    private sealed class State
    {
        public Dictionary<string, string> LastPetal { get; set; } = [];
        public string? LastRing { get; set; }
        public WheelMode? LastFileMode { get; set; }
    }

    private static readonly Lock Gate = new();
    private static State? _state;
    private static Timer? _save;

    /// <summary>Where it is kept; tests point it elsewhere.</summary>
    public static string FilePath
    {
        get => _path ?? Path.Combine(Settings.SettingsStore.Directory, "wheel-memory.json");
        set
        {
            lock (Gate)
            {
                _path = value;
                _state = null;
            }
        }
    }

    private static string? _path;

    private static State Current
    {
        get
        {
            lock (Gate)
            {
                if (_state is null)
                {
                    try
                    {
                        _state = File.Exists(FilePath) ? JsonSerializer.Deserialize<State>(File.ReadAllText(FilePath)) : null;
                    }
                    catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
                    {
                        _state = null;
                    }
                    _state ??= new State();
                }
                return _state;
            }
        }
    }

    /// <summary>The key of the petal chosen last on a page, if any.</summary>
    public static string? LastPetal(string page)
    {
        var state = Current;
        lock (Gate)
            return state.LastPetal.GetValueOrDefault(page);
    }

    /// <summary>The index of that petal in what the page shows now, if it is still there.</summary>
    public static int? IndexIn(string page, WheelContent content)
    {
        if (LastPetal(page) is not { } key)
            return null;
        for (int i = 0; i < content.Items.Count; i++)
            if (content.Items[i].Key == key)
                return i;
        return null;
    }

    public static string? LastRing
    {
        get
        {
            var state = Current;
            lock (Gate)
                return state.LastRing;
        }
    }

    /// <summary>The file wheel chosen from last (convert, tools or actions), to open on it next time.</summary>
    public static WheelMode? LastFileMode
    {
        get
        {
            var state = Current;
            lock (Gate)
                return state.LastFileMode;
        }
        set
        {
            var state = Current;
            lock (Gate)
            {
                if (state.LastFileMode == value) return;
                state.LastFileMode = value;
                _save?.Dispose();
                _save = new Timer(_ => Save(), null, TimeSpan.FromSeconds(1), Timeout.InfiniteTimeSpan);
            }
        }
    }

    public static void Remember(string page, WheelItem item)
    {
        var state = Current;
        lock (Gate)
        {
            state.LastPetal[page] = item.Key;
            if (page.StartsWith("ring:", StringComparison.Ordinal))
                state.LastRing = page["ring:".Length..];
            _save?.Dispose();
            _save = new Timer(_ => Save(), null, TimeSpan.FromSeconds(1), Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Forgets everything, as when the user turns remembering off.</summary>
    public static void Clear()
    {
        lock (Gate)
        {
            _state = new State();
            _save?.Dispose();
            _save = null;
        }
        Save();
    }

    private static void Save()
    {
        string json;
        lock (Gate)
            json = JsonSerializer.Serialize(_state ?? new State());
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Memory is a convenience.
        }
    }
}
