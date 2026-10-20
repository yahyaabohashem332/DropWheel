using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Petal.App.Shell;

/// <summary>A request sent from a second Petal process (Explorer's context menu, a shortcut) to the running one.</summary>
public sealed record PetalCommand(string Name, string[] Files, int X, int Y);

/// <summary>
/// Named pipe between Petal processes. The first instance listens; later ones send their command
/// and exit. Paths arriving within a moment of each other are merged, because Explorer starts one
/// process per selected file.
/// </summary>
public sealed class CommandChannel : IDisposable
{
    private static string PipeName => $"Petal.Commands.{Environment.UserName}";
    private readonly CancellationTokenSource _stop = new();

    public event Action<PetalCommand>? Received;

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);

    public void Listen()
    {
        _ = Task.Run(async () =>
        {
            while (!_stop.IsCancellationRequested)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 4, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await server.WaitForConnectionAsync(_stop.Token);
                    using var reader = new StreamReader(server, Encoding.UTF8);
                    var json = await reader.ReadToEndAsync(_stop.Token);
                    if (JsonSerializer.Deserialize<PetalCommand>(json) is { } command)
                        Received?.Invoke(command);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Command channel");
                }
            }
        });
    }

    /// <summary>Sends a command to the running instance. Returns false if none is listening.</summary>
    public static bool Send(PetalCommand command, int serverProcessId = 0)
    {
        try
        {
            // Let the running instance come to the front: Explorer gave this process that right.
            if (serverProcessId > 0)
                AllowSetForegroundWindow(serverProcessId);
            else
                AllowSetForegroundWindow(-1); // ASFW_ANY
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect(1500);
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(command));
            client.Write(bytes);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose() => _stop.Cancel();
}

/// <summary>Adds or removes "Petal" in File Explorer's right-click menu (per user, no admin rights).</summary>
public static class ContextMenuRegistration
{
    private const string FilesKey = @"Software\Classes\*\shell\Petal";
    private const string FoldersKey = @"Software\Classes\Directory\shell\Petal";

    public static void Apply(bool enabled)
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (!enabled || exe is null)
            {
                Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(FilesKey, throwOnMissingSubKey: false);
                Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(FoldersKey, throwOnMissingSubKey: false);
                return;
            }
            foreach (var (key, label) in new[] { (FilesKey, "Convert with Petal"), (FoldersKey, "Petal actions") })
            {
                using var verb = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(key);
                verb.SetValue("MUIVerb", label);
                verb.SetValue("Icon", $"\"{exe}\",0");
                using var command = verb.CreateSubKey("command");
                command.SetValue("", $"\"{exe}\" --wheel \"%1\"");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Updating the context menu entry");
        }
    }
}
