using System.Text;

namespace Petal.Engines.Documents.Text;

/// <summary>
/// Just enough of the Unicode bidirectional algorithm and Arabic shaping to draw Arabic and
/// Hebrew with PDFsharp, which places glyphs left to right and does no shaping of its own.
/// Text is converted to Arabic presentation forms, then put in visual order line by line.
/// </summary>
internal static class Bidi
{
    private enum Joining { None, Right, Dual, Causing }

    private readonly record struct Forms(char Isolated, char Final, char Initial, char Medial, Joining Type);

    private static readonly Dictionary<char, Forms> ArabicForms = BuildForms();

    private static Dictionary<char, Forms> BuildForms()
    {
        var map = new Dictionary<char, Forms>();
        void R(char c, int iso) => map[c] = new((char)iso, (char)(iso + 1), (char)iso, (char)(iso + 1), Joining.Right);
        void D(char c, int iso) => map[c] = new((char)iso, (char)(iso + 1), (char)(iso + 2), (char)(iso + 3), Joining.Dual);

        map['ء'] = new('ﺀ', 'ﺀ', 'ﺀ', 'ﺀ', Joining.None);
        R('آ', 0xFE81); R('أ', 0xFE83); R('ؤ', 0xFE85); R('إ', 0xFE87);
        D('ئ', 0xFE89); R('ا', 0xFE8D); D('ب', 0xFE8F); R('ة', 0xFE93);
        D('ت', 0xFE95); D('ث', 0xFE99); D('ج', 0xFE9D); D('ح', 0xFEA1);
        D('خ', 0xFEA5); R('د', 0xFEA9); R('ذ', 0xFEAB); R('ر', 0xFEAD);
        R('ز', 0xFEAF); D('س', 0xFEB1); D('ش', 0xFEB5); D('ص', 0xFEB9);
        D('ض', 0xFEBD); D('ط', 0xFEC1); D('ظ', 0xFEC5); D('ع', 0xFEC9);
        D('غ', 0xFECD); D('ف', 0xFED1); D('ق', 0xFED5); D('ك', 0xFED9);
        D('ل', 0xFEDD); D('م', 0xFEE1); D('ن', 0xFEE5); D('ه', 0xFEE9);
        R('و', 0xFEED); R('ى', 0xFEEF); D('ي', 0xFEF1);
        // Persian and Urdu letters live in Presentation Forms-A.
        R('ٱ', 0xFB50); D('پ', 0xFB56); D('چ', 0xFB7A); R('ژ', 0xFB8A);
        D('ک', 0xFB8E); D('گ', 0xFB92); R('ۀ', 0xFBA4); D('ی', 0xFBFC);
        map['ـ'] = new('ـ', 'ـ', 'ـ', 'ـ', Joining.Causing);
        return map;
    }

    private static bool IsTransparent(char c) =>
        c is >= '\u064B' and <= '\u065F' or '\u0670' or >= '\u06D6' and <= '\u06ED';

    public static bool IsRtlChar(char c) =>
        c is >= '\u0590' and <= '\u08FF' && !IsArabicDigit(c) ||
        c is >= '\uFB1D' and <= '\uFDFF' ||
        c is >= '\uFE70' and <= '\uFEFF';

    private static bool IsArabicDigit(char c) => c is >= '\u0660' and <= '\u0669' or >= '\u06F0' and <= '\u06F9';

    private static bool IsDigit(char c) => c is >= '0' and <= '9' || IsArabicDigit(c);

    public static bool ContainsRtl(string text)
    {
        foreach (var c in text)
            if (IsRtlChar(c))
                return true;
        return false;
    }

    /// <summary>True when the first strong character is right-to-left.</summary>
    public static bool IsRtlParagraph(string text)
    {
        foreach (var c in text)
        {
            if (IsRtlChar(c)) return true;
            if (char.IsLetter(c)) return false;
        }
        return false;
    }

