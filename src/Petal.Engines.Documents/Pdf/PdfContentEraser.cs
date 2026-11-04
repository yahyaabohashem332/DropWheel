using System.Globalization;
using System.Text;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;
using UglyToad.PdfPig;
using Encodings = UglyToad.PdfPig.Fonts.Encodings;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using PdfDocument = PdfSharp.Pdf.PdfDocument;
using PigDocument = UglyToad.PdfPig.PdfDocument;

namespace Petal.Engines.Documents.Pdf;

internal enum EraseMode
{
    /// <summary>Retyping and translating: only letters go; a picture or a drawing under them stays.</summary>
    TextOnly,

    /// <summary>Redacting: letters go, and the page fails if a picture or a drawing lies under a box, for those can't be cut here.</summary>
    Everything,
}

/// <summary>
/// Takes letters out of a page's content stream, where they lie in given boxes, and leaves every other letter exactly where it
/// was. The stream is read the way a reader would: its transformations, fonts and widths give each glyph's place, and the
/// glyphs in a box leave their text-showing operators, replaced by the same amount of space. Nothing here is trusted: PdfPig
/// reads the page again afterwards, and a page with a letter left in a box, or a letter gone or moved outside the boxes, counts
/// as failed and stays as it came in. What can't be read (a text in a Form XObject, a font without widths) fails the same way.
/// </summary>
internal static class PdfContentEraser
{
    /// <summary>A letter's height around its baseline, in sizes of the letter, as <see cref="PdfContent"/> reckons it.</summary>
    internal const double Ascent = 0.78, Descent = 0.22;

    /// <summary>The PDF with the letters out, and the pages where that could not be done reliably, which are unchanged.</summary>
    public static (byte[] Pdf, HashSet<int> Failed) Erase(byte[] pdf, IReadOnlyDictionary<int, List<PdfArea>> boxes, EraseMode mode, CancellationToken cancellationToken = default)
    {
        var failed = new HashSet<int>();
        Dictionary<int, List<PlacedLetter>> before;
        try
        {
            before = PlacedLetter.Read(pdf, boxes.Keys);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
        {
            return (pdf, boxes.Keys.ToHashSet());
        }
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var todo = boxes.Keys.Where(p => !failed.Contains(p)).ToList();
            if (todo.Count == 0)
                return (pdf, failed);
            byte[] erased;
            using (var document = PdfSharpIO.Open(pdf, PdfDocumentOpenMode.Modify))
            {
                foreach (int index in todo)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (index < 0 || index >= document.PageCount || !TryRewrite(document.Pages[index], boxes[index], mode))
                        failed.Add(index);
                }
                erased = PdfSharpIO.SaveToBytes(document);
            }
            todo.RemoveAll(failed.Contains);
            Dictionary<int, List<PlacedLetter>> after;
            try
            {
                after = PlacedLetter.Read(erased, todo);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
            {
                failed.UnionWith(todo);
                continue;
            }
            var bad = todo.Where(p => !Clean(before[p], after[p], boxes[p])).ToList();
            if (bad.Count == 0)
                return (erased, failed);
            failed.UnionWith(bad);
        }
    }

    /// <summary>True when no letter is left in a box and every letter clear of the boxes is still where it was.</summary>
    private static bool Clean(List<PlacedLetter> before, List<PlacedLetter> after, List<PdfArea> boxes)
    {
        if (after.Any(l => l.IsIn(boxes)))
            return false;
        var clear = before.Where(l => !boxes.Any(b => l.Area.Overlaps(b.Inflated(1)))).ToList();
        var left = after.GroupBy(l => l.Value).ToDictionary(g => g.Key, g => g.ToList());
        foreach (var letter in clear)
        {
            if (!left.TryGetValue(letter.Value, out var same))
                return false;
            int at = same.FindIndex(l => Math.Abs(l.Area.Left - letter.Area.Left) <= 0.5 && Math.Abs(l.Area.Top - letter.Area.Top) <= 0.5);
            if (at < 0)
                return false;
            same.RemoveAt(at);
        }
        return true;
    }

    private static bool TryRewrite(PdfPage page, List<PdfArea> boxes, EraseMode mode)
    {
        try
        {
            var single = page.Contents.CreateSingleContent();
            var source = Encoding.Latin1.GetString(single.Stream?.UnfilteredValue ?? []);
            var rewritten = new ContentRewriter(source, page.Elements.GetDictionary("/Resources"), PdfPageSpace.Of(page), boxes, mode).Run();
            PdfWalk.SetContent(page, Encoding.Latin1.GetBytes(rewritten));
            return true;
        }
        catch (Exception ex) when (ex is ContentNotReadable or FormatException or ArgumentException or InvalidCastException or InvalidOperationException or IndexOutOfRangeException or NullReferenceException)
        {
            return false;
        }
    }
}

