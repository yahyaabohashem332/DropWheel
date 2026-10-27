using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Petal.Engines.Media;

/// <summary>
/// Chatterbox Multilingual's text tokenizer, as its Python package and tokenizer.json do it: lower case, Unicode NFKD (so
/// "أ" is alef and hamza, as the model learnt it), the language's tag in front ("[ar]"), spaces as "[SPACE]", the tags and
/// other added tokens kept whole, the rest split into words and punctuation and joined into byte-pair pieces. Around the
/// text go the tokens the model expects: the exaggeration slot and the start, then the end and twice the start of speech.
/// </summary>
internal sealed partial class ChatterboxTokenizer
{
    public const int Exaggeration = 6563;
    public const int Start = 255;
    public const int End = 0;
    public const int StartSpeech = 6561;
    public const int StopSpeech = 6562;
    private const int Unknown = 1;

    private readonly Dictionary<string, int> _vocab;
    private readonly Dictionary<(string, string), int> _ranks;
    private readonly List<(string Content, int Id)> _added;
    private readonly bool _lowerCase;

    private ChatterboxTokenizer(Dictionary<string, int> vocab, Dictionary<(string, string), int> ranks, List<(string, int)> added, bool lowerCase)
    {
        _vocab = vocab;
        _ranks = ranks;
        _added = added;
        _lowerCase = lowerCase;
    }

    public static ChatterboxTokenizer Load(string path)
    {
        using var json = JsonDocument.Parse(File.ReadAllBytes(path));
        var model = json.RootElement.GetProperty("model");
        var vocab = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var entry in model.GetProperty("vocab").EnumerateObject())
            vocab[entry.Name] = entry.Value.GetInt32();
        var ranks = new Dictionary<(string, string), int>();
        int rank = 0;
        foreach (var merge in model.GetProperty("merges").EnumerateArray())
        {
            // "t h", or ["t", "h"] in newer files.
            string a, b;
            if (merge.ValueKind == JsonValueKind.Array)
                (a, b) = (merge[0].GetString()!, merge[1].GetString()!);
            else
            {
                var parts = merge.GetString()!.Split(' ', 2);
                (a, b) = (parts[0], parts[1]);
            }
            ranks.TryAdd((a, b), rank++);
        }
        var added = new List<(string, int)>();
        foreach (var token in json.RootElement.GetProperty("added_tokens").EnumerateArray())
            added.Add((token.GetProperty("content").GetString()!, token.GetProperty("id").GetInt32()));
        // The longest first, so "[SPACE]" is never read as "[S…".
        added.Sort((x, y) => y.Item1.Length.CompareTo(x.Item1.Length));
        // The first exports lower-cased the text before NFKD (as the first Python package did); V3 models learnt full
        // case, and their tokenizer says so by doing NFKD itself.
        bool lowerCase = !json.RootElement.TryGetProperty("normalizer", out var normalizer) || !normalizer.GetRawText().Contains("\"NFKD\"", StringComparison.Ordinal);
        return new ChatterboxTokenizer(vocab, ranks, added, lowerCase);
    }

    /// <summary>The text prepared as the Python tokenizer prepares it, before it is split.</summary>
    internal string Prepare(string text, string language)
    {
        var plain = WhiteSpace().Replace(text.Trim(), " ");
        if (_lowerCase)
            plain = plain.ToLowerInvariant();
        plain = plain.Normalize(NormalizationForm.FormKD);
        return ($"[{language.ToLowerInvariant()}]" + plain).Replace(" ", "[SPACE]");
    }

    /// <summary>The model's input ids for a text in a language ("ar", "en"…).</summary>
    public long[] Encode(string text, string language)
    {
        var ids = new List<long> { Exaggeration, Start };
        foreach (var (piece, id) in SplitAdded(Prepare(text, language)))
        {
            if (id >= 0)
            {
                ids.Add(id);
                continue;
            }
            foreach (Match word in Words().Matches(piece))
                foreach (var symbol in Merge(word.Value))
                    ids.Add(_vocab.TryGetValue(symbol, out var known) ? known : Unknown);
        }
        ids.AddRange([End, StartSpeech, StartSpeech]);
        return [.. ids];
    }

    /// <summary>The text cut around its added tokens ("[ar]", "[SPACE]"): those carry their id, the rest -1.</summary>
    private IEnumerable<(string Piece, int Id)> SplitAdded(string text)
    {
        var plain = new StringBuilder();
        for (int i = 0; i < text.Length;)
        {
            int found = -1, length = 0;
            if (text[i] == '[')
                foreach (var (content, id) in _added)
                    if (string.CompareOrdinal(text, i, content, 0, content.Length) == 0)
                    {
                        found = id;
                        length = content.Length;
                        break;
                    }
            if (found < 0)
            {
                plain.Append(text[i++]);
                continue;
            }
            if (plain.Length > 0)
            {
                yield return (plain.ToString(), -1);
                plain.Clear();
            }
            yield return ("", found);
            i += length;
        }
        if (plain.Length > 0)
            yield return (plain.ToString(), -1);
    }

    /// <summary>A word's letters joined pair by pair, the earliest-learnt pair first, as byte-pair encoding does.</summary>
    internal List<string> Merge(string word)
    {
        var symbols = new List<string>(word.Length);
        // One symbol per character (a combining mark stays its own, as the vocabulary has it).
        foreach (var c in word)
            symbols.Add(c.ToString());
        while (symbols.Count > 1)
        {
            int best = -1, bestRank = int.MaxValue;
            for (int i = 0; i + 1 < symbols.Count; i++)
                if (_ranks.TryGetValue((symbols[i], symbols[i + 1]), out var rank) && rank < bestRank)
                {
                    bestRank = rank;
                    best = i;
                }
            if (best < 0)
                break;
            symbols[best] += symbols[best + 1];
            symbols.RemoveAt(best + 1);
        }
        return symbols;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhiteSpace();

    /// <summary>The Whitespace pre-tokenizer: runs of word characters, and runs of anything else but spaces.</summary>
    [GeneratedRegex(@"\w+|[^\w\s]+")]
    private static partial Regex Words();
}
