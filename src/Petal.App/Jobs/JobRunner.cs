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
    // A few heavy jobs at once; FFmpeg and ImageMagick are themselves multi-threaded.
    private readonly SemaphoreSlim _slots = new(Math.Clamp(Environment.ProcessorCount / 2, 1, 3));

    public ConverterRegistry Converters { get; } = converters;

    /// <param name="outputDirectory">Where results go instead of the usual place (watched folders).</param>
    public Task ConvertAsync(IReadOnlyList<string> files, FileFormat target, ScreenPoint at, string? outputDirectory = null)
    {
        var settings = SettingsStore.Current;
        var work = new List<WorkItem>();
        foreach (var file in files)
        {
            var from = Formats.FromPath(file);
            if (from is null || from == target)
                continue;
            var converter = Converters.Find(from, target);
            if (converter is null)
                continue;
            work.Add(new WorkItem(file, Path.GetFileName(file), async (progress, ct) =>
            {
                var output = OutputNaming.ForConversion(file, target, outputDirectory ?? settings.OutputDirectoryFor(file));
                var job = new ConversionJob(file, from, target, output, settings.Conversion);
                var result = await converter.ConvertAsync(job, progress, ct);
                return result.OutputPaths;
            }));
        }
        return RunAsync(L.F("Converting to {0}", target.Label), L.F("Converted to {0}", target.Label), work, at);
    }

    /// <summary>A starred conversion: the favourite's format, size and quality.</summary>
    public Task ConvertAsync(IReadOnlyList<string> files, Favourite favourite, ScreenPoint at, string? outputDirectory = null)
    {
        if (favourite.Target is not { } target)
            return Task.CompletedTask;
        var settings = SettingsStore.Current;
        var conversion = favourite.Apply(settings.Conversion);
        var work = new List<WorkItem>();
        foreach (var file in files)
        {
            var from = Formats.FromPath(file);
            if (from is null || favourite.ConverterFor(from, Converters) is not { } converter)
                continue;
            work.Add(new WorkItem(file, Path.GetFileName(file), async (progress, ct) =>
            {
                var dir = outputDirectory ?? settings.OutputDirectoryFor(file);
                // Saved again in its own format: keep the original and say what changed in the name.
                var output = from == target
                    ? OutputNaming.WithSuffix(file, FavouriteText.Suffix(favourite), target.WriteExtension, dir)
                    : OutputNaming.ForConversion(file, target, dir);
                var job = new ConversionJob(file, from, target, output, conversion);
                var result = await converter.ConvertAsync(job, progress, ct);
                return result.OutputPaths;
            }));
        }
        var label = FavouriteText.Label(favourite);
        return RunAsync(L.F("Converting to {0}", label), L.F("Converted to {0}", label), work, at);
    }

    /// <summary>Runs work items one after another on a single card.</summary>
    /// <param name="describe">Optional subtitle for the finished card, e.g. "3.4 MB → 1.1 MB".</param>
    public async Task RunAsync(string runningTitle, string doneTitle, IReadOnlyList<WorkItem> work, ScreenPoint at, Func<IReadOnlyList<string>, string?>? describe = null)
    {
        if (work.Count == 0)
            return;

        var card = HudCard.ShowAt(at, runningTitle, $"{work[0].Label} · 0%");
        using var cts = new CancellationTokenSource();
        card.CancelRequested += cts.Cancel;

        var outputs = new List<string>();
        var errors = new List<(string Name, string Message)>();

        await _slots.WaitAsync();
        try
        {
            for (int i = 0; i < work.Count; i++)
            {
                if (cts.IsCancellationRequested)
                    break;
                var item = work[i];
                int index = i;
                var progress = new Progress<double>(f =>
                {
                    double overall = (index + Math.Clamp(f, 0, 1)) / work.Count;
                    var prefix = work.Count > 1 ? L.F("{0} of {1}", index + 1, work.Count) + " · " : "";
                    card.SetText(runningTitle, $"{prefix}{item.Label} · {Math.Round(Math.Clamp(f, 0, 1) * 100)}%");
                    card.SetProgress(overall);
                });
                card.SetText(runningTitle, work.Count > 1 ? L.F("{0} of {1}", i + 1, work.Count) + " · " + item.Label : item.Label);
                try
                {
                    var produced = await Task.Run(() => item.Run(progress, cts.Token), cts.Token);
                    outputs.AddRange(produced);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    errors.Add((item.Label, Friendly(ex)));
                }
            }
        }
        finally
        {
            _slots.Release();
        }

        if (cts.IsCancellationRequested)
        {
            card.Fail(L.T("Cancelled"), outputs.Count > 0 ? L.F("{0} of {1} saved before cancelling", outputs.Count, work.Count) : L.T("Nothing was saved"));
        }
        else if (errors.Count == work.Count)
        {
            card.Fail(work.Count == 1 ? L.T("Couldn't finish") : L.F("None of {0} could be processed", work.Count), errors[0].Message);
            Log.Error($"{runningTitle}: {string.Join("; ", errors.Select(e => $"{e.Name}: {e.Message}"))}");
        }
        else
        {
            var saved = L.F("{0} of {1} saved", work.Count - errors.Count, work.Count);
            if (errors.Count > 0)
                saved += " · " + L.F("{0} failed", errors.Count);
            var results = outputs.ToList();
            if (describe?.Invoke(results) is { Length: > 0 } detail)
                saved = $"{saved} · {detail}";
            card.Succeed(doneTitle, saved, () => ExplorerSelection.Reveal(results));
            History.Add(doneTitle, work.Select(w => w.InputPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList(), results);
            if (SettingsStore.Current.SelectResultInExplorer)
                ExplorerSelection.SelectIfVisible(results);
        }
    }

    private static string Friendly(Exception ex) => ex switch
    {
        UnauthorizedAccessException => L.T("Petal is not allowed to save in that folder."),
        IOException io when io.HResult == unchecked((int)0x80070070) => L.T("The disk is full."),
        _ => Shorten(L.T(ex.Message)),
    };

    private static string Shorten(string message) => message.Length > 160 ? message[..160] + "…" : message;
}