/// <summary>A page's content can't be followed reliably, or (redacting) holds something under a box that can't be cut.</summary>
internal sealed class ContentNotReadable(string why) : Exception(why);

/// <summary>One letter PdfPig reads on a page, with its rectangle as displayed.</summary>
internal sealed record PlacedLetter(string Value, PdfArea Area)
{
    /// <summary>Whether it counts as being in the boxes: its middle in one, or half of it under them.</summary>
    public bool IsIn(IEnumerable<PdfArea> boxes)
    {
        double x = Area.Left + Area.Width / 2, y = Area.Top + Area.Height / 2;
        foreach (var box in boxes)
        {
            if (box.Contains(x, y))
                return true;
            double wide = Math.Min(Area.Right, box.Right) - Math.Max(Area.Left, box.Left), high = Math.Min(Area.Bottom, box.Bottom) - Math.Max(Area.Top, box.Top);
            if (wide > 0 && high > 0 && wide * high >= 0.5 * Math.Max(0.01, Area.Width * Area.Height))
                return true;
        }
        return false;
    }

    public static Dictionary<int, List<PlacedLetter>> Read(byte[] pdf, IEnumerable<int> pages)
    {
        var read = new Dictionary<int, List<PlacedLetter>>();
        using var pig = PigDocument.Open(pdf, new ParsingOptions { UseLenientParsing = true, SkipMissingFonts = true });
        foreach (int index in pages)
            read[index] = index >= 0 && index < pig.NumberOfPages ? Of(pig.GetPage(index + 1)) : [];
        return read;
    }

    public static List<PlacedLetter> Of(UglyToad.PdfPig.Content.Page page)
    {
        var letters = new List<PlacedLetter>();
        foreach (var letter in page.Letters)
        {
            if (string.IsNullOrWhiteSpace(letter.Value))
                continue;
            letters.Add(new PlacedLetter(letter.Value, AreaOf(letter, page.Height)));
        }
        return letters;
    }

    /// <summary>The letter's rectangle whichever way it runs: from its start to its end along the baseline, as tall as a letter around it.</summary>
    internal static PdfArea AreaOf(UglyToad.PdfPig.Content.Letter letter, double pageHeight)
    {
        double size = letter.PointSize > 0.5 ? letter.PointSize : letter.FontSize > 0.5 ? letter.FontSize : Math.Max(1, letter.GlyphRectangle.Height);
        double sx = letter.StartBaseLine.X, sy = letter.StartBaseLine.Y, ex = letter.EndBaseLine.X, ey = letter.EndBaseLine.Y;
        double dx = ex - sx, dy = ey - sy, length = Math.Sqrt(dx * dx + dy * dy);
        // Up from the baseline; a letter with no width goes by the direction of its text, taken as level.
        double ux = length < 1e-6 ? 0 : -dy / length, uy = length < 1e-6 ? 1 : dx / length;
        double up = PdfContentEraser.Ascent * size, down = PdfContentEraser.Descent * size;
        double[] xs = [sx + ux * up, sx - ux * down, ex + ux * up, ex - ux * down];
        double[] ys = [sy + uy * up, sy - uy * down, ey + uy * up, ey - uy * down];
        return PdfArea.Between(xs.Min(), pageHeight - ys.Max(), xs.Max(), pageHeight - ys.Min());
    }
}

/// <summary>Follows one content stream and writes it out again with the glyphs in the boxes taken out of it.</summary>
internal sealed class ContentRewriter(string source, PdfDictionary? resources, PdfPageSpace space, List<PdfArea> boxes, EraseMode mode)
{
    private readonly StringBuilder _output = new(source.Length);
    private readonly Stack<GraphicsState> _saved = new();
    private readonly Dictionary<string, FontWidths> _fonts = [];
    private GraphicsState _state = new();
    private Matrix _textMatrix = Matrix.Identity, _lineMatrix = Matrix.Identity;
    private int _at;

    // The path being built, as displayed: its bounds, and whether it is one plain rectangle.
    private bool _hasPath;
    private double _pathLeft, _pathTop, _pathRight, _pathBottom;
    private int _rectangles, _otherSegments;

