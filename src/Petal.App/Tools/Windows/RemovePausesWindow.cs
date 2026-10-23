using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Petal.App.Input;
using Petal.App.Jobs;
using Petal.App.Localization;
using Petal.Core.Jobs;
using Petal.Core.Text;
using Petal.Core.Tools;
using Petal.Engines.Media;

namespace Petal.App.Tools.Windows;

/// <summary>
/// Remove Pauses: how quiet a pause is, how long before it goes, and how much of it stays around each
/// cut. For one file the window says how many pauses there are and how much shorter it will be.
/// </summary>
public sealed class RemovePausesWindow : MediaToolWindow
{
    private readonly Segmented<double> _quiet;
    private readonly Segmented<double> _shortest;
    private readonly Segmented<double> _keep;
    private readonly TextBlock _found = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) };
    private readonly DispatcherTimer _again = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private CancellationTokenSource? _finding;

    public RemovePausesWindow(ToolInfo tool, IReadOnlyList<string> files, JobRunner jobs, ScreenPoint at) : base(tool, files, jobs, at, 460)
    {
        Body.Children.Add(Text(files.Count == 1 ? System.IO.Path.GetFileName(files[0]) : L.P(files.Count, "{0} file", "{0} files"), "Petal.Section"));
        Body.Children.Add(Text("Cuts out the silent stretches of a talk, a lesson or a screen recording, picture and sound together.", "Petal.Caption").WithMarginT(4));
        _quiet = Segments([(-45.0, "Very quiet"), (-35.0, "Quiet"), (-28.0, "Noisy")], -35.0);
        _shortest = Segments([(0.4, "0.4 s"), (0.6, "0.6 s"), (1.0, "1 s"), (2.0, "2 s")], 0.6);
        _keep = Segments([(0.08, "A little"), (0.15, "Some"), (0.3, L.T("More", "amount"))], 0.15);
        Body.Children.Add(Row("A pause is", _quiet, 150, 14));
        Body.Children.Add(Row("At least", _shortest, 150, 10));
        Body.Children.Add(Row("Kept at each cut", _keep, 150, 10));
        _found.Style = (Style)FindResource("Petal.Caption");
        Body.Children.Add(_found);
        foreach (var choice in new[] { (Action<Action>)(a => _quiet.Changed += _ => a()), a => _shortest.Changed += _ => a(), a => _keep.Changed += _ => a() })
            choice(() =>
            {
                _again.Stop();
                _again.Start();
            });
        _again.Tick += async (_, _) =>
        {
            _again.Stop();
            await FindAsync();
        };
        PrimaryButton.Content = L.T("Remove Pauses");
        PrimaryButton.Click += (_, _) => Apply();
        Closed += (_, _) => _finding?.Cancel();
    }

    private RemovePausesOptions Options => new(_quiet.Value, _shortest.Value, _keep.Value) { Settings = Quality };

    protected override async Task OnMediaReadyAsync(MediaInfo info)
    {
        if (Files.Count == 1)
            await FindAsync();
    }

    /// <summary>For one file: how many pauses the choices find, and the length it will have.</summary>
    private async Task FindAsync()
    {
        if (Files.Count != 1 || Info is not { } info)
            return;
        _finding?.Cancel();
        var cancel = _finding = new CancellationTokenSource();
        _found.Text = L.T("Finding the pauses…");
        try
        {
            var options = Options;
            var pauses = await PauseRemover.FindAsync(File, options, cancel.Token);
            var ranges = PauseRemover.KeepRanges(pauses, info.DurationSeconds, options.Keep);
            var kept = ranges.Sum(r => r.End - r.Start);
            int cuts = PauseRemover.Cuts(ranges, info.DurationSeconds);
            if (cancel.IsCancellationRequested)
                return;
            var removed = TimeSpan.FromSeconds(Math.Max(0, info.DurationSeconds - kept));
            _found.Text = removed.TotalSeconds < 0.2
                ? L.T("No pauses this long were found.")
                : L.F("{0}: {1} shorter, {2} instead of {3}", L.P(cuts, "{0} pause", "{0} pauses"), Durations.Format(removed), Durations.Format(TimeSpan.FromSeconds(kept)), Durations.Format(info.Duration));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Info($"Finding pauses failed: {ex.Message}");
            _found.Text = L.T("The pauses couldn't be found in this file.");
        }
    }

    private void Apply()
    {
        var options = Options;
        RunEach("Removing pauses", "Pauses removed", async (file, p, ct) =>
        {
            var output = OutputNaming.ForTool(file, Tool);
            var cut = await PauseRemover.RemoveAsync(file, output, options, p, ct);
            // Nothing to cut makes no file: the card says so rather than "Pauses removed".
            return cut.OutputPath is { } made ? [made] : throw new InvalidOperationException("No pauses this long were found.");
        });
    }
}
