using System.IO;
using System.Text;
using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Petal.Core.Models;

namespace Petal.Recipes.Translation;

/// <summary>
/// English to Arabic, on this PC: Opus-MT (Helsinki-NLP, Apache-2.0), a translation model made for
/// this pair, with beam search over four candidates. For subtitles and short texts.
/// </summary>
public static class Translator
{
    private const long Eos = 0, Pad = 62801;
    private const int Beams = 4;

    public static IReadOnlyList<ModelInfo> Models { get; } =
        [ModelCatalog.OpusEnArEncoder, ModelCatalog.OpusEnArDecoder, ModelCatalog.OpusEnArSource, ModelCatalog.OpusEnArVocabulary];

    public static bool IsReady => Models.All(ModelStore.IsAvailable);

    private static readonly Lock Gate = new();
    private static InferenceSession? _encoder, _decoder;
    private static Unigram? _source;
    private static Dictionary<string, long>? _vocabulary;
    private static string[]? _pieces;

    private static void Load()
    {
        lock (Gate)
        {
            if (_encoder is not null)
                return;
            string Find(ModelInfo model) => ModelStore.Find(model) ?? throw new FileNotFoundException("The translation model isn't downloaded yet.");
            var options = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL, IntraOpNumThreads = Math.Clamp(Environment.ProcessorCount, 1, 8) };
            _source = new Unigram(File.ReadAllBytes(Find(ModelCatalog.OpusEnArSource)));
            using var json = JsonDocument.Parse(File.ReadAllBytes(Find(ModelCatalog.OpusEnArVocabulary)));
            _vocabulary = json.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetInt64(), StringComparer.Ordinal);
            _pieces = new string[_vocabulary.Values.Max() + 1];
            foreach (var (piece, id) in _vocabulary)
                _pieces[id] = piece;
            _encoder = new InferenceSession(Find(ModelCatalog.OpusEnArEncoder), options);
            _decoder = new InferenceSession(Find(ModelCatalog.OpusEnArDecoder), options);
        }
    }

    /// <summary>The source pieces' ids: the target language mark, the text, end of sentence.</summary>
    internal static long[] Encode(string text)
    {
        Load();
        long unknown = _vocabulary!.GetValueOrDefault("<unk>", 1);
        var ids = new List<long> { _vocabulary[">>ara<<"] };
        ids.AddRange(_source!.Split(text).Take(250).Select(p => _vocabulary.GetValueOrDefault(p, unknown)));
        ids.Add(Eos);
        return ids.ToArray();
    }

    /// <summary>Each line in Arabic; empty lines stay empty.</summary>
    public static IReadOnlyList<string> ToArabic(IReadOnlyList<string> lines, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        Load();
        var result = new string[lines.Count];
        for (int i = 0; i < lines.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = lines[i].Trim();
            result[i] = line.Length == 0 || !line.Any(char.IsLetter) ? line : Translate(line);
            progress?.Report((i + 1.0) / lines.Count);
        }
        return result;
    }

    /// <summary>
    /// One line in Arabic. Quick follows only the likeliest word at each step (about four times less work): for
    /// a sentence still being said, whose words may change before it is translated properly.
    /// </summary>
    public static string ToArabic(string line, bool quick)
    {
        Load();
        line = line.Trim();
        return line.Length == 0 || !line.Any(char.IsLetter) ? line : Translate(line, quick ? 1 : Beams);
    }

    private static string Translate(string text, int width = Beams)
    {
        var ids = Encode(text);
        int length = ids.Length;
        var mask = Enumerable.Repeat(1L, length).ToArray();
        float[] hidden;
        using (var idsTensor = OrtValue.CreateTensorValueFromMemory(ids, [1, length]))
        using (var maskTensor = OrtValue.CreateTensorValueFromMemory(mask, [1, length]))
        using (var run = new RunOptions())
        using (var encoded = _encoder!.Run(run, ["input_ids", "attention_mask"], [idsTensor, maskTensor], [_encoder.OutputMetadata.Keys.First()]))
            hidden = encoded[0].GetTensorDataAsSpan<float>().ToArray();
        int size = hidden.Length / length;

        var beams = new List<(List<long> Tokens, double Score)> { ([Pad], 0) };
        var finished = new List<(List<long> Tokens, double Score)>();
        int maxLength = Math.Min(256, length * 3 + 10);
        for (int step = 0; step < maxLength && beams.Count > 0; step++)
        {
            int batch = beams.Count, steps = beams[0].Tokens.Count;
            var decoderIds = new long[batch * steps];
            for (int b = 0; b < batch; b++)
                beams[b].Tokens.CopyTo(decoderIds, b * steps);
            var encoderStates = new float[batch * hidden.Length];
            var encoderMask = new long[batch * length];
            for (int b = 0; b < batch; b++)
            {
                hidden.CopyTo(encoderStates, b * hidden.Length);
                mask.CopyTo(encoderMask, b * length);
            }
            using var idsValue = OrtValue.CreateTensorValueFromMemory(decoderIds, [batch, steps]);
            using var statesValue = OrtValue.CreateTensorValueFromMemory(encoderStates, [batch, length, size]);
            using var maskValue = OrtValue.CreateTensorValueFromMemory(encoderMask, [batch, length]);
            using var run = new RunOptions();
            var names = _decoder!.InputMetadata.Keys.ToList();
            var values = names.Select(n => n == "input_ids" ? idsValue : n.Contains("mask") ? maskValue : statesValue).ToList();
            using var outputs = _decoder.Run(run, names, values, ["logits"]);
            var logits = outputs[0].GetTensorDataAsSpan<float>();
            int vocabulary = logits.Length / (batch * steps);

            var candidates = new List<(List<long> Tokens, double Score)>();
            for (int b = 0; b < batch; b++)
            {
                var row = logits.Slice((b * steps + steps - 1) * vocabulary, vocabulary).ToArray();
                row[Pad] = float.NegativeInfinity;
                // Log-softmax, then the best few continuations of this beam.
                float max = row.Max();
                double sum = 0;
                foreach (var v in row)
                    sum += Math.Exp(v - max);
                double logSum = max + Math.Log(sum);
                foreach (int token in Top(row, width))
                    candidates.Add(([.. beams[b].Tokens, token], beams[b].Score + row[token] - logSum));
            }
            beams.Clear();
            foreach (var candidate in candidates.OrderByDescending(c => c.Score / c.Tokens.Count))
            {
                if (candidate.Tokens[^1] == Eos)
                    finished.Add(candidate);
                else if (beams.Count < width)
                    beams.Add(candidate);
                if (beams.Count == width)
                    break;
            }
            if (finished.Count >= width)
                break;
        }
        var best = (finished.Count > 0 ? finished : beams).MaxBy(c => c.Score / c.Tokens.Count).Tokens;
        var words = new StringBuilder();
        foreach (var token in best.Skip(1))
        {
            if (token is Eos or Pad || token >= _pieces!.Length || _pieces[token] is not { } piece || piece.StartsWith('<'))
                continue;
            words.Append(piece);
        }
        return words.ToString().Replace('▁', ' ').Trim();
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

    /// <summary>Subtitles in Arabic: the same cues and timing, the words translated.</summary>
    public static string TranslateSubtitles(string text, bool vtt, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var cues = Petal.Engines.Files.Subtitles.SubtitleConverter.Parse(text);
        // A cue's lines are one sentence more often than not: translated together.
        var arabic = ToArabic(cues.Select(c => string.Join(' ', c.Text.Split('\n').Select(StripTags))).ToList(), progress, cancellationToken);
        var translated = cues.Select((c, i) => c with { Text = arabic[i] }).ToList();
        return vtt ? Petal.Engines.Files.Subtitles.SubtitleConverter.WriteVtt(translated) : Petal.Engines.Files.Subtitles.SubtitleConverter.WriteSrt(translated);
    }

    /// <summary>Formatting tags (&lt;i&gt;, {\an8}) don't go through translation.</summary>
    private static string StripTags(string line) =>
        System.Text.RegularExpressions.Regex.Replace(line, @"<[^>]{1,20}>|\{\\[^}]{1,20}\}", "").Trim();
}
