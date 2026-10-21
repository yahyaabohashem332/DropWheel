using System.IO;
using System.Text.Json;

namespace Petal.App.Stats;

/// <summary>One day's counts.</summary>
public sealed class DayCount
{
    public int Uses { get; set; }
    public int Files { get; set; }
}

/// <summary>Everything counted, as kept in the file.</summary>
public sealed class StatsData
{
    public DateOnly Since { get; set; } = DateOnly.FromDateTime(DateTime.Now);
    public int Uses { get; set; }
    public int Files { get; set; }
    public long BytesIn { get; set; }
    public long BytesOut { get; set; }

    /// <summary>Bytes saved by results smaller than their originals (compressing, converting to a smaller format).</summary>
    public long Saved { get; set; }

    public double Seconds { get; set; }
    public Dictionary<string, int> Petals { get; set; } = [];
    public Dictionary<string, string> Names { get; set; } = [];
    /// <summary>By day, "2026-10-21".</summary>
    public SortedDictionary<string, DayCount> Days { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>
/// What Petal has done, counted on this PC only and never sent anywhere: petals used, files handled, space
/// saved, and the days Petal was used. It can be paused and wiped from Settings.
/// </summary>
public static class Statistics
{
    private static readonly Lock Gate = new();
    private static StatsData? _data;
    private static Timer? _save;

    /// <summary>Where it is kept; tests point it elsewhere.</summary>
    public static string FilePath { get; set; } = Path.Combine(Settings.SettingsStore.Directory, "statistics.json");

    /// <summary>Whether counting is on; Settings turns it off.</summary>
    public static Func<bool> Enabled { get; set; } = () => Settings.SettingsStore.Current.KeepStatistics;

    public static event Action? Changed;

    private static StatsData Data
    {
        get
        {
            lock (Gate)
            {
                if (_data is null)
                {
                    try
                    {
                        _data = File.Exists(FilePath) ? JsonSerializer.Deserialize<StatsData>(File.ReadAllText(FilePath)) : null;
                    }
                    catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
                    {
                        _data = null;
                    }
                    _data ??= new StatsData();
                }
                return _data;
            }
        }
    }

    /// <summary>A copy of the counts, for showing.</summary>
    public static StatsData Snapshot()
    {
        var data = Data;
        lock (Gate)
            return JsonSerializer.Deserialize<StatsData>(JsonSerializer.Serialize(data))!;
    }

    /// <summary>A petal was used, on <paramref name="files"/> files.</summary>
    public static void RecordPetal(string key, int files, long bytesIn, long bytesOut, string? name = null)
    {
        if (!Enabled())
            return;
        var data = Data;
        lock (Gate)
        {
            data.Uses++;
            data.Petals[key] = data.Petals.GetValueOrDefault(key) + 1;
            if (name is not null)
                data.Names[key] = name;
            Today(data).Uses++;
        }
        RecordWork(files, bytesIn, bytesOut, 0);
    }

    /// <summary>Files handled by a finished job, their sizes before and after, and the time it took.</summary>
    public static void RecordWork(int files, long bytesIn, long bytesOut, double seconds)
    {
        if (!Enabled() || files <= 0 && seconds <= 0)
        {
            ScheduleSave();
            return;
        }
        var data = Data;
        lock (Gate)
        {
            data.Files += files;
            data.BytesIn += bytesIn;
            data.BytesOut += bytesOut;
            if (bytesOut > 0 && bytesIn > bytesOut)
                data.Saved += bytesIn - bytesOut;
            data.Seconds += seconds;
            Today(data).Files += files;
        }
        ScheduleSave();
    }

    private static DayCount Today(StatsData data)
    {
        var day = Key(DateOnly.FromDateTime(DateTime.Now));
        if (!data.Days.TryGetValue(day, out var count))
            data.Days[day] = count = new DayCount();
        return count;
    }

    public static string Key(DateOnly day) => day.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    public static DateOnly Day(string key) => DateOnly.ParseExact(key, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    private static bool Active(StatsData data, DateOnly day) => data.Days.TryGetValue(Key(day), out var count) && count.Uses + count.Files > 0;

    /// <summary>Days in a row with something done, up to today (or yesterday, when today has nothing yet).</summary>
    public static int Streak(StatsData data, DateOnly? today = null)
    {
        var day = today ?? DateOnly.FromDateTime(DateTime.Now);
        if (!Active(data, day))
            day = day.AddDays(-1);
        int streak = 0;
        while (Active(data, day))
        {
            streak++;
            day = day.AddDays(-1);
        }
        return streak;
    }

    /// <summary>The longest run of days in a row ever.</summary>
    public static int LongestStreak(StatsData data)
    {
        int best = 0, run = 0;
        DateOnly? previous = null;
        foreach (var (key, count) in data.Days)
        {
            if (count.Uses + count.Files == 0) continue;
            var day = Day(key);
            run = previous is { } p && p.AddDays(1) == day ? run + 1 : 1;
            best = Math.Max(best, run);
            previous = day;
        }
        return best;
    }

    /// <summary>Milestones reached: 10, 100, 1,000… files; 100 MB, 1 GB… saved; 7, 30 days in a row.</summary>
    public static IReadOnlyList<string> Milestones(StatsData data)
    {
        var reached = new List<string>();
        foreach (var n in new[] { 10, 100, 1000, 10000 })
            if (data.Files >= n) reached.Add($"files:{n}");
        foreach (var mb in new long[] { 100, 1024, 10240 })
            if (data.Saved >= mb * 1024 * 1024) reached.Add($"saved:{mb}");
        foreach (var days in new[] { 7, 30, 100 })
            if (LongestStreak(data) >= days) reached.Add($"streak:{days}");
        return reached;
    }

    /// <summary>Forgets everything counted so far.</summary>
    public static void Reset()
    {
        lock (Gate)
            _data = new StatsData();
        Save();
        Changed?.Invoke();
    }

    /// <summary>Reads the file again (after an import or a test).</summary>
    public static void Reload()
    {
        lock (Gate)
            _data = null;
        Changed?.Invoke();
    }

    private static void ScheduleSave()
    {
        lock (Gate)
        {
            _save?.Dispose();
            _save = new Timer(_ => { Save(); Changed?.Invoke(); }, null, TimeSpan.FromSeconds(2), Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Writes the counts now (tests and quitting).</summary>
    public static void Save()
    {
        string json;
        lock (Gate)
            json = JsonSerializer.Serialize(_data ?? new StatsData());
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Counting is a convenience.
        }
    }
}
