using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Petal.Core.Models;

namespace Petal.Recipes.Translation;

/// <summary>
/// Translation by meaning between any two languages, on this PC: one Gemma 4 model (Google, Apache-2.0) for every pair, with
/// nothing passing through English. Measured on hard sentences it was right where the models made for a pair and M2M100
/// weren't. The largest one here translates files; the quickest one here keeps up with live captions.
/// </summary>
public static partial class GemmaTranslation
{
    /// <summary>The models, marked as translation's: a key can do this job, which isn't the job they do elsewhere.</summary>
    private static ModelInfo Mark(ModelInfo model) => model with { Id = "translate-" + model.Id };

    private static readonly ModelInfo[] Accurate = [ModelCatalog.GemmaTranslator, ModelCatalog.GemmaQuick];
    private static readonly ModelInfo[] Quick = [ModelCatalog.GemmaQuick, ModelCatalog.GemmaTranslator];

    /// <summary>Whether translation can run on this PC.</summary>
    public static bool Ready => ModelStore.IsAvailable(ModelCatalog.LlamaCpp) && Accurate.Any(ModelStore.IsAvailable);

    /// <summary>What translating needs: the model here already, or else the one that does well on any PC.</summary>
    public static IReadOnlyList<ModelInfo> Models => [Mark(ModelCatalog.LlamaCpp), Mark(Accurate.FirstOrDefault(ModelStore.IsAvailable) ?? ModelCatalog.GemmaQuick)];

    /// <summary>What live captions are best with: the quick model, whatever else is here.</summary>
    public static IReadOnlyList<ModelInfo> LiveModels => [Mark(ModelCatalog.LlamaCpp), Mark(ModelCatalog.GemmaQuick)];

    private static string Name(string code)
    {
        if (Translator.Languages.FirstOrDefault(l => l.Code == code) is { } known)
            return known.EnglishName;
        try
        {
            return CultureInfo.GetCultureInfo(code).EnglishName;
        }
        catch (CultureNotFoundException)
        {
            return "";
        }
    }

    /// <summary>Text that can't close the model's turn or open another.</summary>
    private static string Plain(string text) => text.Replace("<|", "< |").Replace("|>", "| >");

    internal static string Prompt(string text, string from, string to, IReadOnlyList<string>? earlier)
    {
        string source = from is "" or "auto" || Name(from).Length == 0 ? "the language it is in" : Name(from);
        string target = Name(to);
        var words = Plain(text).Trim();
        var asked = earlier is { Count: > 0 }
            ? "The lines just before it, for context only; do not translate them:\n" + string.Join('\n', earlier.Select(Plain)) + "\n\nThe text to translate:\n" + words
            : words;
        return $"<|turn>system\nYou are a professional translator. Translate the user's text from {source} into {target}. Keep its full meaning, names and numbers, and any line breaks. Write natural {target}. Reply with the translation only, with no notes.<turn|>\n<|turn>user\n{asked}<turn|>\n<|turn>model\n";
    }

    /// <summary>The text in another language. <paramref name="earlier"/> are the lines before it, so a line that goes on from them reads right.</summary>
    /// <param name="live">For captions: the quickest model here rather than the most accurate.</param>
    public static async Task<string> TranslateAsync(string text, string from, string to, IReadOnlyList<string>? earlier = null, bool live = false, CancellationToken cancellationToken = default)
    {
        if (text.Trim().Length == 0)
            return text;
        var model = (live ? Quick : Accurate).FirstOrDefault(ModelStore.IsAvailable) ?? throw new FileNotFoundException("The translation model isn't downloaded yet.");
        if (text.Length > Longest)
        {
            var parts = new List<string>();
            foreach (var part in Parts(text))
            {
                parts.Add(await TranslateAsync(part, from, to, parts.Count == 0 ? earlier : null, live, cancellationToken).ConfigureAwait(false));
            }
            return string.Join(' ', parts);
        }
        var prompt = Prompt(text, from, to, earlier);
        int most = Math.Clamp(text.Length * 2 + 40, 60, 2000);
        var said = Reply(await GemmaServer.CompleteAsync(model, prompt, most, 0, 1, cancellationToken).ConfigureAwait(false));
        if (Looped(said, text))
        {
            // Stuck on its own words: asked again, told not to.
            said = Reply(await GemmaServer.CompleteAsync(model, prompt, most, 0.2, 1.3, cancellationToken).ConfigureAwait(false));
            if (Looped(said, text))
                return FirstOfLoop(said);
        }
        // Proofread by the same model, which mends its own slips of word and agreement. Measured on 32 translations: five
        // mended, none harmed, at about twice the time; so not where that would make captions fall behind.
        if (said.Length == 0 || (live && !GemmaServer.OnCard))
            return said;
        var read = Reply(await GemmaServer.CompleteAsync(model, ProofPrompt(text, said, from, to), most, 0, 1, cancellationToken).ConfigureAwait(false));
        return Proofread(said, read, text);
    }

