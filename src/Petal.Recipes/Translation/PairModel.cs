using System.IO;
using System.Text;
using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Petal.Core.Models;

namespace Petal.Recipes.Translation;

/// <summary>What a small translation model wrote: the words, and the pieces they were written in.</summary>
internal sealed record PairOutput(string Text, IReadOnlyList<long> Pieces);

/// <summary>
/// One small translation model, run by ONNX Runtime: its encoder and decoder, the source language's pieces, the shared
/// vocabulary, the id the decoder starts from, and the mark of the target language (when the model has several).
/// Opus-MT's models (Marian) are made for one pair, or mark the language wanted in front of the text; M2M100 marks both
/// languages by id, the source's before the text and the target's as the decoder's first word; SMaLL-100, its smaller
/// kin, puts the target's mark before the text and nothing before the answer.
/// <para>
/// A decoder that takes back what it worked out for the words before (a "merged" export, with past keys and values
/// among its inputs) writes each new word from that and the word alone; one that doesn't reads the whole answer so far
/// again for every word, which for a sentence of forty pieces is some twenty times the work.
/// </para>
/// </summary>
/// <param name="pad">The id the decoder starts from.</param>
/// <param name="mark">A piece put before the text to say which language is wanted (">>ara&lt;&lt;").</param>
/// <param name="sourceMark">An id put before the text.</param>
/// <param name="targetMark">An id the answer starts with.</param>
/// <param name="width">How many candidates a careful translation follows at once.</param>
internal sealed class PairModel(IReadOnlyList<ModelInfo> files, long pad, string? mark, long eos = 0, long? ban = null, long? sourceMark = null, long? targetMark = null, int width = 4)
{
    private Loaded? _loaded;

    public IReadOnlyList<ModelInfo> Files => files;

    public bool Ready => files.All(ModelStore.IsAvailable);

    /// <summary>
    /// How many threads a model works with (set before it is first used). Captions on a processor alone share the cores
    /// with the speech model, which is already using all of them: fewer threads each get on better than many.
    /// </summary>
    public static int Threads { get; set; } = Math.Clamp(Environment.ProcessorCount, 1, 8);

    /// <summary>The PC's memory, in bytes (settable, for measuring how a PC with less does).</summary>
    internal static long Memory { get; set; } = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;

    /// <summary>What a decoder that carries on takes back: how many layers' keys and values, and their shape.</summary>
    private sealed record Past(int Layers, int Heads, int Size);

    // One model's parts, shared by all the pairs that use it (M2M100 is one model for every pair).
    private sealed class Loaded
    {
        public required InferenceSession Encoder { get; init; }
        public required InferenceSession Decoder { get; init; }
        public required Unigram Source { get; init; }
        public required Dictionary<string, long> Vocabulary { get; init; }
        public required string[] Pieces { get; init; }
        public required Past? Past { get; init; }
    }

    private static readonly Dictionary<string, Loaded> Shared = [];

