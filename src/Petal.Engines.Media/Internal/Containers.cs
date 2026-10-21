using System.Collections.Frozen;
using Petal.Core.Formats;
using F = Petal.Core.Formats.Formats;

namespace Petal.Engines.Media.Internal;

internal enum VideoCodecKind { None, H264, Vp9, Mpeg4, Wmv2, Gif }

internal enum AudioCodecKind { None, Aac, Mp3, Opus, Vorbis, PcmLe, PcmBe, Flac, Wma }

/// <summary>How Petal writes one container: muxer, default encoders, and which source codecs can be copied as-is.</summary>
internal sealed record ContainerSpec(
    string Id,
    string Muxer,
    VideoCodecKind Video,
    AudioCodecKind Audio,
    FrozenSet<string> CopyVideo,
    FrozenSet<string> CopyAudio,
    bool FastStart = false,
    bool MultipleAudio = false,
    string? SubtitleCodec = null,
    bool CoverArt = false)
{
    public bool IsAudioOnly => Video == VideoCodecKind.None;

    public bool IsGif => Video == VideoCodecKind.Gif;

    /// <summary>MP4/MOV want HEVC tagged "hvc1" for Apple and Windows players.</summary>
    public bool IsIsoMp4 => Muxer is "mp4" or "mov" or "ipod";
}

/// <summary>Video quality: CRF-style constant quality, or an average bitrate (two-pass where the encoder supports it).</summary>
internal sealed record VideoQuality(int Crf, string Preset = "medium", int? BitrateKbps = null);

internal static class Containers
{
    private static FrozenSet<string> Set(params string[] items) => items.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenSet<string> None = Set();

