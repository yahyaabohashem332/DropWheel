using System.Windows;

namespace Petal.App.Screen;

/// <summary>
/// What a click on the screen means when it falls on no text: the picture, icon or chart under it, told apart from the
/// plain background around it. The screen is looked at in small cells; cells with detail in them that touch each other
/// (or nearly) make one thing, and the smallest thing whose box holds the click is the one meant.
/// </summary>
internal static class PictureRegions
{
    /// <summary>Cells of this many pixels a side.</summary>
    private const int Cell = 4;

    /// <summary>Luminance steps between neighbouring pixels that count as detail (out of 255).</summary>
    private const int Detail = 16;

    /// <summary>
    /// The box of the thing under (<paramref name="x"/>, <paramref name="y"/>) in a BGRA still, or null when the click fell
    /// on plain background or the thing would be most of the screen.
    /// </summary>
    public static Int32Rect? Around(byte[] bgra, int width, int height, int x, int y)
    {
        if (x < 0 || y < 0 || x >= width || y >= height || bgra.Length < width * height * 4)
            return null;
        int cw = (width + Cell - 1) / Cell, ch = (height + Cell - 1) / Cell;
        var busy = Busy(bgra, width, height, cw, ch);
        // Gaps of up to two cells (8 pixels) are bridged: a photo's plain sky is still inside its edges.
        var joined = Dilate(busy, cw, ch, 2);
        var label = new int[cw * ch];
        var boxes = new List<(int Left, int Top, int Right, int Bottom)>();
        var queue = new Queue<int>();
        for (int start = 0; start < label.Length; start++)
        {
            if (!joined[start] || label[start] != 0)
                continue;
            int id = boxes.Count + 1;
            int left = int.MaxValue, top = int.MaxValue, right = -1, bottom = -1;
            label[start] = id;
            queue.Enqueue(start);
            while (queue.Count > 0)
            {
                int c = queue.Dequeue();
                int cx = c % cw, cy = c / cw;
                // The box is of the detail itself, not of the bridges around it.
                if (busy[c])
                {
                    left = Math.Min(left, cx);
                    top = Math.Min(top, cy);
                    right = Math.Max(right, cx);
                    bottom = Math.Max(bottom, cy);
                }
                if (cx > 0) Visit(c - 1);
                if (cx < cw - 1) Visit(c + 1);
                if (cy > 0) Visit(c - cw);
                if (cy < ch - 1) Visit(c + cw);
            }
            boxes.Add((left, top, right, bottom));

            void Visit(int n)
            {
                if (joined[n] && label[n] == 0)
                {
                    label[n] = id;
                    queue.Enqueue(n);
                }
            }
        }
        int px = x / Cell, py = y / Cell;
        (int Left, int Top, int Right, int Bottom)? best = null;
        double bestArea = double.MaxValue;
        foreach (var box in boxes)
        {
            if (box.Right < 0 || px < box.Left - 1 || px > box.Right + 1 || py < box.Top - 1 || py > box.Bottom + 1)
                continue;
            double w = (box.Right - box.Left + 1) * Cell, h = (box.Bottom - box.Top + 1) * Cell;
            if (w < 16 || h < 16 || w * h > width * (double)height * 0.8)
                continue;
            if (w * h < bestArea)
            {
                best = box;
                bestArea = w * h;
            }
        }
        if (best is not { } b)
            return null;
        int l = Math.Max(0, b.Left * Cell - 2), t = Math.Max(0, b.Top * Cell - 2);
        int r = Math.Min(width, (b.Right + 1) * Cell + 2), btm = Math.Min(height, (b.Bottom + 1) * Cell + 2);
        return new Int32Rect(l, t, r - l, btm - t);
    }

    /// <summary>Cells with detail: a step in brightness between neighbouring pixels somewhere inside them.</summary>
    internal static bool[] Busy(byte[] bgra, int width, int height, int cw, int ch)
    {
        var luma = new byte[width * height];
        for (int i = 0, p = 0; i < luma.Length; i++, p += 4)
            luma[i] = (byte)((bgra[p] * 29 + bgra[p + 1] * 150 + bgra[p + 2] * 77) >> 8);
        var busy = new bool[cw * ch];
        for (int y = 0; y < height; y++)
        {
            int row = y * width;
            for (int x = 0; x < width; x++)
            {
                int v = luma[row + x];
                bool step = (x + 1 < width && Math.Abs(v - luma[row + x + 1]) > Detail) || (y + 1 < height && Math.Abs(v - luma[row + width + x]) > Detail);
                if (step)
                    busy[y / Cell * cw + x / Cell] = true;
            }
        }
        return busy;
    }

    private static bool[] Dilate(bool[] cells, int cw, int ch, int radius)
    {
        // Across, then down: a square of the radius around every busy cell.
        var across = new bool[cells.Length];
        for (int y = 0; y < ch; y++)
        {
            int last = -radius - 1;
            for (int x = 0; x < cw; x++)
                if (cells[y * cw + x])
                    last = x;
                else if (x - last <= radius)
                    across[y * cw + x] = true;
            last = cw + radius + 1;
            for (int x = cw - 1; x >= 0; x--)
            {
                if (cells[y * cw + x])
                {
                    last = x;
                    across[y * cw + x] = true;
                }
                else if (last - x <= radius)
                    across[y * cw + x] = true;
            }
        }
        var result = new bool[cells.Length];
        for (int x = 0; x < cw; x++)
        {
            int last = -radius - 1;
            for (int y = 0; y < ch; y++)
                if (across[y * cw + x])
                    last = y;
                else if (y - last <= radius)
                    result[y * cw + x] = true;
            last = ch + radius + 1;
            for (int y = ch - 1; y >= 0; y--)
            {
                if (across[y * cw + x])
                {
                    last = y;
                    result[y * cw + x] = true;
                }
                else if (last - y <= radius)
                    result[y * cw + x] = true;
            }
        }
        return result;
    }
}
