using System.Globalization;
using System.Numerics;
using Petal.Core.Util;
using Petal.Engines.Media.Internal;

namespace Petal.Engines.Media;

/// <param name="Frames">Difference hashes of frames at a tenth, half and nine tenths of the way through.</param>
public sealed record VideoPrint(double DurationSeconds, int Width, int Height, ulong[] Frames)
{
    /// <summary>
    /// True when two prints look like the same video: lengths within 2 % (or a second), and the
    /// three frames alike (re-encoded or resized copies, not merely similar scenes).
    /// </summary>
    public bool LooksLike(VideoPrint other)
    {
        double longer = Math.Max(DurationSeconds, other.DurationSeconds);
        if (Math.Abs(DurationSeconds - other.DurationSeconds) > Math.Max(1, longer * 0.02))
            return false;
        if (Width > 0 && other.Width > 0 && Math.Abs((double)Width / Height - (double)other.Width / other.Height) > 0.05)
            return false;
        int total = 0;
        for (int i = 0; i < Math.Min(Frames.Length, other.Frames.Length); i++)
            total += BitOperations.PopCount(Frames[i] ^ other.Frames[i]);
        return total <= 18;
    }
}

/// <summary>A small fingerprint of a video's pictures, for finding copies that differ in size or encoding.</summary>
public static class VideoFingerprint
{
    public static async Task<VideoPrint?> ComputeAsync(string path, CancellationToken cancellationToken = default)
    {
        MediaInfo info;
        try
        {
            info = await MediaProbe.ProbeAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is MediaException or ExternalToolException or IOException)
        {
            return null;
        }
        if (!info.HasVideo || info.DurationSeconds < 0.5)
            return null;
        var frames = new ulong[3];
        double[] at = [0.1, 0.5, 0.9];
        for (int i = 0; i < 3; i++)
        {
            var pixels = new byte[72];
            try
            {
                var time = (info.DurationSeconds * at[i]).ToString("0.###", CultureInfo.InvariantCulture);
                await FfmpegProcess.RunReadingStdoutAsync(Ffmpeg.Exe,
                    ["-hide_banner", "-nostdin", "-loglevel", "error", "-ss", time, "-i", path, "-frames:v", "1",
                     "-vf", "scale=9:8:flags=area,format=gray", "-f", "rawvideo", "pipe:1"],
                    async (stdout, ct) =>
                    {
                        int read = 0, n;
                        while (read < pixels.Length && (n = await stdout.ReadAsync(pixels.AsMemory(read), ct).ConfigureAwait(false)) > 0)
                            read += n;
                    }, cancellationToken).ConfigureAwait(false);
            }
            catch (ExternalToolException)
            {
                return null;
            }
            ulong hash = 0;
            for (int y = 0, bit = 0; y < 8; y++)
            {
                for (int x = 0; x < 8; x++, bit++)
                {
                    if (pixels[y * 9 + x] > pixels[y * 9 + x + 1])
                        hash |= 1UL << bit;
                }
            }
            frames[i] = hash;
        }
        return new VideoPrint(info.DurationSeconds, info.Width, info.Height, frames);
    }
}