    private readonly record struct Matrix(double A, double B, double C, double D, double E, double F)
    {
        public static readonly Matrix Identity = new(1, 0, 0, 1, 0, 0);

        /// <summary>This one applied first, then <paramref name="m"/>.</summary>
        public Matrix Then(Matrix m) => new(
            A * m.A + B * m.C, A * m.B + B * m.D,
            C * m.A + D * m.C, C * m.B + D * m.D,
            E * m.A + F * m.C + m.E, E * m.B + F * m.D + m.F);

        public (double X, double Y) Apply(double x, double y) => (A * x + C * y + E, B * x + D * y + F);
    }

    private sealed record GraphicsState
    {
        public Matrix Ctm { get; init; } = Matrix.Identity;
        public FontWidths? Font { get; init; }
        public double Size { get; init; }
        public double CharSpace { get; init; }
        public double WordSpace { get; init; }
        public double Scale { get; init; } = 1;
        public double Leading { get; init; }
        public double Rise { get; init; }
    }

    private enum Kind { End, Number, Name, Text, Open, Close, Dictionary, Word }

    private readonly record struct Token(Kind Kind, double Number = 0, string Value = "", int End = 0);

    /// <summary>What an operator gets before it: numbers, names, strings, or an array of numbers and strings.</summary>
    private readonly record struct Operand(Kind Kind, double Number, string Value, List<Operand>? Items);

    public string Run()
    {
        var operands = new List<Operand>();
        int from = 0;
        while (true)
        {
            var token = Next();
            switch (token.Kind)
            {
                case Kind.End:
                    _output.Append(source, from, source.Length - from);
                    return _output.ToString();
                case Kind.Word:
                    if (token.Value == "BI")
                        InlineImage();
                    else if (!Operator(token.Value, operands, from))
                        _output.Append(source, from, _at - from);
                    operands.Clear();
                    from = _at;
                    break;
                case Kind.Open:
                    operands.Add(new Operand(Kind.Open, 0, "", Array()));
                    break;
                case Kind.Close:
                    throw new ContentNotReadable("a stray ]");
                default:
                    operands.Add(new Operand(token.Kind, token.Number, token.Value, null));
                    break;
            }
        }
    }

    private List<Operand> Array()
    {
        var items = new List<Operand>();
        while (true)
        {
            var token = Next();
            switch (token.Kind)
            {
                case Kind.Close:
                    return items;
                case Kind.End:
                    throw new ContentNotReadable("an array with no end");
                case Kind.Open:
                    items.Add(new Operand(Kind.Open, 0, "", Array()));
                    break;
                default:
                    items.Add(new Operand(token.Kind, token.Number, token.Value, null));
                    break;
            }
        }
    }

    // ---------- operators ----------

    private static double Number(List<Operand> operands, int fromEnd)
    {
        int at = operands.Count - fromEnd;
        if (at < 0 || operands[at].Kind != Kind.Number)
            throw new ContentNotReadable("an operator without its numbers");
        return operands[at].Number;
    }

    private static Matrix MatrixFrom(List<Operand> o) => new(Number(o, 6), Number(o, 5), Number(o, 4), Number(o, 3), Number(o, 2), Number(o, 1));

