using Petal.Engines.Imaging.Metadata;
using F = Petal.Core.Formats.Formats;

namespace Petal.Engines.Imaging;

/// <summary>
/// Metadata tool for images: read every EXIF/GPS/IPTC/XMP/ICC field, edit or remove fields,
/// or remove everything. JPEG, PNG and WEBP are edited losslessly; HEIC, AVIF, TIFF, BMP and
/// GIF are re-encoded (high quality, all pages/frames for TIFF and GIF).
/// </summary>
public static class ImageMetadata
{
    public static Task<MetadataReport> ReadAsync(string path, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            var bytes = File.ReadAllBytes(path);
            var format = ImageFormats.Sniff(bytes) ?? F.FromPath(path);
            var state = MetadataState.Load(bytes, format);
            cancellationToken.ThrowIfCancellationRequested();
            return new MetadataReport(path, format, state.Entries(path, bytes.LongLength), state.Summary(), MetadataWriter.IsLossless(format));
        }, cancellationToken);

    /// <summary>Applies edits in order and saves the result to <paramref name="outputPath"/> (which must not exist).</summary>
    /// <exception cref="ArgumentException">A field can't be edited/removed or a value doesn't parse.</exception>
    public static Task ApplyAsync(string inputPath, string outputPath, IReadOnlyList<MetadataChange> changes,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default) =>
        Task.Run(async () =>
        {
            var bytes = await File.ReadAllBytesAsync(inputPath, cancellationToken);
            var format = ImageFormats.Sniff(bytes) ?? F.FromPath(inputPath);
            var state = MetadataState.Load(bytes, format);
            foreach (var change in changes)
                state.Apply(change);
            progress?.Report(0.3);
            await MetadataWriter.WriteAsync(inputPath, bytes, format, state, outputPath, removeAll: false, cancellationToken);
            progress?.Report(1);
        }, cancellationToken);

    /// <summary>Removes one field.</summary>
    public static Task RemoveTagAsync(string inputPath, string outputPath, string entryId,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default) =>
        ApplyAsync(inputPath, outputPath, [new MetadataChange.Remove(entryId)], progress, cancellationToken);

    /// <summary>
    /// "Remove All": strips EXIF (camera, GPS, maker notes), XMP, IPTC, comments and hidden camera
    /// data such as HDR gain maps, keeping the colour profile and the photo's orientation.
    /// </summary>
    public static Task RemoveAllAsync(string inputPath, string outputPath, IProgress<double>? progress = null,
        CancellationToken cancellationToken = default) =>
        Task.Run(async () =>
        {
            var bytes = await File.ReadAllBytesAsync(inputPath, cancellationToken);
            var format = ImageFormats.Sniff(bytes) ?? F.FromPath(inputPath);
            var state = MetadataState.Load(bytes, format);
            progress?.Report(0.3);
            await MetadataWriter.WriteAsync(inputPath, bytes, format, state, outputPath, removeAll: true, cancellationToken);
            progress?.Report(1);
        }, cancellationToken);

    /// <summary>Batch "Remove All" (the only metadata action Petal offers for several files).</summary>
    public static Task<IReadOnlyList<ImageBatchResult>> RemoveAllBatchAsync(IReadOnlyList<ImageJobItem> items,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default) =>
        ToolSupport.RunBatchAsync(items, i => (i.InputPath, i.OutputPath),
            (i, p, ct) => RemoveAllAsync(i.InputPath, i.OutputPath, p, ct), progress, cancellationToken);
}
