namespace Petal.Engines.Documents.Model;

/// <summary>A cell placed on the table grid. Continuations stand in for grid slots covered by a row span from above.</summary>
internal readonly record struct GridCell(TableCell Cell, int Column, int ColSpan, bool IsContinuation);

/// <summary>Places cells on a rectangular grid, resolving row and column spans, so writers can emit regular rows.</summary>
internal static class TableLayout
{
    public static (List<List<GridCell>> Rows, int Columns) Layout(Table table)
    {
        var occupied = new Dictionary<(int Row, int Col), (TableCell Cell, int ColSpan)>();
        var placed = new List<List<GridCell>>();
        var columns = 0;

        for (var r = 0; r < table.Rows.Count; r++)
        {
            var row = new List<GridCell>();
            var col = 0;
            foreach (var cell in table.Rows[r].Cells)
            {
                while (occupied.ContainsKey((r, col)))
                    col++;
                var span = Math.Max(1, cell.ColSpan);
                row.Add(new GridCell(cell, col, span, false));
                for (var dr = 1; dr < Math.Max(1, cell.RowSpan); dr++)
                    occupied[(r + dr, col)] = (cell, span);
                col += span;
            }
            columns = Math.Max(columns, col);
            placed.Add(row);
        }

        // Insert continuation slots for row spans coming from above.
        foreach (var ((r, c), (cell, span)) in occupied)
        {
            if (r >= placed.Count)
                continue;
            placed[r].Add(new GridCell(cell, c, span, true));
            columns = Math.Max(columns, c + span);
        }
        foreach (var row in placed)
            row.Sort((a, b) => a.Column.CompareTo(b.Column));
        return (placed, columns);
    }
}