    /// <summary>Follows an operator. True when it wrote something of its own in place of what was there.</summary>
    private bool Operator(string op, List<Operand> o, int from)
    {
        switch (op)
        {
            case "q":
                _saved.Push(_state);
                break;
            case "Q":
                if (_saved.Count > 0)
                    _state = _saved.Pop();
                break;
            case "cm":
                _state = _state with { Ctm = MatrixFrom(o).Then(_state.Ctm) };
                break;
            case "BT":
                _textMatrix = _lineMatrix = Matrix.Identity;
                break;
            case "Tf":
                if (o.Count < 2 || o[^2].Kind != Kind.Name)
                    throw new ContentNotReadable("Tf without a font");
                _state = _state with { Font = Font(o[^2].Value), Size = Number(o, 1) };
                break;
            case "Tc":
                _state = _state with { CharSpace = Number(o, 1) };
                break;
            case "Tw":
                _state = _state with { WordSpace = Number(o, 1) };
                break;
            case "Tz":
                _state = _state with { Scale = Number(o, 1) / 100 };
                break;
            case "TL":
                _state = _state with { Leading = Number(o, 1) };
                break;
            case "Ts":
                _state = _state with { Rise = Number(o, 1) };
                break;
            case "Td":
                NextLine(Number(o, 2), Number(o, 1));
                break;
            case "TD":
                _state = _state with { Leading = -Number(o, 1) };
                NextLine(Number(o, 2), Number(o, 1));
                break;
            case "Tm":
                _textMatrix = _lineMatrix = MatrixFrom(o);
                break;
            case "T*":
                NextLine(0, -_state.Leading);
                break;
            case "Tj":
                return Show(o.Count > 0 ? [o[^1]] : [], "", from);
            case "TJ":
                return Show(o.Count > 0 && o[^1].Items is { } items ? items : [], "", from);
            case "'":
                NextLine(0, -_state.Leading);
                return Show(o.Count > 0 ? [o[^1]] : [], "T*\n", from);
            case "\"":
                _state = _state with { WordSpace = Number(o, 3), CharSpace = Number(o, 2) };
                NextLine(0, -_state.Leading);
                return Show(o.Count > 0 ? [o[^1]] : [], Invariant(o[^3].Number) + " Tw " + Invariant(o[^2].Number) + " Tc T*\n", from);
            case "BDC":
                // What a reader would copy for this text instead of its letters stays out of the file with them.
                if (o.Count >= 2 && o[^1].Kind == Kind.Dictionary && o[^2].Kind == Kind.Name
                    && (o[^1].Value.Contains("/ActualText", StringComparison.Ordinal) || o[^1].Value.Contains("/Alt", StringComparison.Ordinal) || o[^1].Value.Contains("/E ", StringComparison.Ordinal) || o[^1].Value.Contains("/E(", StringComparison.Ordinal)))
                {
                    _output.Append('\n').Append(o[^2].Value).Append(" BMC\n");
                    return true;
                }
                break;
            case "gs":
                if (o.Count > 0 && Lookup("/ExtGState", o[^1].Value)?.Elements.ContainsKey("/Font") == true)
                    throw new ContentNotReadable("a font set by an ExtGState");
                break;
            case "m":
            case "l":
                PathPoint(Number(o, 2), Number(o, 1));
                _otherSegments++;
                break;
            case "c":
                PathPoint(Number(o, 6), Number(o, 5));
                PathPoint(Number(o, 4), Number(o, 3));
                PathPoint(Number(o, 2), Number(o, 1));
                _otherSegments++;
                break;
            case "v":
            case "y":
                PathPoint(Number(o, 4), Number(o, 3));
                PathPoint(Number(o, 2), Number(o, 1));
                _otherSegments++;
                break;
            case "re":
                double x = Number(o, 4), y = Number(o, 3), w = Number(o, 2), h = Number(o, 1);
                PathPoint(x, y);
                PathPoint(x + w, y);
                PathPoint(x, y + h);
                PathPoint(x + w, y + h);
                _rectangles++;
                break;
            case "S" or "s" or "f" or "F" or "f*" or "B" or "B*" or "b" or "b*":
                PaintPath(op is "f" or "F" or "f*");
                break;
            case "n":
                ClearPath();
                break;
            case "sh":
                if (mode == EraseMode.Everything)
                    throw new ContentNotReadable("a shading, whose extent isn't known here");
                break;
            case "Do":
                Drawn(o.Count > 0 ? o[^1].Value : "");
                break;
        }
        return false;
    }

    private void NextLine(double x, double y)
    {
        _lineMatrix = new Matrix(1, 0, 0, 1, x, y).Then(_lineMatrix);
        _textMatrix = _lineMatrix;
    }

    private PdfDictionary? Lookup(string kind, string name) => resources?.Elements.GetDictionary(kind)?.Elements.GetDictionary(name);

    private FontWidths Font(string name)
    {
        if (!_fonts.TryGetValue(name, out var font))
            _fonts[name] = font = FontWidths.Of(Lookup("/Font", name) ?? throw new ContentNotReadable("a font that isn't in the page's resources"));
        return font;
    }

    // ---------- text ----------

