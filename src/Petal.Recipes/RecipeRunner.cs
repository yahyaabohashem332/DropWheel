using System.IO;
using System.Globalization;
using ImageMagick;
using Petal.Core.Actions;
using Petal.Core.Formats;
using Petal.Core.Jobs;
using Petal.Engines.Documents.Pdf;
using Petal.Engines.Files.Archives;
using Petal.Engines.Imaging;
using Petal.Engines.Media;

namespace Petal.Recipes;

/// <summary>
/// Runs a <see cref="Recipe"/>: files go through each step in a private scratch folder, and only
/// the results are saved: beside each original as "photo Shop.webp", or as one ZIP or PDF named
/// after the recipe. Originals are never changed, and a failed run leaves nothing behind.
/// </summary>
public sealed class RecipeRunner(ConverterRegistry converters, ConversionSettings defaults)
{
    private sealed record Item(string Original, string Current);

    /// <param name="outputDirectory">Where results go; null saves them beside the originals.</param>
    /// <returns>The files saved.</returns>
    public async Task<IReadOnlyList<string>> RunAsync(Recipe recipe, IReadOnlyList<string> inputs, string? outputDirectory = null,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        if (recipe.Steps.Count == 0)
            throw new InvalidOperationException("This recipe has no steps yet.");
        if (inputs.Count == 0)
            return [];
        var scratch = Path.Combine(Path.GetTempPath(), "Petal", "recipes", Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(scratch);
        try
        {
            var items = inputs.Select(p => new Item(p, p)).ToList();
            bool renamed = false;
            for (int s = 0; s < recipe.Steps.Count; s++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var step = recipe.Steps[s];
                var dir = Path.Combine(scratch, (s + 1).ToString(CultureInfo.InvariantCulture));
                Directory.CreateDirectory(dir);
                double stepStart = (double)s / recipe.Steps.Count, stepSize = 1.0 / recipe.Steps.Count;
                if (step.IsCollective)
                {
                    items = await CollectiveAsync(step, items, dir, recipe, cancellationToken);
                    renamed |= step.Kind == RecipeStepKind.Rename;
                }
                else
                {
                    long allBytes = items.Sum(i => new FileInfo(i.Current).Length);
                    var next = new List<Item>();
                    for (int i = 0; i < items.Count; i++)
                    {
                        var slice = Slice(progress, stepStart + stepSize * i / items.Count, stepSize / items.Count);
                        foreach (var produced in await PerFileAsync(step, items[i].Current, dir, allBytes, slice, cancellationToken))
                            next.Add(items[i] with { Current = produced });
                    }
                    items = next;
                }
                progress?.Report(stepStart + stepSize);
            }
            return Deliver(items, recipe, outputDirectory, renamed);
        }
        finally
        {
            try { Directory.Delete(scratch, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private async Task<IReadOnlyList<string>> PerFileAsync(RecipeStep step, string input, string dir, long allBytes, IProgress<double>? progress, CancellationToken ct)
    {
        var format = Formats.FromPath(input) ?? throw new InvalidOperationException($"{Path.GetFileName(input)}: DropWheel doesn't know this kind of file.");
        var stem = Formats.BaseName(input);
        string Output(string extension) => OutputNaming.Unique(dir, stem, extension);
        switch (step.Kind)
        {
            case RecipeStepKind.Convert:
            {
                var favourite = new Favourite { Format = step.Format ?? "", LongestSide = step.LongestSide, Quality = step.Quality };
                var target = favourite.Target ?? throw new InvalidOperationException($"\"{step.Format}\" isn't a format DropWheel can write.");
                var converter = favourite.ConverterFor(format, converters)
                    ?? throw new InvalidOperationException($"{format.Label} can't be converted to {target.Label}.");
                var result = await converter.ConvertAsync(new ConversionJob(input, format, target, Output(target.WriteExtension), favourite.Apply(defaults)), progress, ct);
                // Multi-page results (a PDF to JPG) arrive as a folder of files.
                return result.OutputPaths.SelectMany(p => Directory.Exists(p) ? Directory.GetFiles(p).Order(StringComparer.OrdinalIgnoreCase) : (IEnumerable<string>)[p]).ToList();
            }
            case RecipeStepKind.Resize:
            {
                if (step.LongestSide <= 0)
                    return [input];
                var output = Output(format.WriteExtension);
                await ImageTransformer.TransformAsync(input, output, new ImageTransform { Mode = ResizeMode.Fit, Width = step.LongestSide, Height = step.LongestSide }, progress, ct);
                return [output];
            }
            case RecipeStepKind.Rotate:
            {
                var output = Output(format.WriteExtension);
                await ImageTransformer.TransformAsync(input, output, new ImageTransform { Rotation = step.Turns }, progress, ct);
                return [output];
            }
            case RecipeStepKind.Compress:
                return [await CompressAsync(step, input, format, Output, allBytes, progress, ct)];
            case RecipeStepKind.RemoveBackground:
            {
                if (!BackgroundRemover.IsReady)
                    throw new InvalidOperationException("The background removal model isn't downloaded yet. Open Remove Background once to get it.");
                var fill = step.Background is { Length: > 0 } hex ? new MagickColor(hex) : null;
                var output = Output(fill is null && format != Formats.Webp ? "png" : format.WriteExtension);
                await BackgroundRemover.RemoveAsync(input, output, new RemoveBackgroundOptions { Fill = fill }, progress, ct);
                return [output];
            }
            case RecipeStepKind.Watermark:
            {
                if (string.IsNullOrWhiteSpace(step.Text))
                    return [input];
                var spec = new StampSpec
                {
                    Text = step.Text,
                    Place = step.Place,
                    Size = step.Size,
                    Opacity = step.Opacity,
                    Color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(step.Color),
                    Tiled = step.Tiled,
                };
                var output = Output(format.WriteExtension);
                switch (format.Family)
                {
                    case FormatFamily.Video:
                        await VideoTools.WatermarkAsync(input, output, new WatermarkVideoOptions((w, h) => StampRenderer.RenderPng(w, h, spec)) { Settings = defaults }, progress, ct);
                        break;
                    case FormatFamily.Pdf:
                        await PdfStamper.StampAsync(input, output, f => StampRenderer.RenderPng((int)(f.Width * 2), (int)(f.Height * 2), spec), null, progress, ct);
                        break;
                    default:
                        await ImageOverlay.CompositeAsync(input, output, (w, h) => StampRenderer.RenderPng(w, h, spec, maxSide: 4096), progress, ct);
                        break;
                }
                return [output];
            }
            case RecipeStepKind.Searchable:
            {
                var source = input;
                if (format != Formats.Pdf)
                {
                    source = OutputNaming.Unique(dir, stem + " pages", "pdf");
                    await PdfCreator.CreateAsync([input], source, new PdfCreateOptions(), null, ct);
                }
                var output = Output("pdf");
                var result = await SearchablePdf.MakeAsync(source, output, null, progress, ct);
                // A PDF that already has its text stays as it is.
                return [result.Written ? output : source];
            }
            case RecipeStepKind.Clean:
            {
                var output = Output(OutputNaming.ExtensionOf(input));
                await SharingCleaner.CleanAsync(input, output, progress, ct);
                return [output];
            }
            default:
                throw new NotSupportedException(step.Kind.ToString());
        }
    }

    private async Task<string> CompressAsync(RecipeStep step, string input, FileFormat format, Func<string, string> output, long allBytes, IProgress<double>? progress, CancellationToken ct)
    {
        var app = step.App is { } id ? ShareTargets.Find(id) : null;
        long? target = app?.TargetFor(new FileInfo(input).Length, allBytes);
        switch (format.Family)
        {
            case FormatFamily.Image:
            {
                var path = output(format.WriteExtension);
                var result = await ImageCompressor.CompressAsync(input, path, new ImageCompressOptions
                {
                    Preset = step.Strong ? CompressionPreset.Strong : CompressionPreset.Balanced,
                    TargetBytes = target,
                    Resize = app is not null ? ImageResize.LongEdge(app.ImageLongEdge) : ImageResize.Original,
                }, progress, ct);
                return result.OutputPath;
            }
            case FormatFamily.Audio or FormatFamily.Video:
            {
                var path = output(MediaCompressor.CompressedExtension(input));
                var result = await MediaCompressor.CompressAsync(input, path, new CompressOptions(step.Strong ? CompressPreset.Strong : CompressPreset.Balanced, target,
                    MaxResolution: app is not null && format.Family == FormatFamily.Video ? app.VideoShortSide : null) { Settings = defaults }, progress, ct);
                return result.OutputPath;
            }
            case FormatFamily.Pdf:
            {
                var path = output("pdf");
                var result = await PdfCompressor.CompressAsync(input, path, new PdfCompressOptions
                {
                    Preset = step.Strong ? PdfCompressionPreset.Strong : PdfCompressionPreset.Balanced,
                    TargetBytes = target,
                }, progress, ct);
                return result.Written ? path : input;
            }
            default:
                return input;
        }
    }

    private static async Task<List<Item>> CollectiveAsync(RecipeStep step, List<Item> items, string dir, Recipe recipe, CancellationToken ct)
    {
        var paths = items.Select(i => i.Current).ToList();
        var name = Clean(string.IsNullOrWhiteSpace(step.Name) ? recipe.Name : step.Name);
        switch (step.Kind)
        {
            case RecipeStepKind.Rename:
            {
                // Named from the originals, so {name}, {folder} and {date} describe them rather than
                // the scratch copies; the extension is whatever the steps so far have made.
                var plan = BatchRename.Plan(items.Select(i => i.Original).ToList(), new RenameRule { Pattern = string.IsNullOrWhiteSpace(step.Pattern) ? "{name}" : step.Pattern });
                var renamed = new List<Item>();
                for (int i = 0; i < items.Count; i++)
                {
                    var stem = plan[i].Problem == RenameProblem.Empty ? Formats.BaseName(items[i].Original) : Path.GetFileNameWithoutExtension(plan[i].NewName);
                    // Copies, so an untouched original is never moved.
                    var target = OutputNaming.Unique(dir, stem, OutputNaming.ExtensionOf(items[i].Current));
                    File.Copy(items[i].Current, target);
                    renamed.Add(items[i] with { Current = target });
                }
                return renamed;
            }
            case RecipeStepKind.Zip:
            {
                var zip = await ZipCreator.CreateAsync(paths, Path.Combine(dir, name + ".zip"), new ZipOptions(), null, ct);
                return [new Item(items[0].Original, zip)];
            }
            case RecipeStepKind.CombinePdf:
            {
                var output = Path.Combine(dir, name + ".pdf");
                if (paths.All(p => Formats.FromPath(p) == Formats.Pdf))
                {
                    await PdfPageTools.MergeAsync(paths, output, null, null, ct);
                }
                else
                {
                    // Pictures become pages; PDFs among them keep theirs, in order.
                    var parts = new List<string>();
                    int n = 0;
                    foreach (var group in Runs(paths, p => Formats.FromPath(p) == Formats.Pdf))
                    {
                        if (group.Key)
                        {
                            parts.AddRange(group.Items);
                        }
                        else
                        {
                            var part = Path.Combine(dir, $"part {++n}.pdf");
                            await PdfCreator.CreateAsync(group.Items, part, new PdfCreateOptions(), null, ct);
                            parts.Add(part);
                        }
                    }
                    if (parts.Count == 1)
                        File.Move(parts[0], output);
                    else
                        await PdfPageTools.MergeAsync(parts, output, null, null, ct);
                }
                return [new Item(items[0].Original, output)];
            }
            default:
                throw new NotSupportedException(step.Kind.ToString());
        }
    }

    /// <summary>Consecutive runs of items sharing a key, in order.</summary>
    private static IEnumerable<(bool Key, List<string> Items)> Runs(IEnumerable<string> paths, Func<string, bool> key)
    {
        (bool Key, List<string> Items)? run = null;
        foreach (var path in paths)
        {
            bool k = key(path);
            if (run is { } r && r.Key == k)
            {
                r.Items.Add(path);
                continue;
            }
            if (run is { } done)
                yield return done;
            run = (k, [path]);
        }
        if (run is { } last)
            yield return last;
    }

    /// <summary>Moves results out of the scratch folder to their place, never over an existing file.</summary>
    private static List<string> Deliver(List<Item> items, Recipe recipe, string? outputDirectory, bool renamed)
    {
        var saved = new List<string>();
        var suffix = Clean(recipe.Name);
        foreach (var item in items)
        {
            var dir = outputDirectory ?? Path.GetDirectoryName(Path.GetFullPath(item.Original))!;
            Directory.CreateDirectory(dir);
            var ext = OutputNaming.ExtensionOf(item.Current);
            bool single = Path.GetFileName(item.Current) is var file && (file.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || items.Count == 1 && file.StartsWith(suffix + ".", StringComparison.OrdinalIgnoreCase));
            var stem = renamed || single
                ? Formats.BaseName(item.Current)
                : $"{Formats.BaseName(item.Original)} {suffix}".Trim();
            var target = OutputNaming.Unique(dir, stem, ext);
            if (string.Equals(Path.GetFullPath(item.Current), Path.GetFullPath(item.Original), StringComparison.OrdinalIgnoreCase))
                File.Copy(item.Current, target);
            else
                File.Move(item.Current, target);
            saved.Add(target);
        }
        return saved;
    }

    private static string Clean(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c => invalid.Contains(c) ? '-' : c).ToArray()).Trim().TrimEnd('.');
        return cleaned.Length == 0 ? "Recipe" : cleaned;
    }

    private static IProgress<double>? Slice(IProgress<double>? progress, double start, double size) =>
        progress is null ? null : new Progress<double>(f => progress.Report(start + size * Math.Clamp(f, 0, 1)));
}
