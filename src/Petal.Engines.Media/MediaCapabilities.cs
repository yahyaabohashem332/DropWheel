using Petal.Engines.Media.Internal;

namespace Petal.Engines.Media;

/// <summary>What the installed FFmpeg can do on this machine. Probes run once and are cached.</summary>
public static class MediaCapabilities
{
    private static readonly Lock Gate = new();
    private static Task<bool>? _hardware;

    /// <summary>FFmpeg and FFprobe were both found.</summary>
    public static bool IsAvailable => Ffmpeg.IsAvailable;

    /// <summary>
    /// True when the graphics card's H.264 encoder actually opens through Windows (a PC may have one whose driver
    /// can't run it, or none).
    /// </summary>
    public static Task<bool> HasHardwareH264Async()
    {
        lock (Gate)
            return _hardware ??= ProbeAsync(["-f", "lavfi", "-i", "color=c=black:s=256x256:r=25:d=0.2", "-frames:v", "3",
                "-c:v", "h264_mf", "-hw_encoding", "1", "-rate_control", "quality", "-quality", "70", "-pix_fmt", "nv12", "-f", "null", "-"]);
    }

    private static async Task<bool> ProbeAsync(string[] args)
    {
        if (!Ffmpeg.IsAvailable)
            return false;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var result = await FfmpegProcess.RunAsync(
                Ffmpeg.Exe,
                ["-hide_banner", "-nostdin", "-loglevel", "error", .. args],
                null,
                timeout.Token).ConfigureAwait(false);
            return result.Succeeded;
        }
        catch (Exception e) when (e is OperationCanceledException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
