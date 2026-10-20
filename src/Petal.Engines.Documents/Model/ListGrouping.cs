namespace Petal.Engines.Documents.Model;

/// <summary>A paragraph that carries list numbering, before nesting is worked out.</summary>
internal sealed record ListEntry(int Level, bool Ordered, Paragraph Paragraph, int Start = 1);

/// <summary>Turns a flat run of numbered paragraphs (Word, RTF) into nested lists.</summary>
internal static class ListGrouping
{
    public static List<Block> Build(IReadOnlyList<ListEntry> entries)
    {
        var result = new List<Block>();
        var stack = new List<(ListBlock List, int Level)>();
        foreach (var entry in entries)
        {
            while (stack.Count > 0 && stack[^1].Level > entry.Level)
                stack.RemoveAt(stack.Count - 1);
            if (stack.Count > 0 && stack[^1].Level == entry.Level && stack[^1].List.Ordered != entry.Ordered)
                stack.RemoveAt(stack.Count - 1);

            if (stack.Count == 0 || stack[^1].Level < entry.Level)
            {
                var list = new ListBlock(entry.Ordered) { Start = entry.Start };
                if (stack.Count == 0)
                    result.Add(list);
                else
                {
                    var parent = stack[^1].List;
                    if (parent.Items.Count == 0)
                        parent.Items.Add(new ListItem());
                    parent.Items[^1].Blocks.Add(list);
                }
                stack.Add((list, entry.Level));
            }
            var item = new ListItem();
            item.Blocks.Add(entry.Paragraph);
            stack[^1].List.Items.Add(item);
        }
        return result;
    }
}
