using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Messages;
using Petal.Core.Text;

namespace Petal.Recipes.Cloud;

/// <summary>The companies whose models can be used with the user's own key, at the user's own cost.</summary>
public enum CloudProvider
{
    Gemini,
    ChatGpt,
    Claude,
}

/// <summary>A provider and the model chosen from it.</summary>
public sealed record CloudChoice(CloudProvider Provider, string Model);

/// <summary>What went wrong with a cloud model, in words to show (translated by the interface).</summary>
public sealed class CloudException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// The user's API keys, each kept encrypted for this Windows user (DPAPI), beside the settings but never in them:
/// they aren't in a backup, a shared wheel or a support report.
/// </summary>
public static class CloudKeys
{
    private static readonly byte[] Salt = Encoding.UTF8.GetBytes("DropWheel cloud key");

    private static string Folder => Path.Combine(
        Environment.GetEnvironmentVariable("PETAL_SETTINGS_DIR") is { Length: > 0 } own ? own : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Petal"), "keys");

    private static string FileOf(CloudProvider provider) => Path.Combine(Folder, provider.ToString().ToLowerInvariant() + ".key");

    public static bool Has(CloudProvider provider) => File.Exists(FileOf(provider));

    public static string? Get(CloudProvider provider)
    {
        try
        {
            return File.Exists(FileOf(provider)) ? Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(FileOf(provider)), Salt, DataProtectionScope.CurrentUser)) : null;
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static void Set(CloudProvider provider, string key)
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllBytes(FileOf(provider), ProtectedData.Protect(Encoding.UTF8.GetBytes(key.Trim()), Salt, DataProtectionScope.CurrentUser));
    }

    public static void Remove(CloudProvider provider)
    {
        if (File.Exists(FileOf(provider)))
            File.Delete(FileOf(provider));
    }
}

/// <summary>
/// Models in the cloud, used with the user's own key instead of downloading a model. Nothing goes to any of them
/// until the user has added a key and chosen that model for the job: the default is always this PC.
/// </summary>
public static class CloudAi
{
    /// <summary>The model used for writing and answers; null for this PC. Set by the interface from the settings.</summary>
    public static CloudChoice? Writing { get; set; }

    /// <summary>The model used for translation; null for this PC.</summary>
    public static CloudChoice? Translating { get; set; }

    /// <summary>The model used for describing pictures; null for this PC.</summary>
    public static CloudChoice? Seeing { get; set; }

    public static bool CanWrite => Writing is { } c && CloudKeys.Has(c.Provider);

    public static bool CanTranslate => Translating is { } c && CloudKeys.Has(c.Provider);

    public static bool CanSee => Seeing is { } c && CloudKeys.Has(c.Provider);

    public static string Name(CloudProvider provider) => provider switch
    {
        CloudProvider.Gemini => "Gemini",
        CloudProvider.ChatGpt => "ChatGPT",
        _ => "Claude",
    };

    /// <summary>The model a provider opens on, until the user chooses another from its list.</summary>
    public static string DefaultModel(CloudProvider provider) => provider switch
    {
        CloudProvider.Gemini => "gemini-3.8-flash",
        CloudProvider.ChatGpt => "gpt-5",
        _ => "claude-opus-5-5",
    };

