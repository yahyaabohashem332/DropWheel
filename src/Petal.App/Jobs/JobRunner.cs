using System.Collections.Concurrent;
using System.IO;
using Petal.App.Hud;
using Petal.App.Input;
using Petal.App.Settings;
using Petal.App.Shell;
using Petal.Core.Formats;
using Petal.Core.Jobs;
using Petal.App.Localization;

namespace Petal.App.Jobs;

/// <summary>A unit of work shown on one progress card.</summary>
public sealed record WorkItem(string InputPath, string Label, Func<IProgress<double>, CancellationToken, Task<IReadOnlyList<string>>> Run);

/// <summary>Runs conversions and tools in the background and reports them on a HUD card.</summary>
public sealed class JobRunner(ConverterRegistry converters)
{
    // A few drops at once; within one, BatchWidth decides how many files go side by side.
    private readonly SemaphoreSlim _slots = new(Math.Clamp(Environment.ProcessorCount / 2, 1, 3));

    public ConverterRegistry Converters { get; } = converters;

    /// <summary>One file per core, up to four: more would mostly add memory, not speed.</summary>
    public static int PerCore => Math.Clamp(Environment.ProcessorCount, 1, 4);

    /// <summary>
    /// How many files of one drop are converted at once. Pictures and sounds are encoded on one core
    /// each (this ImageMagick build is single-threaded), so they go side by side, one per core; video
    /// (FFmpeg already uses every core), documents (one Office at a time; PDF pages are already saved
    /// together), Photoshop files and anything over 150 MB go one after another.
    /// </summary>
    internal static int BatchWidth(IReadOnlyList<string> files, FileFormat target) =>
        Light(target) || target == Formats.Pdf ? BatchWidth(files) : 1;

    /// <summary>The same for a tool that changes each file: side by side for pictures and sounds.</summary>
    internal static int BatchWidth(IReadOnlyList<string> files)
    {
        if (files.Count < 2 || !files.All(f => Light(Formats.FromPath(f))))
            return 1;
        try
        {
            return files.Any(f => new FileInfo(f).Length > 150L * 1024 * 1024) ? 1 : PerCore;
        }
        catch (IOException)
        {
            return 1;
        }
    }

    private static bool Light(FileFormat? f) =>
        f is not null && f != Formats.Psd && f != Formats.Psb &&
        f.Family is FormatFamily.Image or FormatFamily.Vector or FormatFamily.Audio or FormatFamily.Palette;

    /// <param name="outputDirectory">Where results go instead of the usual place (watched folders).</param>
    /// <param name="describe">More for the finished card, e.g. how many files of a folder were skipped.</param>
    public Task ConvertAsync(IReadOnlyList<string> files, FileFormat target, ScreenPoint at, string? outputDirectory = null,
        Func<IReadOnlyList<string>, string?>? describe = null)
    {
        var settings = SettingsStore.Current;
        var work = new List<WorkItem>();
        var names = new List<Lazy<string>>();
        foreach (var file in files)
        {
            var from = Formats.FromPath(file);
            if (from is null || from == target)
                continue;
            var converter = Converters.Find(from, target);
            if (converter is null)
                continue;
            var index = names.Count;
            names.Add(new(() => OutputNaming.ForConversion(file, target, outputDirectory ?? settings.OutputDirectoryFor(file))));
            work.Add(new WorkItem(file, Path.GetFileName(file), async (progress, ct) =>
            {
                var output = InOrder(names, index);
                var job = new ConversionJob(file, from, target, output, settings.Conversion);
                var result = await converter.ConvertAsync(job, progress, ct);
                return result.OutputPaths;
            }));
        }
        return RunAsync(L.F("Converting to {0}", target.Label), L.F("Converted to {0}", target.Label), work, at, describe, BatchWidth(files, target));
    }

