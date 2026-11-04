using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Petal.Core.Time;

/// <summary>Time spent in one program over some days, and what its windows were called when that is kept.</summary>
public sealed record TimeEntry(string App, long Seconds, IReadOnlyDictionary<string, long> Titles);

/// <summary>
/// Where the time went, day by day, on this PC only: one small file a day, holding how many seconds each program was in
/// front. Nothing else about what was done is kept unless the user asks for window titles too, and nothing is ever sent.
/// </summary>
public sealed class TimeLog(string folder)
{
    private sealed class Day
    {
        public Dictionary<string, long> Apps { get; set; } = [];

        public Dictionary<string, Dictionary<string, long>> Titles { get; set; } = [];
    }

    private readonly Dictionary<DateOnly, Day> _open = [];
    private readonly HashSet<DateOnly> _changed = [];
    private readonly Lock _gate = new();

    public string Folder { get; } = folder;

    /// <summary>The most different titles kept for a program in a day: a browser would otherwise make the file grow without end.</summary>
    private const int MostTitles = 200;

    private string PathOf(DateOnly date) => Path.Combine(Folder, date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".json");

    private Day Get(DateOnly date)
    {
        if (_open.TryGetValue(date, out var day))
            return day;
        day = new Day();
        try
        {
            if (File.Exists(PathOf(date)))
                day = JsonSerializer.Deserialize<Day>(File.ReadAllText(PathOf(date))) ?? day;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // A damaged day starts again; the others are untouched.
        }
        _open[date] = day;
        return day;
    }

    /// <summary>Counts some seconds for a program, and for a title of it when one is given.</summary>
    public void Add(DateOnly date, string app, long seconds, string? title = null)
    {
        if (seconds <= 0 || string.IsNullOrWhiteSpace(app))
            return;
        lock (_gate)
        {
            var day = Get(date);
            day.Apps[app] = day.Apps.GetValueOrDefault(app) + seconds;
            if (!string.IsNullOrWhiteSpace(title))
            {
                if (!day.Titles.TryGetValue(app, out var titles))
                    day.Titles[app] = titles = [];
                if (titles.ContainsKey(title) || titles.Count < MostTitles)
                    titles[title] = titles.GetValueOrDefault(title) + seconds;
            }
            _changed.Add(date);
        }
    }

    /// <summary>Writes what has changed. Written whole and swapped in, so a crash can't leave half a day.</summary>
    public void Save()
    {
        lock (_gate)
        {
            if (_changed.Count == 0)
                return;
            Directory.CreateDirectory(Folder);
            foreach (var date in _changed)
            {
                var path = PathOf(date);
                var temporary = path + ".tmp";
                File.WriteAllText(temporary, JsonSerializer.Serialize(_open[date]), new UTF8Encoding(false));
                File.Move(temporary, path, true);
            }
            _changed.Clear();
            // Past days needn't stay in memory.
            var today = DateOnly.FromDateTime(DateTime.Now);
            foreach (var old in _open.Keys.Where(d => d < today).ToList())
                _open.Remove(old);
        }
    }

    /// <summary>The programs from one day to another, most time first.</summary>
    public IReadOnlyList<TimeEntry> Range(DateOnly from, DateOnly to)
    {
        var apps = new Dictionary<string, long>();
        var titles = new Dictionary<string, Dictionary<string, long>>();
        lock (_gate)
        {
            for (var date = from; date <= to; date = date.AddDays(1))
            {
                if (!_open.ContainsKey(date) && !File.Exists(PathOf(date)))
                    continue;
                var day = Get(date);
                foreach (var (app, seconds) in day.Apps)
                    apps[app] = apps.GetValueOrDefault(app) + seconds;
                foreach (var (app, list) in day.Titles)
                {
                    if (!titles.TryGetValue(app, out var all))
                        titles[app] = all = [];
                    foreach (var (title, seconds) in list)
                        all[title] = all.GetValueOrDefault(title) + seconds;
                }
                if (!_changed.Contains(date) && date < DateOnly.FromDateTime(DateTime.Now))
                    _open.Remove(date);
            }
        }
        return apps.OrderByDescending(a => a.Value).ThenBy(a => a.Key, StringComparer.CurrentCulture)
            .Select(a => new TimeEntry(a.Key, a.Value, titles.TryGetValue(a.Key, out var t) ? t : new Dictionary<string, long>()))
            .ToList();
    }

    /// <summary>A day per row and a program per row within it, for a spreadsheet.</summary>
    public string Csv(DateOnly from, DateOnly to, string dateHeading = "Date", string appHeading = "Program", string minutesHeading = "Minutes")
    {
        static string Cell(string text) => text.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + text.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : Safe(text);
        // A name starting like a formula stays text when a spreadsheet opens it.
        static string Safe(string text) => text.Length > 0 && text[0] is '=' or '+' or '-' or '@' ? "'" + text : text;
        var sb = new StringBuilder();
        sb.Append(Cell(dateHeading)).Append(',').Append(Cell(appHeading)).Append(',').Append(Cell(minutesHeading)).Append("\r\n");
        for (var date = from; date <= to; date = date.AddDays(1))
            foreach (var entry in Range(date, date))
                sb.Append(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append(',').Append(Cell(Safe(entry.App))).Append(',')
                    .Append((entry.Seconds / 60.0).ToString("0.#", CultureInfo.InvariantCulture)).Append("\r\n");
        return sb.ToString();
    }

    /// <summary>Forgets days older than this many, so what is kept stays what is useful.</summary>
    public void Trim(int days, DateOnly today)
    {
        if (days <= 0 || !Directory.Exists(Folder))
            return;
        lock (_gate)
        {
            foreach (var file in Directory.EnumerateFiles(Folder, "*.json"))
                if (DateOnly.TryParseExact(Path.GetFileNameWithoutExtension(file), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) && date < today.AddDays(-days))
                {
                    try { File.Delete(file); } catch (IOException) { }
                }
        }
    }

    /// <summary>Forgets everything.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _open.Clear();
            _changed.Clear();
            if (!Directory.Exists(Folder))
                return;
            foreach (var file in Directory.EnumerateFiles(Folder, "*.json"))
                try { File.Delete(file); } catch (IOException) { }
        }
    }
}
