using System.IO;
using System.Windows.Threading;
using Petal.App.Hud;
using Petal.App.Settings;
using Petal.Core.Formats;
using Petal.Core.Jobs;

namespace Petal.App.Jobs;

/// <summary>
/// Runs the automations from Settings: watches each folder and converts files that arrive in it
/// once they have finished arriving. Files that land together are handled on one progress card.
/// </summary>
public sealed class FolderWatcher : IDisposable
{
    private static readonly TimeSpan Poll = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan GiveUp = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan Gather = TimeSpan.FromSeconds(1.5);

    private readonly JobRunner _jobs;
    private readonly Dispatcher _ui;
    private readonly Action<WatchRule, IReadOnlyList<string>> _run;
    private readonly List<Watch> _watches = [];
    private readonly HashSet<string> _waiting = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource _stop = new();

    /// <param name="run">What to do with each gathered batch; by default it is converted on a progress card.</param>
    public FolderWatcher(JobRunner jobs, Dispatcher ui, Action<WatchRule, IReadOnlyList<string>>? run = null)
    {
        _jobs = jobs;
        _ui = ui;
        _run = run ?? Run;
    }

    /// <summary>Starts watching the enabled rules, replacing whatever was watched before.</summary>
    public void Apply(IReadOnlyList<WatchRule> rules)
    {
        StopAll();
        _stop = new CancellationTokenSource();
        foreach (var rule in rules.Where(r => r.Enabled))
        {
            if (!Directory.Exists(rule.Folder))
            {
                Log.Info($"Automation skipped, folder not found: {rule.Folder}");
                continue;
            }
            try
            {
                _watches.Add(new Watch(this, rule.Copy()));
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
            {
                Log.Error(ex, $"Watching {rule.Folder}");
            }
        }
    }

    private void Arrived(Watch watch, string path)
    {
        if (!watch.Rule.Wants(path))
            return;
        lock (_waiting)
        {
            if (!_waiting.Add(path))
                return;
        }
        var stop = _stop.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                if (await WaitUntilCompleteAsync(path, Poll, GiveUp, stop) && Fits(watch.Rule, path))
                    _ = _ui.BeginInvoke(() => watch.Add(path));
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                lock (_waiting)
                    _waiting.Remove(path);
            }
        }, stop);
    }

    /// <summary>The rule's action can handle this file (a format the favourite or conversion takes).</summary>
    private bool Fits(WatchRule rule, string path)
    {
        if (rule.Action == WatchAction.Organize)
            return true;
        if (Formats.FromPath(path) is not { } from)
            return false;
        return rule.Action switch
        {
            WatchAction.Favourite => FavouriteOf(rule)?.ConverterFor(from, _jobs.Converters) is not null,
            WatchAction.Recipe => RecipeOf(rule)?.AppliesTo([from], _jobs.Converters) == true,
            _ => Formats.FromId(rule.Target) is { } target && _jobs.Converters.Find(from, target) is not null,
        };
    }

    private static Petal.Recipes.Recipe? RecipeOf(WatchRule rule) =>
        Guid.TryParse(rule.Target, out var id) ? SettingsStore.Current.Recipes.FirstOrDefault(r => r.Id == id) : null;

    private static Favourite? FavouriteOf(WatchRule rule) =>
        Guid.TryParse(rule.Target, out var id) ? SettingsStore.Current.Favourites.FirstOrDefault(f => f.Id == id) : null;

    private void Run(WatchRule rule, IReadOnlyList<string> files)
    {
        var existing = files.Where(File.Exists).ToList();
        if (existing.Count == 0)
            return;
        if (rule.Action == WatchAction.Organize)
        {
            _ = OrganizeAsync(rule, existing);
            return;
        }
        try
        {
            Directory.CreateDirectory(rule.OutputFolder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error(ex, $"Creating {rule.OutputFolder}");
            return;
        }
        var at = HudCard.CornerPoint();
        if (rule.Action == WatchAction.Recipe)
        {
            if (RecipeOf(rule) is { } recipe)
                _ = _jobs.RunRecipeAsync(existing, recipe, at, rule.OutputFolder);
            return;
        }
        _ = rule.Action == WatchAction.Favourite
            ? FavouriteOf(rule) is { } favourite ? _jobs.ConvertAsync(existing, favourite, at, rule.OutputFolder) : Task.CompletedTask
            : Formats.FromId(rule.Target) is { } target ? _jobs.ConvertAsync(existing, target, at, rule.OutputFolder) : Task.CompletedTask;
    }

    /// <summary>
    /// Sorts the files into folders, then says so on a card. Moves are kept in History as renames,
    /// so Undo puts every file back where it arrived.
    /// </summary>
    private async Task OrganizeAsync(WatchRule rule, IReadOnlyList<string> files)
    {
        var options = new Petal.Recipes.OrganizeOptions
        {
            ByKind = rule.SortByKind,
            Screenshots = rule.SortScreenshots,
            Invoices = rule.SortInvoices,
            RenameByDate = rule.RenameByDate,
        };
        var moves = new List<RenamedPath>();
        foreach (var file in files)
        {
            try
            {
                var moved = await Task.Run(() => Petal.Recipes.Organizer.OrganizeAsync(file, rule.Folder, options, _jobs.Converters, SettingsStore.Current.Conversion));
                if (moved is not null)
                    moves.Add(new RenamedPath(file, moved));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Error(ex, $"Sorting {file}");
            }
        }
        if (moves.Count == 0)
            return;
        var title = Localization.L.P(moves.Count, "Sorted {0} file", "Sorted {0} files");
        History.Add(title, moves.Select(m => m.From).ToList(), [], moves);
        var folders = moves.Select(m => Path.GetFileName(Path.GetDirectoryName(m.To)!)).Distinct().ToList();
        var card = HudCard.ShowAt(HudCard.CornerPoint(), title, "");
        card.Succeed(title, string.Join(", ", folders), () => Shell.ExplorerSelection.Reveal(moves.Select(m => m.To).ToList()));
    }

    /// <summary>
    /// Waits until a file stops growing and nobody is writing it (a copy or download has finished).
    /// False when it disappeared or never settled.
    /// </summary>
    public static async Task<bool> WaitUntilCompleteAsync(string path, TimeSpan poll, TimeSpan giveUp, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + giveUp;
        (long, DateTime)? last = null;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(poll, cancellationToken);
            var info = new FileInfo(path);
            if (!info.Exists)
                return false;
            var now = (info.Length, info.LastWriteTimeUtc);
            if (now == last && info.Length > 0 && CanOpenAlone(path))
                return true;
            last = now;
        }
        return false;
    }

    private static bool CanOpenAlone(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private void StopAll()
    {
        _stop.Cancel();
        foreach (var watch in _watches)
            watch.Dispose();
        _watches.Clear();
    }

    public void Dispose() => StopAll();

    /// <summary>One folder: its system watcher and the files gathered for the next card.</summary>
    private sealed class Watch : IDisposable
    {
        private readonly FolderWatcher _owner;
        private readonly FileSystemWatcher _watcher;
        private readonly List<string> _batch = [];
        private readonly DispatcherTimer _timer;

        public Watch(FolderWatcher owner, WatchRule rule)
        {
            _owner = owner;
            Rule = rule;
            _timer = new DispatcherTimer(Gather, DispatcherPriority.Background, (_, _) => Flush(), owner._ui);
            _timer.Stop();
            _watcher = new FileSystemWatcher(rule.Folder)
            {
                IncludeSubdirectories = rule.IncludeSubfolders,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite,
                InternalBufferSize = 64 * 1024,
            };
            _watcher.Created += (_, e) => owner.Arrived(this, e.FullPath);
            _watcher.Renamed += (_, e) => owner.Arrived(this, e.FullPath);
            _watcher.Error += (_, e) => Log.Error(e.GetException(), $"Watching {rule.Folder}");
            _watcher.EnableRaisingEvents = true;
        }

        public WatchRule Rule { get; }

        /// <summary>On the UI thread: gathers files that arrive close together.</summary>
        public void Add(string path)
        {
            if (_disposed)
                return;
            if (!_batch.Contains(path, StringComparer.OrdinalIgnoreCase))
                _batch.Add(path);
            _timer.Stop();
            _timer.Start();
        }

        private void Flush()
        {
            _timer.Stop();
            var files = _batch.ToList();
            _batch.Clear();
            _owner._run(Rule, files);
        }

        private bool _disposed;

        public void Dispose()
        {
            _disposed = true;
            _timer.Stop();
            _watcher.EnableRaisingEvents = false;
            _watcher.Dispose();
        }
    }
}
