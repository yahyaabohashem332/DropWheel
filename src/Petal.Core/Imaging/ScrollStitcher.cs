namespace Petal.Core.Imaging;

/// <summary>
/// Joins the frames of a scrolling capture into one tall picture. Each frame is the same area of the screen
/// after a scroll; the stitcher finds how far the page moved by matching rows, ignores parts that stay put
/// (a sticky header, a footer), and adds only the new rows at the bottom.
/// </summary>
public sealed class ScrollStitcher
{
    private readonly int _width, _height;
    private readonly List<byte[]> _rows = [];
    private ulong[] _last = [];

    /// <param name="width">Frame width in pixels (BGRA, 4 bytes a pixel).</param>
    public ScrollStitcher(int width, int height)
    {
        _width = width;
        _height = height;
    }

    public int Width => _width;

    /// <summary>Rows so far.</summary>
    public int Height => _rows.Count;

    /// <summary>
    /// Adds a frame. Returns how many new rows it brought: 0 when the page didn't move (the end of it), or -1
    /// when the frame can't be matched to the one before (the page changed under the capture).
    /// </summary>
    public int Add(byte[] bgra)
    {
        if (bgra.Length != _width * _height * 4)
            throw new ArgumentException("The frame isn't the size of the others.", nameof(bgra));
        var hashes = RowHashes(bgra);
        if (_rows.Count == 0)
        {
            for (int y = 0; y < _height; y++)
                _rows.Add(Row(bgra, y));
            _last = hashes;
            return _height;
        }
        int shift = FindShift(_last, hashes);
        if (shift < 0)
            return -1;
        if (shift == 0)
            return 0;
        // Rows that are the same in both frames at the bottom (a fixed footer) are not part of the page that moved.
        int footer = 0;
        while (footer < _height / 4 && _last[_height - 1 - footer] == hashes[_height - 1 - footer])
            footer++;
        // Take the footer off what was already added, then add the new rows and the footer again.
        if (footer > 0 && _rows.Count >= footer)
            _rows.RemoveRange(_rows.Count - footer, footer);
        for (int y = _height - footer - shift; y < _height; y++)
            if (y >= 0)
                _rows.Add(Row(bgra, y));
        _last = hashes;
        return shift;
    }

    /// <summary>The picture so far, BGRA, <see cref="Width"/> × <see cref="Height"/>.</summary>
    public byte[] Result()
    {
        var all = new byte[_rows.Count * _width * 4];
        for (int y = 0; y < _rows.Count; y++)
            Buffer.BlockCopy(_rows[y], 0, all, y * _width * 4, _width * 4);
        return all;
    }

    /// <summary>
    /// How many rows the new frame moved up: the shift that makes the most of the old frame's lower rows
    /// reappear higher in the new one. Rows the same in both places (sticky headers, plain background)
    /// tell nothing and don't count.
    /// </summary>
    internal static int FindShift(ulong[] before, ulong[] after)
    {
        int h = before.Length;
        int bestShift = -1, bestScore = 0;
        int margin = h / 12;
        for (int s = 1; s < h - h / 8; s++)
        {
            int score = 0, compared = 0;
            for (int y = margin; y + s < h - margin; y += 2)
            {
                ulong a = before[y + s], b = after[y];
                // A row that equals its neighbour (flat colour) matches anywhere: skip it.
                if (y + s + 1 < h && before[y + s + 1] == a)
                    continue;
                compared++;
                if (a == b)
                    score++;
            }
            if (compared >= 6 && score * 10 >= compared * 9 && score > bestScore)
                (bestScore, bestShift) = (score, s);
        }
        if (bestShift > 0)
            return bestShift;
        // Nothing moved: the same frame again.
        return before.SequenceEqual(after) ? 0 : (IsSameIgnoringFlat(before, after) ? 0 : -1);
    }

    private static bool IsSameIgnoringFlat(ulong[] a, ulong[] b)
    {
        int same = 0;
        for (int i = 0; i < a.Length; i++)
            if (a[i] == b[i])
                same++;
        return same * 10 >= a.Length * 9;
    }

    private ulong[] RowHashes(byte[] bgra)
    {
        var hashes = new ulong[_height];
        int stride = _width * 4;
        // A thin band down each side can be a scrollbar that moves on its own; leave it out.
        int skip = Math.Min(24, _width / 10) * 4;
        for (int y = 0; y < _height; y++)
        {
            ulong h = 14695981039346656037UL;
            int start = y * stride;
            for (int x = skip; x < stride - skip; x++)
            {
                h ^= bgra[start + x];
                h *= 1099511628211UL;
            }
            hashes[y] = h;
        }
        return hashes;
    }

    private byte[] Row(byte[] bgra, int y)
    {
        var row = new byte[_width * 4];
        Buffer.BlockCopy(bgra, y * _width * 4, row, 0, row.Length);
        return row;
    }
}
