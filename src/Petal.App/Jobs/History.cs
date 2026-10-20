using System.IO;
using System.Text.Json;
using Microsoft.VisualBasic.FileIO;
using Petal.Core.Actions;

namespace Petal.App.Jobs;

/// <param name="Title">What happened, as the progress card said it ("Converted to PNG").</param>
/// <param name="Renames">For a rename: each file's old and new path, so it can be put back.</param>
public sealed record HistoryEntry(
    Guid Id,
    DateTime Time,
    string Title,
    IReadOnlyList<string> Inputs,
    IReadOnlyList<string> Outputs,
    IReadOnlyList<RenamedPath>? Renames = null,
    bool Undone = false);

public sealed record RenamedPath(string From, string To);

/// <summary>
/// The last few hundred things Petal made, kept on this PC (%APPDATA%\Petal\history.json) so a
/// result can be found again or taken back. Undoing sends results to the Recycle Bin.
/// </summary>
public static class History
{
    private const int Limit = 300;
    private static readonly Lock Gate = new();
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };
    private static List<HistoryEntry>? _entries;

    /// <summary>Where the history lives (overridable for tests).</summary>
    public static string FilePath { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Petal", "history.json");

    /// <summary>False when the user turned the history off in Settings.</summary>
    public static Func<bool> Enabled { get; set; } = () => true;

    public static event Action? Changed;

    /// <summary>Newest first.</summary>
    public static IReadOnlyList<HistoryEntry> Entries
    {
        get
        {
            lock (Gate)
                return [.. Load()];
        }
    }

    public static void Add(string title, IReadOnlyList<string> inputs, IReadOnlyList<string> outputs, IReadOnlyList<RenamedPath>? renames = null)
    {
        if (!Enabled() || outputs.Count == 0 && renames is not { Count: > 0 })
            return;
        lock (Gate)
        {
            var entries = Load();
            entries.Insert(0, new HistoryEntry(Guid.NewGuid(), DateTime.Now, title, inputs, outputs, renames));
            if (entries.Count > Limit)
                entries.RemoveRange(Limit, entries.Count - Limit);
            Save(entries);
        }
        Changed?.Invoke();
    }

    public static void Clear()
    {
        lock (Gate)
        {
            _entries = [];
            Save(_entries);
        }
        Changed?.Invoke();
    }

    /// <summary>
    /// Takes an entry back: its results go to the Recycle Bin (so even this can be undone), or a
    /// rename is reversed. Returns how many files were dealt with.
    /// </summary>
    public static int Undo(Guid id)
    {
        HistoryEntry? entry;
        lock (Gate)
            entry = Load().FirstOrDefault(e => e.Id == id);
        if (entry is null || entry.Undone)
            return 0;

        int count = 0;
        if (entry.Renames is { Count: > 0 } renames)
        {
            count = BatchRename.Undo(renames.Select(r => (r.From, r.To)).ToList()).Count;
        }
        else
        {
            foreach (var path in entry.Outputs)
            {
                try
                {
                    if (File.Exists(path))
                        FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
                    else if (Directory.Exists(path))
                        FileSystem.DeleteDirectory(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
                    else
                        continue;
                    count++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
                {
                    Log.Error(ex, $"Undoing {path}");
                }
            }
        }
        lock (Gate)
        {
            var entries = Load();
            int i = entries.FindIndex(e => e.Id == id);
            if (i >= 0)
                entries[i] = entries[i] with { Undone = true };
            Save(entries);
        }
        Changed?.Invoke();
        return count;
    }

    /// <summary>The results of an entry that are still where Petal put them.</summary>
    public static IReadOnlyList<string> Existing(HistoryEntry entry) =>
        (entry.Renames is { Count: > 0 } r ? r.Select(x => x.To) : entry.Outputs).Where(p => File.Exists(p) || Directory.Exists(p)).ToList();

    private static List<HistoryEntry> Load()
    {
        if (_entries is not null)
            return _entries;
        try
        {
            _entries = File.Exists(FilePath) ? JsonSerializer.Deserialize<List<HistoryEntry>>(File.ReadAllText(FilePath), Json) ?? [] : [];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Log.Error(ex, "Reading the history");
            _entries = [];
        }
        return _entries;
    }

    private static void Save(List<HistoryEntry> entries)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(entries, Json));
            File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error(ex, "Saving the history");
        }
    }

    /// <summary>Forgets what was loaded, so the next read comes from disk (tests).</summary>
    public static void Reload()
    {
        lock (Gate)
            _entries = null;
    }
}
