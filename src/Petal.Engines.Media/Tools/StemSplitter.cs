using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.ML.OnnxRuntime;
using Petal.Core.Models;
using Petal.Engines.Media.Internal;

namespace Petal.Engines.Media;

public enum StemSet
{
    /// <summary>The voice, and everything else together.</summary>
    VocalsAndMusic,
    /// <summary>Vocals, drums, bass, and the rest.</summary>
    FourStems,
}

public sealed record SplitStemsOptions(StemSet Stems = StemSet.VocalsAndMusic) : MediaToolOptions;

/// <summary>
/// Separate Vocals: Hybrid Transformer Demucs (Meta, MIT) splits a song into its voice and its
/// instruments, on this PC. The model sees 7.8-second pieces; they overlap by a quarter and are
/// blended with triangular weights, as Demucs itself does.
/// </summary>
public static class StemSplitter
{
    private const int Rate = 44100;
    private const int Segment = 343980;
    private const int Fft = 4096;
    private const int Hop = 1024;
    private const int Bins = Fft / 2;
    private const int Frames = Segment / Hop + 1; // 336
    private const double MaxMinutes = 30;
    private static readonly string[] Sources = ["Drums", "Bass", "Other", "Vocals"];

    public static IReadOnlyList<ModelInfo> Models { get; } = [ModelCatalog.Demucs];

    public static bool IsReady => ModelStore.IsAvailable(ModelCatalog.Demucs);

    /// <summary>Writes the stems into <paramref name="folder"/> as files of <paramref name="extension"/>; returns them.</summary>
    public static async Task<IReadOnlyList<string>> SplitAsync(string input, string folder, string extension, SplitStemsOptions? options = null,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        options ??= new SplitStemsOptions();
        var model = ModelStore.Find(ModelCatalog.Demucs) ?? throw new MediaException("The separation model isn't downloaded yet.");
        var info = await MediaProbe.ProbeAsync(input, cancellationToken).ConfigureAwait(false);
        if (!info.HasAudio)
            throw new MediaException("This file has no sound to separate.");
        if (info.DurationSeconds > MaxMinutes * 60)
            throw new MediaException($"Petal separates up to {MaxMinutes:0} minutes at a time. Trim the file first.");
        var safe = new SafeProgress(progress);
        var pcm = await MediaPreview.DecodePcmAsync(input, new PcmDecodeOptions(Rate, 2), cancellationToken).ConfigureAwait(false);
        int length = pcm.FrameCount;
        if (length < Rate / 4)
            throw new MediaException("The sound is too short to separate.");
        safe.Report(0.05);

        // Channels apart, normalized by the mix's mean and spread (as Demucs does).
        var mix = new float[2][] { new float[length], new float[length] };
        double sum = 0, squares = 0;
        for (int i = 0; i < length; i++)
        {
            mix[0][i] = pcm.Samples[2 * i];
            mix[1][i] = pcm.Samples[2 * i + 1];
            double mono = (mix[0][i] + mix[1][i]) / 2;
            sum += mono;
            squares += mono * mono;
        }
        float mean = (float)(sum / length), std = (float)Math.Sqrt(Math.Max(1e-12, squares / length - (sum / length) * (sum / length)));
        for (int c = 0; c < 2; c++)
            for (int i = 0; i < length; i++)
                mix[c][i] = (mix[c][i] - mean) / std;

        var stems = await Task.Run(() => Separate(model, mix, length, safe.Slice(0.05, 0.85), cancellationToken), cancellationToken).ConfigureAwait(false);

        // Back to the mix's level, then the requested files.
        foreach (var stem in stems)
            for (int c = 0; c < 2; c++)
                for (int i = 0; i < length; i++)
                    stem[c][i] = stem[c][i] * std + mean;
        var outputs = options.Stems == StemSet.FourStems
            ? Sources.Select((name, s) => (name, stems[s])).ToList()
            : [("Vocals", stems[3]), ("Music", Add(stems[0], stems[1], stems[2]))];

        Directory.CreateDirectory(folder);
        var written = new List<string>();
        for (int k = 0; k < outputs.Count; k++)
        {
            var (name, audio) = outputs[k];
            var path = OutputPath(folder, name, extension);
            await EncodeAsync(audio, length, path, input, options, safe.Slice(0.9 + 0.1 * k / outputs.Count, 0.1 / outputs.Count), cancellationToken).ConfigureAwait(false);
            written.Add(path);
        }
        safe.Report(1);
        return written;
    }

    private static string OutputPath(string folder, string name, string extension)
    {
        var path = Path.Combine(folder, $"{name}.{extension}");
        for (int n = 2; File.Exists(path); n++)
            path = Path.Combine(folder, $"{name} {n}.{extension}");
        return path;
    }

    private static float[][] Add(params float[][][] parts)
    {
        int length = parts[0][0].Length;
        var sum = new float[2][] { new float[length], new float[length] };
        foreach (var part in parts)
            for (int c = 0; c < 2; c++)
                for (int i = 0; i < length; i++)
                    sum[c][i] += part[c][i];
        return sum;
    }

