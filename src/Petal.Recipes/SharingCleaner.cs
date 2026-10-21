using System.IO;
using Petal.Core.Formats;
using Petal.Engines.Documents.Office;
using Petal.Engines.Documents.Pdf;
using Petal.Engines.Imaging;
using Petal.Engines.Media;

namespace Petal.Recipes;

/// <summary>
/// Clean for Sharing: copies without where a photo or video was taken, which device made it, and
/// who wrote a PDF or Office file. Pictures keep their colour profile and orientation; audio and
/// video streams are copied, not re-encoded.
/// </summary>
public static class SharingCleaner
{
    public static bool CanClean(string path)
    {
        if (OfficeMetadata.CanClean(path))
            return true;
        var format = Formats.FromPath(path);
        return format?.Family is FormatFamily.Image or FormatFamily.Audio or FormatFamily.Video or FormatFamily.Pdf || format == Formats.Psd || format == Formats.Psb;
    }

    public static async Task CleanAsync(string input, string output, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        if (OfficeMetadata.CanClean(input))
        {
            await OfficeMetadata.CleanAsync(input, output, cancellationToken);
            return;
        }
        var format = Formats.FromPath(input);
        if (format == Formats.Psd || format == Formats.Psb)
        {
            await Petal.Engines.Imaging.PhotoshopTools.CleanForSharingAsync(input, output, cancellationToken);
            return;
        }
        switch (format?.Family)
        {
            case FormatFamily.Image:
                await ImageMetadata.RemoveAllAsync(input, output, progress, cancellationToken);
                break;
            case FormatFamily.Audio or FormatFamily.Video:
                await MediaMetadata.RemoveAllAsync(input, output, progress, cancellationToken);
                break;
            case FormatFamily.Pdf:
                await PdfMetadataEditor.RemoveAllAsync(input, output, cancellationToken);
                break;
            default:
                throw new NotSupportedException("Petal can't clean this kind of file.");
        }
    }

    /// <summary>True when a photo or video records where it was taken.</summary>
    public static async Task<bool> HasLocationAsync(string path, CancellationToken cancellationToken = default)
    {
        try
        {
            switch (Formats.FromPath(path)?.Family)
            {
                case FormatFamily.Image:
                    return (await ImageMetadata.ReadAsync(path, cancellationToken)).Summary.HasLocation;
                case FormatFamily.Video:
                    var groups = await MediaMetadata.ReadAsync(path, cancellationToken);
                    return groups.SelectMany(g => g.Entries).Any(e => e.Key.Contains("location", StringComparison.OrdinalIgnoreCase) && e.Value.Length > 0);
                default:
                    return false;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }
}