    private static readonly FrozenDictionary<string, ContainerSpec> ById = new ContainerSpec[]
    {
        new("mp4", "mp4", VideoCodecKind.H264, AudioCodecKind.Aac,
            Set("h264", "hevc", "av1", "mpeg4"), Set("aac", "mp3", "ac3", "eac3", "alac"),
            FastStart: true, MultipleAudio: true, SubtitleCodec: "mov_text"),
        new("mov", "mov", VideoCodecKind.H264, AudioCodecKind.Aac,
            Set("h264", "hevc", "mpeg4", "prores", "mjpeg"), Set("aac", "mp3", "alac", "ac3", "pcm_s16le", "pcm_s16be", "pcm_s24le", "pcm_s24be"),
            FastStart: true, MultipleAudio: true, SubtitleCodec: "mov_text"),
        new("mkv", "matroska", VideoCodecKind.H264, AudioCodecKind.Aac,
            Set("h264", "hevc", "av1", "vp8", "vp9", "mpeg4", "mpeg2video", "mjpeg", "prores", "theora", "ffv1"),
            Set("aac", "mp3", "opus", "vorbis", "flac", "ac3", "eac3", "dts", "alac", "truehd", "mp2", "pcm_s16le", "pcm_s24le", "pcm_f32le"),
            MultipleAudio: true, SubtitleCodec: "srt"),
        new("webm", "webm", VideoCodecKind.Vp9, AudioCodecKind.Opus,
            Set("vp8", "vp9", "av1"), Set("opus", "vorbis"), MultipleAudio: true),
        new("avi", "avi", VideoCodecKind.Mpeg4, AudioCodecKind.Mp3,
            Set("mpeg4", "msmpeg4v3", "mjpeg"), Set("mp3", "ac3", "mp2", "pcm_s16le")),
        new("wmv", "asf", VideoCodecKind.Wmv2, AudioCodecKind.Wma,
            Set("wmv1", "wmv2", "wmv3", "vc1"), Set("wmav1", "wmav2", "wmapro")),
        new("gif", "gif", VideoCodecKind.Gif, AudioCodecKind.None, None, None),

        new("mp3", "mp3", VideoCodecKind.None, AudioCodecKind.Mp3, None, Set("mp3"), CoverArt: true),
        new("m4a", "ipod", VideoCodecKind.None, AudioCodecKind.Aac, None, Set("aac", "alac"), FastStart: true, CoverArt: true),
        new("wav", "wav", VideoCodecKind.None, AudioCodecKind.PcmLe, None, Set("pcm_s16le", "pcm_s24le", "pcm_s32le", "pcm_f32le", "pcm_u8")),
        new("aiff", "aiff", VideoCodecKind.None, AudioCodecKind.PcmBe, None, Set("pcm_s16be", "pcm_s24be", "pcm_s32be")),
        new("flac", "flac", VideoCodecKind.None, AudioCodecKind.Flac, None, Set("flac"), CoverArt: true),
        new("ogg", "ogg", VideoCodecKind.None, AudioCodecKind.Vorbis, None, Set("vorbis", "opus", "flac")),
        new("opus", "opus", VideoCodecKind.None, AudioCodecKind.Opus, None, Set("opus")),
        new("wma", "asf", VideoCodecKind.None, AudioCodecKind.Wma, None, Set("wmav1", "wmav2", "wmapro")),
    }.ToFrozenDictionary(s => s.Id, StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenSet<string> TextSubtitles = Set("subrip", "ass", "ssa", "mov_text", "webvtt", "text");

    private static readonly FrozenSet<string> LosslessAudio = Set("flac", "alac", "wavpack", "ape", "tta", "truehd", "mlp");

    public static bool Supports(FileFormat format) => ById.ContainsKey(format.Id);

    public static ContainerSpec For(FileFormat format) =>
        ById.TryGetValue(format.Id, out var spec) ? spec : throw new NotSupportedException($"Petal cannot write {format.Label} media files");

    /// <summary>The spec for an output path, falling back to the input's format for unknown extensions.</summary>
    public static ContainerSpec ForOutput(string outputPath, string inputPath)
    {
        foreach (var path in new[] { outputPath, inputPath })
        {
            var format = F.FromPath(path);
            if (format is null || !Supports(format))
                continue;
            // ".aac" files are raw ADTS streams, not MP4 containers like ".m4a".
            if (path.EndsWith(".aac", StringComparison.OrdinalIgnoreCase))
                return For(format) with { Id = "aac", Muxer = "adts", FastStart = false, CoverArt = false, CopyAudio = Set("aac") };
            return For(format);
        }
        throw new MediaException($"Petal cannot write \"{Path.GetExtension(outputPath)}\" media files.");
    }

    /// <summary>The subtitle codec to write for a source subtitle stream, or null when it cannot be carried over.</summary>
    public static string? SubtitleCodecFor(ContainerSpec target, MediaStreamInfo stream)
    {
        if (target.SubtitleCodec is null || !TextSubtitles.Contains(stream.Codec))
            return null;
        return target.SubtitleCodec switch
        {
            "srt" when stream.Codec is "subrip" or "ass" or "ssa" or "webvtt" => "copy",
            "mov_text" when stream.Codec == "mov_text" => "copy",
            _ => target.SubtitleCodec,
        };
    }

    /// <summary>Bit depth to keep when writing PCM or FLAC: 24 for high-resolution lossless sources, else 16.</summary>
    public static int BitDepthOf(MediaStreamInfo? audio)
    {
        if (audio is null)
            return 16;
        var lossless = audio.Codec.StartsWith("pcm_", StringComparison.Ordinal) || LosslessAudio.Contains(audio.Codec);
        return lossless && audio.BitsPerSample > 16 ? 24 : 16;
    }

    public static bool IsLossless(MediaStreamInfo audio) =>
        audio.Codec.StartsWith("pcm_", StringComparison.Ordinal) || LosslessAudio.Contains(audio.Codec);

    public static List<string> VideoEncoderArgs(VideoCodecKind kind, VideoQuality q, bool hardware, int pass = 0, string? passLog = null)
    {
        var args = new List<string>();
        var bitrate = q.BitrateKbps is { } kb ? Math.Max(16, kb) : (int?)null;
        switch (kind)
        {
            case VideoCodecKind.H264 when hardware:
                args.AddRange(["-c:v", "h264_nvenc", "-preset", "p5", "-tune", "hq", "-rc", "vbr"]);
                if (bitrate is { } hb)
                    args.AddRange(["-b:v", $"{hb}k", "-maxrate", $"{hb * 3 / 2}k", "-bufsize", $"{hb * 2}k"]);
                else
                    args.AddRange(["-cq", Inv.I(Math.Clamp(q.Crf + 2, 1, 51)), "-b:v", "0"]);
                args.AddRange(["-pix_fmt", "yuv420p", "-profile:v", "high"]);
                break;
            case VideoCodecKind.H264:
                args.AddRange(["-c:v", "libx264", "-preset", q.Preset]);
                if (bitrate is { } xb)
                    args.AddRange(["-b:v", $"{xb}k", "-maxrate", $"{xb * 2}k", "-bufsize", $"{xb * 4}k"]);
                else
                    args.AddRange(["-crf", Inv.I(Math.Clamp(q.Crf, 0, 51))]);
                args.AddRange(["-pix_fmt", "yuv420p", "-profile:v", "high"]);
                break;
            case VideoCodecKind.Vp9:
                args.AddRange(["-c:v", "libvpx-vp9", "-row-mt", "1", "-deadline", "good", "-cpu-used", "4"]);
                if (bitrate is { } vb)
                    args.AddRange(["-b:v", $"{vb}k"]);
                else
                    args.AddRange(["-crf", Inv.I(Math.Clamp(q.Crf + 11, 4, 63)), "-b:v", "0"]);
                args.AddRange(["-pix_fmt", "yuv420p"]);
                break;
            case VideoCodecKind.Mpeg4:
                // Rate-distortion decisions and 4MV give MPEG-4 Part 2 noticeably smaller files at the same quality.
                args.AddRange(["-c:v", "mpeg4", "-vtag", "xvid", "-mbd", "rd", "-flags", "+mv4+aic", "-trellis", "1"]);
                args.AddRange(bitrate is { } mb ? ["-b:v", $"{mb}k"] : ["-q:v", Inv.I(QScale(q.Crf))]);
                args.AddRange(["-pix_fmt", "yuv420p"]);
                break;
            case VideoCodecKind.Wmv2:
                args.Add("-c:v");
                args.Add("wmv2");
                args.AddRange(bitrate is { } wb ? ["-b:v", $"{wb}k"] : ["-q:v", Inv.I(QScale(q.Crf))]);
                args.AddRange(["-pix_fmt", "yuv420p"]);
                break;
            default:
                throw new NotSupportedException($"No encoder for {kind}");
        }

        if (pass > 0 && passLog is not null && kind is VideoCodecKind.H264 or VideoCodecKind.Vp9 && !hardware)
            args.AddRange(["-pass", Inv.I(pass), "-passlogfile", passLog]);
        return args;
    }

    public static bool SupportsTwoPass(VideoCodecKind kind) => kind is VideoCodecKind.H264 or VideoCodecKind.Vp9;

    public static List<string> AudioEncoderArgs(AudioCodecKind kind, int kbps, int bitDepth, bool maxFlacCompression = false) => kind switch
    {
        AudioCodecKind.Aac => ["-c:a", "aac", "-b:a", $"{Math.Clamp(kbps, 24, 512)}k"],
        AudioCodecKind.Mp3 => ["-c:a", "libmp3lame", "-b:a", $"{Mp3Bitrate(kbps)}k"],
        AudioCodecKind.Opus => ["-c:a", "libopus", "-b:a", $"{Math.Clamp(kbps, 8, 256)}k", "-vbr", "on"],
        AudioCodecKind.Vorbis => ["-c:a", "libvorbis", "-q:a", Inv.I(VorbisQuality(kbps))],
        AudioCodecKind.PcmLe => ["-c:a", bitDepth >= 24 ? "pcm_s24le" : "pcm_s16le"],
        AudioCodecKind.PcmBe => ["-c:a", bitDepth >= 24 ? "pcm_s24be" : "pcm_s16be"],
        // A fixed 4608-sample block: FFmpeg 9 otherwise copies the source's frame size (47, 576,
        // 1152… samples), and Windows can't play FLAC with such blocks.
        AudioCodecKind.Flac => ["-c:a", "flac", "-compression_level", maxFlacCompression ? "8" : "5", "-sample_fmt", bitDepth >= 24 ? "s32" : "s16", "-frame_size", "4608"],
        AudioCodecKind.Wma => ["-c:a", "wmav2", "-b:a", $"{Math.Clamp(kbps, 32, 192)}k"],
        _ => throw new NotSupportedException($"No encoder for {kind}"),
    };

    /// <summary>Extra muxer options: fast start for MP4/MOV, Windows-friendly ID3 tags, ID3 in AIFF.</summary>
    public static List<string> MuxerArgs(ContainerSpec spec)
    {
        var args = new List<string>();
        if (spec.FastStart)
            args.AddRange(["-movflags", "+faststart"]);
        if (spec.Muxer == "mp3")
            args.AddRange(["-id3v2_version", "3"]);
        if (spec.Muxer == "aiff")
            args.AddRange(["-write_id3v2", "1"]);
        return args;
    }

    private static readonly int[] Mp3Rates = [32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320];

    private static int Mp3Bitrate(int kbps) => Mp3Rates.MinBy(r => Math.Abs(r - kbps));

    private static int VorbisQuality(int kbps) => kbps switch
    {
        <= 64 => 0,
        <= 80 => 1,
        <= 96 => 2,
        <= 112 => 3,
        <= 128 => 4,
        <= 160 => 5,
        <= 192 => 6,
        <= 224 => 7,
        <= 256 => 8,
        <= 320 => 9,
        _ => 10,
    };

    /// <summary>MPEG-4 part 2 / WMV quantiser roughly matching an x264 CRF.</summary>
    private static int QScale(int crf) => Math.Clamp((int)Math.Round(crf / 5.0), 2, 31);
}
