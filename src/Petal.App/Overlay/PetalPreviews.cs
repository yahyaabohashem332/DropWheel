using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Petal.App.Jobs;
using Petal.App.Localization;
using Petal.App.Settings;
using Petal.App.Util;
using Petal.Core.Formats;
using Petal.Core.Jobs;
using Petal.Core.Tools;
using Petal.Engines.Imaging;
using Petal.Engines.Media;

namespace Petal.App.Overlay;

/// <summary>What a petal will make, shown in the centre before the drop: a size, a picture, or both.</summary>
public sealed record PetalPreview(string Text, ImageSource? Picture = null);

/// <summary>
/// Works out a petal's result while the pointer rests on it: pictures are converted for real in the
/// background (so the size is exact), sound and video from a few seconds in the middle (so it is close),
/// and Remove Background shows its cut-out. Results are remembered for the files as they are now.
/// </summary>
public sealed class PetalPreviews(JobRunner jobs)
{
    /// <summary>Pictures larger than this, or more seconds of work than this, aren't previewed.</summary>
    private const long LargestPicture = 80L * 1024 * 1024;
    private static readonly TimeSpan Longest = TimeSpan.FromSeconds(8);
    private const double SampleSeconds = 4;

    private readonly Dictionary<string, PetalPreview?> _made = [];
    private readonly Queue<string> _order = new();
    private readonly object _gate = new();

    public static string Folder => Path.Combine(Path.GetTempPath(), "Petal Previews");

    public async Task<PetalPreview?> MakeAsync(WheelItem item, IReadOnlyList<string> files, CancellationToken cancellationToken)
    {
        if (!SettingsStore.Current.PetalPreviews || files.Count is 0 or > 50 || files.Any(f => !File.Exists(f)))
            return null;
        var settings = SettingsStore.Current.Conversion;
        var key = Key(item, files, settings);
        lock (_gate)
        {
            if (_made.TryGetValue(key, out var known))
                return known;
        }
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(Longest);
        PetalPreview? preview;
        try
        {
            preview = item.Payload switch
            {
                FileFormat target => await SizeAsync(files, target, settings, (from, to) => jobs.Converters.Find(from, to), limit.Token),
                Favourite { Target: { } target } favourite => await SizeAsync(files, target, favourite.Apply(settings), (from, _) => favourite.ConverterFor(from, jobs.Converters), limit.Token),
                ToolInfo { Id: ToolId.RemoveBackground } when files.Count == 1 && BackgroundRemover.IsReady => await CutOutAsync(files[0], limit.Token),
                _ => null,
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Too slow this time (the first video can wait for the encoders to be found): nothing is
            // shown, and the next rest on the petal tries again.
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Info($"preview: {item.Name} gave nothing ({ex.GetType().Name}: {ex.Message})");
            preview = null;
        }
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            _made[key] = preview;
            _order.Enqueue(key);
            while (_order.Count > 96)
                _made.Remove(_order.Dequeue());
        }
        return preview;
    }

    private static string Key(WheelItem item, IReadOnlyList<string> files, ConversionSettings settings) =>
        item.Key + "|" + System.Text.Json.JsonSerializer.Serialize(settings) + "|" +
        string.Join("|", files.Select(f => f + "@" + File.GetLastWriteTimeUtc(f).Ticks + "#" + new FileInfo(f).Length));