    /// <summary>
    /// A text-showing operator: each glyph's place is worked out and the matrix moved on. When glyphs fall in a box it is written
    /// again as TJ, with what they took up as a number, so whatever follows stays where it was.
    /// </summary>
    private bool Show(List<Operand> parts, string before, int from)
    {
        var font = _state.Font ?? throw new ContentNotReadable("text with no font set");
        if (font.Vertical)
            throw new ContentNotReadable("vertical text");
        double size = _state.Size, scale = _state.Scale;
        if (Math.Abs(size) < 1e-9 || Math.Abs(scale) < 1e-9)
            throw new ContentNotReadable("text of no size");
        var written = new StringBuilder("[");
        var kept = new StringBuilder();
        double gap = 0;
        bool took = false;

        void FlushKept()
        {
            if (kept.Length == 0)
                return;
            written.Append('<');
            foreach (char c in kept.ToString())
                written.Append(((int)c).ToString("X2", CultureInfo.InvariantCulture));
            written.Append('>');
            kept.Clear();
        }
        void FlushGap()
        {
            if (Math.Abs(gap) < 1e-9)
                return;
            written.Append(Invariant(gap)).Append(' ');
            gap = 0;
        }

        foreach (var part in parts)
        {
            if (part.Kind == Kind.Number)
            {
                gap += part.Number;
                MoveText(-part.Number / 1000 * size * scale);
                continue;
            }
            if (part.Kind != Kind.Text)
                continue;
            var text = part.Value;
            for (int i = 0; i < text.Length; i += font.Bytes)
            {
                if (i + font.Bytes > text.Length)
                    throw new ContentNotReadable("a string cut through a character");
                int code = font.Bytes == 1 ? text[i] : (text[i] << 8) | text[i + 1];
                double width = font.Width(code) / 1000;
                double words = font.Bytes == 1 && code == 32 ? _state.WordSpace : 0;
                if (InBox(width))
                {
                    took = true;
                    gap -= (width * size + _state.CharSpace + words) / size * 1000;
                }
                else
                {
                    if (Math.Abs(gap) >= 1e-9)
                    {
                        FlushKept();
                        FlushGap();
                    }
                    kept.Append(text, i, font.Bytes);
                }
                MoveText((width * size + _state.CharSpace + words) * scale);
            }
        }
        if (!took)
            return false;
        FlushKept();
        FlushGap();
        written.Append("] TJ\n");
        _output.Append('\n').Append(before).Append(written);
        return true;
    }

    private void MoveText(double by) => _textMatrix = new Matrix(1, 0, 0, 1, by, 0).Then(_textMatrix);

    /// <summary>Whether the glyph about to be shown touches a box enough to go with it.</summary>
    private bool InBox(double width)
    {
        double size = _state.Size, rise = _state.Rise;
        var full = new Matrix(size * _state.Scale, 0, 0, size, 0, rise).Then(_textMatrix).Then(_state.Ctm);
        var area = Quad(full, 0, -PdfContentEraser.Descent, width, PdfContentEraser.Ascent);
        foreach (var box in boxes)
        {
            double high = Math.Min(area.Bottom, box.Bottom) - Math.Max(area.Top, box.Top);
            if (high <= 0.1 * area.Height)
                continue;
            // A combining mark has no width of its own; it goes when its place is in the box.
            if (area.Width < 0.2)
            {
                if (area.Left >= box.Left && area.Left <= box.Right)
                    return true;
                continue;
            }
            double wide = Math.Min(area.Right, box.Right) - Math.Max(area.Left, box.Left);
            if (wide > Math.Max(0.05 * area.Width, 0.1))
                return true;
        }
        return false;
    }

    /// <summary>The bounds as displayed of a rectangle in the coordinates of <paramref name="m"/>.</summary>
    private PdfArea Quad(Matrix m, double x1, double y1, double x2, double y2)
    {
        double left = double.MaxValue, top = double.MaxValue, right = double.MinValue, bottom = double.MinValue;
        foreach (var (x, y) in new[] { (x1, y1), (x2, y1), (x1, y2), (x2, y2) })
        {
            var user = m.Apply(x, y);
            var shown = space.ToDisplay(user.X, user.Y);
            left = Math.Min(left, shown.X);
            right = Math.Max(right, shown.X);
            top = Math.Min(top, shown.Y);
            bottom = Math.Max(bottom, shown.Y);
        }
        return new PdfArea(left, top, right - left, bottom - top);
    }

    // ---------- what isn't text ----------

    private void PathPoint(double x, double y)
    {
        var user = _state.Ctm.Apply(x, y);
        var shown = space.ToDisplay(user.X, user.Y);
        if (!_hasPath)
        {
            _hasPath = true;
            _pathLeft = _pathRight = shown.X;
            _pathTop = _pathBottom = shown.Y;
            return;
        }
        _pathLeft = Math.Min(_pathLeft, shown.X);
        _pathRight = Math.Max(_pathRight, shown.X);
        _pathTop = Math.Min(_pathTop, shown.Y);
        _pathBottom = Math.Max(_pathBottom, shown.Y);
    }

    private void ClearPath()
    {
        _hasPath = false;
        _rectangles = _otherSegments = 0;
    }

    /// <summary>
    /// A drawing under a box could itself be what is being hidden (a signature, words turned to outlines), and only a picture
    /// of the page can cut it. A plain filled rectangle wider than the box all round is a background, and tells nothing.
    /// </summary>
    private void PaintPath(bool fillOnly)
    {
        if (mode == EraseMode.Everything && _hasPath)
        {
            var path = new PdfArea(_pathLeft, _pathTop, _pathRight - _pathLeft, _pathBottom - _pathTop).Inflated(1);
            foreach (var box in boxes)
            {
                if (!path.Overlaps(box))
                    continue;
                bool background = fillOnly && _rectangles == 1 && _otherSegments == 0
                    && _pathLeft <= box.Left && _pathRight >= box.Right && _pathTop <= box.Top && _pathBottom >= box.Bottom;
                if (!background)
                    throw new ContentNotReadable("a drawing under a box");
            }
        }
        ClearPath();
    }

