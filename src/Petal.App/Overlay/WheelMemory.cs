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

        /// <summary>How often each petal was chosen, by kind of file and wheel ("jpg|Conversions").</summary>
        public Dictionary<string, Dictionary<string, int>> Usual { get; set; } = [];
    }

    /// <summary>Choices before a petal counts as the usual one for a kind of file.</summary>
    public const int UsualAfter = 3;

    /// <summary>"jpg", "jpg,png": the kinds of file in a selection, or null for folders and mixed bags.</summary>
    public static string? KindOf(IReadOnlyList<string> files)
    {
        if (files.Count == 0 || files.Any(Directory.Exists))
            return null;
        var kinds = files.Select(f => Path.GetExtension(f).TrimStart('.').ToLowerInvariant()).Where(e => e.Length > 0).Distinct().Order().ToList();
        return kinds.Count is 0 or > 3 ? null : string.Join(",", kinds);
    }

    /// <summary>Counts a choice towards the usual petal for a kind of file on a wheel.</summary>
    public static void CountChoice(string kind, string page, string key)
    {
        var state = Current;
        lock (Gate)
        {
            var slot = kind + "|" + page;
            if (!state.Usual.TryGetValue(slot, out var counts))
                state.Usual[slot] = counts = [];
            counts[key] = counts.GetValueOrDefault(key) + 1;
            // Only the top few are worth keeping.
            if (counts.Count > 12)
                foreach (var drop in counts.OrderBy(c => c.Value).Take(counts.Count - 12).Select(c => c.Key).ToList())
                    counts.Remove(drop);
            _save?.Dispose();
            _save = new Timer(_ => Save(), null, TimeSpan.FromSeconds(1), Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>The petal chosen most for this kind of file on this wheel, once it has been chosen a few times.</summary>
    public static string? UsualFor(string kind, string page)
    {
        var state = Current;
        lock (Gate)
        {
            if (!state.Usual.TryGetValue(kind + "|" + page, out var counts) || counts.Count == 0)
                return null;
            var best = counts.MaxBy(c => c.Value);
            return best.Value >= UsualAfter ? best.Key : null;
        }
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
