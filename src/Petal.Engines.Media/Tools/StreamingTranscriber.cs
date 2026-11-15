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

/// <summary>A sentence that is final: its words, the language, when it was said, and its number (the first is 1).</summary>
public sealed record StreamSentence(string Text, string Language, double Start, double End, int Number = 0);

/// <summary>
/// What the captions show now: the sentence before, the final words of the sentence being said, the newest words
/// that may still change, when the newest was said (seconds since listening began; -1 when nothing waits), and the
/// number of the sentence being said.
/// </summary>
public sealed record StreamState(string Previous, string Current, string Draft, string Language, double Newest, int Number = 0);

/// <summary>
/// One pass, for measuring: the stretch of the stream it read (seconds), what it heard, how long it took, and how sure
/// Whisper was of its words (0 to 1).
/// </summary>
public sealed record StreamPass(double From, double To, IReadOnlyList<StreamWord> Words, bool Closing, double Took, double Sure = 1);

/// <summary>
/// Near-live speech to text with Whisper, for captions. Instead of waiting for a pause, the sound since the last
/// finished sentence is written down again about every half second; the words two passes in a row agree on are
/// final ("local agreement", as in Macháček et al., Whisper-Streaming), the newest are a draft shown at once.
/// <para>
/// What is speech is told by a model made for that (Silero VAD), on sound brought to an even level first: a quiet
/// video or a soft voice is heard like a loud one, music and noise aren't sent to Whisper to make words of, and a
/// pause ends the sentence at once. No sound is ever thrown away to catch up: a pass takes the same time whether it
/// hears two seconds or twenty, so a slow PC shows the words later and never loses them.
/// </para>
/// </summary>
public sealed class StreamingTranscriber : IDisposable
{
    public const int Rate = 16000;

    private WhisperFactory _factory;
    private bool _aligned;
    private readonly bool _alignWords;
    private readonly WhisperVadFactory? _voiceFactory;
    private readonly WhisperVadProcessor? _voice;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _model = new(1, 1);
    private readonly bool _autoLanguage;
    private readonly string[] _languages;
    private readonly Dictionary<string, double> _votes = [];
    private string _language;
    private int _detections;
    // After a pause the language is asked about once more: another video, another speaker.
    private bool _askAgain;
    // How sure Whisper was of the last pass's words, 0 to 1.
    private double _sure = 1;

    // The sound still held: since the last cut. _start is where it begins in the stream, in samples.
    private float[] _sound = new float[Rate * 40];
    private int _count;
    private long _start;
    private long _received;
    private long _lastAdded = Stopwatch.GetTimestamp();
    // How far into the stream the last pass heard.
    private long _heardTo;
    // What a pass usually takes, in seconds, and how many in a row ran far over it.
    private double _usual;
    private int _overtime;

    private readonly StreamAssembler _words = new();

    /// <param name="modelPath">A Whisper model (ggml).</param>
    /// <param name="language">"auto" to tell (it is then kept once it is clear), or a code such as "en".</param>
    /// <param name="alignWords">
    /// Word times aligned to the sound (DTW) when the model's alignment heads are known: for a graphics card, where a
    /// pass is quick whatever it reads. False for a processor alone: Whisper then reads only as much as there is to
    /// hear instead of its full half minute, several times quicker, and the word times are its own estimates.
    /// </param>
    /// <param name="voiceModelPath">The model that tells speech from other sound (Silero VAD, ggml); loudness alone when null.</param>
    /// <param name="languages">The languages "auto" chooses among; any language Whisper knows when null.</param>
    public StreamingTranscriber(string modelPath, string language = "auto", bool alignWords = true, string? voiceModelPath = null, IReadOnlyList<string>? languages = null)
    {
        _alignWords = alignWords;
        (_factory, _aligned) = Load(modelPath, alignWords);
        _autoLanguage = string.IsNullOrEmpty(language) || language == "auto";
        _language = _autoLanguage ? "auto" : language;
        _languages = languages?.ToArray() ?? [];
        _words.Language = _autoLanguage ? "" : language;
        _words.Sentence += sentence => Sentence?.Invoke(sentence);
        if (voiceModelPath is not null && File.Exists(voiceModelPath))
        {
            try
            {
                // On the processor: it is tiny (a thirtieth of a second for half a minute of sound) and never waits for the card.
                _voiceFactory = WhisperVadFactory.FromPath(voiceModelPath, new WhisperFactoryOptions { UseGpu = false });
                _voice = _voiceFactory.CreateBuilder()
                    .WithThreshold(0.5f)
                    .WithMinSpeechDuration(TimeSpan.FromMilliseconds(120))
                    .WithMinSilenceDuration(TimeSpan.FromMilliseconds(100))
                    .WithSpeechPadding(TimeSpan.FromMilliseconds(30))
                    .WithThreads(2)
                    .Build();
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                // Loudness tells speech then, as before there was this model.
                _voiceFactory?.Dispose();
                _voiceFactory = null;
                _voice = null;
            }
        }
    }

