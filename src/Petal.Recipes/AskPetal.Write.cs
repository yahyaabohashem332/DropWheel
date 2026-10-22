using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using Petal.Core.Models;

namespace Petal.Recipes;

public static partial class AskPetal
{
    /// <summary>The longest text handed to the model; the rest is left out.</summary>
    public const int MaxWritingText = 6000;

    /// <summary>
    /// Free writing with the same model, on this PC: a summary, a rewrite or a translation of a text,
    /// following <paramref name="instruction"/>. Returns the model's answer only.
    /// </summary>
    public static async Task<string> WriteAsync(string instruction, string text, int maxTokens = 700, CancellationToken cancellationToken = default)
    {
        var folder = RuntimeFolder();
        var model = ModelStore.Find(ModelCatalog.Qwen) ?? throw new InvalidOperationException("The AI model isn't downloaded yet.");
        var work = Path.Combine(Path.GetTempPath(), "Petal", $"write-{Guid.NewGuid():N}");
        Directory.CreateDirectory(work);
        try
        {
            var clean = text.Replace("<|", "< |").Trim();
            if (clean.Length > MaxWritingText)
                clean = clean[..MaxWritingText];
            var prompt = Path.Combine(work, "prompt.txt");
            await File.WriteAllTextAsync(prompt,
                $"<|im_start|>system\n{instruction}<|im_end|>\n<|im_start|>user\n{clean}<|im_end|>\n<|im_start|>assistant\n",
                new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            var start = new ProcessStartInfo(Path.Combine(folder, "llama-completion.exe"))
            {
                WorkingDirectory = folder,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            int threads = Math.Clamp(Environment.ProcessorCount - 1, 1, 8);
            foreach (var arg in new[] { "-m", model, "-f", prompt, "-n", maxTokens.ToString(CultureInfo.InvariantCulture), "--temp", "0.2", "--repeat-penalty", "1.1",
                         "-no-cnv", "--no-display-prompt", "-c", "8192", "-t", threads.ToString(CultureInfo.InvariantCulture) })
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
            return CleanAnswer(output);
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>What the model is told for each kind of writing; the answer comes back in the text's language.</summary>
    public static class Instructions
    {
        private static string Language(bool arabic) => arabic ? "Arabic" : "the same language as the text";

        public static string Translate(bool intoEnglish) =>
            $"Translate the user's text into {(intoEnglish ? "English" : "Arabic")}. Keep the meaning, names, numbers and line breaks. Reply with the translation only, nothing else.";

        public static string Summary(bool points, bool arabic) => points
            ? $"Summarize the user's text as 3 to 6 short bullet points starting with \"• \", in {Language(arabic)}. Reply with the bullet points only."
            : $"Summarize the user's text in 2 or 3 sentences, in {Language(arabic)}. Reply with the summary only.";

        public static string Rewrite(string style, bool arabic) => style switch
        {
            "short" => $"Rewrite the user's text to be much shorter, keeping what matters, in {Language(arabic)}. Reply with the new text only.",
            "formal" => $"Rewrite the user's text in a polite, formal tone, in {Language(arabic)}. Reply with the new text only.",
            "friendly" => $"Rewrite the user's text in a warm, friendly tone, in {Language(arabic)}. Reply with the new text only.",
            "fix" => $"Correct the spelling, grammar and punctuation of the user's text, changing nothing else, in {Language(arabic)}. Reply with the corrected text only.",
            _ => $"Rewrite the user's text so it is clear and easy to read, keeping its meaning, in {Language(arabic)}. Reply with the new text only.",
        };
    }

    /// <summary>The answer without the marks the model ends with.</summary>
    internal static string CleanAnswer(string output)
    {
        foreach (var mark in new[] { "<|im_end|>", "<|endoftext|>", "[end of text]", "<|im_start|>" })
        {
            int at = output.IndexOf(mark, StringComparison.Ordinal);
            if (at >= 0)
                output = output[..at];
        }
        return output.Trim();
    }
}
