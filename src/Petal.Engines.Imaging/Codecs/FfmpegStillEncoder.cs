using System.Globalization;
using ImageMagick;
using Petal.Core.Util;

namespace Petal.Engines.Imaging;

/// <summary>Still-image encoders that Magick.NET lacks, driven through FFmpeg.</summary>
internal static class FfmpegStillEncoder
{
    private static readonly Lazy<string> EncoderList = new(() => Probe("-encoders"));
    private static readonly Lazy<string> MuxerList = new(() => Probe("-muxers"));
    private static readonly Lazy<IReadOnlyList<string>> HevcEncoders = new(FindHevcEncoders);

    /// <summary>A graphics card runs only a few encoders at once (five on a GeForce): HEIC photos wait their turn.</summary>
    private static readonly SemaphoreSlim HevcGate = new(2);

    internal const string NoHevcText = "Saving HEIC needs Windows' HEVC encoder: a graphics card that encodes HEVC, or the HEVC Video Extensions from the Microsoft Store.";

    /// <summary>Windows' HEVC encoder works through FFmpeg (needed for HEIC output).</summary>
    public static bool CanEncodeHevc => HevcEncoders.Value.Count > 0;

    /// <summary>The ways to encode HEVC that work here, best first: "1" the graphics card, "0" Windows' software encoder.</summary>
    public static IReadOnlyList<string> HevcModes => HevcEncoders.Value;

