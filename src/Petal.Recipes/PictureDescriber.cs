using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using Petal.Core.Models;

namespace Petal.Recipes;

/// <summary>
/// Says what a picture shows, in English, on this PC: SmolVLM (500 million parameters) run by the same
/// llama.cpp program as Ask Petal. A sentence for alt text, or a fuller paragraph. The interface translates it
/// when it speaks another language.
/// </summary>
public static class PictureDescriber
{
    public static IReadOnlyList<ModelInfo> Models { get; } = [ModelCatalog.LlamaCpp, ModelCatalog.SmolVlm, ModelCatalog.SmolVlmProjector];

    public static bool ModelReady => Cloud.CloudAi.CanSee || Models.All(ModelStore.IsAvailable);

    /// <summary>Pictures are made this small first: the model sees them at about this size anyway, and larger ones only cost time.</summary>
    public const int Side = 640;

    /// <summary>
    /// Describes a picture. <paramref name="picture"/> must be a PNG or JPEG the model can open; callers make
    /// one from any other format first.
    /// </summary>
    public static Task<string> DescribeAsync(string picture, bool detailed, CancellationToken cancellationToken = default) =>
        RunAsync(picture, Prompt(detailed), detailed ? 260 : 90, cancellationToken);

    /// <summary>A question about a picture (in English, which is what the model reads), answered in English in a few sentences.</summary>
    public static Task<string> AskAsync(string picture, string question, CancellationToken cancellationToken = default) =>
        RunAsync(picture, question.Trim() + " Answer briefly.", 200, cancellationToken);

    private static async Task<string> RunAsync(string picture, string prompt, int tokens, CancellationToken cancellationToken)
    {
        if (Cloud.CloudAi.CanSee)
        {
            var type = picture.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? "image/png" : "image/jpeg";
            return await Cloud.CloudAi.WriteAsync(Cloud.CloudAi.Seeing!, "Look at the picture and answer in English.", prompt, null, tokens, 0.1,
                (await File.ReadAllBytesAsync(picture, cancellationToken).ConfigureAwait(false), type), cancellationToken).ConfigureAwait(false);
        }
        var folder = AskPetal.RuntimeFolder();
        var program = Path.Combine(folder, "llama-mtmd-cli.exe");
        if (!File.Exists(program))
            throw new InvalidOperationException("The AI program is missing a part. Download the model again.");
        var model = ModelStore.Find(ModelCatalog.SmolVlm) ?? throw new InvalidOperationException("The picture model isn't downloaded yet.");
        var projector = ModelStore.Find(ModelCatalog.SmolVlmProjector) ?? throw new InvalidOperationException("The picture model isn't downloaded yet.");
        var start = new ProcessStartInfo(program)
        {
            WorkingDirectory = folder,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        int threads = Math.Clamp(Environment.ProcessorCount - 1, 1, 8);
        foreach (var arg in new[]
        {
            "-m", model, "--mmproj", projector, "--image", picture, "-p", prompt,
            "-n", tokens.ToString(CultureInfo.InvariantCulture), "--temp", "0.1", "-t", threads.ToString(CultureInfo.InvariantCulture),
        })
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("The AI model didn't start.");
        using var kill = cancellationToken.Register(() => { try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } });
        var errors = process.StandardError.ReadToEndAsync(CancellationToken.None);
        var output = await process.StandardOutput.ReadToEndAsync(CancellationToken.None).ConfigureAwait(false);
        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        _ = await errors.ConfigureAwait(false);
        if (process.ExitCode != 0)
            throw new InvalidOperationException("The AI model stopped unexpectedly.");
        return Clean(output);
    }

    internal static string Prompt(bool detailed) => detailed
        ? "Describe this image in detail in one short paragraph: what it shows, the setting, colours and any text in it."
        : "Write alt text for this image: one clear, detailed sentence about what it shows.";

    /// <summary>The answer alone: no end marks, no "In this image we can see", starting with a capital and ending with a full stop.</summary>
    internal static string Clean(string output)
    {
        var text = AskPetal.CleanAnswer(output).Replace("<end_of_utterance>", "", StringComparison.Ordinal).Trim();
        foreach (var opener in new[] { "In this image we can see ", "In this image, we can see ", "In this picture we can see ", "The image shows ", "This image shows " })
        {
            if (text.StartsWith(opener, StringComparison.OrdinalIgnoreCase) && text.Length > opener.Length + 3)
            {
                text = text[opener.Length..];
                break;
            }
        }
        if (text.Length == 0)
            return text;
        text = char.ToUpperInvariant(text[0]) + text[1..];
        if (!".!?".Contains(text[^1]))
            text += ".";
        return text;
    }
}