    /// <summary>Where a provider gets its keys, for the link in Settings.</summary>
    public static string KeysPage(CloudProvider provider) => provider switch
    {
        CloudProvider.Gemini => "https://aistudio.google.com/apikey",
        CloudProvider.ChatGpt => "https://platform.openai.com/api-keys",
        _ => "https://console.anthropic.com/settings/keys",
    };

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };

    /// <summary>For tests: a server in place of each provider's.</summary>
    internal static string? TestServer { get; set; }

    private static string GeminiBase => TestServer ?? "https://generativelanguage.googleapis.com";
    private static string OpenAiBase => TestServer ?? "https://api.openai.com";

    private static string Key(CloudProvider provider) =>
        CloudKeys.Get(provider) ?? throw new CloudException("Add your API key in Settings › AI Models first.");

    /// <summary>Writes with a cloud model: its answer, handed to <paramref name="partial"/> as it comes.</summary>
    /// <param name="picture">A picture to look at, as bytes and its type ("image/png").</param>
    public static async Task<string> WriteAsync(CloudChoice choice, string instruction, string text, Action<string>? partial = null, int maxTokens = 4000, double temperature = 0.2,
        (byte[] Bytes, string Type)? picture = null, CancellationToken cancellationToken = default)
    {
        // Thinking counts towards the limit on the newest models, so a short answer still needs room.
        maxTokens = Math.Max(maxTokens, 4000);
        var answer = new StringBuilder();
        void Add(string words)
        {
            answer.Append(words);
            partial?.Invoke(answer.ToString());
        }
        try
        {
            switch (choice.Provider)
            {
                case CloudProvider.Gemini:
                    await GeminiAsync(choice.Model, instruction, text, Add, maxTokens, temperature, picture, cancellationToken).ConfigureAwait(false);
                    break;
                case CloudProvider.ChatGpt:
                    await OpenAiAsync(choice.Model, instruction, text, Add, maxTokens, picture, cancellationToken).ConfigureAwait(false);
                    break;
                default:
                    await ClaudeAsync(choice.Model, instruction, text, Add, maxTokens, picture, cancellationToken).ConfigureAwait(false);
                    break;
            }
        }
        catch (HttpRequestException ex)
        {
            throw new CloudException("The cloud model couldn't be reached. Check the internet connection.", ex);
        }
        return answer.ToString().Trim();
    }

    /// <summary>
    /// Each line translated into a language by the chosen model: numbered in batches, so the lines stay lines, and a
    /// line that doesn't come back is asked for alone.
    /// </summary>
    public static async Task<IReadOnlyList<string>> TranslateAsync(IReadOnlyList<string> lines, string into, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var choice = Translating ?? throw new CloudException("Add your API key in Settings › AI Models first.");
        var result = new string[lines.Count];
        string language = Translation.Translator.EnglishName(into);
        var batch = new List<int>();
        int chars = 0, done = 0;
        async Task SendAsync()
        {
            if (batch.Count == 0)
                return;
            var prompt = new StringBuilder();
            for (int i = 0; i < batch.Count; i++)
                prompt.Append(i + 1).Append(". ").Append(lines[batch[i]].Replace('\n', ' ')).Append('\n');
            var reply = await WriteAsync(choice, BatchInstruction(language), prompt.ToString(), cancellationToken: cancellationToken).ConfigureAwait(false);
            var parsed = NumberedLines(reply);
            for (int i = 0; i < batch.Count; i++)
                result[batch[i]] = parsed.TryGetValue(i + 1, out var line) ? line
                    : await WriteAsync(choice, BatchInstruction(language), "1. " + lines[batch[i]].Replace('\n', ' '), cancellationToken: cancellationToken).ConfigureAwait(false) is var alone && NumberedLines(alone).TryGetValue(1, out var single) ? single : alone;
            done += batch.Count;
            progress?.Report((double)done / lines.Count);
            batch.Clear();
            chars = 0;
        }
        for (int i = 0; i < lines.Count; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0 || !line.Any(char.IsLetter))
            {
                result[i] = line;
                done++;
                continue;
            }
            batch.Add(i);
            chars += line.Length;
            if (batch.Count >= 40 || chars > 6000)
                await SendAsync().ConfigureAwait(false);
        }
        await SendAsync().ConfigureAwait(false);
        return result;
    }

    internal static string BatchInstruction(string language) =>
        $"Translate each numbered line into {language}, keeping the meaning, names and numbers. Each line may be a fragment of a document; translate it as it stands. " +
        "If a line is already in that language, repeat it. Reply with the same numbers, one line each in the form \"1. text\", and nothing else.";

    /// <summary>A reply's lines by their numbers.</summary>
    internal static Dictionary<int, string> NumberedLines(string reply)
    {
        var lines = new Dictionary<int, string>();
        foreach (var raw in reply.Replace("\r\n", "\n").Split('\n'))
        {
            var match = System.Text.RegularExpressions.Regex.Match(raw.Trim(), @"^(\d+)[.)]\s*(.*)$");
            if (match.Success && int.TryParse(match.Groups[1].Value, out int number))
                lines[number] = match.Groups[2].Value.Trim();
        }
        return lines;
    }

    /// <summary>The models a provider offers this key, as text models people would choose; a refused key throws.</summary>
    public static async Task<IReadOnlyList<string>> ModelsAsync(CloudProvider provider, CancellationToken cancellationToken = default)
    {
        var key = Key(provider);
        try
        {
            switch (provider)
            {
                case CloudProvider.Gemini:
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, GeminiBase + "/v1beta/models?pageSize=200");
                    request.Headers.Add("x-goog-api-key", key);
                    var json = await GetJsonAsync(request, cancellationToken).ConfigureAwait(false);
                    return (json["models"]?.AsArray() ?? [])
                        .Select(m => m?["name"]?.GetValue<string>()?.Replace("models/", "") ?? "")
                        .Where(n => n.StartsWith("gemini", StringComparison.Ordinal) && !System.Text.RegularExpressions.Regex.IsMatch(n, "embedding|tts|image|live|transcribe|omni|aqa"))
                        .OrderBy(n => n, StringComparer.Ordinal).ToList();
                }
                case CloudProvider.ChatGpt:
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, OpenAiBase + "/v1/models");
                    request.Headers.Add("Authorization", "Bearer " + key);
                    var json = await GetJsonAsync(request, cancellationToken).ConfigureAwait(false);
                    return (json["data"]?.AsArray() ?? [])
                        .Select(m => m?["id"]?.GetValue<string>() ?? "")
                        .Where(n => n.StartsWith("gpt-", StringComparison.Ordinal) && !System.Text.RegularExpressions.Regex.IsMatch(n, "audio|realtime|transcribe|tts|image|search|instruct"))
                        .OrderBy(n => n, StringComparer.Ordinal).ToList();
                }
                default:
                {
                    var client = NewClaude(key);
                    var names = new List<string>();
                    var page = await client.Models.List(cancellationToken: cancellationToken).ConfigureAwait(false);
                    await foreach (var model in page.Paginate(cancellationToken).ConfigureAwait(false))
                        names.Add(model.ID);
                    return names;
                }
            }
        }
        catch (HttpRequestException ex)
        {
            throw new CloudException("The cloud model couldn't be reached. Check the internet connection.", ex);
        }
        catch (AnthropicApiException ex)
        {
            throw Refused(ex);
        }
    }

    private static async Task<JsonNode> GetJsonAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var response = await Http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw Refused(response.StatusCode, body);
        return JsonNode.Parse(body) ?? new JsonObject();
    }

    /// <summary>What an error from a provider means to the person paying for the key.</summary>
    internal static CloudException Refused(HttpStatusCode status, string body) => (int)status switch
    {
        400 when body.Contains("API key", StringComparison.OrdinalIgnoreCase) || body.Contains("API_KEY", StringComparison.Ordinal) => new CloudException("The API key was refused. Check it in Settings › AI Models."),
        401 or 403 => new CloudException("The API key was refused. Check it in Settings › AI Models."),
        404 => new CloudException("This model isn't available with your key. Choose another in Settings › AI Models."),
        402 or 429 => new CloudException("The provider says the key is out of credit or over its limit. Try again later, or check your account."),
        >= 500 => new CloudException("The provider's service is having trouble. Try again later."),
        _ => new CloudException("The cloud model refused the request: " + Excerpt(body)),
    };

    private static CloudException Refused(AnthropicApiException ex) => Refused(ex.StatusCode, ex.ResponseBody ?? ex.Message);

    private static string Excerpt(string body)
    {
        try
        {
            if (JsonNode.Parse(body)?["error"]?["message"]?.GetValue<string>() is { Length: > 0 } message)
                return message;
        }
        catch (JsonException)
        {
        }
        return body.Length > 200 ? body[..200] : body;
    }

    private static async IAsyncEnumerable<string> EventsAsync(HttpRequestMessage request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw Refused(response.StatusCode, await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (line.StartsWith("data:", StringComparison.Ordinal))
                yield return line[5..].Trim();
        }
    }

    private static async Task GeminiAsync(string model, string instruction, string text, Action<string> add, int maxTokens, double temperature, (byte[] Bytes, string Type)? picture, CancellationToken cancellationToken)
    {
        var input = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = text } };
        if (picture is { } p)
            input.Add(new JsonObject { ["type"] = "image", ["data"] = Convert.ToBase64String(p.Bytes), ["mime_type"] = p.Type });
        var body = new JsonObject
        {
            ["model"] = model,
            ["input"] = input,
            ["system_instruction"] = instruction,
            ["store"] = false,
            ["stream"] = true,
            ["generation_config"] = new JsonObject { ["temperature"] = temperature, ["max_output_tokens"] = maxTokens },
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, GeminiBase + "/v1beta/interactions?alt=sse") { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        request.Headers.Add("x-goog-api-key", Key(CloudProvider.Gemini));
        await foreach (var data in EventsAsync(request, cancellationToken).ConfigureAwait(false))
        {
            var node = Parse(data);
            if (node?["event_type"]?.GetValue<string>() == "step.delta" && node["delta"]?["type"]?.GetValue<string>() == "text" && node["delta"]?["text"]?.GetValue<string>() is { } words)
                add(words);
        }
    }

    private static async Task OpenAiAsync(string model, string instruction, string text, Action<string> add, int maxTokens, (byte[] Bytes, string Type)? picture, CancellationToken cancellationToken)
    {
        JsonNode content = text;
        if (picture is { } p)
            content = new JsonArray
            {
                new JsonObject { ["type"] = "text", ["text"] = text },
                new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = $"data:{p.Type};base64,{Convert.ToBase64String(p.Bytes)}" } },
            };
        var body = new JsonObject
        {
            ["model"] = model,
            ["stream"] = true,
            ["max_completion_tokens"] = maxTokens,
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "system", ["content"] = instruction },
                new JsonObject { ["role"] = "user", ["content"] = content },
            },
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, OpenAiBase + "/v1/chat/completions") { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        request.Headers.Add("Authorization", "Bearer " + Key(CloudProvider.ChatGpt));
        await foreach (var data in EventsAsync(request, cancellationToken).ConfigureAwait(false))
        {
            if (data == "[DONE]")
                break;
            if (Parse(data)?["choices"]?[0]?["delta"]?["content"]?.GetValue<string>() is { } words)
                add(words);
        }
    }

    private static JsonNode? Parse(string data)
    {
        try
        {
            return JsonNode.Parse(data);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static AnthropicClient NewClaude(string key) =>
        TestServer is { } server ? new AnthropicClient { ApiKey = key, BaseUrl = server } : new AnthropicClient { ApiKey = key };

    private static async Task ClaudeAsync(string model, string instruction, string text, Action<string> add, int maxTokens, (byte[] Bytes, string Type)? picture, CancellationToken cancellationToken)
    {
        var client = NewClaude(Key(CloudProvider.Claude));
        UserMessageParam message = picture is { } p
            ? new UserMessageParam
            {
                Content = new List<ContentBlockParam>
                {
                    new ImageBlockParam { Source = new Base64ImageSource { Data = Convert.ToBase64String(p.Bytes), MediaType = p.Type == "image/png" ? MediaType.ImagePng : MediaType.ImageJpeg } },
                    new TextBlockParam { Text = text },
                },
            }
            : new UserMessageParam { Content = text };
        var parameters = new MessageCreateParams { Model = model, MaxTokens = maxTokens, System = instruction, Messages = [message] };
        bool refused = false;
        try
        {
            await foreach (var streamEvent in client.Messages.CreateStreaming(parameters, cancellationToken).ConfigureAwait(false))
            {
                if (streamEvent.TryPickContentBlockDelta(out var delta) && delta.Delta.TryPickText(out var words))
                    add(words.Text);
                else if (streamEvent.TryPickDelta(out var ending) && ending.Delta.StopReason == StopReason.Refusal)
                    refused = true;
            }
        }
        catch (AnthropicApiException ex)
        {
            throw Refused(ex);
        }
        if (refused)
            throw new CloudException("The cloud model declined this request.");
    }
}