    /// <summary>A reply without the empty room some of these models write for their thoughts first.</summary>
    internal static string Reply(string said)
    {
        said = Regex.Replace(said, @"<\|channel>.*?<channel\|>", "", RegexOptions.Singleline);
        int open = said.IndexOf("<|channel>", StringComparison.Ordinal);
        return (open >= 0 ? said[..open] : said).Trim();
    }

    internal static string ProofPrompt(string text, string translation, string from, string to)
    {
        string source = from is "" or "auto" || Name(from).Length == 0 ? "another language" : Name(from);
        string target = Name(to);
        return $"<|turn>system\nYou are a meticulous {target} editor. Below are a text in {source} and its {target} translation. Check the translation word by word: wrong words, wrong grammar or agreement, and anything whose meaning differs from the original. Correct what is wrong and change nothing else. If it is already right, repeat it exactly. Reply with the final {target} translation only, with no notes.<turn|>\n<|turn>user\nOriginal:\n{Plain(text).Trim()}\n\nTranslation:\n{Plain(translation)}<turn|>\n<|turn>model\n";
    }

    /// <summary>The proofread text, unless it stopped being the same translation: an editor mends words, and doesn't lose half of them or go round.</summary>
    internal static string Proofread(string translation, string read, string source)
    {
        int before = Word().Matches(translation).Count, after = Word().Matches(read).Count;
        return read.Length == 0 || Looped(read, source) || after < before * 0.7 || after > before * 1.4 + 2 ? translation : read;
    }

    /// <summary>The most characters asked about at once, with room for its translation in what the model holds.</summary>
    internal const int Longest = 2500;

    /// <summary>A long text cut after a sentence where it can be, otherwise after a word.</summary>
    internal static IEnumerable<string> Parts(string text)
    {
        for (int start = 0; start < text.Length;)
        {
            int end = Math.Min(start + Longest, text.Length);
            if (end < text.Length)
            {
                int cut = text.LastIndexOfAny(['.', '!', '?', '؟', '۔', '。', '\n'], end - 1, end - start);
                if (cut <= start)
                    cut = text.LastIndexOf(' ', end - 1, end - start);
                if (cut > start)
                    end = cut + 1;
            }
            if (text[start..end].Trim() is { Length: > 0 } part)
                yield return part;
            start = end;
        }
    }

    /// <summary>A batch is over: the model is let go soon, not in a few minutes, since whatever comes next may want the memory.</summary>
    public static void Finished() => GemmaServer.LetGoSoon();

    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex Word();

    /// <summary>Whether a reply has gone round in circles: the same few words again and again where the text doesn't, or far too long for it.</summary>
    internal static bool Looped(string said, string source)
    {
        var words = Word().Matches(said).Select(m => m.Value).ToList();
        var asked = Word().Matches(source).Select(m => m.Value).ToList();
        return words.Count > asked.Count * 4 + 16 || (GoesRound(words) && !GoesRound(asked));
    }

