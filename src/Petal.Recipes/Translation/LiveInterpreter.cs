using System.Collections.Concurrent;
using Petal.Core.Text;
using Petal.Engines.Media;

namespace Petal.Recipes.Translation;

/// <summary>
/// A line of live translation. A finished sentence's line is shown on the captions' first line: first the quick
/// translation it had while it was being said (<see cref="Proper"/> false), moved up the moment it ended, then its
/// proper translation in its place. The sentence being said has the second line: <see cref="Text"/> grows word by
/// word as the speaker goes on, and its first <see cref="Steady"/> characters no longer change.
/// </summary>
/// <param name="Number">The sentence it is of (the first is 1).</param>
/// <param name="Final">The sentence is over.</param>
public sealed record LiveLine(int Number, bool Final, string Text, int Steady, bool Proper);

/// <summary>What puts live speech into another language: finished sentences properly, and the words so far of one being said.</summary>
public interface ILiveTranslator
{
    /// <summary>A finished sentence in another language, read by the sentences before it; null when nothing can translate it now.</summary>
    Task<string?> SentenceAsync(string text, string from, string to, IReadOnlyList<string> before, CancellationToken cancellationToken);

    /// <summary>
    /// The words so far of a sentence still being said, carrying on from <paramref name="begun"/> (the part of their
    /// translation that no longer changes); null when nothing can translate them now.
    /// </summary>
    Task<string?> SoFarAsync(string text, string from, string to, IReadOnlyList<string> before, string begun, CancellationToken cancellationToken);
}

/// <summary>
/// Interprets a talk as it is heard, for captions with two lines. Many languages don't settle what a sentence means
/// until it ends (the verb comes last, or the "not"), so no one line can be both instant and right. The second line is
/// instant: the words so far, translated as they come, each new translation carrying on from the words the last two
/// agreed on, so it grows and doesn't jump. The first line is right: when the sentence ends, what the second line holds
/// moves up at once, and the whole sentence, translated properly with the ones before it to read it by, takes its place.
/// Finished sentences always come first; when they gather faster than they can be translated they go as one, so the
/// translation catches up and nothing is left out.
/// </summary>
public sealed class LiveInterpreter
{
    private readonly ILiveTranslator _translator;
    private readonly Func<string?> _into;
    private readonly ConcurrentQueue<StreamSentence> _finished = new();
    private readonly SemaphoreSlim _wake = new(0);
    private volatile StreamState? _talk;
    private volatile CancellationTokenSource? _quick;
    private volatile bool _afresh;

    /// <param name="translator">What translates.</param>
    /// <param name="into">The language wanted now ("ar"), or null for none: asked each time, so a change is followed at once.</param>
    public LiveInterpreter(ILiveTranslator translator, Func<string?> into)
    {
        _translator = translator;
        _into = into;
    }

    /// <summary>A line to show.</summary>
    public event Action<LiveLine>? Line;

    /// <summary>A translation went wrong (the next is tried all the same).</summary>
    public event Action<Exception>? Failed;

    /// <summary>The talk as heard now: the sentence being said, with its newest words.</summary>
    public void Heard(StreamState talk)
    {
        _talk = talk;
        _wake.Release();
    }

    /// <summary>A sentence is over. Its proper translation comes before anything else, the quick one under way included.</summary>
    public void Finished(StreamSentence sentence)
    {
        _finished.Enqueue(sentence);
        try { _quick?.Cancel(); } catch (ObjectDisposedException) { }
        _wake.Release();
    }

    /// <summary>The language wanted changed: the sentence being said is translated afresh.</summary>
    public void Afresh()
    {
        _afresh = true;
        _wake.Release();
    }

