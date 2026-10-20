using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using ImageMagick;
using Petal.Core.Formats;
using Petal.Core.Jobs;
using Petal.Core.Tools;
using Petal.Engines.Documents;
using Petal.Engines.Documents.Pdf;
using Petal.Engines.Files.Archives;
using Petal.Engines.Files.Subtitles;
using Petal.Engines.Imaging;
using Petal.Engines.Media;
using F = Petal.Core.Formats.Formats;

namespace Petal.Cli;

/// <summary>The commands. Results are printed one path per line, so they can be piped.</summary>
internal sealed class Cli(TextWriter output, TextWriter error, bool showProgress)
{
    private const int Ok = 0, Failed = 1, Usage = 2, Cancelled = 130;

    private ConverterRegistry? _converters;

    private static string Version => Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";

    private ConverterRegistry Converters => _converters ??= CreateConverters();

    /// <summary>The same engines, in the same order, as the app.</summary>
    private static ConverterRegistry CreateConverters()
    {
        try { MagickNET.SetLogEvents(LogEventTypes.None); OpenCL.IsEnabled = false; } catch { /* older ImageMagick builds */ }
        var converters = new ConverterRegistry();
        converters.Register(new ImageConverter());
        converters.Register(new MediaConverter());
        converters.Register(new ArchiveConverter());
        converters.Register(new SubtitleConverter());
        DocumentEngine.Register(converters);
        return converters;
    }