    private void Drawn(string name)
    {
        if (mode != EraseMode.Everything)
            return;
        var drawn = Lookup("/XObject", name);
        if (drawn is null)
            return;
        var area = drawn.Elements.GetName("/Subtype") == "/Form" ? FormArea(drawn) : Quad(_state.Ctm, 0, 0, 1, 1);
        if (boxes.Any(b => area.Overlaps(b)))
            throw new ContentNotReadable("a picture or a form under a box");
    }

    private PdfArea FormArea(PdfDictionary form)
    {
        var bbox = form.Elements.GetArray("/BBox");
        if (bbox is null || bbox.Elements.Count < 4)
            return new PdfArea(-1e6, -1e6, 2e6, 2e6);
        var inner = _state.Ctm;
        if (form.Elements.GetArray("/Matrix") is { Elements.Count: 6 } m)
            inner = new Matrix(m.Elements.GetReal(0), m.Elements.GetReal(1), m.Elements.GetReal(2), m.Elements.GetReal(3), m.Elements.GetReal(4), m.Elements.GetReal(5)).Then(inner);
        return Quad(inner, bbox.Elements.GetReal(0), bbox.Elements.GetReal(1), bbox.Elements.GetReal(2), bbox.Elements.GetReal(3));
    }

    /// <summary>An inline image is copied as it is, up to its EI; redacting, it may not lie under a box.</summary>
    private void InlineImage()
    {
        int start = _at - 2;
        while (true)
        {
            var token = Next();
            if (token.Kind == Kind.End)
                throw new ContentNotReadable("an inline image with no data");
            if (token.Kind == Kind.Word && token.Value == "ID")
                break;
        }
        int end = _at;
        while (true)
        {
            end = source.IndexOf("EI", end, StringComparison.Ordinal);
            if (end < 0)
                throw new ContentNotReadable("an inline image with no end");
            bool before = end > 0 && IsSpace(source[end - 1]);
            bool after = end + 2 >= source.Length || IsSpace(source[end + 2]);
            if (before && after)
                break;
            end += 2;
        }
        _at = end + 2;
        _output.Append(source, start, _at - start);
        if (mode == EraseMode.Everything && boxes.Any(b => Quad(_state.Ctm, 0, 0, 1, 1).Overlaps(b)))
            throw new ContentNotReadable("an inline picture under a box");
    }

    // ---------- reading the stream ----------

    private static bool IsSpace(char c) => c is ' ' or '\n' or '\r' or '\t' or '\f' or '\0';

    private static bool IsDelimiter(char c) => c is '(' or ')' or '<' or '>' or '[' or ']' or '{' or '}' or '/' or '%';