    /// <summary>A starred conversion: the favourite's format, size and quality.</summary>
    public Task ConvertAsync(IReadOnlyList<string> files, Favourite favourite, ScreenPoint at, string? outputDirectory = null,
        Func<IReadOnlyList<string>, string?>? describe = null)
    {
        if (favourite.Target is not { } target)
            return Task.CompletedTask;
        var settings = SettingsStore.Current;
        var conversion = favourite.Apply(settings.Conversion);
        var work = new List<WorkItem>();
        var names = new List<Lazy<string>>();
        foreach (var file in files)
        {
            var from = Formats.FromPath(file);
            if (from is null || favourite.ConverterFor(from, Converters) is not { } converter)
                continue;
            var index = names.Count;
            names.Add(new(() =>
            {
                var dir = outputDirectory ?? settings.OutputDirectoryFor(file);
                // Saved again in its own format: keep the original and say what changed in the name.
                return from == target
                    ? OutputNaming.WithSuffix(file, FavouriteText.Suffix(favourite), target.WriteExtension, dir)
                    : OutputNaming.ForConversion(file, target, dir);
            }));
            work.Add(new WorkItem(file, Path.GetFileName(file), async (progress, ct) =>
            {
                var output = InOrder(names, index);
                var job = new ConversionJob(file, from, target, output, conversion);
                var result = await converter.ConvertAsync(job, progress, ct);
                return result.OutputPaths;
            }));
        }
        var label = FavouriteText.Label(favourite);
        return RunAsync(L.F("Converting to {0}", label), L.F("Converted to {0}", label), work, at, describe, BatchWidth(files, target));
    }

    /// <summary>
    /// Item <paramref name="index"/>'s output name, worked out once every earlier item has its own: files
    /// converted side by side still get their names in the drop's order ("a.jpg" "a.webp", "a.png" "a 2.webp").
    /// </summary>
    private static string InOrder(List<Lazy<string>> names, int index)
    {
        for (int i = 0; i < index; i++)
            _ = names[i].Value;
        return names[index].Value;
    }

    /// <summary>A recipe over the files, as one job: its steps can combine them (a ZIP, one PDF).</summary>
    public Task RunRecipeAsync(IReadOnlyList<string> files, Petal.Recipes.Recipe recipe, ScreenPoint at, string? outputDirectory = null)
    {
        var settings = SettingsStore.Current;
        var runner = new Petal.Recipes.RecipeRunner(Converters, settings.Conversion);
        var label = files.Count == 1 ? Path.GetFileName(files[0]) : L.P(files.Count, "{0} file", "{0} files");
        var work = new WorkItem(files[0], label, (progress, ct) =>
            runner.RunAsync(recipe, files, outputDirectory ?? (files.Count > 0 ? settings.OutputDirectoryFor(files[0]) : null), progress, ct));
        return RunAsync(L.F("Running {0}", recipe.Name), L.F("{0}: done", recipe.Name), [work], at);
    }

