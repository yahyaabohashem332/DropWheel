using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Petal.Core.Util;

/// <summary>
/// The way other programs reach the running Petal: Stream Deck, AutoHotkey, Power Automate, scripts and
/// the command line. A named pipe for the signed-in user only (web pages can't reach it): one line of JSON
/// in, one line of JSON back.
///
///   {"cmd":"status"}                                 → {"ok":true,"version":"1.8.0","paused":false}
///   {"cmd":"actions"}                                → {"ok":true,"actions":[{"id","name","section"}],"wheels":[{"id","name"}]}
///   {"cmd":"do","action":"screen.full"}              → runs a petal of the wheels without files
///   {"cmd":"wheel","name":"Work"}                    → opens that wheel under the pointer (by name or id)
///   {"cmd":"commands","text":"rec"}                  → opens the command palette, with the text typed
///   {"cmd":"files","petal":"webp","files":["…"]}     → a petal of the file wheels on files (a format,
///                                                       "tool:Compress", "action:Zip", a favourite's or recipe's name)
/// </summary>
public static class PetalApi
{
    /// <summary>The pipe's name; PETAL_API_PIPE gives tests a pipe of their own.</summary>
    public static string PipeName => Environment.GetEnvironmentVariable("PETAL_API_PIPE") is { Length: > 0 } name ? name : $"Petal.Api.{Environment.UserName}";

    /// <summary>Longest request or answer, in characters: enough for many file paths.</summary>
    public const int Longest = 1 << 20;

    /// <summary>Sends a request to the running Petal and returns its answer; null when Petal isn't running.</summary>
    public static async Task<JsonObject?> SendAsync(JsonObject request, TimeSpan? wait = null, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            // A running Petal answers the knock at once; without one, saying so quickly beats waiting.
            using (var knock = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                knock.CancelAfter(TimeSpan.FromSeconds(1.5));
                await pipe.ConnectAsync(knock.Token).ConfigureAwait(false);
            }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(wait ?? TimeSpan.FromSeconds(10));
            await WriteLineAsync(pipe, request.ToJsonString(), timeout.Token).ConfigureAwait(false);
            var answer = await ReadLineAsync(pipe, timeout.Token).ConfigureAwait(false);
            return answer is null ? null : JsonNode.Parse(answer) as JsonObject;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public static async Task WriteLineAsync(Stream stream, string line, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(line.Replace('\n', ' ') + "\n");
        await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>One line, up to <see cref="Longest"/> characters; null when the other side closed first.</summary>
    public static async Task<string?> ReadLineAsync(Stream stream, CancellationToken ct)
    {
        var bytes = new List<byte>(256);
        var one = new byte[1];
        while (bytes.Count < Longest * 4)
        {
            int n = await stream.ReadAsync(one, ct).ConfigureAwait(false);
            if (n == 0)
                return bytes.Count == 0 ? null : Encoding.UTF8.GetString(bytes.ToArray());
            if (one[0] == (byte)'\n')
                return Encoding.UTF8.GetString(bytes.ToArray());
            bytes.Add(one[0]);
        }
        throw new IOException("The request is too long.");
    }

    public static JsonObject Ok(JsonObject? more = null)
    {
        var answer = new JsonObject { ["ok"] = true };
        if (more is not null)
            foreach (var (key, value) in more.ToList())
            {
                more.Remove(key);
                answer[key] = value;
            }
        return answer;
    }

    public static JsonObject Error(string message) => new() { ["ok"] = false, ["error"] = message };
}