    /// <summary>
    /// "1" when the graphics card's HEVC encoder works, "0" when Windows' software one does (the HEVC Video
    /// Extensions bring it); none when neither does, as Windows itself has no HEVC encoder of its own.
    /// </summary>
    private static IReadOnlyList<string> FindHevcEncoders()
    {
        var found = new List<string>();
        if (ToolLocator.Ffmpeg is not { } ffmpeg)
            return found;
        foreach (var hardware in new[] { "1", "0" })
        {
            try
            {
                var result = Task.Run(() => ProcessRunner.RunAsync(ffmpeg, ["-hide_banner", "-nostdin", "-loglevel", "error",
                    "-f", "lavfi", "-i", "color=c=gray:s=256x256:r=30:d=0.1", "-frames:v", "2", "-pix_fmt", "nv12",
                    "-c:v", "hevc_mf", "-hw_encoding", hardware, "-g", "1", "-rate_control", "quality", "-quality", "50", "-f", "null", "-"])).GetAwaiter().GetResult();
                if (result.Succeeded)
                    found.Add(hardware);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
            {
                return found;
            }
        }
        return found;
    }

    public static bool CanEncodeAvif =>
        ToolLocator.Ffmpeg is not null &&
        AvifEncoder is not null &&
        MuxerList.Value.Contains(" avif ", StringComparison.Ordinal);

    private static string? AvifEncoder =>
        EncoderList.Value.Contains(" libaom-av1 ", StringComparison.Ordinal) ? "libaom-av1" :
        EncoderList.Value.Contains(" libsvtav1 ", StringComparison.Ordinal) ? "libsvtav1" : null;

    /// <summary>Maps 1–100 quality to an x265-style CRF (≈18 at 90, ≈30 at 50).</summary>
    public static int HevcCrf(int quality) => Math.Clamp((int)Math.Round(45 - 0.3 * Math.Clamp(quality, 1, 100)), 0, 51);

    /// <summary>Windows' quality setting (0–100) giving about what x265 gives at a CRF, as measured for its H.264 encoder.</summary>
    private static readonly (int Crf, int Quality)[] MfQuality =
        [(0, 95), (14, 88), (18, 77), (20, 71), (23, 59), (26, 46), (28, 38), (32, 27), (36, 18), (40, 11), (51, 1)];

    internal static int HevcQuality(int quality)
    {
        var crf = HevcCrf(quality);
        for (var i = 1; i < MfQuality.Length; i++)
        {
            var (high, low) = (MfQuality[i - 1], MfQuality[i]);
            if (crf > low.Crf)
                continue;
            return (int)Math.Round(high.Quality + (low.Quality - high.Quality) * (double)(crf - high.Crf) / (low.Crf - high.Crf));
        }
        return 1;
    }

    /// <summary>
    /// Encodes raw RGB24 frames (all <paramref name="width"/> × <paramref name="height"/>) as an
    /// all-intra Annex-B HEVC stream, full-range BT.709 4:2:0, with Windows' HEVC encoder. BT.709 because
    /// Windows' encoder writes no matrix into the stream, and decoders then take BT.709.
    /// </summary>
    public static async Task<byte[]> EncodeHevcAsync(Stream rgbFrames, int width, int height, int quality, string preset,
        bool signalSrgb, CancellationToken cancellationToken, string? mode = null)
    {
        var ffmpeg = ToolLocator.Ffmpeg ?? throw new NotSupportedException("HEIC output needs FFmpeg, which was not found.");
        var hardware = mode ?? (HevcEncoders.Value.Count > 0 ? HevcEncoders.Value[0] : throw new NotSupportedException(NoHevcText));
        _ = preset;
        using var output = AtomicFile.CreateScratch("hevc");
        var args = new List<string>
        {
            "-hide_banner", "-loglevel", "error",
            "-f", "rawvideo", "-pix_fmt", "rgb24", "-s", $"{width}x{height}", "-framerate", "30", "-i", "pipe:0",
            "-vf", "scale=out_color_matrix=bt709:out_range=full:flags=accurate_rnd+full_chroma_int,format=nv12",
            // Every tile a picture of its own: a key frame each.
            "-c:v", "hevc_mf", "-hw_encoding", hardware, "-g", "1", "-rate_control", "quality",
            "-quality", HevcQuality(quality).ToString(CultureInfo.InvariantCulture),
            "-color_range", "pc", "-colorspace", "bt709",
        };
        if (signalSrgb)
            args.AddRange(["-color_primaries", "bt709", "-color_trc", "iec61966-2-1"]);
        args.AddRange(["-f", "hevc", "-y", output.Path]);

        await HevcGate.WaitAsync(cancellationToken);
        try
        {
            var result = await ProcessRunner.RunAsync(ffmpeg, args, standardInput: rgbFrames, cancellationToken: cancellationToken);
            if (!result.Succeeded || !File.Exists(output.Path))
                throw new ExternalToolException("ffmpeg", result);
            return await File.ReadAllBytesAsync(output.Path, cancellationToken);
        }
        finally
        {
            HevcGate.Release();
        }
    }

    /// <summary>AVIF through FFmpeg's AV1 encoders, keeping transparency as an alpha plane.</summary>
    public static async Task EncodeAvifAsync(MagickImage image, Stream destination, int quality, CancellationToken cancellationToken)
    {
        var ffmpeg = ToolLocator.Ffmpeg ?? throw new NotSupportedException("FFmpeg was not found.");
        var encoder = AvifEncoder ?? throw new NotSupportedException("FFmpeg has no AV1 encoder.");
        bool alpha = image.HasAlpha;
        var pixels = image.GetPixels().ToByteArray(alpha ? PixelMapping.RGBA : PixelMapping.RGB)
            ?? throw new InvalidOperationException("Could not read pixels");
        int w = (int)image.Width, h = (int)image.Height;
        int crf = Math.Clamp((int)Math.Round(63 - 0.55 * Math.Clamp(quality, 1, 100)), 0, 63);

        using var output = AtomicFile.CreateScratch("avif");
        var args = new List<string>
        {
            "-hide_banner", "-loglevel", "error",
            "-f", "rawvideo", "-pix_fmt", alpha ? "rgba" : "rgb24", "-s", $"{w}x{h}", "-i", "pipe:0", "-frames:v", "1",
        };
        const string Yuv = "scale=out_color_matrix=bt601:out_range=full:flags=accurate_rnd+full_chroma_int,format=yuv420p";
        if (alpha)
        {
            // The alpha plane would inherit the RGB input's "identity" matrix, which AV1 forbids with subsampling.
            args.AddRange(["-filter_complex", $"[0:v]split=2[c][a];[c]{Yuv}[cv];[a]alphaextract,format=gray,setparams=colorspace=smpte170m:range=pc[av]",
                "-map", "[cv]", "-map", "[av]"]);
        }
        else
        {
            args.AddRange(["-vf", Yuv]);
        }
        args.AddRange(["-color_range", "pc", "-colorspace", "smpte170m", "-color_primaries", "bt709", "-color_trc", "iec61966-2-1"]);
        args.AddRange(["-c:v", encoder, "-crf", crf.ToString(CultureInfo.InvariantCulture)]);
        if (encoder == "libaom-av1")
            args.AddRange(["-still-picture", "1", "-cpu-used", "6", "-row-mt", "1"]);
        args.AddRange(["-f", "avif", "-y", output.Path]);

        using var input = new MemoryStream(pixels, writable: false);
        var result = await ProcessRunner.RunAsync(ffmpeg, args, standardInput: input, cancellationToken: cancellationToken);
        if (!result.Succeeded || !File.Exists(output.Path))
            throw new ExternalToolException("ffmpeg", result);
        await using var fs = File.OpenRead(output.Path);
        await fs.CopyToAsync(destination, cancellationToken);
    }

    private static string Probe(string listFlag)
    {
        var ffmpeg = ToolLocator.Ffmpeg;
        if (ffmpeg is null)
            return "";
        try
        {
            var result = Task.Run(() => ProcessRunner.RunAsync(ffmpeg, ["-hide_banner", listFlag])).GetAwaiter().GetResult();
            return result.StandardOutput;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            return "";
        }
    }
}