    /// <summary>
    /// Runs work items on a single card: one after another, or <paramref name="width"/> at a time for
    /// independent files that each keep one core busy (see <see cref="BatchWidth"/>).
    /// </summary>
    /// <param name="describe">Optional subtitle for the finished card, e.g. "3.4 MB → 1.1 MB".</param>
    public async Task RunAsync(string runningTitle, string doneTitle, IReadOnlyList<WorkItem> work, ScreenPoint at,
        Func<IReadOnlyList<string>, string?>? describe = null, int width = 1)
    {
        if (work.Count == 0)
            return;
        // Files from a dropped folder keep their results' place in its copy until this run ends.
        using var held = FolderLease.HoldFor(work.Select(w => w.InputPath));

        long startedAt = Environment.TickCount64;
        var card = HudCard.ShowAt(at, runningTitle, $"{work[0].Label} · 0%");
        using var cts = new CancellationTokenSource();
        card.CancelRequested += cts.Cancel;

        // Results stay in the order the files came in, whichever finishes first.
        var produced = new IReadOnlyList<string>?[work.Count];
        var errors = new ConcurrentQueue<(int Index, string Name, string Message)>();
        var fractions = new double[work.Count];
        int started = 0;
        bool together = width > 1 && work.Count > 1;

        // Made here, on the interface thread, so every report comes back to it.
        var reports = work.Select((item, index) => new Progress<double>(f =>
        {
            fractions[index] = Math.Clamp(f, 0, 1);
            double overall = fractions.Sum() / work.Count;
            if (together)
            {
                // Several files at once: how many have started, the latest news, and the whole drop's progress.
                var count = L.F("{0} of {1}", Math.Min(Volatile.Read(ref started), work.Count), work.Count);
                card.SetText(runningTitle, $"{count} · {item.Label} · {Math.Round(overall * 100)}%");
            }
            else
            {
                var prefix = work.Count > 1 ? L.F("{0} of {1}", index + 1, work.Count) + " · " : "";
                card.SetText(runningTitle, $"{prefix}{item.Label} · {Math.Round(fractions[index] * 100)}%");
            }
            card.SetProgress(overall);
        })).ToList();

        await _slots.WaitAsync();
        using var apart = together ? OutputNaming.KeepApart() : null;
        try
        {
            var options = new ParallelOptions { MaxDegreeOfParallelism = together ? width : 1, CancellationToken = cts.Token };
            await Parallel.ForEachAsync(Enumerable.Range(0, work.Count), options, async (index, ct) =>
            {
                var item = work[index];
                Interlocked.Increment(ref started);
                ((IProgress<double>)reports[index]).Report(0);
                try
                {
                    produced[index] = await Task.Run(() => item.Run(reports[index], ct), ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                }
                catch (Exception ex)
                {
                    errors.Enqueue((index, item.Label, Friendly(ex)));
                }
            });
        }
        catch (OperationCanceledException)
        {
            // Cancelled from the card: what was saved before stays.
        }
        finally
        {
            _slots.Release();
        }

        var outputs = produced.Where(p => p is not null).SelectMany(p => p!).ToList();
        var failures = errors.OrderBy(e => e.Index).Select(e => (e.Name, e.Message)).ToList();

        if (cts.IsCancellationRequested)
        {
            card.Fail(L.T("Cancelled"), outputs.Count > 0 ? L.F("{0} of {1} saved before cancelling", outputs.Count, work.Count) : L.T("Nothing was saved"));
        }
        else if (failures.Count == work.Count)
        {
            card.Fail(work.Count == 1 ? L.T("Couldn't finish") : L.F("None of {0} could be processed", work.Count), failures[0].Message);
            Log.Error($"{runningTitle}: {string.Join("; ", failures.Select(e => $"{e.Name}: {e.Message}"))}");
        }
        else
        {
            var saved = L.F("{0} of {1} saved", work.Count - failures.Count, work.Count);
            if (failures.Count > 0)
                saved += " · " + L.F("{0} failed", failures.Count);
            var results = outputs.ToList();
            if (describe?.Invoke(results) is { Length: > 0 } detail)
                saved = $"{saved} · {detail}";
            card.Succeed(doneTitle, saved, () => ExplorerSelection.Reveal(results));
            History.Add(doneTitle, work.Select(w => w.InputPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList(), results);
            if (SettingsStore.Current.SelectResultInExplorer)
                ExplorerSelection.SelectIfVisible(results);
            var done = work.Where((_, i) => produced[i] is not null).Select(w => w.InputPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            Stats.Statistics.RecordWork(done.Count, SizeOf(done), SizeOf(results), (Environment.TickCount64 - startedAt) / 1000.0);
        }
    }

    /// <summary>The bytes in the files (and folders) that exist, for the statistics.</summary>
    private static long SizeOf(IEnumerable<string> paths)
    {
        long total = 0;
        foreach (var path in paths)
        {
            try
            {
                if (File.Exists(path))
                    total += new FileInfo(path).Length;
                else if (Directory.Exists(path))
                    total += new DirectoryInfo(path).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
        return total;
    }

    private static string Friendly(Exception ex) => ex switch
    {
        Petal.Engines.Documents.Pdf.PdfOpenException { LockedForChanges: true } => L.T("Its author has locked this PDF against changes."),
        Petal.Engines.Documents.Pdf.PdfOpenException { NeedsPassword: true } => L.T("This PDF is protected by a password."),
        UnauthorizedAccessException => L.T("Petal is not allowed to save in that folder."),
        IOException io when io.HResult == unchecked((int)0x80070070) => L.T("The disk is full."),
        _ => Shorten(L.T(ex.Message)),
    };

    private static string Shorten(string message) => message.Length > 160 ? message[..160] + "…" : message;
}
