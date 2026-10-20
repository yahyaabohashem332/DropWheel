using Petal.App.Hud;
using Petal.App.Input;
using Petal.App.Jobs;
using Petal.Core.Tools;

namespace Petal.App.Tools;

/// <summary>Opens a tool's window, or runs tools that need no settings straight away.</summary>
public sealed class ToolLauncher(JobRunner jobs)
{
    public JobRunner Jobs { get; } = jobs;

    public bool IsAvailable(ToolInfo tool) => true;

    public Task LaunchAsync(ToolInfo tool, IReadOnlyList<string> files, ScreenPoint at)
    {
        var card = HudCard.ShowAt(at, tool.Name, "Coming soon");
        card.Fail(tool.Name, "This tool is still being built.");
        return Task.CompletedTask;
    }
}
