using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Petal.Core.Models;
using Petal.Engines.Media.Internal;
using Windows.Media.SpeechSynthesis;
using Windows.Storage.Streams;

namespace Petal.Engines.Media;

public enum VoiceKind
{
    /// <summary>A voice installed in Windows (Settings › Time &amp; language › Speech).</summary>
    Windows,
    /// <summary>A natural-sounding Piper voice, downloaded once.</summary>
    Natural,
}

/// <param name="Id">The Windows voice's id, or the natural voice's own id.</param>
/// <param name="Language">BCP-47, e.g. "ar-SA" or "en-US".</param>
public sealed record SpeechVoice(string Id, string Name, string Language, VoiceKind Kind)
{
    /// <summary>What a natural voice needs on this PC: the Piper program, the voice and its settings.</summary>
    public IReadOnlyList<ModelInfo> Models { get; init; } = [];

    public bool IsReady => Kind == VoiceKind.Windows || Models.All(ModelStore.IsAvailable);

    /// <summary>Download size of whatever is still missing.</summary>
    public long MissingBytes => Models.Where(m => !ModelStore.IsAvailable(m)).Sum(m => m.Size);

    public bool Speaks(string language) => Language.StartsWith(language, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Text to speech, saved as audio: Windows' own voices (Arabic among them), or Piper's natural English voice, run on
/// this PC. Long texts are spoken in pieces and joined into one MP3.
/// </summary>
public static class Speech
{
    public static readonly SpeechVoice Linda = new("piper-en-ljspeech", "Linda", "en-US", VoiceKind.Natural)
    {
        Models = [ModelCatalog.Piper, ModelCatalog.VoiceLinda, ModelCatalog.VoiceLindaSettings],
    };

    public static IReadOnlyList<SpeechVoice> NaturalVoices { get; } = [Linda];

    /// <summary>The voices installed in Windows; none when speech isn't available.</summary>
    public static IReadOnlyList<SpeechVoice> WindowsVoices()
    {
        try
        {
            return SpeechSynthesizer.AllVoices
                .Select(v => new SpeechVoice(v.Id, v.DisplayName, v.Language, VoiceKind.Windows))
                .OrderBy(v => v.Language, StringComparer.OrdinalIgnoreCase).ThenBy(v => v.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or TypeLoadException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public static IReadOnlyList<SpeechVoice> AllVoices() => [.. WindowsVoices(), .. NaturalVoices];

    /// <summary>The language a text is mostly written in, from its letters ("ar", "tr", "en", …).</summary>
    public static string GuessLanguage(string text)
    {
        int arabic = 0, latin = 0, cyrillic = 0, greek = 0, hebrew = 0, turkish = 0, sample = 0;
        foreach (var c in text)
        {
            if (!char.IsLetter(c))
                continue;
            if (++sample > 20_000)
                break;
            switch (c)
            {
                case >= '\u0600' and <= '\u06FF' or >= '\u0750' and <= '\u077F' or >= '\uFB50' and <= '\uFDFF' or >= '\uFE70' and <= '\uFEFF':
                    arabic++;
                    break;
                case >= '\u0590' and <= '\u05FF':
                    hebrew++;
                    break;
                case >= '\u0400' and <= '\u04FF':
                    cyrillic++;
                    break;
                case >= '\u0370' and <= '\u03FF':
                    greek++;
                    break;
                case 'ğ' or 'Ğ' or 'ş' or 'Ş' or 'ı' or 'İ':
                    turkish++;
                    latin++;
                    break;
                case < 'ɐ':
                    latin++;
                    break;
            }
        }
        int best = new[] { arabic, latin, cyrillic, greek, hebrew }.Max();
        if (best == 0)
            return "en";
        if (best == arabic)
            return "ar";
        if (best == cyrillic)
            return "ru";
        if (best == greek)
            return "el";
        if (best == hebrew)
            return "he";
        return turkish * 200 > latin ? "tr" : "en";
    }

    /// <summary>
    /// The voice for a language: a natural voice that's already here, else a Windows one, else a
    /// natural one that would have to be downloaded. Null when nothing speaks it.
    /// </summary>
    public static SpeechVoice? Choose(string language, IReadOnlyList<SpeechVoice> voices) =>
        voices.FirstOrDefault(v => v.Kind == VoiceKind.Natural && v.IsReady && v.Speaks(language))
        ?? voices.FirstOrDefault(v => v.Kind == VoiceKind.Windows && v.Speaks(language))
        ?? voices.FirstOrDefault(v => v.Speaks(language));

    /// <summary>Roughly how long the text takes to say at this rate.</summary>
    public static TimeSpan EstimateDuration(string text, double rate = 1) =>
        TimeSpan.FromSeconds(text.Count(c => !char.IsWhiteSpace(c)) / 13.0 / Math.Clamp(rate, 0.5, 3));

    /// <summary>Speaks <paramref name="text"/> into an MP3 (or WAV, by the output's extension).</summary>
    /// <param name="rate">1 is the voice's normal pace; 0.5 to 2.</param>
    public static async Task SpeakToFileAsync(string text, SpeechVoice voice, double rate, string output, string? title = null,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        TempOutput.EnsureNotInput(output);
        var chunks = Chunks(text);
        if (chunks.Count == 0)
            throw new MediaException("There's no text to read aloud.");
        rate = Math.Clamp(rate, 0.5, 2);
        var raw = Path.Combine(Path.GetTempPath(), "Petal", $"speech-{Guid.NewGuid():N}.pcm");
        Directory.CreateDirectory(Path.GetDirectoryName(raw)!);
        try
        {
            var speaking = new Progress<double>(f => progress?.Report(f * 0.85));
            var format = voice.Kind == VoiceKind.Windows
                ? await WindowsAsync(chunks, voice, rate, raw, speaking, cancellationToken).ConfigureAwait(false)
                : await NaturalAsync(chunks, voice, rate, raw, speaking, cancellationToken).ConfigureAwait(false);
            double seconds = new FileInfo(raw).Length / (double)(format.SampleRate * format.Channels * 2);
            var encoding = new Progress<double>(f => progress?.Report(0.85 + f * 0.15));
            bool wav = output.EndsWith(".wav", StringComparison.OrdinalIgnoreCase);
            List<string> args = ["-f", "s16le", "-ar", format.SampleRate.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "-ac", format.Channels.ToString(System.Globalization.CultureInfo.InvariantCulture), "-i", raw];
            if (!string.IsNullOrWhiteSpace(title))
                args.AddRange(["-metadata", $"title={title}"]);
            args.AddRange(wav ? ["-c:a", "pcm_s16le"] : ["-c:a", "libmp3lame", "-b:a", "64k", "-ac", "1"]);
            await Ffmpeg.EncodeAsync(args, output, wav ? "wav" : "mp3", seconds, encoding, cancellationToken).ConfigureAwait(false);
            progress?.Report(1);
        }
        finally
        {
            try { File.Delete(raw); } catch (IOException) { }
        }
    }

    private readonly record struct PcmFormat(int SampleRate, int Channels);

    /// <summary>
    /// Pieces of at most about 1,500 characters, cut at paragraph and sentence ends, so long
    /// books speak in steps (and pause where the text does).
    /// </summary>
    public static List<string> Chunks(string text, int max = 1500)
    {
        var chunks = new List<string>();
        var current = new StringBuilder();
        void Flush()
        {
            if (current.Length > 0)
                chunks.Add(current.ToString().Trim());
            current.Clear();
        }
        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            var paragraph = string.Join(' ', line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            if (paragraph.Length == 0)
                continue;
            foreach (var sentence in Sentences(paragraph, max))
            {
                if (current.Length > 0 && current.Length + sentence.Length + 1 > max)
                    Flush();
                if (current.Length > 0)
                    current.Append(' ');
                current.Append(sentence);
            }
            // A paragraph ends a piece once it's reasonably long, so headings keep their pause.
            if (current.Length > max / 3)
                Flush();
            else if (current.Length > 0 && !".!?؟:;".Contains(current[^1]))
                current.Append('.');
        }
        Flush();
        return chunks.Where(c => c.Any(char.IsLetterOrDigit)).ToList();
    }

    private static IEnumerable<string> Sentences(string paragraph, int max)
    {
        int start = 0;
        for (int i = 0; i < paragraph.Length; i++)
        {
            bool end = paragraph[i] is '.' or '!' or '?' or '؟' or '۔' && (i + 1 == paragraph.Length || paragraph[i + 1] == ' ');
            if (!end && i - start < max)
                continue;
            if (!end)
            {
                // A run-on sentence: break at the last space before the limit.
                int space = paragraph.LastIndexOf(' ', i, i - start);
                if (space > start)
                    i = space;
            }
            yield return paragraph[start..(i + 1)].Trim();
            start = i + 1;
        }
        if (start < paragraph.Length && paragraph[start..].Trim() is { Length: > 0 } rest)
            yield return rest;
    }

    private static async Task<PcmFormat> WindowsAsync(List<string> chunks, SpeechVoice voice, double rate, string raw, IProgress<double> progress, CancellationToken ct)
    {
        using var synthesizer = new SpeechSynthesizer();
        synthesizer.Voice = SpeechSynthesizer.AllVoices.FirstOrDefault(v => v.Id == voice.Id)
            ?? throw new MediaException($"The voice {voice.Name} is no longer installed.");
        synthesizer.Options.SpeakingRate = rate;
        PcmFormat? format = null;
        long total = chunks.Sum(c => c.Length), done = 0;
        await using var file = new FileStream(raw, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
        foreach (var chunk in chunks)
        {
            ct.ThrowIfCancellationRequested();
            using var stream = await synthesizer.SynthesizeTextToStreamAsync(chunk).AsTask(ct).ConfigureAwait(false);
            var bytes = new byte[stream.Size];
            using (var reader = new DataReader(stream.GetInputStreamAt(0)))
            {
                await reader.LoadAsync((uint)stream.Size).AsTask(ct).ConfigureAwait(false);
                reader.ReadBytes(bytes);
            }
            var (pcm, chunkFormat) = ReadWav(bytes);
            format ??= chunkFormat;
            if (chunkFormat != format)
                throw new MediaException("The voice changed its audio format partway through.");
            await file.WriteAsync(pcm, ct).ConfigureAwait(false);
            done += chunk.Length;
            progress.Report((double)done / total);
        }
        return format ?? new PcmFormat(16000, 1);
    }

    /// <summary>The 16-bit PCM samples of a RIFF WAVE file, and their format.</summary>
    private static (ReadOnlyMemory<byte> Pcm, PcmFormat Format) ReadWav(byte[] wav)
    {
        if (wav.Length < 12 || Encoding.ASCII.GetString(wav, 0, 4) != "RIFF" || Encoding.ASCII.GetString(wav, 8, 4) != "WAVE")
            throw new MediaException("Windows didn't return speech audio.");
        PcmFormat? format = null;
        for (int at = 12; at + 8 <= wav.Length;)
        {
            var id = Encoding.ASCII.GetString(wav, at, 4);
            int size = BitConverter.ToInt32(wav, at + 4);
            int body = at + 8;
            if (size < 0 || body + size > wav.Length)
                size = wav.Length - body;
            if (id == "fmt ")
            {
                int bits = BitConverter.ToInt16(wav, body + 14);
                if (BitConverter.ToInt16(wav, body) != 1 || bits != 16)
                    throw new MediaException("Windows returned speech in a format DropWheel doesn't expect.");
                format = new PcmFormat(BitConverter.ToInt32(wav, body + 4), BitConverter.ToInt16(wav, body + 2));
            }
            else if (id == "data" && format is { } f)
            {
                return (wav.AsMemory(body, size), f);
            }
            at = body + size + (size & 1);
        }
        throw new MediaException("Windows didn't return speech audio.");
    }

    /// <summary>Piper's program folder, unpacked from its download the first time.</summary>
    internal static string PiperFolder()
    {
        var zip = ModelStore.Find(ModelCatalog.Piper) ?? throw new MediaException("The natural voices aren't downloaded yet.");
        // Unpacked among the downloads, which can be written to even when the zip ships with the app.
        var folder = Path.Combine(ModelStore.Folder, ModelStore.PiperRuntime);
        var exe = Path.Combine(folder, "piper", "piper.exe");
        if (File.Exists(exe))
            return Path.GetDirectoryName(exe)!;
        var staging = folder + ".part";
        if (Directory.Exists(staging))
            Directory.Delete(staging, recursive: true);
        ZipFile.ExtractToDirectory(zip, staging);
        if (Directory.Exists(folder))
            Directory.Delete(folder, recursive: true);
        Directory.Move(staging, folder);
        return Path.GetDirectoryName(exe)!;
    }

    private static async Task<PcmFormat> NaturalAsync(List<string> chunks, SpeechVoice voice, double rate, string raw, IProgress<double> progress, CancellationToken ct)
    {
        if (!voice.IsReady)
            throw new MediaException($"The voice {voice.Name} isn't downloaded yet.");
        var folder = PiperFolder();
        var model = ModelStore.Find(voice.Models[1])!;
        var settings = ModelStore.Find(voice.Models[2])!;
        int sampleRate;
        using (var json = JsonDocument.Parse(await File.ReadAllBytesAsync(settings, ct).ConfigureAwait(false)))
            sampleRate = json.RootElement.GetProperty("audio").GetProperty("sample_rate").GetInt32();

        var start = new ProcessStartInfo(Path.Combine(folder, "piper.exe"))
        {
            WorkingDirectory = folder,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
        };
        foreach (var arg in new[] { "--model", model, "--config", settings, "--output_raw", "--quiet",
                     "--length_scale", (1 / rate).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture), "--sentence_silence", "0.3" })
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new MediaException("Piper didn't start.");
        using var kill = ct.Register(() => { try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } });
        var errors = process.StandardError.ReadToEndAsync(CancellationToken.None);

        // Each piece is one line; Piper answers with audio as it goes.
        var feeding = Task.Run(async () =>
        {
            try
            {
                foreach (var chunk in chunks)
                    await process.StandardInput.WriteLineAsync(chunk.Replace('\n', ' ')).ConfigureAwait(false);
            }
            finally
            {
                process.StandardInput.Close();
            }
        }, CancellationToken.None);

        double expected = Math.Max(1, EstimateDuration(string.Join(' ', chunks), rate).TotalSeconds * sampleRate * 2);
        await using (var file = new FileStream(raw, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
        {
            var buffer = new byte[1 << 16];
            long written = 0;
            int read;
            var output = process.StandardOutput.BaseStream;
            while ((read = await output.ReadAsync(buffer, CancellationToken.None).ConfigureAwait(false)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, read), CancellationToken.None).ConfigureAwait(false);
                written += read;
                progress.Report(Math.Min(0.97, written / expected));
            }
        }
        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        try { await feeding.ConfigureAwait(false); } catch (IOException) { }
        if (process.ExitCode != 0)
            throw new MediaException($"Piper stopped: {(await errors.ConfigureAwait(false)).Trim()}");
        progress.Report(1);
        return new PcmFormat(sampleRate, 1);
    }
}
