using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Petal.Engines.Media.Internal;

namespace Petal.Engines.Media;

public enum WaveformChannel
{
    /// <summary>All channels mixed down to mono.</summary>
    Mix,
    Left,
    Right,
}

/// <param name="Channel">Which channel to draw.</param>
/// <param name="Start">Start of the range to draw; null is the beginning.</param>
/// <param name="Length">Length of the range; null runs to the end.</param>
/// <param name="AudioFilter">Optional FFmpeg audio filter applied before measuring, e.g. a tool's preview filter.</param>
public sealed record WaveformOptions(
    WaveformChannel Channel = WaveformChannel.Mix,
    TimeSpan? Start = null,
    TimeSpan? Length = null,
    string? AudioFilter = null);

/// <summary>Per-bucket sample minimum, maximum and RMS in [-1, 1], ready for drawing.</summary>
public sealed record WaveformData(float[] Min, float[] Max, float[] Rms, TimeSpan Duration)
{
    public int Buckets => Max.Length;

    /// <summary>Largest absolute sample value over the whole range.</summary>
    public float Peak => Max.Length == 0 ? 0 : Math.Max(Max.Max(), -Min.Min());
}

/// <param name="SampleRate">Output sample rate in Hz.</param>
/// <param name="Channels">Output channel count (1 = mono, 2 = stereo).</param>
/// <param name="Start">Where to start decoding; null is the beginning.</param>
/// <param name="Length">How much to decode; null runs to the end.</param>
/// <param name="AudioFilter">Optional FFmpeg audio filter, e.g. <see cref="AudioTools.BleepPreviewFilter"/>.</param>
public sealed record PcmDecodeOptions(
    int SampleRate = 44100,
    int Channels = 2,
    TimeSpan? Start = null,
    TimeSpan? Length = null,
    string? AudioFilter = null);

/// <summary>Interleaved 32-bit float samples, e.g. for NAudio's <c>WaveFormat.CreateIeeeFloatWaveFormat</c>.</summary>
public sealed record PcmAudio(float[] Samples, int SampleRate, int Channels)
{
    public int FrameCount => Channels == 0 ? 0 : Samples.Length / Channels;

    public TimeSpan Duration => SampleRate == 0 ? TimeSpan.Zero : TimeSpan.FromSeconds((double)FrameCount / SampleRate);
}

/// <summary>Frames, filmstrips, waveforms and PCM for the tool windows' previews.</summary>
public static class MediaPreview
{
    /// <summary>
    /// One frame as PNG bytes, using fast (keyframe) seeking followed by exact decoding to <paramref name="time"/>.
    /// The frame is display-oriented and scaled down to <paramref name="maxWidth"/> (0 keeps full resolution).
    /// </summary>
    public static async Task<byte[]> ExtractFrameAsync(string path, TimeSpan time, int maxWidth = 0, CancellationToken cancellationToken = default)
    {
        var bytes = await FrameAsync(path, ["-ss", Inv.N(Math.Max(0, time.TotalSeconds))], maxWidth, cancellationToken).ConfigureAwait(false);
        if (bytes.Length == 0 && time > TimeSpan.Zero)
        {
            // Past the last frame: fall back to the end of the stream.
            bytes = await FrameAsync(path, ["-sseof", "-0.5"], maxWidth, cancellationToken).ConfigureAwait(false);
        }
        if (bytes.Length == 0)
            throw new MediaException($"Could not read a frame from \"{Path.GetFileName(path)}\".");
        return bytes;
    }