    /// <summary>The same two to eight words three times running. The text is asked about too: someone who says "no, no, no" is translated so.</summary>
    private static bool GoesRound(List<string> words)
    {
        for (int length = 2; length <= 8; length++)
        {
            for (int start = 0; start + length * 3 <= words.Count; start++)
            {
                bool again = true;
                for (int i = 0; i < length * 2 && again; i++)
                    again = words[start + i].Equals(words[start + length + i], StringComparison.OrdinalIgnoreCase);
                if (again)
                    return true;
            }
        }
        return false;
    }

    /// <summary>What was said before it started going round: up to the end of its first sentence.</summary>
    internal static string FirstOfLoop(string said)
    {
        var first = Regex.Match(said, @"^.*?[.!?؟۔。](?=\s|$)", RegexOptions.Singleline);
        return first.Success ? first.Value : said;
    }
}

/// <summary>
/// The model kept loaded by llama.cpp's server while it is being used, so each sentence takes its own time and not the
/// model's loading too, and let go after a few idle minutes. On a graphics card with room for it, where that was measured
/// to be several times quicker; on the processor otherwise, or as soon as the card fails it. It listens only on this PC,
/// with a key made for the run, and goes when this program does, however that happens.
/// </summary>
internal static class GemmaServer
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private static readonly TimeSpan Idle = TimeSpan.FromMinutes(3);
    private static Process? _process;
    private static string _running = "";
    private static string _address = "";
    private static string _key = "";
    private static bool _onCard;
    private static bool _cardFailed;
    private static bool _gettingCard;
    private static DateTime _used;
    private static Timer? _idle;
    private static IntPtr _job;

    /// <summary>For tests: a server in place of the real one.</summary>
    internal static string? TestServer { get; set; }

    public static async Task<string> CompleteAsync(ModelInfo model, string prompt, int most, double temperature, double repeatPenalty, CancellationToken cancellationToken)
    {
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            for (int attempt = 0; ; attempt++)
            {
                if (TestServer is null)
                    await EnsureAsync(model, cancellationToken).ConfigureAwait(false);
                _idle?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                // A hung card takes the machine's drawing with it: it gets what the longest reply could need, and no more.
                limit.CancelAfter(_onCard ? TimeSpan.FromSeconds(30 + most / 5) : TimeSpan.FromMinutes(10));
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Post, (TestServer ?? _address) + "/completion")
                    {
                        Content = new StringContent(JsonSerializer.Serialize(new Dictionary<string, object>
                        {
                            ["prompt"] = prompt,
                            ["n_predict"] = most,
                            ["temperature"] = temperature,
                            ["repeat_penalty"] = repeatPenalty,
                            ["stop"] = new[] { "<turn|>", "<|turn>" },
                            ["cache_prompt"] = false,
                        }), Encoding.UTF8, "application/json"),
                    };
                    if (_key.Length > 0)
                        request.Headers.Authorization = new("Bearer", _key);
                    using var response = await Http.SendAsync(request, limit.Token).ConfigureAwait(false);
                    response.EnsureSuccessStatusCode();
                    using var reply = JsonDocument.Parse(await response.Content.ReadAsStringAsync(limit.Token).ConfigureAwait(false));
                    return AskPetal.CleanAnswer(reply.RootElement.GetProperty("content").GetString() ?? "");
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested && attempt == 0 && ex is HttpRequestException or OperationCanceledException or JsonException)
                {
                    // It stopped or hung. On the card: never again this run. Once more, on the processor if so.
                    Log($"The translation model stopped ({ex.Message}).");
                    if (_onCard)
                        CardFailed();
                    Stop();
                }
                finally
                {
                    _used = DateTime.UtcNow;
                    _idle?.Change(Idle, Timeout.InfiniteTimeSpan);
                }
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException("The AI model stopped unexpectedly.", ex);
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>Let go in a moment, unless something else is asked for by then.</summary>
    public static void LetGoSoon() => _idle?.Change(TimeSpan.FromSeconds(20), Timeout.InfiniteTimeSpan);

    private static void Log(string message) => Trace.WriteLine("GemmaServer: " + message);

    private static async Task EnsureAsync(ModelInfo model, CancellationToken cancellationToken)
    {
        var weights = ModelStore.Find(model) ?? throw new FileNotFoundException("The translation model isn't downloaded yet.");
        // Either model will do while the other is at work (captions on during a document, say): changing over for each sentence wouldn't.
        if (_process is { HasExited: false } && (_running == weights || DateTime.UtcNow - _used < TimeSpan.FromSeconds(20)))
            return;
        Stop();
        _idle ??= new Timer(_ => StopWhenFree());
        var card = CardRuledOut() ? null : await CardFor(model, cancellationToken).ConfigureAwait(false);
        _onCard = true;
        if (card is { } plan)
        {
            if (await StartAsync(plan.Folder, weights, plan.Device, plan.Layers, cancellationToken).ConfigureAwait(false))
                return;
            CardFailed();
        }
        _onCard = false;
        if (!await StartAsync(AskPetal.RuntimeFolder(), weights, null, 0, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("The AI model didn't start.");
    }

    private static async Task<bool> StartAsync(string folder, string weights, string? device, int layers, CancellationToken cancellationToken)
    {
        int port;
        var free = new TcpListener(System.Net.IPAddress.Loopback, 0);
        free.Start();
        port = ((System.Net.IPEndPoint)free.LocalEndpoint).Port;
        free.Stop();
        _key = Guid.NewGuid().ToString("N");
        var start = new ProcessStartInfo(Path.Combine(folder, "llama-server.exe"))
        {
            WorkingDirectory = folder,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in new[] { "-m", weights, "--host", "127.0.0.1", "--port", port.ToString(CultureInfo.InvariantCulture), "--api-key", _key, "-c", "4096", "-np", "1", "--no-webui", "-ngl", layers.ToString(CultureInfo.InvariantCulture) })
            start.ArgumentList.Add(arg);
        if (device is not null)
        {
            start.ArgumentList.Add("--device");
            start.ArgumentList.Add(device);
        }
        var process = Process.Start(start);
        if (process is null)
            return false;
        Tie(process);
        process.OutputDataReceived += (_, _) => { };
        process.ErrorDataReceived += (_, _) => { };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        _process = process;
        _running = weights;
        _address = $"http://127.0.0.1:{port}";
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < TimeSpan.FromMinutes(3))
        {
            if (process.HasExited)
                return false;
            try
            {
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                limit.CancelAfter(TimeSpan.FromSeconds(5));
                using var health = await Http.GetAsync(_address + "/health", limit.Token).ConfigureAwait(false);
                if (health.IsSuccessStatusCode)
                    return true;
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested && ex is HttpRequestException or OperationCanceledException)
            {
            }
            await Task.Delay(200, cancellationToken).ConfigureAwait(false);
        }
        Stop();
        return false;
    }

    private static void StopWhenFree()
    {
        if (!Gate.Wait(0))
            return;
        try
        {
            Stop();
        }
        finally
        {
            Gate.Release();
        }
    }

    private static void Stop()
    {
        try
        {
            if (_process is { HasExited: false } running)
                running.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
        _process?.Dispose();
        _process = null;
        _running = "";
    }

    // ---------- the graphics card ----------

    /// <summary>Whether the model now running is on a graphics card, where a second reading costs little.</summary>
    public static bool OnCard => _onCard;

    private static string CardFailedMark => Path.Combine(ModelStore.Folder, "llama-card-failed.txt");

    /// <summary>
    /// The card failed: not used again in this run, nor for a week. Should it have hung, every run wouldn't hang it again;
    /// should it have been short of memory that once, it isn't ruled out for good.
    /// </summary>
    private static void CardFailed()
    {
        _cardFailed = true;
        try
        {
            File.WriteAllText(CardFailedMark, DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static bool CardRuledOut() =>
        _cardFailed || (File.Exists(CardFailedMark) && DateTime.UtcNow - File.GetLastWriteTimeUtc(CardFailedMark) < TimeSpan.FromDays(7));

    /// <summary>The card to run on and how much of the model it takes; null when the processor is the better place.</summary>
    private static async Task<(string Folder, string Device, int Layers)?> CardFor(ModelInfo model, CancellationToken cancellationToken)
    {
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64 || !CardNamed())
            return null;
        var folder = CardRuntime();
        if (folder is null)
        {
            // A small download, in the background: from the next time, the card.
            if (!_gettingCard)
            {
                _gettingCard = true;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await ModelStore.DownloadAsync(ModelCatalog.LlamaVulkan).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or TaskCanceledException or UnauthorizedAccessException)
                    {
                        Log($"The graphics card's program didn't download ({ex.Message}).");
                    }
                }, CancellationToken.None);
            }
            return null;
        }
        var start = new ProcessStartInfo(Path.Combine(folder, "llama-server.exe"), "--list-devices")
        {
            WorkingDirectory = folder,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        string listing;
        using (var process = Process.Start(start))
        {
            if (process is null)
                return null;
            var errors = process.StandardError.ReadToEndAsync(cancellationToken);
            listing = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false) + await errors.ConfigureAwait(false);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        return Plan(listing, model.Size) is { } plan ? (folder, plan.Device, plan.Layers) : null;
    }

    /// <summary>
    /// From llama.cpp's list of devices, the card with the most room, leaving out graphics built into the processor (measured
    /// slower than the processor itself). All of the model when it fits with room to spare; otherwise part of it if that is
    /// worthwhile, which was measured to help the larger model too.
    /// </summary>
    internal static (string Device, int Layers)? Plan(string listing, long modelSize)
    {
        (string Device, long Free)? best = null;
        foreach (Match device in Regex.Matches(listing, @"(Vulkan\d+): (.+?) \(\d+ MiB, (\d+) MiB free\)"))
        {
            if (Regex.IsMatch(device.Groups[2].Value, @"Iris|UHD|HD Graphics|Radeon\(TM\) Graphics|Radeon Graphics|Radeon \d+M|llvmpipe|Microsoft", RegexOptions.IgnoreCase))
                continue;
            long free = long.Parse(device.Groups[3].Value, CultureInfo.InvariantCulture) * 1024 * 1024;
            if (best is null || free > best.Value.Free)
                best = (device.Groups[1].Value, free);
        }
        if (best is not { } card)
            return null;
        long room = card.Free - 800L * 1024 * 1024;
        return room >= modelSize ? (card.Device, 99) : room >= modelSize / 2 ? (card.Device, 25) : null;
    }

    /// <summary>Whether Windows lists a graphics card of the kind worth using.</summary>
    private static bool CardNamed()
    {
        try
        {
            using var adapters = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            foreach (var name in adapters?.GetSubKeyNames() ?? [])
            {
                if (!name.All(char.IsDigit))
                    continue;
                using var adapter = adapters!.OpenSubKey(name);
                if (adapter?.GetValue("DriverDesc") is string description && Regex.IsMatch(description, @"NVIDIA|Radeon (RX|Pro)|Intel\(R\) Arc", RegexOptions.IgnoreCase))
                    return true;
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
        }
        return false;
    }

    /// <summary>The graphics build's folder, unpacked from its download the first time; null while it isn't here.</summary>
    private static string? CardRuntime()
    {
        if (ModelStore.Find(ModelCatalog.LlamaVulkan) is not { } zip)
            return null;
        var folder = Path.Combine(ModelStore.Folder, "llama-runtime-vulkan");
        if (File.Exists(Path.Combine(folder, "llama-server.exe")))
            return folder;
        var staging = folder + ".part";
        if (Directory.Exists(staging))
            Directory.Delete(staging, recursive: true);
        ZipFile.ExtractToDirectory(zip, staging);
        if (Directory.Exists(folder))
            Directory.Delete(folder, recursive: true);
        Directory.Move(staging, folder);
        return folder;
    }

    // ---------- going when this program goes ----------

    /// <summary>Windows ends the server when this process ends, even if it crashes: seven gigabytes aren't left behind.</summary>
    private static void Tie(Process process)
    {
        if (_job == IntPtr.Zero)
        {
            _job = CreateJobObject(IntPtr.Zero, null);
            var limits = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION { BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION { LimitFlags = 0x2000 } };
            SetInformationJobObject(_job, 9, ref limits, (uint)Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>());
        }
        AssignProcessToJobObject(_job, process.Handle);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }
}
