using System.Diagnostics;
using System.Text;
using Whisper.net;

namespace Petal.Engines.Media;

/// <summary>A word Whisper heard, with when it was said in the stream (seconds since listening began).</summary>
public sealed record StreamWord(string Text, double Start, double End)
{
    /// <summary>The word as compared between passes: lower case, without punctuation around it.</summary>
    internal string Key { get; } = Normalize(Text);

    public static string Normalize(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text.Trim())
            if (char.IsLetterOrDigit(c))
                sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }
}

/// <summary>A sentence that is final: its words, the language, and when it was said.</summary>
public sealed record StreamSentence(string Text, string Language, double Start, double End);

/// <summary>
/// What the captions show now: the sentence before, the final words of the sentence being said, the newest words
/// that may still change, and when the newest was said (seconds since listening began; -1 when nothing waits).
/// </summary>
public sealed record StreamState(string Previous, string Current, string Draft, string Language, double Newest);

/// <summary>
/// Near-live speech to text with Whisper, for captions. Instead of waiting for a pause, the sound since the last
/// finished sentence is written down again about every half second; the words two passes in a row agree on are
/// final ("local agreement", as in Macháček et al., Whisper-Streaming), the newest are a draft shown at once.
/// When a sentence is final its sound is cut off at its last word's end, so each pass stays short and quick. A
/// pass never retries at a higher temperature and has a time limit; when the PC falls behind, the window is cut
/// rather than left to grow, so the captions stay live instead of falling behind or stopping.
/// </summary>
public sealed class StreamingTranscriber : IDisposable
{
    public const int Rate = 16000;

    private readonly WhisperFactory _factory;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _model = new(1, 1);
    private readonly bool _autoLanguage;
    private readonly bool _aligned;
    private readonly bool _canTranslate;
    private readonly string? _translatingPath;
    private WhisperFactory? _translating;
    private string _language;
    private int _sameLanguage;

    // The open window: the sound since the last cut. _windowStart is where it starts in the stream, in samples.
    private float[] _window = new float[Rate * 40];
    private int _windowCount;
    private long _windowStart;
    private long _received;
    // The last few minutes of sound, for translating a finished sentence from its sound.
    private readonly float[] _archive = new float[Rate * 120];
    private long _archived;

    private List<StreamWord> _previous = [];
    private readonly List<StreamWord> _finalInWindow = [];
    private readonly List<StreamWord> _sentence = [];
    private readonly List<string> _recent = [];
    private double _noise = 0.004;
    private DateTime _lastSound = DateTime.UtcNow;

    /// <param name="modelPath">A Whisper model (ggml).</param>
    /// <param name="language">"auto" to tell (it is then kept once it is clear), or a code such as "en".</param>
    /// <param name="alignWords">Word times aligned to the sound (DTW) when the model's alignment heads are known.</param>
    /// <param name="translatingModelPath">
    /// A model that can translate, for <see cref="ToEnglishAsync"/> when this one can't (large-v3 turbo); loaded the
    /// first time it is needed.
    /// </param>
    public StreamingTranscriber(string modelPath, string language = "auto", bool alignWords = true, string? translatingModelPath = null)
    {
        if (!File.Exists(modelPath))
            throw new FileNotFoundException("The speech model isn't downloaded yet.", modelPath);
        // Word times aligned to the sound (DTW) where the model's alignment heads are known: the window is cut at a
        // finished sentence's last word, and a cut in the wrong place would lose or repeat words.
        var heads = alignWords ? Heads(Path.GetFileName(modelPath)) : null;
        _factory = heads is { } preset
            ? WhisperFactory.FromPath(modelPath, new WhisperFactoryOptions { UseDtwTimeStamps = true, HeadsPreset = preset })
            : WhisperFactory.FromPath(modelPath);
        _aligned = heads is not null;
        _canTranslate = Transcriber.CanTranslate(modelPath);
        _translatingPath = translatingModelPath;
        _autoLanguage = string.IsNullOrEmpty(language) || language == "auto";
        _language = _autoLanguage ? "auto" : language;
    }

