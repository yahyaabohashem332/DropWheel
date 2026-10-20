using System.IO;
using Petal.App.Hud;
using Petal.App.Input;
using Petal.App.Jobs;
using Petal.App.Tools.Windows;
using Petal.Core.Tools;
using Petal.Engines.Files.Archives;

namespace Petal.App.Tools;

/// <summary>Opens a tool's window near the drop point, or runs tools that need no settings straight away.</summary>
public sealed class ToolLauncher(JobRunner jobs)
{
    public JobRunner Jobs { get; } = jobs;

    /// <summary>Tools whose backend is available on this machine.</summary>
    public bool IsAvailable(ToolInfo tool) => Factories.ContainsKey(tool.Id) || Direct.ContainsKey(tool.Id);

    private delegate ToolWindow WindowFactory(ToolInfo tool, IReadOnlyList<string> files, JobRunner jobs, ScreenPoint at);

    private delegate Task DirectRun(ToolInfo tool, IReadOnlyList<string> files, JobRunner jobs, ScreenPoint at);

    private static readonly Dictionary<ToolId, WindowFactory> Factories = new()
    {
        [ToolId.Metadata] = (t, f, j, a) => new MetadataWindow(t, f, j, a),
        [ToolId.AnnotatePhoto] = (t, f, j, a) => new AnnotateWindow(t, f[0], j, a),
        [ToolId.AddBackground] = (t, f, j, a) => new AddBackgroundWindow(t, f[0], j, a),
        [ToolId.CreateCollage] = (t, f, j, a) => new CollageWindow(t, f, j, a),
    };

    private static readonly Dictionary<ToolId, DirectRun> Direct = new()
    {
        [ToolId.ExtractArchive] = (tool, files, jobs, at) => jobs.RunAsync("Extracting", "Extracted",
            files.Select(f => new WorkItem(f, Path.GetFileName(f), async (progress, ct) =>
                (IReadOnlyList<string>)[await ArchiveExtractor.ExtractAsync(f, progress, ct)])).ToList(), at),
    };

    /// <summary>Lets other parts of the app add tools as their engines come online.</summary>
    internal static void RegisterWindow(ToolId id, Func<ToolInfo, IReadOnlyList<string>, JobRunner, ScreenPoint, ToolWindow> factory) =>
        Factories[id] = (t, f, j, a) => factory(t, f, j, a);

    internal static void RegisterDirect(ToolId id, Func<ToolInfo, IReadOnlyList<string>, JobRunner, ScreenPoint, Task> run) =>
        Direct[id] = (t, f, j, a) => run(t, f, j, a);

    public Task LaunchAsync(ToolInfo tool, IReadOnlyList<string> files, ScreenPoint at)
    {
        if (Direct.TryGetValue(tool.Id, out var direct))
            return direct(tool, files, Jobs, at);
        if (Factories.TryGetValue(tool.Id, out var factory))
        {
            var window = factory(tool, files, Jobs, at);
            window.ShowNear(at);
            return Task.CompletedTask;
        }
        var card = HudCard.ShowAt(at, tool.Name, "");
        card.Fail(tool.Name, "This tool isn't available on this PC.");
        return Task.CompletedTask;
    }
}
