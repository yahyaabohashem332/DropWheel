using System.Numerics;
using Microsoft.ML.OnnxRuntime;

namespace Petal.Engines.Media;

/// <summary>
/// Takes the music out of a soundtrack and keeps its other sounds (things moving, steps, rain, wind, the hum of a room):
/// TIGER (Xu, Li, Chen and Hu, Tsinghua University, Apache-2.0), trained on Divide and Remaster to split film sound into
/// dialogue, music and effects, run on this PC through ONNX Runtime. The model hears 12 seconds of one channel at a time
/// as a spectrogram (2048-point, every 512 samples, at 44.1 kHz). It hears the middle of the stereo (both sides together);
/// what it keeps of each moment and pitch is then kept of each side, so the sounds stay where they were. Each point is kept
/// by the share of its power the model gives to effects, raised to the fourth power: on a mix of speech, rock music, rain
/// and footsteps (speech already taken out) that leaves the music 20 dB down and the other sounds within half a decibel,
/// where keeping the effects' share as it is left the music only 13 dB down. Pieces overlap by two seconds and are blended
/// across them; a few are heard at once when the PC has the memory (each takes over 2 GB).
/// </summary>
public static class MusicRemover
{
    public const int Rate = 44100;
    private const int Fft = 2048;
    private const int Hop = 512;
    private const int Bins = Fft / 2 + 1;
    private const int Segment = 12 * Rate;
    private const int Overlap = 2 * Rate;
    private const int Step = Segment - Overlap;
    private const int Frames = Segment / Hop + 1;

