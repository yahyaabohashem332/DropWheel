using Petal.Engines.Files.Design;
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
        converters.Register(new PaletteConverter());
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
                "searchable" => await SearchableAsync(Arguments.Parse(rest), ct),
                "speak" => await SpeakAsync(Arguments.Parse(rest), ct),
                "denoise" => await DenoiseAsync(Arguments.Parse(rest), ct),
                "separate" => await SeparateAsync(Arguments.Parse(rest, "four"), ct),
                "translate" => await TranslateAsync(Arguments.Parse(rest), ct),
                "ask" => await AskAsync(Arguments.Parse(rest, "run"), ct),
                "search" => Search(Arguments.Parse(rest)),
                "voices" => Voices(),
                "codes" => await CodesAsync(Arguments.Parse(rest), ct),
                "info" => await InfoAsync(Arguments.Parse(rest), ct),
                "layers" => await LayersAsync(Arguments.Parse(rest, "full", "hidden"), ct),
                "slim" => await SlimAsync(Arguments.Parse(rest), ct),
                "preview" => await PreviewAsync(Arguments.Parse(rest), ct),
                "collect" => await CollectAsync(Arguments.Parse(rest, "no-fonts"), ct),
                "colours" or "colors" => ColoursCommand(Arguments.Parse(rest)),
                "palette" => PaletteCommand(Arguments.Parse(rest)),
                "formats" => Formats(Arguments.Parse(rest)),
                "recipes" => ListRecipes(),
                "run" => await RunRecipeAsync(Arguments.Parse(rest), ct),
                "do" => await AskPetalAsync(new() { ["cmd"] = "do", ["action"] = One(rest, "petal do <petal>") }, ct),
                "open-wheel" => await AskPetalAsync(new() { ["cmd"] = "wheel", ["name"] = One(rest, "petal open-wheel <name>") }, ct),
                "commands" => await AskPetalAsync(new() { ["cmd"] = "commands", ["text"] = string.Join(' ', rest) }, ct),
                "with" => await WithAsync(rest, ct),
                "actions" => await ActionsAsync(ct),
                "status" => await StatusAsync(ct),
                "version" or "--version" or "-v" => Print($"DropWheel {Version}"),
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
        DropWheel — convert and tidy up files from the command line.

        Usage:
          petal convert <files> --to <format> [--max <px>] [--quality <n>] [--out <folder>]
          petal resize <images> (--max <px> | --width <px> | --height <px> | --percent <n>)
                       [--to <format>] [--enlarge] [--ai] [--out <folder>]
          petal compress <files> [--strong | --for whatsapp|email|discord|upload|instagram] [--out <folder>]
          petal zip <files or folders> [-o <archive.zip>] [--password <text>]
          petal qr <text> [-o <code.png | code.svg>]
          petal text <image>        Print the text in a picture
          petal speak <document> [--voice <name>] [--speed <0.5-2>] [--out <folder>]
                                    Reads a document aloud into an MP3
          petal voices              The voices installed, and the natural ones DropWheel can use
          petal denoise <files> [--strength gentle|strong|full] [--out <folder>]
                                    Takes noise out of speech in recordings and videos
          petal separate <files> [--four] [--out <folder>]
                                    Splits songs into vocals and music (or four parts)
          petal translate <subtitles> --to <language> [--from <language>] [--out <folder>]
                                    Subtitles (SRT, VTT) into en, ar, tr, fa, ur, id, fr or es
          (These three use AI models DropWheel downloads once, from its windows.)
          petal searchable <pdfs or pictures> [--out <folder>]
                                    Scans get a text layer so they can be searched and copied
                                    (pictures of pages become one PDF)
          petal codes <image>       Print the QR codes and barcodes in a picture
          petal info <file>         Format, size, dimensions or duration; layers, pages, fonts and links of Adobe files
          petal layers <psd> [--full] [--hidden] [--out <folder>]
                                    Every layer of a Photoshop file as a PNG named after it
          petal slim <psd>          A copy without Photoshop's hidden document history
          petal preview <eps|indd|xd>  The preview picture stored in the file, as a PNG
          petal collect <project> [--no-fonts] [--out <folder>]
                                    A Premiere, After Effects, InDesign or IDML project with every file it uses
          petal colours <picture> [--count <3-12>] [--as ase|aco|css|json|gpl|png] [--out <folder>]
                                    The main colours of a picture, as swatches
          petal palette <ase|aco> [--as css|json|gpl|png|ase|aco] [--out <folder>]
                                    The colours of a swatch file, listed or saved for code and other apps
          petal formats [<file>]    What each format (or this file) converts to
          petal ask "<request>" <files> [--run] [--out <folder>]
                                    Says what DropWheel would do for a request in plain words
                                    (English or Arabic); --run does it
          petal search <words>      Files found by Search by Meaning (turn it on in DropWheel's Settings)
          petal recipes             The recipes made in DropWheel's Settings
          petal run <recipe> <files> [--out <folder>]   Runs a recipe on the files
          petal version

        The running DropWheel (turn on "Let other programs run petals" in Settings):
          petal actions             The petals that "petal do" can run, and the wheels
          petal do <petal>          Runs a petal of the wheels without files, e.g. screen.full
          petal open-wheel <name>   Opens a wheel under the pointer, e.g. Work
          petal commands [<text>]   Opens the command palette, with the text typed in it
          petal with <petal> <files>  A petal of the file wheels on files: a format (webp),
                                    tool:Compress, action:Zip, or a favourite's or recipe's name
          petal status              Whether DropWheel is running, and its version

        Notes:
          Wildcards work everywhere: petal convert *.heic --to jpg
          --max keeps pictures and videos within that many pixels on the longest side.
          --quality is 1-100 for pictures, the bitrate in kbps for audio, and the CRF for
          video (17 best ... 28 small). Without it, the quality set in DropWheel is used.
          Results are saved beside the originals unless --out names a folder; nothing is
          ever overwritten. Each saved path is printed on its own line.

        """;

    // ---------- the running Petal ----------

    private static string One(List<string> rest, string usage) =>
        rest.Count == 1 ? rest[0] : throw new UsageException("Usage: " + usage);

    /// <summary>Asks the running Petal; a failure is printed and is an error.</summary>
    private async Task<int> AskPetalAsync(System.Text.Json.Nodes.JsonObject request, CancellationToken ct)
    {
        var answer = await Petal.Core.Util.PetalApi.SendAsync(request, null, ct);
        if (answer is null)
        {
            error.WriteLine("DropWheel isn't running, or \"Let other programs run petals\" is off in its Settings.");
            return Failed;
        }
        if (answer["ok"]?.GetValue<bool>() != true)
        {
            error.WriteLine(answer["error"]?.GetValue<string>() ?? "DropWheel said no.");
            return Failed;
        }
        return Ok;
    }

    private async Task<int> WithAsync(List<string> rest, CancellationToken ct)
    {
        if (rest.Count < 2)
            throw new UsageException("Usage: petal with <petal> <files>");
        var files = Arguments.Parse(rest.Skip(1).ToList()).Files();
        var list = new System.Text.Json.Nodes.JsonArray(files.Select(f => (System.Text.Json.Nodes.JsonNode)Path.GetFullPath(f)).ToArray());
        return await AskPetalAsync(new() { ["cmd"] = "files", ["petal"] = rest[0], ["files"] = list }, ct);
    }

    private async Task<int> ActionsAsync(CancellationToken ct)
    {
        var answer = await Petal.Core.Util.PetalApi.SendAsync(new() { ["cmd"] = "actions" }, null, ct);
        if (answer is null)
            return await AskPetalAsync(new() { ["cmd"] = "status" }, ct);
        if (answer["actions"] is not System.Text.Json.Nodes.JsonArray actions)
            return await AskPetalAsync(new() { ["cmd"] = "actions" }, ct);
        foreach (var group in actions.GroupBy(a => a?["section"]?.GetValue<string>() ?? ""))
        {
            output.WriteLine(group.Key);
            foreach (var a in group)
                output.WriteLine($"  {a?["id"]?.GetValue<string>(),-22} {a?["name"]?.GetValue<string>()}");
        }
        if (answer["wheels"] is System.Text.Json.Nodes.JsonArray wheels)
        {
            output.WriteLine("Wheels");
            foreach (var w in wheels)
                output.WriteLine($"  {w?["name"]?.GetValue<string>()}{(w?["on"]?.GetValue<bool>() == false ? " (off)" : "")}");
        }
        return Ok;
    }

    private async Task<int> StatusAsync(CancellationToken ct)
    {
        var answer = await Petal.Core.Util.PetalApi.SendAsync(new() { ["cmd"] = "status" }, TimeSpan.FromSeconds(3), ct);
        if (answer is null)
        {
            output.WriteLine("DropWheel isn't running (or doesn't let other programs in).");
            return Failed;
        }
        output.WriteLine($"DropWheel {answer["version"]?.GetValue<string>()} is running{(answer["paused"]?.GetValue<bool>() == true ? ", paused" : "")}.");
        return Ok;
    }

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
                failures += Fail(file, "DropWheel doesn't know this kind of file.");
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
            throw new UsageException($"\"{name}\" isn't a format DropWheel can write. See \"petal formats\".");
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
            var path = OutputNaming.ForTool(file, tool, outputDirectory: outDir, extensionOverride: to?.WriteExtension ?? PictureExtension(file));
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
        a.Only("strong", "out", "for");
        bool strong = a.Has("strong");
        ShareTarget? app = a.Get("for") is { } forName
            ? ShareTargets.Find(forName) ?? throw new UsageException($"Unknown app \"{forName}\". Choose one of: {string.Join(", ", ShareTargets.All.Select(t => t.Id))}.")
            : null;
        var tool = ToolRegistry.Get(ToolId.Compress);
        var outDir = a.OutputFolder();
        var files = a.Files();
        long allBytes = files.Sum(f => new FileInfo(f).Length);
        int failures = 0;
        foreach (var file in files)
        {
            var format = F.FromPath(file);
            long? targetBytes = app?.TargetFor(new FileInfo(file).Length, allBytes);
            Func<IProgress<double>, Task<(string Path, long Before, long After)>>? work = format?.Family switch
            {
                FormatFamily.Image or FormatFamily.Vector => async progress =>
                {
                    var path = OutputNaming.ForTool(file, tool, outputDirectory: outDir, extensionOverride: PictureExtension(file));
                    var r = await ImageCompressor.CompressAsync(file, path, new ImageCompressOptions
                    {
                        Preset = strong ? CompressionPreset.Strong : CompressionPreset.Balanced,
                        TargetBytes = targetBytes,
                        Resize = app is not null ? ImageResize.LongEdge(app.ImageLongEdge) : ImageResize.Original,
                    }, progress, ct);
                    return (r.OutputPath, r.OriginalBytes, r.OutputBytes);
                },
                FormatFamily.Audio or FormatFamily.Video => async progress =>
                {
                    var ext = MediaCompressor.CompressedExtension(file);
                    var path = OutputNaming.ForTool(file, tool, outputDirectory: outDir, extensionOverride: ext == OutputNaming.ExtensionOf(file) ? null : ext);
                    var r = await MediaCompressor.CompressAsync(file, path, new CompressOptions(strong ? CompressPreset.Strong : CompressPreset.Balanced, targetBytes,
                        MaxResolution: app is not null && format.Family == FormatFamily.Video ? app.VideoShortSide : null) { Settings = UserDefaults() }, progress, ct);
                    return (r.OutputPath, r.OriginalBytes, r.OutputBytes);
                },
                FormatFamily.Pdf => async progress =>
                {
                    var path = OutputNaming.ForTool(file, tool, outputDirectory: outDir);
                    var r = await PdfCompressor.CompressAsync(file, path, new PdfCompressOptions { Preset = strong ? PdfCompressionPreset.Strong : PdfCompressionPreset.Balanced, TargetBytes = targetBytes }, progress, ct);
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

    private async Task<int> AskAsync(Arguments a, CancellationToken ct)
    {
        a.Only("run", "out");
        if (a.Values.Count < 2)
            throw new UsageException("Say what to do, then name the files: petal ask \"make them webp\" *.jpg");
        var request = a.Values[0];
        a.Values.RemoveAt(0);
        var files = a.Files();
        var formats = files.Select(F.FromPath).OfType<FileFormat>().ToList();
        var answer = await Petal.Recipes.AskPetal.AskAsync(request, formats, ct);
        if (answer.Recipe is not { } recipe)
        {
            error.WriteLine(Petal.Recipes.AskPetal.ModelReady
                ? "DropWheel didn't find something it can do in that request."
                : "DropWheel didn't understand that. Try simpler words, or download the AI model in DropWheel's Ask DropWheel window.");
            return Failed;
        }
        int n = 1;
        foreach (var step in recipe.Steps)
            output.WriteLine($"{n++}. {Describe(step)}");
        if (!recipe.AppliesTo(formats, Converters))
        {
            error.WriteLine("These steps don't fit the files named.");
            return Failed;
        }
        if (!a.Has("run"))
            return Ok;
        var runner = new Petal.Recipes.RecipeRunner(Converters, UserDefaults());
        var saved = await runner.RunAsync(recipe, files, a.OutputFolder(), new Progress<double>(f => ShowProgress(recipe.Name, f)), ct);
        ClearProgress();
        foreach (var path in saved)
            output.WriteLine(path);
        return Ok;
    }

    private static string Describe(Petal.Recipes.RecipeStep step) => step.Kind switch
    {
        Petal.Recipes.RecipeStepKind.Convert => $"Convert to {F.FromId(step.Format ?? "")?.Label ?? step.Format}",
        Petal.Recipes.RecipeStepKind.Resize => $"Resize to {step.LongestSide} px",
        Petal.Recipes.RecipeStepKind.Rotate => $"Rotate {step.Turns}°",
        Petal.Recipes.RecipeStepKind.Compress => step.App is { } app ? $"Compress for {app}" : step.Strong ? "Compress (strong)" : "Compress",
        Petal.Recipes.RecipeStepKind.RemoveBackground => step.Background is { } colour ? $"Remove the background, on {colour}" : "Remove the background",
        Petal.Recipes.RecipeStepKind.Watermark => $"Watermark \"{step.Text}\"",
        Petal.Recipes.RecipeStepKind.Clean => "Clean for sharing",
        Petal.Recipes.RecipeStepKind.Rename => $"Rename to \"{step.Pattern}\"",
        Petal.Recipes.RecipeStepKind.Zip => "Put them in a ZIP",
        Petal.Recipes.RecipeStepKind.CombinePdf => "Combine into one PDF",
        Petal.Recipes.RecipeStepKind.Searchable => "Make searchable (OCR)",
        _ => step.Kind.ToString(),
    };

    private int Search(Arguments a)
    {
        a.Only();
        if (a.Values.Count == 0)
            throw new UsageException("Say what to look for: petal search \"beach sunset\"");
        if (!File.Exists(Petal.Recipes.Search.SearchIndex.DefaultPath))
        {
            error.WriteLine("There's no search index yet. Turn on Search by Meaning in DropWheel's Settings, and let it read your files.");
            return Failed;
        }
        var query = string.Join(' ', a.Values);
        var meaning = Petal.Engines.Imaging.Meaning.IsReady ? Petal.Engines.Imaging.Meaning.EmbedText(query) : null;
        using var index = new Petal.Recipes.Search.SearchIndex();
        var results = index.Search(query, meaning);
        foreach (var hit in results.ByMeaning.Concat(results.ByWords).DistinctBy(h => h.Path, StringComparer.OrdinalIgnoreCase))
            output.WriteLine(hit.Path);
        if (results.ByMeaning.Count + results.ByWords.Count == 0)
        {
            error.WriteLine("Nothing found.");
            return Failed;
        }
        return Ok;
    }

    private string? MissingModel(IReadOnlyList<Petal.Core.Models.ModelInfo> models, string tool)
    {
        if (models.All(Petal.Core.Models.ModelStore.IsAvailable))
            return null;
        error.WriteLine($"{tool} needs its AI model. Open {tool} in DropWheel once to download it.");
        return tool;
    }

    private async Task<int> DenoiseAsync(Arguments a, CancellationToken ct)
    {
        a.Only("strength", "out");
        var files = a.Files();
        if (MissingModel(VoiceCleaner.Models, "Remove Noise") is not null)
            return Failed;
        double db = a.Get("strength")?.ToLowerInvariant() switch
        {
            null or "full" => 100,
            "strong" => 24,
            "gentle" => 12,
            var other => throw new UsageException($"--strength is gentle, strong or full, not \"{other}\"."),
        };
        var outDir = a.OutputFolder();
        var tool = ToolRegistry.Get(ToolId.RemoveNoise);
        int failed = 0;
        foreach (var file in files)
        {
            var path = OutputNaming.ForTool(file, tool, outputDirectory: outDir);
            try
            {
                await VoiceCleaner.CleanAsync(file, path, new CleanVoiceOptions(db) { Settings = UserDefaults() }, new Progress<double>(f => ShowProgress(Path.GetFileName(file), f)), ct);
                ClearProgress();
                output.WriteLine(path);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                ClearProgress();
                error.WriteLine($"{Path.GetFileName(file)}: {ex.Message}");
                failed++;
            }
        }
        return failed == 0 ? Ok : Failed;
    }

    private async Task<int> SeparateAsync(Arguments a, CancellationToken ct)
    {
        a.Only("four", "out");
        var files = a.Files();
        if (MissingModel(StemSplitter.Models, "Separate Vocals") is not null)
            return Failed;
        var options = new SplitStemsOptions(a.Has("four") ? StemSet.FourStems : StemSet.VocalsAndMusic) { Settings = UserDefaults() };
        var outDir = a.OutputFolder();
        var tool = ToolRegistry.Get(ToolId.SeparateVocals);
        int failed = 0;
        foreach (var file in files)
        {
            var folder = OutputNaming.ForTool(file, tool, outputDirectory: outDir);
            var extension = F.FromPath(file)?.Family == FormatFamily.Audio ? OutputNaming.ExtensionOf(file) : "m4a";
            try
            {
                var stems = await StemSplitter.SplitAsync(file, folder, extension, options, new Progress<double>(f => ShowProgress(Path.GetFileName(file), f)), ct);
                ClearProgress();
                foreach (var stem in stems)
                    output.WriteLine(stem);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                ClearProgress();
                error.WriteLine($"{Path.GetFileName(file)}: {ex.Message}");
                failed++;
            }
        }
        return failed == 0 ? Ok : Failed;
    }

    private async Task<int> TranslateAsync(Arguments a, CancellationToken ct)
    {
        a.Only("out", "to", "from");
        var files = a.Files();
        if (files.Any(f => F.FromPath(f)?.Family != FormatFamily.Subtitle))
            throw new UsageException("Name subtitle files (SRT or VTT).");
        string into = a.Get("to") ?? "ar";
        if (Petal.Recipes.Translation.Translator.Languages.All(l => l.Code != into))
            throw new UsageException("--to takes a language code: " + string.Join(", ", Petal.Recipes.Translation.Translator.Languages.Select(l => l.Code)) + ".");
        var outDir = a.OutputFolder();
        foreach (var file in files)
        {
            var text = await File.ReadAllTextAsync(file, ct);
            var vtt = OutputNaming.ExtensionOf(file).Equals("vtt", StringComparison.OrdinalIgnoreCase);
            var from = a.Get("from") ?? Petal.Core.Text.LanguageGuess.Of(text.Length > 4000 ? text[..4000] : text) ?? "en";
            if (MissingModel(Petal.Recipes.Translation.Translator.ModelsFor(from, into), "Translate Subtitles") is not null)
                return Failed;
            var translated = await Petal.Recipes.Translation.Translator.TranslateSubtitlesAsync(text, vtt, from, into, new Progress<double>(f => ShowProgress(Path.GetFileName(file), f)), ct);
            ClearProgress();
            var path = OutputNaming.Unique(outDir ?? Path.GetDirectoryName(file)!, Path.GetFileNameWithoutExtension(file) + "." + into, OutputNaming.ExtensionOf(file));
            await File.WriteAllTextAsync(path, translated, new System.Text.UTF8Encoding(true), ct);
            output.WriteLine(path);
        }
        return Ok;
    }

    private int Voices()
    {
        foreach (var voice in Speech.AllVoices())
        {
            var state = voice.Kind == VoiceKind.Natural
                ? voice.IsReady ? "natural" : "natural, download it in DropWheel's Read Aloud first"
                : "Windows";
            output.WriteLine($"{voice.Name} ({voice.Language}, {state})");
        }
        return Ok;
    }

    private async Task<int> SpeakAsync(Arguments a, CancellationToken ct)
    {
        a.Only("voice", "speed", "out");
        var file = Single(a);
        var format = F.FromPath(file);
        if (format is null || !Petal.Recipes.DocumentText.CanRead(format, Converters))
            throw new UsageException("Name a document: PDF, Word, text, Markdown, web page or e-book.");
        double speed = 1;
        if (a.Get("speed") is { } text && (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out speed) || speed is < 0.5 or > 2))
            throw new UsageException("--speed must be a number from 0.5 to 2, for example 1.25.");
        var content = await Petal.Recipes.DocumentText.ReadAsync(file, Converters, UserDefaults(), null, ct);
        if (string.IsNullOrWhiteSpace(content))
        {
            error.WriteLine($"{Path.GetFileName(file)}: there's no text to read.");
            return Failed;
        }
        var voices = Speech.AllVoices();
        SpeechVoice? voice;
        if (a.Get("voice") is { } name)
        {
            voice = voices.FirstOrDefault(v => v.Name.Equals(name, StringComparison.OrdinalIgnoreCase) || v.Name.Replace("Microsoft ", "").Equals(name, StringComparison.OrdinalIgnoreCase))
                ?? throw new UsageException($"No voice called \"{name}\". See \"petal voices\".");
        }
        else
        {
            var language = Speech.GuessLanguage(content);
            voice = Speech.Choose(language, voices.Where(v => v.IsReady).ToList());
            if (voice is null)
            {
                error.WriteLine($"No voice for this document's language ({language}) is ready. Add one in Windows Settings › Time & language › Speech, or download a natural voice in DropWheel's Read Aloud.");
                return Failed;
            }
        }
        if (!voice.IsReady)
        {
            error.WriteLine($"{voice.Name} isn't downloaded yet. Open Read Aloud in DropWheel once to get it.");
            return Failed;
        }
        var path = OutputNaming.ForTool(file, ToolRegistry.Get(ToolId.ReadAloud), outputDirectory: a.OutputFolder());
        await Speech.SpeakToFileAsync(content, voice, speed, path, Path.GetFileNameWithoutExtension(file), new Progress<double>(f => ShowProgress(voice.Name, f)), ct);
        ClearProgress();
        output.WriteLine(path);
        return Ok;
    }

    private async Task<int> SearchableAsync(Arguments a, CancellationToken ct)
    {
        a.Only("out");
        var files = a.Files();
        var pdfs = files.Where(f => F.FromPath(f) == F.Pdf).ToList();
        var pictures = files.Where(f => F.FromPath(f) is { Family: FormatFamily.Image, CanRead: true }).ToList();
        if (pdfs.Count + pictures.Count != files.Count)
            throw new UsageException("Name PDFs, or pictures of pages.");
        if (!SearchablePdf.IsAvailable)
        {
            error.WriteLine("Windows text recognition isn't available. Add a language with OCR in Windows Settings › Time & language.");
            return Failed;
        }
        var outDir = a.OutputFolder();
        var tool = ToolRegistry.Get(ToolId.MakeSearchable);
        int failed = 0;

        async Task MakeAsync(string input, string label, string path)
        {
            try
            {
                var result = await SearchablePdf.MakeAsync(input, path, null, new Progress<double>(f => ShowProgress(label, f)), ct);
                ClearProgress();
                if (!result.Written)
                {
                    error.WriteLine($"{label}: every page already has text.");
                    return;
                }
                output.WriteLine(path);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                ClearProgress();
                error.WriteLine($"{label}: {ex.Message}");
                failed++;
            }
        }

        foreach (var pdf in pdfs)
            await MakeAsync(pdf, Path.GetFileName(pdf), OutputNaming.ForTool(pdf, tool, outputDirectory: outDir));
        if (pictures.Count > 0)
        {
            var pages = Path.Combine(Path.GetTempPath(), "Petal", $"pages-{Guid.NewGuid():N}.pdf");
            Directory.CreateDirectory(Path.GetDirectoryName(pages)!);
            try
            {
                await PdfCreator.CreateAsync(pictures, pages, new PdfCreateOptions(), null, ct);
                await MakeAsync(pages, Path.GetFileName(pictures[0]), OutputNaming.ForTool(pictures[0], tool, outputDirectory: outDir));
            }
            finally
            {
                try { File.Delete(pages); } catch (IOException) { }
            }
        }
        return failed == 0 ? Ok : Failed;
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
                // Illustrator and EPS drawings have no pixels of their own: their details follow below.
                case FormatFamily.Image or FormatFamily.Vector when format != F.Ai && format != F.Eps:
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
        try
        {
            AdobeInfo(file, format);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            output.WriteLine($"  (Couldn't read the design details: {ex.Message})");
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
                throw new UsageException($"DropWheel doesn't know \"{a.Values[0]}\".");
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

    /// <summary>Pictures Petal can't write (Photoshop, RAW, Illustrator, EPS, SVG) are saved as PNG, or JPG for camera photos.</summary>
    private static string? PictureExtension(string file) => F.FromPath(file) is { CanWrite: false } format ? (format == F.Raw ? "jpg" : "png") : null;

    // ---------- Adobe files ----------

    private void AdobeInfo(string file, FileFormat? format)
    {
        if (format == F.Psd || format == F.Psb)
        {
            var psd = PsdFile.Read(file);
            output.WriteLine($"  Document  {psd.Width} × {psd.Height} px, {psd.ColorMode} {psd.Depth}-bit, {Math.Round(psd.DpiX)} ppi{(psd.HasComposite ? "" : ", no flat copy saved")}");
            var kinds = psd.Layers.Where(l => l.Kind is not (PsdLayerKind.Empty or PsdLayerKind.Group)).GroupBy(l => l.Kind).Select(g => $"{g.Count()} {g.Key switch { PsdLayerKind.Text => "text", PsdLayerKind.SmartObject => "smart object", _ => "pixel" }}");
            output.WriteLine($"  Layers    {string.Join(", ", kinds)}; {psd.Layers.Count(l => l.Kind == PsdLayerKind.Group)} groups, {psd.Layers.Count(l => l.FrameIndex >= 0 && l.Hidden)} hidden");
            foreach (var layer in Enumerable.Reverse(psd.Layers).Where(l => l.Kind != PsdLayerKind.Empty))
                output.WriteLine($"            {string.Concat(layer.Group.Select(_ => "  "))}{(layer.Kind == PsdLayerKind.Group ? "▸ " : "")}{layer.Name}{(layer.Hidden ? " (hidden)" : "")}");
            if (psd.Fonts.Count > 0)
                output.WriteLine($"  Fonts     {string.Join(", ", psd.Fonts.Select(f => FontIndex.Find(new DesignFont(f, null, null, f, null)) is null ? f + " (missing)" : f))}");
            if (PhotoshopTools.HistoryEntries(psd) is int history and > 0)
                output.WriteLine($"  History   {history} entries (petal slim removes them)");
            return;
        }
        if (!DesignDocuments.CanRead(file))
            return;
        var d = DesignDocuments.Read(file);
        output.WriteLine($"  Kind      {d.Kind}{(d.Application is { } app ? $" ({app})" : "")}");
        foreach (var (label, value) in d.Facts)
            output.WriteLine($"  {label,-9} {value}");
        if (d.Fonts.Count > 0)
            output.WriteLine($"  Fonts     {string.Join(", ", d.Fonts.Select(f => FontIndex.Find(f) is null ? f.Name + " (missing)" : f.Name))}");
        var folder = Path.GetDirectoryName(Path.GetFullPath(file))!;
        foreach (var link in d.Links)
            output.WriteLine($"  Link      {link}{(ProjectCollector.Resolve(link, folder) is null ? " (missing)" : "")}");
        if (d.Swatches.Count > 0)
            output.WriteLine($"  Swatches  {d.Swatches.Count}");
    }

    private async Task<int> LayersAsync(Arguments a, CancellationToken ct)
    {
        a.Only("full", "hidden", "out");
        var options = new LayerExportOptions(a.Has("full"), a.Has("hidden"));
        var tool = ToolRegistry.Get(ToolId.ExportLayers);
        var outDir = a.OutputFolder();
        int failures = 0;
        foreach (var file in a.Files())
        {
            if (F.FromPath(file) is not { } f || (f != F.Psd && f != F.Psb))
            {
                failures += Fail(file, "Name Photoshop files (PSD or PSB).");
                continue;
            }
            var folder = OutputNaming.ForTool(file, tool, outputDirectory: outDir);
            failures += await StepAsync(file, async progress => await PhotoshopTools.ExportLayersAsync(file, folder, options, progress, ct), ct);
        }
        return failures == 0 ? Ok : Failed;
    }

    private async Task<int> SlimAsync(Arguments a, CancellationToken ct)
    {
        a.Only("out");
        var tool = ToolRegistry.Get(ToolId.SlimPhotoshop);
        var outDir = a.OutputFolder();
        int failures = 0;
        foreach (var file in a.Files())
        {
            var path = OutputNaming.ForTool(file, tool, outputDirectory: outDir);
            failures += await StepAsync(file, async _ =>
            {
                var (before, after, entries) = await PhotoshopTools.RemoveHistoryAsync(file, path, ct);
                error.WriteLine($"{Path.GetFileName(file)}: {entries} history entries removed, {Size(before)} → {Size(after)}");
                return [path];
            }, ct);
        }
        return failures == 0 ? Ok : Failed;
    }

    private async Task<int> PreviewAsync(Arguments a, CancellationToken ct)
    {
        a.Only("out");
        var tool = ToolRegistry.Get(ToolId.SavePreview);
        var outDir = a.OutputFolder();
        int failures = 0;
        foreach (var file in a.Files())
        {
            var path = OutputNaming.ForTool(file, tool, outputDirectory: outDir);
            failures += await StepAsync(file, _ => Task.Run<IReadOnlyList<string>>(() =>
            {
                MagickImage? image = F.FromPath(file) == F.Eps ? EpsFile.LoadPreview(file) : null;
                if (image is null && DesignDocuments.CanRead(file) && DesignDocuments.Preview(file) is { } bytes)
                    image = new MagickImage(bytes);
                if (image is null)
                    throw new InvalidOperationException("This file has no preview picture.");
                using (image)
                    image.Write(path, MagickFormat.Png);
                return [path];
            }, ct), ct);
        }
        return failures == 0 ? Ok : Failed;
    }

    private async Task<int> CollectAsync(Arguments a, CancellationToken ct)
    {
        a.Only("no-fonts", "out");
        var file = Single(a);
        if (!ProjectCollector.CanCollect(file))
            throw new UsageException("Name a Premiere (.prproj), After Effects (.aep), InDesign (.indd), IDML or Illustrator file.");
        var plan = ProjectCollector.Plan(file);
        foreach (var item in plan.Files.Where(i => i.Found is null))
            error.WriteLine($"Missing: {item.Recorded}");
        foreach (var font in plan.Fonts.Where(f => f.Installed is null))
            error.WriteLine($"Font not on this PC: {font.Font.Name}");
        var folder = OutputNaming.ForTool(file, ToolRegistry.Get(ToolId.CollectFiles), outputDirectory: a.OutputFolder());
        var result = await ProjectCollector.CollectAsync(plan, folder, new CollectOptions(!a.Has("no-fonts")), new Progress<double>(f => ShowProgress(Path.GetFileName(file), f)), ct);
        ClearProgress();
        output.WriteLine(result.Folder);
        error.WriteLine($"{result.Copied} files collected, {result.Missing} missing, {result.Fonts} fonts{(result.Relinked ? "; the copied project points at them" : "")}.");
        return result.Missing == 0 ? Ok : Failed;
    }

    private int ColoursCommand(Arguments a)
    {
        a.Only("count", "as", "out");
        var file = Single(a);
        int count = a.Int("count", 3, 12) ?? 6;
        var swatches = Swatches.Extract(file, count);
        return SwatchOutput(file, swatches, a.Get("as"), a.OutputFolder(), "Colours");
    }

    private int PaletteCommand(Arguments a)
    {
        a.Only("as", "out");
        var file = Single(a);
        return SwatchOutput(file, Swatches.Read(file), a.Get("as"), a.OutputFolder(), null);
    }

    /// <summary>Lists the colours, or saves them in the format asked for.</summary>
    private int SwatchOutput(string file, IReadOnlyList<Swatch> swatches, string? format, string? outDir, string? suffix)
    {
        if (format is null)
        {
            foreach (var s in swatches)
                output.WriteLine($"{s.Hex}  {s.Name}{(s.Group is { } g ? $"  ({g})" : "")}{(s.Spot ? "  spot" : "")}");
            return Ok;
        }
        format = format.ToLowerInvariant();
        if (format is not ("ase" or "aco" or "css" or "json" or "gpl" or "png"))
            throw new UsageException("--as is ase, aco, css, json, gpl or png.");
        var stem = Path.GetFileNameWithoutExtension(file) + (suffix is null ? "" : " " + suffix);
        var path = OutputNaming.Unique(outDir ?? Path.GetDirectoryName(Path.GetFullPath(file))!, stem, format);
        switch (format)
        {
            case "ase": Swatches.WriteAse(swatches, path); break;
            case "aco": Swatches.WriteAco(swatches, path); break;
            case "css": File.WriteAllText(path, Swatches.ToCss(swatches)); break;
            case "json": File.WriteAllText(path, Swatches.ToJson(swatches)); break;
            case "gpl": File.WriteAllText(path, Swatches.ToGpl(swatches, stem)); break;
            default: Swatches.WriteSheet(swatches, path); break;
        }
        output.WriteLine(path);
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

        public List<Petal.Recipes.Recipe>? Recipes { get; set; }
    }

    /// <summary>The recipes saved in Petal's Settings.</summary>
    internal static IReadOnlyList<Petal.Recipes.Recipe> UserRecipes(string? settingsFile = null)
    {
        settingsFile ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Petal", "settings.json");
        try
        {
            if (File.Exists(settingsFile))
            {
                var options = new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } };
                return JsonSerializer.Deserialize<SavedSettings>(File.ReadAllText(settingsFile), options)?.Recipes ?? [];
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
        }
        return [];
    }

    private int ListRecipes()
    {
        var recipes = UserRecipes();
        if (recipes.Count == 0)
        {
            output.WriteLine("No recipes yet. Make one in DropWheel's Settings › Recipes.");
            return Ok;
        }
        foreach (var recipe in recipes)
            output.WriteLine($"{recipe.Name}: {string.Join(" → ", recipe.Steps.Select(s => s.Kind))}");
        return Ok;
    }

    /// <summary>Recipes are looked up by name (or a unique start of it), ignoring case.</summary>
    private async Task<int> RunRecipeAsync(Arguments a, CancellationToken ct, IReadOnlyList<Petal.Recipes.Recipe>? recipes = null)
    {
        a.Only("out");
        if (a.Values.Count < 2)
            throw new UsageException("Name a recipe and at least one file: petal run \"Shop\" *.jpg");
        recipes ??= UserRecipes();
        var name = a.Values[0];
        var matches = recipes.Where(r => r.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 0)
            matches = recipes.Where(r => r.Name.StartsWith(name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count != 1)
            throw new UsageException(matches.Count == 0 ? $"No recipe called \"{name}\". See \"petal recipes\"." : $"\"{name}\" matches several recipes; type more of the name.");
        a.Values.RemoveAt(0);
        var files = a.Files();
        var runner = new Petal.Recipes.RecipeRunner(Converters, UserDefaults());
        var progress = new Progress<double>(f => ShowProgress(matches[0].Name, f));
        try
        {
            var saved = await runner.RunAsync(matches[0], files, a.OutputFolder(), progress, ct);
            ClearProgress();
            foreach (var path in saved)
                output.WriteLine(path);
            return Ok;
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not UsageException)
        {
            ClearProgress();
            error.WriteLine($"{matches[0].Name}: {ex.Message}");
            return Failed;
        }
    }

    /// <summary>For tests: runs a recipe given directly rather than from Settings.</summary>
    internal Task<int> RunRecipeAsync(Petal.Recipes.Recipe recipe, IReadOnlyList<string> args, CancellationToken ct) =>
        RunRecipeAsync(Arguments.Parse([recipe.Name, .. args]), ct, [recipe]);
}
