using System.IO;
using NAudio.Wave;
using Petal.App.Settings;
using Petal.Core.Models;
using Petal.Engines.Media;

namespace Petal.App.Words;

/// <summary>
/// Listens to the microphone and writes down what was said, with Whisper on this PC: for dictation and
/// for asking Petal out loud. Nothing is sent anywhere.
/// </summary>
internal sealed class Listener : IDisposable
{
    public static readonly ModelInfo[] Models = [ModelCatalog.WhisperFast, ModelCatalog.WhisperBalanced, ModelCatalog.WhisperAccurate];

    private readonly string _file = Path.Combine(Path.GetTempPath(), "Petal", $"listen-{Guid.NewGuid():N}.wav");
    private WaveInEvent? _input;
    private WaveFileWriter? _writer;
    private readonly object _lock = new();
    private TaskCompletionSource? _stopped;

    /// <summary>How loud the last moment was, 0 to 1, for a level meter.</summary>
    public event Action<double>? Level;

    /// <summary>The model chosen in the settings ("Fast", "Balanced" or "Accurate").</summary>
    public static ModelInfo ChosenModel => SettingsStore.Current.DictationAccuracy switch
    {
        "Fast" => ModelCatalog.WhisperFast,
        "Accurate" => ModelCatalog.WhisperAccurate,
        _ => ModelCatalog.WhisperBalanced,
    };

    /// <summary>The model to use: the one chosen, or a more accurate one already here.</summary>
    public static ModelInfo? ReadyModel() =>
        ModelStore.IsAvailable(ChosenModel) ? ChosenModel : Models.Reverse().FirstOrDefault(ModelStore.IsAvailable);

    public static bool HasMicrophone
    {
        get
        {
            try { return WaveInEvent.DeviceCount > 0; }
            catch (NAudio.MmException) { return false; }
        }
    }

    public TimeSpan Length { get; private set; }

    public void Start()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
        _input = new WaveInEvent { WaveFormat = new WaveFormat(16000, 16, 1), BufferMilliseconds = 60 };
        _writer = new WaveFileWriter(_file, _input.WaveFormat);
        _stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _input.DataAvailable += (_, e) =>
        {
            lock (_lock)
            {
                if (_writer is null)
                    return;
                _writer.Write(e.Buffer, 0, e.BytesRecorded);
                Length = TimeSpan.FromSeconds(_writer.Length / 32000.0);
            }
            int peak = 0;
            for (int i = 0; i + 1 < e.BytesRecorded; i += 2)
                peak = Math.Max(peak, Math.Abs((int)BitConverter.ToInt16(e.Buffer, i)));
            Level?.Invoke(Math.Min(1, peak / 20000.0));
        };
        _input.RecordingStopped += (_, _) => _stopped.TrySetResult();
        _input.StartRecording();
    }

    /// <summary>Stops listening and returns what was said ("" for silence).</summary>
    public async Task<string> FinishAsync(CancellationToken cancellationToken = default)
    {
        await StopAsync();
        if (Length < TimeSpan.FromSeconds(0.6))
            return "";
        var model = ReadyModel() ?? throw new FileNotFoundException("The speech model isn't downloaded yet.");
        var language = SettingsStore.Current.DictationLanguage is { Length: > 0 } l ? l : "auto";
        var transcript = await Transcriber.TranscribeAsync(_file, new TranscribeOptions(ModelStore.Find(model)!, language), null, cancellationToken);
        var text = string.Join(" ", transcript.Segments.Select(s => s.Text.Trim()).Where(t => t.Length > 0 && !IsNoise(t)));
        return text.Trim();
    }

    /// <summary>What Whisper writes for silence or noise: "[BLANK_AUDIO]", "(music)", "♪".</summary>
    internal static bool IsNoise(string text) =>
        text.StartsWith('[') && text.EndsWith(']') || text.StartsWith('(') && text.EndsWith(')') || text.All(c => c is '♪' or ' ' or '.');

    private async Task StopAsync()
    {
        if (_input is null)
            return;
        _input.StopRecording();
        if (_stopped is not null)
            await Task.WhenAny(_stopped.Task, Task.Delay(1500));
        lock (_lock)
        {
            _writer?.Dispose();
            _writer = null;
        }
        _input.Dispose();
        _input = null;
    }

    public void Dispose()
    {
        try
        {
            _input?.StopRecording();
            _input?.Dispose();
        }
        catch (NAudio.MmException)
        {
        }
        lock (_lock)
        {
            _writer?.Dispose();
            _writer = null;
        }
        try { File.Delete(_file); } catch (IOException) { }
    }
}