    /// <summary>The alignment heads for a model file of whisper.cpp ("ggml-small-q5_1.bin"), or null when unknown.</summary>
    internal static WhisperAlignmentHeadsPreset? Heads(string file)
    {
        var name = file.ToLowerInvariant();
        bool english = name.Contains(".en");
        return name switch
        {
            _ when name.Contains("large-v3-turbo") => WhisperAlignmentHeadsPreset.LargeV3Turbo,
            _ when name.Contains("large-v3") => WhisperAlignmentHeadsPreset.LargeV3,
            _ when name.Contains("large-v2") => WhisperAlignmentHeadsPreset.LargeV2,
            _ when name.Contains("medium") => english ? WhisperAlignmentHeadsPreset.MediumEn : WhisperAlignmentHeadsPreset.Medium,
            _ when name.Contains("small") => english ? WhisperAlignmentHeadsPreset.SmallEn : WhisperAlignmentHeadsPreset.Small,
            _ when name.Contains("base") => english ? WhisperAlignmentHeadsPreset.BaseEn : WhisperAlignmentHeadsPreset.Base,
            _ when name.Contains("tiny") => english ? WhisperAlignmentHeadsPreset.TinyEn : WhisperAlignmentHeadsPreset.Tiny,
            _ => null,
        };
    }

    /// <summary>Seconds of new sound between passes.</summary>
    public double Step { get; init; } = 0.5;

    /// <summary>The longest window, in seconds, before it is cut whether a sentence ended or not.</summary>
    public double LongestWindow { get; init; } = 12;

    /// <summary>The draft or final words changed.</summary>
    public event Action<StreamState>? Changed;

    /// <summary>A pass went wrong (listening goes on).</summary>
    public event Action<Exception>? Failed;

    /// <summary>A sentence became final (for translating it).</summary>
    public event Action<StreamSentence>? Sentence;

    /// <summary>The library Whisper runs on (the processor, or the graphics card).</summary>
    public string Runtime => Whisper.net.LibraryLoader.RuntimeOptions.LoadedLibrary?.ToString() ?? "?";

    /// <summary>How long the last pass took for the sound it read (below 1 keeps up; the smaller, the quicker the words).</summary>
    public double Speed { get; private set; }

    /// <summary>How long the last pass took, in seconds: passes follow one another, so about how far the words are behind.</summary>
    public double LastPass { get; private set; }

    /// <summary>The language being heard ("en"), once known.</summary>
    public string Language => _language;

    /// <summary>Adds sound: 16 kHz mono samples from -1 to 1. Any thread.</summary>
    public void Add(ReadOnlySpan<float> samples)
    {
        if (samples.IsEmpty)
            return;
        lock (_gate)
        {
            if (_windowCount + samples.Length > _window.Length)
                Array.Resize(ref _window, Math.Max(_window.Length * 2, _windowCount + samples.Length));
            samples.CopyTo(_window.AsSpan(_windowCount));
            _windowCount += samples.Length;
            _received += samples.Length;
            foreach (var s in samples)
                _archive[_archived++ % _archive.Length] = s;
        }
    }

