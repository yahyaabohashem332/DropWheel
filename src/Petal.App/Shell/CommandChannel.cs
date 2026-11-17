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

/// <summary>Adds or removes DropWheel's entries in File Explorer's right-click menu (per user, no admin rights).</summary>
public static class ContextMenuRegistration
{
    private const string FilesKey = @"Software\Classes\*\shell\Petal";
    private const string FoldersKey = @"Software\Classes\Directory\shell\Petal";

    public static void Apply(bool enabled)
    {
        // The Store's package brings its own entry for the files Petal knows, and leaves the registry alone.
        if (Packaged.IsPackaged)
            return;
        try
        {
            var exe = Environment.ProcessPath;
            SendTo.Apply(enabled ? exe : null, SendTo.Folder);
            if (!enabled || exe is null)
            {
                Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(FilesKey, throwOnMissingSubKey: false);
                Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(FoldersKey, throwOnMissingSubKey: false);
                return;
            }
            foreach (var (key, label) in new[] { (FilesKey, Localization.L.T("Convert with DropWheel")), (FoldersKey, Localization.L.T("DropWheel actions")) })
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

/// <summary>
/// DropWheel in File Explorer's Send to menu. The right-click entry is started once for each file and
/// Windows leaves it out for more than 15; Send to hands every selected file over at once.
/// </summary>
public static class SendTo
{
    public const string ShortcutName = "DropWheel.lnk";

    /// <summary>The user's Send to folder.</summary>
    public static string Folder => Environment.GetFolderPath(Environment.SpecialFolder.SendTo);

    /// <summary>
    /// Puts a shortcut to <paramref name="exe"/> in <paramref name="folder"/>, or (null) takes away the one
    /// DropWheel put there. A shortcut of that name pointing at another program is left alone.
    /// </summary>
    public static void Apply(string? exe, string folder)
    {
        if (string.IsNullOrEmpty(folder))
            return;
        var path = System.IO.Path.Combine(folder, ShortcutName);
        try
        {
            var existing = System.IO.File.Exists(path) ? Target(path) : null;
            bool ours = existing is not null && System.IO.Path.GetFileName(existing).Equals("Petal.exe", StringComparison.OrdinalIgnoreCase);
            if (exe is null)
            {
                if (ours)
                    System.IO.File.Delete(path);
                return;
            }
            if (System.IO.File.Exists(path) && !ours)
                return;
            if (ours && string.Equals(existing, exe, StringComparison.OrdinalIgnoreCase))
                return;
            System.IO.Directory.CreateDirectory(folder);
            Write(path, exe);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException or System.Reflection.TargetInvocationException)
        {
            Log.Error(ex, "Updating DropWheel in Send to");
        }
    }

    private static object Shell() =>
        Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell") ?? throw new System.Runtime.InteropServices.COMException("No WScript.Shell"))!;

    /// <summary>Where a shortcut points.</summary>
    internal static string? Target(string link)
    {
        var shortcut = Shell().GetType().InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, Shell(), [link])!;
        return shortcut.GetType().InvokeMember("TargetPath", System.Reflection.BindingFlags.GetProperty, null, shortcut, null) as string;
    }

    private static void Write(string link, string exe)
    {
        var shell = Shell();
        var shortcut = shell.GetType().InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, shell, [link])!;
        var type = shortcut.GetType();
        type.InvokeMember("TargetPath", System.Reflection.BindingFlags.SetProperty, null, shortcut, [exe]);
        type.InvokeMember("WorkingDirectory", System.Reflection.BindingFlags.SetProperty, null, shortcut, [System.IO.Path.GetDirectoryName(exe)!]);
        type.InvokeMember("IconLocation", System.Reflection.BindingFlags.SetProperty, null, shortcut, [exe + ",0"]);
        type.InvokeMember("Description", System.Reflection.BindingFlags.SetProperty, null, shortcut, [Localization.L.T("Convert with DropWheel")]);
        type.InvokeMember("Save", System.Reflection.BindingFlags.InvokeMethod, null, shortcut, null);
    }
}
