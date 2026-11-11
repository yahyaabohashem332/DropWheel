using Petal.Engines.Media.Internal;

namespace Petal.Engines.Media;

/// <summary>
/// H.264 and AAC as DropWheel writes them: through Windows' own Media Foundation encoders, which Windows is licensed
/// for, as DropWheel's FFmpeg holds no encoder of either. The graphics card's encoder when asked and it works,
/// Windows' software encoder otherwise.
/// </summary>
public static class MediaEncoders
{
    /// <summary>
    /// Windows' quality setting (0–100) that gives about what x264 gives at a CRF, for its software encoder at its
    /// best and for the graphics card's. Measured by SSIM on camera footage and two kinds of drawn picture.
    /// </summary>
    private static readonly (int Crf, int Software, int Hardware)[] Quality =
    [
        (0, 100, 95), (14, 96, 88), (18, 86, 77), (20, 78, 71), (23, 63, 59), (26, 48, 46), (28, 41, 38), (32, 31, 27),
        (36, 22, 18), (40, 14, 11), (51, 1, 1),
    ];

    /// <summary>The quality setting for a CRF, between the measured ones.</summary>
    internal static int QualityFor(int crf, bool hardware)
    {
        crf = Math.Clamp(crf, 0, 51);
        for (var i = 1; i < Quality.Length; i++)
        {
            var (high, low) = (Quality[i - 1], Quality[i]);
            if (crf > low.Crf)
                continue;
            double t = (double)(crf - high.Crf) / (low.Crf - high.Crf);
            double a = hardware ? high.Hardware : high.Software, b = hardware ? low.Hardware : low.Software;
            return (int)Math.Round(a + (b - a) * t);
        }
        return 1;
    }

    /// <summary>
    /// H.264, High profile. <paramref name="crf"/> is the quality as the settings give it (18 very good, 23 good, 28
    /// small); <paramref name="quality"/> is Windows' own setting (1–100) in its place, and <paramref name="bitrateKbps"/>
    /// an average bitrate instead, which Windows' encoder only keeps to over a long video and well above its smallest
    /// (sizes are met through the quality setting). <paramref name="fast"/> is for recording as it happens, where
    /// keeping up matters more than the last few percent of size.
    /// </summary>
    public static List<string> H264(int crf, bool hardware, bool fast = false, int? bitrateKbps = null, int? quality = null)
    {
        var args = new List<string> { "-c:v", "h264_mf", "-hw_encoding", hardware ? "1" : "0", "-profile:v", "100" };
        if (quality is { } setting)
            args.AddRange(["-rate_control", "quality", "-quality", Inv.I(Math.Clamp(setting, 1, 100))]);
        else if (bitrateKbps is { } kbps)
            args.AddRange(["-rate_control", "u_vbr", "-b:v", $"{Math.Max(16, kbps)}k"]);
        else
            args.AddRange(["-rate_control", "quality", "-quality", Inv.I(QualityFor(crf, hardware))]);
        // The software encoder's slowest setting gives smaller files at a higher quality, at 150 frames of 1080p a
        // second on a laptop's processor; recording live takes its quickest (370).
        if (!hardware)
            args.AddRange(["-compression_level", fast ? "0" : "100"]);
        args.AddRange(["-pix_fmt", "nv12"]);
        return args;
    }

    private static readonly int[] AacKbps = [96, 128, 160, 192];

    /// <summary>
    /// AAC-LC. Windows' encoder takes 44.1 or 48 kHz, one, two or six channels and 96, 128, 160 or 192 kbps: the
    /// nearest of each is used.
    /// </summary>
    public static List<string> Aac(int kbps, int channels = 2, int sampleRate = 48000)
    {
        var rate = AacKbps.MinBy(r => Math.Abs(r - kbps));
        var layout = channels switch { 1 => 1, >= 6 => 6, _ => 2 };
        var hz = sampleRate is 44100 or 22050 or 11025 ? 44100 : 48000;
        return ["-c:a", "aac_mf", "-b:a", $"{rate}k", "-ac", Inv.I(layout), "-ar", Inv.I(hz), "-sample_fmt", "s16"];
    }

    /// <summary>The bitrate Windows' AAC encoder will use for a wish: what a size has to be worked out with.</summary>
    public static int AacKbpsFor(int kbps) => AacKbps.MinBy(r => Math.Abs(r - kbps));

    /// <summary>Whether the graphics card's H.264 encoder opens on this PC.</summary>
    public static Task<bool> HardwareH264Async() => MediaCapabilities.HasHardwareH264Async();
}