    private static (WhisperFactory Factory, bool Aligned) Load(string modelPath, bool alignWords)
    {
        if (!File.Exists(modelPath))
            throw new FileNotFoundException("The speech model isn't downloaded yet.", modelPath);
        // For measuring, on a PC that has a graphics card, how a PC without one does: Whisper on the processor.
        if (Environment.GetEnvironmentVariable("PETAL_NO_CARD") is { Length: > 0 })
            Whisper.net.LibraryLoader.RuntimeOptions.RuntimeLibraryOrder = [Whisper.net.LibraryLoader.RuntimeLibrary.Cpu];
        // Word times aligned to the sound (DTW) where the model's alignment heads are known: the held sound is cut
        // after a finished sentence's last word, and the words heard again are found by when they were said.
        var heads = alignWords ? Heads(Path.GetFileName(modelPath)) : null;
        var factory = heads is { } preset
            ? WhisperFactory.FromPath(modelPath, new WhisperFactoryOptions { UseDtwTimeStamps = true, HeadsPreset = preset })
            : WhisperFactory.FromPath(modelPath);
        return (factory, heads is not null);
    }

    /// <summary>
    /// Whether a word ends its sentence, knowing the word after it when there is one ("U.S. imperialism" and "Dr. Smith"
    /// go on): the rule sentences are finished by here, for whoever reads the draft before they are.
    /// </summary>
    public static bool EndsSentence(string word, string? next) => StreamAssembler.EndsSentence(word, next);

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

    /// <summary>Seconds of quiet after speech that end the sentence being said.</summary>
    public double Pause { get; init; } = 0.6;

    /// <summary>
    /// The longest stretch of sound held, in seconds, before it is cut whether a sentence ended or not (15 at most on a
    /// processor alone, where a pass takes longer the more it reads).
    /// </summary>
    public double LongestWindow { get; init; } = 22;

    /// <summary>The draft or final words changed.</summary>
    public event Action<StreamState>? Changed;

    /// <summary>A pass went wrong (listening goes on).</summary>
    public event Action<Exception>? Failed;

    /// <summary>A sentence became final (for translating it).</summary>
    public event Action<StreamSentence>? Sentence;

    /// <summary>A pass was read (for measuring and tests).</summary>
    public event Action<StreamPass>? Passed;

    /// <summary>The library Whisper runs on (the processor, or the graphics card).</summary>
    public string Runtime => Whisper.net.LibraryLoader.RuntimeOptions.LoadedLibrary?.ToString() ?? "?";

    /// <summary>Whether a model tells speech from other sound (loudness alone does otherwise).</summary>
    public bool HearsVoice => _voice is not null;

    /// <summary>How long the last pass took for the sound it read (below 1 keeps up; the smaller, the quicker the words).</summary>
    public double Speed { get; private set; }

    /// <summary>How long the last pass took, in seconds: passes follow one another, so about how far the words are behind.</summary>
    public double LastPass { get; private set; }

    /// <summary>The language being heard ("en"), once known; "auto" until then.</summary>
    public string Language => _language;