    /// <summary>Scripts that need shaping or reordering a plain PDF text renderer cannot do well.</summary>
    public static bool NeedsComplexLayout(string text)
    {
        foreach (var c in text)
        {
            if (IsRtlChar(c)) return true;
            // Devanagari through Malayalam, Sinhala, Thai, Lao, Tibetan, Myanmar, Khmer.
            if (c is >= '\u0900' and <= '\u0DFF' or >= '\u0E00' and <= '\u0FFF' or >= '\u1000' and <= '\u109F' or >= '\u1780' and <= '\u17FF')
                return true;
        }
        return false;
    }

    /// <summary>Replaces Arabic letters with their contextual presentation forms (logical order is kept).</summary>
    public static string Shape(string text)
    {
        if (!text.Any(c => ArabicForms.ContainsKey(c)))
            return text;
        var sb = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (!ArabicForms.TryGetValue(c, out var forms))
            {
                sb.Append(c);
                continue;
            }

            var prev = Neighbor(text, i, -1);
            var next = Neighbor(text, i, +1);
            bool joinsPrev = prev is { } p && ArabicForms.TryGetValue(p.Char, out var pf) && pf.Type is Joining.Dual or Joining.Causing && forms.Type != Joining.None;

            // Lam followed by alef becomes one ligature glyph.
            if (c == 'ل' && next is { } n && LamAlef(n.Char) is { } lig)
            {
                sb.Append(joinsPrev ? (char)(lig + 1) : (char)lig);
                // Copy any marks between lam and alef, then skip the alef.
                for (var k = i + 1; k < n.Index; k++)
                    sb.Append(text[k]);
                i = n.Index;
                continue;
            }

            bool joinsNext = next is { } nx && ArabicForms.TryGetValue(nx.Char, out var nf) && nf.Type != Joining.None &&
                             forms.Type is Joining.Dual or Joining.Causing;
            sb.Append((joinsPrev, joinsNext) switch
            {
                (true, true) => forms.Medial,
                (true, false) => forms.Final,
                (false, true) => forms.Initial,
                _ => forms.Isolated,
            });
        }
        return sb.ToString();
    }

    private static int? LamAlef(char alef) => alef switch
    {
        'آ' => 0xFEF5,
        'أ' => 0xFEF7,
        'إ' => 0xFEF9,
        'ا' => 0xFEFB,
        _ => null,
    };

    private static (char Char, int Index)? Neighbor(string text, int i, int step)
    {
        for (var j = i + step; j >= 0 && j < text.Length; j += step)
        {
            if (IsTransparent(text[j]))
                continue;
            return (text[j], j);
        }
        return null;
    }

    private enum Cls : byte { L, R, D, N }

    /// <summary>
    /// Shapes and reorders one line for left-to-right glyph placement.
    /// <paramref name="rtlParagraph"/> defaults to the direction of the first strong character.
    /// </summary>
    public static string ToVisual(string line, bool? rtlParagraph = null) =>
        ContainsRtl(line) ? FixMarks(VisualOrder(Shape(line), rtlParagraph)) : line;

    /// <summary>
    /// Visual order without shaping: what a PDF viewer reads back as the original text after its
    /// own right-to-left handling. Used for invisible text, where letter forms don't matter.
    /// </summary>
    public static string Reorder(string line, bool? rtlParagraph = null) =>
        ContainsRtl(line) ? VisualOrder(line, rtlParagraph) : line;

    private static string VisualOrder(string shaped, bool? rtlParagraph)
    {
        var rtl = rtlParagraph ?? IsRtlParagraph(shaped);
        var n = shaped.Length;
        var cls = new Cls[n];
        for (var i = 0; i < n; i++)
        {
            var c = shaped[i];
            cls[i] = IsRtlChar(c) ? Cls.R : IsDigit(c) ? Cls.D : char.IsLetter(c) ? Cls.L : Cls.N;
        }

        // Separators inside numbers ("3.14", "1,000", "12:30") belong to the number.
        for (var i = 1; i < n - 1; i++)
            if (cls[i] == Cls.N && shaped[i] is '.' or ',' or ':' or '/' && cls[i - 1] == Cls.D && cls[i + 1] == Cls.D)
                cls[i] = Cls.D;

        // W7: numbers after left-to-right text are left-to-right text.
        var lastStrong = rtl ? Cls.R : Cls.L;
        for (var i = 0; i < n; i++)
        {
            if (cls[i] is Cls.L or Cls.R) lastStrong = cls[i];
            else if (cls[i] == Cls.D && lastStrong == Cls.L) cls[i] = Cls.L;
        }

        // N1/N2: neutrals between the same direction take it, others take the paragraph's.
        var embedding = rtl ? Cls.R : Cls.L;
        for (var i = 0; i < n;)
        {
            if (cls[i] != Cls.N) { i++; continue; }
            var start = i;
            while (i < n && cls[i] == Cls.N) i++;
            var before = start == 0 ? embedding : Strength(cls[start - 1]);
            var after = i == n ? embedding : Strength(cls[i]);
            var resolved = before == after ? before : embedding;
            for (var k = start; k < i; k++) cls[k] = resolved;
        }

        // Levels: odd is right-to-left.
        var levels = new int[n];
        var baseLevel = rtl ? 1 : 0;
        for (var i = 0; i < n; i++)
        {
            levels[i] = (baseLevel, cls[i]) switch
            {
                (0, Cls.L) => 0,
                (0, Cls.R) => 1,
                (0, Cls.D) => 2,
                (1, Cls.R) => 1,
                (1, _) => 2,
                _ => baseLevel,
            };
        }

        var chars = shaped.ToCharArray();
        for (var i = 0; i < n; i++)
            if ((levels[i] & 1) == 1)
                chars[i] = Mirror(chars[i]);

        var order = Enumerable.Range(0, n).ToArray();
        var max = levels.Max();
        for (var level = max; level >= 1; level--)
        {
            for (var i = 0; i < n;)
            {
                if (levels[order[i]] < level) { i++; continue; }
                var start = i;
                while (i < n && levels[order[i]] >= level) i++;
                Array.Reverse(order, start, i - start);
            }
        }

        var sb = new StringBuilder(n);
        for (var i = 0; i < n; i++)
            sb.Append(chars[order[i]]);
        return sb.ToString();
    }

    private static Cls Strength(Cls c) => c == Cls.D ? Cls.R : c;

    /// <summary>Combining marks were placed before their base by the reversal; puts them back after it.</summary>
    private static string FixMarks(string visual)
    {
        if (!visual.Any(IsTransparent))
            return visual;
        var chars = visual.ToCharArray();
        for (var i = 0; i < chars.Length - 1; i++)
        {
            if (!IsTransparent(chars[i])) continue;
            var j = i;
            while (j < chars.Length && IsTransparent(chars[j])) j++;
            if (j < chars.Length)
            {
                var baseChar = chars[j];
                Array.Copy(chars, i, chars, i + 1, j - i);
                chars[i] = baseChar;
            }
            i = j;
        }
        return new string(chars);
    }

    private static char Mirror(char c) => c switch
    {
        '(' => ')', ')' => '(',
        '[' => ']', ']' => '[',
        '{' => '}', '}' => '{',
        '<' => '>', '>' => '<',
        '«' => '»', '»' => '«',
        _ => c,
    };

    /// <summary>
    /// Undoes presentation forms and visual order for text pulled out of PDFs that store
    /// right-to-left text visually. Used on extracted words, where word order is handled by layout.
    /// </summary>
    public static string NormalizeExtracted(string word)
    {
        if (!word.Any(c => c is >= '\uFB50' and <= '\uFEFF'))
            return word;
        return word.Normalize(NormalizationForm.FormKC);
    }
}
