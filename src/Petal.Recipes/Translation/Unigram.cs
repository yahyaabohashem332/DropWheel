using System.IO;
using System.Text;

namespace Petal.Recipes.Translation;

/// <summary>
/// A SentencePiece unigram tokenizer, read straight from a .spm model file: text is normalized
/// (NFKC, single spaces shown as "▁", one in front), then split into the vocabulary pieces whose
/// scores add up best (Viterbi). Matches SentencePiece's own segmentation for Marian models. A model trained as BPE
/// (M2M100's) is split SentencePiece's other way: from single letters, always joining the pair that scores best.
/// </summary>
internal sealed class Unigram
{
    private const char Space = '▁';
    private readonly Dictionary<string, float> _pieces = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _ids = new(StringComparer.Ordinal);
    private readonly int _longest;
    private readonly float _unknownScore;
    private readonly bool _bpe;

    public Unigram(byte[] model)
    {
        float lowest = 0;
        // ModelProto: field 1 is a repeated SentencePiece { 1: piece, 2: score, 3: type }.
        int at = 0;
        while (at < model.Length)
        {
            var (field, wire) = Key(model, ref at);
            if (field == 1 && wire == 2)
            {
                int length = (int)Varint(model, ref at);
                ReadPiece(model.AsSpan(at, length));
                at += length;
            }
            else if (field == 2 && wire == 2)
            {
                // TrainerSpec: field 3 is the kind of model, and 2 is BPE.
                int length = (int)Varint(model, ref at);
                var spec = model.AsSpan(at, length).ToArray();
                int i = 0;
                while (i < spec.Length)
                {
                    var (inner, kind) = Key(spec, ref i);
                    if (inner == 3 && kind == 0)
                        _bpe = Varint(spec, ref i) == 2;
                    else
                        Skip(spec, ref i, kind);
                }
                at += length;
            }
            else
            {
                Skip(model, ref at, wire);
            }
        }
        _longest = _pieces.Keys.Max(p => p.Length);
        _unknownScore = lowest - 10;

        void ReadPiece(ReadOnlySpan<byte> data)
        {
            string? piece = null;
            float score = 0;
            int type = 1;
            int i = 0;
            var bytes = data.ToArray();
            while (i < bytes.Length)
            {
                var (field, wire) = Key(bytes, ref i);
                if (field == 1 && wire == 2)
                {
                    int length = (int)Varint(bytes, ref i);
                    piece = Encoding.UTF8.GetString(bytes, i, length);
                    i += length;
                }
                else if (field == 2 && wire == 5)
                {
                    score = BitConverter.ToSingle(bytes, i);
                    i += 4;
                }
                else if (field == 3 && wire == 0)
                {
                    type = (int)Varint(bytes, ref i);
                }
                else
                {
                    Skip(bytes, ref i, wire);
                }
            }
            if (piece is not null)
                _ids.TryAdd(piece, _ids.Count);
            // Normal and user-defined pieces take part in splitting; control, unknown and byte pieces don't.
            if (piece is not null && type is 1 or 4)
            {
                _pieces[piece] = score;
                lowest = Math.Min(lowest, score);
            }
        }
    }

    private static (int Field, int Wire) Key(byte[] data, ref int at)
    {
        var key = Varint(data, ref at);
        return ((int)(key >> 3), (int)(key & 7));
    }

    private static ulong Varint(byte[] data, ref int at)
    {
        ulong value = 0;
        for (int shift = 0; ; shift += 7)
        {
            byte b = data[at++];
            value |= (ulong)(b & 0x7F) << shift;
            if (b < 0x80)
                return value;
        }
    }

    private static void Skip(byte[] data, ref int at, int wire)
    {
        switch (wire)
        {
            case 0: Varint(data, ref at); break;
            case 1: at += 8; break;
            case 2:
                int length = (int)Varint(data, ref at);
                at += length;
                break;
            case 5: at += 4; break;
            default: throw new InvalidDataException("The tokenizer model is damaged.");
        }
    }

    /// <summary>A piece's number in the model (its place among them, from 0), or -1 for a piece it doesn't have.</summary>
    public int IdOf(string piece) => _ids.TryGetValue(piece, out var id) ? id : -1;

    /// <summary>NFKC, whitespace collapsed, "▁" for each space and one in front.</summary>
    internal static string Normalize(string text)
    {
        var folded = text.Normalize(NormalizationForm.FormKC);
        var sb = new StringBuilder(folded.Length + 1);
        sb.Append(Space);
        bool space = false;
        foreach (var c in folded.Trim())
        {
            if (char.IsWhiteSpace(c))
            {
                space = true;
                continue;
            }
            if (space)
                sb.Append(Space);
            space = false;
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>The pieces that score best overall; characters no piece covers stand alone (as unknown).</summary>
    /// <summary>BPE: from single letters, the neighbouring pair whose joined piece scores best is joined, until none can be.</summary>
    private List<string> Merge(string s)
    {
        var symbols = new List<string>(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            bool pair = char.IsHighSurrogate(s[i]) && i + 1 < s.Length;
            symbols.Add(pair ? s.Substring(i, 2) : s[i].ToString());
            if (pair)
                i++;
        }
        while (true)
        {
            int best = -1;
            float score = float.NegativeInfinity;
            for (int i = 0; i + 1 < symbols.Count; i++)
                if (_pieces.TryGetValue(symbols[i] + symbols[i + 1], out var joined) && joined > score)
                {
                    best = i;
                    score = joined;
                }
            if (best < 0)
                return symbols;
            symbols[best] += symbols[best + 1];
            symbols.RemoveAt(best + 1);
        }
    }

    public List<string> Split(string text)
    {
        var s = Normalize(text);
        if (_bpe)
            return Merge(s);
        int n = s.Length;
        var best = new float[n + 1];
        var from = new int[n + 1];
        Array.Fill(best, float.NegativeInfinity);
        best[0] = 0;
        for (int end = 1; end <= n; end++)
        {
            for (int start = Math.Max(0, end - _longest); start < end; start++)
            {
                if (float.IsNegativeInfinity(best[start]))
                    continue;
                float score;
                if (_pieces.TryGetValue(s[start..end], out var known))
                    score = known;
                else if (end - start == 1 || end - start == 2 && char.IsSurrogatePair(s[start], s[start + 1]))
                    score = _unknownScore;
                else
                    continue;
                if (best[start] + score > best[end])
                {
                    best[end] = best[start] + score;
                    from[end] = start;
                }
            }
        }
        var pieces = new List<string>();
        for (int end = n; end > 0; end = from[end])
            pieces.Add(s[from[end]..end]);
        pieces.Reverse();
        return pieces;
    }
}
