using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Threading;
using Petal.App.Input;
using Petal.App.Overlay;
using Petal.App.Settings;
using Petal.App.Wheels;
using Petal.Core.Actions;
using Petal.Core.Formats;
using Petal.Core.Tools;
using Petal.Core.Util;
using static Petal.App.Interop.NativeMethods;

namespace Petal.App.Shell;

/// <summary>
/// The running Petal's side of <see cref="PetalApi"/>: answers status and lists, runs built-in petals,
/// opens wheels and the command palette, and runs file petals on files. Only Petal's own built-in
/// petals can be run this way, never the scripts in the user's wheels.
/// </summary>
public sealed class LocalApi(PetalController controller, Dispatcher ui) : IDisposable
{
    private readonly CancellationTokenSource _stop = new();

    public void Listen()
    {
        for (int i = 0; i < 2; i++)
            _ = Task.Run(ServeAsync);
    }

    private async Task ServeAsync()
    {
        bool told = false;
        while (!_stop.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                pipe = new NamedPipeServerStream(PetalApi.PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Another program holds the name: try again now and then, without spinning.
                if (!told)
                    Log.Info($"The channel for other programs couldn't be opened: {ex.Message}");
                told = true;
                try { await Task.Delay(TimeSpan.FromSeconds(10), _stop.Token); } catch (OperationCanceledException) { return; }
                continue;
            }
            try
            {
                await using var _ = pipe;
                await pipe.WaitForConnectionAsync(_stop.Token);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(15));
                var line = await PetalApi.ReadLineAsync(pipe, timeout.Token);
                JsonObject answer;
                try
                {
                    answer = line is null ? PetalApi.Error("Nothing was asked.") : await AnswerAsync(JsonNode.Parse(line) as JsonObject);
                }
                catch (JsonException)
                {
                    answer = PetalApi.Error("That isn't JSON.");
                }
                await PetalApi.WriteLineAsync(pipe, answer.ToJsonString(), timeout.Token);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or UnauthorizedAccessException)
            {
                // The other side went away; wait for the next one.
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Answering another program");
            }
        }
    }

    /// <summary>Works out the answer to one request (on the UI thread for anything that shows or runs).</summary>
    internal async Task<JsonObject> AnswerAsync(JsonObject? request)
    {
        if (Text(request?["cmd"]) is not { } cmd)
            return PetalApi.Error("Say what to do in \"cmd\".");
        var settings = SettingsStore.Current;
        switch (cmd)
        {
            case "status":
                return PetalApi.Ok(new JsonObject { ["version"] = UpdateService.Current.ToString(3), ["paused"] = settings.Paused, ["store"] = Packaged.IsPackaged });

            case "actions":
            {
                var actions = new JsonArray(WheelActions.All.Select(a => (JsonNode)new JsonObject { ["id"] = a.Id, ["name"] = a.Name, ["section"] = a.Section }).ToArray());
                var wheels = new JsonArray(settings.Wheels.Select(w => (JsonNode)new JsonObject { ["id"] = w.Id, ["name"] = w.Name, ["on"] = w.Enabled }).ToArray());
                return PetalApi.Ok(new JsonObject { ["actions"] = actions, ["wheels"] = wheels });
            }

            case "do":
            {
                var id = Text(request["action"]);
                if (WheelActions.Get(id) is not { } action)
                    return PetalApi.Error($"There is no petal \"{id}\". Ask for \"actions\" to see them.");
                Run(async () =>
                {
                    var (front, at) = FrontAndPointer();
                    await EntryRunner.RunAsync(Rings.Item(new WheelEntry { Kind = EntryKind.Action, Action = action.Id }), at, front, controller);
                });
                return PetalApi.Ok();
            }

            case "wheel":
            {
                var name = Text(request["name"]) ?? "";
                var wheel = settings.Wheels.FirstOrDefault(w => w.Id == name) ?? settings.Wheels.FirstOrDefault(w => string.Equals(w.Name, name, StringComparison.OrdinalIgnoreCase) || string.Equals(Localization.L.T(w.Name), name, StringComparison.OrdinalIgnoreCase));
                if (wheel is null)
                    return PetalApi.Error($"There is no wheel \"{name}\".");
                Run(async () =>
                {
                    var (front, at) = FrontAndPointer();
                    await controller.OpenRingsAsync(front, at, wheel.Id);
                });
                return PetalApi.Ok();
            }

            case "commands":
            {
                var text = Text(request["text"]) ?? "";
                Run(() => Commands.CommandPalette.ToggleAsync(GetForegroundWindow(), text));
                return PetalApi.Ok();
            }

            case "files":
            {
                var files = (request["files"] as JsonArray)?.Select(Text).OfType<string>().ToList() ?? [];
                if (files.Count == 0)
                    return PetalApi.Error("Give the files in \"files\".");
                var missing = files.FirstOrDefault(f => !File.Exists(f) && !Directory.Exists(f));
                if (missing is not null)
                    return PetalApi.Error($"\"{missing}\" isn't there.");
                if (FilePetal(Text(request["petal"]) ?? "", settings) is not { } item)
                    return PetalApi.Error("There is no such petal. Use a format (\"webp\"), \"tool:Compress\", \"action:Zip\", or a favourite's or recipe's name.");
                Run(() =>
                {
                    var (_, at) = FrontAndPointer();
                    controller.RunOnFiles(item, files, at);
                    return Task.CompletedTask;
                });
                return PetalApi.Ok();
            }

            default:
                return PetalApi.Error($"Petal doesn't know \"{cmd}\".");
        }
    }

    /// <summary>A string from the request, or null when it is missing or isn't one.</summary>
    private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    /// <summary>Starts work on the interface thread; the answer says it started, and a failure is written to the log.</summary>
    private void Run(Func<Task> work) =>
        _ = ui.InvokeAsync(work).Task.Unwrap().ContinueWith(t => Log.Error(t.Exception!.GetBaseException(), "A request from another program"),
            TaskContinuationOptions.OnlyOnFaulted);

    /// <summary>A name of an enum's member, not a number: "999" or "1,2" would otherwise parse.</summary>
    private static bool Named<T>(string text, out T value) where T : struct, Enum =>
        Enum.TryParse(text, ignoreCase: true, out value) && Enum.IsDefined(value) && !text.Any(c => char.IsDigit(c) || c == ',');

    /// <summary>A file wheel's petal by name: a format id, "tool:Id", "action:Id", or a favourite's or recipe's name.</summary>
    internal static WheelItem? FilePetal(string name, AppSettings settings)
    {
        if (name.StartsWith("tool:", StringComparison.OrdinalIgnoreCase))
            return Named<ToolId>(name[5..], out var tool) ? WheelItem.From(ToolRegistry.Get(tool)) : null;
        if (name.StartsWith("action:", StringComparison.OrdinalIgnoreCase))
            return Named<FileActionId>(name[7..], out var id) && FileActionRegistry.All.FirstOrDefault(a => a.Id == id) is { } action ? WheelItem.From(action) : null;
        if (Formats.FromId(name.ToLowerInvariant()) is { } format)
            return WheelItem.From(format);
        if (settings.Recipes.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase)) is { } recipe)
            return WheelItem.From(recipe);
        if (settings.Favourites.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase) || string.Equals(Jobs.FavouriteText.Label(f), name, StringComparison.OrdinalIgnoreCase)) is { } favourite)
            return WheelItem.From(favourite);
        return null;
    }

    private static (IntPtr Front, ScreenPoint At) FrontAndPointer()
    {
        GetCursorPos(out var p);
        return (GetForegroundWindow(), new ScreenPoint(p.X, p.Y));
    }

    public void Dispose() => _stop.Cancel();
}