    private static readonly double[] Hann = Enumerable.Range(0, Fft).Select(i => 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / Fft)).ToArray();

    /// <summary>
    /// How many pieces are heard at once: the model's many small steps keep two threads each busier than one piece spread
    /// over them all, but each piece takes over 2 GB, so only as many as the memory beyond 10 GB holds.
    /// </summary>
    private static int Together
    {
        get
        {
            double gigabytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1e9;
            return (int)Math.Clamp(Math.Min(Environment.ProcessorCount / 2, (gigabytes - 10) / 2.5), 1, 4);
        }
    }

    /// <summary>
    /// The sounds of a stereo (or mono) soundtrack at 44.1 kHz without its music or its speech. <paramref name="model"/> is
    /// the path of tiger.int8.onnx.
    /// </summary>
    public static float[][] Sounds(string model, IReadOnlyList<float[]> channels, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        int length = channels[0].Length;
        var middle = new float[length];
        for (int c = 0; c < channels.Count; c++)
            for (int i = 0; i < length; i++)
                middle[i] += channels[c][i] / channels.Count;

        int together = Together;
        // Memory given back after each step instead of kept for the next run: a third of the peak.
        using var options = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL, IntraOpNumThreads = Math.Max(1, Environment.ProcessorCount / together),
            EnableCpuMemArena = false, EnableMemoryPattern = false };
        using var session = new InferenceSession(model, options);
        // A second of quiet before and after, so the start and the end are heard with something either side.
        int pad = Rate;
        int pieces = Math.Max(1, (int)Math.Ceiling((length + 2.0 * pad - Segment) / Step) + 1);
        var sums = channels.Select(_ => new double[(pieces - 1) * Step + Segment]).ToArray();
        var weights = new double[(pieces - 1) * Step + Segment];
        var gate = new Lock();
        int done = 0;
        Parallel.For(0, pieces, new ParallelOptions { MaxDegreeOfParallelism = together, CancellationToken = cancellationToken }, k =>
        {
            int from = k * Step - pad;
            var mask = Mask(session, Piece(middle, from));
            var heard = channels.Select(channel => Apply(Piece(channel, from), mask)).ToArray();
            lock (gate)
            {
                for (int i = 0; i < Segment; i++)
                {
                    // Faded in and out across the overlaps (the first and last pieces' outer edges fall in the padding).
                    double w = Math.Min(1, Math.Min((i + 1.0) / Overlap, (Segment - i) / (double)Overlap));
                    weights[k * Step + i] += w;
                    for (int c = 0; c < heard.Length; c++)
                        sums[c][k * Step + i] += heard[c][i] * w;
                }
                progress?.Report(++done / (double)pieces);
            }
        });
        var result = new float[channels.Count][];
        for (int c = 0; c < channels.Count; c++)
        {
            var channel = result[c] = new float[length];
            for (int i = 0; i < length; i++)
                channel[i] = weights[pad + i] > 0 ? (float)(sums[c][pad + i] / weights[pad + i]) : 0;
        }
        return result;
    }

    /// <summary>12 seconds of a channel from <paramref name="from"/>, quiet where it runs past either end.</summary>
    private static float[] Piece(float[] channel, int from)
    {
        var piece = new float[Segment];
        for (int i = Math.Max(0, -from); i < Segment && from + i < channel.Length; i++)
            piece[i] = channel[from + i];
        return piece;
    }

    /// <summary>For each pitch and moment of a piece, how much of it is kept: the effects' share of its power, to the fourth.</summary>
    private static float[] Mask(InferenceSession session, float[] piece)
    {
        var mix = Spectrum(piece);
        var spec = new float[2 * Bins * Frames];
        for (int i = 0; i < mix.Length; i++)
        {
            spec[i] = (float)mix[i].Real;
            spec[Bins * Frames + i] = (float)mix[i].Imaginary;
        }
        using var input = OrtValue.CreateTensorValueFromMemory(spec, [1, 2, Bins, Frames]);
        using var run = new RunOptions();
        using var outputs = session.Run(run, ["mix_spec"], [input], ["stems_spec"]);
        var stems = outputs[0].GetTensorDataAsSpan<float>();
        // The model's stems, each as its real and its imaginary part: dialogue, music, effects.
        var mask = new float[Bins * Frames];
        int n = mask.Length;
        for (int i = 0; i < n; i++)
        {
            double dialogue = stems[i] * (double)stems[i] + stems[n + i] * (double)stems[n + i];
            double music = stems[2 * n + i] * (double)stems[2 * n + i] + stems[3 * n + i] * (double)stems[3 * n + i];
            double effects = stems[4 * n + i] * (double)stems[4 * n + i] + stems[5 * n + i] * (double)stems[5 * n + i];
            double share = effects / (dialogue + music + effects + 1e-12);
            share *= share;
            mask[i] = (float)(share * share);
        }
        return mask;
    }

    /// <summary>A piece with only what the mask keeps of it.</summary>
    private static float[] Apply(float[] piece, float[] mask)
    {
        var spectrum = Spectrum(piece);
        for (int i = 0; i < spectrum.Length; i++)
            spectrum[i] *= mask[i];
        return Sound(spectrum);
    }

    /// <summary>
    /// torch.stft(center=True) as the model was trained on: half a window reflected on both sides, Hann, one-sided, not
    /// normalized; frequency-major ([bin, frame]).
    /// </summary>
    private static Complex[] Spectrum(float[] piece)
    {
        var centred = new float[Segment + Fft];
        for (int i = 0; i < centred.Length; i++)
        {
            int j = i - Fft / 2;
            j = j < 0 ? -j : j >= Segment ? 2 * (Segment - 1) - j : j;
            centred[i] = piece[j];
        }
        var spectrum = new Complex[Bins * Frames];
        var buffer = new Complex[Fft];
        for (int t = 0; t < Frames; t++)
        {
            for (int i = 0; i < Fft; i++)
                buffer[i] = new Complex(centred[t * Hop + i] * Hann[i], 0);
            StemSplitter.Transform(buffer, inverse: false);
            for (int f = 0; f < Bins; f++)
                spectrum[f * Frames + t] = buffer[f];
        }
        return spectrum;
    }

    /// <summary>torch.istft: each frame back, windowed and overlapped, over the windows' sum, the centring dropped.</summary>
    private static float[] Sound(Complex[] spectrum)
    {
        var sum = new double[Segment + Fft];
        var weight = new double[Segment + Fft];
        var buffer = new Complex[Fft];
        for (int t = 0; t < Frames; t++)
        {
            for (int f = 0; f < Bins; f++)
                buffer[f] = spectrum[f * Frames + t];
            for (int f = 1; f < Bins - 1; f++)
                buffer[Fft - f] = Complex.Conjugate(buffer[f]);
            // The ends of a real signal's spectrum are real.
            buffer[0] = new Complex(buffer[0].Real, 0);
            buffer[Bins - 1] = new Complex(buffer[Bins - 1].Real, 0);
            StemSplitter.Transform(buffer, inverse: true);
            int at = t * Hop;
            for (int i = 0; i < Fft && at + i < sum.Length; i++)
            {
                sum[at + i] += buffer[i].Real / Fft * Hann[i];
                weight[at + i] += Hann[i] * Hann[i];
            }
        }
        var result = new float[Segment];
        for (int i = 0; i < Segment; i++)
        {
            int at = i + Fft / 2;
            result[i] = weight[at] > 1e-11 ? (float)(sum[at] / weight[at]) : 0;
        }
        return result;
    }
}
