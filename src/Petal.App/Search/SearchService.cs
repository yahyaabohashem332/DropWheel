using System.IO;
using System.Runtime.InteropServices;
using Petal.App.Settings;
using Petal.Core.Jobs;
using Petal.Engines.Imaging;
using Petal.Recipes.Search;

namespace Petal.App.Search;

/// <summary>
/// Keeps the search index up to date: looks through the chosen folders every half hour, and reads
/// new or changed files only while nobody is using the PC (or for a while after "Index now"), on
/// a low-priority thread.
/// </summary>
internal static class SearchService
{
    private static readonly TimeSpan ScanEvery = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan IdleAfter = TimeSpan.FromSeconds(45);
    private static readonly AutoResetEvent Wake = new(false);
    private static readonly Lock Gate = new();
    private static SearchIndex? _index;
    private static SearchIndexer? _indexer;
    private static Thread? _thread;
    private static CancellationTokenSource? _stop;
    private static DateTime _lastScan = DateTime.MinValue;
    private static DateTime _busyUntil = DateTime.MinValue;

    /// <summary>Raised (on the indexing thread) when the counts change.</summary>
    public static event Action? Changed;

    public static bool Running => _thread is not null;

    public static IReadOnlyList<string> DefaultFolders()
    {
        string Known(Environment.SpecialFolder folder) => Environment.GetFolderPath(folder);
        var downloads = Path.Combine(Known(Environment.SpecialFolder.UserProfile), "Downloads");
        return new[] { Known(Environment.SpecialFolder.MyPictures), Known(Environment.SpecialFolder.MyDocuments), Known(Environment.SpecialFolder.DesktopDirectory), downloads, Known(Environment.SpecialFolder.MyVideos) }
            .Where(f => f.Length > 0 && Directory.Exists(f)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static IReadOnlyList<string> Folders => SettingsStore.Current.SearchFolders is { Count: > 0 } chosen ? chosen : DefaultFolders();

    public static IndexStatus Status()
    {
        lock (Gate)
        {
            if (_index is not null)
                return _index.Status();
        }
        if (!File.Exists(SearchIndex.DefaultPath))
            return default;
        using var index = new SearchIndex();
        return index.Status();
    }

    public static void Start(ConverterRegistry converters)
    {
        lock (Gate)
        {
            if (_thread is not null || !SettingsStore.Current.SearchEnabled)
                return;
            _index = new SearchIndex();
            _indexer = new SearchIndexer(_index, converters, SettingsStore.Current.Conversion);
            _stop = new CancellationTokenSource();
            _lastScan = DateTime.MinValue;
            Meaning.Threads = 2;
            _thread = new Thread(() => Loop(_stop.Token)) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "Petal search index" };
            _thread.Start();
        }
    }

    public static void Stop()
    {
        Thread? thread;
        lock (Gate)
        {
            thread = _thread;
            _stop?.Cancel();
            _thread = null;
        }
        Wake.Set();
        thread?.Join(TimeSpan.FromSeconds(5));
        lock (Gate)
        {
            _index?.Dispose();
            _index = null;
            _indexer = null;
        }
        Meaning.Release();
    }

    /// <summary>Reads waiting files now, for the next ten minutes, without waiting for the PC to be idle.</summary>
    public static void Hurry()
    {
        _busyUntil = DateTime.UtcNow.AddMinutes(10);
        Wake.Set();
    }

    /// <summary>Looks through the folders again at once (after they changed).</summary>
    public static void Rescan()
    {
        _lastScan = DateTime.MinValue;
        Wake.Set();
    }

    /// <summary>Forgets everything and reads all files again.</summary>
    public static void Rebuild()
    {
        lock (Gate)
            _index?.Reset();
        Rescan();
    }

    /// <summary>Once the meaning models are here, pictures and videos read without them are read again.</summary>
    public static void ReadPicturesAgain()
    {
        lock (Gate)
            _index?.ReadAgainWithoutMeaning();
        Wake.Set();
    }

    public static SearchResults Search(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return new SearchResults([], []);
        float[]? meaning = null;
        if (Meaning.IsReady)
        {
            try
            {
                meaning = Meaning.EmbedText(query);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Log.Error(ex, "Understanding a search");
            }
        }
        lock (Gate)
        {
            if (_index is not null)
                return _index.Search(query, meaning);
        }
        if (!File.Exists(SearchIndex.DefaultPath))
            return new SearchResults([], []);
        using var index = new SearchIndex();
        return index.Search(query, meaning);
    }

    /// <summary>The indexed files that mention the most of these words, with their words: for answering questions.</summary>
    public static IReadOnlyList<(string Path, string Body)> Mentions(IReadOnlyList<string> terms, int limit = 5)
    {
        lock (Gate)
        {
            if (_index is not null)
                return _index.Mentions(terms, limit);
        }
        if (!File.Exists(SearchIndex.DefaultPath))
            return [];
        using var index = new SearchIndex();
        return index.Mentions(terms, limit);
    }

    private static void Loop(CancellationToken stop)
    {
        int read = 0;
        while (!stop.IsCancellationRequested)
        {
            try
            {
                if (DateTime.UtcNow - _lastScan > ScanEvery)
                {
                    _indexer!.Scan(Folders, stop);
                    _lastScan = DateTime.UtcNow;
                    Changed?.Invoke();
                }
                if (Idle() || DateTime.UtcNow < _busyUntil)
                {
                    if (_indexer!.ReadNextAsync(stop).GetAwaiter().GetResult())
                    {
                        if (++read % 10 == 0)
                            Changed?.Invoke();
                        continue;
                    }
                    Changed?.Invoke();
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Log.Error(ex, "Updating the search index");
                Wake.WaitOne(TimeSpan.FromMinutes(1));
            }
            Wake.WaitOne(TimeSpan.FromSeconds(5));
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint Size;
        public uint Time;
    }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LastInputInfo info);

    /// <summary>No key pressed and the mouse unmoved for a while.</summary>
    private static bool Idle()
    {
        var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
        if (!GetLastInputInfo(ref info))
            return false;
        uint idle = unchecked((uint)Environment.TickCount - info.Time);
        return idle >= IdleAfter.TotalMilliseconds;
    }
}