    /// <summary>Adds sound: 16 kHz mono samples from -1 to 1. Any thread.</summary>
    public void Add(ReadOnlySpan<float> samples)
    {
        if (samples.IsEmpty)
            return;
        lock (_gate)
        {
            if (_count + samples.Length > _sound.Length)
                Array.Resize(ref _sound, Math.Max(_sound.Length * 2, _count + samples.Length));
            samples.CopyTo(_sound.AsSpan(_count));
            _count += samples.Length;
            _received += samples.Length;
            _lastAdded = Stopwatch.GetTimestamp();
        }
    }

    /// <summary>Listens until cancelled: a pass whenever there is a step of new sound, the draft finished when it goes quiet.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        long heardAt = 0;
        int failures = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                long received;
                bool stopped;
                lock (_gate)
                {
                    received = _received;
                    // The PC's sound sends nothing at all while nothing plays: that is a pause too.
                    stopped = Stopwatch.GetElapsedTime(_lastAdded).TotalSeconds > Pause + 0.3;
                }
                bool fresh = received - heardAt >= Step * Rate;
                bool idle = stopped && _words.Waiting;
                if (!fresh && !idle)
                {
                    try { await Task.Delay(20, cancellationToken).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
                    continue;
                }
                heardAt = received;
                // Too little said yet to read: looked at again as soon as a tenth of a second more has come, not a whole step later.
                if (!await PassAsync(idle, cancellationToken).ConfigureAwait(false))
                    heardAt = received - (long)((Step - 0.1) * Rate);
                failures = 0;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception e)
            {
                // A pass that went wrong never ends the captions, and the sound it was given is still here for the next.
                Failed?.Invoke(e);
                if (++failures >= 3)
                {
                    // Three in a row: this sound can't be heard. What was understood is kept and listening starts afresh.
                    failures = 0;
                    _words.FinishAll();
                    long end;
                    lock (_gate)
                        end = _start + _count;
                    CutTo(end - Rate / 2);
                    Changed?.Invoke(_words.State());
                }
                try { await Task.Delay(100, cancellationToken).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
            }
        }
    }

    /// <summary>
    /// One look at the sound held: whether there is speech in it, and if so what it says. False when there was speech
    /// but not yet enough of it to read.
    /// </summary>
    private async Task<bool> PassAsync(bool idle, CancellationToken cancellationToken)
    {
        float[] sound;
        long start;
        lock (_gate)
        {
            sound = _sound.AsSpan(0, _count).ToArray();
            start = _start;
        }
        if (sound.Length < Rate * 3 / 10 && !idle)
            return true;
        var level = SpeechLevel.Level(sound);
        var speech = Speech(level);
        if (speech.Count == 0)
        {
            // Nothing said in all that is held: what was waiting is final, and the quiet is let go.
            if (_words.Waiting)
            {
                _words.FinishAll();
                Changed?.Invoke(_words.State());
            }
            CutTo(start + sound.Length - Rate * 3 / 10);
            return true;
        }
        // Quiet before the first words isn't kept, nor sent to Whisper to make something of.
        if (!_words.Holding && speech[0].Start > 0.8)
        {
            int lead = (int)((speech[0].Start - 0.4) * Rate);
            CutTo(start + lead);
            sound = sound[lead..];
            level = level[lead..];
            start += lead;
            double shift = (double)lead / Rate;
            speech = speech.Select(s => (s.Start - shift, s.End - shift)).ToList();
        }
        double seconds = (double)sound.Length / Rate, last = Math.Min(seconds, speech[^1].End), quiet = seconds - last;
        long speechEnd = start + (long)(last * Rate);
        bool closing = idle || quiet >= Pause;
        if (closing && _heardTo >= speechEnd + Rate * 15 / 100)
        {
            // The speaker stopped and the last pass heard all of it: its words are final, with no pass more.
            if (_words.Waiting)
            {
                _words.FinishAll();
                Changed?.Invoke(_words.State());
            }
            CutTo(Math.Min(start + sound.Length, speechEnd + Rate / 5));
            _askAgain = true;
            return true;
        }
        // Under a second of sound is where Whisper makes things up, a long line of them at times: unless the speaker has
        // stopped, the first words wait for a little more to be said.
        if (!closing && seconds < 1.1)
            return false;
        // A long quiet after the words isn't read: at the end of the sound, quiet is where Whisper adds words of its own.
        int to = quiet > 0.4 ? Math.Min(sound.Length, (int)((last + 0.3) * Rate)) : sound.Length;
        var timer = Stopwatch.StartNew();
        List<StreamWord> heard;
        try
        {
            heard = await TranscribeAsync(level.AsSpan(0, to).ToArray(), start, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException) when (++_overtime < 3)
        {
            // Whisper went round in circles on this sound (it writes to its limit then, many times a pass's usual
            // length). Nothing is lost: the sound is still held, and the next pass reads it with what was said since.
            LastPass = timer.Elapsed.TotalSeconds;
            return true;
        }
        _overtime = 0;
        // Words Whisper was this unsure of are what comes of listening in the wrong language: the talk may have gone
        // over to another without a pause, so the language is asked about on the next pass.
        if (_autoLanguage && _language != "auto" && _sure < 0.5 && seconds >= 2.5)
            _askAgain = true;
        LastPass = timer.Elapsed.TotalSeconds;
        _usual = _usual == 0 ? LastPass : _usual * 0.8 + LastPass * 0.2;
        Speed = LastPass / Math.Max(Step, (double)to / Rate);
        _heardTo = start + to;
        if (_aligned)
            heard = InSpeech(heard, speech, (double)start / Rate);
        Passed?.Invoke(new StreamPass((double)start / Rate, (double)(start + to) / Rate, heard, closing, LastPass, _sure));
        _words.Language = _language == "auto" ? "" : _language;
        // Where nobody spoke, within what was read: the breaths long talk is parted at.
        double offset = (double)start / Rate;
        var pauses = new List<(double, double)>();
        for (int i = 1; i < speech.Count; i++)
            pauses.Add((offset + speech[i - 1].End, offset + speech[i].Start));
        _words.Take(heard, (double)(start + to) / Rate, closing, pauses);
        if (closing)
        {
            CutTo(Math.Min(start + sound.Length, speechEnd + Rate / 5));
            _askAgain = true;
        }
        else
        {
            if (_words.Cut is { } cut)
            {
                // A sentence ended a pass ago: its sound goes, at the quietest moment between it and the next.
                int from = (int)(cut.From * Rate - start), until = (int)(cut.To * Rate - start);
                CutTo(start + SpeechLevel.Quietest(level, from, until), atCut: true);
            }
            else if (seconds > (_aligned ? LongestWindow : Math.Min(LongestWindow, 15)))
            {
                // Without a sentence ending, the held sound would outgrow what Whisper reads at once.
                _words.Force();
            }
            if (seconds > LongestWindow + 6 && _words.Cut is null)
            {
                // Sound a model calls speech and Whisper makes no sentence of (singing in a language it wasn't told):
                // it can't be held for ever. What was understood is kept, and listening goes on from the last seconds.
                _words.FinishAll();
                CutTo(start + sound.Length - Rate * 2);
            }
        }
        Changed?.Invoke(_words.State());
        return true;
    }

    /// <summary>Where there is speech in a levelled sound, as seconds from its start.</summary>
    private List<(double Start, double End)> Speech(float[] level)
    {
        if (_voice is null)
            return SpeechLevel.Loud(level);
        return _voice.DetectSpeech(level).Select(s => (s.Start.TotalSeconds, s.End.TotalSeconds)).Where(s => s.Item2 > s.Item1).ToList();
    }

    /// <summary>
    /// Without the words put where nobody was speaking: Whisper makes those up (over music, in a pause). A word within
    /// half a second of speech stays, for the times of both are only near enough.
    /// </summary>
    internal static List<StreamWord> InSpeech(List<StreamWord> words, List<(double Start, double End)> speech, double offset)
    {
        return words.Where(w => speech.Any(s => w.Start < offset + s.End + 0.5 && w.End > offset + s.Start - 0.5)).ToList();
    }

    private async Task<List<StreamWord>> TranscribeAsync(float[] sound, long start, CancellationToken cancellationToken)
    {
        double seconds = (double)sound.Length / Rate;
        // whisper.cpp reads nothing shorter than a second.
        if (sound.Length < Rate * 12 / 10)
            Array.Resize(ref sound, Rate * 12 / 10);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        // Several times what a pass usually takes here is no longer listening but going round in circles.
        limit.CancelAfter(TimeSpan.FromSeconds(_usual == 0 ? 60 : Math.Max(4, _usual * 6)));
        try
        {
            await _model.WaitAsync(limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("The speech model was busy for too long.");
        }
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_language == "auto")
                Tell(sound, seconds);
            else if (_autoLanguage && _askAgain && seconds >= 2.5)
                TellAgain(sound);
            var builder = _factory.CreateBuilder()
                .WithLanguage(_language == "auto" ? Leading() : _language)
                .WithTokenTimestamps()
                .WithProbabilities()
                .WithNoContext()
                .WithTemperature(0)
                .WithTemperatureInc(0)
                .WithThreads(Math.Clamp(Environment.ProcessorCount, 1, 8));
            // A shorter encoder context is several times quicker on a processor, where reading takes most of a pass.
            // Not with aligned times: whisper.cpp's read past their buffer with it (measured: the small model crashed
            // every other run).
            if (!_aligned)
                builder = builder.WithAudioContextSize(Math.Clamp((int)Math.Ceiling(sound.Length * 50.0 / Rate) + 96, 384, 1500));
            // What was said just before, so the words carry on in the same spelling and language.
            var before = _words.Context((double)start / Rate);
            if (before.Length > 0)
                builder = builder.WithPrompt(before);
            await using var processor = builder.Build();
            var words = new List<StreamWord>();
            double sure = 0, letters = 0;
            try
            {
                await foreach (var segment in processor.ProcessAsync(sound, limit.Token).ConfigureAwait(false))
                {
                    if (segment.NoSpeechProbability > 0.6 && segment.Probability < 0.5 || Credits.Is(segment.Text))
                        continue;
                    words.AddRange(Words(segment, start, sound.Length, _aligned));
                    sure += segment.Probability * segment.Text.Length;
                    letters += segment.Text.Length;
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("A pass of the speech model took too long.");
            }
            _sure = letters > 0 ? sure / letters : 1;
            var clean = Clean(words);
            if (_aligned)
                DropInvented(clean);
            DropSqueezed(clean);
            return clean;
        }
        finally
        {
            _model.Release();
        }
    }

    /// <summary>
    /// Which language is being spoken, asked of Whisper among those it may be. Each answer counts for how sure it is
    /// and how much speech it had; once one language is clearly ahead it is kept, since asking costs a pass's time.
    /// </summary>
    private void Tell(float[] sound, double seconds)
    {
        using var probe = _factory.CreateBuilder().WithLanguage("auto").WithThreads(Math.Clamp(Environment.ProcessorCount, 1, 8)).Build();
        var (language, probability) = _languages.Length > 0 ? probe.DetectLanguageWithProbability(sound, _languages) : probe.DetectLanguageWithProbability(sound);
        if (string.IsNullOrEmpty(language))
            return;
        _votes[language] = _votes.GetValueOrDefault(language) + probability * Math.Min(seconds, 10);
        _detections++;
        var ahead = _votes.OrderByDescending(v => v.Value).ToList();
        double second = ahead.Count > 1 ? ahead[1].Value : 0;
        if (ahead[0].Value - second >= 2.5 || _detections >= 6)
            _language = ahead[0].Key;
    }

    /// <summary>
    /// The first words after a pause, once there are enough of them: is this still the language settled on? Someone who
    /// goes from an English video to an Arabic one is followed, where the language used to stay what the first video spoke.
    /// Only a clear answer changes it: a few words of another language inside a talk don't. It is also asked when
    /// Whisper was unsure of a whole pass, which is what the wrong language looks like.
    /// </summary>
    private void TellAgain(float[] sound)
    {
        _askAgain = false;
        // The newest five seconds: what is being spoken now, not what the sentence began in.
        if (sound.Length > Rate * 5)
            sound = sound[^(Rate * 5)..];
        using var probe = _factory.CreateBuilder().WithLanguage("auto").WithThreads(Math.Clamp(Environment.ProcessorCount, 1, 8)).Build();
        var (language, probability) = _languages.Length > 0 ? probe.DetectLanguageWithProbability(sound, _languages) : probe.DetectLanguageWithProbability(sound);
        if (string.IsNullOrEmpty(language) || language == _language || probability < 0.8)
            return;
        _language = language;
        _votes.Clear();
        _votes[language] = 10;
        _words.Forget();
    }

    /// <summary>The language ahead so far, while it isn't settled.</summary>
    private string Leading() => _votes.Count == 0 ? "auto" : _votes.MaxBy(v => v.Value).Key;

    /// <summary>
    /// One pass over a moment of quiet, before listening: on its first pass the graphics card prepares its work
    /// (several seconds the first time), which would otherwise hold up the first words.
    /// </summary>
    public async Task WarmUpAsync(CancellationToken cancellationToken)
    {
        await _model.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await WarmAsync(_factory, _language == "auto" ? "en" : _language, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _model.Release();
        }
    }

    private static async Task WarmAsync(WhisperFactory factory, string language, CancellationToken cancellationToken)
    {
        await using var processor = factory.CreateBuilder()
            .WithLanguage(language)
            .WithTokenTimestamps()
            .WithNoContext()
            .WithTemperature(0)
            .WithTemperatureInc(0)
            .WithThreads(Math.Clamp(Environment.ProcessorCount, 1, 8))
            .Build();
        await foreach (var _ in processor.ProcessAsync(new float[Rate * 2], cancellationToken).ConfigureAwait(false))
        {
        }
    }

    /// <summary>
    /// Changes the speech model while listening (a quicker one when this PC can't keep up). The sound held and the
    /// words so far stay as they are: the next pass is simply read by the other model.
    /// </summary>
    public async Task UseModelAsync(string modelPath, CancellationToken cancellationToken)
    {
        var (factory, aligned) = Load(modelPath, _alignWords);
        try
        {
            await WarmAsync(factory, _language == "auto" ? "en" : _language, cancellationToken).ConfigureAwait(false);
            await _model.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            factory.Dispose();
            throw;
        }
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            (factory, _factory) = (_factory, factory);
            _aligned = aligned;
        }
        finally
        {
            _model.Release();
            factory.Dispose();
        }
    }

    /// <summary>
    /// A segment's words at their times in the stream, spelled as the segment's text spells them (a letter of Arabic
    /// or Turkish can be split between two of Whisper's pieces, and a piece alone doesn't read).
    /// </summary>
    private static List<StreamWord> Words(SegmentData segment, long windowStart, int windowLength, bool aligned)
    {
        double offset = (double)windowStart / Rate, last = (double)windowLength / Rate;
        // Never later than the sound there is (Whisper's last times can run past it).
        return TokenWords.Spelled(segment.Text, TokenWords.Of(segment, aligned))
            .Select(w => new StreamWord(w.Text, offset + Math.Min(w.Start, last), offset + Math.Min(Math.Max(w.Start, w.End), last))).ToList();
    }

    /// <summary>Without what Whisper writes for noise ("[Music]", "♪") and without a phrase stuck in a loop.</summary>
    internal static List<StreamWord> Clean(List<StreamWord> words)
    {
        var clean = new List<StreamWord>(words.Count);
        foreach (var w in words)
        {
            if (w.Key.Length == 0)
            {
                // Punctuation alone belongs to the word before it.
                if (clean.Count > 0 && w.Text.Length <= 3 && !w.Text.Contains('[') && !w.Text.Contains('♪') && !w.Text.Contains('*'))
                    clean[^1] = clean[^1] with { Text = clean[^1].Text + w.Text, End = Math.Max(clean[^1].End, w.End) };
                continue;
            }
            if (w.Text.StartsWith('[') || w.Text.StartsWith('(') || w.Text.StartsWith('*') || w.Text.Contains('♪'))
                continue;
            clean.Add(w);
        }
        // A word or a few repeated over and over: Whisper going round in circles. Keep the first time.
        for (int size = 1; size <= 6; size++)
            for (int i = 0; i + size * 3 <= clean.Count; i++)
            {
                int repeats = 1;
                while (i + (repeats + 1) * size <= clean.Count && Enumerable.Range(0, size).All(k => clean[i + k].Key == clean[i + repeats * size + k].Key))
                    repeats++;
                if (repeats >= (size == 1 ? 4 : 3))
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
    /// At the end of the sound Whisper sometimes says the sentence over again, or goes on with one of its own, and has
    /// no time left to put the words in: it gives them all a moment each, a dozen words in half a second. Nobody speaks
    /// four words in a quarter of a second, so a run like that at the end of a pass was never said.
    /// </summary>
    internal static void DropSqueezed(List<StreamWord> words)
    {
        for (int n = words.Count; n >= 4; n--)
        {
            if (words[^1].Start - words[^n].Start < (n - 1) * 0.085)
            {
                words.RemoveRange(words.Count - n, n);
                return;
            }
        }
    }

    /// <summary>Drops the held sound before a point of the stream (in samples), and tells the words so.</summary>
    private void CutTo(long sample, bool atCut = false)
    {
        lock (_gate)
        {
            long drop = Math.Clamp(sample - _start, 0, _count);
            if (drop == 0)
                return;
            Array.Copy(_sound, drop, _sound, 0, _count - drop);
            _count -= (int)drop;
            _start += drop;
            sample = _start;
        }
        _words.Dropped((double)sample / Rate, atCut);
    }

    private bool _disposed;

    /// <summary>Lets the model go, after the pass under way (those that come later fail at once).</summary>
    public void Dispose()
    {
        bool idle = _model.Wait(TimeSpan.FromSeconds(15));
        if (!_disposed)
        {
            _disposed = true;
            if (idle)
            {
                _factory.Dispose();
                _voice?.Dispose();
                _voiceFactory?.Dispose();
            }
        }
        if (idle)
            _model.Release();
    }
}

/// <summary>
/// The lines Whisper learnt from the ends of subtitle files and writes when it hears nothing it knows: nobody says
/// them, so a segment that is one of them isn't shown.
/// </summary>
internal static class Credits
{
    private static readonly string[] Known =
    [
        "subtitlesbytheamaraorgcommunity", "subtitlesbyamaraorg", "amaraorg", "subtitlesby", "subtitledby", "captionsby", "transcribedby",
        "translatedby", "soustitresrealisesparlacommunautedamaraorg", "soustitrage", "soustitresrealises", "soustitrespar",
        "subtitulosrealizadosporlacomunidaddeamaraorg", "subtitulospor", "subtituladopor", "subtitulosdelacomunidad",
        "altyazımk", "altyazimk", "ترجمةنانسيقنقر", "نانسيقنقر", "اشتركوافيالقناة",
        "terjemahanoleh", "subtitleoleh", "terimakasihtelahmenonton", "untertitelderamaraorgcommunity", "untertitelvon", "legendaspelacomunidadeamaraorg",
        "wwwzeoranger", "opensubtitles",
    ];

    public static bool Is(string text)
    {
        var plain = new StringBuilder(text.Length);
        foreach (var c in text.Normalize(NormalizationForm.FormD))
        {
            if (char.IsLetterOrDigit(c))
                plain.Append(char.ToLowerInvariant(c));
        }
        var line = plain.ToString();
        if (line.Length == 0 || line.Length > 70)
            return false;
        foreach (var known in Known)
        {
            // The whole of a short line, or its start.
            if (line.StartsWith(known, StringComparison.Ordinal) && line.Length <= known.Length + 24)
                return true;
        }
        return false;
    }
}
