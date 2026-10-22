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

/// <summary>Turns sound from a sound card (any rate, any number of channels) into 16 kHz mono for speech models.</summary>
public sealed class SpeechResampler(int rate, int channels)
{
    private double _position;
    private float _last;

    /// <summary>Interleaved samples in; 16 kHz mono out, continuing smoothly from the call before.</summary>
    public float[] Convert(ReadOnlySpan<float> interleaved)
    {
        int frames = interleaved.Length / channels;
        if (frames == 0)
            return [];
        var mono = new float[frames];
        for (int f = 0; f < frames; f++)
        {
            float sum = 0;
            for (int c = 0; c < channels; c++)
                sum += interleaved[f * channels + c];
            mono[f] = sum / channels;
        }
        double step = (double)rate / SpeechChunker.Rate;
        var output = new List<float>((int)(frames / step) + 2);
        // _position is where the next output sample falls, counting the previous call's last sample as -1.
        while (_position < frames - 1)
        {
            int i = (int)Math.Floor(_position);
            double t = _position - i;
            float a = i < 0 ? _last : mono[i];
            float b = mono[i + 1];
            output.Add((float)(a + (b - a) * t));
            _position += step;
        }
        _position -= frames;
        _last = mono[^1];
        return [.. output];
    }
}
