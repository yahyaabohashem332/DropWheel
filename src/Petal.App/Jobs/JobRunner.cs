using System.IO;
using Petal.App.Hud;
using Petal.App.Input;
using Petal.App.Settings;
using Petal.App.Shell;
using Petal.Core.Formats;
using Petal.Core.Jobs;

namespace Petal.App.Jobs;

/// <summary>A unit of work shown on one progress card.</summary>
public sealed record WorkItem(string InputPath, string Label, Func<IProgress<double>, CancellationToken, Task<IReadOnlyList<string>>> Run);

/// <summary>Runs conversions and tools in the background and reports them on a HUD card.</summary>
public sealed class JobRunner(ConverterRegistry converters)
{
    // A few heavy jobs at once; FFmpeg and ImageMagick are themselves multi-threaded.
    private readonly SemaphoreSlim _slots = new(Math.Clamp(Environment.ProcessorCount / 2, 1, 3));

    public ConverterRegistry Converters { get; } = converters;

    public Task ConvertAsync(IReadOnlyList<string> files, FileFormat target, ScreenPoint at)
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
                var output = OutputNaming.ForConversion(file, target, settings.OutputDirectoryFor(file));
                var job = new ConversionJob(file, from, target, output, settings.Conversion);
                var result = await converter.ConvertAsync(job, progress, ct);
                return result.OutputPaths;
            }));
        }
        return RunAsync($"Converting to {target.Label}", $"Converted to {target.Label}", work, at);
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
                    var prefix = work.Count > 1 ? $"{index + 1} of {work.Count} · " : "";
                    card.SetText(runningTitle, $"{prefix}{item.Label} · {Math.Round(Math.Clamp(f, 0, 1) * 100)}%");
                    card.SetProgress(overall);
                });
                card.SetText(runningTitle, work.Count > 1 ? $"{i + 1} of {work.Count} · {item.Label}" : item.Label);
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
            card.Fail("Cancelled", outputs.Count > 0 ? $"{outputs.Count} of {work.Count} saved before cancelling" : "Nothing was saved");
        }
        else if (errors.Count == work.Count)
        {
            card.Fail(work.Count == 1 ? "Couldn't finish" : $"None of {work.Count} could be processed", errors[0].Message);
            Log.Error($"{runningTitle}: {string.Join("; ", errors.Select(e => $"{e.Name}: {e.Message}"))}");
        }
        else
        {
            var saved = outputs.Count == 1 ? "1 of 1 saved" : $"{work.Count - errors.Count} of {work.Count} saved";
            if (errors.Count > 0)
                saved += $" · {errors.Count} failed";
            var results = outputs.ToList();
            if (describe?.Invoke(results) is { Length: > 0 } detail)
                saved = $"{saved} · {detail}";
            card.Succeed(doneTitle, saved, () => ExplorerSelection.Reveal(results));
            if (SettingsStore.Current.SelectResultInExplorer)
                ExplorerSelection.SelectIfVisible(results);
        }
    }

    private static string Friendly(Exception ex) => ex switch
    {
        UnauthorizedAccessException => "Petal is not allowed to save in that folder.",
        IOException io when io.HResult == unchecked((int)0x80070070) => "The disk is full.",
        _ => ex.Message.Length > 160 ? ex.Message[..160] + "…" : ex.Message,
    };
}
