using System.IO;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Petal.Core.Models;
using Petal.Recipes.Translation;

namespace Petal.Recipes.Questions;

/// <summary>
/// multilingual-e5-small on this PC (on the processor): a text as 384 numbers that say what it means, so the passages
/// of a file that speak to a question are found by meaning and not only by its words, in Arabic, English and about a
/// hundred other languages, and across them. A question is read as "query: …", a passage as "passage: …", as it was
/// trained; the numbers are the mean of what it makes of each piece, scaled to length 1, so two texts' likeness is
/// their dot product.
/// </summary>
internal sealed class PassageModel : IDisposable
{
    public const int Size = 384;

    /// <summary>The most pieces read of a text (a passage of about a hundred words is well under).</summary>
    private const int Longest = 256;

    private const int Batch = 16;

    // XLM-R's numbers: its SentencePiece pieces one place on, after <s>, <pad>, </s> and <unk>.
    private const long Start = 0, Pad = 1, End = 2, Unknown = 3;

    private readonly InferenceSession _session;
    private readonly Unigram _pieces;

    public static IReadOnlyList<ModelInfo> Models { get; } = [ModelCatalog.Passages, ModelCatalog.PassagesVocabulary];

    public static bool Ready => Models.All(ModelStore.IsAvailable);

    public PassageModel()
    {
        var options = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL, IntraOpNumThreads = Math.Clamp(Environment.ProcessorCount, 1, 8) };
        _pieces = new Unigram(File.ReadAllBytes(ModelStore.Find(ModelCatalog.PassagesVocabulary) ?? throw new FileNotFoundException("The passage model isn't downloaded yet.")));
        _session = new InferenceSession(ModelStore.Find(ModelCatalog.Passages) ?? throw new FileNotFoundException("The passage model isn't downloaded yet."), options);
    }

    /// <summary>A text's numbers for the model: &lt;s&gt;, its pieces (at most <see cref="Longest"/> in all), &lt;/s&gt;.</summary>
    internal long[] Ids(string text)
    {
        var ids = new List<long> { Start };
        foreach (var piece in _pieces.Split(text))
        {
            if (ids.Count == Longest - 1)
                break;
            int id = _pieces.IdOf(piece);
            ids.Add(id > 2 ? id + 1 : Unknown);
        }
        ids.Add(End);
        return ids.ToArray();
    }

    /// <summary>A question's meaning.</summary>
    public float[] Query(string question) => Embed(["query: " + question])[0];

    /// <summary>Passages' meanings, some at a time.</summary>
    public float[][] Passages(IReadOnlyList<string> passages, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var result = new float[passages.Count][];
        for (int from = 0; from < passages.Count; from += Batch)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var some = passages.Skip(from).Take(Batch).Select(p => "passage: " + p).ToList();
            var meanings = Embed(some);
            for (int i = 0; i < meanings.Length; i++)
                result[from + i] = meanings[i];
            progress?.Report(Math.Min(1, (from + some.Count) / (double)passages.Count));
        }
        return result;
    }

    private float[][] Embed(IReadOnlyList<string> texts)
    {
        var ids = texts.Select(Ids).ToList();
        int n = ids.Count, length = ids.Max(i => i.Length);
        var input = new DenseTensor<long>([n, length]);
        var mask = new DenseTensor<long>([n, length]);
        var types = new DenseTensor<long>([n, length]);
        for (int b = 0; b < n; b++)
        {
            for (int t = 0; t < length; t++)
            {
                bool there = t < ids[b].Length;
                input[b, t] = there ? ids[b][t] : Pad;
                mask[b, t] = there ? 1 : 0;
            }
        }
        using var outputs = _session.Run(
        [
            NamedOnnxValue.CreateFromTensor("input_ids", input),
            NamedOnnxValue.CreateFromTensor("attention_mask", mask),
            NamedOnnxValue.CreateFromTensor("token_type_ids", types),
        ]);
        var hidden = outputs.First().AsTensor<float>();
        var result = new float[n][];
        for (int b = 0; b < n; b++)
        {
            var mean = new float[Size];
            int count = ids[b].Length;
            for (int t = 0; t < count; t++)
                for (int k = 0; k < Size; k++)
                    mean[k] += hidden[b, t, k];
            double norm = 0;
            for (int k = 0; k < Size; k++)
                norm += mean[k] * (double)mean[k];
            float scale = (float)(1 / Math.Sqrt(Math.Max(norm, 1e-12)));
            for (int k = 0; k < Size; k++)
                mean[k] *= scale;
            result[b] = mean;
        }
        return result;
    }

    /// <summary>How alike two meanings are (1 the same, 0 unrelated).</summary>
    public static float Likeness(float[] a, float[] b)
    {
        float sum = 0;
        for (int k = 0; k < a.Length; k++)
            sum += a[k] * b[k];
        return sum;
    }

    public void Dispose() => _session.Dispose();
}
