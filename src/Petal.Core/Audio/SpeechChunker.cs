namespace Petal.Core.Audio;

/// <summary>
/// Cuts a live stream of sound (16 kHz mono) into pieces of speech for a speech model: a piece ends at a
/// pause once there is enough of it, or when it grows too long, so captions keep up with the talk. The
/// quiet level is learnt as it goes, so a noisy room or soft music doesn't count as speech.
/// </summary>
public sealed class SpeechChunker
{
    public const int Rate = 16000;
    private const int Window = Rate * 30 / 1000;
    private readonly double _shortest, _longest, _pause;
    private readonly List<float> _piece = [];
    private readonly Queue<float[]> _before = new();
    private readonly List<float> _window = [];
    private double _noise = 0.004;
    private int _quietWindows, _loudWindows;
    private bool _speaking;

    /// <param name="shortest">Seconds a piece needs before a pause may end it.</param>
    /// <param name="longest">Seconds after which a piece ends anyway.</param>
    /// <param name="pause">Seconds of quiet that end a piece.</param>
    public SpeechChunker(double shortest = 1.2, double longest = 7, double pause = 0.55)
    {
        _shortest = shortest;
        _longest = longest;
        _pause = pause;
    }

    /// <summary>Adds sound; returns the pieces of speech it completed (often none).</summary>
    public IReadOnlyList<float[]> Add(ReadOnlySpan<float> samples)
    {
        var done = new List<float[]>();
        foreach (var s in samples)
        {
            _window.Add(s);
            if (_window.Count < Window)
                continue;
            var window = _window.ToArray();
            _window.Clear();
            double rms = Math.Sqrt(window.Sum(v => (double)v * v) / window.Length);
            bool loud = rms > Math.Max(0.006, _noise * 3);
            if (!loud)
                _noise = _noise * 0.97 + rms * 0.03;
            if (!_speaking)
            {
                // A little of what came just before, so the first sound isn't cut.
                _before.Enqueue(window);
                while (_before.Count > 8)
                    _before.Dequeue();
                if (loud)
                {
                    _speaking = true;
                    _quietWindows = 0;
                    _loudWindows = 1;
                    foreach (var w in _before)
                        _piece.AddRange(w);
                    _before.Clear();
                }
                continue;
            }
            _piece.AddRange(window);
            _quietWindows = loud ? 0 : _quietWindows + 1;
            if (loud)
                _loudWindows++;
            double length = (double)_piece.Count / Rate, quiet = _quietWindows * Window / (double)Rate;
            bool enoughSpeech = _loudWindows * Window >= Rate * 0.3;
            if (length >= _longest || (quiet >= _pause && length >= _shortest && enoughSpeech))
            {
                if (enoughSpeech)
                    done.Add([.. _piece]);
                _piece.Clear();
                _speaking = quiet < _pause && length >= _longest;
                _quietWindows = 0;
                _loudWindows = 0;
            }
            else if (quiet >= _pause * 2 && length < _shortest)
            {
                // A short sound and then quiet again: a cough, a click. Let it go.
                _piece.Clear();
                _speaking = false;
            }
        }
        return done;
    }

    /// <summary>Whatever speech is waiting, at the end.</summary>
    public float[]? Flush()
    {
        if (!_speaking || _piece.Count < Rate / 2)
            return null;
        var rest = _piece.ToArray();
        _piece.Clear();
        _speaking = false;
        return rest;
    }
}

/// <summary>
/// Turns sound from a sound card (any rate, any number of channels) into 16 kHz mono for speech models. What 16 kHz
/// can't carry is taken out first (a windowed-sinc filter): left in, the hiss of an "s" and the top of music fold back
/// down among the sounds speech is told by, and a speech model hears them as part of the words.
/// </summary>
public sealed class SpeechResampler
{
    private const int Phases = 128;
    private readonly int _channels;
    private readonly double _step;
    // The filter reaches this many samples of the incoming sound either side of each one it makes.
    private readonly int _half;
    private readonly float[][] _kernels;
    // The last samples of the call before, so the sound continues smoothly, and where the next sample to make falls
    // in them and what follows.
    private float[] _history;
    private double _next;

    public SpeechResampler(int rate, int channels)
    {
        _channels = Math.Max(1, channels);
        _step = (double)rate / SpeechChunker.Rate;
        // Everything above this share of the incoming rate goes: a little under half of the lower of the two rates.
        double cutoff = 0.5 * Math.Min(1, 1 / _step) * 0.92;
        _half = (int)Math.Ceiling(8 / (2 * cutoff));
        _kernels = new float[Phases][];
        for (int p = 0; p < Phases; p++)
        {
            var kernel = new float[_half * 2];
            double sum = 0;
            for (int k = 0; k < kernel.Length; k++)
            {
                double t = k - _half + 1 - (double)p / Phases;
                double x = 2 * cutoff * t;
                double sinc = Math.Abs(x) < 1e-9 ? 1 : Math.Sin(Math.PI * x) / (Math.PI * x);
                double window = Math.Abs(t) >= _half ? 0 : 0.5 + 0.5 * Math.Cos(Math.PI * t / _half);
                kernel[k] = (float)(sinc * window);
                sum += kernel[k];
            }
            for (int k = 0; k < kernel.Length; k++)
                kernel[k] = (float)(kernel[k] / sum);
            _kernels[p] = kernel;
        }
        _history = new float[_half * 2];
        _next = _history.Length;
    }

    /// <summary>Interleaved samples in; 16 kHz mono out, continuing smoothly from the call before.</summary>
    public float[] Convert(ReadOnlySpan<float> interleaved)
    {
        int frames = interleaved.Length / _channels;
        if (frames == 0)
            return [];
        var sound = new float[_history.Length + frames];
        _history.CopyTo(sound, 0);
        for (int f = 0; f < frames; f++)
        {
            float sum = 0;
            for (int c = 0; c < _channels; c++)
                sum += interleaved[f * _channels + c];
            sound[_history.Length + f] = sum / _channels;
        }
        var output = new float[(int)Math.Max(0, (sound.Length - _half - _next) / _step) + 2];
        int made = 0;
        while (true)
        {
            int centre = (int)Math.Floor(_next);
            int phase = (int)Math.Round((_next - centre) * Phases);
            if (phase == Phases)
            {
                centre++;
                phase = 0;
            }
            if (centre + _half >= sound.Length)
                break;
            var kernel = _kernels[phase];
            int from = centre - _half + 1;
            float value = 0;
            for (int k = 0; k < kernel.Length; k++)
                value += sound[from + k] * kernel[k];
            if (made == output.Length)
                Array.Resize(ref output, output.Length * 2);
            output[made++] = value;
            _next += _step;
        }
        int keep = _history.Length;
        Array.Copy(sound, sound.Length - keep, _history, 0, keep);
        _next -= sound.Length - keep;
        return made == output.Length ? output : output[..made];
    }
}
