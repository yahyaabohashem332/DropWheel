using Petal.Core.Util;

namespace Petal.Engines.Imaging;

/// <summary>
/// Camera RAW pictures are decoded without their metadata; this copies it from the original into
/// the converted picture with ExifTool: date, camera and lens, exposure, place, and the camera's
/// own notes. The picture is already upright, so the orientation becomes "normal".
/// </summary>
internal static class RawMetadata
{
    public static async Task CopyAsync(string raw, string output, CancellationToken cancellationToken)
    {
        if (ToolLocator.ExifTool is null || !File.Exists(output))
            return;
        // A picture that converted but couldn't take the metadata is still a good picture: failures are ignored.
        await ExifToolProcess.RunAsync(
        [
            "-m", "-q", "-q", "-overwrite_original",
            "-tagsFromFile", raw, "-exif:all", "-xmp:all", "-iptc:all",
            // Not the RAW's own thumbnail, nor its sensor size.
            "--IFD1:all", "--ExifImageWidth", "--ExifImageHeight",
            "-Orientation#=1",
            output,
        ], cancellationToken).ConfigureAwait(false);
    }
}
