using System.Buffers.Binary;
using System.Globalization;
using System.Numerics.Tensors;
using System.Text;
using System.Text.Json;
using ImageMagick;
using Microsoft.ML.OnnxRuntime;
using Petal.Core.Models;

namespace Petal.Engines.Imaging;

/// <summary>
/// What pictures show and what words mean, as 512 numbers each, in one shared space: CLIP
/// ViT-B/32 for pictures and a multilingual text model trained into the same space (so "قطط على
/// أريكة" finds the photo of cats on a sofa). Close vectors mean close meaning. Runs on the
/// processor, on this PC.
/// </summary>
public static class Meaning
{
    public const int Dimensions = 512;
    private const int Side = 224;
    private const int MaxTokens = 128;
    private static readonly float[] Mean = [0.48145466f, 0.4578275f, 0.40821073f];
    private static readonly float[] Std = [0.26862954f, 0.26130258f, 0.27577711f];

    public static IReadOnlyList<ModelInfo> Models { get; } =
        [ModelCatalog.ClipVision, ModelCatalog.ClipText, ModelCatalog.ClipTextProjection, ModelCatalog.ClipTextVocabulary];

    public static bool IsReady => Models.All(ModelStore.IsAvailable);

    private static readonly Lock Gate = new();
    private static InferenceSession? _vision, _text;
    private static float[]? _projection;
    private static WordPiece? _tokenizer;

    /// <summary>Threads each model may use: few, so indexing in the background stays out of the way.</summary>
    public static int Threads { get; set; } = Math.Clamp(Environment.ProcessorCount / 2, 1, 4);

    private static InferenceSession Session(ModelInfo model) =>
        new(ModelStore.Find(model) ?? throw new FileNotFoundException($"{model.Name} isn't downloaded yet."),
            new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL, IntraOpNumThreads = Threads });

    /// <summary>The picture's meaning, from its upright, centre-cropped middle.</summary>
    public static float[] EmbedImage(string path)
    {
        using var image = ImageLoader.Load(path);
        image.AutoOrient();
        return EmbedImage(image);
    }

    public static float[] EmbedImage(MagickImage source)
    {
        using var image = (MagickImage)source.Clone();
        ImageWriter.FlattenOnWhite(image);
        image.ColorSpace = ColorSpace.sRGB;
        // CLIP's preparation: the short side to 224 (bicubic), then the middle 224 × 224.
        double scale = (double)Side / Math.Min(image.Width, image.Height);
        image.FilterType = FilterType.Catrom;
        image.Resize(new MagickGeometry((uint)Math.Max(Side, Math.Round(image.Width * scale)), (uint)Math.Max(Side, Math.Round(image.Height * scale))) { IgnoreAspectRatio = true });
        image.Crop(new MagickGeometry((int)(image.Width - Side) / 2, (int)(image.Height - Side) / 2, Side, Side));
        image.ResetPage();
        var rgb = image.GetPixels().ToByteArray(PixelMapping.RGB) ?? throw new InvalidOperationException("The picture has no pixels.");
        var input = new float[3 * Side * Side];
        for (int i = 0; i < Side * Side; i++)
        {
            for (int c = 0; c < 3; c++)
                input[c * Side * Side + i] = (rgb[i * 3 + c] / 255f - Mean[c]) / Std[c];
        }
        InferenceSession session;
        lock (Gate)
            session = _vision ??= Session(ModelCatalog.ClipVision);
        using var tensor = OrtValue.CreateTensorValueFromMemory(input, [1, 3, Side, Side]);
        var output = session.OutputMetadata.Keys.FirstOrDefault(k => k.Contains("embeds", StringComparison.OrdinalIgnoreCase)) ?? session.OutputMetadata.Keys.First();
        using var run = new RunOptions();
        using var results = session.Run(run, [session.InputMetadata.Keys.First()], [tensor], [output]);
        return Normalized(results[0].GetTensorDataAsSpan<float>().ToArray());
    }

    /// <summary>The meaning of a short text (a search, a caption), in any of about 50 languages.</summary>
    public static float[] EmbedText(string text)
    {
        InferenceSession session;
        WordPiece tokenizer;
        float[] projection;
        lock (Gate)
        {
            session = _text ??= Session(ModelCatalog.ClipText);
            tokenizer = _tokenizer ??= new WordPiece(File.ReadAllLines(ModelStore.Find(ModelCatalog.ClipTextVocabulary)!, Encoding.UTF8));
            projection = _projection ??= ReadProjection(ModelStore.Find(ModelCatalog.ClipTextProjection)!);
        }
        var ids = tokenizer.Encode(text, MaxTokens);
        int n = ids.Length;
        var mask = Enumerable.Repeat(1L, n).ToArray();
        using var idsTensor = OrtValue.CreateTensorValueFromMemory(ids, [1, n]);
        using var maskTensor = OrtValue.CreateTensorValueFromMemory(mask, [1, n]);
        var inputs = session.InputMetadata.Keys.ToList();
        var values = inputs.Select(name => name.Contains("mask", StringComparison.OrdinalIgnoreCase) ? maskTensor : idsTensor).ToList();
        using var run = new RunOptions();
        using var results = session.Run(run, inputs, values, [session.OutputMetadata.Keys.First()]);
        var tokens = results[0].GetTensorDataAsSpan<float>();
        int width = tokens.Length / n;

        // Mean of the token vectors, then the 768 → 512 projection into CLIP's space.
        var pooled = new float[width];
        for (int t = 0; t < n; t++)
        {
            for (int d = 0; d < width; d++)
                pooled[d] += tokens[t * width + d] / n;
        }
        var embedded = new float[Dimensions];
        for (int o = 0; o < Dimensions; o++)
            embedded[o] = TensorPrimitives.Dot(projection.AsSpan(o * width, width), pooled);
        return Normalized(embedded);
    }

    /// <summary>Cosine similarity of two normalized vectors: about 0.2 to 0.35 for a picture and its description.</summary>
    public static float Similarity(ReadOnlySpan<float> a, ReadOnlySpan<float> b) => TensorPrimitives.Dot(a, b);

    private static float[] Normalized(float[] vector)
    {
        float length = TensorPrimitives.Norm(vector);
        if (length > 0)
            TensorPrimitives.Divide(vector, length, vector);
        return vector;
    }

    /// <summary>The projection's weights (512 rows of 768) from a safetensors file.</summary>
    private static float[] ReadProjection(string path)
    {
        var bytes = File.ReadAllBytes(path);
        long headerLength = BinaryPrimitives.ReadInt64LittleEndian(bytes);
        using var header = JsonDocument.Parse(bytes.AsMemory(8, (int)headerLength));
        var weight = header.RootElement.EnumerateObject().First(p => p.Name != "__metadata__").Value;
        if (weight.GetProperty("dtype").GetString() != "F32")
            throw new InvalidDataException("The projection isn't stored as 32-bit floats.");
        var offsets = weight.GetProperty("data_offsets");
        int start = 8 + (int)headerLength + offsets[0].GetInt32(), end = 8 + (int)headerLength + offsets[1].GetInt32();
        var values = new float[(end - start) / 4];
        Buffer.BlockCopy(bytes, start, values, 0, end - start);
        return values;
    }

    /// <summary>Unloads the models (they hold a few hundred MB).</summary>
    public static void Release()
    {
        lock (Gate)
        {
            _vision?.Dispose();
            _text?.Dispose();
            _vision = _text = null;
        }
    }
}

