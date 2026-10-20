using Petal.Engines.Media.Internal;

namespace Petal.Engines.Media;

/// <summary>What the installed FFmpeg can do on this machine. Probes run once and are cached.</summary>
public static class MediaCapabilities
{
    private static readonly Lock Gate = new();
    private static Task<bool>? _nvenc;
    private static Task<bool>? _rubberband;

    /// <summary>FFmpeg and FFprobe were both found.</summary>
    public static bool IsAvailable => Ffmpeg.IsAvailable;

    /// <summary>
    /// True when NVIDIA's H.264 encoder actually opens (the build may include NVENC while the GPU or driver cannot run it).
    /// </summary>
    public static Task<bool> HasHardwareH264Async()
    {
        lock (Gate)
            return _nvenc ??= ProbeAsync(["-f", "lavfi", "-i", "color=c=black:s=256x256:r=25:d=0.2", "-frames:v", "3", "-c:v", "h264_nvenc", "-f", "null", "-"]);
    }

    /// <summary>The Rubber Band time-stretching filter is present (better quality than atempo at extreme speeds).</summary>
    internal static Task<bool> HasRubberbandAsync()
    {
        lock (Gate)
            return _rubberband ??= ProbeAsync(["-f", "lavfi", "-i", "sine=f=440:r=44100:d=0.3", "-af", "rubberband=tempo=2", "-f", "null", "-"]);
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