    /// <summary>All four sources over the whole mix: overlapping pieces, blended.</summary>
    private static float[][][] Separate(string model, float[][] mix, int length, IProgress<double> progress, CancellationToken ct)
    {
        using var options = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL, IntraOpNumThreads = Environment.ProcessorCount };
        using var session = new InferenceSession(model, options);
        var outputs = new float[4][][];
        for (int s = 0; s < 4; s++)
            outputs[s] = [new float[length], new float[length]];
        var weights = new float[length];

        // Triangular weights, highest in the middle of each piece.
        var window = new float[Segment];
        for (int i = 0; i < Segment; i++)
            window[i] = Math.Min(i + 1, Segment - i) / (float)(Segment / 2);
        int stride = (int)(Segment * 0.75);
        var offsets = new List<int>();
        for (int offset = 0; offset < length; offset += stride)
            offsets.Add(offset);

        for (int k = 0; k < offsets.Count; k++)
        {
            ct.ThrowIfCancellationRequested();
            int offset = offsets[k], chunk = Math.Min(Segment, length - offset);
            // A short last piece is centred in real neighbouring sound (silence past the ends).
            int start = offset - (Segment - chunk) / 2;
            var piece = new float[2 * Segment];
            for (int c = 0; c < 2; c++)
                for (int i = 0; i < Segment; i++)
                {
                    int at = start + i;
                    piece[c * Segment + i] = at >= 0 && at < length ? mix[c][at] : 0;
                }
            var separated = Run(session, piece);
            int trim = (Segment - chunk) / 2;
            for (int s = 0; s < 4; s++)
                for (int c = 0; c < 2; c++)
                    for (int i = 0; i < chunk; i++)
                        outputs[s][c][offset + i] += window[i] * separated[s][c][trim + i];
            for (int i = 0; i < chunk; i++)
                weights[offset + i] += window[i];
            progress.Report((k + 1.0) / offsets.Count);
        }
        for (int s = 0; s < 4; s++)
            for (int c = 0; c < 2; c++)
                for (int i = 0; i < length; i++)
                    outputs[s][c][i] /= Math.Max(1e-8f, weights[i]);
        return outputs;
    }

    /// <summary>One 7.8 s stereo piece through the model: its sound and spectrogram in, four sources out.</summary>
    private static float[][][] Run(InferenceSession session, float[] piece)
    {
        var spectrogram = new float[4 * Bins * Frames];
        for (int c = 0; c < 2; c++)
        {
            var z = Spec(piece.AsSpan(c * Segment, Segment));
            for (int f = 0; f < Bins; f++)
                for (int t = 0; t < Frames; t++)
                {
                    var value = z[f * Frames + t];
                    spectrogram[((2 * c) * Bins + f) * Frames + t] = (float)value.Real;
                    spectrogram[((2 * c + 1) * Bins + f) * Frames + t] = (float)value.Imaginary;
                }
        }
        using var wave = OrtValue.CreateTensorValueFromMemory(piece, [1, 2, Segment]);
        using var spec = OrtValue.CreateTensorValueFromMemory(spectrogram, [1, 4, Bins, Frames]);
        using var run = new RunOptions();
        var names = session.InputMetadata.Keys.ToList();
        var values = names.Select(n => session.InputMetadata[n].Dimensions.Length == 3 ? wave : spec).ToList();
        var outputNames = session.OutputMetadata.Keys.ToList();
        using var results = session.Run(run, names, values, outputNames);
        int specIndex = outputNames.FindIndex(n => session.OutputMetadata[n].Dimensions.Length == 5);
        var freq = results[specIndex].GetTensorDataAsSpan<float>();
        var time = results[1 - specIndex].GetTensorDataAsSpan<float>();

        var sources = new float[4][][];
        var bins = new Complex[Bins * Frames];
        for (int s = 0; s < 4; s++)
        {
            sources[s] = new float[2][];
            for (int c = 0; c < 2; c++)
            {
                int re = ((s * 4 + 2 * c) * Bins) * Frames, im = ((s * 4 + 2 * c + 1) * Bins) * Frames;
                for (int j = 0; j < Bins * Frames; j++)
                    bins[j] = new Complex(freq[re + j], freq[im + j]);
                var wav = InverseSpec(bins);
                int timeAt = (s * 2 + c) * Segment;
                for (int i = 0; i < Segment; i++)
                    wav[i] += time[timeAt + i];
                sources[s][c] = wav;
            }
        }
        return sources;
    }

    // ---- Demucs's spectrogram: a Hann-windowed, normalized STFT, padded so frames line up with hops ----

    private static readonly float[] Hann = Enumerable.Range(0, Fft).Select(i => (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / Fft))).ToArray();

    /// <summary>Bins 0 to 2047 by 336 frames, as Demucs's _spec makes them (row-major, frequency first).</summary>
    internal static Complex[] Spec(ReadOnlySpan<float> x)
    {
        int le = (int)Math.Ceiling(x.Length / (double)Hop);
        int pad = Hop / 2 * 3;
        var padded = Reflect(x, pad, pad + le * Hop - x.Length);
        // torch.stft(center=True) reflects another half window on both sides.
        var centred = Reflect(padded, Fft / 2, Fft / 2);
        int frames = 1 + (centred.Length - Fft) / Hop;
        var result = new Complex[Bins * le];
        var buffer = new Complex[Fft];
        double scale = 1 / Math.Sqrt(Fft);
        // _spec drops the last bin and the first two and last two frames.
        for (int t = 0; t < le; t++)
        {
            int frame = t + 2;
            if (frame >= frames)
                break;
            for (int i = 0; i < Fft; i++)
                buffer[i] = new Complex(centred[frame * Hop + i] * Hann[i], 0);
            Transform(buffer, inverse: false);
            for (int f = 0; f < Bins; f++)
                result[f * le + t] = buffer[f] * scale;
        }
        return result;
    }

    /// <summary>The inverse of <see cref="Spec"/>, back to <see cref="Segment"/> samples (Demucs's _ispec).</summary>
    internal static float[] InverseSpec(Complex[] z)
    {
        int le = z.Length / Bins;
        int pad = Hop / 2 * 3;
        int frames = le + 4;
        int total = Fft + Hop * (frames - 1);
        var output = new double[total];
        var norm = new double[total];
        var buffer = new Complex[Fft];
        double scale = Math.Sqrt(Fft);
        for (int frame = 0; frame < frames; frame++)
        {
            int t = frame - 2;
            Array.Clear(buffer);
            if (t >= 0 && t < le)
            {
                // Bins 0..2047 given; bin 2048 (Nyquist) was dropped, so it is zero; the rest mirror.
                for (int f = 0; f < Bins; f++)
                    buffer[f] = z[f * le + t] * scale;
                for (int f = 1; f < Bins; f++)
                    buffer[Fft - f] = Complex.Conjugate(buffer[f]);
            }
            Transform(buffer, inverse: true);
            int at = frame * Hop;
            for (int i = 0; i < Fft; i++)
            {
                output[at + i] += buffer[i].Real / Fft * Hann[i];
                norm[at + i] += Hann[i] * Hann[i];
            }
        }
        // istft(center=True) removes the half window it added; _ispec then removes its own padding.
        var result = new float[le * Hop];
        int skip = Fft / 2 + pad;
        for (int i = 0; i < Math.Min(Segment, result.Length); i++)
        {
            int at = skip + i;
            result[i] = (float)(output[at] / Math.Max(1e-8, norm[at]));
        }
        return result[..Segment];
    }

    private static float[] Reflect(ReadOnlySpan<float> x, int left, int right)
    {
        var y = new float[x.Length + left + right];
        for (int i = 0; i < y.Length; i++)
        {
            int j = i - left;
            while (j < 0 || j >= x.Length)
                j = j < 0 ? -j : 2 * (x.Length - 1) - j;
            y[i] = x[j];
        }
        return y;
    }

    private static float[] Reflect(float[] x, int left, int right) => Reflect(x.AsSpan(), left, right);

    /// <summary>In-place radix-2 FFT (length a power of two); the inverse is unscaled.</summary>
    internal static void Transform(Complex[] data, bool inverse)
    {
        int n = data.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1)
                j ^= bit;
            j ^= bit;
            if (i < j)
                (data[i], data[j]) = (data[j], data[i]);
        }
        for (int size = 2; size <= n; size <<= 1)
        {
            double angle = 2 * Math.PI / size * (inverse ? 1 : -1);
            var step = new Complex(Math.Cos(angle), Math.Sin(angle));
            for (int start = 0; start < n; start += size)
            {
                var w = Complex.One;
                for (int k = 0; k < size / 2; k++)
                {
                    var even = data[start + k];
                    var odd = data[start + k + size / 2] * w;
                    data[start + k] = even + odd;
                    data[start + k + size / 2] = even - odd;
                    w *= step;
                }
            }
        }
    }

    private static async Task EncodeAsync(float[][] audio, int length, string path, string source, SplitStemsOptions options, IProgress<double> progress, CancellationToken ct)
    {
        using var scratch = new ScratchFolder();
        var raw = scratch.File("stem.f32");
        var interleaved = new float[2 * length];
        for (int i = 0; i < length; i++)
        {
            interleaved[2 * i] = audio[0][i];
            interleaved[2 * i + 1] = audio[1][i];
        }
        await File.WriteAllBytesAsync(raw, MemoryMarshal.AsBytes(interleaved.AsSpan()).ToArray(), ct).ConfigureAwait(false);
        var target = Containers.ForOutput(path, source);
        List<string> args = ["-f", "f32le", "-ar", Inv.I(Rate), "-ac", "2", "-i", raw, "-map", "0:a"];
        args.AddRange(Containers.AudioEncoderArgs(target.Audio, options.EffectiveSettings.AudioBitrateKbps, 16, false));
        await Ffmpeg.EncodeAsync(args, path, target.Muxer, length / (double)Rate, progress, ct).ConfigureAwait(false);
    }
}
