using Whisper.net;

namespace Petal.Engines.Media;

/// <summary>What a piece of speech said, and the language it was said in ("en", "ar"…).</summary>
public sealed record LiveText(string Text, string Language);

/// <summary>
/// Whisper kept loaded for a stream of short pieces of speech (captions, meeting notes), so each piece
/// is written down in a moment instead of loading the model again. On the graphics card when it can.
/// </summary>
public sealed class LiveTranscriber : IDisposable
{
    private readonly WhisperFactory _factory;
    private readonly string _language;
    private readonly SemaphoreSlim _one = new(1, 1);

    /// <param name="language">"auto" to tell, or a code such as "ar".</param>
    public LiveTranscriber(string modelPath, string language = "auto")
    {
        if (!File.Exists(modelPath))
            throw new FileNotFoundException("The speech model isn't downloaded yet.", modelPath);
        _factory = WhisperFactory.FromPath(modelPath);
        _language = string.IsNullOrEmpty(language) ? "auto" : language;
    }

    /// <summary>The words in 16 kHz mono samples; with <paramref name="intoEnglish"/>, their English translation.</summary>
    public async Task<LiveText> TranscribeAsync(float[] samples, bool intoEnglish = false, CancellationToken cancellationToken = default)
    {
        await _one.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var builder = _factory.CreateBuilder().WithLanguage(_language).WithNoContext();
            if (intoEnglish)
                builder = builder.WithTranslate();
            await using var processor = builder.Build();
            var parts = new List<string>();
            string language = _language;
            await foreach (var segment in processor.ProcessAsync(samples, cancellationToken).ConfigureAwait(false))
            {
                var text = segment.Text.Trim();
                if (text.Length > 0 && !IsNoise(text))
                    parts.Add(text);
                if (!string.IsNullOrEmpty(segment.Language))
                    language = segment.Language;
            }
            return new LiveText(string.Join(" ", parts), intoEnglish ? "en" : language);
        }
        finally
        {
            _one.Release();
        }
    }

    /// <summary>What Whisper writes for silence or noise: "[BLANK_AUDIO]", "(music)", "♪".</summary>
    public static bool IsNoise(string text) =>
        text.StartsWith('[') && text.EndsWith(']') || text.StartsWith('(') && text.EndsWith(')') || text.All(c => c is '♪' or ' ' or '.' or '…');

    public void Dispose()
    {
        _factory.Dispose();
        _one.Dispose();
    }
}