    /// <summary>Translates until cancelled.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        // The quick translation of the sentence being said: its words, those of them that no longer change, the words
        // it was made from, and which sentence it is of.
        var quick = new List<string>();
        int steady = 0;
        string[] source = [];
        int quickOf = 0;
        // The latest finished sentence dealt with, and the last few as they were said.
        int done = 0;
        var before = new List<string>();
        string lastKey = "";
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await _wake.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            try
            {
                if (_afresh)
                {
                    _afresh = false;
                    quick = [];
                    steady = 0;
                    source = [];
                }
                string? into = _into();
                while (_finished.TryDequeue(out var sentence))
                {
                    var text = sentence.Text;
                    int first = sentence.Number, number = sentence.Number;
                    string language = sentence.Language;
                    // Behind the speaker: what has gathered goes as one.
                    while (_finished.TryDequeue(out var next))
                    {
                        text += " " + next.Text;
                        number = next.Number;
                        if (next.Language.Length > 0)
                            language = next.Language;
                    }
                    var moved = quickOf >= first && quickOf <= number && quick.Count > 0 && Covers(source, text) ? string.Join(' ', quick) : null;
                    quick = [];
                    steady = 0;
                    source = [];
                    quickOf = 0;
                    done = Math.Max(done, number);
                    // The same words final twice running are a slip of the speech model: said once, shown once.
                    var key = StreamWord.Normalize(text);
                    if (key.Length == 0 || key == lastKey)
                        continue;
                    lastKey = key;
                    string from = From(language, text);
                    if (into is null || from == into)
                    {
                        Remember(before, text);
                        continue;
                    }
                    if (moved is not null)
                        Line?.Invoke(new LiveLine(number, true, moved, 0, false));
                    var translated = await _translator.SentenceAsync(text, from, into, before, cancellationToken).ConfigureAwait(false);
                    Remember(before, text);
                    if (!string.IsNullOrWhiteSpace(translated))
                        Line?.Invoke(new LiveLine(number, true, Flat(translated), 0, true));
                }
                if (into is null || _talk is not { } talk || talk.Number <= done)
                    continue;
                var words = FirstSentence((talk.Current + " " + talk.Draft).Split(' ', StringSplitOptions.RemoveEmptyEntries));
                // A word or two say too little to translate: "The" has a dozen translations until its noun comes.
                if (words.Length < 2 || quickOf == talk.Number && words.SequenceEqual(source))
                    continue;
                var said = string.Join(' ', words);
                string spoken = From(talk.Language, said);
                if (spoken == into)
                    continue;
                if (quickOf != talk.Number || Reread(source, words))
                {
                    // Another sentence, or the speech model now hears its earlier words differently: what was translated
                    // from them may never have been said, so nothing of it is kept.
                    quick = [];
                    steady = 0;
                }
                quickOf = talk.Number;
                string? so;
                using (var own = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    _quick = own;
                    try
                    {
                        // A sentence that finished in the meantime comes first.
                        if (!_finished.IsEmpty)
                            continue;
                        so = await _translator.SoFarAsync(said, spoken, into, before.TakeLast(1).ToList(), string.Join(' ', quick.Take(steady)), own.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        continue;
                    }
                    finally
                    {
                        _quick = null;
                    }
                }
                source = words;
                if (string.IsNullOrWhiteSpace(so))
                    continue;
                var now = Unfinished(Flat(so)).Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
                // What two translations in a row agree on no longer changes; the newest word may yet.
                int same = 0;
                while (same < now.Count - 1 && same < quick.Count && now[same] == quick[same])
                    same++;
                steady = Math.Min(Math.Max(same, Math.Min(steady, now.Count - 1)), now.Count);
                quick = now;
                var line = string.Join(' ', now);
                Line?.Invoke(new LiveLine(talk.Number, false, line, string.Join(' ', now.Take(steady)).Length, false));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Failed?.Invoke(ex);
            }
        }
    }

    private static void Remember(List<string> before, string text)
    {
        before.Add(text);
        if (before.Count > 2)
            before.RemoveAt(0);
    }

    /// <summary>The language a talk is in: as heard, or told from its words when the speech model didn't say.</summary>
    private static string From(string language, string text) =>
        language is "" or "auto" ? LanguageGuess.Of(text) ?? "" : language;

    private static string Flat(string text) => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>
    /// The words up to the first full stop among them. The draft often runs a word or two into the next sentence before
    /// this one is called finished: translated with it, they would stand on the first line as part of a sentence they
    /// aren't in.
    /// </summary>
    internal static string[] FirstSentence(string[] words)
    {
        for (int i = 0; i + 1 < words.Length; i++)
            if (StreamingTranscriber.EndsSentence(words[i], words[i + 1]))
                return words[..(i + 1)];
        return words;
    }

    /// <summary>Without a full stop at its end: the sentence isn't over, whatever the translator wrote.</summary>
    internal static string Unfinished(string text) => text.TrimEnd('.', '。', '۔', '…', ' ');

    /// <summary>
    /// Whether the words a quick translation was made from are nearly all of a sentence, so it can stand for the
    /// sentence until the proper translation comes.
    /// </summary>
    internal static bool Covers(string[] source, string sentence)
    {
        int words = sentence.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        return source.Length >= 2 && source.Length >= words * 0.6;
    }

    /// <summary>
    /// Whether the speech model changed its mind about words well before the newest ones (the last three are a draft and
    /// change all the time).
    /// </summary>
    internal static bool Reread(string[] before, string[] now)
    {
        int same = 0;
        while (same < before.Length && same < now.Length && StreamWord.Normalize(before[same]) == StreamWord.Normalize(now[same]))
            same++;
        return same < before.Length - 3;
    }
}