    /// <summary>
    /// The size after converting: the first file is converted (pictures whole, sound and video a few
    /// seconds of it) and the others are taken to shrink or grow the same way.
    /// </summary>
    internal static async Task<PetalPreview?> SizeAsync(IReadOnlyList<string> files, FileFormat target, ConversionSettings settings,
        Func<FileFormat, FileFormat, IConverter?> converterFor, CancellationToken ct)
    {
        var first = files[0];
        if (Formats.FromPath(first) is not { } from || from == target && settings.MaxLongEdge == 0 || converterFor(from, target) is not { } converter)
            return null;
        if (files.Any(f => Formats.FromPath(f) != from))
            return null;
        long before = files.Sum(f => new FileInfo(f).Length);
        long firstSize = new FileInfo(first).Length;
        if (before == 0 || firstSize == 0)
            return null;
        Directory.CreateDirectory(Folder);
        var work = Path.Combine(Folder, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            double firstAfter;
            bool exact;
            switch (from.Family)
            {
                case FormatFamily.Image or FormatFamily.Vector:
                {
                    if (firstSize > LargestPicture)
                        return null;
                    firstAfter = await ConvertedSize(converter, first, from, target, settings, work, ct);
                    exact = files.Count == 1;
                    break;
                }
                case FormatFamily.Audio or FormatFamily.Video:
                {
                    var info = await MediaProbe.ProbeAsync(first, ct);
                    double seconds = info.Duration.TotalSeconds;
                    if (seconds <= 0)
                        return null;
                    if (seconds <= SampleSeconds * 2)
                    {
                        firstAfter = await ConvertedSize(converter, first, from, target, settings, work, ct);
                        exact = files.Count == 1;
                        break;
                    }
                    // A few seconds from the middle, copied as they are, stand for the whole.
                    double start = seconds / 2 - SampleSeconds / 2;
                    var sample = Path.Combine(work, "sample" + Path.GetExtension(first));
                    if (from.Family == FormatFamily.Video)
                        await VideoTools.TrimAsync(first, sample, new TrimVideoOptions(start, start + SampleSeconds, FastCopy: true), null, ct);
                    else
                        await AudioTools.TrimAsync(first, sample, new TrimAudioOptions(start, start + SampleSeconds), null, ct);
                    var sampleInfo = await MediaProbe.ProbeAsync(sample, ct);
                    double sampleSeconds = sampleInfo.Duration.TotalSeconds;
                    if (sampleSeconds < 1)
                        return null;
                    double perSecond = await ConvertedSize(converter, sample, from, target, settings, work, ct) / sampleSeconds;
                    firstAfter = perSecond * seconds;
                    exact = false;
                    break;
                }
                default:
                    return null;
            }
            long after = (long)Math.Round(firstAfter * before / firstSize);
            return new PetalPreview(Describe(before, after, exact));
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static async Task<double> ConvertedSize(IConverter converter, string input, FileFormat from, FileFormat to, ConversionSettings settings, string work, CancellationToken ct)
    {
        var output = Path.Combine(work, "result" + Guid.NewGuid().ToString("N")[..6] + "." + to.WriteExtension);
        var result = await converter.ConvertAsync(new ConversionJob(input, from, to, output, settings), null, ct);
        return result.OutputPaths.Sum(p => File.Exists(p) ? new FileInfo(p).Length : Directory.Exists(p) ? new DirectoryInfo(p).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length) : 0);
    }

    /// <summary>"640 KB · 78% smaller", or "About 4.1 MB · 12% larger" when worked out from part of the file.</summary>
    internal static string Describe(long before, long after, bool exact)
    {
        var size = Sizes.Format(after);
        var amount = exact ? size : L.F("About {0}", size);
        double change = (after - before) / (double)before;
        int percent = (int)Math.Round(Math.Abs(change) * 100);
        if (percent < 1)
            return amount + " · " + L.T("About the same size");
        return amount + " · " + L.F(change < 0 ? "{0}% smaller" : "{0}% larger", percent);
    }

    private static async Task<PetalPreview?> CutOutAsync(string file, CancellationToken ct)
    {
        if (Formats.FromPath(file) is not { Family: FormatFamily.Image })
            return null;
        var png = await BackgroundRemover.PreviewAsync(file, 320, null, ct);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = new MemoryStream(png);
        image.EndInit();
        image.Freeze();
        return new PetalPreview(L.T("The subject, without its background"), image);
    }

    /// <summary>Clears what is remembered (the settings for quality changed).</summary>
    public void Forget()
    {
        lock (_gate)
        {
            _made.Clear();
            _order.Clear();
        }
    }
}
