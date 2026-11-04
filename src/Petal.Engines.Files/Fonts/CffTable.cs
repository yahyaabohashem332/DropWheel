using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Petal.Engines.Files.Fonts;

/// <summary>A stretch of an outline drawn with cubic curves: a move, a line (<see cref="C1X"/> unused) or a curve.</summary>
internal readonly record struct PathStep(char Kind, double C1X, double C1Y, double C2X, double C2Y, double X, double Y);

/// <summary>CFF, the outlines of an OTF font: Type 2 charstrings read, and written without hints.</summary>
internal static class CffTable
{
    // ---- Reading.

    private static List<(int Offset, int Length)> ReadIndex(byte[] d, ref int at)
    {
        int count = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(at));
        at += 2;
        var items = new List<(int, int)>(count);
        if (count == 0)
            return items;
        int size = d[at++];
        int table = at;
        int Offset(int i)
        {
            int value = 0;
            for (int b = 0; b < size; b++)
                value = value << 8 | d[table + i * size + b];
            return value;
        }
        int data = table + (count + 1) * size - 1;
        for (int i = 0; i < count; i++)
            items.Add((data + Offset(i), Offset(i + 1) - Offset(i)));
        at = data + Offset(count);
        return items;
    }

    private static Dictionary<int, List<double>> ReadDict(byte[] d, int offset, int length)
    {
        var dict = new Dictionary<int, List<double>>();
        var values = new List<double>();
        int at = offset, end = offset + length;
        while (at < end)
        {
            int b = d[at++];
            if (b <= 21)
            {
                int key = b == 12 ? 1200 + d[at++] : b;
                dict[key] = values;
                values = [];
            }
            else if (b == 28)
            {
                values.Add(BinaryPrimitives.ReadInt16BigEndian(d.AsSpan(at)));
                at += 2;
            }
            else if (b == 29)
            {
                values.Add(BinaryPrimitives.ReadInt32BigEndian(d.AsSpan(at)));
                at += 4;
            }
            else if (b == 30)
            {
                var text = new StringBuilder();
                bool done = false;
                while (!done)
                {
                    int pair = d[at++];
                    foreach (int nibble in new[] { pair >> 4, pair & 15 })
                    {
                        if (nibble == 15)
                        {
                            done = true;
                            break;
                        }
                        text.Append(nibble switch { <= 9 => nibble.ToString(CultureInfo.InvariantCulture), 10 => ".", 11 => "E", 12 => "E-", 14 => "-", _ => "" });
                    }
                }
                values.Add(double.TryParse(text.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0);
            }
            else if (b is >= 32 and <= 246)
                values.Add(b - 139);
            else if (b is >= 247 and <= 250)
                values.Add((b - 247) * 256 + d[at++] + 108);
            else if (b is >= 251 and <= 254)
                values.Add(-(b - 251) * 256 - d[at++] - 108);
        }
        return dict;
    }

    /// <summary>Each glyph's outline as cubic paths, in font units.</summary>
    public static List<List<PathStep>> Read(byte[] cff, int unitsPerEm)
    {
        if (cff.Length < 4 || cff[0] != 1)
            throw new InvalidDataException("This font uses CFF2 variable outlines, which DropWheel can't convert.");
        int at = cff[2];
        ReadIndex(cff, ref at); // Names.
        var tops = ReadIndex(cff, ref at);
        ReadIndex(cff, ref at); // Strings.
        var globals = ReadIndex(cff, ref at);
        if (tops.Count == 0)
            throw new InvalidDataException("This font has no outlines.");
        var top = ReadDict(cff, tops[0].Offset, tops[0].Length);
        if (!top.TryGetValue(17, out var charStringsAt))
            throw new InvalidDataException("This font has no outlines.");
        int place = (int)charStringsAt[0];
        var glyphs = ReadIndex(cff, ref place);
        double scale = top.TryGetValue(1207, out var matrix) && matrix.Count >= 1 && matrix[0] > 0 ? matrix[0] * unitsPerEm : 1000.0 / 1000 * (unitsPerEm == 1000 ? 1 : unitsPerEm / 1000.0);

        List<(int Offset, int Length)> Locals(Dictionary<int, List<double>> dict)
        {
            if (!dict.TryGetValue(18, out var privateAt) || privateAt.Count < 2)
                return [];
            int size = (int)privateAt[0], offset = (int)privateAt[1];
            var priv = ReadDict(cff, offset, size);
            if (!priv.TryGetValue(19, out var subrs) || subrs.Count == 0)
                return [];
            int subrAt = offset + (int)subrs[0];
            return ReadIndex(cff, ref subrAt);
        }

        // A CID font keeps its subroutines font by font, and says which font each glyph belongs to.
        var perFont = new List<List<(int, int)>>();
        byte[]? select = null;
        if (top.TryGetValue(1236, out var fontsAt))
        {
            int fa = (int)fontsAt[0];
            foreach (var (offset, length) in ReadIndex(cff, ref fa))
                perFont.Add(Locals(ReadDict(cff, offset, length)));
            select = new byte[glyphs.Count];
            if (top.TryGetValue(1237, out var selectAt))
            {
                int s = (int)selectAt[0];
                int format = cff[s++];
                if (format == 0)
                    cff.AsSpan(s, glyphs.Count).CopyTo(select);
                else
                {
                    int ranges = BinaryPrimitives.ReadUInt16BigEndian(cff.AsSpan(s));
                    s += 2;
                    for (int r = 0; r < ranges; r++)
                    {
                        int first = BinaryPrimitives.ReadUInt16BigEndian(cff.AsSpan(s));
                        byte font = cff[s + 2];
                        int next = BinaryPrimitives.ReadUInt16BigEndian(cff.AsSpan(s + 3));
                        for (int g = first; g < next && g < select.Length; g++)
                            select[g] = font;
                        s += 3;
                    }
                }
            }
        }
        else
            perFont.Add(Locals(top));

        var result = new List<List<PathStep>>(glyphs.Count);
        for (int g = 0; g < glyphs.Count; g++)
        {
            var locals = perFont[select is null ? 0 : Math.Min(select[g], (byte)(perFont.Count - 1))];
            var path = new CharString(cff, globals, locals).Run(glyphs[g]);
            if (Math.Abs(scale - 1) > 1e-9)
                path = path.Select(s => s with { C1X = s.C1X * scale, C1Y = s.C1Y * scale, C2X = s.C2X * scale, C2Y = s.C2Y * scale, X = s.X * scale, Y = s.Y * scale }).ToList();
            result.Add(path);
        }
        return result;
    }

    /// <summary>A Type 2 charstring run through: hints are passed over, and what it draws is noted.</summary>
    private sealed class CharString(byte[] d, List<(int Offset, int Length)> globals, List<(int Offset, int Length)> locals)
    {
        private readonly List<double> _stack = [];
        private readonly List<PathStep> _path = [];
        private double _x, _y;
        private int _hints;
        private bool _widthSeen;
        private bool _ended;

        public List<PathStep> Run((int Offset, int Length) code)
        {
            Execute(code.Offset, code.Length, 0);
            return _path;
        }

        private static int Bias(int count) => count < 1240 ? 107 : count < 33900 ? 1131 : 32768;

        private void Move(double dx, double dy)
        {
            _x += dx;
            _y += dy;
            _path.Add(new PathStep('M', 0, 0, 0, 0, _x, _y));
        }

        private void Line(double dx, double dy)
        {
            _x += dx;
            _y += dy;
            _path.Add(new PathStep('L', 0, 0, 0, 0, _x, _y));
        }

        private void Curve(double a, double b, double c, double e, double f, double g)
        {
            double x1 = _x + a, y1 = _y + b, x2 = x1 + c, y2 = y1 + e;
            _x = x2 + f;
            _y = y2 + g;
            _path.Add(new PathStep('C', x1, y1, x2, y2, _x, _y));
        }

        /// <summary>The optional width at the start of a charstring, which the font's metrics say anyway.</summary>
        private void DropWidth(int expected)
        {
            if (!_widthSeen)
            {
                if (_stack.Count > expected)
                    _stack.RemoveAt(0);
                _widthSeen = true;
            }
        }

        private void Stems()
        {
            if (!_widthSeen)
            {
                if (_stack.Count % 2 != 0)
                    _stack.RemoveAt(0);
                _widthSeen = true;
            }
            _hints += _stack.Count / 2;
            _stack.Clear();
        }

        private void Execute(int offset, int length, int depth)
        {
            if (depth > 10)
                return;
            int at = offset, end = offset + length;
            while (at < end && !_ended)
            {
                int b = d[at++];
                if (b >= 32 || b == 28)
                {
                    if (b == 28)
                    {
                        _stack.Add(BinaryPrimitives.ReadInt16BigEndian(d.AsSpan(at)));
                        at += 2;
                    }
                    else if (b <= 246)
                        _stack.Add(b - 139);
                    else if (b <= 250)
                        _stack.Add((b - 247) * 256 + d[at++] + 108);
                    else if (b <= 254)
                        _stack.Add(-(b - 251) * 256 - d[at++] - 108);
                    else
                    {
                        _stack.Add(BinaryPrimitives.ReadInt32BigEndian(d.AsSpan(at)) / 65536.0);
                        at += 4;
                    }
                    continue;
                }
                var s = _stack;
                switch (b)
                {
                    case 1 or 3 or 18 or 23:
                        Stems();
                        break;
                    case 19 or 20:
                        Stems();
                        at += (_hints + 7) / 8;
                        break;
                    case 21:
                        DropWidth(2);
                        Move(s[^2], s[^1]);
                        s.Clear();
                        break;
                    case 22:
                        DropWidth(1);
                        Move(s[^1], 0);
                        s.Clear();
                        break;
                    case 4:
                        DropWidth(1);
                        Move(0, s[^1]);
                        s.Clear();
                        break;
                    case 5:
                        for (int i = 0; i + 1 < s.Count; i += 2)
                            Line(s[i], s[i + 1]);
                        s.Clear();
                        break;
                    case 6 or 7:
                    {
                        bool across = b == 6;
                        foreach (double v in s)
                        {
                            if (across)
                                Line(v, 0);
                            else
                                Line(0, v);
                            across = !across;
                        }
                        s.Clear();
                        break;
                    }
                    case 8:
                        for (int i = 0; i + 5 < s.Count; i += 6)
                            Curve(s[i], s[i + 1], s[i + 2], s[i + 3], s[i + 4], s[i + 5]);
                        s.Clear();
                        break;
                    case 24:
                    {
                        int i = 0;
                        for (; i + 7 < s.Count; i += 6)
                            Curve(s[i], s[i + 1], s[i + 2], s[i + 3], s[i + 4], s[i + 5]);
                        Line(s[i], s[i + 1]);
                        s.Clear();
                        break;
                    }
                    case 25:
                    {
                        int i = 0;
                        for (; i + 7 < s.Count; i += 2)
                            Line(s[i], s[i + 1]);
                        Curve(s[i], s[i + 1], s[i + 2], s[i + 3], s[i + 4], s[i + 5]);
                        s.Clear();
                        break;
                    }
                    case 26:
                    {
                        int i = 0;
                        double dx = s.Count % 2 != 0 ? s[i++] : 0;
                        for (; i + 3 < s.Count; i += 4, dx = 0)
                            Curve(dx, s[i], s[i + 1], s[i + 2], 0, s[i + 3]);
                        s.Clear();
                        break;
                    }
                    case 27:
                    {
                        int i = 0;
                        double dy = s.Count % 2 != 0 ? s[i++] : 0;
                        for (; i + 3 < s.Count; i += 4, dy = 0)
                            Curve(s[i], dy, s[i + 1], s[i + 2], s[i + 3], 0);
                        s.Clear();
                        break;
                    }
                    case 30 or 31:
                    {
                        bool across = b == 31;
                        int i = 0;
                        while (i + 3 < s.Count)
                        {
                            bool last = s.Count - i is 5;
                            double extra = last ? s[i + 4] : 0;
                            if (across)
                                Curve(s[i], 0, s[i + 1], s[i + 2], extra, s[i + 3]);
                            else
                                Curve(0, s[i], s[i + 1], s[i + 2], s[i + 3], extra);
                            across = !across;
                            i += 4;
                        }
                        s.Clear();
                        break;
                    }
                    case 10 or 29:
                    {
                        var table = b == 10 ? locals : globals;
                        int n = (int)s[^1] + Bias(table.Count);
                        s.RemoveAt(s.Count - 1);
                        if (n >= 0 && n < table.Count)
                            Execute(table[n].Offset, table[n].Length, depth + 1);
                        break;
                    }
                    case 11:
                        return;
                    case 14:
                        _ended = true;
                        return;
                    case 12:
                        Flex(d[at++]);
                        s.Clear();
                        break;
                    default:
                        s.Clear();
                        break;
                }
            }
        }

        private void Flex(int op)
        {
            var s = _stack;
            switch (op)
            {
                case 35 when s.Count >= 12:
                    Curve(s[0], s[1], s[2], s[3], s[4], s[5]);
                    Curve(s[6], s[7], s[8], s[9], s[10], s[11]);
                    break;
                case 34 when s.Count >= 7:
                    Curve(s[0], 0, s[1], s[2], s[3], 0);
                    Curve(s[4], 0, s[5], -s[2], s[6], 0);
                    break;
                case 36 when s.Count >= 9:
                    Curve(s[0], s[1], s[2], s[3], s[4], 0);
                    Curve(s[5], 0, s[6], s[7], s[8], -(s[1] + s[3] + s[7]));
                    break;
                case 37 when s.Count >= 11:
                {
                    double dx = s[0] + s[2] + s[4] + s[6] + s[8], dy = s[1] + s[3] + s[5] + s[7] + s[9];
                    Curve(s[0], s[1], s[2], s[3], s[4], s[5]);
                    if (Math.Abs(dx) > Math.Abs(dy))
                        Curve(s[6], s[7], s[8], s[9], s[10], -dy);
                    else
                        Curve(s[6], s[7], s[8], s[9], -dx, s[10]);
                    break;
                }
            }
        }
    }

    // ---- Writing.

    private static void Number(Stream to, double value)
    {
        int n = (int)Math.Round(value);
        if (n is >= -107 and <= 107)
            to.WriteByte((byte)(n + 139));
        else if (n is >= 108 and <= 1131)
        {
            n -= 108;
            to.WriteByte((byte)(n / 256 + 247));
            to.WriteByte((byte)(n % 256));
        }
        else if (n is >= -1131 and <= -108)
        {
            n = -n - 108;
            to.WriteByte((byte)(n / 256 + 251));
            to.WriteByte((byte)(n % 256));
        }
        else
        {
            to.WriteByte(28);
            to.WriteByte((byte)(n >> 8));
            to.WriteByte((byte)n);
        }
    }

    /// <summary>A charstring: the width, then what it draws, with whole-number steps that can't add up to a drift.</summary>
    internal static byte[] CharStringOf(IReadOnlyList<PathStep> path, int width)
    {
        var o = new MemoryStream();
        bool widthDone = false;
        void Width()
        {
            if (!widthDone && width != 0)
                Number(o, width);
            widthDone = true;
        }
        int x = 0, y = 0;
        (double, double) Step(double tx, double ty)
        {
            int nx = (int)Math.Round(tx), ny = (int)Math.Round(ty);
            (double, double) d = (nx - x, ny - y);
            (x, y) = (nx, ny);
            return d;
        }
        const int Most = 40;
        int run = 0;
        char kind = ' ';
        void Close()
        {
            if (run > 0)
                o.WriteByte(kind == 'L' ? (byte)5 : (byte)8);
            run = 0;
        }
        foreach (var step in path)
        {
            if (step.Kind == 'M')
            {
                Close();
                Width();
                var (dx, dy) = Step(step.X, step.Y);
                Number(o, dx);
                Number(o, dy);
                o.WriteByte(21);
            }
            else if (step.Kind == 'L')
            {
                if (kind != 'L' || run + 2 > Most)
                    Close();
                kind = 'L';
                var (dx, dy) = Step(step.X, step.Y);
                Number(o, dx);
                Number(o, dy);
                run += 2;
            }
            else
            {
                if (kind != 'C' || run + 6 > Most)
                    Close();
                kind = 'C';
                foreach (var (tx, ty) in new[] { (step.C1X, step.C1Y), (step.C2X, step.C2Y), (step.X, step.Y) })
                {
                    var (dx, dy) = Step(tx, ty);
                    Number(o, dx);
                    Number(o, dy);
                }
                run += 6;
            }
        }
        Close();
        Width();
        o.WriteByte(14);
        return o.ToArray();
    }

    private static byte[] Index(IReadOnlyList<byte[]> items)
    {
        var o = new MemoryStream();
        o.WriteByte((byte)(items.Count >> 8));
        o.WriteByte((byte)items.Count);
        if (items.Count == 0)
            return o.ToArray();
        o.WriteByte(4);
        int offset = 1;
        void Offset(int v)
        {
            o.WriteByte((byte)(v >> 24));
            o.WriteByte((byte)(v >> 16));
            o.WriteByte((byte)(v >> 8));
            o.WriteByte((byte)v);
        }
        Offset(offset);
        foreach (var item in items)
        {
            offset += item.Length;
            Offset(offset);
        }
        foreach (var item in items)
            o.Write(item);
        return o.ToArray();
    }

    private static void Long(Stream to, int value)
    {
        to.WriteByte(29);
        to.WriteByte((byte)(value >> 24));
        to.WriteByte((byte)(value >> 16));
        to.WriteByte((byte)(value >> 8));
        to.WriteByte((byte)value);
    }

    private static void Real(Stream to, double value)
    {
        to.WriteByte(30);
        var text = value.ToString("0.########", CultureInfo.InvariantCulture);
        var nibbles = new List<int>();
        foreach (char c in text)
            nibbles.Add(c switch { '.' => 10, '-' => 14, _ => c - '0' });
        nibbles.Add(15);
        if (nibbles.Count % 2 != 0)
            nibbles.Add(15);
        for (int i = 0; i < nibbles.Count; i += 2)
            to.WriteByte((byte)(nibbles[i] << 4 | nibbles[i + 1]));
    }

    /// <summary>A whole CFF table: the top dictionary holds offsets of fixed size, so it is measured once and written with them.</summary>
    public static byte[] Write(string fontName, int unitsPerEm, (int X0, int Y0, int X1, int Y1) box, IReadOnlyList<string> glyphNames, IReadOnlyList<byte[]> charStrings)
    {
        var strings = glyphNames.Skip(1).Select(n => Encoding.ASCII.GetBytes(n)).ToList();
        var nameIndex = Index([Encoding.ASCII.GetBytes(fontName)]);
        var stringIndex = Index(strings);
        var globalIndex = Index([]);
        var charStringIndex = Index(charStrings);

        // Charset: format 2 with one range would need contiguous strings, which they are: names 391, 392 and so on.
        var charset = new MemoryStream();
        charset.WriteByte(2);
        if (glyphNames.Count > 1)
        {
            charset.WriteByte(391 >> 8);
            charset.WriteByte(391 & 255);
            int left = glyphNames.Count - 2;
            charset.WriteByte((byte)(left >> 8));
            charset.WriteByte((byte)left);
        }

        var privateDict = new MemoryStream();
        Number(privateDict, 0);
        privateDict.WriteByte(20);
        Number(privateDict, 0);
        privateDict.WriteByte(21);

        byte[] Top(int charsetAt, int charStringsAt, int privateAt)
        {
            var t = new MemoryStream();
            foreach (var v in new[] { box.X0, box.Y0, box.X1, box.Y1 })
                Number(t, v);
            t.WriteByte(5);
            if (unitsPerEm != 1000)
            {
                Real(t, 1.0 / unitsPerEm);
                Number(t, 0);
                Number(t, 0);
                Real(t, 1.0 / unitsPerEm);
                Number(t, 0);
                Number(t, 0);
                t.WriteByte(12);
                t.WriteByte(7);
            }
            Long(t, charsetAt);
            t.WriteByte(15);
            Long(t, charStringsAt);
            t.WriteByte(17);
            Long(t, (int)privateDict.Length);
            Long(t, privateAt);
            t.WriteByte(18);
            return t.ToArray();
        }

        int topLength = Index([Top(0, 0, 0)]).Length;
        int charsetAt = 4 + nameIndex.Length + topLength + stringIndex.Length + globalIndex.Length;
        int charStringsAt = charsetAt + (int)charset.Length;
        int privateAt = charStringsAt + charStringIndex.Length;

        var o = new MemoryStream();
        o.Write([1, 0, 4, 4]);
        o.Write(nameIndex);
        o.Write(Index([Top(charsetAt, charStringsAt, privateAt)]));
        o.Write(stringIndex);
        o.Write(globalIndex);
        charset.WriteTo(o);
        o.Write(charStringIndex);
        privateDict.WriteTo(o);
        return o.ToArray();
    }
}