    public async Task<int> RunAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        if (args.Count == 0 || args[0] is "help" or "--help" or "-h" or "/?")
        {
            output.Write(Help);
            return Ok;
        }
        var rest = args.Skip(1).ToList();
        try
        {
            return args[0].ToLowerInvariant() switch
            {
                "convert" => await ConvertAsync(Arguments.Parse(rest), ct),
                "resize" => await ResizeAsync(Arguments.Parse(rest, "enlarge", "ai"), ct),
                "compress" => await CompressAsync(Arguments.Parse(rest, "strong"), ct),
                "zip" => await ZipAsync(Arguments.Parse(rest), ct),
                "qr" => await QrAsync(Arguments.Parse(rest)),
                "text" => await TextAsync(Arguments.Parse(rest), ct),
                "codes" => await CodesAsync(Arguments.Parse(rest), ct),
                "info" => await InfoAsync(Arguments.Parse(rest), ct),
                "formats" => Formats(Arguments.Parse(rest)),
                "version" or "--version" or "-v" => Print($"Petal {Version}"),
                _ => throw new UsageException($"Unknown command \"{args[0]}\". Try \"petal help\"."),
            };
        }
        catch (UsageException ex)
        {
            error.WriteLine(ex.Message);
            return Usage;
        }
        catch (OperationCanceledException)
        {
            ClearProgress();
            error.WriteLine("Cancelled.");
            return Cancelled;
        }
    }

    private int Print(string line)
    {
        output.WriteLine(line);
        return Ok;
    }

    public const string Help = """
        Petal — convert and tidy up files from the command line.

        Usage:
          petal convert <files> --to <format> [--max <px>] [--quality <n>] [--out <folder>]
          petal resize <images> (--max <px> | --width <px> | --height <px> | --percent <n>)
                       [--to <format>] [--enlarge] [--ai] [--out <folder>]
          petal compress <files> [--strong] [--out <folder>]
          petal zip <files or folders> [-o <archive.zip>] [--password <text>]
          petal qr <text> [-o <code.png | code.svg>]
          petal text <image>        Print the text in a picture
          petal codes <image>       Print the QR codes and barcodes in a picture
          petal info <file>         Format, size, dimensions or duration
          petal formats [<file>]    What each format (or this file) converts to
          petal version

        Notes:
          Wildcards work everywhere: petal convert *.heic --to jpg
          --max keeps pictures and videos within that many pixels on the longest side.
          --quality is 1-100 for pictures, the bitrate in kbps for audio, and the CRF for
          video (17 best ... 28 small). Without it, the quality set in Petal is used.
          Results are saved beside the originals unless --out names a folder; nothing is
          ever overwritten. Each saved path is printed on its own line.

        """;

    // ---------- convert ----------

    private async Task<int> ConvertAsync(Arguments a, CancellationToken ct)
    {
        a.Only("to", "max", "quality", "out");
        var target = FormatNamed(a.Get("to") ?? throw new UsageException("Say which format with --to, for example --to jpg."));
        var favourite = new Favourite { Format = target.Id, LongestSide = a.Int("max", 16, 20000) ?? 0, Quality = a.Int("quality", 1, 512) ?? 0 };
        var settings = favourite.Apply(UserDefaults());
        var files = a.Files();
        var outDir = a.OutputFolder();
        int failures = 0;
        foreach (var file in files)
        {
            var from = F.FromPath(file);
            if (from is null)
            {
                failures += Fail(file, "Petal doesn't know this kind of file.");
                continue;
            }
            if (favourite.ConverterFor(from, Converters) is not { } converter)
            {
                failures += Fail(file, from == target
                    ? $"It's already {target.Label}. Add --max or --quality to make a smaller copy."
                    : $"{from.Label} can't be converted to {target.Label}{(ConversionMatrix.CanConvert(from, target) ? " on this PC (a helper program is missing)" : "")}.");
                continue;
            }
            var path = from == target
                ? OutputNaming.WithSuffix(file, SameFormatSuffix(favourite), target.WriteExtension, outDir)
                : OutputNaming.ForConversion(file, target, outDir);
            failures += await StepAsync(file, async progress =>
            {
                var result = await converter.ConvertAsync(new ConversionJob(file, from, target, path, settings), progress, ct);
                return result.OutputPaths;
            }, ct);
        }
        return failures == 0 ? Ok : Failed;
    }

    private static string SameFormatSuffix(Favourite f) => f.LongestSide > 0 ? $"{f.LongestSide}px" : $"q{f.Quality}";

    private static FileFormat FormatNamed(string name)
    {
        var id = name.Trim().TrimStart('.').ToLowerInvariant();
        var format = F.FromId(id) ?? F.FromPath("x." + id);
        if (format is not { CanWrite: true })
            throw new UsageException($"\"{name}\" isn't a format Petal can write. See \"petal formats\".");
        return format;
    }

    // ---------- resize ----------

    private async Task<int> ResizeAsync(Arguments a, CancellationToken ct)
    {
        a.Only("max", "width", "height", "percent", "to", "enlarge", "ai", "out");
        var modes = new[] { "max", "width", "height", "percent" }.Where(a.Has).ToList();
        if (modes.Count != 1)
            throw new UsageException("Give exactly one of --max, --width, --height or --percent.");
        var transform = modes[0] switch
        {
            "max" => new ImageTransform { Mode = ResizeMode.Fit, Width = a.Int("max", 1, 65000)!.Value, Height = a.Int("max", 1, 65000)!.Value },
            "width" => new ImageTransform { Mode = ResizeMode.Width, Width = a.Int("width", 1, 65000)!.Value },
            "height" => new ImageTransform { Mode = ResizeMode.Height, Height = a.Int("height", 1, 65000)!.Value },
            _ => new ImageTransform { Mode = ResizeMode.Percent, Percent = a.Int("percent", 1, 1000)!.Value },
        };
        transform = transform with { Enlarge = a.Has("enlarge") || a.Has("ai"), AiEnlarge = a.Has("ai") };
        if (transform.AiEnlarge && !AiUpscaler.IsReady)
            throw new UsageException("The AI enlargement model isn't installed; leave out --ai.");
        FileFormat? to = a.Get("to") is { } name ? FormatNamed(name) : null;
        if (to is not null && to.Family != FormatFamily.Image)
            throw new UsageException("Resized pictures can be saved as picture formats only.");
        var tool = ToolRegistry.Get(ToolId.ResizeImage);
        var outDir = a.OutputFolder();
        int failures = 0;
        foreach (var file in a.Files())
        {
            if (F.FromPath(file) is not { Family: FormatFamily.Image or FormatFamily.Vector })
            {
                failures += Fail(file, "Only pictures can be resized.");
                continue;
            }
            var path = OutputNaming.ForTool(file, tool, outputDirectory: outDir, extensionOverride: to?.WriteExtension);
            failures += await StepAsync(file, async progress =>
            {
                await ImageTransformer.TransformAsync(file, path, transform, progress, ct);
                return [path];
            }, ct);
        }
        return failures == 0 ? Ok : Failed;
    }

    // ---------- compress ----------

    private async Task<int> CompressAsync(Arguments a, CancellationToken ct)
    {
        a.Only("strong", "out");
        bool strong = a.Has("strong");
        var tool = ToolRegistry.Get(ToolId.Compress);
        var outDir = a.OutputFolder();
        int failures = 0;
        foreach (var file in a.Files())
        {
            var format = F.FromPath(file);
            Func<IProgress<double>, Task<(string Path, long Before, long After)>>? work = format?.Family switch
            {
                FormatFamily.Image or FormatFamily.Vector => async progress =>
                {
                    var path = OutputNaming.ForTool(file, tool, outputDirectory: outDir);
                    var r = await ImageCompressor.CompressAsync(file, path, new ImageCompressOptions { Preset = strong ? CompressionPreset.Strong : CompressionPreset.Balanced }, progress, ct);
                    return (r.OutputPath, r.OriginalBytes, r.OutputBytes);
                },
                FormatFamily.Audio or FormatFamily.Video => async progress =>
                {
                    var ext = MediaCompressor.CompressedExtension(file);
                    var path = OutputNaming.ForTool(file, tool, outputDirectory: outDir, extensionOverride: ext == OutputNaming.ExtensionOf(file) ? null : ext);
                    var r = await MediaCompressor.CompressAsync(file, path, new CompressOptions(strong ? CompressPreset.Strong : CompressPreset.Balanced) { Settings = UserDefaults() }, progress, ct);
                    return (r.OutputPath, r.OriginalBytes, r.OutputBytes);
                },
                FormatFamily.Pdf => async progress =>
                {
                    var path = OutputNaming.ForTool(file, tool, outputDirectory: outDir);
                    var r = await PdfCompressor.CompressAsync(file, path, new PdfCompressOptions { Preset = strong ? PdfCompressionPreset.Strong : PdfCompressionPreset.Balanced }, progress, ct);
                    return (r.Written ? path : file, r.OriginalBytes, r.Written ? r.OutputBytes : r.OriginalBytes);
                },
                _ => null,
            };
            if (work is null)
            {
                failures += Fail(file, "Only pictures, audio, video and PDFs can be compressed.");
                continue;
            }
            failures += await StepAsync(file, async progress =>
            {
                var (path, before, after) = await work(progress);
                if (path == file || after >= before)
                {
                    Note($"{Path.GetFileName(file)} is already as small as it gets.");
                    return path == file ? [] : [path];
                }
                Note($"{Path.GetFileName(file)}: {Size(before)} → {Size(after)} ({Math.Round(100 * (1 - (double)after / before)).ToString(CultureInfo.InvariantCulture)}% smaller)");
                return [path];
            }, ct);
        }
        return failures == 0 ? Ok : Failed;
    }

    // ---------- zip, qr ----------

    private async Task<int> ZipAsync(Arguments a, CancellationToken ct)
    {
        a.Only("o", "output", "password");
        var paths = a.Files(allowFolders: true);
        var target = a.Get("o", "output") is { } named
            ? Path.GetFullPath(named.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? named : named + ".zip")
            : ZipCreator.DefaultOutput(paths);
        if (File.Exists(target))
            throw new UsageException($"{target} already exists.");
        var password = a.Get("password");
        return await StepAsync(paths[0], async progress => [await ZipCreator.CreateAsync(paths, target, new ZipOptions { Password = password }, progress, ct)], ct) == 0 ? Ok : Failed;
    }

    private async Task<int> QrAsync(Arguments a)
    {
        a.Only("o", "output");
        if (a.Values.Count == 0)
            throw new UsageException("Give the text or link to put in the code, in quotes.");
        var text = string.Join(' ', a.Values);
        bool[,] modules;
        try
        {
            modules = QrGenerator.Encode(text);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            error.WriteLine("That is too long for a QR code.");
            return Failed;
        }
        if (a.Get("o", "output") is not { } file)
        {
            output.Write(Terminal(modules));
            return Ok;
        }
        var path = Path.GetFullPath(file);
        if (path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
            await File.WriteAllTextAsync(path, QrGenerator.ToSvg(modules, "#000000", "#FFFFFF"));
        else if (path.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            await File.WriteAllBytesAsync(path, QrGenerator.ToPng(modules, 16, MagickColors.Black, MagickColors.White));
        else
            throw new UsageException("Save the code as .png or .svg.");
        return Print(path);
    }

    /// <summary>The code drawn with half blocks, two rows per line, light modules lit (for dark terminals).</summary>
    internal static string Terminal(bool[,] modules)
    {
        int n = modules.GetLength(0), quiet = 2;
        bool Dark(int x, int y) => x >= 0 && y >= 0 && x < n && y < n && modules[x, y];
        var sb = new System.Text.StringBuilder();
        for (int y = -quiet; y < n + quiet; y += 2)
        {
            for (int x = -quiet; x < n + quiet; x++)
            {
                bool top = !Dark(x, y), bottom = !Dark(x, y + 1) && y + 1 < n + quiet;
                sb.Append(top && bottom ? '█' : top ? '▀' : bottom ? '▄' : ' ');
            }
            sb.Append('\n');
        }
        return sb.ToString();
    }

    // ---------- text, codes, info, formats ----------

    private async Task<int> TextAsync(Arguments a, CancellationToken ct)
    {
        a.Only();
        var file = Single(a);
        if (F.FromPath(file) is not { Family: FormatFamily.Image })
            throw new UsageException("Name a picture; for PDFs use: petal convert file.pdf --to txt");
        if (!DocumentEngine.IsOcrAvailable)
        {
            error.WriteLine("Windows text recognition isn't available. Add a language with OCR in Windows Settings › Time & language.");
            return Failed;
        }
        var text = await ImageRedactor.ReadTextAsync(file, ct);
        if (string.IsNullOrWhiteSpace(text))
        {
            error.WriteLine("No text found.");
            return Failed;
        }
        output.WriteLine(text.TrimEnd());
        return Ok;
    }

    private async Task<int> CodesAsync(Arguments a, CancellationToken ct)
    {
        a.Only();
        var file = Single(a);
        var codes = await BarcodeScanner.ScanAsync(file, ct);
        if (codes.Count == 0)
        {
            error.WriteLine("No QR code or barcode found.");
            return Failed;
        }
        foreach (var code in codes)
            output.WriteLine(code.Text);
        return Ok;
    }

    private async Task<int> InfoAsync(Arguments a, CancellationToken ct)
    {
        a.Only();
        var file = Single(a);
        var format = F.FromPath(file);
        var length = new FileInfo(file).Length;
        output.WriteLine($"{Path.GetFileName(file)}");
        output.WriteLine($"  Format    {format?.Label ?? "unknown"}");
        output.WriteLine($"  Size      {Size(length)} ({length.ToString("N0", CultureInfo.InvariantCulture)} bytes)");
        try
        {
            switch (format?.Family)
            {
                case FormatFamily.Image or FormatFamily.Vector:
                    var image = await ImageInspector.ReadAsync(file, ct);
                    output.WriteLine($"  Pixels    {image.DisplayWidth} × {image.DisplayHeight}{(image.FrameCount > 1 ? $", {image.FrameCount} frames" : "")}");
                    output.WriteLine($"  Colour    {image.ColorSpace}, {image.BitDepth}-bit{(image.HasAlpha ? ", transparent" : "")}{(image.ColorProfile is { } p ? $", {p}" : "")}");
                    break;
                case FormatFamily.Audio or FormatFamily.Video:
                    var media = await MediaProbe.ProbeAsync(file, ct);
                    output.WriteLine($"  Duration  {media.Duration:hh\\:mm\\:ss\\.ff}");
                    if (media.Video is { } v)
                        output.WriteLine($"  Video     {v.Codec}, {v.DisplayWidth} × {v.DisplayHeight}{(v.FrameRate > 0 ? $", {v.FrameRate.ToString("0.##", CultureInfo.InvariantCulture)} fps" : "")}");
                    if (media.Audio is { } au)
                        output.WriteLine($"  Audio     {au.Codec}, {au.Channels} ch, {au.SampleRate} Hz{(au.BitRate > 0 ? $", {au.BitRate / 1000} kbps" : "")}");
                    break;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            output.WriteLine($"  (Couldn't read the contents: {ex.Message})");
        }
        if (format is not null)
        {
            var targets = Converters.AvailableTargets([format]);
            output.WriteLine($"  Converts  {(targets.Count == 0 ? "—" : string.Join(", ", targets.Select(t => t.Label)))}");
        }
        return Ok;
    }

    private int Formats(Arguments a)
    {
        a.Only();
        if (a.Values.Count > 0)
        {
            var format = F.FromPath(a.Values[0]) ?? F.FromId(a.Values[0].TrimStart('.'));
            if (format is null)
                throw new UsageException($"Petal doesn't know \"{a.Values[0]}\".");
            var targets = Converters.AvailableTargets([format]);
            output.WriteLine(targets.Count == 0 ? $"{format.Label} has no conversions on this PC." : $"{format.Label} → {string.Join(", ", targets.Select(t => t.Label))}");
            return Ok;
        }
        foreach (var group in F.All.Where(f => f.CanRead).GroupBy(f => f.Family))
        {
            output.WriteLine(group.Key.ToString());
            foreach (var format in group)
            {
                var targets = Converters.AvailableTargets([format]);
                output.WriteLine($"  {format.Label,-7} → {(targets.Count == 0 ? "—" : string.Join(", ", targets.Select(t => t.Label)))}");
            }
        }
        return Ok;
    }

    private static string Single(Arguments a)
    {
        var files = a.Files();
        if (files.Count != 1)
            throw new UsageException("Name one file.");
        return files[0];
    }

    // ---------- running and reporting ----------

    /// <summary>Runs one file's work with a progress line; prints the results. Returns 1 when it failed.</summary>
    private async Task<int> StepAsync(string file, Func<IProgress<double>, Task<IReadOnlyList<string>>> work, CancellationToken ct)
    {
        var name = Path.GetFileName(file.TrimEnd('\\'));
        var progress = new Progress<double>(f => ShowProgress(name, f));
        ShowProgress(name, 0);
        try
        {
            var results = await work(progress);
            ClearProgress();
            foreach (var path in results)
                output.WriteLine(path);
            return 0;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            ClearProgress();
            ct.ThrowIfCancellationRequested();
            return Fail(file, ex.Message);
        }
    }

    private int Fail(string file, string message)
    {
        ClearProgress();
        error.WriteLine($"{Path.GetFileName(file)}: {message}");
        return 1;
    }

    private void Note(string message)
    {
        ClearProgress();
        error.WriteLine(message);
    }

    private readonly Lock _progressGate = new();
    private int _progressWidth;

    private void ShowProgress(string name, double fraction)
    {
        if (!showProgress)
            return;
        lock (_progressGate)
        {
            var line = $"{name} {Math.Round(Math.Clamp(fraction, 0, 1) * 100)}%";
            error.Write("\r" + line.PadRight(_progressWidth));
            _progressWidth = line.Length;
        }
    }

    private void ClearProgress()
    {
        if (!showProgress)
            return;
        lock (_progressGate)
        {
            if (_progressWidth == 0)
                return;
            error.Write("\r" + new string(' ', _progressWidth) + "\r");
            _progressWidth = 0;
        }
    }

    /// <summary>Decimal units, as in the app.</summary>
    private static string Size(long bytes) => bytes switch
    {
        < 1000 => $"{bytes} B",
        < 1000 * 1000 => (bytes / 1000.0).ToString("0", CultureInfo.InvariantCulture) + " KB",
        < 1000L * 1000 * 1000 => (bytes / 1e6).ToString("0.#", CultureInfo.InvariantCulture) + " MB",
        _ => (bytes / 1e9).ToString("0.##", CultureInfo.InvariantCulture) + " GB",
    };

    /// <summary>The quality defaults chosen in Petal's Settings, so the command line matches the app.</summary>
    internal static ConversionSettings UserDefaults(string? settingsFile = null)
    {
        settingsFile ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Petal", "settings.json");
        try
        {
            if (File.Exists(settingsFile))
            {
                var options = new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } };
                var saved = JsonSerializer.Deserialize<SavedSettings>(File.ReadAllText(settingsFile), options);
                if (saved?.Conversion is { } conversion)
                    return conversion;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
        }
        return new ConversionSettings();
    }

    private sealed class SavedSettings
    {
        public ConversionSettings? Conversion { get; set; }
    }
}
