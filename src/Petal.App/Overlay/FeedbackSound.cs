using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Petal.App.Overlay;

/// <summary>
/// Soft "tick" when a petal is highlighted and a deeper "pop" on drop. Sounds are synthesised at
/// start-up and mixed into a low-latency output that only runs while the wheel is open.
/// </summary>
public sealed class FeedbackSound : IDisposable
{
    private const int SampleRate = 48000;
    private static readonly WaveFormat Format = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, 2);

    private readonly float[] _tick = Synthesize(frequency: 2900, durationMs: 26, decay: 230, gain: 0.16f, noise: 0.35f);
    private readonly float[] _drop = Synthesize(frequency: 880, durationMs: 70, decay: 60, gain: 0.22f, noise: 0.1f, sweep: -0.35f);
    private readonly Lock _gate = new();
    private MixingSampleProvider? _mixer;
    private IWavePlayer? _output;
    private Timer? _idleTimer;

    public bool Enabled { get; set; } = true;

    /// <summary>Opens the audio device ahead of time so the first tick plays without delay.</summary>
    public void Warm()
    {
        if (!Enabled) return;
        lock (_gate)
        {
            _idleTimer?.Dispose();
            _idleTimer = null;
            if (_output is not null) return;
            try
            {
                _mixer = new MixingSampleProvider(Format) { ReadFully = true };
#pragma warning disable CS0618
                var output = new WasapiOut(AudioClientShareMode.Shared, useEventSync: true, latency: 30);
#pragma warning restore CS0618
                output.Init(_mixer);
                output.Play();
                _output = output;
            }
            catch
            {
                // No audio device: stay silent.
                _output = null;
                _mixer = null;
            }
        }
    }

    /// <summary>Releases the audio device a little after the wheel closes.</summary>
    public void Cool()
    {
        lock (_gate)
        {
            _idleTimer?.Dispose();
            _idleTimer = new Timer(_ => Release(), null, TimeSpan.FromSeconds(3), Timeout.InfiniteTimeSpan);
        }
    }

    public void Tick() => Play(_tick);

    public void Drop() => Play(_drop);

    private void Play(float[] samples)
    {
        if (!Enabled) return;
        Warm();
        lock (_gate)
            _mixer?.AddMixerInput(new BufferProvider(samples));
    }

    private void Release()
    {
        lock (_gate)
        {
            _output?.Stop();
            _output?.Dispose();
            _output = null;
            _mixer = null;
        }
    }

    private static float[] Synthesize(double frequency, int durationMs, double decay, float gain, float noise, float sweep = 0)
    {
        int n = SampleRate * durationMs / 1000;
        var data = new float[n * 2];
        var rng = new Random(7);
        double phase = 0;
        double lastNoise = 0;
        for (int i = 0; i < n; i++)
        {
            double t = (double)i / SampleRate;
            double env = Math.Exp(-t * decay) * Math.Min(1, i / (SampleRate * 0.0008));
            double f = frequency * (1 + sweep * t * 1000 / durationMs);
            phase += 2 * Math.PI * f / SampleRate;
            // Low-passed noise gives the click a little texture.
            lastNoise = lastNoise * 0.6 + (rng.NextDouble() * 2 - 1) * 0.4;
            float v = (float)((Math.Sin(phase) * (1 - noise) + lastNoise * noise) * env * gain);
            data[i * 2] = v;
            data[i * 2 + 1] = v;
        }
        return data;
    }

    public void Dispose()
    {
        _idleTimer?.Dispose();
        Release();
    }

    private sealed class BufferProvider(float[] data) : ISampleProvider
    {
        private int _position;

        public WaveFormat WaveFormat => Format;

        public int Read(Span<float> buffer)
        {
            int available = Math.Min(buffer.Length, data.Length - _position);
            data.AsSpan(_position, available).CopyTo(buffer);
            _position += available;
            return available;
        }
    }
}