    /// <summary>Frames at the given times, decoded a few at a time in parallel.</summary>
    public static async Task<IReadOnlyList<byte[]>> ExtractFramesAsync(string path, IReadOnlyList<TimeSpan> times, int maxWidth = 0, CancellationToken cancellationToken = default)
    {
        var results = new byte[times.Count][];
        await Parallel.ForEachAsync(
            Enumerable.Range(0, times.Count),
            new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 2, 4), CancellationToken = cancellationToken },
            async (i, ct) => results[i] = await ExtractFrameAsync(path, times[i], maxWidth, ct).ConfigureAwait(false)).ConfigureAwait(false);
        return results;
    }

    /// <summary><paramref name="count"/> evenly spaced thumbnails (taken at the middle of each slot) for a trim/split filmstrip.</summary>
    public static async Task<IReadOnlyList<byte[]>> ExtractFilmstripAsync(string path, int count, int maxWidth = 160, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        var info = await MediaProbe.ProbeAsync(path, cancellationToken).ConfigureAwait(false);
        if (!info.HasVideo)
            throw new MediaException($"\"{Path.GetFileName(path)}\" has no video.");
        var duration = info.DurationSeconds;
        var times = Enumerable.Range(0, count)
            .Select(i => TimeSpan.FromSeconds(duration <= 0 ? 0 : (i + 0.5) * duration / count))
            .ToList();
        return await ExtractFramesAsync(path, times, maxWidth, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<byte[]> FrameAsync(string path, string[] seek, int maxWidth, CancellationToken cancellationToken)
    {
        var args = new List<string> { "-hide_banner", "-nostdin", "-loglevel", "error" };
        args.AddRange(seek);
        args.AddRange(["-i", path, "-map", "0:v:0", "-an", "-sn", "-dn", "-frames:v", "1"]);
        if (maxWidth > 0)
            args.AddRange(["-vf", $"scale=w=min(iw\\,{maxWidth}):h=-2:flags=bicubic"]);
        args.AddRange(["-c:v", "png", "-f", "image2pipe", "-"]);

        using var buffer = new MemoryStream();
        await FfmpegProcess.RunReadingStdoutAsync(Ffmpeg.Exe, args, (s, ct) => s.CopyToAsync(buffer, ct), cancellationToken).ConfigureAwait(false);
        return buffer.ToArray();
    }

    /// <summary>
    /// Decodes the audio and summarises it into <paramref name="buckets"/> min/max/RMS columns.
    /// Memory stays small for long files: samples are folded into blocks while streaming.
    /// </summary>
    public static async Task<WaveformData> GetWaveformAsync(string path, int buckets, WaveformOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(buckets, 1);
        options ??= new WaveformOptions();
        var info = await MediaProbe.ProbeAsync(path, cancellationToken).ConfigureAwait(false);
        if (!info.HasAudio)
            throw new MediaException($"\"{Path.GetFileName(path)}\" has no audio track.");

        var start = options.Start ?? TimeSpan.Zero;
        var length = options.Length ?? info.Duration - start;
        var seconds = Math.Max(0.01, length.TotalSeconds);

        // Enough samples for every bucket, but no more than needed: 8 kHz is plenty for the shape of a long file.
        var rate = (int)Math.Clamp(Math.Ceiling(buckets * 64 / seconds), 8000, 48000);
        var expected = (long)(rate * seconds);
        var blockSize = (int)Math.Clamp(expected / ((long)buckets * 4), 1, 4096);

        var filters = new List<string>();
        if (!string.IsNullOrEmpty(options.AudioFilter))
            filters.Add(options.AudioFilter);
        var channel = options.Channel switch
        {
            WaveformChannel.Left => "c0",
            WaveformChannel.Right when info.AudioChannels >= 2 => "c1",
            WaveformChannel.Right => "c0",
            _ => null,
        };
        if (channel is not null)
            filters.Add($"pan=mono|c0={channel}");

        var args = DecodeArgs(path, start, options.Length, filters, rate, channels: 1);
        var mins = new List<float>();
        var maxs = new List<float>();
        var sums = new List<double>();
        var counts = new List<int>();

        float bMin = 0, bMax = 0;
        double bSum = 0;
        var bCount = 0;

        await FfmpegProcess.RunReadingStdoutAsync(Ffmpeg.Exe, args, async (stream, ct) =>
        {
            var buffer = new byte[64 * 1024];
            var carry = 0;
            while (true)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(carry), ct).ConfigureAwait(false);
                if (read == 0)
                    break;
                var total = carry + read;
                var whole = total / 4 * 4;
                foreach (var v in MemoryMarshal.Cast<byte, float>(buffer.AsSpan(0, whole)))
                {
                    if (bCount == 0)
                    {
                        bMin = v;
                        bMax = v;
                    }
                    else
                    {
                        if (v < bMin) bMin = v;
                        if (v > bMax) bMax = v;
                    }
                    bSum += (double)v * v;
                    if (++bCount == blockSize)
                    {
                        mins.Add(bMin);
                        maxs.Add(bMax);
                        sums.Add(bSum);
                        counts.Add(bCount);
                        bCount = 0;
                        bSum = 0;
                    }
                }
                carry = total - whole;
                if (carry > 0)
                    Buffer.BlockCopy(buffer, whole, buffer, 0, carry);
            }
        }, cancellationToken).ConfigureAwait(false);

        if (bCount > 0)
        {
            mins.Add(bMin);
            maxs.Add(bMax);
            sums.Add(bSum);
            counts.Add(bCount);
        }

        var min = new float[buckets];
        var max = new float[buckets];
        var rms = new float[buckets];
        var blocks = mins.Count;
        if (blocks > 0)
        {
            for (var b = 0; b < buckets; b++)
            {
                var from = (int)((long)b * blocks / buckets);
                var to = Math.Max(from + 1, (int)((long)(b + 1) * blocks / buckets));
                float lo = float.MaxValue, hi = float.MinValue;
                double sum = 0;
                long n = 0;
                for (var k = from; k < to && k < blocks; k++)
                {
                    lo = Math.Min(lo, mins[k]);
                    hi = Math.Max(hi, maxs[k]);
                    sum += sums[k];
                    n += counts[k];
                }
                min[b] = Math.Clamp(lo, -1, 1);
                max[b] = Math.Clamp(hi, -1, 1);
                rms[b] = n == 0 ? 0 : (float)Math.Min(1, Math.Sqrt(sum / n));
            }
        }
        var decoded = TimeSpan.FromSeconds(counts.Sum(c => (long)c) / (double)rate);
        return new WaveformData(min, max, rms, decoded);
    }

    /// <summary>Decodes audio to interleaved float PCM so the UI can play previews.</summary>
    public static async Task<PcmAudio> DecodePcmAsync(string path, PcmDecodeOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new PcmDecodeOptions();
        ArgumentOutOfRangeException.ThrowIfLessThan(options.SampleRate, 1000);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.Channels, 1);

        var filters = string.IsNullOrEmpty(options.AudioFilter) ? new List<string>() : [options.AudioFilter];
        var args = DecodeArgs(path, options.Start ?? TimeSpan.Zero, options.Length, filters, options.SampleRate, options.Channels);
        using var buffer = new MemoryStream();
        await FfmpegProcess.RunReadingStdoutAsync(Ffmpeg.Exe, args, (s, ct) => s.CopyToAsync(buffer, ct), cancellationToken).ConfigureAwait(false);

        var bytes = buffer.GetBuffer().AsSpan(0, (int)buffer.Length);
        var frameBytes = 4 * options.Channels;
        bytes = bytes[..(bytes.Length / frameBytes * frameBytes)];
        var samples = new float[bytes.Length / 4];
        if (BitConverter.IsLittleEndian)
        {
            MemoryMarshal.Cast<byte, float>(bytes).CopyTo(samples);
        }
        else
        {
            for (var i = 0; i < samples.Length; i++)
                samples[i] = BinaryPrimitives.ReadSingleLittleEndian(bytes[(i * 4)..]);
        }
        return new PcmAudio(samples, options.SampleRate, options.Channels);
    }

    private static List<string> DecodeArgs(string path, TimeSpan start, TimeSpan? length, List<string> filters, int rate, int channels)
    {
        var args = new List<string> { "-hide_banner", "-nostdin", "-loglevel", "error" };
        if (start > TimeSpan.Zero)
            args.AddRange(["-ss", Inv.N(start.TotalSeconds)]);
        args.AddRange(["-i", path]);
        if (length is { } len)
            args.AddRange(["-t", Inv.N(Math.Max(0.001, len.TotalSeconds))]);
        args.AddRange(["-map", "0:a:0", "-vn", "-sn", "-dn"]);
        if (filters.Count > 0)
            args.AddRange(["-af", string.Join(',', filters)]);
        args.AddRange(["-ac", Inv.I(channels), "-ar", Inv.I(rate), "-c:a", "pcm_f32le", "-f", "f32le", "-"]);
        return args;
    }
}