/// <summary>
/// Live translation by what this PC has: the chosen cloud model for finished sentences when there is a key (each
/// request is billed, so the words so far aren't sent to it), the model on this PC otherwise and for the words so far.
/// Which model on this PC depends on its graphics card. With one, the model that reads any language by its meaning
/// keeps up with the speaker. Without one that model takes seconds a sentence while the speech model wants the same
/// cores, and falls ever further behind: there the small models made for the two languages translate
/// (<see cref="SmallTranslation"/>), which take a fraction of a second.
/// </summary>
public sealed class LiveTranslator : ILiveTranslator
{
    private readonly HashSet<string> _asked = [];
    private readonly SmallSoFar _soFar = new();

    /// <summary>Whether the model is told that the words were written down from speech, and may be misheard.</summary>
    public bool Heard { get; init; } = true;

    /// <summary>
    /// What would translate these two languages best on this PC isn't here: raised once for each pair, for offering it.
    /// Until it comes, whatever else is here translates.
    /// </summary>
    public event Action<string, string>? Missing;

    /// <summary>Whether this PC has a graphics card that runs the large model fast enough: as found, unless said.</summary>
    public bool Card { get; init; } = GemmaTranslation.HasCard;

    /// <summary>
    /// Whether small models are what should translate captions: there is no graphics card for the large one, and they
    /// know the two languages.
    /// </summary>
    public static bool SmallWanted(string from, string to, bool card) => !card && SmallTranslation.Knows(from, to);

    /// <summary>
    /// Whether small models translate this pair now: they are wanted and here, or they are here and the large one isn't.
    /// </summary>
    private bool Small(string from, string to) =>
        SmallTranslation.ReadyFor(from, to) && (!Card || !GemmaTranslation.Ready);

    private void Ask(string from, string to)
    {
        bool first;
        lock (_asked)
            first = _asked.Add(from + ">" + to);
        if (first)
            Missing?.Invoke(from, to);
    }

    public async Task<string?> SentenceAsync(string text, string from, string to, IReadOnlyList<string> before, CancellationToken cancellationToken)
    {
        if (Cloud.CloudAi.CanTranslate)
            return (await Translator.TranslateAsync([text], from.Length > 0 ? from : "auto", to, null, cancellationToken).ConfigureAwait(false))[0];
        if (Small(from, to))
            return await Task.Run(() => SmallTranslation.Sentence(text, from, to, cancellationToken), cancellationToken).ConfigureAwait(false);
        if (SmallWanted(from, to, Card) || !GemmaTranslation.Ready)
            Ask(from, to);
        if (!GemmaTranslation.Ready)
            return null;
        var said = await GemmaTranslation.TranslateAsync(text, from, to, before.Count > 0 ? before.TakeLast(1).ToList() : null, live: true, cancellationToken, heard: Heard).ConfigureAwait(false);
        return to == "ar" ? Translator.Tidy(said) : said;
    }

    public async Task<string?> SoFarAsync(string text, string from, string to, IReadOnlyList<string> before, string begun, CancellationToken cancellationToken)
    {
        if (Small(from, to))
            return await Task.Run(() => _soFar.Translate(text, from, to, begun, cancellationToken), cancellationToken).ConfigureAwait(false);
        if (!GemmaTranslation.Ready)
            return null;
        var said = await GemmaTranslation.SoFarAsync(text, from, to, before.Count > 0 ? before : null, begun, cancellationToken).ConfigureAwait(false);
        return to == "ar" ? Translator.Tidy(said) : said;
    }
}
