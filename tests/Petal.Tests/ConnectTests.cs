using System.Text.Json.Nodes;
using System.Windows.Threading;
using Petal.App.Settings;
using Petal.App.Shell;
using Petal.Core.Util;

namespace Petal.Tests;

/// <summary>Other programs reaching Petal through its pipe, and Petal Health looking itself over.</summary>
[Collection("History")]
public sealed class ConnectTests
{
    [Fact]
    public async Task Status_and_lists_come_back_and_mistakes_are_explained()
    {
        var api = new LocalApi(null!, Dispatcher.CurrentDispatcher);
        var status = await api.AnswerAsync(new JsonObject { ["cmd"] = "status" });
        Assert.True(status["ok"]!.GetValue<bool>());
        Assert.Matches(@"^\d+\.\d+\.\d+$", status["version"]!.GetValue<string>());

        var lists = await api.AnswerAsync(new JsonObject { ["cmd"] = "actions" });
        var actions = lists["actions"]!.AsArray();
        Assert.Contains(actions, a => a!["id"]!.GetValue<string>() == "screen.full");
        Assert.All(actions, a => Assert.False(string.IsNullOrEmpty(a!["section"]!.GetValue<string>())));

        Assert.False((await api.AnswerAsync(new JsonObject { ["cmd"] = "fly" }))["ok"]!.GetValue<bool>());
        Assert.False((await api.AnswerAsync(null))["ok"]!.GetValue<bool>());
        var unknown = await api.AnswerAsync(new JsonObject { ["cmd"] = "do", ["action"] = "no.such" });
        Assert.Contains("no.such", unknown["error"]!.GetValue<string>());
        var missing = await api.AnswerAsync(new JsonObject { ["cmd"] = "files", ["petal"] = "webp", ["files"] = new JsonArray(@"C:\nowhere\none.png") });
        Assert.Contains("isn't there", missing["error"]!.GetValue<string>());
        Assert.False((await api.AnswerAsync(new JsonObject { ["cmd"] = "wheel", ["name"] = "No Such Wheel" }))["ok"]!.GetValue<bool>());
    }

    [Fact]
    public void File_petals_are_named_by_format_tool_action_recipe_or_favourite()
    {
        var settings = new AppSettings
        {
            Recipes = [new Petal.Recipes.Recipe { Name = "Shop", Steps = [new() { Kind = Petal.Recipes.RecipeStepKind.Zip }] }],
            Favourites = [new Petal.Core.Jobs.Favourite { Format = "webp", LongestSide = 1600, Name = "Web" }],
        };
        Assert.IsType<Petal.Core.Formats.FileFormat>(LocalApi.FilePetal("WEBP", settings)!.Payload);
        Assert.Equal(Petal.Core.Tools.ToolId.Compress, Assert.IsType<Petal.Core.Tools.ToolInfo>(LocalApi.FilePetal("tool:compress", settings)!.Payload).Id);
        Assert.Equal(Petal.Core.Actions.FileActionId.Zip, Assert.IsType<Petal.Core.Actions.FileActionInfo>(LocalApi.FilePetal("action:Zip", settings)!.Payload).Id);
        Assert.IsType<Petal.Recipes.Recipe>(LocalApi.FilePetal("shop", settings)!.Payload);
        Assert.IsType<Petal.Core.Jobs.Favourite>(LocalApi.FilePetal("Web", settings)!.Payload);
        Assert.Null(LocalApi.FilePetal("tool:Teleport", settings));
        Assert.Null(LocalApi.FilePetal("nothing", settings));
    }

    [Fact]
    public async Task A_program_asks_through_the_pipe_and_gets_an_answer()
    {
        var name = "Petal.Api.Test." + Guid.NewGuid().ToString("N")[..8];
        Environment.SetEnvironmentVariable("PETAL_API_PIPE", name);
        var api = new LocalApi(null!, Dispatcher.CurrentDispatcher);
        try
        {
            api.Listen();
            JsonObject? answer = null;
            for (int i = 0; i < 20 && answer is null; i++)
            {
                answer = await PetalApi.SendAsync(new JsonObject { ["cmd"] = "status" }, TimeSpan.FromSeconds(2));
                if (answer is null)
                    await Task.Delay(100);
            }
            Assert.NotNull(answer);
            Assert.True(answer["ok"]!.GetValue<bool>());
            // Not JSON: a clear answer, not a hang.
            var bad = await PetalApi.SendAsync(new JsonObject { ["cmd"] = "status", ["x"] = new JsonArray() }, TimeSpan.FromSeconds(2));
            Assert.NotNull(bad);
        }
        finally
        {
            api.Dispose();
            Environment.SetEnvironmentVariable("PETAL_API_PIPE", null);
        }
        // With nothing listening, asking gives nothing back rather than an error.
        Environment.SetEnvironmentVariable("PETAL_API_PIPE", name + ".gone");
        try
        {
            Assert.Null(await PetalApi.SendAsync(new JsonObject { ["cmd"] = "status" }, TimeSpan.FromMilliseconds(300)));
        }
        finally
        {
            Environment.SetEnvironmentVariable("PETAL_API_PIPE", null);
        }
    }

    [Fact]
    public void Recent_errors_are_counted_from_the_log()
    {
        var now = new DateTime(2026, 10, 23, 12, 0, 0);
        string[] lines =
        [
            "2026-10-01 09:00:00.000 ERROR too old",
            "2026-10-22 09:00:00.000 INFO fine",
            "2026-10-22 10:00:00.000 ERROR Running Compress: boom",
            "2026-10-23 11:00:00.000 ERROR Reading the clipboard: busy",
            "not a log line ERROR either",
        ];
        var (count, latest) = Health.RecentErrors(lines, now);
        Assert.Equal(2, count);
        Assert.Contains("clipboard", latest);
    }

    [Fact]
    public void The_report_leaves_out_what_is_personal()
    {
        var json = new JsonObject
        {
            ["Theme"] = "Dark",
            ["Snippets"] = new JsonArray(new JsonObject { ["Abbreviation"] = ";addr", ["Text"] = "14 Harbour Road" }),
            ["Workspaces"] = new JsonArray(new JsonObject { ["Name"] = "Work" }),
            ["OutputFolder"] = @"C:\Users\me\Private",
        }.ToJsonString();
        var plain = Health.PlainSettings(json);
        Assert.Contains("Dark", plain);
        Assert.DoesNotContain("Harbour", plain);
        Assert.DoesNotContain("Private", plain);
        Assert.Contains("(left out)", plain);
    }

    [Fact]
    public async Task Petal_Health_looks_at_everything_and_says_so()
    {
        var checks = await Health.RunAsync();
        Assert.Equal(11, checks.Count);
        Assert.All(checks, c => Assert.False(string.IsNullOrWhiteSpace(c.Summary)));
        var text = Health.Text(checks);
        Assert.Contains("Disk space", text);
        Assert.Contains("Helper programs", text);
        _ = Health.GraphicsCards();
    }
}