    private Loaded Load()
    {
        lock (Shared)
        {
            if (_loaded is not null)
                return _loaded;
            // By its encoder and its decoder: a pair may be here with two decoders, one that carries on and one that doesn't.
            string key = files[0].Id + "+" + files[1].Id;
            if (Shared.TryGetValue(key, out var known))
                return _loaded = known;
            string Find(ModelInfo model) => ModelStore.Find(model) ?? throw new FileNotFoundException("The translation model isn't downloaded yet.");
            var options = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL, IntraOpNumThreads = Threads };
            // Threads that wait for work by spinning keep whole cores busy doing nothing, between one word and the
            // next: cores the speech model wants while captions run.
            options.AddSessionConfigEntry("session.intra_op.allow_spinning", "0");
            using var json = JsonDocument.Parse(File.ReadAllBytes(Find(files[3])));
            var vocabulary = json.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetInt64(), StringComparer.Ordinal);
            var pieces = new string[vocabulary.Values.Max() + 1];
            foreach (var (piece, id) in vocabulary)
                pieces[id] = piece;
            var encoder = new InferenceSession(Find(files[0]), options);
            // The decoder's largest table, a row of numbers for every piece of the vocabulary, is kept as bytes, and
            // as the files are made it is turned back into numbers again for every word written: 10 ms of a word's 21
            // for a pair's own model, 60 of 88 for M2M100. Told to, ONNX Runtime does it once, when the model loads;
            // the price is the table as numbers in memory, twice over (once for each of the decoder's two ways of
            // running): 0.26 GB for a pair's model, 1 GB for M2M100. So it is done where the PC has the room.
            int numbers = encoder.OutputMetadata.Values.First().Dimensions is [.., > 0 and var last] ? last : 1024;
            long room = 2L * pieces.Length * numbers * sizeof(float);
            if (room <= Memory / 12)
                options.AddSessionConfigEntry("session.disable_quant_qdq", "1");
            var decoder = new InferenceSession(Find(files[1]), options);
            Past? past = null;
            if (decoder.InputMetadata.ContainsKey("use_cache_branch")
                && decoder.InputMetadata.TryGetValue("past_key_values.0.decoder.key", out var first) && first.Dimensions is [_, > 0, _, > 0])
            {
                past = new Past(decoder.InputMetadata.Keys.Count(k => k.EndsWith(".decoder.key", StringComparison.Ordinal)), first.Dimensions[1], first.Dimensions[3]);
            }
            return _loaded = Shared[key] = new Loaded
            {
                Source = new Unigram(File.ReadAllBytes(Find(files[2]))),
                Vocabulary = vocabulary,
                Pieces = pieces,
                Encoder = encoder,
                Decoder = decoder,
                Past = past,
            };
        }
    }

    /// <summary>Loads the model now, so the first translation doesn't wait for it.</summary>
    public void Prepare() => Load();

    public long[] Encode(string text)
    {
        var model = Load();
        long unknown = model.Vocabulary.GetValueOrDefault("<unk>", 1);
        var ids = new List<long>();
        if (sourceMark is { } own)
            ids.Add(own);
        if (mark is not null)
            ids.Add(model.Vocabulary[mark]);
        ids.AddRange(model.Source.Split(text).Take(250).Select(p => model.Vocabulary.GetValueOrDefault(p, unknown)));
        ids.Add(eos);
        return ids.ToArray();
    }

    public IReadOnlyList<string> Lines(IReadOnlyList<string> lines, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var result = new string[lines.Count];
        for (int i = 0; i < lines.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = lines[i].Trim();
            result[i] = line.Length == 0 || !line.Any(char.IsLetter) ? line : Translate(line, width, null, cancellationToken).Text;
            progress?.Report((i + 1.0) / lines.Count);
        }
        return result;
    }

    /// <summary>One line. Quick follows only the likeliest word at each step (about four times less work).</summary>
    public string Line(string line, bool quick)
    {
        line = line.Trim();
        return line.Length == 0 || !line.Any(char.IsLetter) ? line : Translate(line, quick ? 1 : width, null, default).Text;
    }

    /// <summary>
    /// The start of an answer that is made of its first few words: the pieces up to where the word after them begins.
    /// A translation that carries on from words already on screen is given these, and writes only what follows.
    /// </summary>
    public IReadOnlyList<long> Start(IReadOnlyList<long> answer, int words)
    {
        var pieces = Load().Pieces;
        int seen = 0;
        for (int i = 0; i < answer.Count; i++)
        {
            long id = answer[i];
            bool begins = id >= 0 && id < pieces.Length && pieces[id] is { } piece && piece.StartsWith('▁') || i == 0;
            if (begins && ++seen > words)
                return answer.Take(i).ToList();
        }
        return answer;
    }

    /// <summary>Whether a piece begins a word.</summary>
    public bool Begins(long id)
    {
        var pieces = Load().Pieces;
        return id >= 0 && id < pieces.Length && pieces[id] is { } piece && piece.StartsWith('▁');
    }

    /// <summary>The words some pieces spell.</summary>
    public string Words(IEnumerable<long> answer)
    {
        var pieces = Load().Pieces;
        var words = new StringBuilder();
        foreach (var id in answer)
            if (id >= 0 && id < pieces.Length && pieces[id] is { } piece && !piece.StartsWith('<'))
                words.Append(piece);
        return words.ToString().Replace('▁', ' ').Trim();
    }

    private sealed record Beam(List<long> Tokens, double Score);

    /// <summary>
    /// The text in the model's other language. <paramref name="candidates"/> is how many candidates are followed at
    /// once (1: the likeliest word at each step). <paramref name="begun"/> is the start of the answer, in pieces, when
    /// it is already settled: the model carries on from it, and what it had written can't change.
    /// <paramref name="most"/> is how many pieces it may add at most (a guard against an answer that never ends).
    /// </summary>
    public PairOutput Translate(string text, int candidates, IReadOnlyList<long>? begun, CancellationToken cancellationToken, int? most = null)
    {
        var model = Load();
        var ids = Encode(text);
        int length = ids.Length;
        var mask = Enumerable.Repeat(1L, length).ToArray();
        float[] hidden;
        using (var idsTensor = OrtValue.CreateTensorValueFromMemory(ids, [1, length]))
        using (var maskTensor = OrtValue.CreateTensorValueFromMemory(mask, [1, length]))
        using (var run = new RunOptions())
        {
            var names = model.Encoder.InputMetadata.Keys.ToList();
            using var encoded = model.Encoder.Run(run, names, names.Select(n => n == "input_ids" ? idsTensor : maskTensor).ToList(), [model.Encoder.OutputMetadata.Keys.First()]);
            hidden = encoded[0].GetTensorDataAsSpan<float>().ToArray();
        }

        List<long> begin = targetMark is { } target ? [pad, target] : [pad];
        int given = begin.Count;
        if (begun is { Count: > 0 })
            begin.AddRange(begun);
        int maxLength = Math.Min(most ?? int.MaxValue, Math.Min(256, length * 3 + 10));
        var best = model.Past is { } past
            ? Carrying(model, past, hidden, mask, begin, candidates, maxLength, cancellationToken)
            : Rereading(model, hidden, mask, begin, candidates, maxLength, cancellationToken);
        var answer = new List<long>(best.Count);
        var words = new StringBuilder();
        foreach (var token in best.Skip(given))
        {
            if (token == eos || token == pad || token == ban || token >= model.Pieces.Length || model.Pieces[token] is not { } piece || piece.StartsWith('<'))
                continue;
            answer.Add(token);
            words.Append(piece);
        }
        return new PairOutput(words.ToString().Replace('▁', ' ').Trim(), answer);
    }

    /// <summary>
    /// How each candidate goes on, from the decoder's numbers for its last word (<paramref name="logits"/> holds
    /// <paramref name="steps"/> words for each of the candidates): the best few stay, those that ended are put aside.
    /// <paramref name="from"/> says which candidate each of the new ones carries on. False when there is nothing more to follow.
    /// </summary>
    private bool Advance(List<Beam> beams, List<Beam> finished, ReadOnlySpan<float> logits, int steps, int candidates, out int[] from)
    {
        int rows = beams.Count;
        int vocabulary = logits.Length / (rows * steps);
        var next = new List<(Beam Beam, int From)>();
        for (int b = 0; b < rows; b++)
        {
            var row = logits.Slice((b * steps + steps - 1) * vocabulary, vocabulary).ToArray();
            row[ban ?? pad] = float.NegativeInfinity;
            if (candidates == 1)
            {
                int likeliest = 0;
                for (int i = 1; i < row.Length; i++)
                    if (row[i] > row[likeliest])
                        likeliest = i;
                next.Add((new Beam([.. beams[b].Tokens, likeliest], 0), b));
                continue;
            }
            // Log-softmax, then the best few continuations of this candidate.
            float max = row.Max();
            double sum = 0;
            foreach (var v in row)
                sum += Math.Exp(v - max);
            double logSum = max + Math.Log(sum);
            foreach (int token in Top(row, candidates))
                next.Add((new Beam([.. beams[b].Tokens, token], beams[b].Score + row[token] - logSum), b));
        }
        beams.Clear();
        var parents = new List<int>();
        foreach (var candidate in next.OrderByDescending(c => c.Beam.Score / c.Beam.Tokens.Count))
        {
            if (candidate.Beam.Tokens[^1] == eos)
                finished.Add(candidate.Beam);
            else if (beams.Count < candidates)
            {
                beams.Add(candidate.Beam);
                parents.Add(candidate.From);
            }
            if (beams.Count == candidates)
                break;
        }
        from = parents.ToArray();
        return beams.Count > 0 && finished.Count < candidates;
    }

    private static List<long> Best(List<Beam> beams, List<Beam> finished) =>
        (finished.Count > 0 ? finished : beams).MaxBy(c => c.Score / c.Tokens.Count)!.Tokens;

    /// <summary>The answer by a decoder that reads all of it so far again for every word.</summary>
    private List<long> Rereading(Loaded model, float[] hidden, long[] mask, List<long> begin, int candidates, int maxLength, CancellationToken cancellationToken)
    {
        int length = mask.Length, size = hidden.Length / length;
        var beams = new List<Beam> { new(begin, 0) };
        var finished = new List<Beam>();
        var names = model.Decoder.InputMetadata.Keys.ToList();
        for (int step = 0; step < maxLength; step++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int batch = beams.Count, steps = beams[0].Tokens.Count;
            var decoderIds = new long[batch * steps];
            for (int b = 0; b < batch; b++)
                beams[b].Tokens.CopyTo(decoderIds, b * steps);
            using var idsValue = OrtValue.CreateTensorValueFromMemory(decoderIds, [batch, steps]);
            using var statesValue = OrtValue.CreateTensorValueFromMemory(Repeated(hidden, batch), [batch, length, size]);
            using var maskValue = OrtValue.CreateTensorValueFromMemory(Repeated(mask, batch), [batch, length]);
            using var run = new RunOptions();
            var values = names.Select(n => n == "input_ids" ? idsValue : n.Contains("mask") ? maskValue : statesValue).ToList();
            using var outputs = model.Decoder.Run(run, names, values, ["logits"]);
            if (!Advance(beams, finished, outputs[0].GetTensorDataAsSpan<float>(), steps, candidates, out _))
                break;
        }
        return Best(beams, finished);
    }

    /// <summary>
    /// The answer by a decoder that carries on: the start goes through it whole, once, and every word after that is
    /// written from the word before and what the decoder kept of the rest.
    /// </summary>
    private List<long> Carrying(Loaded model, Past past, float[] hidden, long[] mask, List<long> begin, int candidates, int maxLength, CancellationToken cancellationToken)
    {
        int length = mask.Length, size = hidden.Length / length, kept = past.Layers * 2;
        var decoder = model.Decoder;
        var names = decoder.InputMetadata.Keys.ToList();
        // What is asked of it: its numbers for the next word, what it keeps of the answer, and (the first time) of the text.
        var own = new List<string> { "logits" };
        var theirs = new List<string>();
        for (int layer = 0; layer < past.Layers; layer++)
        {
            own.Add($"present.{layer}.decoder.key");
            own.Add($"present.{layer}.decoder.value");
            theirs.Add($"present.{layer}.encoder.key");
            theirs.Add($"present.{layer}.encoder.value");
        }
        // Where each input's value is found: among what it kept of the answer (0 and up), or of the text (-1 and down).
        static int Place(string name)
        {
            // "past_key_values.3.decoder.value"
            var parts = name.Split('.');
            int at = int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture) * 2 + (parts[3] == "value" ? 1 : 0);
            return parts[2] == "decoder" ? at : -1 - at;
        }

        var beams = new List<Beam> { new(begin, 0) };
        var finished = new List<Beam>();
        var made = new List<OrtValue>();
        IDisposableReadOnlyCollection<OrtValue>? first = null, current = null;
        using var run = new RunOptions();
        try
        {
            using (var no = OrtValue.CreateTensorValueFromMemory(new[] { false }, [1]))
            using (var nothing = OrtValue.CreateTensorValueFromMemory(new float[past.Heads * past.Size], [1, past.Heads, 1, past.Size]))
            using (var idsValue = OrtValue.CreateTensorValueFromMemory(begin.ToArray(), [1, begin.Count]))
            using (var statesValue = OrtValue.CreateTensorValueFromMemory(hidden, [1, length, size]))
            using (var maskValue = OrtValue.CreateTensorValueFromMemory(mask, [1, length]))
            {
                var values = names.Select(n => n == "input_ids" ? idsValue : n == "use_cache_branch" ? no : n == "encoder_hidden_states" ? statesValue : n.Contains("mask") ? maskValue : nothing).ToList();
                first = current = decoder.Run(run, names, values, [.. own, .. theirs]);
            }
            using var yes = OrtValue.CreateTensorValueFromMemory(new[] { true }, [1]);
            // The text's part for as many candidates as are followed: made when first needed.
            var forRows = new Dictionary<int, (OrtValue States, OrtValue Mask, OrtValue[] Kept)>();
            int steps = begin.Count;
            for (int step = 0; step < maxLength; step++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int rows = beams.Count;
                if (!Advance(beams, finished, current[0].GetTensorDataAsSpan<float>(), steps, candidates, out var from))
                    break;
                int batch = beams.Count;
                if (!forRows.TryGetValue(batch, out var text))
                {
                    var states = OrtValue.CreateTensorValueFromMemory(Repeated(hidden, batch), [batch, length, size]);
                    var masks = OrtValue.CreateTensorValueFromMemory(Repeated(mask, batch), [batch, length]);
                    var keptOfText = new OrtValue[kept];
                    for (int k = 0; k < kept; k++)
                        keptOfText[k] = batch == 1 ? first[1 + kept + k] : Rows(first[1 + kept + k], 1, new int[batch]);
                    made.Add(states);
                    made.Add(masks);
                    if (batch > 1)
                        made.AddRange(keptOfText);
                    forRows[batch] = text = (states, masks, keptOfText);
                }
                // What it kept of the answer follows the candidates: each new one has what the one it carries on had.
                bool same = batch == rows && from.AsSpan().SequenceEqual(Enumerable.Range(0, rows).ToArray());
                var keptOfAnswer = new OrtValue[kept];
                var forThisPass = new List<OrtValue>();
                for (int k = 0; k < kept; k++)
                {
                    keptOfAnswer[k] = same ? current[1 + k] : Rows(current[1 + k], rows, from);
                    if (!same)
                        forThisPass.Add(keptOfAnswer[k]);
                }
                var last = new long[batch];
                for (int b = 0; b < batch; b++)
                    last[b] = beams[b].Tokens[^1];
                IDisposableReadOnlyCollection<OrtValue> next;
                using (var idsValue = OrtValue.CreateTensorValueFromMemory(last, [batch, 1]))
                {
                    var values = names.Select(n => n == "input_ids" ? idsValue : n == "use_cache_branch" ? yes : n == "encoder_hidden_states" ? text.States
                        : n.Contains("mask") ? text.Mask : Place(n) is >= 0 and var at ? keptOfAnswer[at] : text.Kept[-1 - Place(n)]).ToList();
                    next = decoder.Run(run, names, values, own);
                }
                foreach (var value in forThisPass)
                    value.Dispose();
                if (!ReferenceEquals(current, first))
                    current.Dispose();
                current = next;
                steps = 1;
            }
            return Best(beams, finished);
        }
        finally
        {
            foreach (var value in made)
                value.Dispose();
            if (current is not null && !ReferenceEquals(current, first))
                current.Dispose();
            first?.Dispose();
        }
    }

    /// <summary>Some rows of a tensor, in the order asked for, as a tensor of its own.</summary>
    private static OrtValue Rows(OrtValue value, int rows, int[] from)
    {
        var all = value.GetTensorDataAsSpan<float>();
        int each = all.Length / rows;
        var data = new float[from.Length * each];
        for (int i = 0; i < from.Length; i++)
            all.Slice(from[i] * each, each).CopyTo(data.AsSpan(i * each, each));
        var shape = value.GetTensorTypeAndShape().Shape;
        shape[0] = from.Length;
        return OrtValue.CreateTensorValueFromMemory(data, shape);
    }

    private static T[] Repeated<T>(T[] one, int times)
    {
        if (times == 1)
            return one;
        var all = new T[one.Length * times];
        for (int i = 0; i < times; i++)
            one.CopyTo(all, i * one.Length);
        return all;
    }

    private static IEnumerable<int> Top(float[] values, int count)
    {
        var best = new List<int>(count + 1);
        for (int i = 0; i < values.Length; i++)
        {
            if (best.Count < count || values[i] > values[best[^1]])
            {
                int at = best.FindIndex(b => values[i] > values[b]);
                best.Insert(at < 0 ? best.Count : at, i);
                if (best.Count > count)
                    best.RemoveAt(count);
            }
        }
        return best;
    }
}
