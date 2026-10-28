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
    /// following <paramref name="instruction"/>. Returns the model's answer only. A <paramref name="temperature"/> of 0
    /// gives the same answer every time, for answers that must only repeat what they were given.
    /// </summary>
    public static Task<string> WriteAsync(string instruction, string text, int maxTokens = 700, CancellationToken cancellationToken = default, double temperature = 0.2) =>
        WriteAsync(instruction, text, null, maxTokens, cancellationToken, temperature);

    /// <summary>
    /// The same, with the answer handed to <paramref name="partial"/> as it is written, so it can be shown word by word
    /// (on the thread that reads the model's output). Another <paramref name="model"/> can write it (a larger one, on every
    /// core: it is waited for), and its answer can be begun for it (<paramref name="begun"/>, given back with the rest).
    /// </summary>
    public static async Task<string> WriteAsync(string instruction, string text, Action<string>? partial, int maxTokens = 700, CancellationToken cancellationToken = default,
        double temperature = 0.2, ModelInfo? model = null, string begun = "")
    {
        var folder = RuntimeFolder();
        var weights = ModelStore.Find(model ?? ModelCatalog.Qwen) ?? throw new InvalidOperationException("The AI model isn't downloaded yet.");
        var work = Path.Combine(Path.GetTempPath(), "Petal", $"write-{Guid.NewGuid():N}");
        Directory.CreateDirectory(work);
        try
        {
            var clean = text.Replace("<|", "< |").Trim();
            if (clean.Length > MaxWritingText)
                clean = clean[..MaxWritingText];
            var prompt = Path.Combine(work, "prompt.txt");
            await File.WriteAllTextAsync(prompt,
                $"<|im_start|>system\n{instruction}<|im_end|>\n<|im_start|>user\n{clean}<|im_end|>\n<|im_start|>assistant\n{begun}",
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
            int threads = Math.Clamp(Environment.ProcessorCount - (model is null ? 1 : 0), 1, 8);
            foreach (var arg in new[] { "-m", weights, "-f", prompt, "-n", maxTokens.ToString(CultureInfo.InvariantCulture), "--temp", temperature.ToString("0.##", CultureInfo.InvariantCulture), "--repeat-penalty", "1.1",
                         "-no-cnv", "--no-display-prompt", "-c", "8192", "-t", threads.ToString(CultureInfo.InvariantCulture) })
                start.ArgumentList.Add(arg);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("The AI model didn't start.");
            using var kill = cancellationToken.Register(() => { try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } });
            var errors = process.StandardError.ReadToEndAsync(CancellationToken.None);
            var output = new StringBuilder();
            var buffer = new char[256];
            int read;
            while ((read = await process.StandardOutput.ReadAsync(buffer, CancellationToken.None).ConfigureAwait(false)) > 0)
            {
                output.Append(buffer, 0, read);
                if (partial is not null && !cancellationToken.IsCancellationRequested)
                    partial(CleanAnswer(begun + output));
            }
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            _ = await errors.ConfigureAwait(false);
            if (process.ExitCode != 0)
                throw new InvalidOperationException("The AI model stopped unexpectedly.");
            return CleanAnswer(begun + output);
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// What the model is given for a question about text from the screen: the text, the questions so far with their
    /// answers (so the next can follow on), and the new question.
    /// </summary>
    public static string ScreenQuestion(string text, IReadOnlyList<(string Question, string Answer)> earlier, string question)
    {
        var sb = new StringBuilder();
        sb.Append("Text from the screen:\n\"\"\"\n").Append(text).Append("\n\"\"\"\n\n");
        foreach (var (q, a) in earlier)
            sb.Append("Question: ").Append(q).Append("\nAnswer: ").Append(a).Append("\n\n");
        sb.Append("Question: ").Append(question);
        return sb.ToString();
    }

    /// <summary>What the model is told for each kind of writing; the answer comes back in the text's language.</summary>
    public static class Instructions
    {
        private static string Language(bool arabic) => arabic ? "Arabic" : "the same language as the text";

        /// <summary>Into a language by its English name; text already in it goes into English instead.</summary>
        public static string TranslateInto(string language) =>
            $"Translate the user's text into {language}. If it is already in {language}, translate it into English instead. Keep the meaning, names, numbers and line breaks. Reply with the translation only, nothing else.";

        public static string Translate(bool intoEnglish) =>
            $"Translate the user's text into {(intoEnglish ? "English" : "Arabic")}. Keep the meaning, names, numbers and line breaks. Reply with the translation only, nothing else.";

        public static string Summary(bool points, bool arabic) => points
            ? $"Summarize the user's text as 3 to 6 short bullet points starting with \"• \", in {Language(arabic)}. Reply with the bullet points only."
            : $"Summarize the user's text in 2 or 3 sentences, in {Language(arabic)}. Reply with the summary only.";

        /// <summary>What something circled on the screen means, said simply, in the reader's language.</summary>
        public static string Explain(string language) =>
            $"The user circled the text below on their screen and wants to understand it. Explain it simply and briefly in {language}: " +
            "what it means and anything worth knowing about it. If it is a word, a name or a term, define it. If it is code, an error message or a formula, " +
            "say what it does or how to fix it. Do not repeat the text. Use at most six short sentences.";

        /// <summary>A question about something circled on the screen, answered from it first.</summary>
        public static string Answer(string language) =>
            $"Answer the user's question about the text from their screen, briefly and accurately, in {language}. Use the text first; " +
            "if the answer is not in it, say so in a few words, then answer from what you know and say that you did.";

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