/// <summary>BERT's WordPiece tokenizer, cased (multilingual BERT): words split at spaces and punctuation, then into vocabulary pieces.</summary>
internal sealed class WordPiece
{
    private readonly Dictionary<string, long> _vocabulary;
    private readonly long _unknown, _start, _end;

    public WordPiece(IEnumerable<string> vocabulary)
    {
        _vocabulary = new Dictionary<string, long>(StringComparer.Ordinal);
        long id = 0;
        foreach (var token in vocabulary)
            _vocabulary.TryAdd(token, id++);
        _unknown = _vocabulary["[UNK]"];
        _start = _vocabulary["[CLS]"];
        _end = _vocabulary["[SEP]"];
    }

    public long[] Encode(string text, int maxTokens)
    {
        var ids = new List<long> { _start };
        foreach (var word in Words(text))
        {
            foreach (var piece in Pieces(word))
            {
                if (ids.Count >= maxTokens - 1)
                    break;
                ids.Add(piece);
            }
        }
        ids.Add(_end);
        return ids.ToArray();
    }

    private static IEnumerable<string> Words(string text)
    {
        var current = new StringBuilder();
        foreach (var c in text)
        {
            if (c == 0 || c == 0xFFFD || char.IsControl(c) && !char.IsWhiteSpace(c))
                continue;
            if (char.IsWhiteSpace(c))
            {
                if (current.Length > 0)
                    yield return current.ToString();
                current.Clear();
            }
            else if (IsPunctuation(c) || IsCjk(c))
            {
                if (current.Length > 0)
                    yield return current.ToString();
                current.Clear();
                yield return c.ToString();
            }
            else
            {
                current.Append(c);
            }
        }
        if (current.Length > 0)
            yield return current.ToString();
    }

    private static bool IsPunctuation(char c) =>
        c is >= '!' and <= '/' or >= ':' and <= '@' or >= '[' and <= '`' or >= '{' and <= '~'
        || char.GetUnicodeCategory(c) is UnicodeCategory.ConnectorPunctuation or UnicodeCategory.DashPunctuation
            or UnicodeCategory.OpenPunctuation or UnicodeCategory.ClosePunctuation or UnicodeCategory.InitialQuotePunctuation
            or UnicodeCategory.FinalQuotePunctuation or UnicodeCategory.OtherPunctuation;

    private static bool IsCjk(char c) => c is >= '\u4E00' and <= '\u9FFF' or >= '\u3400' and <= '\u4DBF' or >= '\uF900' and <= '\uFAFF';

    /// <summary>Longest vocabulary piece first, then "##" continuations; an unsplittable word is [UNK].</summary>
    private IEnumerable<long> Pieces(string word)
    {
        if (word.Length > 100)
        {
            yield return _unknown;
            yield break;
        }
        var pieces = new List<long>();
        int start = 0;
        while (start < word.Length)
        {
            long found = -1;
            int end = word.Length;
            for (; end > start; end--)
            {
                var piece = (start > 0 ? "##" : "") + word[start..end];
                if (_vocabulary.TryGetValue(piece, out var id))
                {
                    found = id;
                    break;
                }
            }
            if (found < 0)
            {
                yield return _unknown;
                yield break;
            }
            pieces.Add(found);
            start = end;
        }
        foreach (var piece in pieces)
            yield return piece;
    }
}