    /// <summary>Listens until cancelled: a pass whenever there is a step of new sound, the draft finished when it goes quiet.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        long lastPass = 0;
        int failures = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                long received;
                lock (_gate)
                    received = _received;
                if (received - lastPass < Step * Rate)
                {
                    // The PC's sound sends nothing while it is silent: what is waiting is finished by the clock.
                    if (DateTime.UtcNow - _lastSound > TimeSpan.FromSeconds(1.2) && (_previous.Count > 0 || _sentence.Count > 0))
                        FinishAll();
                    try { await Task.Delay(30, cancellationToken).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
                    continue;
                }
                lastPass = received;
                float[] window;
                long windowStart;
                lock (_gate)
                {
                    window = _window.AsSpan(0, _windowCount).ToArray();
                    windowStart = _windowStart;
                }
                double seconds = (double)window.Length / Rate;
                bool loud = Loud(window.AsSpan(Math.Max(0, window.Length - (int)(Step * Rate))));
                if (loud)
                    _lastSound = DateTime.UtcNow;
                if (!loud && !Loud(window))
                {
                    // Nothing said in the whole window: what was waiting is final, and the quiet is let go.
                    FinishAll();
                    CutAt(windowStart + window.Length);
                    continue;
                }
                if (seconds < 0.6)
                    continue;
                var timer = Stopwatch.StartNew();
                List<StreamWord>? heard;
                try
                {
                    heard = await TranscribeAsync(window, windowStart, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    heard = null;
                }
                Speed = timer.Elapsed.TotalSeconds / Math.Max(Step, seconds);
                LastPass = timer.Elapsed.TotalSeconds;
                if (heard is null)
                {
                    // Too slow (or stuck): keep what was said, start afresh from now rather than fall behind.
                    if (++failures >= 2)
                    {
                        CatchUp(windowStart + window.Length);
                        failures = 0;
                    }
                    continue;
                }
                failures = 0;
                Agree(heard, windowStart, loud);
                // Too long a window, or behind the talk (a pass took longer than the sound it read): what was heard
                // becomes final and the window starts after it, so the next pass is short again. A slow pass alone
                // isn't behind: on a processor most of a pass is Whisper's fixed 30 seconds, whatever the window.
                if (seconds > LongestWindow || timer.Elapsed.TotalSeconds > seconds && seconds > LongestWindow / 2)
                    CatchUp(windowStart + window.Length);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // A pass that went wrong never ends the captions: what was heard is kept, and listening goes on.
                Failed?.Invoke(e);
                _previous = [];
                long end;
                lock (_gate)
                {
                    lastPass = _received;
                    end = _windowStart + _windowCount;
                }
                CutAt(end);
            }
        }
    }

    /// <summary>
    /// What was heard becomes final and the window starts after its last word, so the next pass is short again.
    /// The sound after that word stays (cutting at the window's end would lose the words not written yet).
    /// </summary>
    private void CatchUp(long windowEnd)
    {
        FinishAll();
        var last = _finalInWindow.LastOrDefault();
        CutAt(last is null ? windowEnd : Math.Min(windowEnd, (long)(last.End * Rate)));
    }

    private async Task<List<StreamWord>?> TranscribeAsync(float[] window, long windowStart, CancellationToken cancellationToken)
    {
        double seconds = (double)window.Length / Rate;
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(TimeSpan.FromSeconds(Math.Max(3, seconds * 1.5)));
        await _model.WaitAsync(limit.Token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var builder = _factory.CreateBuilder()
                .WithLanguage(_language)
                .WithTokenTimestamps()
                .WithNoContext()
                .WithTemperature(0)
                .WithTemperatureInc(0)
                .WithThreads(Math.Clamp(Environment.ProcessorCount, 1, 8));
            // No shorter encoder context (WithAudioContextSize), though it would be quicker: whisper.cpp's aligned
            // times read past their buffer with it (measured: the small model crashed every other run), and without
            // aligned times the made-up words can't be told.
            // What was said just before, so the words carry on in the same spelling and language.
            var context = string.Join(' ', _recent.TakeLast(2));
            if (context.Length > 0)
                builder = builder.WithPrompt(context.Length > 200 ? context[^200..] : context);
            await using var processor = builder.Build();
            var words = new List<StreamWord>();
            await foreach (var segment in processor.ProcessAsync(window, limit.Token).ConfigureAwait(false))
            {
                if (_autoLanguage && !string.IsNullOrEmpty(segment.Language))
                    Learn(segment.Language);
                if (segment.NoSpeechProbability > 0.7 && segment.Probability < 0.4)
                    continue;
                words.AddRange(Words(segment, windowStart, window.Length, _aligned));
            }
            var clean = Clean(words);
            if (_aligned)
                DropInvented(clean);
            return clean;
        }
        finally
        {
            _model.Release();
        }
    }

    /// <summary>
    /// One pass over a moment of quiet, before listening: on its first pass the graphics card prepares its work
    /// (several seconds the first time), which would otherwise hold up the first words and run out of time.
    /// </summary>
    public async Task WarmUpAsync(CancellationToken cancellationToken)
    {
        await _model.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await using var processor = _factory.CreateBuilder()
                .WithLanguage(_language == "auto" ? "en" : _language)
                .WithTokenTimestamps()
                .WithNoContext()
                .WithTemperature(0)
                .WithTemperatureInc(0)
                .WithThreads(Math.Clamp(Environment.ProcessorCount, 1, 8))
                .Build();
            await foreach (var _ in processor.ProcessAsync(new float[Rate], cancellationToken).ConfigureAwait(false))
            {
            }
        }
        finally
        {
            _model.Release();
        }
    }

    /// <summary>Once the same language is heard a few passes running, it is kept: telling it each time costs time.</summary>
    private void Learn(string language)
    {
        if (_language == "auto")
        {
            _sameLanguage = language == _lastHeard ? _sameLanguage + 1 : 0;
            _lastHeard = language;
            if (_sameLanguage >= 2)
                _language = language;
        }
    }

    private string _lastHeard = "";

    /// <summary>
    /// A segment's tokens joined into words (a token starting with a space starts a word), at their times in the
    /// stream: a word runs from its first token's aligned time to the next word's (the segment's end for the last).
    /// </summary>
    private static List<StreamWord> Words(SegmentData segment, long windowStart, int windowLength, bool aligned)
    {
        double offset = (double)windowStart / Rate, last = (double)windowLength / Rate;
        var spans = new List<(string Text, double Start, double End)>();
        var text = new StringBuilder();
        double start = -1, end = 0;
        foreach (var token in segment.Tokens)
        {
            var t = token.Text;
            if (string.IsNullOrEmpty(t) || t.StartsWith("[_") || t.StartsWith("<|"))
                continue;
            double at = aligned && token.DtwTimestamp > 0 ? token.DtwTimestamp / 100.0 : token.Start / 100.0;
            if (t.StartsWith(' ') && text.Length > 0)
            {
                spans.Add((text.ToString().Trim(), start, end));
                text.Clear();
                start = -1;
            }
            if (start < 0)
                start = at;
            end = Math.Max(Math.Max(at, token.End / 100.0), start);
            text.Append(t);
        }
        if (text.ToString().Trim().Length > 0)
            spans.Add((text.ToString().Trim(), Math.Max(0, start), Math.Max(end, segment.End.TotalSeconds)));
        var words = new List<StreamWord>(spans.Count);
        for (int i = 0; i < spans.Count; i++)
        {
            double wordEnd = i + 1 < spans.Count ? Math.Max(spans[i].Start, Math.Min(spans[i].End, spans[i + 1].Start)) : spans[i].End;
            // Never later than the sound there is (Whisper's last times can run past it).
            words.Add(new StreamWord(spans[i].Text, offset + Math.Min(spans[i].Start, last), offset + Math.Min(wordEnd, last)));
        }
        return words;
    }

    /// <summary>Without what Whisper writes for noise ("[Music]", "♪") and without a phrase stuck in a loop.</summary>
    private static List<StreamWord> Clean(List<StreamWord> words)
    {
        var clean = new List<StreamWord>(words.Count);
        foreach (var w in words)
        {
            if (w.Key.Length == 0 && !w.Text.Any(char.IsLetterOrDigit))
            {
                // Punctuation alone belongs to the word before it.
                if (clean.Count > 0 && w.Text.Length <= 3 && !w.Text.Contains('[') && !w.Text.Contains('♪'))
                    clean[^1] = clean[^1] with { Text = clean[^1].Text + w.Text, End = w.End };
                continue;
            }
            if (w.Text.StartsWith('[') || w.Text.StartsWith('(') || w.Text.Contains('♪'))
                continue;
            clean.Add(w);
        }
        // A word or a few repeated over and over: Whisper going round in circles. Keep the first time.
        for (int size = 1; size <= 4; size++)
            for (int i = 0; i + size * 4 <= clean.Count; i++)
            {
                int repeats = 1;
                while (i + (repeats + 1) * size <= clean.Count && Enumerable.Range(0, size).All(k => clean[i + k].Key == clean[i + repeats * size + k].Key))
                    repeats++;
                if (repeats >= 4)
                {
                    clean.RemoveRange(i + size, clean.Count - i - size);
                    return clean;
                }
            }
        return clean;
    }

    /// <summary>
    /// Where the sound stops in the middle of a word, Whisper may go on with a sentence of its own. Its aligned
    /// times give it away: the invented words are all put at the same moment at the end, where real words each
    /// have their own. Such a run at the end of a pass is left out (the next pass hears what was really said).
    /// </summary>
    internal static void DropInvented(List<StreamWord> words)
    {
        int run = words.Count - 1;
        while (run > 0 && words[run].Start - words[run - 1].Start < 0.02)
            run--;
        if (words.Count - run >= 2)
            words.RemoveRange(run, words.Count - run);
    }

    /// <summary>
    /// The words two passes agree on are final; the rest of the newest pass is the draft. Words already final
    /// (their sound is still in the window) are found again by their text, so the new ones start after them.
    /// </summary>
    private void Agree(List<StreamWord> heard, long windowStart, bool talking)
    {
        var fresh = AfterFinal(heard, windowStart);
        int same = 0;
        while (same < fresh.Count && same < _previous.Count && fresh[same].Key == _previous[same].Key && fresh[same].Key.Length > 0)
            same++;
        // The last word heard while the talk goes on is held back: at the edge of the sound Whisper often ends it
        // with a full stop that isn't there. The next pass, or the quiet after it, settles it.
        if (same == fresh.Count && talking)
            same--;
        if (same > 0)
            Finish(fresh.Take(same).ToList());
        _previous = fresh.Skip(Math.Max(0, same)).ToList();
        Changed?.Invoke(State());
    }

    /// <summary>
    /// The words of a pass that aren't final yet. The last final words still in the window are looked for in it
    /// (the run nearest where they were said); what follows them is new. When they aren't heard again, the words
    /// said after the last final one are new, without any of those at their start that repeat it.
    /// </summary>
    private List<StreamWord> AfterFinal(List<StreamWord> heard, long windowStart)
    {
        if (_finalInWindow.Count == 0)
            return heard;
        double finalEnd = _finalInWindow[^1].End;
        for (int n = Math.Min(4, _finalInWindow.Count); n >= 1; n--)
        {
            int best = -1;
            double bestGap = n == 1 ? 0.8 : 2.0;
            for (int i = 0; i + n <= heard.Count; i++)
            {
                bool match = true;
                for (int k = 0; k < n && match; k++)
                    match = heard[i + k].Key == _finalInWindow[_finalInWindow.Count - n + k].Key;
                double gap = Math.Abs(heard[i + n - 1].End - finalEnd);
                if (match && gap < bestGap)
                {
                    best = i;
                    bestGap = gap;
                }
            }
            if (best >= 0)
                return heard.Skip(best + n).ToList();
        }
        var fresh = heard.Where(w => (w.Start + w.End) / 2 > finalEnd - 0.02).ToList();
        if (fresh.Count > 0 && fresh[0].Start < finalEnd + 1.0)
            for (int n = Math.Min(5, Math.Min(fresh.Count, _finalInWindow.Count)); n >= 1; n--)
                if (Enumerable.Range(0, n).All(i => fresh[i].Key == _finalInWindow[_finalInWindow.Count - n + i].Key))
                {
                    fresh.RemoveRange(0, n);
                    break;
                }
        return fresh;
    }

    /// <summary>Words become final; a sentence that ends with them is passed on, and its sound cut away.</summary>
    private void Finish(List<StreamWord> words)
    {
        _finalInWindow.AddRange(words);
        foreach (var w in words)
        {
            _sentence.Add(w);
            bool end = w.Text.EndsWith('.') || w.Text.EndsWith('?') || w.Text.EndsWith('!') || w.Text.EndsWith('؟') || w.Text.EndsWith('。');
            // Long talk without a full stop still makes sentences, at a comma or after enough words; only a full stop
            // cuts the sound, where the next words can't have begun.
            if (end)
                EndSentence(cut: true);
            else if (_sentence.Count >= 28 || _sentence.Count >= 14 && (w.Text.EndsWith(',') || w.Text.EndsWith('،')))
                EndSentence(cut: false);
        }
    }

    private void EndSentence(bool cut = true)
    {
        if (_sentence.Count == 0)
            return;
        var text = string.Join(' ', _sentence.Select(w => w.Text));
        var sentence = new StreamSentence(text, _language == "auto" ? _lastHeard : _language, _sentence[0].Start, _sentence[^1].End);
        _recent.Add(text);
        if (_recent.Count > 6)
            _recent.RemoveAt(0);
        // Its sound isn't needed any more. The window keeps its last two words, so the next pass hears them again
        // and the new words are found after them by their text (a cut by time alone loses or repeats a word).
        if (cut)
            CutAt((long)((_sentence[Math.Max(0, _sentence.Count - 2)].Start - 0.1) * Rate));
        _sentence.Clear();
        Sentence?.Invoke(sentence);
    }

    /// <summary>All that is waiting becomes final (it went quiet, or the window was cut).</summary>
    private void FinishAll()
    {
        // Taken out first: a sentence finished on the way cuts the window, which trims _previous.
        var waiting = _previous;
        _previous = [];
        if (waiting.Count > 0)
            Finish(waiting);
        EndSentence();
        Changed?.Invoke(State());
    }

    /// <summary>Drops the window's sound before a point of the stream (in samples), and the final words said before it.</summary>
    private void CutAt(long sample)
    {
        lock (_gate)
        {
            long drop = Math.Clamp(sample - _windowStart, 0, _windowCount);
            if (drop == 0)
                return;
            Array.Copy(_window, drop, _window, 0, _windowCount - drop);
            _windowCount -= (int)drop;
            _windowStart += drop;
        }
        double cut = (double)sample / Rate;
        _finalInWindow.RemoveAll(w => w.End <= cut + 0.01);
        _previous.RemoveAll(w => w.End <= cut + 0.01);
    }

    private StreamState State()
    {
        double newest = _previous.Count > 0 ? _previous[^1].End : _sentence.Count > 0 ? _sentence[^1].End : -1;
        return new StreamState(_recent.LastOrDefault() ?? "", string.Join(' ', _sentence.Select(w => w.Text)),
            string.Join(' ', _previous.Select(w => w.Text)), _language == "auto" ? _lastHeard : _language, newest);
    }

    /// <summary>Speech, not quiet or a hum: louder than the quiet level learnt so far.</summary>
    private bool Loud(ReadOnlySpan<float> samples)
    {
        const int Window = Rate * 30 / 1000;
        int loud = 0, windows = 0;
        for (int i = 0; i + Window <= samples.Length; i += Window)
        {
            double sum = 0;
            for (int k = 0; k < Window; k++)
                sum += samples[i + k] * samples[i + k];
            double rms = Math.Sqrt(sum / Window);
            windows++;
            if (rms > Math.Max(0.006, _noise * 3))
                loud++;
            else
                _noise = _noise * 0.98 + rms * 0.02;
        }
        // A tenth of the time loud is speech (or a voice over music); less is a click or a cough.
        return windows > 0 && loud * 10 >= windows;
    }

    /// <summary>
    /// A finished sentence put into English by Whisper from its own sound (for languages the translation models
    /// don't read), with a model trained to translate. Waits for the current pass; empty when its sound is no
    /// longer kept, or when no model here can translate.
    /// </summary>
    public async Task<string> ToEnglishAsync(StreamSentence sentence, CancellationToken cancellationToken)
    {
        float[] sound;
        lock (_gate)
        {
            long from = (long)(sentence.Start * Rate) - Rate / 5, to = (long)(sentence.End * Rate) + Rate / 5;
            from = Math.Max(from, Math.Max(0, _archived - _archive.Length));
            to = Math.Min(to, _archived);
            if (to - from < Rate / 4)
                return "";
            sound = new float[to - from];
            for (long i = from; i < to; i++)
                sound[i - from] = _archive[i % _archive.Length];
        }
        await _model.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            // large-v3 turbo was never trained to translate (asked to, it writes nonsense): another model does it.
            var factory = _canTranslate ? _factory
                : _translating ??= _translatingPath is { } path && File.Exists(path) ? WhisperFactory.FromPath(path) : null;
            if (factory is null)
                return "";
            var builder = factory.CreateBuilder().WithLanguage(sentence.Language is { Length: > 0 } l && l != "auto" ? l : "auto")
                .WithTranslate().WithNoContext().WithTemperature(0).WithTemperatureInc(0).WithThreads(Math.Clamp(Environment.ProcessorCount, 1, 8));
            await using var processor = builder.Build();
            var parts = new List<string>();
            await foreach (var segment in processor.ProcessAsync(sound, cancellationToken).ConfigureAwait(false))
                if (segment.Text.Trim() is { Length: > 0 } t && !LiveTranscriber.IsNoise(t))
                    parts.Add(t);
            return string.Join(' ', parts);
        }
        finally
        {
            _model.Release();
        }
    }

    private bool _disposed;

    /// <summary>Lets the model go, after the pass or translation under way (those that come later fail at once).</summary>
    public void Dispose()
    {
        bool idle = _model.Wait(TimeSpan.FromSeconds(15));
        if (!_disposed)
        {
            _disposed = true;
            if (idle)
            {
                _factory.Dispose();
                _translating?.Dispose();
            }
        }
        if (idle)
            _model.Release();
    }
}