    private Token Next()
    {
        while (_at < source.Length)
        {
            char c = source[_at];
            if (IsSpace(c))
            {
                _at++;
                continue;
            }
            if (c == '%')
            {
                while (_at < source.Length && source[_at] is not ('\n' or '\r'))
                    _at++;
                continue;
            }
            break;
        }
        if (_at >= source.Length)
            return new Token(Kind.End);
        char first = source[_at];
        switch (first)
        {
            case '[':
                _at++;
                return new Token(Kind.Open);
            case ']':
                _at++;
                return new Token(Kind.Close);
            case '(':
                return new Token(Kind.Text, Value: Literal());
            case '<':
                if (_at + 1 < source.Length && source[_at + 1] == '<')
                    return new Token(Kind.Dictionary, Value: DictionaryText());
                return new Token(Kind.Text, Value: Hexadecimal());
            case '/':
            {
                int start = _at++;
                while (_at < source.Length && !IsSpace(source[_at]) && !IsDelimiter(source[_at]))
                    _at++;
                return new Token(Kind.Name, Value: source[start.._at]);
            }
        }
        if (first is '{' or '}' or ')' or '>')
            throw new ContentNotReadable("a stray delimiter");
        int from = _at;
        while (_at < source.Length && !IsSpace(source[_at]) && !IsDelimiter(source[_at]))
            _at++;
        var word = source[from.._at];
        if (first is >= '0' and <= '9' or '+' or '-' or '.' && double.TryParse(word, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            return new Token(Kind.Number, number);
        return new Token(Kind.Word, Value: word);
    }

    private string Literal()
    {
        var text = new StringBuilder();
        int depth = 1;
        _at++;
        while (_at < source.Length)
        {
            char c = source[_at++];
            if (c == '\\')
            {
                if (_at >= source.Length)
                    break;
                char e = source[_at++];
                switch (e)
                {
                    case 'n': text.Append('\n'); break;
                    case 'r': text.Append('\r'); break;
                    case 't': text.Append('\t'); break;
                    case 'b': text.Append('\b'); break;
                    case 'f': text.Append('\f'); break;
                    case '\r':
                        if (_at < source.Length && source[_at] == '\n')
                            _at++;
                        break;
                    case '\n':
                        break;
                    case >= '0' and <= '7':
                        int value = e - '0';
                        for (int i = 0; i < 2 && _at < source.Length && source[_at] is >= '0' and <= '7'; i++)
                            value = value * 8 + (source[_at++] - '0');
                        text.Append((char)(value & 0xFF));
                        break;
                    default:
                        text.Append(e);
                        break;
                }
                continue;
            }
            if (c == '(')
                depth++;
            else if (c == ')' && --depth == 0)
                return text.ToString();
            text.Append(c);
        }
        throw new ContentNotReadable("a string with no end");
    }

    private string Hexadecimal()
    {
        int end = source.IndexOf('>', _at);
        if (end < 0)
            throw new ContentNotReadable("a hexadecimal string with no end");
        var digits = new StringBuilder();
        for (int i = _at + 1; i < end; i++)
            if (!IsSpace(source[i]))
                digits.Append(source[i]);
        _at = end + 1;
        if (digits.Length % 2 == 1)
            digits.Append('0');
        var text = new StringBuilder(digits.Length / 2);
        for (int i = 0; i < digits.Length; i += 2)
            text.Append((char)int.Parse(digits.ToString(i, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
        return text.ToString();
    }

    /// <summary>A dictionary kept as its text, strings skipped over so a bracket in one doesn't count.</summary>
    private string DictionaryText()
    {
        int start = _at, depth = 0;
        while (_at < source.Length)
        {
            char c = source[_at];
            if (c == '(')
            {
                Literal();
                continue;
            }
            if (c == '<' && _at + 1 < source.Length && source[_at + 1] == '<')
            {
                depth++;
                _at += 2;
                continue;
            }
            if (c == '>' && _at + 1 < source.Length && source[_at + 1] == '>')
            {
                depth--;
                _at += 2;
                if (depth == 0)
                    return source[start.._at];
                continue;
            }
            _at++;
        }
        throw new ContentNotReadable("a dictionary with no end");
    }

    private static string Invariant(double value) => value.ToString("0.#####", CultureInfo.InvariantCulture);
}

/// <summary>A font's glyph widths as its dictionary gives them, which is what readers lay text out by.</summary>
internal sealed class FontWidths
{
    private readonly Dictionary<int, double> _widths = [];
    private readonly double _missing;

    private FontWidths(int bytes, double missing, bool vertical)
    {
        Bytes = bytes;
        _missing = missing;
        Vertical = vertical;
    }

    public int Bytes { get; }

    public bool Vertical { get; }

    public double Width(int code) => _widths.TryGetValue(code, out var width) ? width : _missing;

    public static FontWidths Of(PdfDictionary font)
    {
        var subtype = font.Elements.GetName("/Subtype");
        if (subtype == "/Type0")
            return Composite(font);
        if (subtype is "/Type3")
            throw new ContentNotReadable("a Type 3 font");
        if (font.Elements.GetArray("/Widths") is not { } widths)
            return Standard(font);
        var descriptor = font.Elements.GetDictionary("/FontDescriptor");
        var result = new FontWidths(1, descriptor?.Elements.GetReal("/MissingWidth") ?? 0, false);
        int first = font.Elements.GetInteger("/FirstChar");
        for (int i = 0; i < widths.Elements.Count; i++)
            result._widths[first + i] = widths.Elements.GetReal(i);
        return result;
    }

    /// <summary>One of the fourteen standard fonts, which a file may name without any widths: every reader knows them, by each letter's name.</summary>
    private static FontWidths Standard(PdfDictionary font)
    {
        var name = font.Elements.GetName("/BaseFont").TrimStart('/');
        if (!Standard14.IsFontInStandard14(name))
            throw new ContentNotReadable("a font without widths");
        var metrics = Standard14.GetAdobeFontMetrics(name);
        var family = metrics.FontName;
        Encodings.Encoding encoding = family == "Symbol" ? Encodings.SymbolEncoding.Instance : family == "ZapfDingbats" ? Encodings.ZapfDingbatsEncoding.Instance : Encodings.StandardEncoding.Instance;
        var written = font.Elements["/Encoding"] is { } item ? PdfWalk.Dictionary(item) : null;
        var based = written is not null ? written.Elements.GetName("/BaseEncoding") : font.Elements.GetName("/Encoding");
        if (based == "/WinAnsiEncoding")
            encoding = Encodings.WinAnsiEncoding.Instance;
        else if (based == "/MacRomanEncoding")
            encoding = Encodings.MacRomanEncoding.Instance;
        var names = encoding.CodeToNameMap.ToDictionary(p => p.Key, p => p.Value);
        if (written?.Elements.GetArray("/Differences") is { } differences)
        {
            int code = 0;
            foreach (var entry in differences.Elements)
            {
                if (entry is PdfInteger number)
                    code = number.Value;
                else if (entry is PdfName letter)
                    names[code++] = letter.Value.TrimStart('/');
            }
        }
        var result = new FontWidths(1, 0, false);
        foreach (var (code, letter) in names)
            if (metrics.CharacterMetrics.TryGetValue(letter, out var metric))
                result._widths[code] = metric.Width.X;
        return result;
    }

    private static FontWidths Composite(PdfDictionary font)
    {
        var encoding = font.Elements.GetName("/Encoding");
        if (encoding is not ("/Identity-H" or "/Identity-V"))
            throw new ContentNotReadable("a character map that isn't Identity");
        var descendant = font.Elements.GetArray("/DescendantFonts") is { Elements.Count: > 0 } list ? PdfWalk.Dictionary(list.Elements[0]) : null;
        if (descendant is null)
            throw new ContentNotReadable("a composite font with no descendant");
        double fallback = descendant.Elements.ContainsKey("/DW") ? descendant.Elements.GetReal("/DW") : 1000;
        var result = new FontWidths(2, fallback, encoding == "/Identity-V");
        if (descendant.Elements.GetArray("/W") is not { } w)
            return result;
        for (int i = 0; i + 1 < w.Elements.Count;)
        {
            int first = w.Elements.GetInteger(i);
            if (PdfWalk.Value(w.Elements[i + 1]) is PdfArray run)
            {
                for (int k = 0; k < run.Elements.Count; k++)
                    result._widths[first + k] = run.Elements.GetReal(k);
                i += 2;
            }
            else
            {
                if (i + 2 >= w.Elements.Count)
                    break;
                int last = w.Elements.GetInteger(i + 1);
                double width = w.Elements.GetReal(i + 2);
                for (int code = first; code <= last && code - first < 70000; code++)
                    result._widths[code] = width;
                i += 3;
            }
        }
        return result;
    }
}

/// <summary>Small steps through a file's objects, past indirect references.</summary>
internal static class PdfWalk
{
    /// <summary>A page's whole content, replaced with these operators.</summary>
    public static void SetContent(PdfPage page, byte[] operators)
    {
        var contents = page.Contents;
        contents.Elements.Clear();
        contents.AppendContent().CreateStream(operators);
    }

    public static PdfItem? Value(PdfItem? item) => item is PdfReference reference ? reference.Value : item;

    public static PdfDictionary? Dictionary(PdfItem? item) => Value(item) as PdfDictionary;

    public static PdfArray? Array(PdfItem? item) => Value(item) as PdfArray;

    /// <summary>A number however it is stored; null when it isn't one.</summary>
    public static double? Number(PdfItem? item) => Value(item) switch
    {
        PdfInteger i => i.Value,
        PdfReal r => r.Value,
        PdfLongInteger l => l.Value,
        PdfIntegerObject i => i.Value,
        PdfRealObject r => r.Value,
        _ => null,
    };

    /// <summary>A string or a name; a long value can be a stream, too. Null when it is neither.</summary>
    public static string? Text(PdfItem? item) => Value(item) switch
    {
        PdfString s => s.Value,
        PdfStringObject s => s.Value,
        PdfName n => n.Value,
        PdfNameObject n => n.Value,
        PdfDictionary { Stream: { } stream } => Encoding.UTF8.GetString(stream.UnfilteredValue),
        _ => null,
    };

    /// <summary>A rectangle given as an array of four numbers, or null.</summary>
    public static (double X1, double Y1, double X2, double Y2)? Rectangle(PdfItem? item)
    {
        if (Array(item) is not { Elements.Count: >= 4 } a)
            return null;
        if (Number(a.Elements[0]) is not { } x1 || Number(a.Elements[1]) is not { } y1 || Number(a.Elements[2]) is not { } x2 || Number(a.Elements[3]) is not { } y2)
            return null;
        return (Math.Min(x1, x2), Math.Min(y1, y2), Math.Max(x1, x2), Math.Max(y1, y2));
    }
}
